using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;

namespace CustomCoverArt.Tests;

internal static class TestImages
{
    /// <summary>
    /// A valid 43-byte GIF holding one 1×1 frame, whose logical screen claims
    /// <paramref name="width"/>×<paramref name="height"/>. Identify reports the screen size,
    /// and a full decode would allocate it, so this stands in for a decompression bomb
    /// without the test ever allocating one.
    /// </summary>
    public static byte[] GifWithScreen(ushort width, ushort height) => new byte[]
    {
        0x47, 0x49, 0x46, 0x38, 0x39, 0x61,                         // "GIF89a"
        (byte)(width & 0xFF), (byte)(width >> 8),
        (byte)(height & 0xFF), (byte)(height >> 8),
        0x80, 0x00, 0x00,                                           // global colour table, 2 entries
        0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00,                         // white, black
        0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x00, 0x00,             // graphic control extension
        0x2C, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, // 1×1 image descriptor at 0,0
        0x02, 0x02, 0x44, 0x01, 0x00,                               // LZW min size 2, data, terminator
        0x3B,                                                       // trailer
    };

    /// <summary>
    /// Like <see cref="GifWithScreen"/> but repeating the 1×1 frame <paramref name="frames"/> times,
    /// so a header-only file can claim a long animation over a big logical screen.
    /// </summary>
    public static byte[] GifWithScreenFrames(ushort width, ushort height, int frames)
    {
        var single = GifWithScreen(width, height);
        const int headerLen = 19;                 // signature + screen + colour table
        int blockLen = single.Length - headerLen - 1; // one frame block, minus trailer
        var bytes = new byte[headerLen + blockLen * frames + 1];
        Array.Copy(single, 0, bytes, 0, headerLen);
        for (int i = 0; i < frames; i++)
        {
            Array.Copy(single, headerLen, bytes, headerLen + i * blockLen, blockLen);
        }

        bytes[^1] = 0x3B;
        return bytes;
    }

    /// <summary>Writes an animated GIF whose frames alternate red (even) / blue (odd).</summary>
    public static void SaveMultiFrameGif(string path, int width, int height, int frames)
    {
        using var gif = new Image<Rgba32>(width, height, Color.Red);
        for (int i = 1; i < frames; i++)
        {
            using var frame = new Image<Rgba32>(width, height, i % 2 == 0 ? Color.Red : Color.Blue);
            gif.Frames.AddFrame(frame.Frames.RootFrame);
        }

        gif.SaveAsGif(path);
    }

    /// <summary>Writes an animated PNG (APNG) whose frames alternate red / blue.</summary>
    public static void SaveMultiFrameApng(string path, int width, int height, int frames)
    {
        using var png = new Image<Rgba32>(width, height, Color.Red);
        for (int i = 1; i < frames; i++)
        {
            using var frame = new Image<Rgba32>(width, height, i % 2 == 0 ? Color.Red : Color.Blue);
            png.Frames.AddFrame(frame.Frames.RootFrame);
        }

        png.SaveAsPng(path);
    }

    /// <summary>
    /// A 4×2 GIF89a with a two-entry palette: index 0 opaque red, index 1 green but marked
    /// transparent by the graphic control extension. The left half is index 0, the right half
    /// index 1. The LZW stream is written by hand with a clear code before every pixel, so the
    /// code width stays at 3 bits and no encoder is involved.
    /// </summary>
    public static byte[] PalettedGifWithTransparentIndex()
    {
        var indices = new[] { 0, 0, 1, 1, 0, 0, 1, 1 };
        const int clear = 4, end = 5, width = 3;
        var codes = new List<int>();
        foreach (var index in indices)
        {
            codes.Add(clear);
            codes.Add(index);
        }

        codes.Add(end);

        var packed = new List<byte>();
        int acc = 0, bits = 0;
        foreach (var code in codes)
        {
            acc |= code << bits;
            bits += width;
            while (bits >= 8)
            {
                packed.Add((byte)(acc & 0xFF));
                acc >>= 8;
                bits -= 8;
            }
        }

        if (bits > 0)
        {
            packed.Add((byte)(acc & 0xFF));
        }

        var bytes = new List<byte>
        {
            0x47, 0x49, 0x46, 0x38, 0x39, 0x61,                         // "GIF89a"
            0x04, 0x00, 0x02, 0x00,                                     // 4×2 screen
            0x80, 0x00, 0x00,                                           // global colour table, 2 entries
            0xFF, 0x00, 0x00, 0x00, 0xFF, 0x00,                         // red, green
            0x21, 0xF9, 0x04, 0x01, 0x00, 0x00, 0x01, 0x00,             // GCE: transparent flag, index 1
            0x2C, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x02, 0x00, 0x00, // 4×2 image descriptor at 0,0
            0x02,                                                       // LZW minimum code size
            (byte)packed.Count,
        };
        bytes.AddRange(packed);
        bytes.Add(0x00); // end of image data
        bytes.Add(0x3B); // trailer
        return bytes.ToArray();
    }

    /// <summary>
    /// PNG signature, a well-formed IHDR (8-bit RGBA, correct CRC) of the given size, and the
    /// start of an IDAT chunk whose body is <paramref name="idatBody"/> (no CRC, no IEND). Enough
    /// for a header reader to report the size, never enough to decode.
    /// </summary>
    public static byte[] PngWithValidIhdr(int width, int height, byte[] idatBody)
    {
        var ihdr = new byte[17];
        "IHDR"u8.CopyTo(ihdr);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(8), height);
        ihdr[12] = 8; // bit depth
        ihdr[13] = 6; // RGBA
        var all = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };
        all.AddRange(ihdr);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(ihdr));
        all.AddRange(crc);
        var len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, idatBody.Length);
        all.AddRange(len);
        all.AddRange("IDAT"u8.ToArray());
        all.AddRange(idatBody);
        return all.ToArray();
    }

    /// <summary>Path of a committed binary fixture under Fixtures/images.</summary>
    public static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "images", name);

    /// <summary>A Skia-encoded PNG of <paramref name="bitmap"/>.</summary>
    public static byte[] SkiaPng(SKBitmap bitmap)
    {
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>An opaque RGBA bitmap filled with seeded noise, so encoders cannot compress it to nothing.</summary>
    public static SKBitmap NoiseBitmap(int width, int height, int seed)
    {
        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var rng = new Random(seed);
        var span = bmp.GetPixelSpan();
        for (var i = 0; i < span.Length; i += 4)
        {
            span[i] = (byte)rng.Next(256);
            span[i + 1] = (byte)rng.Next(256);
            span[i + 2] = (byte)rng.Next(256);
            span[i + 3] = 255;
        }

        return bmp;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    public static string TempPath(string ext) =>
        Path.Combine(Path.GetTempPath(), $"cca_img_{Guid.NewGuid():N}{ext}");
}
