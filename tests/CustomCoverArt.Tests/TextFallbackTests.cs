using System;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using CustomCoverArt.Services.Rendering;
using SkiaSharp;
using Xunit;

namespace CustomCoverArt.Tests;

[Collection("TextSkipWarning")]
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
        using var canvas = RenderSurface.New(80, 80, new SKColor(0, 128, 0));
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 80, Height = 80 } };
        doc.Background.DimColor = "#ff0000";
        // Null Shadow breaks the full render; null Content breaks the plain DrawText fallback.
        var layer = new CoverLayer { Type = "text", Content = null!, Shadow = null! };

        var ex = Record.Exception(() => TextRenderer.RenderTextLayerWithFallback(canvas, layer, doc));

        Assert.Null(ex);
        Assert.Equal(new Rgba(0, 128, 0, 255), TestPixels.Px(canvas, 40, 40));
        Assert.Equal(new Rgba(0, 128, 0, 255), TestPixels.Px(canvas, 2, 2));
    }

    [Theory]
    [InlineData(100f, 1400, 1024f)]  // absurd size clamps down
    [InlineData(0.001f, 100, 8f)]    // tiny size clamps up
    [InlineData(0.2f, 1000, 100f)]   // normal: half the layer size
    public void FallbackFontPixelSize_IsClamped(float size, int canvasHeight, float expected)
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = canvasHeight, Height = canvasHeight } };
        Assert.Equal(expected, CustomCoverArt.Services.Rendering.TextRenderer.FallbackFontPixelSize(new CoverLayer { Size = size }, doc), 3);
    }

    private static (CoverDocument Doc, CoverLayer Layer) SimpleText() => (
        new CoverDocument { Canvas = new CanvasSettings { Width = 80, Height = 80 } },
        new CoverLayer { Type = "text", Content = "Hi", Size = 0.3f });

    /// <summary>
    /// If shaping is impossible (HarfBuzz missing or mismatched) both attempts throw and the layer is
    /// skipped: that must leave the canvas alone, say so in the log, and say it only once.
    /// </summary>
    [Fact]
    public void LayerSkippedBecauseShapingFails_WarnsOncePerProcess()
    {
        var (doc, layer) = SimpleText();
        var log = new CapturingLogger<TextFallbackTests>();
        TextRenderer.ResetSkipWarningForTests();
        TextRenderer.ShapeFaultForTests.Value = new DllNotFoundException("libHarfBuzzSharp");
        try
        {
            using (RenderLog.Use(log))
            {
                for (var i = 0; i < 3; i++)
                {
                    using var canvas = RenderSurface.New(80, 80, new SKColor(0, 128, 0));
                    TextRenderer.RenderTextLayerWithFallback(canvas, layer, doc);
                    Assert.Equal(new Rgba(0, 128, 0, 255), TestPixels.Px(canvas, 40, 40));
                }
            }
        }
        finally
        {
            TextRenderer.ShapeFaultForTests.Value = null;
            TextRenderer.ResetSkipWarningForTests();
        }

        var entry = Assert.Single(log.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, entry.Level);
        Assert.Contains("DllNotFoundException", entry.Message);
        Assert.Contains("libHarfBuzzSharp", entry.Message);
        Assert.Contains("skipped", entry.Message);
    }

    [Fact]
    public void LayerSkipped_OutsideARenderScope_DoesNotSpendTheWarning()
    {
        var (doc, layer) = SimpleText();
        var log = new CapturingLogger<TextFallbackTests>();
        TextRenderer.ResetSkipWarningForTests();
        TextRenderer.ShapeFaultForTests.Value = new InvalidOperationException("no shaper");
        try
        {
            using (var canvas = RenderSurface.New(80, 80))
            {
                TextRenderer.RenderTextLayerWithFallback(canvas, layer, doc); // nobody listening
            }

            using (RenderLog.Use(log))
            {
                using var canvas = RenderSurface.New(80, 80);
                TextRenderer.RenderTextLayerWithFallback(canvas, layer, doc);
            }
        }
        finally
        {
            TextRenderer.ShapeFaultForTests.Value = null;
            TextRenderer.ResetSkipWarningForTests();
        }

        Assert.Single(log.Entries);
    }

    [Fact]
    public void LayerThatRendersNormally_LogsNothing()
    {
        var (doc, layer) = SimpleText();
        var log = new CapturingLogger<TextFallbackTests>();
        TextRenderer.ResetSkipWarningForTests();

        using (RenderLog.Use(log))
        using (var canvas = RenderSurface.New(80, 80))
        {
            TextRenderer.RenderTextLayerWithFallback(canvas, layer, doc);
        }

        Assert.Empty(log.Entries);
    }
}

/// <summary>
/// The text-skip warning is once per process, so a render in any other test class that skips a
/// text layer could use it up mid-test. Run this collection on its own, after the parallel ones.
/// </summary>
[CollectionDefinition("TextSkipWarning", DisableParallelization = true)]
public class TextSkipWarningCollection
{
}
