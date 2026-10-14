using System;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// Pins the numbers that make the server's text shadow and outline look like the canvas
/// preview. The canvas is the reference: its semantics come from the HTML canvas spec, and
/// it is what the user designs against.
/// </summary>
public class TextEffectParityTests
{
    private const int Px = 300;

    // A 300×300 black solid background with one white 30px "HELLO" at the centre;
    // `configure` adjusts the layer per test.
    private static CoverDocument Doc(Action<CoverLayer> configure)
    {
        var d = new CoverDocument { Canvas = new CanvasSettings { Width = Px, Height = Px } };
        d.Background.Source = BackgroundSources.Solid;
        d.Background.DimColor = "#000000";
        var layer = new CoverLayer
        {
            Type = "text", Content = "HELLO", Color = "#ffffff",
            Size = 0.1f, X = 0.5f, Y = 0.5f
        };
        configure(layer);
        d.Layers.Add(layer);
        return d;
    }

    private static Image<Rgba32> Render(CoverDocument doc)
    {
        var img = new Image<Rgba32>(Px, Px);
        DocumentRenderer.ComposeDocumentFrame(img, null, doc);
        return img;
    }

    // Weighted centroid and per-axis variance of a per-pixel weight over the whole image.
    private static (double Mass, double Mx, double My, double Vx, double Vy) Moments(
        Image<Rgba32> img, Func<Rgba32, double> weight)
    {
        double m = 0, sx = 0, sy = 0, sxx = 0, syy = 0;
        for (var y = 0; y < img.Height; y++)
            for (var x = 0; x < img.Width; x++)
            {
                var w = weight(img[x, y]);
                if (w <= 0) { continue; }
                m += w; sx += w * x; sy += w * y; sxx += w * x * x; syy += w * y * y;
            }
        Assert.True(m > 0, "Nothing to measure: the weighted ink is empty.");
        var mx = sx / m;
        var my = sy / m;
        return (m, mx, my, sxx / m - mx * mx, syy / m - my * my);
    }

    // Shadow ink: a pure-red shadow over black is (255a, 0, 0). The white glyphs and their
    // grey antialiasing have R == G, so R - G is 255 × shadow alpha wherever the two do not touch.
    private static double ShadowInk(Rgba32 p) => p.R - p.G;

    // Glyph ink: the red shadow has no green at all, so G is the glyphs alone.
    private static double GlyphInk(Rgba32 p) => p.G;

    /// <summary>
    /// The HTML canvas spec blurs a shadow with a Gaussian whose standard deviation is
    /// shadowBlur / 2. ImageSharp's GaussianBlur takes the sigma itself, so passing Blur
    /// through unchanged made every server shadow twice as soft as the preview. Convolving
    /// with a Gaussian adds exactly sigma² to a shape's variance on each axis, so the
    /// variance gained between a hard and a blurred shadow measures the sigma the server
    /// actually used, whatever the glyphs look like.
    /// </summary>
    [Theory]
    [InlineData(8)]
    [InlineData(12)]
    public void ShadowBlur_UsesHalfTheBlurValueAsSigma(int blur)
    {
        // Straight down and clear of the 30px glyphs, so no shadow pixel hides under a glyph.
        CoverDocument ShadowDoc(int b) => Doc(l =>
        {
            l.Y = 0.3f;
            l.Shadow = new TextShadowSettings { Enabled = true, Color = "#ff0000", Blur = b, OffsetX = 0, OffsetY = 50 };
        });

        using var hard = Render(ShadowDoc(0));
        using var soft = Render(ShadowDoc(blur));
        var h = Moments(hard, ShadowInk);
        var s = Moments(soft, ShadowInk);

        var sigma = Math.Sqrt(((s.Vx - h.Vx) + (s.Vy - h.Vy)) / 2);
        var expected = blur / 2.0;
        Assert.InRange(sigma, expected * 0.85, expected * 1.15);
    }

