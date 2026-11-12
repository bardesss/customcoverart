using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using CustomCoverArt.Services.Rendering;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Xunit;

namespace CustomCoverArt.Tests;

public sealed class ImageLayerRenderTests : IDisposable
{
    private readonly List<string> _files = new();

    public void Dispose()
    {
        foreach (var f in _files)
        {
            try { File.Delete(f); } catch { }
        }
    }

    private string Png(int w, int h, SKColor color)
    {
        var path = Path.Combine(Path.GetTempPath(), $"logo_{Guid.NewGuid():N}.png");
        using (var logo = RenderSurface.New(w, h, color))
        {
            TestPixels.SavePng(logo, path);
        }

        _files.Add(path);
        return path;
    }

    private static CoverLayer Layer(string path, float w, float h, Action<CoverLayer>? tweak = null)
    {
        var layer = new CoverLayer { Type = "image", ImagePath = path, X = 0.5f, Y = 0.5f, Width = w, Height = h, Opacity = 1f };
        tweak?.Invoke(layer);
        return layer;
    }

    [Fact]
    public void RenderImageLayer_DrawsLogoPixelsAtCenter()
    {
        var tmp = Png(50, 50, SKColors.Red);
        using var canvas = RenderSurface.New(200, 200); // transparent

        DocumentRenderer.RenderImageLayer(canvas, Layer(tmp, 0.25f, 0.25f));

        var center = TestPixels.Px(canvas, 100, 100);
        Assert.True(center.R > 200 && center.A > 200, "Logo should be drawn opaque red at the center.");
        Assert.Equal(0, TestPixels.Px(canvas, 5, 5).A); // corners stay transparent
    }

    /// <summary>
    /// An unrotated layer is pixel-aligned: a 50 px logo centred on 100 covers exactly 75..124, with
    /// crisp edges (no half-pixel smear from centring on a fractional origin).
    /// </summary>
    [Fact]
    public void RenderImageLayer_Unrotated_IsPixelAligned()
    {
        var tmp = Png(50, 50, SKColors.Red);
        using var canvas = RenderSurface.New(200, 200);

        DocumentRenderer.RenderImageLayer(canvas, Layer(tmp, 0.25f, 0.25f));

        Assert.Equal(new Rgba(255, 0, 0, 255), TestPixels.Px(canvas, 75, 75));
        Assert.Equal(new Rgba(255, 0, 0, 255), TestPixels.Px(canvas, 124, 124));
        Assert.Equal(0, TestPixels.Px(canvas, 74, 100).A);
        Assert.Equal(0, TestPixels.Px(canvas, 125, 100).A);
        Assert.Equal(0, TestPixels.Px(canvas, 100, 74).A);
        Assert.Equal(0, TestPixels.Px(canvas, 100, 125).A);
    }

    [Fact]
    public void RenderImageLayer_MissingFile_DoesNotThrow()
    {
        using var canvas = RenderSurface.New(100, 100);
        var layer = Layer("/no/such/file.png", 0.5f, 0.5f);
        DocumentRenderer.RenderImageLayer(canvas, layer); // must be a no-op, not throw
        Assert.Equal(0, TestPixels.Px(canvas, 50, 50).A);
    }

    /// <summary>
    /// Opacity must fade the layer rather than being ignored: half-opaque red over a
    /// transparent canvas lands at alpha ~128, not 255.
    /// </summary>
    [Fact]
    public void RenderImageLayer_HalfOpacity_BlendsAtHalfAlpha()
    {
        var tmp = Png(40, 40, SKColors.Red);
        using var canvas = RenderSurface.New(100, 100);

        DocumentRenderer.RenderImageLayer(canvas, Layer(tmp, 0.4f, 0.4f, l => l.Opacity = 0.5f));

        var center = TestPixels.Px(canvas, 50, 50);
        Assert.InRange(center.A, 127, 128);
        Assert.Equal(255, center.R);
    }

    /// <summary>Opacity 0 draws nothing at all: the canvas bytes are unchanged.</summary>
    [Fact]
    public void RenderImageLayer_ZeroOpacity_DrawsNothing()
    {
        var tmp = Png(40, 40, SKColors.Red);
        using var canvas = RenderSurface.New(100, 100, new SKColor(10, 20, 30));
        var before = TestPixels.Bytes(canvas);

        DocumentRenderer.RenderImageLayer(canvas, Layer(tmp, 0.4f, 0.4f, l => l.Opacity = 0f));

        Assert.Equal(before, TestPixels.Bytes(canvas));
    }

