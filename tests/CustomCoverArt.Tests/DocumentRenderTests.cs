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
}
