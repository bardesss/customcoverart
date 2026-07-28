using CustomCoverArt.Models;
using CustomCoverArt.Services;
using Xunit;

namespace CustomCoverArt.Tests;

public class DocumentMigrationTests
{
    [Fact]
    public void FromSettings_MapsCanvasAndBackground()
    {
        var s = new CoverArtSettings
        {
            Title = "Movies", ExportWidth = 1280, ExportHeight = 720,
            OutputFormat = "png", DimensionPreset = "landscape",
            BackgroundDim = 0.4f, BackgroundBlur = 3f, DimColor = "#101010",
            BackgroundFit = "contain", BackgroundImagePath = ""
        };

        var d = DocumentMigration.FromSettings(s);

        Assert.Equal(1280, d.Canvas.Width);
        Assert.Equal(720, d.Canvas.Height);
        Assert.Equal("landscape", d.Canvas.DimensionPreset);
        Assert.Equal(0.4f, d.Background.Dim);
        Assert.Equal("contain", d.Background.Fit);
        Assert.Equal(1f, d.Background.Transform.Scale);
    }

    [Fact]
    public void FromSettings_CreatesExactlyOneTextLayer()
    {
        var s = new CoverArtSettings { Title = "Movies", TextSize = 120, ExportHeight = 1400, TextColor = "#ffcc00" };
        var d = DocumentMigration.FromSettings(s);

        Assert.Single(d.Layers);
        var layer = d.Layers[0];
        Assert.Equal("text", layer.Type);
        Assert.Equal("Movies", layer.Content);
        Assert.Equal("#ffcc00", layer.Color);
        // 120px on a 1400px-tall canvas => ~0.0857 fraction.
        Assert.InRange(layer.Size, 0.084f, 0.087f);
    }

    [Fact]
    public void FromSettings_LeftAlignMapsToLeftAnchor()
    {
        var s = new CoverArtSettings { Title = "X", TextAlign = TextAlign.Left, TextPadding = 0.05f };
        var d = DocumentMigration.FromSettings(s);
        Assert.Equal(TextAlign.Left, d.Layers[0].Align);
        Assert.InRange(d.Layers[0].X, 0.04f, 0.06f); // near the left padding
    }

    // Pins the null-passthrough contract the client-side migrateTemplateToDocument
    // (configPage.html) mirrors: a legacy CoverArtSettings with no gradient must
    // migrate to Background.Gradient == null, NOT the default purple/blue
    // gradient. Falling back to a default here would put a phantom gradient
    // (IsEnabled:true) on every plain-image/flat-colour legacy design.
    [Fact]
    public void FromSettings_NullGradientStaysNull()
    {
        var s = new CoverArtSettings { Title = "X", BackgroundGradient = null };
        var d = DocumentMigration.FromSettings(s);
        Assert.Null(d.Background.Gradient);
    }

    [Fact]
    public void FromSettings_NonNullGradientPassesThroughUnchanged()
    {
        var gradient = new GradientSettings { IsEnabled = true, Type = GradientType.Radial, Angle = 45f };
        var s = new CoverArtSettings { Title = "X", BackgroundGradient = gradient };

        var d = DocumentMigration.FromSettings(s);

        Assert.Same(gradient, d.Background.Gradient);
        Assert.True(d.Background.Gradient!.IsEnabled);
        Assert.Equal(GradientType.Radial, d.Background.Gradient.Type);
        Assert.Equal(45f, d.Background.Gradient.Angle);
    }
}
