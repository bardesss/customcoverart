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
}
