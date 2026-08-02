using CustomCoverArt.Models;
using CustomCoverArt.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// Before v3.3.0.0 the background was described by two overlapping fields: a Source
/// dropdown and a separate Gradient.IsEnabled checkbox. These pin the collapse into a
/// single Source value, and the back-compat behaviour for documents that predate it.
/// </summary>
public class BackgroundSourceTests
{
    private static BackgroundLayer Bg(string source, string imagePath = "", bool? gradientEnabled = null)
    {
        var bg = new BackgroundLayer { Source = source, ImagePath = imagePath };
        bg.Gradient = gradientEnabled is null ? null : new GradientSettings { IsEnabled = gradientEnabled.Value };
        return bg;
    }

    [Theory]
    [InlineData("collage", "", null, "collage")]                 // collage is untouched
    [InlineData("collage", "/x/y.png", true, "collage")]          // ...even with an image set
    [InlineData("upload", "/x/y.png", null, "upload")]            // an image wins
    [InlineData("poster", "/x/y.png", true, "upload")]            // legacy "poster" with an image
    [InlineData("upload", "", true, "gradient")]                  // no image, gradient on
    [InlineData("none", "", true, "gradient")]
    [InlineData("upload", "", false, "solid")]                    // no image, gradient off
    [InlineData("none", "", null, "solid")]
    [InlineData("", "", null, "solid")]
    [InlineData("poster", "", null, "solid")]
    public void NormalizeBackgroundSource_FollowsTheMigrationTable(
        string source, string imagePath, bool? gradientEnabled, string expected)
    {
        var bg = Bg(source, imagePath, gradientEnabled);
        DocumentMigration.NormalizeBackgroundSource(bg);
        Assert.Equal(expected, bg.Source);
    }

    /// <summary>Running it twice must not move an already-migrated document.</summary>
    [Fact]
    public void NormalizeBackgroundSource_IsIdempotent()
    {
        var bg = Bg("none", string.Empty, true);
        DocumentMigration.NormalizeBackgroundSource(bg);
        var once = bg.Source;
        DocumentMigration.NormalizeBackgroundSource(bg);
        Assert.Equal(once, bg.Source);
    }

    [Fact]
    public void Normalize_AppliesTheBackgroundSourceMigration()
    {
        var doc = new CoverDocument();
        doc.Background.Source = "none";
        doc.Background.Gradient = new GradientSettings { IsEnabled = true };

        DocumentMigration.Normalize(doc);

        Assert.Equal(BackgroundSources.Gradient, doc.Background.Source);
    }

    /// <summary>
    /// Source "solid" must fill with the base colour even when a stale Gradient.IsEnabled
    /// is left over from before the migration, or switching to Solid would do nothing.
    /// </summary>
    [Fact]
    public void ComposeDocumentFrame_SolidSource_FillsBaseColour_IgnoringStaleGradientFlag()
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 40, Height = 40 } };
        doc.Background.Source = BackgroundSources.Solid;
        doc.Background.DimColor = "#ff0000";
        doc.Background.Gradient = new GradientSettings
        {
            IsEnabled = true,
            Stops = new() { new GradientStop { Color = "#0000ff", Position = 0 },
                            new GradientStop { Color = "#0000ff", Position = 1 } }
        };

        using var canvas = new Image<Rgba32>(40, 40);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        Assert.True(canvas[20, 20].R > 200 && canvas[20, 20].B < 60, "Solid must win over a stale gradient flag.");
    }

    /// <summary>Back-compat: a document that never migrated still renders its gradient.</summary>
    [Fact]
    public void ComposeDocumentFrame_LegacyUploadSourceWithGradient_StillDrawsGradient()
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 40, Height = 40 } };
        doc.Background.Source = BackgroundSources.Upload;   // legacy shape, no ImagePath
        doc.Background.DimColor = "#000000";
        doc.Background.Gradient = new GradientSettings
        {
            IsEnabled = true,
            Stops = new() { new GradientStop { Color = "#00ff00", Position = 0 },
                            new GradientStop { Color = "#00ff00", Position = 1 } }
        };

        using var canvas = new Image<Rgba32>(40, 40);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        Assert.True(canvas[20, 20].G > 200, "A pre-migration document must keep rendering its gradient.");
    }

    /// <summary>Source "gradient" draws the gradient even if IsEnabled was never set.</summary>
    [Fact]
    public void ComposeDocumentFrame_GradientSource_DrawsGradientWithoutTheLegacyFlag()
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 40, Height = 40 } };
        doc.Background.Source = BackgroundSources.Gradient;
        doc.Background.DimColor = "#000000";
        doc.Background.Gradient = new GradientSettings
        {
            IsEnabled = false,   // deliberately stale/false
            Stops = new() { new GradientStop { Color = "#00ff00", Position = 0 },
                            new GradientStop { Color = "#00ff00", Position = 1 } }
        };

        using var canvas = new Image<Rgba32>(40, 40);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        Assert.True(canvas[20, 20].G > 200, "Source is authoritative once migrated.");
    }
}
