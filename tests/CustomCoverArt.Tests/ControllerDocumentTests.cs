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

    /// <summary>
    /// Batching must carry the WHOLE design. The flat settings model holds one title, so
    /// if batch apply fell back to it every extra text layer and logo would vanish from
    /// the applied covers with no error shown.
    /// </summary>
    [Fact]
    public void BuildBatchDocument_KeepsEveryLayer_AndRetitlesOnlyTheTitleLayer()
    {
        var doc = new CoverDocument();
        doc.Layers.Add(new CoverLayer { Id = "title", Type = "text", Content = "Old name" });
        doc.Layers.Add(new CoverLayer { Id = "tagline", Type = "text", Content = "Keep me" });
        doc.Layers.Add(new CoverLayer { Id = "logo", Type = "image", ImagePath = "/data/customcoverart/uploads/l.png" });

        var built = CustomCoverArtController.BuildBatchDocument(doc, "Documentaries", "11111111-1111-1111-1111-111111111111");

        Assert.Equal(3, built.Layers.Count);
        Assert.Equal("Documentaries", built.Layers.First(l => l.Id == "title").Content);
        Assert.Equal("Keep me", built.Layers.First(l => l.Id == "tagline").Content);
        Assert.Equal("/data/customcoverart/uploads/l.png", built.Layers.First(l => l.Id == "logo").ImagePath);
        // Deep clone: retitling one target must not bleed into the next.
        Assert.Equal("Old name", doc.Layers.First(l => l.Id == "title").Content);
    }

    /// <summary>A design built entirely from new layers has no "title" id to look for.</summary>
    [Fact]
    public void BuildBatchDocument_NoTitleId_UsesTheBottomMostTextLayer()
    {
        var doc = new CoverDocument();
        doc.Layers.Add(new CoverLayer { Id = "labc", Type = "text", Content = "first" });
        doc.Layers.Add(new CoverLayer { Id = "ldef", Type = "text", Content = "second" });

        var built = CustomCoverArtController.BuildBatchDocument(doc, "Anime", "22222222-2222-2222-2222-222222222222");

        Assert.Equal("Anime", built.Layers[0].Content);
        Assert.Equal("second", built.Layers[1].Content);
    }

    [Fact]
    public void BuildBatchDocument_Collage_RepointsAtTheTarget()
    {
        var doc = new CoverDocument();
        doc.Background.Source = BackgroundSources.Collage;
        doc.Background.Collage = new CollageSettings { SourceId = string.Empty };

        var built = CustomCoverArtController.BuildBatchDocument(doc, "Shows", "33333333-3333-3333-3333-333333333333");

        Assert.Equal("33333333-3333-3333-3333-333333333333", built.Background.Collage!.SourceId);
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
    public void NormalizeTemplate_DocumentWithNullLayers_DoesNotThrow()
    {
        // Mirrors a real client payload: {"name":"x","document":{"layers":null}} (ASP.NET
        // Core's MVC JSON options are case-insensitive; PascalCase is used here since a bare
        // JsonSerializer.Deserialize call, unlike the real controller pipeline, is
        // case-sensitive by default). Deserializing through System.Text.Json (rather than a
        // C# object initializer) is what actually reproduces the malformed body a client can
        // send, since STJ does not enforce non-null on non-nullable reference types.
        var json = "{\"Name\":\"x\",\"Document\":{\"Layers\":null}}";
        var template = System.Text.Json.JsonSerializer.Deserialize<SavedTemplate>(json);
        Assert.NotNull(template);
        Assert.Null(template!.Document!.Layers);

        var exception = Record.Exception(() => CustomCoverArtController.NormalizeTemplate(template));

        Assert.Null(exception);
        Assert.NotNull(template.Document!.Layers);
        Assert.Empty(template.Document.Layers);
    }

    [Fact]
    public void NormalizeTemplate_DocumentWithNullBackground_DoesNotThrowAndStripsTitleWhenPresent()
    {
        // Mirrors a real client payload: {"name":"x","document":{"background":null,"layers":[...]}}.
        var json = "{\"Name\":\"x\",\"Document\":{\"Background\":null,\"Layers\":[" +
                   "{\"Id\":\"title\",\"Type\":\"text\",\"Content\":\"My Movies\"}]}}";
        var template = System.Text.Json.JsonSerializer.Deserialize<SavedTemplate>(json);
        Assert.NotNull(template);
        Assert.Null(template!.Document!.Background);

        var exception = Record.Exception(() => CustomCoverArtController.NormalizeTemplate(template));

        Assert.Null(exception);
        Assert.NotNull(template.Document!.Background);
        Assert.Equal(string.Empty, template.Document.Layers.First(l => l.Id == "title").Content);
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
