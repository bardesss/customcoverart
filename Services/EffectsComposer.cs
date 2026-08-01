using CustomCoverArt.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CustomCoverArt.Services;

/// <summary>
/// Non-destructive composition effects, applied in a fixed order around the layers so
/// the client canvas can mirror it exactly:
///
///   background → <see cref="ApplySoftLight"/> → layers → vignette → grain → border
///
/// The border is deliberately last: it is a frame around the finished cover, so grain
/// and vignette must not sit on top of it. Every effect is a no-op when disabled, and
/// the disabled state is the default (see <see cref="EffectSettings"/>) — a document
/// written before these existed must render byte-for-byte as it always did.
///
/// Kept separate from <see cref="DocumentRenderer"/> so that file stays about
/// background/layer compositing.
/// </summary>
public static class EffectsComposer
{
    /// <summary>The post-layer effects, in order. Soft-light is applied by the caller before the layers.</summary>
    public static void Apply(Image<Rgba32> canvas, EffectSettings fx)
    {
        if (fx.Vignette.Enabled) { ApplyVignette(canvas, fx.Vignette); }
        if (fx.Grain.Enabled) { ApplyGrain(canvas, fx.Grain); }
        if (fx.Border.Enabled) { DrawBorder(canvas, fx.Border); }
    }

    /// <summary>Flat colour wash under the layers.</summary>
    public static void ApplySoftLight(Image<Rgba32> canvas, SoftLightSettings s)
    {
        if (!s.Enabled) { return; }
        var opacity = Math.Clamp(s.Opacity, 0f, 1f);
        if (opacity <= 0f) { return; }

        var color = DocumentRenderer.SafeColor(s.Color, Color.White);
        // DrawImage, not Fill: Fill ignores brush alpha on alpha-less pixel formats —
        // the same trap that once blacked out dimmed backgrounds (see ApplyBackgroundLayer).
        using var overlay = new Image<Rgba32>(canvas.Width, canvas.Height, color);
        canvas.Mutate(x => x.DrawImage(overlay, Point.Empty, opacity));
    }

