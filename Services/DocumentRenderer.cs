using CustomCoverArt.Models;
using CustomCoverArt.Services.Rendering;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace CustomCoverArt.Services;

/// <summary>Colour stops of a gradient, sorted by position, as Skia's gradient shaders take them.</summary>
internal sealed record GradientStops(SKColor[] Colors, float[] Positions);

/// <summary>
/// Compositor: renders a <see cref="CoverDocument"/> onto a SkiaSharp canvas
/// (RGBA8888, premultiplied, no colour space; see <see cref="RenderSurface"/>).
/// Shared by the single-image and animated paths. This is the sole owner of
/// background/gradient/overlay/image-layer drawing; text goes through
/// <see cref="TextRenderer"/> and the effects through <see cref="EffectsComposer"/>.
/// Both the legacy flat-settings path (via <see cref="DocumentMigration"/>) and the
/// document-native endpoints render through this same code.
/// </summary>
public static class DocumentRenderer
{
    private static readonly AsyncLocal<ILogger?> TestLog = new();

    /// <summary>Where skipped-layer warnings go; set by <c>CoverArtService</c>.</summary>
    public static ILogger Log { get; set; } = NullLogger.Instance;

    private static ILogger Logger => TestLog.Value ?? Log;

    /// <summary>
    /// Composites one frame (background + visible layers + effects) onto the supplied canvas.
    /// <paramref name="background"/> is one decoded frame; it is only read, never mutated, so the
    /// caller may reuse it.
    /// </summary>
    public static void ComposeDocumentFrame(SKBitmap canvas, SKBitmap? background, CoverDocument doc)
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
                TextRenderer.RenderTextLayerWithFallback(canvas, layer, doc);
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
    /// Draws an image (logo/icon) layer: the image at <see cref="CoverLayer.ImagePath"/>,
    /// sized to the layer's normalized <c>Width</c>/<c>Height</c> fraction of the canvas,
    /// rotated about its centre and drawn centred on <c>(X, Y)</c> at <c>Opacity</c>, exactly as
    /// the client canvas does (translate, rotate, draw centred).
    ///
    /// The path is expected to have been sandbox-filtered upstream
    /// (<see cref="CoverArtService.GenerateFromDocumentAsync"/>); this method never
    /// resolves relative paths of its own. It is also deliberately total: a missing,
    /// corrupt or oversized file skips the layer with a warning instead of failing the
    /// whole render, mirroring <see cref="TextRenderer.RenderTextLayerWithFallback"/>.
    /// A layer wholly or partly off the canvas is simply clipped.
    /// </summary>
    public static void RenderImageLayer(SKBitmap canvas, CoverLayer layer)
    {
        if (string.IsNullOrEmpty(layer.ImagePath))
        {
            return;
        }

        var opacity = Math.Clamp(layer.Opacity, 0f, 1f);
        if (opacity <= 0f)
        {
            return;
        }

        try
        {
            // Shared decompression-bomb guard; a logo is always drawn as a still, so only
            // its first frame is ever decoded.
            using var decoded = SafeImageLoader.TryDecode(layer.ImagePath, maxFrames: 1);
            if (decoded is null)
            {
                Logger.LogWarning("Image layer skipped (missing, undecodable or too large): {Path}", layer.ImagePath);
                return;
            }

            var size = ImageLayerPixelSize(layer, canvas.Width, canvas.Height);
            var source = decoded.RootFrame;
            using var pixmap = source.PeekPixels();
            using var image = SKImage.FromPixels(pixmap);

            using var target = new SKCanvas(canvas);
            target.Translate((int)Math.Round(layer.X * canvas.Width), (int)Math.Round(layer.Y * canvas.Height));
            if (layer.Rotation != 0f)
            {
                target.RotateDegrees(layer.Rotation);
            }

            // Integer halves, so an unrotated layer lands on whole pixels (crisp edges) at the same
            // place the ImageSharp renderer put it: top-left = centre - size / 2.
            var dst = SKRect.Create(-(size.Width / 2), -(size.Height / 2), size.Width, size.Height);
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = SKColors.White.WithAlpha((byte)Math.Round(opacity * 255f)),
            };
            target.DrawImage(image, dst, Resampling.For(source.Width, source.Height, size.Width, size.Height), paint);
        }
        catch (Exception ex)
        {
            // Never break the whole render for one logo.
            Logger.LogWarning(ex, "Image layer skipped (could not be drawn): {Path}", layer.ImagePath);
        }
    }

    /// <summary>Routes this renderer's warnings to <paramref name="logger"/> on the calling flow only, until disposed.</summary>
    internal static IDisposable CaptureLogForTests(ILogger logger)
    {
        var previous = TestLog.Value;
        TestLog.Value = logger;
        return new RestoreLog(previous);
    }

    /// <summary>Largest an image layer may be, as a multiple of the canvas on each axis.</summary>
    internal const float MaxImageLayerScale = 1.5f;

    /// <summary>
    /// Pixel size an image layer is drawn at. Width/Height are client-supplied fractions,
    /// so they are clamped: a layer bigger than 1.5× the canvas is mostly off-canvas anyway.
    /// </summary>
    internal static PixelSize ImageLayerPixelSize(CoverLayer layer, int canvasWidth, int canvasHeight)
    {
        var normW = Math.Clamp(layer.Width, 0f, MaxImageLayerScale);
        var normH = Math.Clamp(layer.Height, 0f, MaxImageLayerScale);
        return new PixelSize(
            Math.Max(1, (int)Math.Round(normW * canvasWidth)),
            Math.Max(1, (int)Math.Round(normH * canvasHeight)));
    }

    /// <summary>
    /// Parses a colour (<c>#rgb</c>, <c>#rgba</c>, <c>#rrggbb</c>, <c>#rrggbbaa</c>), falling back to a
    /// default instead of throwing so a malformed colour in the request body cannot abort the whole render.
    /// </summary>
    internal static SKColor SafeColor(string? hex, SKColor fallback) => SkiaColors.Parse(hex, fallback);

    /// <summary>
    /// Draws a background image under the document's fit, pan/zoom, blur and dim. The fit decides the
    /// footprint (the whole canvas for cover and stretch, the letterboxed rectangle for contain) and the
    /// virtual size the image is scaled to; the transform picks the window of that scaled image to show
    /// (<see cref="TransformedSourceRect"/>). That window is mapped back to source pixels and drawn
    /// straight into the footprint, so the image is resampled once. <paramref name="source"/> is only read.
    /// </summary>
    internal static void ApplyBackgroundLayer(SKBitmap canvas, SKBitmap source, BackgroundLayer bg)
    {
        var fit = (bg.Fit ?? "cover").Trim().ToLowerInvariant();
        var baseColor = SafeColor(bg.DimColor, SKColors.Black);
        int canvasW = canvas.Width, canvasH = canvas.Height;

        PixelRect footprint;
        int fittedW, fittedH;
        if (fit == "stretch")
        {
            // Distort to fill the whole canvas exactly.
            footprint = new PixelRect(0, 0, canvasW, canvasH);
            (fittedW, fittedH) = (canvasW, canvasH);
        }
        else if (fit == "contain")
        {
            // The whole image inside the canvas (background-size: contain), letterboxed with the
            // base colour. Pan/zoom works inside that smaller footprint and never turns contain
            // into stretch.
            var scale = Math.Min((float)canvasW / source.Width, (float)canvasH / source.Height);
            var w = Math.Max(1, (int)Math.Round(source.Width * scale));
            var h = Math.Max(1, (int)Math.Round(source.Height * scale));
            footprint = new PixelRect((canvasW - w) / 2, (canvasH - h) / 2, w, h);
            (fittedW, fittedH) = (w, h);
            using var fill = new SKCanvas(canvas);
            fill.DrawColor(baseColor, SKBlendMode.SrcOver);
        }
        else
        {
            // "cover" (default): scale to fill the canvas keeping the aspect ratio; what does not
            // fit is the slack the transform pans across (centred when the transform is identity).
            var scale = Math.Max((float)canvasW / source.Width, (float)canvasH / source.Height);
            footprint = new PixelRect(0, 0, canvasW, canvasH);
            fittedW = Math.Max(canvasW, (int)Math.Ceiling(source.Width * scale));
            fittedH = Math.Max(canvasH, (int)Math.Ceiling(source.Height * scale));
        }

        var window = TransformedSourceRect(fittedW, fittedH, footprint.Width, footprint.Height, bg.Transform);
        var toSourceX = (float)source.Width / fittedW;
        var toSourceY = (float)source.Height / fittedH;
        var sourceRect = new SKRect(window.X * toSourceX, window.Y * toSourceY, window.Right * toSourceX, window.Bottom * toSourceY);
        var sampling = Resampling.For(
            Math.Max(1, (int)Math.Round(sourceRect.Width)),
            Math.Max(1, (int)Math.Round(sourceRect.Height)),
            footprint.Width,
            footprint.Height);

        using (var pixmap = source.PeekPixels())
        using (var image = SKImage.FromPixels(pixmap))
        {
            if (bg.Blur > 0)
            {
                DrawBlurred(canvas, image, sourceRect, footprint, sampling, bg.Blur);
            }
            else
            {
                using var target = new SKCanvas(canvas);
                target.DrawImage(image, sourceRect, ToSkRect(footprint), sampling);
            }
        }

        // Dim: a flat wash of the base colour at fractional opacity over the background only (the
        // layers come later). Blended per pixel with ImageSharp's float arithmetic, because dim over a
        // solid colour is an Exact case (spec 7.1).
        if (bg.Dim > 0)
        {
            EffectsComposer.Wash(canvas, baseColor, Math.Min(bg.Dim, 1f));
        }
    }

    /// <summary>
    /// The scaled window, blurred in destination space (sigma = <paramref name="sigma"/>) with its edges
    /// clamped to the footprint, as ImageSharp blurred the fitted image before drawing it: the scaled
    /// image is drawn into a footprint-sized bitmap first, so the blur's clamp sees the footprint's
    /// edges and nothing of the letterbox bars, and the result is clipped back to the footprint.
    /// </summary>
    private static void DrawBlurred(SKBitmap canvas, SKImage image, SKRect sourceRect, PixelRect footprint, SKSamplingOptions sampling, float sigma)
    {
        using var scaled = RenderSurface.New(footprint.Width, footprint.Height);
        using (var scaledCanvas = new SKCanvas(scaled))
        {
            scaledCanvas.DrawImage(image, sourceRect, SKRect.Create(footprint.Width, footprint.Height), sampling);
        }

        using var scaledPixmap = scaled.PeekPixels();
        using var scaledImage = SKImage.FromPixels(scaledPixmap);
        using var blur = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { ImageFilter = blur };
        using var target = new SKCanvas(canvas);
        target.ClipRect(ToSkRect(footprint));
        target.DrawImage(scaledImage, footprint.X, footprint.Y, paint);
    }

    private static SKRect ToSkRect(PixelRect r) => SKRect.Create(r.X, r.Y, r.Width, r.Height);

    /// <summary>
    /// Sub-rectangle of the fit-scaled background to draw, given pan/zoom: the
    /// destination footprint shrunk by the zoom, panned across everything the
    /// fit-scaled image has to spare. Clamped in-bounds.
    ///
    /// <paramref name="footprintW"/>/<paramref name="footprintH"/> are the destination
    /// the background occupies, NOT necessarily the canvas: for cover/stretch it is
    /// the whole canvas, but for contain it's the smaller letterboxed footprint.
    /// Under "cover" the scaled image is larger than the footprint, and that leftover is
    /// exactly what the user expects to pan across, even at the default Scale=1.
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

    internal static void CreateGradientBackground(SKBitmap canvas, BackgroundLayer bg)
    {
        // Source is authoritative post-migration. The IsEnabled fallback keeps a document
        // that never went through Normalize (an older client POSTing directly) rendering
        // as it did before, but an explicit "solid" always wins, so choosing
        // Solid in the UI cannot be silently overridden by a stale flag left behind.
        var useGradient = bg.Source == BackgroundSources.Gradient
            || (bg.Source != BackgroundSources.Solid && bg.Gradient?.IsEnabled == true);

        using var target = new SKCanvas(canvas);
        if (useGradient && bg.Gradient is not null)
        {
            using var shader = CreateGradientShader(bg.Gradient, canvas.Width, canvas.Height);
            using var paint = new SKPaint { Shader = shader };
            target.DrawRect(SKRect.Create(canvas.Width, canvas.Height), paint);
        }
        else
        {
            target.DrawColor(SafeColor(bg.DimColor, SKColors.Black), SKBlendMode.SrcOver);
        }
    }

    /// <summary>
    /// Builds the gradient shader: radial when the settings ask for it, otherwise a linear
    /// ramp along a line through the centre at <c>Angle</c> degrees, long enough to span
    /// the canvas corner to corner. Beyond the first and last stop the end colours are held
    /// (clamp), with no dithering and unpremultiplied interpolation (Skia's defaults).
    ///
    /// <paramref name="forceLinear"/> exists for the background OVERLAY, which reuses this
    /// same settings type but is deliberately linear-only (see the spec's inert-fields note).
    /// Shared with <see cref="ApplyGradientOverlay"/> so the two cannot drift apart.
    /// </summary>
    internal static SKShader CreateGradientShader(GradientSettings gradient, int width, int height, bool forceLinear = false)
    {
        var stops = BuildGradientStops(gradient);

        if (!forceLinear && gradient.Type == GradientType.Radial)
        {
            var centre = new SKPoint(gradient.CenterX * width, gradient.CenterY * height);
            var radius = Math.Max(1f, gradient.Radius * Math.Min(width, height));
            return SKShader.CreateRadialGradient(centre, radius, stops.Colors, stops.Positions, SKShaderTileMode.Clamp);
        }

        var rad = gradient.Angle * Math.PI / 180.0;
        var dx = (float)Math.Cos(rad);
        var dy = (float)Math.Sin(rad);
        var cx = width / 2f;
        var cy = height / 2f;
        var half = (Math.Abs(dx) * width + Math.Abs(dy) * height) / 2f;

        var p0 = new SKPoint(cx - dx * half, cy - dy * half);
        var p1 = new SKPoint(cx + dx * half, cy + dy * half);

        return SKShader.CreateLinearGradient(p0, p1, stops.Colors, stops.Positions, SKShaderTileMode.Clamp);
    }

    /// <summary>
    /// Composites the background overlay: a linear multi-stop gradient whose stops carry
    /// their own alpha, drawn SrcOver over the finished background and under the layers.
    ///
    /// Deliberately does NOT use BuildGradientStops' Start/End fallback. That fallback yields
    /// an OPAQUE ramp between StartColor/EndColor (Jellyfin brand purple/blue by default,
    /// falling back further to black/white only if those hex values fail to parse), fully
    /// covering the canvas edge to edge. That is a reasonable default for a background
    /// gradient, but for an overlay it would obliterate the poster regardless of which
    /// colours it resolves to. Fewer than two (non-null) stops = off.
    /// </summary>
    internal static void ApplyGradientOverlay(SKBitmap canvas, GradientSettings? overlay)
    {
        if (overlay is null || !overlay.IsEnabled) { return; }
        if (overlay.Stops is null || overlay.Stops.Count(s => s is not null) < 2) { return; }

        using var shader = CreateGradientShader(overlay, canvas.Width, canvas.Height, forceLinear: true);
        using var paint = new SKPaint { Shader = shader };
        using var target = new SKCanvas(canvas);
        target.DrawRect(SKRect.Create(canvas.Width, canvas.Height), paint);
    }

    /// <summary>
    /// Builds the colour stops from the gradient settings: the explicit Stops list
    /// (sorted by position, positions and alphas clamped to 0..1) if it has 2+ entries,
    /// otherwise the Start/End colours as a fallback.
    /// </summary>
    internal static GradientStops BuildGradientStops(GradientSettings gradient)
    {
        // Null entries are skipped even on paths that never ran Normalize.
        var explicitStops = gradient.Stops?.Where(s => s is not null).ToList();
        if (explicitStops is { Count: >= 2 })
        {
            var ordered = explicitStops.OrderBy(s => s.Position).ToList();
            return new GradientStops(
                ordered.Select(s => SkiaColors.WithAlpha(SafeColor(s.Color, new SKColor(128, 128, 128)), s.Alpha)).ToArray(),
                ordered.Select(s => Math.Clamp(s.Position, 0f, 1f)).ToArray());
        }

        return new GradientStops(
            new[] { SafeColor(gradient.StartColor, SKColors.Black), SafeColor(gradient.EndColor, SKColors.White) },
            new[] { 0f, 1f });
    }

    private sealed class RestoreLog : IDisposable
    {
        private readonly ILogger? _previous;
        private bool _disposed;

        public RestoreLog(ILogger? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            TestLog.Value = _previous;
        }
    }
}
