using CustomCoverArt.Models;
using CustomCoverArt.Services.Rendering;
using SkiaSharp;

namespace CustomCoverArt.Services;

/// <summary>
/// Non-destructive composition effects, applied in a fixed order around the layers so
/// the client canvas can mirror it exactly:
///
///   background → <see cref="ApplySoftLight"/> → layers → vignette → grain → border
///
/// The border is deliberately last: it is a frame around the finished cover, so grain
/// and vignette must not sit on top of it. Every effect is a no-op when disabled, and
/// the disabled state is the default (see <see cref="EffectSettings"/>), because a
/// document written before these existed must render byte-for-byte as it always did.
///
/// Kept separate from <see cref="DocumentRenderer"/> so that file stays about
/// background/layer compositing.
/// </summary>
public static class EffectsComposer
{
    /// <summary>The post-layer effects, in order. Soft-light is applied by the caller before the layers.</summary>
    internal static void Apply(SKBitmap canvas, EffectSettings fx)
    {
        if (fx.Vignette.Enabled) { ApplyVignette(canvas, fx.Vignette); }
        if (fx.Grain.Enabled) { ApplyGrain(canvas, fx.Grain); }
        if (fx.Border.Enabled) { DrawBorder(canvas, fx.Border); }
    }

    /// <summary>Flat colour wash under the layers.</summary>
    internal static void ApplySoftLight(SKBitmap canvas, SoftLightSettings s)
    {
        if (!s.Enabled) { return; }
        var opacity = Math.Clamp(s.Opacity, 0f, 1f);
        if (opacity <= 0f) { return; }

        Wash(canvas, SkiaColors.Parse(s.Color, SKColors.White), opacity);
    }

    /// <summary>
    /// SrcOver of a flat <paramref name="color"/> (its own alpha times <paramref name="opacity"/>) over
    /// the whole canvas, with ImageSharp's float arithmetic. Shared by soft light and the background dim,
    /// both of which are Exact over a solid colour (spec 7.1).
    /// </summary>
    internal static void Wash(SKBitmap canvas, SKColor color, float opacity)
    {
        RequirePremulRgba(canvas);

        // A flat SrcOver wash, blended per pixel rather than with DrawRect: Skia's SrcOver of the colour at
        // alpha = round(opacity * 255) was one level off ImageSharp's float blend on many pixels, and soft
        // light over a solid colour is an Exact case (spec 7.1). The arithmetic below (values scaled by
        // 1/255f, fused multiply-add, round half to even) matches ImageSharp's SIMD path exactly: 0
        // differences against DrawImage over 256 backdrop levels x 7 colours x 9 opacities. ImageSharp's
        // scalar row tail (the last width % 8 pixels of a row) rounds exact .5 ties up instead, so up to one
        // level can differ there; that is an ImageSharp inconsistency and is not reproduced.
        // Pinned by EffectsRenderTests.SoftLight_FractionalOpacity_MatchesPinnedBytes.
        const float inv = 1f / 255f;
        float srcAlpha = color.Alpha * inv * opacity;
        float keep = 1f - srcAlpha;
        float sr = color.Red * inv, sg = color.Green * inv, sb = color.Blue * inv;

        var bytes = canvas.GetPixelSpan();
        int stride = canvas.RowBytes;
        for (int y = 0; y < canvas.Height; y++)
        {
            int o = y * stride;
            for (int x = 0; x < canvas.Width; x++, o += 4)
            {
                // The buffer is premultiplied, so the backdrop channels blend as they are (on an opaque
                // pixel that is the plain colour).
                byte alpha = Blend(bytes[o + 3] * inv, 1f, srcAlpha, keep);
                bytes[o] = Cap(Blend(bytes[o] * inv, sr, srcAlpha, keep), alpha);
                bytes[o + 1] = Cap(Blend(bytes[o + 1] * inv, sg, srcAlpha, keep), alpha);
                bytes[o + 2] = Cap(Blend(bytes[o + 2] * inv, sb, srcAlpha, keep), alpha);
                bytes[o + 3] = alpha;
            }
        }
    }

