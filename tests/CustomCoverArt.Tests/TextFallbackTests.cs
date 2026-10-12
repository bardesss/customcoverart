using CustomCoverArt.Models;
using CustomCoverArt.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CustomCoverArt.Tests;

public class TextFallbackTests
{
    /// <summary>
    /// When both the full render and the simple fallback throw, the layer must be skipped.
    /// The old "ultimate fallback" filled the whole canvas with DimColor, erasing the
    /// background and every layer drawn before it because of one bad layer.
    /// </summary>
    [Fact]
    public void LayerThatCannotRenderAtAll_LeavesTheCanvasUntouched()
    {
        using var canvas = new Image<Rgba32>(80, 80, Color.Green);
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 80, Height = 80 } };
        doc.Background.DimColor = "#ff0000";
        // Null Shadow breaks the full render; null Content breaks the plain DrawText fallback.
        var layer = new CoverLayer { Type = "text", Content = null!, Shadow = null! };

        var ex = Record.Exception(() => DocumentRenderer.RenderTextLayerWithFallback(canvas, layer, doc));

        Assert.Null(ex);
        Assert.Equal(new Rgba32(0, 128, 0, 255), canvas[40, 40]);
        Assert.Equal(new Rgba32(0, 128, 0, 255), canvas[2, 2]);
    }

    [Theory]
    [InlineData(100f, 1400, 1024f)]  // absurd size clamps down
    [InlineData(0.001f, 100, 8f)]    // tiny size clamps up
    [InlineData(0.2f, 1000, 100f)]   // normal: half the layer size
    public void FallbackFontPixelSize_IsClamped(float size, int canvasHeight, float expected)
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = canvasHeight, Height = canvasHeight } };
        Assert.Equal(expected, DocumentRenderer.FallbackFontPixelSize(new CoverLayer { Size = size }, doc), 3);
    }
}
