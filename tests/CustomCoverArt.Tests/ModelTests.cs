using CustomCoverArt.Models;
using Xunit;

namespace CustomCoverArt.Tests;

public class ModelTests
{
    [Fact]
    public void CoverArtSettings_HasCollageAndAnimationDefaults()
    {
        var s = new CoverArtSettings();
        Assert.Equal("upload", s.BackgroundSource);
        Assert.Null(s.Collage);
        Assert.Null(s.Animation);
    }

    [Fact]
    public void CollageSettings_DefaultsAreSafe()
    {
        var c = new CollageSettings();
        Assert.Equal("medium", c.Density);
        Assert.Equal("library", c.SourceType);
        Assert.Equal(string.Empty, c.SourceId);
    }

    [Fact]
    public void AnimationSettings_DefaultsAreBounded()
    {
        var a = new AnimationSettings();
        Assert.False(a.Enabled);
        Assert.Equal(20, a.FrameCount);
        Assert.True(a.Loop);
    }

    [Fact]
    public void SavedTemplate_HoldsNameAndSettings()
    {
        var t = new SavedTemplate { Name = "Neon", Settings = new CoverArtSettings { TextSize = 200 } };
        Assert.Equal("Neon", t.Name);
        Assert.Equal(200, t.Settings.TextSize);
    }
}
