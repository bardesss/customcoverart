using System.Numerics;
using CustomCoverArt.Models;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace CustomCoverArt.Services.Rendering;

/// <summary>
/// One shaped line of text positioned from origin (0, 0) on the baseline: the glyphs as a blob and
/// as one outline path, its advance width and the font's vertical metrics. Dispose it to release both.
/// </summary>
/// <param name="Blob">The glyphs, or null when shaping produced none (empty text).</param>
/// <param name="Width">Total advance width in pixels.</param>
/// <param name="Ascent">Skia ascent: distance above the baseline, negative.</param>
/// <param name="Descent">Skia descent: distance below the baseline, positive.</param>
/// <param name="Path">The same glyphs at the same HarfBuzz positions as one path (what is drawn); null when <paramref name="Blob"/> is.</param>
internal sealed record ShapedLine(SKTextBlob? Blob, float Width, float Ascent, float Descent, SKPath? Path = null) : IDisposable
{
    public void Dispose()
    {
        Blob?.Dispose();
        Path?.Dispose();
    }
}

/// <summary>
/// Draws text layers with SkiaSharp, shaped by HarfBuzz (spec 6.4). Layout follows the editor's
/// canvas, which is the reference: <c>textBaseline = 'middle'</c>, <c>textAlign</c> left/centre/right
/// of the shaped advance, rotation about the layer anchor, a screen-space shadow blurred with sigma
/// <c>blur / 2</c>, and an outline that is one round-joined stroke of width <c>2 x Width</c>.
///
/// Shadow, outline and fill all draw the glyph OUTLINE PATH, never the blob. Skia rasterises a
/// blob through cached glyph masks with a colour-dependent "fake gamma + contrast" boost (white
/// Noto Light at 34 px measured 28 % heavier than its path, black 10 % lighter, and the boost
/// switches off abruptly above 256 px); the mask tables also differ per platform build. A path
/// fill has none of that, so weight does not depend on colour, size or platform, matches the
/// earlier renderer's geometry, and shares its geometry with the outline stroke.
/// </summary>
internal static class TextRenderer
{
    /// <summary>
    /// Renders a text layer: shadow, outline, then fill. Coordinates are normalized on the layer
    /// (0..1 of the bitmap); the font size is a fraction of the DOCUMENT's canvas height. Throws on
    /// failure; <see cref="RenderTextLayerWithFallback"/> is the total version.
    /// </summary>
    public static void RenderTextLayer(SKBitmap canvas, CoverLayer layer, CoverDocument doc)
    {
        // The font size keys off doc.Canvas.Height (the design's reference resolution), not the
        // bitmap: an animated render may draw onto a downscaled working buffer whose document was
        // rescaled to match (CoverArtService.ScaleDocumentForCanvas), and a caller may also draw a
        // document onto a buffer of another size and still expect proportional text. The [8, 1024]
        // clamp bounds glyph rasterisation for every caller (a render-thread DoS guard).
        var fontPixelSize = Math.Clamp(layer.Size * doc.Canvas.Height, 8f, 1024f);
        var face = FontStore.Resolve(layer);

        // Per-layer opacity. The client sets ctx.globalAlpha once per layer and composites each
        // canvas operation (shadow, stroke, fill) separately under it, so every element carries
        // the opacity in its own colour here.
        var opacity = Math.Clamp(layer.Opacity, 0f, 1f);
        var textColor = SkiaColors.WithAlpha(SkiaColors.Parse(layer.Color, SKColors.White), opacity);

        var anchor = new Vector2(layer.X * canvas.Width, layer.Y * canvas.Height);

        // Rotation pivots on the layer's own anchor, matching the client's translate/rotate.
        var rotate = layer.Rotation != 0f
            ? Matrix3x2.CreateRotation((float)(layer.Rotation * Math.PI / 180.0), anchor)
            : Matrix3x2.Identity;

        // Offsets clamp to [-50, 50] px and blur to [0, 50]: the document clamps (spec 7.1).
        var shadowColor = SkiaColors.WithAlpha(SkiaColors.Parse(layer.Shadow.Color, SKColors.Black), opacity);
        var shadowBlur = Math.Clamp(layer.Shadow.Blur, 0, 50);
        var shadowDx = Math.Clamp(layer.Shadow.OffsetX, -50, 50);
        var shadowDy = Math.Clamp(layer.Shadow.OffsetY, -50, 50);
        var shadowTransform = ShadowTransform(rotate, shadowDx, shadowDy);

        var outlineWidth = layer.Outline.Enabled ? Math.Clamp(layer.Outline.Width, 0, 10) : 0;
        var outlineColor = SkiaColors.WithAlpha(SkiaColors.Parse(layer.Outline.Color, SKColors.Black), opacity);

        using var line = Shape(face, fontPixelSize, layer.Content);
        var glyphs = line.Path;
        if (glyphs is null)
        {
            return;
        }

        var origin = Origin(line, layer.Align, anchor);

        // The shadow first, so it sits beneath the outline and the fill.
        if (layer.Shadow.Enabled)
        {
            DrawShadow(canvas, glyphs, origin, shadowDx, shadowDy, shadowTransform, shadowColor, shadowBlur);
        }

        using var target = new SKCanvas(canvas);
        target.SetMatrix(ToSkMatrix(rotate));
        target.Translate(origin.X, origin.Y);

        // Outline: the canvas strokes the glyph contours ONCE under globalAlpha with lineWidth =
        // 2 x Width and round joins, so half the stroke lies inside the glyph, under the fill.
        if (outlineWidth > 0)
        {
            using var stroke = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 2f * outlineWidth,
                StrokeJoin = SKStrokeJoin.Round,
                Color = outlineColor,
            };
            target.DrawPath(glyphs, stroke);
        }

        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = textColor };
        target.DrawPath(glyphs, fill);
    }

    /// <summary>
    /// Renders a text layer, retrying once without effects if the full render throws. If that
    /// fails too the layer is skipped: one bad layer must never cost the rest of the cover.
    /// </summary>
    public static void RenderTextLayerWithFallback(SKBitmap canvas, CoverLayer layer, CoverDocument doc)
    {
        try
        {
            RenderTextLayer(canvas, layer, doc);
        }
        catch
        {
            try
            {
                // Plain bundled Regular at half size, centred on the canvas, no effects or opacity.
                using var line = Shape(FontStore.Bundled(FontWeight.Normal), FallbackFontPixelSize(layer, doc), layer.Content);
                if (line.Path is null)
                {
                    return;
                }

                var origin = Origin(line, TextAlign.Center, new Vector2(canvas.Width / 2f, canvas.Height / 2f));
                using var target = new SKCanvas(canvas);
                target.Translate(origin.X, origin.Y);
                using var paint = new SKPaint { IsAntialias = true, Color = SkiaColors.Parse(layer.Color, SKColors.White) };
                target.DrawPath(line.Path, paint);
            }
            catch
            {
                // Skip this layer. Filling the canvas here (as this once did) erased the
                // background and every layer already drawn.
            }
        }
    }

    /// <summary>
    /// Font size for the effects-free fallback: half the layer's size, clamped to the same
    /// [8, 1024] px range as the main render so a bad layer cannot drive huge rasterisation.
    /// </summary>
    public static float FallbackFontPixelSize(CoverLayer layer, CoverDocument doc)
        => Math.Clamp(layer.Size * doc.Canvas.Height * 0.5f, 8f, 1024f);

    /// <summary>
    /// Drawing transform for a text layer's shadow. The canvas preview offsets a shadow in SCREEN
    /// space: per the HTML canvas spec, shadowOffsetX/Y "are not affected by the current
    /// transformation matrix", so a rotated layer's shadow still falls the way the offset says on
    /// screen. The shadow is drawn at the anchor plus (dx, dy), so this undoes the offset, rotates
    /// about the anchor, and re-applies it after the rotation. System.Numerics uses row vectors,
    /// so <c>A * B</c> applies A first: T(-d), then R, then T(+d). An unrotated layer gets identity.
    /// </summary>
    public static Matrix3x2 ShadowTransform(Matrix3x2 rotate, float dx, float dy)
    {
        if (rotate.IsIdentity)
        {
            return Matrix3x2.Identity;
        }

        return Matrix3x2.CreateTranslation(-dx, -dy) * rotate * Matrix3x2.CreateTranslation(dx, dy);
    }

    /// <summary>The same affine map as a Skia matrix (System.Numerics row vectors, Skia column vectors).</summary>
    public static SKMatrix ToSkMatrix(Matrix3x2 m) => new()
    {
        ScaleX = m.M11,
        SkewY = m.M12,
        SkewX = m.M21,
        ScaleY = m.M22,
        TransX = m.M31,
        TransY = m.M32,
        Persp0 = 0,
        Persp1 = 0,
        Persp2 = 1,
    };

    /// <summary>
    /// Shapes one line with HarfBuzz (kerning, ligatures, marks, as Chrome's canvas does) into a
    /// positioned blob. A glyph the face lacks draws as its .notdef; there is no font fallback.
    /// </summary>
    public static ShapedLine Shape(SKTypeface face, float pixelSize, string text)
    {
        ArgumentNullException.ThrowIfNull(face);
        ArgumentNullException.ThrowIfNull(text);

        using var font = NewFont(face, pixelSize);
        var metrics = font.Metrics;
        if (text.Length == 0)
        {
            return new ShapedLine(null, 0f, metrics.Ascent, metrics.Descent);
        }

        // SKShaper keeps a HarfBuzz buffer and is not thread-safe, so one per call.
        using var shaper = new SKShaper(face);
        var result = shaper.Shape(text, 0f, 0f, font);
        var count = result.Codepoints.Length;
        if (count == 0)
        {
            return new ShapedLine(null, result.Width, metrics.Ascent, metrics.Descent);
        }

        using var builder = new SKTextBlobBuilder();
        var run = builder.AllocatePositionedRun(font, count);
        var glyphs = run.Glyphs;
        var positions = run.Positions;
        var path = new SKPath();
        try
        {
            for (var i = 0; i < count; i++)
            {
                var glyph = (ushort)result.Codepoints[i];
                var at = result.Points[i];
                glyphs[i] = glyph;
                positions[i] = at;

                // Each glyph's outline at its shaped position; glyphs without one (spaces) add nothing.
                using var outline = font.GetGlyphPath(glyph);
                if (outline is not null && !outline.IsEmpty)
                {
                    path.AddPath(outline, at.X, at.Y);
                }
            }

            return new ShapedLine(builder.Build(), result.Width, metrics.Ascent, metrics.Descent, path);
        }
        catch
        {
            path.Dispose();
            throw;
        }
    }

    private static SKFont NewFont(SKTypeface face, float pixelSize) => new(face, pixelSize)
    {
        // Unhinted, subpixel-positioned, linear metrics: layout does not snap to the pixel grid
        // differently per platform (spec 6.4).
        Edging = SKFontEdging.Antialias,
        Subpixel = true,
        Hinting = SKFontHinting.None,
        LinearMetrics = true,
    };

    /// <summary>
    /// Baseline origin of a line anchored at <paramref name="anchor"/>: x from the alignment and the
    /// shaped width, y so the ascent/descent box is centred on the anchor (<c>textBaseline = 'middle'</c>).
    /// </summary>
    private static SKPoint Origin(ShapedLine line, TextAlign align, Vector2 anchor)
    {
        var x = align switch
        {
            TextAlign.Left => anchor.X,
            TextAlign.Right => anchor.X - line.Width,
            _ => anchor.X - (line.Width / 2f),
        };
        var y = anchor.Y - ((line.Ascent + line.Descent) / 2f);
        return new SKPoint(x, y);
    }

    private static void DrawShadow(
        SKBitmap canvas,
        SKPath glyphs,
        SKPoint origin,
        float dx,
        float dy,
        Matrix3x2 shadowTransform,
        SKColor color,
        int blur)
    {
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };

        if (blur <= 0)
        {
            // An unblurred shadow is a plain offset copy; one SrcOver draw needs no surface of its own.
            using var direct = new SKCanvas(canvas);
            direct.SetMatrix(ToSkMatrix(shadowTransform));
            direct.Translate(origin.X + dx, origin.Y + dy);
            direct.DrawPath(glyphs, paint);
            return;
        }

        // A blurred shadow needs its own surface, so the blur never touches what is already on the
        // canvas. Canvas shadowBlur is not a sigma: the HTML spec's shadow Gaussian has standard
        // deviation shadowBlur / 2.
        using var surface = RenderSurface.New(canvas.Width, canvas.Height);
        using (var shadowCanvas = new SKCanvas(surface))
        {
            shadowCanvas.SetMatrix(ToSkMatrix(shadowTransform));
            shadowCanvas.Translate(origin.X + dx, origin.Y + dy);
            shadowCanvas.DrawPath(glyphs, paint);
        }

        var sigma = blur / 2f;
        using var filter = SKImageFilter.CreateBlur(sigma, sigma);
        using var composite = new SKPaint { ImageFilter = filter };
        using var target = new SKCanvas(canvas);
        target.DrawBitmap(surface, 0, 0, composite);
    }
}
