using System;
using CustomCoverArt.Services.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;
using Xunit;

namespace CustomCoverArt.Tests;

public class ImageSharpBridgeTests
{
    [Fact]
    public void RoundTrip_OpaquePixels_AreExact()
    {
        using var src = new Image<Rgba32>(7, 5);
        for (var y = 0; y < 5; y++)
        {
            for (var x = 0; x < 7; x++)
            {
                src[x, y] = new Rgba32((byte)(x * 37 + 1), (byte)(y * 51 + 3), (byte)((x * 11 + y * 29) % 256), 255);
            }
        }

        using var skia = ImageSharpBridge.ToSkia(src);
        using var back = ImageSharpBridge.ToImageSharp(skia);

        Assert.Equal((7, 5), (back.Width, back.Height));
        for (var y = 0; y < 5; y++)
        {
            for (var x = 0; x < 7; x++)
            {
                Assert.Equal(src[x, y], back[x, y]);
            }
        }
    }

    [Fact]
    public void RoundTrip_MidToneAtModerateAlpha_WithinOneLevel()
    {
        // Premultiplied 8-bit storage is inherently lossy at very low alpha (alpha 1 keeps
        // one level of colour), so mid tones are only held to one level from alpha 128 up.
        foreach (byte alpha in new byte[] { 128, 200, 254 })
        {
            using var src = new Image<Rgba32>(1, 1);
            src[0, 0] = new Rgba32(10, 120, 77, alpha);
            using var skia = ImageSharpBridge.ToSkia(src);
            using var back = ImageSharpBridge.ToImageSharp(skia);
            var a = back[0, 0];
            Assert.Equal(alpha, a.A);
            Assert.InRange(a.R, 9, 11);
            Assert.InRange(a.G, 119, 121);
            Assert.InRange(a.B, 76, 78);
        }
    }

    [Fact]
    public void RoundTrip_FullyTransparent_StaysTransparent()
    {
        using var src = new Image<Rgba32>(1, 1);
        src[0, 0] = new Rgba32(0, 0, 0, 0);
        using var skia = ImageSharpBridge.ToSkia(src);
        using var back = ImageSharpBridge.ToImageSharp(skia);
        Assert.Equal(0, back[0, 0].A);
    }

    [Fact]
    public void RoundTrip_SemiTransparent_WithinOneLevel()
    {
        var colours = new[] { new Rgba32(255, 0, 0, 0), new Rgba32(0, 255, 0, 0), new Rgba32(0, 0, 0, 0), new Rgba32(255, 255, 255, 0) };
        var alphas = new byte[] { 1, 64, 128, 254 };
        using var src = new Image<Rgba32>(colours.Length, alphas.Length);
        for (var y = 0; y < alphas.Length; y++)
        {
            for (var x = 0; x < colours.Length; x++)
            {
                var c = colours[x];
                src[x, y] = new Rgba32(c.R, c.G, c.B, alphas[y]);
            }
        }

        using var skia = ImageSharpBridge.ToSkia(src);
        using var back = ImageSharpBridge.ToImageSharp(skia);

        for (var y = 0; y < alphas.Length; y++)
        {
            for (var x = 0; x < colours.Length; x++)
            {
                var e = src[x, y];
                var a = back[x, y];
                Assert.Equal(e.A, a.A);
                Assert.InRange(a.R, Math.Max(0, e.R - 1), e.R + 1);
                Assert.InRange(a.G, Math.Max(0, e.G - 1), e.G + 1);
                Assert.InRange(a.B, Math.Max(0, e.B - 1), e.B + 1);
            }
        }
    }

    [Fact]
    public void ToSkia_IsPremultipliedRgba8888()
    {
        using var src = new Image<Rgba32>(1, 1);
        src[0, 0] = new Rgba32(200, 100, 50, 128);
        using var skia = ImageSharpBridge.ToSkia(src);
        Assert.Equal(SKColorType.Rgba8888, skia.ColorType);
        Assert.Equal(SKAlphaType.Premul, skia.AlphaType);
        Assert.Equal(new byte[] { 100, 50, 25, 128 }, skia.GetPixelSpan().Slice(0, 4).ToArray());
    }

    [Fact]
    public void ToSkia_NonRgbaFormat_ConvertsChannels()
    {
        using var src = new Image<Bgra32>(2, 2);
        src[1, 0] = new Bgra32(3, 2, 1, 255); // r,g,b,a ctor order: R=3,G=2,B=1
        using var skia = ImageSharpBridge.ToSkia(src);
        var expected = src[1, 0];
        var c = skia.GetPixel(1, 0);
        Assert.Equal((expected.R, expected.G, expected.B, expected.A), (c.Red, c.Green, c.Blue, c.Alpha));
    }

    [Fact]
    public void ToSkia_MultiFrame_TakesFirstFrameOnly()
    {
        using var src = new Image<Rgba32>(2, 2, new Rgba32(10, 10, 10, 255));
        using var second = new Image<Rgba32>(2, 2, new Rgba32(200, 0, 0, 255));
        src.Frames.AddFrame(second.Frames.RootFrame);
        using var skia = ImageSharpBridge.ToSkia(src);
        Assert.Equal(new SKColor(10, 10, 10), skia.GetPixel(0, 0));
    }

    [Fact]
    public void RenderSurface_New_IsTransparentOrFilled()
    {
        using var clear = RenderSurface.New(3, 2);
        Assert.Equal((3, 2), (clear.Width, clear.Height));
        Assert.Equal(SKColorType.Rgba8888, clear.ColorType);
        Assert.Equal(SKAlphaType.Premul, clear.AlphaType);
        Assert.Null(clear.Info.ColorSpace);
        Assert.Equal(new SKColor(0, 0, 0, 0), clear.GetPixel(2, 1));

        using var filled = RenderSurface.New(3, 2, new SKColor(9, 8, 7));
        Assert.Equal(new SKColor(9, 8, 7), filled.GetPixel(2, 1));
    }
}
