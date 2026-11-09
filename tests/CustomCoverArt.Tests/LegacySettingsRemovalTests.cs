using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CustomCoverArt.Controllers;
using CustomCoverArt.Models;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// The flat CoverArtSettings model predates the layered CoverDocument. Client and server
/// ship in the same DLL, so the client never talks to an older server: every request now
/// carries only the document. Stored templates saved before the canvas editor (Settings
/// only) are still honoured, by migrating them to a document.
/// </summary>
public class LegacySettingsRemovalTests
{
    [Fact]
    public void Client_SendsOnlyTheLayeredDocument()
    {
        var js = ConfigPageSource.Js();
        Assert.DoesNotContain("collectSettings", js, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"[{,]\s*Settings\s*:", js);
        Assert.DoesNotContain("backgroundImagePath", js, StringComparison.Ordinal);
        Assert.DoesNotContain("customFontPath", js, StringComparison.Ordinal);
        Assert.DoesNotContain("function collectGradientStops(", js, StringComparison.Ordinal);
        Assert.Contains("{ Document: batchDoc, Targets: targets }", js, StringComparison.Ordinal);
    }

    [Fact]
    public void Client_StillMigratesSettingsOnlyTemplates()
    {
        Assert.Contains("function migrateTemplateToDocument(", ConfigPageSource.Js(), StringComparison.Ordinal);
    }

    [Fact]
    public void BatchApplyRequest_CarriesNoFlatSettings()
    {
        Assert.Null(typeof(BatchApplyRequest).GetProperty("Settings"));
    }

    [Fact]
    public void NoEndpointAcceptsTheFlatSettingsModel()
    {
        var actions = typeof(CustomCoverArtController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

        var flat = actions
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(CoverArtSettings)))
            .Select(m => m.Name)
            .ToList();
        Assert.True(flat.Count == 0, "Actions still taking CoverArtSettings: " + string.Join(", ", flat));

        var routes = actions.SelectMany(m => m.GetCustomAttributes<HttpPostAttribute>()).Select(a => a.Template).ToList();
        Assert.DoesNotContain("apply", routes);
        Assert.DoesNotContain("preview", routes);
        Assert.Contains("document/apply", routes);
        Assert.Contains("document/preview", routes);

        Assert.Null(typeof(CoverArtSettings).Assembly.GetType("CustomCoverArt.Models.ApplyCoverArtRequest"));
        Assert.Null(typeof(CustomCoverArtController).GetMethod("BuildBatchSettings"));
    }

    [Fact]
    public void TemplateDocument_UsesTheLayeredDesignWhenThereIsOne()
    {
        var layered = new CoverDocument();
        var tpl = new SavedTemplate { Name = "T", Document = layered, Settings = new CoverArtSettings { Title = "ignored" } };

        Assert.Same(layered, CustomCoverArtController.TemplateDocument(tpl));
    }

    /// <summary>
    /// A template saved before the canvas editor has only flat Settings. Batch apply used
    /// to render it through BuildBatchSettings; it is now migrated to a document first,
    /// and must still be retitled per target and have its collage re-pointed.
    /// </summary>
    [Fact]
    public void TemplateDocument_MigratesASettingsOnlyTemplate_SoBatchApplyStillRetitlesIt()
    {
        var tpl = new SavedTemplate
        {
            Name = "Old",
            Settings = new CoverArtSettings
            {
                Title = "",
                TextColor = "#ffcc00",
                BackgroundSource = "collage",
                Collage = new CollageSettings { SourceId = "", Density = "medium" }
            }
        };

        var doc = CustomCoverArtController.TemplateDocument(tpl);
        var built = CustomCoverArtController.BuildBatchDocument(doc, "Kids", "target-9");

        var title = Assert.Single(built.Layers, l => l.Id == "title");
        Assert.Equal("Kids", title.Content);
        Assert.Equal("#ffcc00", title.Color);
        Assert.Equal("collage", built.Background.Source);
        Assert.Equal("target-9", built.Background.Collage!.SourceId);
        Assert.Equal("medium", built.Background.Collage!.Density);
        Assert.Equal("", tpl.Settings.Title); // the stored template is untouched
    }

    [Fact]
    public async Task BatchApply_WithNeitherTemplateNorDocument_SaysSo()
    {
        var controller = ControllerHost.New();

        var res = await controller.BatchApply(new BatchApplyRequest
        {
            Targets = { new BatchTargetRef { Id = "11111111-1111-1111-1111-111111111111" } }
        });

        Assert.False(res.Success);
        Assert.Equal("No template or document provided.", res.ErrorMessage);
    }
}
