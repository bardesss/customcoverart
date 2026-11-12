using CustomCoverArt.Models;
using CustomCoverArt.Services.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CustomCoverArt.Services;

/// <summary>
/// Compositor: renders a <see cref="CoverDocument"/> onto an ImageSharp canvas.
/// Shared by the single-image and animated paths; extended by later phases
/// (image layers, effects). This is the sole owner of background/gradient/font/
/// text-drawing logic. It moved here from <see cref="CoverArtService"/> so both
/// the legacy flat-settings path (via <see cref="DocumentMigration"/>) and
/// future document-native endpoints render through the same code.
/// </summary>
public static class DocumentRenderer
{
    /// <summary>
    /// Composites one frame (background + visible layers) onto the supplied canvas.
    /// </summary>
    public static void ComposeDocumentFrame(Image<Rgba32> canvas, Image? background, CoverDocument doc)
    {
        if (background is not null)
        {
            ApplyBackgroundLayer(canvas, background, doc.Background);
        }
        else
        {
            CreateGradientBackground(canvas, doc.Background);
        }

        // Soft-light washes the background BEFORE the layers, so text and logos sit on
        // top of the tint rather than under it. The client mirrors this order exactly.
        EffectsComposer.ApplySoftLight(canvas, doc.Effects.SoftLight);

        // The overlay goes last before the layers: its colour must be authoritative (a
        // soft-light wash after it would desaturate it) and it must be the last thing
        // under the text, which is the whole legibility contract. It lives HERE rather
        // than in ApplyBackgroundLayer so it applies to all four background sources;
        // ApplyBackgroundLayer runs only on the image path.
        ApplyGradientOverlay(canvas, doc.Background.Overlay);

        foreach (var layer in doc.Layers)
        {
            if (!layer.Visible) { continue; }

            if (layer.Type == "text")
            {
                RenderTextLayerWithFallback(canvas, layer, doc);
            }
            else if (layer.Type == "image")
            {
                RenderImageLayer(canvas, layer);
            }
        }

        // Vignette → grain → border, the border outermost and last.
        EffectsComposer.Apply(canvas, doc.Effects);
    }

    /// <summary>
    /// Draws an image (logo/icon) layer: the PNG at <see cref="CoverLayer.ImagePath"/>,
    /// resized to the layer's normalized <c>Width</c>/<c>Height</c> fraction of the canvas,
    /// optionally rotated, composited centred on <c>(X, Y)</c> at <c>Opacity</c>.
    ///
    /// The path is expected to have been sandbox-filtered upstream
    /// (<see cref="CoverArtService.GenerateFromDocumentAsync"/>); this method never
    /// resolves relative paths of its own. It is also deliberately total: a missing,
    /// corrupt or oversized file skips the layer instead of failing the whole render,
    /// mirroring <see cref="RenderTextLayerWithFallback"/>.
    /// </summary>
    public static void RenderImageLayer(Image<Rgba32> canvas, CoverLayer layer)
    {
        if (string.IsNullOrEmpty(layer.ImagePath) || !File.Exists(layer.ImagePath))
        {
            return;
        }

        var size = ImageLayerPixelSize(layer, canvas.Width, canvas.Height);
        var w = size.Width;
        var h = size.Height;

        try
        {
            // Shared decompression-bomb guard; a logo is always drawn as a still, so only
            // its first frame is ever decoded.
            using var logo = ImageSharpBridge.TryLoadStill(layer.ImagePath);
            if (logo is null)
            {
                return;
            }

            logo.Mutate(x => x.Resize(w, h));

            // Rotation grows the bounding box, so the top-left must be derived from the
            // ROTATED size. Otherwise the layer visibly drifts off its anchor.
            if (layer.Rotation != 0f)
            {
                logo.Mutate(x => x.Rotate(layer.Rotation));
            }

            var cx = (int)Math.Round(layer.X * canvas.Width);
            var cy = (int)Math.Round(layer.Y * canvas.Height);
            var px = cx - logo.Width / 2;
            var py = cy - logo.Height / 2;

            // DrawImage needs the two rectangles to overlap; a layer dragged fully off
            // the canvas is a legitimate state, so treat it as a no-op rather than
            // letting the processor throw.
            if (px + logo.Width <= 0 || py + logo.Height <= 0 || px >= canvas.Width || py >= canvas.Height)
            {
                return;
            }

            var opacity = Math.Clamp(layer.Opacity, 0f, 1f);
            canvas.Mutate(x => x.DrawImage(logo, new Point(px, py), opacity));
        }
        catch
        {
            // Unreadable/undecodable logo: skip it, never break the whole render.
        }
    }

    /// <summary>Largest an image layer may be, as a multiple of the canvas on each axis.</summary>
    internal const float MaxImageLayerScale = 1.5f;