    // out = src * a + backdrop * (1 - a), where the source is 1 for the alpha channel.
    private static byte Blend(float backdrop, float src, float a, float keep)
    {
        var v = MathF.FusedMultiplyAdd(src, a, backdrop * keep) * 255f;
        return (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
    }

    /// <summary>Radial darkening toward the edges, strongest in the corners.</summary>
    internal static void ApplyVignette(SKBitmap canvas, VignetteSettings v)
    {
        var amount = Math.Clamp(v.Amount, 0f, 1f);
        if (amount <= 0f) { return; }

        // Softness 0 would divide by zero below; a hair above it is a hard-edged ring.
        var softness = Math.Clamp(v.Softness, 0.01f, 1f);
        var color = SkiaColors.Parse(v.Color, SKColors.Black);
        RequirePremulRgba(canvas);
        int w = canvas.Width, h = canvas.Height;
        float cx = w / 2f, cy = h / 2f;
        float maxD = MathF.Sqrt(cx * cx + cy * cy);
        if (maxD <= 0f) { return; }

        // Premultiplied RGBA. On opaque pixels that is the plain colour, so the arithmetic is
        // exactly what it always was; below full alpha each channel is capped at alpha so the
        // pixel stays a valid premultiplied colour.
        var bytes = canvas.GetPixelSpan();
        int stride = canvas.RowBytes;
        for (int y = 0; y < h; y++)
        {
            int o = y * stride;
            for (int x = 0; x < w; x++, o += 4)
            {
                float dx = x - cx, dy = y - cy;
                float d = MathF.Sqrt(dx * dx + dy * dy) / maxD;
                // 0 until `softness` from the edge, then ramps to full strength.
                float edge = Math.Clamp((d - (1f - softness)) / softness, 0f, 1f);
                float a = edge * amount;
                if (a <= 0f) { continue; }

                byte alpha = bytes[o + 3];
                // The pixel is premultiplied, so the vignette colour is too (exactly 1 on opaque pixels).
                float k = alpha / 255f;
                bytes[o] = Cap((byte)(bytes[o] * (1 - a) + color.Red * k * a), alpha);
                bytes[o + 1] = Cap((byte)(bytes[o + 1] * (1 - a) + color.Green * k * a), alpha);
                bytes[o + 2] = Cap((byte)(bytes[o + 2] * (1 - a) + color.Blue * k * a), alpha);
                // Alpha deliberately untouched: a vignette tints, it does not cut holes.
            }
        }
    }

    /// <summary>
    /// Seeded monochrome film grain. The seed lives in the document so re-rendering a
    /// saved design reproduces the same noise instead of shimmering on every apply.
    /// </summary>
    internal static void ApplyGrain(SKBitmap canvas, GrainSettings g)
    {
        var amount = Math.Clamp(g.Amount, 0f, 1f);
        if (amount <= 0f) { return; }

        RequirePremulRgba(canvas);
        int w = canvas.Width, h = canvas.Height;

        // Draw the noise from a seeded PRNG walked in row-major order. Sampling it
        // inline (rather than pre-filling a w*h buffer) keeps a 4K canvas from
        // allocating megabytes just to be read once, and stays deterministic because
        // the walk order is fixed.
        var rng = new Random(g.Seed);
        var strength = amount * 64f; // +/- up to ~64 levels at full amount

        var bytes = canvas.GetPixelSpan();
        int stride = canvas.RowBytes;
        for (int y = 0; y < h; y++)
        {
            int o = y * stride;
            for (int x = 0; x < w; x++, o += 4)
            {
                float n = (rng.Next(-128, 128) / 128f) * strength;
                byte alpha = bytes[o + 3];
                n *= alpha / 255f; // noise scales with coverage on premultiplied pixels (exactly 1 when opaque)
                bytes[o] = Cap((byte)Math.Clamp(bytes[o] + n, 0, 255), alpha);
                bytes[o + 1] = Cap((byte)Math.Clamp(bytes[o + 1] + n, 0, 255), alpha);
                bytes[o + 2] = Cap((byte)Math.Clamp(bytes[o + 2] + n, 0, 255), alpha);
            }
        }
    }

    /// <summary>Inset frame, optionally rounded, optionally doubled with a thinner inner line.</summary>
    internal static void DrawBorder(SKBitmap canvas, BorderSettings b)
    {
        int t = Math.Clamp(b.Thickness, 0, 64);
        if (t <= 0) { return; }
        var color = SkiaColors.Parse(b.Color, SKColors.White);

        RequirePremulRgba(canvas);
        int maxRadius = Math.Min(canvas.Width, canvas.Height) / 2;
        int radius = Math.Clamp(b.Radius, 0, Math.Min(512, maxRadius));

        using var surface = new SKCanvas(canvas);
        DrawInsetFrame(surface, canvas.Width, canvas.Height, color, inset: 0, thickness: t, radius);

        if (b.Double)
        {
            int gap = Math.Clamp(b.Gap, 0, 32);
            int inset = t + gap;
            // A second frame only fits if there is canvas left inside the first.
            if (inset * 2 < Math.Min(canvas.Width, canvas.Height))
            {
                DrawInsetFrame(surface, canvas.Width, canvas.Height, color, inset, Math.Max(1, t / 2), Math.Max(0, radius - inset));
            }
        }
    }

    private static void DrawInsetFrame(SKCanvas surface, int width, int height, SKColor color, int inset, int thickness, int radius)
    {
        // A stroke straddles its path, so the path runs down the middle of the line.
        // Otherwise half the border would fall outside the canvas and be clipped.
        float half = thickness / 2f;
        float x0 = inset + half, y0 = inset + half;
        float x1 = width - inset - half, y1 = height - inset - half;
        float w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0) { return; }

        using var paint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = thickness,
            IsAntialias = true,
            Color = color,
        };
        var rect = new SKRect(x0, y0, x1, y1);
        if (radius > 0)
        {
            var r = Math.Min(radius, Math.Min(w, h) / 2f);
            surface.DrawRoundRect(rect, r, r, paint);
        }
        else
        {
            surface.DrawRect(rect, paint);
        }
    }

    private static byte Cap(byte value, byte alpha) => value > alpha ? alpha : value;

    private static void RequirePremulRgba(SKBitmap canvas)
    {
        if (canvas.ColorType != SKColorType.Rgba8888 || canvas.AlphaType != SKAlphaType.Premul)
        {
            throw new ArgumentException($"Expected an Rgba8888/Premul bitmap, got {canvas.ColorType}/{canvas.AlphaType}.", nameof(canvas));
        }
    }
}
