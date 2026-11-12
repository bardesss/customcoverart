using System;
using System.IO;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using CustomCoverArt.Services.Rendering;
using SkiaSharp;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// Regression guard for the background-dim black-out bug, and the dim arithmetic itself.
///
/// JPEG library posters decode without an alpha channel. A naive fill of such an
/// image with a semi-transparent brush can ignore the brush alpha and paint fully opaque, so ANY
/// non-zero dim turned the whole background black. The dim is a flat wash at fractional opacity,
/// which blends correctly whatever the source was.
/// </summary>
public class BackgroundDimTests
{
    private static CoverDocument Doc(string path, float dim, string dimColor = "#000000")
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 400, Height = 400 } };
        doc.Background.Source = BackgroundSources.Upload;
        doc.Background.ImagePath = path;
        doc.Background.Fit = "cover";
        doc.Background.Dim = dim;
        doc.Background.DimColor = dimColor;
        return doc;
    }

    [Fact]
    public void Dim_OnJpegLoadedBackground_DarkensButDoesNotBlackOut()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"ccadim_{Guid.NewGuid():N}.jpg");
        using (var src = RenderSurface.New(200, 300, new SKColor(200, 60, 60)))
        {
            TestPixels.SaveJpeg(src, tmp);
        }

        try
        {
            using var decoded = SafeImageLoader.TryDecode(tmp, 1);
            Assert.NotNull(decoded);
            using var canvas = RenderSurface.New(400, 400);

            DocumentRenderer.ComposeDocumentFrame(canvas, decoded!.RootFrame, Doc(tmp, 0.05f));

            var p = TestPixels.Px(canvas, 10, 10);
            // 5% black dim over red(200): ~190, and definitely not blacked out.
            Assert.InRange(p.R, 183, 197);
            Assert.Equal(255, p.A);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    /// <summary>Dim 0 is no dim at all: the background comes through byte for byte.</summary>
    [Fact]
    public void DimZero_LeavesTheBackgroundExactlyAsDrawn()
    {
        using var bg = TestPixels.Painted(400, 400, (x, y) => new SKColor((byte)x, (byte)y, (byte)(x ^ y)));
        using var canvas = RenderSurface.New(400, 400);

        DocumentRenderer.ComposeDocumentFrame(canvas, bg, Doc(string.Empty, 0f, "#ff0000"));

        Assert.Equal(TestPixels.Bytes(bg), TestPixels.Bytes(canvas));
    }

    /// <summary>Dim 1 replaces the background with the dim colour everywhere.</summary>
    [Fact]
    public void DimOne_IsTheDimColourEverywhere()
    {
        using var bg = TestPixels.Painted(50, 80, (x, y) => new SKColor((byte)(x * 5), (byte)(y * 3), 77));
        using var canvas = RenderSurface.New(400, 400);

        DocumentRenderer.ComposeDocumentFrame(canvas, bg, Doc(string.Empty, 1f, "#102a60"));

        for (var y = 0; y < 400; y += 7)
        {
            for (var x = 0; x < 400; x += 7)
            {
                Assert.Equal(new Rgba(0x10, 0x2a, 0x60, 255), TestPixels.Px(canvas, x, y));
            }
        }
    }

    /// <summary>
    /// Dim over a solid colour is an Exact case (spec 7.1): the bytes the float blend produced,
    /// out = dim colour * 0.35 + backdrop * 0.65, rounded. Over (200, 60, 60): 135.6, 53.7, 72.6 -> (136, 54, 73).
    /// Over (1, 1, 1): 6.25, 15.35, 34.25 -> (6, 15, 34), where a SrcOver DrawRect with a byte alpha of
    /// round(0.35 * 255) = 89 lands one level high on every channel, (7, 16, 35).
    /// </summary>
    [Theory]
    [InlineData(200, 60, 60, 136, 54, 73)]
    [InlineData(1, 1, 1, 6, 15, 34)]
    public void Dim_OverASolidColour_MatchesTheFloatBlendExactly(byte r, byte g, byte b, byte er, byte eg, byte eb)
    {
        using var bg = RenderSurface.New(30, 30, new SKColor(r, g, b));
        using var canvas = RenderSurface.New(400, 400);

        DocumentRenderer.ComposeDocumentFrame(canvas, bg, Doc(string.Empty, 0.35f, "#102a60"));

        Assert.Equal(new Rgba(er, eg, eb, 255), TestPixels.Px(canvas, 200, 200));
        Assert.Equal(new Rgba(er, eg, eb, 255), TestPixels.Px(canvas, 0, 399));
    }
}
