using System;
using CustomCoverArt.Services;
using CustomCoverArt.Services.Rendering;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;
using Xunit;

namespace CustomCoverArt.Tests;

public class SkiaColorsTests
{
    [Theory]
    [InlineData("#ff0000")] [InlineData("ff0000")] [InlineData("#F0A")] [InlineData("#f0a8")]
    [InlineData("#11223344")] [InlineData("#000000")] [InlineData("#FFFFFF")] [InlineData("#12345")]
    [InlineData("#gg0000")] [InlineData("#1234567")]
    public void Parse_MatchesImageSharpParseHex(string hex)
    {
        var expected = DocumentRenderer.SafeColor(hex, SixLabors.ImageSharp.Color.Gray).ToPixel<Rgba32>();
        var actual = SkiaColors.Parse(hex, new SKColor(128, 128, 128));
        Assert.Equal((expected.R, expected.G, expected.B, expected.A), (actual.Red, actual.Green, actual.Blue, actual.Alpha));
    }

    [Theory] [InlineData(null)] [InlineData("")] [InlineData("   ")] [InlineData("red")]
    public void Parse_NullEmptyOrNamed_ReturnsFallback(string? hex)
        => Assert.Equal(new SKColor(1, 2, 3), SkiaColors.Parse(hex, new SKColor(1, 2, 3)));

    [Fact]
    public void Parse_TrimsWhitespace()
        => Assert.Equal(new SKColor(0x11, 0x22, 0x33), SkiaColors.Parse("  #112233 \t", SKColors.Black));

    [Fact]
    public void Parse_ShortFormDoublesEachDigit()
        => Assert.Equal(new SKColor(0xFF, 0x00, 0xAA, 0x88), SkiaColors.Parse("#f0a8", SKColors.Black));

    [Fact]
    public void WithAlpha_ReplacesAlphaKeepingRgb()
    {
        var c = SkiaColors.WithAlpha(new SKColor(10, 20, 30, 255), 0.5f);
        Assert.Equal((10, 20, 30), (c.Red, c.Green, c.Blue));
        Assert.InRange(c.Alpha, 127, 128);
    }

    [Theory]
    [InlineData(-1f, 0)] [InlineData(0f, 0)] [InlineData(1f, 255)] [InlineData(7f, 255)]
    public void WithAlpha_ClampsToUnitRange(float a, int expected)
        => Assert.Equal(expected, SkiaColors.WithAlpha(SKColors.White, a).Alpha);

    [Theory]
    [InlineData(0.1f)] [InlineData(0.25f)] [InlineData(0.5f)] [InlineData(0.75f)] [InlineData(0.9f)]
    public void WithAlpha_MatchesImageSharpWithAlpha(float a)
    {
        var expected = SixLabors.ImageSharp.Color.White.WithAlpha(a).ToPixel<Rgba32>().A;
        Assert.InRange(SkiaColors.WithAlpha(SKColors.White, a).Alpha, expected - 1, expected + 1);
    }
}