    /// <summary>
    /// A rotated logo stays centred on its anchor: it is drawn about (X, Y), as the client does.
    /// A 45°-rotated square leaves its bounding-box corners transparent while the centre stays
    /// covered, and that asymmetry is what pins the centring down.
    /// </summary>
    [Fact]
    public void RenderImageLayer_Rotated_StaysCenteredOnAnchor()
    {
        var tmp = Png(64, 64, SKColors.Red);
        using var canvas = RenderSurface.New(200, 200);

        DocumentRenderer.RenderImageLayer(canvas, Layer(tmp, 0.5f, 0.5f, l => l.Rotation = 45f));

        Assert.True(TestPixels.Px(canvas, 100, 100).A > 200, "Rotated logo must still cover its anchor point.");
        // 100px square rotated 45° => a diamond of half-diagonal ~70px: the point
        // 60px straight up from the centre is inside, 60px diagonally out is not.
        Assert.True(TestPixels.Px(canvas, 100, 40).A > 200, "Diamond tip should cover straight-up 60px.");
        Assert.True(TestPixels.Px(canvas, 40, 100).A > 200, "Diamond tip should cover straight-left 60px.");
        Assert.Equal(0, TestPixels.Px(canvas, 142, 142).A);
        Assert.Equal(0, TestPixels.Px(canvas, 58, 58).A);
    }

    /// <summary>A quarter turn swaps the footprint: an 80x20 bar becomes a 20x80 one about the same centre.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(90f)]
    [InlineData(-90f)]
    public void RenderImageLayer_QuarterTurn_SwapsTheFootprint(float rotation)
    {
        var tmp = Png(80, 20, SKColors.Red);
        using var canvas = RenderSurface.New(200, 200);

        DocumentRenderer.RenderImageLayer(canvas, Layer(tmp, 0.4f, 0.1f, l => l.Rotation = rotation));

        var vertical = rotation != 0f;
        int Alpha(int x, int y) => TestPixels.Px(canvas, x, y).A;
        Assert.Equal(255, Alpha(100, 100));
        // Along the long axis, 30 px out from the centre is inside; along the short axis it is outside.
        Assert.Equal(vertical ? 255 : 0, Alpha(100, 70));
        Assert.Equal(vertical ? 255 : 0, Alpha(100, 130));
        Assert.Equal(vertical ? 0 : 255, Alpha(70, 100));
        Assert.Equal(vertical ? 0 : 255, Alpha(130, 100));
    }

    /// <summary>A layer dragged wholly off the canvas is a legitimate state: no throw, nothing drawn.</summary>
    [Theory]
    [InlineData(3f, 0.5f)]
    [InlineData(-2f, 0.5f)]
    [InlineData(0.5f, 9f)]
    [InlineData(-5f, -5f)]
    public void RenderImageLayer_OffCanvas_DoesNothingAndDoesNotThrow(float x, float y)
    {
        var tmp = Png(40, 40, SKColors.Red);
        using var canvas = RenderSurface.New(100, 100, SKColors.Black);
        var before = TestPixels.Bytes(canvas);

        var ex = Record.Exception(() => DocumentRenderer.RenderImageLayer(canvas, Layer(tmp, 0.4f, 0.4f, l => { l.X = x; l.Y = y; })));

        Assert.Null(ex);
        Assert.Equal(before, TestPixels.Bytes(canvas));
    }

    /// <summary>Half off the canvas: the part that is on it is drawn, the rest clipped.</summary>
    [Fact]
    public void RenderImageLayer_PartlyOffCanvas_DrawsTheVisiblePart()
    {
        var tmp = Png(40, 40, SKColors.Red);
        using var canvas = RenderSurface.New(100, 100);

        DocumentRenderer.RenderImageLayer(canvas, Layer(tmp, 0.4f, 0.4f, l => l.X = 0f));

        Assert.Equal(new Rgba(255, 0, 0, 255), TestPixels.Px(canvas, 0, 50));
        Assert.Equal(new Rgba(255, 0, 0, 255), TestPixels.Px(canvas, 19, 50));
        Assert.Equal(0, TestPixels.Px(canvas, 20, 50).A);
    }

    /// <summary>A logo is resized to the layer, enlarging or shrinking: a 10x10 and a 400x400 source fill the same 50 px box.</summary>
    [Theory]
    [InlineData(10)]
    [InlineData(400)]
    public void RenderImageLayer_ResizesTheSourceToTheLayer(int sourceSide)
    {
        var tmp = Png(sourceSide, sourceSide, SKColors.Blue);
        using var canvas = RenderSurface.New(200, 200);

        DocumentRenderer.RenderImageLayer(canvas, Layer(tmp, 0.25f, 0.25f));

        Assert.Equal(new Rgba(0, 0, 255, 255), TestPixels.Px(canvas, 76, 100));
        Assert.Equal(new Rgba(0, 0, 255, 255), TestPixels.Px(canvas, 123, 100));
        Assert.Equal(0, TestPixels.Px(canvas, 73, 100).A);
        Assert.Equal(0, TestPixels.Px(canvas, 126, 100).A);
    }

