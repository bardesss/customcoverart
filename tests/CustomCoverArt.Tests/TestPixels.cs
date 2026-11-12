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

    /// <summary>
    /// Decodes the first frame of a PNG or GIF file (a render output) into an RGBA8888 premultiplied
    /// bitmap with no colour space, through <see cref="SKCodec"/> rather than the engine under test.
    /// </summary>
    public static SKBitmap Load(string path)
    {
        using var codec = SKCodec.Create(path) ?? throw new InvalidOperationException($"Not a decodable image: {path}");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        var result = codec.GetPixels(info, bitmap.GetPixels());
        if (result != SKCodecResult.Success)
        {
            bitmap.Dispose();
            throw new InvalidOperationException($"Decoding {path} failed: {result}");
        }

        return bitmap;
    }

    /// <summary>Writes <paramref name="bitmap"/> as a PNG file.</summary>
    public static void SavePng(SKBitmap bitmap, string path) => Save(bitmap, path, SKEncodedImageFormat.Png, 100);

    /// <summary>Writes <paramref name="bitmap"/> as a quality-90 JPEG file.</summary>
    public static void SaveJpeg(SKBitmap bitmap, string path) => Save(bitmap, path, SKEncodedImageFormat.Jpeg, 90);

    /// <summary>A bitmap of <paramref name="width"/> x <paramref name="height"/> filled by <paramref name="color"/>(x, y).</summary>
    public static SKBitmap Painted(int width, int height, Func<int, int, SKColor> color)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, color(x, y));
            }
        }

        return bitmap;
    }

    /// <summary>A copy of the raw pixel bytes.</summary>
    public static byte[] Bytes(SKBitmap bitmap) => bitmap.GetPixelSpan().ToArray();

    private static void Save(SKBitmap bitmap, string path, SKEncodedImageFormat format, int quality)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality);
        System.IO.File.WriteAllBytes(path, data.ToArray());
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
