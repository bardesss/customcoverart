using System;
using CustomCoverArt.Services;
using CustomCoverArt.Services.Rendering;
using SkiaSharp;
using Xunit;

namespace CustomCoverArt.Tests;

public class SkiaColorsTests
{
    /// <summary>
    /// The hex rules SafeColor has always followed (3/4/6/8 digits, optional '#', anything else gray);
    /// the expected tuples are frozen literals.
    /// </summary>
    [Theory]
    [InlineData("#ff0000", 255, 0, 0, 255)] [InlineData("ff0000", 255, 0, 0, 255)]
    [InlineData("#F0A", 255, 0, 170, 255)] [InlineData("#f0a8", 255, 0, 170, 136)]
    [InlineData("#11223344", 17, 34, 51, 68)] [InlineData("#000000", 0, 0, 0, 255)]
    [InlineData("#FFFFFF", 255, 255, 255, 255)] [InlineData("#12345", 128, 128, 128, 255)]
    [InlineData("#gg0000", 128, 128, 128, 255)] [InlineData("#1234567", 128, 128, 128, 255)]
    public void Parse_FollowsHexRules(string hex, int r, int g, int b, int a)
    {
        var actual = SkiaColors.Parse(hex, new SKColor(128, 128, 128));
        Assert.Equal((r, g, b, a), ((int)actual.Red, (int)actual.Green, (int)actual.Blue, (int)actual.Alpha));
    }

    /// <summary>DocumentRenderer.SafeColor is SkiaColors.Parse: same values, same fallback.</summary>
    [Theory]
    [InlineData("#ff000080")] [InlineData("abc")] [InlineData("nonsense")] [InlineData(null)]
    public void SafeColor_DelegatesToParse(string? hex)
        => Assert.Equal(SkiaColors.Parse(hex, new SKColor(9, 8, 7)), DocumentRenderer.SafeColor(hex, new SKColor(9, 8, 7)));

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

    /// <summary>Exact alpha bytes (round half away from zero of a * 255), frozen literals.</summary>
    [Theory]
    [InlineData(0.1f, 26)] [InlineData(0.25f, 64)] [InlineData(0.5f, 128)] [InlineData(0.75f, 191)] [InlineData(0.9f, 230)]
    public void WithAlpha_GivesExactByte(float a, int expected)
        => Assert.Equal(expected, SkiaColors.WithAlpha(SKColors.White, a).Alpha);
}
