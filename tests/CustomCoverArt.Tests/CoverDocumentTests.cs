using CustomCoverArt.Models;
using Xunit;

namespace CustomCoverArt.Tests;

public class CoverDocumentTests
{
    [Fact]
    public void CoverDocument_HasSafeDefaults()
    {
        var d = new CoverDocument();
        Assert.Equal(2, d.Version);
        Assert.Equal(1400, d.Canvas.Width);
        Assert.Equal(1400, d.Canvas.Height);
        Assert.Equal("upload", d.Background.Source);
        Assert.Equal(1f, d.Background.Transform.Scale);
        Assert.Empty(d.Layers);
        Assert.NotNull(d.Effects);
    }

    [Fact]
    public void CoverLayer_TextDefaults()
    {
        var l = new CoverLayer();
        Assert.Equal("text", l.Type);
        Assert.True(l.Visible);
        Assert.Equal(0.5f, l.X);
        Assert.Equal(0.5f, l.Y);
        Assert.Equal(1f, l.Opacity);
    }
}