    /// <summary>
    /// Canvas shadow offsets "are not affected by the current transformation matrix" (HTML
    /// spec), so in the preview a rotated layer's shadow still falls in screen space. The
    /// server used to rotate the offset along with the text. Offsets here are perpendicular
    /// to the line of text, so the shadow never slides under the glyphs and the centroid
    /// shift is exactly the offset. Covers both the hard (Blur 0) and the blurred path.
    /// </summary>
    [Theory]
    [InlineData(0f, 0, 40, 0)]
    [InlineData(0f, 0, 40, 6)]
    [InlineData(90f, 40, 0, 0)]
    [InlineData(90f, 40, 0, 6)]
    [InlineData(270f, 40, 0, 0)]
    [InlineData(270f, 40, 0, 6)]
    public void ShadowOffset_IsScreenSpace_OnRotatedLayers(float rotation, int dx, int dy, int blur)
    {
        using var img = Render(Doc(l =>
        {
            l.Rotation = rotation;
            l.Shadow = new TextShadowSettings { Enabled = true, Color = "#ff0000", Blur = blur, OffsetX = dx, OffsetY = dy };
        }));

        var glyph = Moments(img, GlyphInk);
        var shadow = Moments(img, ShadowInk);

        Assert.InRange(shadow.Mx - glyph.Mx, dx - 2.0, dx + 2.0);
        Assert.InRange(shadow.My - glyph.My, dy - 2.0, dy + 2.0);
    }

    /// <summary>Unrotated layers must keep the plain offset-origin path, byte for byte.</summary>
    [Fact]
    public void ShadowTransform_UnrotatedLayer_IsIdentity()
    {
        Assert.True(DocumentRenderer.ShadowTransform(System.Numerics.Matrix3x2.Identity, 7f, -3f).IsIdentity);
    }

    // 60px "HELLO" with a red outline, centred on black.
    private static CoverDocument OutlineDoc(float opacity, bool outline, int width = 4, float rotation = 0f) => Doc(l =>
    {
        l.Size = 0.2f;
        l.Opacity = opacity;
        l.Rotation = rotation;
        l.Outline = new TextOutlineSettings { Enabled = outline, Color = "#ff0000", Width = width };
    });

    // A pixel that is outline and nothing else: fully red, no white fill mixed in.
    private static bool IsPureOutline(Rgba32 p) => p.R >= 250 && p.G <= 5 && p.B <= 5;

    // Horizontal extent of anything bright (white fill or red outline).
    private static int InkWidth(Image<Rgba32> img)
    {
        int min = int.MaxValue, max = int.MinValue;
        for (var y = 0; y < img.Height; y++)
            for (var x = 0; x < img.Width; x++)
                if (img[x, y].R > 128) { if (x < min) { min = x; } if (x > max) { max = x; } }
        return max < min ? 0 : max - min + 1;
    }

    /// <summary>
    /// The canvas strokes the outline ONCE under globalAlpha, so at 50% opacity an outline
    /// pixel is half strength (≈128 of 255 over black). The server used to draw up to 440
    /// offset copies, each at the layer opacity, and they stacked back up to fully opaque.
    /// </summary>
    [Fact]
    public void Outline_AtHalfOpacity_IsHalfStrength()
    {
        using var opaque = Render(OutlineDoc(1f, true));
        using var half = Render(OutlineDoc(0.5f, true));

        double sumR = 0, sumG = 0;
        var n = 0;
        for (var y = 0; y < Px; y++)
            for (var x = 0; x < Px; x++)
            {
                if (!IsPureOutline(opaque[x, y])) { continue; }
                sumR += half[x, y].R;
                sumG += half[x, y].G;
                n++;
            }

        Assert.True(n > 200, $"Expected a solid outline band at full opacity, found {n} pixels.");
        Assert.InRange(sumR / n, 112.0, 143.0);
        Assert.True(sumG / n < 10, $"Outline pixels should stay pure red, mean G was {sumG / n:F1}.");
    }

    /// <summary>
    /// Width parity: the canvas strokes with lineWidth = 2 × Width centred on the glyph
    /// contour, so the outline reaches Width px beyond the glyphs on each side.
    /// </summary>
    [Fact]
    public void Outline_ReachesWidthPixelsBeyondTheGlyphs()
    {
        using var plain = Render(OutlineDoc(1f, false));
        using var outlined = Render(OutlineDoc(1f, true, width: 6));

        var grow = InkWidth(outlined) - InkWidth(plain);
        Assert.InRange(grow, 2 * 6 - 2, 2 * 6 + 2);
    }

    /// <summary>The outline surface must be drawn through the layer rotation like the fill.</summary>
    [Fact]
    public void Outline_FollowsTheLayerRotation()
    {
        using var img = Render(OutlineDoc(1f, true, rotation: 90f));

        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        for (var y = 0; y < Px; y++)
            for (var x = 0; x < Px; x++)
            {
                if (!IsPureOutline(img[x, y])) { continue; }
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }

        Assert.True(maxX >= minX, "The rotated outline drew nothing.");
        var w = maxX - minX + 1;
        var h = maxY - minY + 1;
        Assert.True(h > 2 * w, $"Rotated 90°, the outline should be tall, not wide (w={w}, h={h}).");
    }
}
