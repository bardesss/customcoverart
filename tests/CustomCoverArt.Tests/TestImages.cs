using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

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

    public static string TempPath(string ext) =>
        Path.Combine(Path.GetTempPath(), $"cca_img_{Guid.NewGuid():N}{ext}");
}
