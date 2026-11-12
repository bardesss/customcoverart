using CustomCoverArt.Services.Rendering;
using SkiaSharp;
using Xunit;

namespace CustomCoverArt.Tests;

public class RenderSurfaceTests
{
    [Fact]
    public void New_IsTransparentRgbaPremulWithoutColourSpace()
    {
        using var clear = RenderSurface.New(3, 2);
        Assert.Equal((3, 2), (clear.Width, clear.Height));
        Assert.Equal(3 * 4, clear.RowBytes);
        Assert.Equal(SKColorType.Rgba8888, clear.ColorType);
        Assert.Equal(SKAlphaType.Premul, clear.AlphaType);
        Assert.Null(clear.Info.ColorSpace);
        Assert.Equal(new SKColor(0, 0, 0, 0), clear.GetPixel(2, 1));
    }

    [Fact]
    public void New_WithFill_ClearsToThatColourWithTheSameFormat()
    {
        using var filled = RenderSurface.New(3, 2, new SKColor(9, 8, 7));
        Assert.Equal((3, 2), (filled.Width, filled.Height));
        Assert.Equal(3 * 4, filled.RowBytes);
        Assert.Equal(SKColorType.Rgba8888, filled.ColorType);
        Assert.Equal(SKAlphaType.Premul, filled.AlphaType);
        Assert.Null(filled.Info.ColorSpace);
        Assert.Equal(new SKColor(9, 8, 7), filled.GetPixel(0, 0));
        Assert.Equal(new SKColor(9, 8, 7), filled.GetPixel(2, 1));
    }
}
