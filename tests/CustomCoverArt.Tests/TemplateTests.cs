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
}
