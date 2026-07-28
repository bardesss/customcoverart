using System;
using System.Collections.Generic;
using System.IO;
using CustomCoverArt.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CustomCoverArt.Tests;

public class CollageComposerTests
{
    [Theory]
    [InlineData("sparse", 4)]
    [InlineData("medium", 6)]
    [InlineData("dense", 8)]
    [InlineData("unknown", 6)]
    public void ColumnsFor_MapsDensity(string density, int expected)
    {
        Assert.Equal(expected, CollageComposer.ColumnsFor(density));
    }

    [Fact]
    public void BuildCollage_EmptyPosters_ReturnsCanvasOfRequestedSize()
    {
        using var img = CollageComposer.BuildCollage(new List<string>(), 800, 600, "medium", 0);
        Assert.Equal(800, img.Width);
        Assert.Equal(600, img.Height);
    }

    [Fact]
    public void BuildCollage_FewerPostersThanTiles_StillFillsWithoutThrowing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cca-collage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var paths = new List<string>();
        for (int i = 0; i < 2; i++)
        {
            var p = Path.Combine(dir, $"p{i}.png");
            using (var poster = new Image<Rgba32>(100, 150, Color.Blue)) { poster.Save(p); }
            paths.Add(p);
        }

        using var img = CollageComposer.BuildCollage(paths, 800, 600, "sparse", 42);
        Assert.Equal(800, img.Width);
        Assert.Equal(600, img.Height);

        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void BuildCollage_IsDeterministicForSameSeed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cca-collage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var paths = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            var p = Path.Combine(dir, $"p{i}.png");
            using (var poster = new Image<Rgba32>(100, 150, i % 2 == 0 ? Color.Red : Color.Green)) { poster.Save(p); }
            paths.Add(p);
        }

        using var a = CollageComposer.BuildCollage(paths, 400, 400, "medium", 7);
        using var b = CollageComposer.BuildCollage(paths, 400, 400, "medium", 7);
        Assert.Equal(a[0, 0], b[0, 0]);
        Assert.Equal(a[200, 200], b[200, 200]);

        try { Directory.Delete(dir, true); } catch { }
    }
}