    /// <summary>
    /// End-to-end: the compositor must dispatch Type=="image" layers, honour Visible,
    /// and keep draw order (layers render in array order, last on top), whichever colour is last.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComposeDocumentFrame_DrawsImageLayers_InOrder_SkippingHidden(bool blueLast)
    {
        var red = Png(20, 20, SKColors.Red);
        var blue = Png(20, 20, SKColors.Blue);
        var green = Png(20, 20, new SKColor(0, 255, 0));

        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 100, Height = 100 } };
        doc.Background.DimColor = "#000000";
        doc.Background.Dim = 0f;
        doc.Layers.Add(Layer(green, 0.5f, 0.5f, l => l.Visible = false));
        doc.Layers.Add(Layer(blueLast ? red : blue, 0.5f, 0.5f));
        doc.Layers.Add(Layer(blueLast ? blue : red, 0.5f, 0.5f));
        doc.Layers.Add(Layer(green, 0.5f, 0.5f, l => l.Visible = false));

        using var canvas = RenderSurface.New(100, 100);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        Assert.Equal(blueLast ? new Rgba(0, 0, 255, 255) : new Rgba(255, 0, 0, 255), TestPixels.Px(canvas, 50, 50));
    }

    /// <summary>
    /// A logo that is missing or not an image skips just that layer, with a warning; the background
    /// and the other layers still render.
    /// </summary>
    [Theory]
    [InlineData("garbage")]
    [InlineData("truncated")]
    [InlineData("missing")]
    public void ComposeDocumentFrame_BadLogo_SkipsTheLayerLogsAndRendersTheRest(string kind)
    {
        var good = Png(20, 20, SKColors.Blue);
        var bad = Path.Combine(Path.GetTempPath(), $"logo_{Guid.NewGuid():N}.png");
        _files.Add(bad);
        if (kind == "garbage")
        {
            File.WriteAllText(bad, "this is not an image");
        }
        else if (kind == "truncated")
        {
            var bytes = File.ReadAllBytes(Png(64, 64, SKColors.Red));
            File.WriteAllBytes(bad, bytes.Take(40).ToArray());
        }

        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 100, Height = 100 } };
        doc.Background.Source = BackgroundSources.Solid;
        doc.Background.DimColor = "#00ff00";
        doc.Layers.Add(Layer(bad, 0.8f, 0.8f));
        doc.Layers.Add(Layer(good, 0.2f, 0.2f));

        var log = new CapturingLogger<ImageLayerRenderTests>();
        using var canvas = RenderSurface.New(100, 100);
        using (DocumentRenderer.CaptureLogForTests(log))
        {
            var ex = Record.Exception(() => DocumentRenderer.ComposeDocumentFrame(canvas, null, doc));
            Assert.Null(ex);
        }

        Assert.Equal(new Rgba(0, 255, 0, 255), TestPixels.Px(canvas, 20, 20)); // background, not the bad logo
        Assert.Equal(new Rgba(0, 0, 255, 255), TestPixels.Px(canvas, 50, 50)); // the next layer still drew
        var warning = Assert.Single(log.Entries, e => e.Message.Contains(Path.GetFileName(bad)));
        Assert.Equal(LogLevel.Warning, warning.Level);
    }

    /// <summary>Nested log captures restore the OUTER capture when the inner one ends, not nothing.</summary>
    [Fact]
    public void CaptureLogForTests_NestedScopes_RestoreTheOuterCapture()
    {
        var bad = Path.Combine(Path.GetTempPath(), $"logo_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(bad, new byte[] { 1, 2, 3 });
        _files.Add(bad);
        var outer = new CapturingLogger<ImageLayerRenderTests>();
        var inner = new CapturingLogger<ImageLayerRenderTests>();
        using var canvas = RenderSurface.New(20, 20);

        using (var outerScope = DocumentRenderer.CaptureLogForTests(outer))
        {
            using (var innerScope = DocumentRenderer.CaptureLogForTests(inner))
            {
                DocumentRenderer.RenderImageLayer(canvas, Layer(bad, 0.5f, 0.5f));
                Assert.Single(inner.Entries);
                Assert.Empty(outer.Entries);
                innerScope.Dispose(); // idempotent: a second dispose must not clobber the outer capture
            }

            DocumentRenderer.RenderImageLayer(canvas, Layer(bad, 0.5f, 0.5f));
            Assert.Single(inner.Entries);
            Assert.Single(outer.Entries);
        }

        DocumentRenderer.RenderImageLayer(canvas, Layer(bad, 0.5f, 0.5f));
        Assert.Single(inner.Entries);
        Assert.Single(outer.Entries);
    }

    /// <summary>
    /// A layer is clamped to 1.5× the canvas before it becomes a draw. 4× (the old cap) on a 2048²
    /// canvas was an 8192² buffer that Rotate then grew further, ~0.5-1 GB per layer.
    /// </summary>
    [Fact]
    public void ImageLayerPixelSize_ClampsToOneAndAHalfCanvases()
    {
        var size = DocumentRenderer.ImageLayerPixelSize(new CoverLayer { Width = 4f, Height = 10f }, 200, 100);
        Assert.Equal(300, size.Width);
        Assert.Equal(150, size.Height);
    }

    [Fact]
    public void ImageLayerPixelSize_NeverBelowOnePixel()
    {
        var size = DocumentRenderer.ImageLayerPixelSize(new CoverLayer { Width = 0f, Height = -2f }, 200, 100);
        Assert.Equal(1, size.Width);
        Assert.Equal(1, size.Height);
    }
}
