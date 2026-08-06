using CustomCoverArt.Models;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CustomCoverArt.Services;

/// <summary>
/// Compositor: renders a <see cref="CoverDocument"/> onto an ImageSharp canvas.
/// Shared by the single-image and animated paths; extended by later phases
/// (image layers, effects). This is the sole owner of background/gradient/font/
/// text-drawing logic — moved here from <see cref="CoverArtService"/> so both
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

        // Clamp the normalized size before it becomes an allocation: Width/Height are
        // client-supplied, and an absurd fraction would drive a giant Resize buffer.
        var normW = Math.Clamp(layer.Width, 0f, 4f);
        var normH = Math.Clamp(layer.Height, 0f, 4f);
        var w = Math.Max(1, (int)Math.Round(normW * canvas.Width));
        var h = Math.Max(1, (int)Math.Round(normH * canvas.Height));

        try
        {
            // Decompression-bomb guard: inspect the header before a full decode.
            var info = Image.Identify(layer.ImagePath);
            const long maxSourcePixels = 8192L * 8192L;
            if ((long)info.Width * info.Height > maxSourcePixels)
            {
                return;
            }

            using var logo = Image.Load<Rgba32>(layer.ImagePath);
            logo.Mutate(x => x.Resize(w, h));

            // Rotation grows the bounding box, so the top-left must be derived from the
            // ROTATED size — otherwise the layer visibly drifts off its anchor.
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
            // Footprint is the letterboxed size, not the canvas — see ApplyBackgroundTransform.
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
        // IMPORTANT: do NOT use Fill() with a semi-transparent SolidBrush here —
        // on backgrounds decoded without an alpha channel (JPEG posters load as
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
            backgroundImage.Mutate(x => x.Crop(rect));
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
    public static Rectangle TransformedSourceRect(int fittedW, int fittedH, int footprintW, int footprintH, BackgroundTransform t)
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
        return new Rectangle(x, y, w, h);
    }

    internal static void CreateGradientBackground(Image<Rgba32> image, BackgroundLayer bg)
    {
        // Source is authoritative post-migration. The IsEnabled fallback keeps a document
        // that never went through Normalize (an older client POSTing directly) rendering
        // exactly as it did before — but an explicit "solid" always wins, so choosing
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
    /// same settings type but is deliberately linear-only — see the spec's inert-fields note.
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
    /// Builds the colour stops from the gradient settings — the explicit Stops
    /// list if it has 2+ entries, otherwise the Start/End colours as a fallback.
    /// </summary>
    public static ColorStop[] BuildColorStops(GradientSettings gradient)
    {
        if (gradient.Stops is { Count: >= 2 })
        {
            return gradient.Stops
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
    /// Renders a text layer: main text plus optional shadow/outline effects.
    /// Coordinates are normalized on the layer (0..1 of canvas width/height);
    /// converted here to pixels against the document's canvas.
    /// </summary>
    internal static void RenderTextLayer(Image<Rgba32> canvas, CoverLayer layer, CoverDocument doc)
    {
        // IMPORTANT: font size uses doc.Canvas.Height (the document's declared
        // *reference* resolution), NOT canvas.Height (the actual pixel buffer
        // passed in). doc.Canvas is the design's reference resolution; the
        // compositor may draw onto a differently-sized buffer — e.g.
        // CoverArtService.GenerateAnimatedAsync downscales the working buffer
        // for oversized animated GIFs via ScaleDocumentForCanvas, which points a
        // cloned document's Canvas.Width/Height at the capped working size so
        // layer.Size (a fraction of canvas height) stays proportionally correct
        // without any extra scaling math. Keying font size off doc.Canvas.Height
        // (not canvas.Height) is what makes that possible: it lets a caller
        // resize the actual buffer independently of the document's own declared
        // dimensions and still get proportionally correct text. See
        // DocumentRenderTests.ComposeDocumentFrame_BufferHeightDiffersFromCanvas_FontSizeFollowsDocCanvasHeight
        // for a direct regression guard against swapping in canvas.Height here.
        //
        // Clamp to [8, 1024]px: this is the render-time parity equivalent of the
        // legacy CoverArtSettings.TextSize [8,1024] clamp (removed from
        // GenerateFromDocumentAsync since the document model has no absolute
        // TextSize field to clamp pre-render). An unbounded fontPixelSize would
        // drive unbounded glyph rasterization — a render-thread DoS — so this
        // guards every document-native caller (including future Task 6+
        // endpoints), not just the legacy migrated path.
        var fontPixelSize = Math.Clamp(layer.Size * doc.Canvas.Height, 8f, 1024f);
        var font = CreateFont(layer, fontPixelSize);

        // Per-layer opacity folded into every colour this layer draws with. The client
        // canvas sets ctx.globalAlpha once per layer and then strokes/fills separately,
        // so alpha-per-element is the matching behaviour (outline and fill each fade
        // independently rather than being flattened first).
        var opacity = Math.Clamp(layer.Opacity, 0f, 1f);
        var textColor = SafeColor(layer.Color, Color.White).WithAlpha(opacity);

        // Convert normalized layer coordinates to canvas pixels.
        var textPosition = new PointF(layer.X * canvas.Width, layer.Y * canvas.Height);

        // Create text options
        var textOptions = new RichTextOptions(font)
        {
            Origin = textPosition,
            HorizontalAlignment = GetHorizontalAlignment(layer.Align),
            VerticalAlignment = VerticalAlignment.Center
        };

        // Rotation pivots on the layer's own anchor, matching the client's
        // translate/rotate around (X*W, Y*H). Set on the drawing context (not the
        // image) so it applies to this layer's passes only — which is also why every
        // pass below shares ONE Mutate block.
        var rotate = layer.Rotation != 0f
            ? System.Numerics.Matrix3x2.CreateRotation(
                (float)(layer.Rotation * Math.PI / 180.0),
                new System.Numerics.Vector2(textPosition.X, textPosition.Y))
            : System.Numerics.Matrix3x2.Identity;

        // Text shadow. Clamp offsets to [-50, 50]px: the render-time parity equivalent of
        // the legacy CoverArtSettings.TextShadowOffsetX/Y clamp.
        var shadowColor = SafeColor(layer.Shadow.Color, Color.Black).WithAlpha(opacity);
        var shadowBlur = Math.Clamp(layer.Shadow.Blur, 0, 50);
        var shadowOptions = new RichTextOptions(font)
        {
            Origin = new PointF(
                textPosition.X + Math.Clamp(layer.Shadow.OffsetX, -50, 50),
                textPosition.Y + Math.Clamp(layer.Shadow.OffsetY, -50, 50)),
            HorizontalAlignment = textOptions.HorizontalAlignment,
            VerticalAlignment = textOptions.VerticalAlignment
        };

        // A blurred shadow needs its own surface: blurring has to happen before the glyphs
        // are drawn on top, and it must not blur whatever is already on the canvas. Done
        // OUTSIDE the main Mutate block below because that block sets the rotation
        // transform — the scratch surface already has the rotation baked in, and drawing
        // it through the transform as well would rotate the shadow twice.
        if (layer.Shadow.Enabled && shadowBlur > 0)
        {
            using var shadowSurface = new Image<Rgba32>(canvas.Width, canvas.Height);
            shadowSurface.Mutate(sc =>
            {
                if (!rotate.IsIdentity) { sc.SetDrawingTransform(rotate); }
                sc.DrawText(shadowOptions, layer.Content, shadowColor);
            });
            shadowSurface.Mutate(sc => sc.GaussianBlur(shadowBlur));
            canvas.Mutate(x => x.DrawImage(shadowSurface, Point.Empty, 1f));
        }

        canvas.Mutate(ctx =>
        {
            if (!rotate.IsIdentity)
            {
                ctx.SetDrawingTransform(rotate);
            }

            // An unblurred shadow is a plain offset copy, drawn first so it sits beneath
            // the outline and the fill.
            if (layer.Shadow.Enabled && shadowBlur <= 0)
            {
                ctx.DrawText(shadowOptions, layer.Content, shadowColor);
            }

            if (layer.Outline.Enabled)
            {
                var outlineColor = SafeColor(layer.Outline.Color, Color.Black).WithAlpha(opacity);
                var outlineWidth = Math.Clamp(layer.Outline.Width, 0, 10);

                // Draw outline by drawing text multiple times with slight offsets
                for (int x = -outlineWidth; x <= outlineWidth; x++)
                {
                    for (int y = -outlineWidth; y <= outlineWidth; y++)
                    {
                        if (x == 0 && y == 0) continue;

                        var outlinePosition = new PointF(textPosition.X + x, textPosition.Y + y);
                        var outlineOptions = new RichTextOptions(font)
                        {
                            Origin = outlinePosition,
                            HorizontalAlignment = textOptions.HorizontalAlignment,
                            VerticalAlignment = textOptions.VerticalAlignment
                        };

                        ctx.DrawText(outlineOptions, layer.Content, outlineColor);
                    }
                }
            }

            // Draw main text
            ctx.DrawText(textOptions, layer.Content, textColor);
        });
    }

    /// <summary>
    /// Renders a text layer with fallback mechanisms if the primary render throws.
    /// </summary>
    internal static void RenderTextLayerWithFallback(Image<Rgba32> canvas, CoverLayer layer, CoverDocument doc)
    {
        try
        {
            RenderTextLayer(canvas, layer, doc);
        }
        catch
        {
            // Fallback: simple text overlay without advanced effects.
            try
            {
                var fallbackFontPx = Math.Max(12, layer.Size * doc.Canvas.Height * 0.5f);
                var font = BundledFamily(FontWeight.Normal).CreateFont(fallbackFontPx);
                var textColor = SafeColor(layer.Color, Color.White);
                var position = new PointF(canvas.Width / 2f, canvas.Height / 2f);

                canvas.Mutate(x => x.DrawText(layer.Content, font, textColor, position));
            }
            catch
            {
                // Ultimate fallback: Just fill with the dim/background color
                var backgroundColor = SafeColor(doc.Background.DimColor, Color.Black);
                canvas.Mutate(x => x.Fill(backgroundColor));
            }
        }
    }

    private static HorizontalAlignment GetHorizontalAlignment(TextAlign textAlign)
    {
        return textAlign switch
        {
            TextAlign.Left => HorizontalAlignment.Left,
            TextAlign.Right => HorizontalAlignment.Right,
            TextAlign.Center => HorizontalAlignment.Center,
            _ => HorizontalAlignment.Center
        };
    }

    // Bundled fonts (Noto Sans, SIL OFL-1.1 — the same family Jellyfin's web UI
    // uses), embedded so text ALWAYS renders, even on Jellyfin Docker images
    // that ship no system fonts. Every UI weight has its own face so the font
    // weight control actually changes the rendered thickness.
    private static readonly FontCollection BundledFonts = new();
    private static readonly object FontLock = new();
    private static readonly Dictionary<FontWeight, FontFamily> BundledFamilies = new();

    private static FontFamily BundledFamily(FontWeight weight)
    {
        lock (FontLock)
        {
            if (BundledFamilies.TryGetValue(weight, out var existing))
            {
                return existing;
            }

            var faceName = weight switch
            {
                FontWeight.Light => "NotoSans-Light",
                FontWeight.Medium => "NotoSans-Medium",
                FontWeight.SemiBold => "NotoSans-SemiBold",
                FontWeight.Bold => "NotoSans-Bold",
                FontWeight.ExtraBold => "NotoSans-ExtraBold",
                _ => "NotoSans-Regular" // Normal and any unmapped value
            };

            var resource = $"CustomCoverArt.Resources.fonts.{faceName}.ttf";
            using var stream = typeof(DocumentRenderer).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Bundled font not found: {resource}");

            var family = BundledFonts.Add(stream);
            BundledFamilies[weight] = family;
            return family;
        }
    }

    /// <summary>
    /// Creates a font: a client-uploaded custom font if one was provided,
    /// otherwise the bundled Noto Sans face for the requested weight.
    /// </summary>
    internal static Font CreateFont(CoverLayer layer, float fontPixelSize)
    {
        // Custom uploaded font takes priority (its path is already sandboxed).
        if (!string.IsNullOrEmpty(layer.FontPath) && File.Exists(layer.FontPath))
        {
            try
            {
                var fontCollection = new FontCollection();
                var fontFamily = fontCollection.Add(layer.FontPath);
                return fontFamily.CreateFont(fontPixelSize);
            }
            catch
            {
                // Fall back to the bundled font if the custom one fails to load.
            }
        }

        return BundledFamily(layer.Weight).CreateFont(fontPixelSize);
    }
}
