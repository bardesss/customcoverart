using CustomCoverArt.Models;
using CustomCoverArt.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CustomCoverArt.Tests;

public class DocumentRenderTests
{
    [Fact]
    public void ComposeDocumentFrame_GradientBackground_DrawsTextPixels()
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 200, Height = 200 } };
        doc.Background.Gradient = new GradientSettings
        {
            IsEnabled = true,
            Stops = new() { new GradientStop { Color = "#000000", Position = 0 }, new GradientStop { Color = "#000000", Position = 1 } }
        };
        doc.Layers.Add(new CoverLayer { Type = "text", Content = "HELLO", Color = "#ffffff", Size = 0.2f, X = 0.5f, Y = 0.5f });

        using var canvas = new Image<Rgba32>(200, 200);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        // At least one near-white pixel from the text exists over the black gradient.
        var found = false;
        for (int y = 0; y < 200 && !found; y++)
            for (int x = 0; x < 200; x++)
            {
                var p = canvas[x, y];
                if (p.R > 200 && p.G > 200 && p.B > 200) { found = true; break; }
            }
        Assert.True(found, "Expected white text pixels over the black background.");
    }

    [Fact]
    public void ComposeDocumentFrame_HiddenLayer_IsNotDrawn()
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 100, Height = 100 } };
        doc.Background.DimColor = "#000000";
        doc.Layers.Add(new CoverLayer { Type = "text", Content = "X", Color = "#ffffff", Size = 0.5f, Visible = false });

        using var canvas = new Image<Rgba32>(100, 100);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        for (int y = 0; y < 100; y++)
            for (int x = 0; x < 100; x++)
                Assert.True(canvas[x, y].R < 40, "Hidden layer must not render.");
    }

    /// <summary>
    /// A 100x300 background banded red/green/blue drawn into a 100x100 canvas with
    /// "cover": the fit crops away two thirds of it, and OffsetY chooses WHICH third
    /// is shown — at the default Scale=1, with no zoom required. Panning used to be a
    /// no-op here because the slack came from zoom alone, so the render was always green.
    /// </summary>
    [Theory]
    [InlineData(0f, 0, 255, 0)]    // identity -> the centre band (plain centre-crop)
    [InlineData(-1f, 255, 0, 0)]   // panned to the top    -> red
    [InlineData(1f, 0, 0, 255)]    // panned to the bottom -> blue
    public void ComposeDocumentFrame_CoverFit_OffsetYChoosesBandAtScale1(float offsetY, byte r, byte g, byte b)
    {
        using var background = new Image<Rgba32>(100, 300);
        for (var y = 0; y < 300; y++)
        {
            var band = y < 100 ? new Rgba32(255, 0, 0) : y < 200 ? new Rgba32(0, 255, 0) : new Rgba32(0, 0, 255);
            for (var x = 0; x < 100; x++)
            {
                background[x, y] = band;
            }
        }

        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 100, Height = 100 } };
        doc.Background.Fit = "cover";
        doc.Background.Dim = 0f;
        doc.Background.Transform = new BackgroundTransform { Scale = 1f, OffsetY = offsetY };

        using var canvas = new Image<Rgba32>(100, 100);
        DocumentRenderer.ComposeDocumentFrame(canvas, background, doc);

        var centre = canvas[50, 50];
        Assert.Equal(r, centre.R);
        Assert.Equal(g, centre.G);
        Assert.Equal(b, centre.B);
    }

    [Fact]
    public async System.Threading.Tasks.Task GenerateFromDocumentAsync_ProducesPng()
    {
        var svc = AnimationTestHost.NewCoverArtService();
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 200, Height = 200, Format = "png" } };
        doc.Background.DimColor = "#222222";
        doc.Layers.Add(new CoverLayer { Type = "text", Content = "Hi", Color = "#ffffff", Size = 0.2f });

        var path = await svc.GenerateFromDocumentAsync(doc);

        Assert.True(System.IO.File.Exists(path));
        Assert.EndsWith(".png", path);
        using var img = SixLabors.ImageSharp.Image.Load(path);
        Assert.Equal(200, img.Width);
        try { System.IO.File.Delete(path); } catch { }
    }

    /// <summary>
    /// Direct, deterministic guard for the doc-vs-buffer height invariant in
    /// RenderTextLayer: font pixel size MUST be computed as
    /// `layer.Size * doc.Canvas.Height` (the document's declared reference
    /// resolution), never from the actual pixel buffer's height. Unlike the
    /// animated-GIF downscale regression test (which now always has
    /// canvas.Height == doc.Canvas.Height, since ScaleDocumentForCanvas points
    /// the cloned document's own Canvas at the capped working size), this test
    /// composes the SAME document onto two buffers of DIFFERENT heights, so it
    /// can catch a doc.Canvas.Height → canvas.Height swap on that line.
    ///
    /// doc.Canvas.Height (500) and layer.Size (0.2) never change between the
    /// two renders, so the target font size (~100px) is fixed — IF font sizing
    /// correctly keys off doc.Canvas.Height. Both buffers (500 and 300) are
    /// comfortably larger than that ~100px glyph, so neither render clips it;
    /// the measured absolute glyph pixel height should therefore be nearly
    /// identical across both buffers. A regression that keyed font size off
    /// the buffer's own height instead would size the second render's glyph at
    /// ~0.2*300=60px — a ~40% shrink versus the first render's ~100px — which
    /// the ratio assertion below catches.
    /// </summary>
    [Fact]
    public void ComposeDocumentFrame_BufferHeightDiffersFromCanvas_FontSizeFollowsDocCanvasHeight()
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 500, Height = 500 } };
        doc.Background.DimColor = "#000000";
        doc.Layers.Add(new CoverLayer { Type = "text", Content = "H", Color = "#ffffff", Size = 0.2f, X = 0.5f, Y = 0.5f });

        using var bufferMatchingCanvas = new Image<Rgba32>(500, 500);
        DocumentRenderer.ComposeDocumentFrame(bufferMatchingCanvas, null, doc);
        var heightAtMatchingBuffer = MeasureGlyphPixelHeight(bufferMatchingCanvas);

        using var smallerBuffer = new Image<Rgba32>(300, 300);
        DocumentRenderer.ComposeDocumentFrame(smallerBuffer, null, doc);
        var heightAtSmallerBuffer = MeasureGlyphPixelHeight(smallerBuffer);

        var ratio = heightAtSmallerBuffer / (float)heightAtMatchingBuffer;
        Assert.InRange(ratio, 0.85f, 1.15f);
    }

    /// <summary>Bounding-box height of near-white pixels, in absolute pixels.</summary>
    private static int MeasureGlyphPixelHeight(Image<Rgba32> canvas)
    {
        int minY = int.MaxValue, maxY = int.MinValue;
        for (int y = 0; y < canvas.Height; y++)
            for (int x = 0; x < canvas.Width; x++)
            {
                var p = canvas[x, y];
                if (p.R > 200 && p.G > 200 && p.B > 200)
                {
                    if (y < minY) { minY = y; }
                    if (y > maxY) { maxY = y; }
                }
            }

        Assert.True(minY <= maxY, "Expected to find rendered text pixels.");
        return maxY - minY + 1;
    }
}
