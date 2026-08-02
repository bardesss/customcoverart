using CustomCoverArt.Models;
using CustomCoverArt.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// The client canvas has always rendered a text shadow as a soft glow
/// (<c>ctx.shadowBlur</c>), while the server drew a hard offset copy and ignored
/// <see cref="TextShadowSettings.Blur"/> entirely — the most visible disagreement between
/// the live preview and the cover that actually gets applied.
/// </summary>
public class TextShadowBlurTests
{
    private static CoverDocument Doc(bool shadow, int blur)
    {
        var d = new CoverDocument { Canvas = new CanvasSettings { Width = 200, Height = 200 } };
        d.Background.Source = BackgroundSources.Solid;
        d.Background.DimColor = "#000000";
        d.Layers.Add(new CoverLayer
        {
            Type = "text", Content = "HELLO", Color = "#ffffff", Size = 0.22f, X = 0.5f, Y = 0.5f,
            Shadow = new TextShadowSettings { Enabled = shadow, Color = "#ff0000", Blur = blur, OffsetX = 6, OffsetY = 6 }
        });
        return d;
    }

    // Red-DOMINANT pixels: the shadow colour, not the white glyphs and not the grey
    // antialiasing along their edges (which is red-equals-green-equals-blue).
    private static int ShadowPixels(Image<Rgba32> img)
    {
        var n = 0;
        for (var y = 0; y < img.Height; y++)
            for (var x = 0; x < img.Width; x++)
            {
                var p = img[x, y];
                if (p.R > 40 && p.R > p.G + 30 && p.R > p.B + 30) { n++; }
            }
        return n;
    }

    /// <summary>
    /// A blurred shadow spreads over more pixels than a hard one. If Blur is ignored the
    /// two renders are identical, which is exactly the bug.
    /// </summary>
    [Fact]
    public void BlurredShadow_CoversMoreArea_ThanHardShadow()
    {
        using var hard = new Image<Rgba32>(200, 200);
        DocumentRenderer.ComposeDocumentFrame(hard, null, Doc(true, 0));
        using var soft = new Image<Rgba32>(200, 200);
        DocumentRenderer.ComposeDocumentFrame(soft, null, Doc(true, 8));

        var hardArea = ShadowPixels(hard);
        var softArea = ShadowPixels(soft);

        Assert.True(hardArea > 0, "The unblurred shadow should render at all.");
        Assert.True(softArea > hardArea * 1.2,
            $"A blurred shadow should spread wider (hard={hardArea}, soft={softArea}).");
    }

    /// <summary>Blur 0 must stay exactly the crisp shadow it has always been.</summary>
    [Fact]
    public void ZeroBlur_IsUnchanged()
    {
        using var a = new Image<Rgba32>(200, 200);
        DocumentRenderer.ComposeDocumentFrame(a, null, Doc(true, 0));
        using var b = new Image<Rgba32>(200, 200);
        DocumentRenderer.ComposeDocumentFrame(b, null, Doc(true, 0));

        for (var y = 0; y < 200; y++)
            for (var x = 0; x < 200; x++)
                Assert.Equal(a[x, y], b[x, y]);
    }

    /// <summary>The shadow goes UNDER the glyphs, not over them.</summary>
    [Fact]
    public void Shadow_DoesNotCoverTheGlyphs()
    {
        using var img = new Image<Rgba32>(200, 200);
        DocumentRenderer.ComposeDocumentFrame(img, null, Doc(true, 6));

        var white = 0;
        for (var y = 0; y < 200; y++)
            for (var x = 0; x < 200; x++)
            {
                var p = img[x, y];
                if (p.R > 200 && p.G > 200 && p.B > 200) { white++; }
            }
        Assert.True(white > 0, "The text itself must still be drawn on top of its shadow.");
    }

    /// <summary>A disabled shadow draws nothing regardless of Blur.</summary>
    [Fact]
    public void DisabledShadow_DrawsNothing()
    {
        using var img = new Image<Rgba32>(200, 200);
        DocumentRenderer.ComposeDocumentFrame(img, null, Doc(false, 20));
        Assert.Equal(0, ShadowPixels(img));
    }
}
