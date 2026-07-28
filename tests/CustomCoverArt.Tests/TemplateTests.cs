using CustomCoverArt.Controllers;
using CustomCoverArt.Models;
using Xunit;

namespace CustomCoverArt.Tests;

public class TemplateTests
{
    [Fact]
    public void NormalizeTemplate_StripsTitleAndTargetSpecificFields()
    {
        var t = new SavedTemplate
        {
            Name = "  Neon  ",
            Settings = new CoverArtSettings
            {
                Title = "Movies",
                TextSize = 180,
                BackgroundSource = "collage",
                Collage = new CollageSettings { SourceId = "abc-123", Density = "dense" }
            }
        };

        var n = CustomCoverArtController.NormalizeTemplate(t);

        Assert.Equal("Neon", n.Name);                 // trimmed
        Assert.Equal(string.Empty, n.Settings.Title); // title stripped
        Assert.Equal(180, n.Settings.TextSize);       // design kept
        Assert.Equal("collage", n.Settings.BackgroundSource);
        // Collage SourceId is target-specific → cleared; density (a design choice) kept.
        Assert.Equal(string.Empty, n.Settings.Collage!.SourceId);
        Assert.Equal("dense", n.Settings.Collage!.Density);
    }

    [Fact]
    public void BuildBatchSettings_SetsTitleAndCollageSource()
    {
        var baseSettings = new CoverArtSettings
        {
            Title = "",
            BackgroundSource = "collage",
            Collage = new CollageSettings { SourceId = "", Density = "medium" }
        };

        var built = CustomCoverArtController.BuildBatchSettings(baseSettings, "Kids", "target-9");

        Assert.Equal("Kids", built.Title);
        Assert.Equal("target-9", built.Collage!.SourceId);
        // Original is not mutated (clone).
        Assert.Equal("", baseSettings.Title);
    }

    [Fact]
    public void BuildBatchSettings_NonCollageLeavesCollageNull()
    {
        var baseSettings = new CoverArtSettings { BackgroundSource = "upload" };
        var built = CustomCoverArtController.BuildBatchSettings(baseSettings, "Movies", "id-1");
        Assert.Equal("Movies", built.Title);
        Assert.Null(built.Collage);
    }
}
