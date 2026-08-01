using CustomCoverArt.Models;
using CustomCoverArt.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CustomCoverArt.Tests;

public class ImageLayerRenderTests
{
    [Fact]
    public void RenderImageLayer_DrawsLogoPixelsAtCenter()
    {
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"logo_{System.Guid.NewGuid():N}.png");
        using (var logo = new Image<Rgba32>(50, 50, Color.Red)) { logo.SaveAsPng(tmp); }
        try
        {
            using var canvas = new Image<Rgba32>(200, 200); // transparent
            var layer = new CoverLayer { Type = "image", ImagePath = tmp, X = 0.5f, Y = 0.5f, Width = 0.25f, Height = 0.25f, Opacity = 1f };
            DocumentRenderer.RenderImageLayer(canvas, layer);

            var center = canvas[100, 100];
            Assert.True(center.R > 200 && center.A > 200, "Logo should be drawn opaque red at the center.");
            Assert.Equal(0, canvas[5, 5].A); // corners stay transparent
        }
        finally { System.IO.File.Delete(tmp); }
    }

    [Fact]
    public void RenderImageLayer_MissingFile_DoesNotThrow()
    {
        using var canvas = new Image<Rgba32>(100, 100);
        var layer = new CoverLayer { Type = "image", ImagePath = "/no/such/file.png", X = 0.5f, Y = 0.5f, Width = 0.5f, Height = 0.5f };
        DocumentRenderer.RenderImageLayer(canvas, layer); // must be a no-op, not throw
        Assert.Equal(0, canvas[50, 50].A);
    }

    /// <summary>
    /// Opacity must fade the layer rather than being ignored: half-opaque red over a
    /// transparent canvas lands at alpha ~128, not 255.
    /// </summary>
    [Fact]
    public void RenderImageLayer_HalfOpacity_BlendsAtHalfAlpha()
    {
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"logo_{System.Guid.NewGuid():N}.png");
        using (var logo = new Image<Rgba32>(40, 40, Color.Red)) { logo.SaveAsPng(tmp); }
        try
        {
            using var canvas = new Image<Rgba32>(100, 100);
            var layer = new CoverLayer { Type = "image", ImagePath = tmp, X = 0.5f, Y = 0.5f, Width = 0.4f, Height = 0.4f, Opacity = 0.5f };
            DocumentRenderer.RenderImageLayer(canvas, layer);

            var center = canvas[50, 50];
            Assert.InRange(center.A, 100, 155);
        }
        finally { System.IO.File.Delete(tmp); }
    }

    /// <summary>
    /// A rotated logo stays centred on its anchor: rotation grows the bounding box, so
    /// the top-left has to be recomputed from the ROTATED size, not the pre-rotation one.
    /// A 45°-rotated square leaves its new bounding-box corners transparent while the
    /// centre stays covered — that asymmetry is what pins the centring down.
    /// </summary>
    [Fact]
    public void RenderImageLayer_Rotated_StaysCenteredOnAnchor()
    {
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"logo_{System.Guid.NewGuid():N}.png");
        using (var logo = new Image<Rgba32>(64, 64, Color.Red)) { logo.SaveAsPng(tmp); }
        try
        {
            using var canvas = new Image<Rgba32>(200, 200);
            var layer = new CoverLayer
            {
                Type = "image", ImagePath = tmp,
                X = 0.5f, Y = 0.5f, Width = 0.5f, Height = 0.5f,
                Rotation = 45f, Opacity = 1f
            };
            DocumentRenderer.RenderImageLayer(canvas, layer);

            Assert.True(canvas[100, 100].A > 200, "Rotated logo must still cover its anchor point.");
            // 100px square rotated 45° => a diamond of half-diagonal ~70px: the point
            // 60px straight up from the centre is inside, 60px diagonally out is not.
            Assert.True(canvas[100, 40].A > 200, "Diamond tip should cover straight-up 60px.");
            Assert.Equal(0, canvas[142, 142].A);
        }
        finally { System.IO.File.Delete(tmp); }
    }

    /// <summary>
    /// End-to-end: the compositor must dispatch Type=="image" layers, honour Visible,
    /// and keep draw order (layers render in array order, last on top).
    /// </summary>
    [Fact]
    public void ComposeDocumentFrame_DrawsImageLayers_InOrder_SkippingHidden()
    {
        var red = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"logo_{System.Guid.NewGuid():N}.png");
        var blue = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"logo_{System.Guid.NewGuid():N}.png");
        var green = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"logo_{System.Guid.NewGuid():N}.png");
        using (var i = new Image<Rgba32>(20, 20, Color.Red)) { i.SaveAsPng(red); }
        using (var i = new Image<Rgba32>(20, 20, Color.Blue)) { i.SaveAsPng(blue); }
        using (var i = new Image<Rgba32>(20, 20, Color.Green)) { i.SaveAsPng(green); }
        try
        {
            var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 100, Height = 100 } };
            doc.Background.DimColor = "#000000";
            doc.Background.Dim = 0f;
            doc.Layers.Add(new CoverLayer { Type = "image", ImagePath = green, X = 0.5f, Y = 0.5f, Width = 0.5f, Height = 0.5f, Visible = false });
            doc.Layers.Add(new CoverLayer { Type = "image", ImagePath = red, X = 0.5f, Y = 0.5f, Width = 0.5f, Height = 0.5f });
            doc.Layers.Add(new CoverLayer { Type = "image", ImagePath = blue, X = 0.5f, Y = 0.5f, Width = 0.5f, Height = 0.5f });

            using var canvas = new Image<Rgba32>(100, 100);
            DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

            var center = canvas[50, 50];
            Assert.True(center.B > 200 && center.R < 60 && center.G < 60, "Last visible layer (blue) must be on top.");
        }
        finally
        {
            foreach (var p in new[] { red, blue, green }) { try { System.IO.File.Delete(p); } catch { } }
        }
    }
}
