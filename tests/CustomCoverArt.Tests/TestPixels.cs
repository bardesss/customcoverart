using System;
using SkiaSharp;

namespace CustomCoverArt.Tests;

/// <summary>A straight-alpha RGBA pixel read back from a bitmap.</summary>
internal readonly record struct Rgba(byte R, byte G, byte B, byte A);

internal static class TestPixels
{
    /// <summary>
    /// The unpremultiplied colour at (<paramref name="x"/>, <paramref name="y"/>) of an
    /// RGBA8888 premultiplied bitmap (the only layout the decoder produces).
    /// </summary>
    public static Rgba Px(SKBitmap bmp, int x, int y)
    {
        if (bmp.ColorType != SKColorType.Rgba8888 || bmp.AlphaType != SKAlphaType.Premul)
        {
            throw new ArgumentException($"Expected Rgba8888/Premul, got {bmp.ColorType}/{bmp.AlphaType}.", nameof(bmp));
        }

        var span = bmp.GetPixelSpan();
        var o = (y * bmp.RowBytes) + (x * 4);
        var a = span[o + 3];
        return new Rgba(Unpremul(span[o], a), Unpremul(span[o + 1], a), Unpremul(span[o + 2], a), a);
    }

    /// <summary>Whether every channel of <paramref name="actual"/> is within <paramref name="tolerance"/> of <paramref name="expected"/>.</summary>
    public static bool Near(Rgba actual, Rgba expected, int tolerance)
        => Math.Abs(actual.R - expected.R) <= tolerance
           && Math.Abs(actual.G - expected.G) <= tolerance
           && Math.Abs(actual.B - expected.B) <= tolerance
           && Math.Abs(actual.A - expected.A) <= tolerance;

    private static byte Unpremul(byte c, byte a)
    {
        if (a == 255)
        {
            return c;
        }

        if (a == 0)
        {
            return 0;
        }

        return (byte)Math.Min(255, ((c * 255) + (a / 2)) / a);
    }
}