    /// <summary>
    /// Pixel size an image layer is resized to. Width/Height are client-supplied fractions,
    /// so they are clamped before they become an allocation: a layer bigger than 1.5× the
    /// canvas is mostly off-canvas anyway, and Rotate grows the buffer further still.
    /// </summary>
    internal static PixelSize ImageLayerPixelSize(CoverLayer layer, int canvasWidth, int canvasHeight)
    {
        var normW = Math.Clamp(layer.Width, 0f, MaxImageLayerScale);
        var normH = Math.Clamp(layer.Height, 0f, MaxImageLayerScale);
        return new PixelSize(
            Math.Max(1, (int)Math.Round(normW * canvasWidth)),
            Math.Max(1, (int)Math.Round(normH * canvasHeight)));
    }

    // Parses a #rrggbb colour, falling back to a default instead of throwing so
    // a malformed colour in the request body cannot abort the whole render.
    internal static Color SafeColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        try
        {
            return Color.ParseHex(hex);
        }
        catch
        {
            return fallback;
        }
    }

    internal static void ApplyBackgroundLayer(Image<Rgba32> image, Image backgroundImage, BackgroundLayer bg)
    {
        var fit = (bg.Fit ?? "cover").Trim().ToLowerInvariant();
        var baseColor = SafeColor(bg.DimColor, Color.Black);

        if (fit == "stretch")
        {
            // Distort to fill the whole canvas exactly.
            backgroundImage.Mutate(x => x.Resize(image.Width, image.Height));
            ApplyBackgroundTransform(backgroundImage, image.Width, image.Height, bg.Transform);
            if (bg.Blur > 0)
            {
                backgroundImage.Mutate(x => x.GaussianBlur(bg.Blur));
            }
            image.Mutate(x => x.DrawImage(backgroundImage, Point.Empty, 1f));
        }
        else if (fit == "contain")
        {
            // Fit the whole image inside the canvas (like background-size: contain),
            // letterboxing the remainder with the base colour.
            image.Mutate(x => x.Fill(baseColor));

            var scale = Math.Min((float)image.Width / backgroundImage.Width,
                                 (float)image.Height / backgroundImage.Height);
            var w = Math.Max(1, (int)Math.Round(backgroundImage.Width * scale));
            var h = Math.Max(1, (int)Math.Round(backgroundImage.Height * scale));
            backgroundImage.Mutate(x => x.Resize(w, h));
            // Footprint is the letterboxed size, not the canvas (see ApplyBackgroundTransform).
            ApplyBackgroundTransform(backgroundImage, w, h, bg.Transform);
            if (bg.Blur > 0)
            {
                backgroundImage.Mutate(x => x.GaussianBlur(bg.Blur));
            }

            var px = (image.Width - w) / 2;
            var py = (image.Height - h) / 2;
            image.Mutate(x => x.DrawImage(backgroundImage, new Point(px, py), 1f));
        }
        else
        {
            // "cover" (default): scale to fill the canvas keeping the source aspect
            // ratio, then centre-crop (like CSS background-size: cover).
            var scale = Math.Max((float)image.Width / backgroundImage.Width,
                                 (float)image.Height / backgroundImage.Height);
            var newWidth = Math.Max(image.Width, (int)Math.Ceiling(backgroundImage.Width * scale));
            var newHeight = Math.Max(image.Height, (int)Math.Ceiling(backgroundImage.Height * scale));

            backgroundImage.Mutate(x => x.Resize(newWidth, newHeight));

            // The centre-crop is NOT done here: ApplyBackgroundTransform performs it,
            // so the part "cover" would discard stays pannable. With an identity
            // transform it crops dead-centre, exactly as the plain centre-crop did.
            ApplyBackgroundTransform(backgroundImage, image.Width, image.Height, bg.Transform);

            if (bg.Blur > 0)
            {
                backgroundImage.Mutate(x => x.GaussianBlur(bg.Blur));
            }
            image.Mutate(x => x.DrawImage(backgroundImage, Point.Empty, 1f));
        }

        // Apply dimming by compositing a solid overlay at fractional opacity.
        // IMPORTANT: do NOT use Fill() with a semi-transparent SolidBrush here.
        // On backgrounds decoded without an alpha channel (JPEG posters load as
        // Rgb24) Fill ignores the brush alpha and paints fully opaque, which
        // blacked out the entire background for any non-zero dim. DrawImage with
        // an opacity blends correctly on every pixel format. Text is drawn later,
        // so only the background is dimmed.
        if (bg.Dim > 0)
        {
            using var dimOverlay = new Image<Rgba32>(image.Width, image.Height, baseColor);
            image.Mutate(x => x.DrawImage(dimOverlay, Point.Empty, bg.Dim));
        }
    }

    /// <summary>
    /// Applies the user's pan/zoom to a background that has been SCALED for the
    /// current <c>Fit</c> mode but not yet cropped, so client canvas and server
    /// render frame it identically. This also performs the fit's own crop: under
    /// "cover" the scaled image is larger than the footprint, and that leftover is
    /// exactly what the user expects to pan across. Deriving the pannable slack from
    /// zoom alone made repositioning a silent no-op at the default Scale=1.
    ///
    /// <paramref name="footprintW"/>/<paramref name="footprintH"/> are the destination
    /// the background occupies, NOT necessarily the canvas: for cover/stretch it is
    /// the whole canvas, but for contain it's the smaller letterboxed footprint.
    /// Resizing a contain-fitted image out to full canvas would silently turn
    /// "contain" into "stretch", which the transform feature must not do.
    ///
    /// Skips the crop and the resample whenever either would be a no-op, so an
    /// identity transform still reproduces the plain centered render EXACTLY.
    /// </summary>
    private static void ApplyBackgroundTransform(Image backgroundImage, int footprintW, int footprintH, BackgroundTransform transform)
    {
        var rect = TransformedSourceRect(backgroundImage.Width, backgroundImage.Height, footprintW, footprintH, transform);

        if (rect.X != 0 || rect.Y != 0 || rect.Width != backgroundImage.Width || rect.Height != backgroundImage.Height)
        {
            backgroundImage.Mutate(x => x.Crop(new Rectangle(rect.X, rect.Y, rect.Width, rect.Height)));
        }

        // Only a zoomed window needs resampling back up: an unzoomed crop already IS
        // footprint-sized, so cropping alone reproduces the old centre-crop bit for bit.
        if (backgroundImage.Width != footprintW || backgroundImage.Height != footprintH)
        {
            backgroundImage.Mutate(x => x.Resize(footprintW, footprintH));
        }
    }

    /// <summary>
    /// Sub-rectangle of the fit-scaled background to draw, given pan/zoom: the
    /// destination footprint shrunk by the zoom, panned across everything the
    /// fit-scaled image has to spare. Clamped in-bounds.
    /// </summary>
    public static PixelRect TransformedSourceRect(int fittedW, int fittedH, int footprintW, int footprintH, BackgroundTransform t)
    {
        var scale = Math.Max(1f, t.Scale);
        // Clamp to the image: a "contain" footprint can exceed it on an axis, and a
        // window larger than the source would upscale rather than pan.
        var w = Math.Clamp((int)Math.Round(footprintW / scale), 1, fittedW);
        var h = Math.Clamp((int)Math.Round(footprintH / scale), 1, fittedH);
        var slackX = fittedW - w;
        var slackY = fittedH - h;
        // Offset -1..1 maps across the available slack; 0 = centered. The centre uses
        // integer halving so identity matches a plain centre-crop to the pixel.
        var x = (slackX / 2) + (int)Math.Round(Math.Clamp(t.OffsetX, -1f, 1f) * slackX / 2f);
        var y = (slackY / 2) + (int)Math.Round(Math.Clamp(t.OffsetY, -1f, 1f) * slackY / 2f);
        x = Math.Clamp(x, 0, slackX);
        y = Math.Clamp(y, 0, slackY);
        return new PixelRect(x, y, w, h);
    }

    internal static void CreateGradientBackground(Image<Rgba32> image, BackgroundLayer bg)
    {
        // Source is authoritative post-migration. The IsEnabled fallback keeps a document
        // that never went through Normalize (an older client POSTing directly) rendering
        // as it did before, but an explicit "solid" always wins, so choosing
        // Solid in the UI cannot be silently overridden by a stale flag left behind.
        var useGradient = bg.Source == BackgroundSources.Gradient
            || (bg.Source != BackgroundSources.Solid && bg.Gradient?.IsEnabled == true);

        if (useGradient && bg.Gradient is not null)
        {
            ApplyGradientBackground(image, bg.Gradient);
        }
        else
        {
            var backgroundColor = SafeColor(bg.DimColor, Color.Black);
            image.Mutate(x => x.Fill(backgroundColor));
        }
    }

    /// <summary>
    /// Applies a multi-stop gradient background (linear at a given angle, or radial).
    /// </summary>
    internal static void ApplyGradientBackground(Image<Rgba32> image, GradientSettings gradient)
    {
        var brush = CreateGradientBrush(gradient, image.Width, image.Height);
        image.Mutate(x => x.Fill(brush));
    }

    /// <summary>
    /// Builds the gradient brush: radial when the settings ask for it, otherwise a linear
    /// ramp along a line through the centre at <c>Angle</c> degrees, long enough to span
    /// the canvas corner to corner.
    ///
    /// <paramref name="forceLinear"/> exists for the background OVERLAY, which reuses this
    /// same settings type but is deliberately linear-only (see the spec's inert-fields note).
    /// Shared with <see cref="ApplyGradientOverlay"/> so the two cannot drift apart.
    /// </summary>
    public static Brush CreateGradientBrush(GradientSettings gradient, int width, int height, bool forceLinear = false)
    {
        var stops = BuildColorStops(gradient);

        if (!forceLinear && gradient.Type == GradientType.Radial)
        {
            var centerX = gradient.CenterX * width;
            var centerY = gradient.CenterY * height;
            var radius = Math.Max(1f, gradient.Radius * Math.Min(width, height));

            return new RadialGradientBrush(new PointF(centerX, centerY), radius, GradientRepetitionMode.None, stops);
        }

        var rad = gradient.Angle * Math.PI / 180.0;
        var dx = (float)Math.Cos(rad);
        var dy = (float)Math.Sin(rad);
        var cx = width / 2f;
        var cy = height / 2f;
        var half = (Math.Abs(dx) * width + Math.Abs(dy) * height) / 2f;

        var p0 = new PointF(cx - dx * half, cy - dy * half);
        var p1 = new PointF(cx + dx * half, cy + dy * half);

        return new LinearGradientBrush(p0, p1, GradientRepetitionMode.None, stops);
    }

    /// <summary>
    /// Composites the background overlay: a linear multi-stop gradient whose stops carry
    /// their own alpha, drawn over the finished background and under the layers.
    ///
    /// Deliberately does NOT use BuildColorStops' Start/End fallback. That fallback yields
    /// an OPAQUE ramp between StartColor/EndColor (Jellyfin brand purple/blue by default,
    /// falling back further to black/white only if those hex values fail to parse), fully
    /// covering the canvas edge to edge. That is a reasonable default for a background
    /// gradient, but for an overlay it would obliterate the poster regardless of which
    /// colours it resolves to. Fewer than two stops = off.
    /// </summary>
    internal static void ApplyGradientOverlay(Image<Rgba32> canvas, GradientSettings? overlay)
    {
        if (overlay is null || !overlay.IsEnabled) { return; }
        if (overlay.Stops is not { Count: >= 2 }) { return; }

        var brush = CreateGradientBrush(overlay, canvas.Width, canvas.Height, forceLinear: true);

        // Rendered into its OWN transparent Rgba32 buffer, then composited. Filling the
        // canvas directly with a semi-transparent brush is the trap that once blacked out
        // dimmed backgrounds (see ApplyBackgroundLayer): Fill ignores brush alpha on
        // alpha-less pixel formats. This buffer is explicitly Rgba32, and SrcOver onto a
        // zero-alpha destination resolves exactly to the source, so the default graphics
        // options are already correct and no custom GraphicsOptions are needed.
        using var scrim = new Image<Rgba32>(canvas.Width, canvas.Height);
        scrim.Mutate(x => x.Fill(brush));
        canvas.Mutate(x => x.DrawImage(scrim, Point.Empty, 1f));
    }

    /// <summary>
    /// Builds the colour stops from the gradient settings: the explicit Stops
    /// list if it has 2+ entries, otherwise the Start/End colours as a fallback.
    /// </summary>
    public static ColorStop[] BuildColorStops(GradientSettings gradient)
    {
        // Null entries are skipped even on paths that never ran Normalize.
        var explicitStops = gradient.Stops?.Where(s => s is not null).ToList();
        if (explicitStops is { Count: >= 2 })
        {
            return explicitStops
                .OrderBy(s => s.Position)
                .Select(s => new ColorStop(
                    Math.Clamp(s.Position, 0f, 1f),
                    SafeColor(s.Color, Color.Gray).WithAlpha(Math.Clamp(s.Alpha, 0f, 1f))))
                .ToArray();
        }

        return new[]
        {
            new ColorStop(0f, SafeColor(gradient.StartColor, Color.Black)),
            new ColorStop(1f, SafeColor(gradient.EndColor, Color.White))
        };
    }

    /// <summary>
    /// Renders a text layer through <see cref="TextRenderer.RenderTextLayerWithFallback"/>, which
    /// never throws: one bad layer must never cost the rest of the cover. The ImageSharp canvas is
    /// bridged until the compositor itself moves to SkiaSharp.
    /// </summary>
    internal static void RenderTextLayerWithFallback(Image<Rgba32> canvas, CoverLayer layer, CoverDocument doc)
        => ImageSharpBridge.DrawOnto(canvas, c => TextRenderer.RenderTextLayerWithFallback(c, layer, doc));
}
