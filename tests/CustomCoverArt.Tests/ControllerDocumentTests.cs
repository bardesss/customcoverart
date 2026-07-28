using System.Linq;
using CustomCoverArt.Controllers;
using CustomCoverArt.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CustomCoverArt.Tests;

public class ControllerDocumentTests
{
    [Fact]
    public void ApplyDocumentRequest_Defaults()
    {
        var r = new ApplyDocumentRequest();
        Assert.Equal(string.Empty, r.LibraryId);
        Assert.NotNull(r.Document);
    }

    [Fact]
    public void SavedTemplate_CanHoldDocument()
    {
        var t = new SavedTemplate { Name = "N", Document = new CoverDocument() };
        Assert.NotNull(t.Document);
        Assert.Equal(2, t.Document!.Version);
    }

    [Fact]
    public void NormalizeTemplate_WithDocument_StripsTitleLayerAndCollageSource()
    {
        var doc = new CoverDocument();
        doc.Layers.Add(new CoverLayer { Id = "title", Type = "text", Content = "My Movies" });
        doc.Layers.Add(new CoverLayer { Id = "subtitle", Type = "text", Content = "Keep me" });
        doc.Background.Collage = new CollageSettings { SourceId = "abc-123" };

        var template = new SavedTemplate { Name = "T", Document = doc };
        var normalized = CustomCoverArtController.NormalizeTemplate(template);

        Assert.Equal(string.Empty, normalized.Document!.Layers.First(l => l.Id == "title").Content);
        Assert.Equal("Keep me", normalized.Document.Layers.First(l => l.Id == "subtitle").Content);
        Assert.Equal(string.Empty, normalized.Document.Background.Collage!.SourceId);
    }

    [Fact]
    public void NormalizeTemplate_WithoutDocument_StillBlanksLegacySettings()
    {
        var template = new SavedTemplate { Name = "T", Settings = new CoverArtSettings { Title = "Movies" } };
        var normalized = CustomCoverArtController.NormalizeTemplate(template);

        Assert.Null(normalized.Document);
        Assert.Equal(string.Empty, normalized.Settings.Title);
    }

    [Fact]
    public async System.Threading.Tasks.Task GenerateFromDocumentAsync_NullBackgroundTransform_DoesNotThrow()
    {
        // A client can POST a partial/malformed CoverDocument (System.Text.Json does not
        // enforce non-null on non-nullable reference types), e.g. "transform": null. This
        // must degrade gracefully rather than 500 with an NRE in ApplyBackgroundTransform.
        // A background image must actually be loaded for that code path to run at all
        // (with no background image, rendering falls back to the gradient path, which
        // never touches Transform), so write a real image inside the sandboxed uploads dir.
        var (svc, dataDir) = AnimationTestHost.New();
        try
        {
            var uploads = System.IO.Path.Combine(dataDir, "customcoverart", "uploads");
            System.IO.Directory.CreateDirectory(uploads);
            var bgPath = System.IO.Path.Combine(uploads, "bg.png");
            using (var bg = new Image<Rgba32>(80, 80, Color.Green))
            {
                bg.SaveAsPng(bgPath);
            }

            var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 100, Height = 100, Format = "png" } };
            doc.Background.Source = "upload";
            doc.Background.ImagePath = bgPath;
            doc.Background.Transform = null!;
            doc.Layers.Add(new CoverLayer { Type = "text", Content = "Hi" });

            var path = await svc.GenerateFromDocumentAsync(doc);

            Assert.True(System.IO.File.Exists(path));
            try { System.IO.File.Delete(path); } catch { }
        }
        finally
        {
            try { System.IO.Directory.Delete(dataDir, true); } catch { }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task GenerateFromDocumentAsync_NullBackgroundAndLayers_DoesNotThrow()
    {
        var svc = AnimationTestHost.NewCoverArtService();
        var doc = new CoverDocument
        {
            Canvas = new CanvasSettings { Width = 100, Height = 100, Format = "png" },
            Background = null!,
            Layers = null!,
        };

        var path = await svc.GenerateFromDocumentAsync(doc);

        Assert.True(System.IO.File.Exists(path));
        try { System.IO.File.Delete(path); } catch { }
    }
}
