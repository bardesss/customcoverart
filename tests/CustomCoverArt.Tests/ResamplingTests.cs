using CustomCoverArt.Services.Rendering;
using SkiaSharp;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>Resampling choice (spec S5 row 8): cubic Mitchell when enlarging on either axis, linear with linear mipmaps otherwise.</summary>
public class ResamplingTests
{
    [Theory]
    [InlineData(10, 10, 20, 20)]   // both axes enlarge
    [InlineData(10, 10, 11, 10)]   // one axis enlarges by a pixel
    [InlineData(100, 10, 50, 40)]  // one shrinks, the other enlarges
    [InlineData(1, 1, 2, 1)]
    public void Enlarging_IsCubicMitchell(int srcW, int srcH, int dstW, int dstH)
    {
        var s = Resampling.For(srcW, srcH, dstW, dstH);

        Assert.True(s.UseCubic);
        Assert.Equal(SKCubicResampler.Mitchell.B, s.Cubic.B);
        Assert.Equal(SKCubicResampler.Mitchell.C, s.Cubic.C);
    }

    [Theory]
    [InlineData(10, 10, 10, 10)]   // same size
    [InlineData(400, 300, 50, 30)] // shrinking
    [InlineData(400, 300, 400, 30)]
    public void SameSizeOrShrinking_IsLinearWithLinearMipmaps(int srcW, int srcH, int dstW, int dstH)
    {
        var s = Resampling.For(srcW, srcH, dstW, dstH);

        Assert.False(s.UseCubic);
        Assert.Equal(SKFilterMode.Linear, s.Filter);
        Assert.Equal(SKMipmapMode.Linear, s.Mipmap);
    }
}