    /// <summary>Radial darkening toward the edges, strongest in the corners.</summary>
    public static void ApplyVignette(Image<Rgba32> canvas, VignetteSettings v)
    {
        var amount = Math.Clamp(v.Amount, 0f, 1f);
        if (amount <= 0f) { return; }

        // Softness 0 would divide by zero below; a hair above it is a hard-edged ring.
        var softness = Math.Clamp(v.Softness, 0.01f, 1f);
        var color = DocumentRenderer.SafeColor(v.Color, Color.Black).ToPixel<Rgba32>();
        int w = canvas.Width, h = canvas.Height;
        float cx = w / 2f, cy = h / 2f;
        float maxD = MathF.Sqrt(cx * cx + cy * cy);
        if (maxD <= 0f) { return; }

        canvas.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                {
                    float dx = x - cx, dy = y - cy;
                    float d = MathF.Sqrt(dx * dx + dy * dy) / maxD;
                    // 0 until `softness` from the edge, then ramps to full strength.
                    float edge = Math.Clamp((d - (1f - softness)) / softness, 0f, 1f);
                    float a = edge * amount;
                    if (a <= 0f) { continue; }

                    ref var px = ref row[x];
                    px.R = (byte)(px.R * (1 - a) + color.R * a);
                    px.G = (byte)(px.G * (1 - a) + color.G * a);
                    px.B = (byte)(px.B * (1 - a) + color.B * a);
                    // Alpha deliberately untouched: a vignette tints, it does not cut holes.
                }
            }
        });
    }

    /// <summary>
    /// Seeded monochrome film grain. The seed lives in the document so re-rendering a
    /// saved design reproduces the same noise instead of shimmering on every apply.
    /// </summary>
    public static void ApplyGrain(Image<Rgba32> canvas, GrainSettings g)
    {
        var amount = Math.Clamp(g.Amount, 0f, 1f);
        if (amount <= 0f) { return; }

        int w = canvas.Width, h = canvas.Height;

        // Draw the noise from a seeded PRNG walked in row-major order. Sampling it
        // inline (rather than pre-filling a w*h buffer) keeps a 4K canvas from
        // allocating megabytes just to be read once, and stays deterministic because
        // the walk order is fixed.
        var rng = new Random(g.Seed);
        var strength = amount * 64f; // +/- up to ~64 levels at full amount

        canvas.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                {
                    float n = (rng.Next(-128, 128) / 128f) * strength;
                    ref var px = ref row[x];
                    px.R = (byte)Math.Clamp(px.R + n, 0, 255);
                    px.G = (byte)Math.Clamp(px.G + n, 0, 255);
                    px.B = (byte)Math.Clamp(px.B + n, 0, 255);
                }
            }
        });
    }

    /// <summary>Inset frame, optionally rounded, optionally doubled with a thinner inner line.</summary>
    public static void DrawBorder(Image<Rgba32> canvas, BorderSettings b)
    {
        var color = DocumentRenderer.SafeColor(b.Color, Color.White);
        int t = Math.Clamp(b.Thickness, 0, 64);
        if (t <= 0) { return; }

        int maxRadius = Math.Min(canvas.Width, canvas.Height) / 2;
        int radius = Math.Clamp(b.Radius, 0, Math.Min(512, maxRadius));

        DrawInsetFrame(canvas, color, inset: 0, thickness: t, radius);

        if (b.Double)
        {
            int gap = Math.Clamp(b.Gap, 0, 32);
            int inset = t + gap;
            // A second frame only fits if there is canvas left inside the first.
            if (inset * 2 < Math.Min(canvas.Width, canvas.Height))
            {
                DrawInsetFrame(canvas, color, inset, Math.Max(1, t / 2), Math.Max(0, radius - inset));
            }
        }
    }

    private static void DrawInsetFrame(Image<Rgba32> canvas, Color color, int inset, int thickness, int radius)
    {
        var pen = Pens.Solid(color, thickness);
        // A stroke straddles its path, so the path runs down the middle of the line —
        // otherwise half the border would fall outside the canvas and be clipped.
        float half = thickness / 2f;
        float x0 = inset + half, y0 = inset + half;
        float x1 = canvas.Width - inset - half, y1 = canvas.Height - inset - half;
        float w = x1 - x0, h = y1 - y0;
        if (w <= 0 || h <= 0) { return; }

        IPath path = radius > 0
            ? BuildRoundedRect(x0, y0, w, h, Math.Min(radius, Math.Min(w, h) / 2f))
            : new RectangularPolygon(x0, y0, w, h);

        canvas.Mutate(ctx => ctx.Draw(pen, path));
    }

    // Four straight edges joined by 90-degree corner arcs. AddArc's angles are degrees,
    // clockwise from 3 o'clock in ImageSharp's y-down space, so the sequence runs
    // top -> right -> bottom -> left.
    private static IPath BuildRoundedRect(float x, float y, float w, float h, float r)
    {
        var pb = new PathBuilder();
        pb.AddLine(new PointF(x + r, y), new PointF(x + w - r, y));
        pb.AddArc(new PointF(x + w - r, y + r), r, r, 0, -90, 90);
        pb.AddLine(new PointF(x + w, y + r), new PointF(x + w, y + h - r));
        pb.AddArc(new PointF(x + w - r, y + h - r), r, r, 0, 0, 90);
        pb.AddLine(new PointF(x + w - r, y + h), new PointF(x + r, y + h));
        pb.AddArc(new PointF(x + r, y + h - r), r, r, 0, 90, 90);
        pb.AddLine(new PointF(x, y + h - r), new PointF(x, y + r));
        pb.AddArc(new PointF(x + r, y + r), r, r, 0, 180, 90);
        pb.CloseFigure();
        return pb.Build();
    }
}
