using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CustomCoverArt.Services;
using SkiaSharp;
using Xunit;

namespace CustomCoverArt.Tests;

public sealed class CollageComposerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cca-collage-" + Guid.NewGuid().ToString("N"));

    public CollageComposerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Poster(string name, int w, int h, Func<int, int, SKColor> color)
    {
        var path = Path.Combine(_dir, name);
        using var bmp = TestPixels.Painted(w, h, color);
        TestPixels.SavePng(bmp, path);
        return path;
    }

    private string Solid(string name, SKColor c) => Poster(name, 100, 150, (_, _) => c);

    private string Garbage(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 });
        return path;
    }

    [Theory]
    [InlineData("sparse", 4)]
    [InlineData("medium", 6)]
    [InlineData("dense", 8)]
    [InlineData("unknown", 6)]
    [InlineData("SPARSE", 4)]
    [InlineData("Dense", 8)]
    [InlineData("", 6)]
    [InlineData(null, 6)]
    public void ColumnsFor_MapsDensity(string? density, int expected)
    {
        Assert.Equal(expected, CollageComposer.ColumnsFor(density!));
    }

    [Fact]
    public void BuildCollage_EmptyPosters_ReturnsDarkCanvasOfRequestedSize()
    {
        using var img = CollageComposer.BuildCollage(new List<string>(), 800, 600, "medium", 0);
        Assert.Equal(800, img.Width);
        Assert.Equal(600, img.Height);
        Assert.Equal(SKColorType.Rgba8888, img.ColorType);
        Assert.Equal(SKAlphaType.Premul, img.AlphaType);
        Assert.Equal(new Rgba(18, 18, 18, 255), TestPixels.Px(img, 0, 0));
        Assert.Equal(new Rgba(18, 18, 18, 255), TestPixels.Px(img, 799, 599));
    }

    [Fact]
    public void BuildCollage_NullPosters_ReturnsDarkCanvas()
    {
        using var img = CollageComposer.BuildCollage(null!, 120, 80, "medium", 0);
        Assert.Equal(new Rgba(18, 18, 18, 255), TestPixels.Px(img, 60, 40));
    }

    [Fact]
    public void BuildCollage_OnePoster_FillsEveryTile()
    {
        var paths = new List<string> { Solid("p.png", new SKColor(200, 30, 30)) };

        using var img = CollageComposer.BuildCollage(paths, 800, 600, "sparse", 42);

        // 4 columns of 200x300 tiles, 2 rows: every tile centre shows the poster.
        for (var r = 0; r < 2; r++)
        {
            for (var c = 0; c < 4; c++)
            {
                Assert.Equal(new Rgba(200, 30, 30, 255), TestPixels.Px(img, (c * 200) + 100, (r * 300) + 150));
            }
        }
    }

    [Fact]
    public void BuildCollage_FewerPostersThanTiles_RepeatsThemWithoutThrowing()
    {
        var paths = new List<string> { Solid("a.png", new SKColor(0, 0, 255)), Solid("b.png", new SKColor(255, 0, 0)) };

        using var img = CollageComposer.BuildCollage(paths, 800, 600, "sparse", 42);

        Assert.Equal(800, img.Width);
        Assert.Equal(600, img.Height);
        var seen = new HashSet<Rgba>();
        for (var r = 0; r < 2; r++)
        {
            for (var c = 0; c < 4; c++)
            {
                seen.Add(TestPixels.Px(img, (c * 200) + 100, (r * 300) + 150));
            }
        }

        Assert.Equal(new HashSet<Rgba> { new(0, 0, 255, 255), new(255, 0, 0, 255) }, seen);
    }

    [Fact]
    public void BuildCollage_UndecodablePostersAreSkippedAndGoodOnesStillDraw()
    {
        var paths = new List<string>
        {
            Solid("good.png", new SKColor(0, 200, 0)),
            Garbage("bad.png"),
            Path.Combine(_dir, "missing.png"),
        };

        SKBitmap? img = null;
        var ex = Record.Exception(() => img = CollageComposer.BuildCollage(paths, 800, 600, "sparse", 3));
        Assert.Null(ex);
        using (img)
        {
            var good = 0;
            var dark = 0;
            for (var r = 0; r < 2; r++)
            {
                for (var c = 0; c < 4; c++)
                {
                    var px = TestPixels.Px(img!, (c * 200) + 100, (r * 300) + 150);
                    if (px == new Rgba(0, 200, 0, 255)) { good++; }
                    else if (px == new Rgba(18, 18, 18, 255)) { dark++; }
                }
            }

            Assert.True(good >= 1, "a good poster should be drawn");
            Assert.True(dark >= 1, "a bad poster leaves the dark canvas showing");
            Assert.Equal(8, good + dark);
        }
    }

    [Fact]
    public void BuildCollage_OnlyUndecodablePosters_GivesDarkCanvas()
    {
        using var img = CollageComposer.BuildCollage(new List<string> { Garbage("bad.png") }, 400, 300, "medium", 1);
        Assert.Equal(new Rgba(18, 18, 18, 255), TestPixels.Px(img, 200, 150));
    }

    [Fact]
    public void BuildCollage_WidePoster_CoverCropKeepsTheCentre()
    {
        // 400x100: red left quarter, green middle half, blue right quarter. A 200x300 tile
        // scales it 3x, so the window is the central ~67 source pixels: all green.
        var wide = Poster("wide.png", 400, 100, (x, _) => x < 100 ? new SKColor(255, 0, 0) : x < 300 ? new SKColor(0, 255, 0) : new SKColor(0, 0, 255));

        using var img = CollageComposer.BuildCollage(new List<string> { wide }, 800, 600, "sparse", 0);

        foreach (var (x, y) in new[] { (5, 5), (100, 150), (195, 295), (205, 5), (795, 595) })
        {
            Assert.Equal(new Rgba(0, 255, 0, 255), TestPixels.Px(img, x, y));
        }
    }

    [Fact]
    public void BuildCollage_TallPoster_CoverCropKeepsTheCentre()
    {
        // 100x400: red top quarter, green middle half, blue bottom quarter. The 200x300 tile
        // scales it 2x and shows source rows 125..275: all green.
        var tall = Poster("tall.png", 100, 400, (_, y) => y < 100 ? new SKColor(255, 0, 0) : y < 300 ? new SKColor(0, 255, 0) : new SKColor(0, 0, 255));

        using var img = CollageComposer.BuildCollage(new List<string> { tall }, 800, 600, "sparse", 0);

        foreach (var (x, y) in new[] { (5, 5), (100, 150), (195, 295), (205, 305), (795, 595) })
        {
            Assert.Equal(new Rgba(0, 255, 0, 255), TestPixels.Px(img, x, y));
        }
    }

    [Theory]
    [InlineData("sparse", 4)]
    [InlineData("medium", 6)]
    [InlineData("dense", 8)]
    public void BuildCollage_DensityDecidesTheColumnCount(string density, int columns)
    {
        // A poster that is red on its left half and blue on its right half: along one row,
        // each tile contributes exactly one blue-to-red step at its left edge.
        var split = Poster("split.png", 100, 150, (x, _) => x < 50 ? new SKColor(255, 0, 0) : new SKColor(0, 0, 255));

        using var img = CollageComposer.BuildCollage(new List<string> { split }, 800, 900, density, 0);

        var steps = 0;
        var wasRed = TestPixels.Px(img, 0, 100).R > 128;
        for (var x = 1; x < img.Width; x++)
        {
            var isRed = TestPixels.Px(img, x, 100).R > 128;
            if (isRed && !wasRed) { steps++; }
            wasRed = isRed;
        }

        Assert.Equal(columns - 1, steps);
    }

    [Fact]
    public void BuildCollage_SameSeedGivesIdenticalPixels_DifferentSeedGivesADifferentOrder()
    {
        var paths = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            paths.Add(Solid($"p{i}.png", new SKColor((byte)(40 * i), (byte)(255 - (40 * i)), 90)));
        }

        using var a = CollageComposer.BuildCollage(paths, 400, 400, "medium", 7);
        using var b = CollageComposer.BuildCollage(paths, 400, 400, "medium", 7);
        using var c = CollageComposer.BuildCollage(paths, 400, 400, "medium", 8);

        Assert.True(TestPixels.Bytes(a).SequenceEqual(TestPixels.Bytes(b)));
        Assert.False(TestPixels.Bytes(a).SequenceEqual(TestPixels.Bytes(c)));
    }

    [Fact]
    public void BuildCollage_OpaquePosters_GiveAnOpaqueCanvasOnEveryPixel()
    {
        var paths = new List<string> { Solid("p.png", new SKColor(10, 200, 90)) };

        using var img = CollageComposer.BuildCollage(paths, 333, 211, "dense", 5);

        var span = img.GetPixelSpan();
        for (var y = 0; y < img.Height; y++)
        {
            for (var x = 0; x < img.Width; x++)
            {
                Assert.Equal(255, span[(y * img.RowBytes) + (x * 4) + 3]);
            }
        }
    }
}
