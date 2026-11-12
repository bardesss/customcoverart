using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using CustomCoverArt.Services.Rendering;
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

    /// <summary>Writes an animated GIF (through <see cref="GifWriter"/>) whose frames alternate red (even) / blue (odd), delay 0.</summary>
    public static void SaveMultiFrameGif(string path, int width, int height, int frames)
    {
        var colours = new (SKColor Colour, int DelayCentiseconds)[frames];
        for (int i = 0; i < frames; i++)
        {
            colours[i] = (i % 2 == 0 ? SKColors.Red : SKColors.Blue, 0);
        }

        SaveGif(path, width, height, colours, loop: true);
    }

    /// <summary>
    /// Writes a GIF through <see cref="GifWriter"/>: one solid frame per entry of
    /// <paramref name="frames"/>, each with its own delay in centiseconds.
    /// </summary>
    public static void SaveGif(string path, int width, int height, IReadOnlyList<(SKColor Colour, int DelayCentiseconds)> frames, bool loop)
    {
        var bitmaps = new List<SKBitmap>(frames.Count);
        try
        {
            var gifFrames = new List<GifFrame>(frames.Count);
            foreach (var (colour, delay) in frames)
            {
                var bitmap = RenderSurface.New(width, height, colour);
                bitmaps.Add(bitmap);
                gifFrames.Add(new GifFrame(bitmap, delay));
            }

            using var file = File.Create(path);
            GifWriter.Write(file, gifFrames, loop);
        }
        finally
        {
            foreach (var bitmap in bitmaps)
            {
                bitmap.Dispose();
            }
        }
    }

    /// <summary>
    /// A hand-built 4x4 GIF89a of two frames: a full red first frame, then a 1x1 blue frame at
    /// (1, 1). Both frames use disposal 1 ("do not dispose"), so the second frame shows red
    /// everywhere except the blue pixel at (1, 1). Delays 10 cs each, palette red/blue/black/white.
    /// The LZW streams put a clear code before every pixel, so the code width stays at 3 bits.
    /// </summary>
    public static byte[] GifTwoFramesPartial()
    {
        var bytes = new List<byte>
        {
            0x47, 0x49, 0x46, 0x38, 0x39, 0x61,             // "GIF89a"
            0x04, 0x00, 0x04, 0x00,                         // 4x4 screen
            0x81, 0x00, 0x00,                               // global colour table, 4 entries
            0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF,             // red, blue
            0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF,             // black, white
        };

        // Frame 1: 4x4 at (0, 0), all index 0 (red).
        bytes.AddRange(new byte[] { 0x21, 0xF9, 0x04, 0x04, 0x0A, 0x00, 0x00, 0x00 }); // GCE: disposal 1, 10 cs
        bytes.AddRange(new byte[] { 0x2C, 0x00, 0x00, 0x00, 0x00, 0x04, 0x00, 0x04, 0x00, 0x00 });
        AddLzwImage(bytes, new int[16]);

        // Frame 2: 1x1 at (1, 1), index 1 (blue).
        bytes.AddRange(new byte[] { 0x21, 0xF9, 0x04, 0x04, 0x0A, 0x00, 0x00, 0x00 });
        bytes.AddRange(new byte[] { 0x2C, 0x01, 0x00, 0x01, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00 });
        AddLzwImage(bytes, new[] { 1 });

        bytes.Add(0x3B); // trailer
        return bytes.ToArray();
    }

    // LZW minimum code size 2 (clear 4, end 5, 3-bit codes), a clear code before every index.
    private static void AddLzwImage(List<byte> bytes, int[] indices)
    {
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

        bytes.Add(0x02);
        bytes.Add((byte)packed.Count);
        bytes.AddRange(packed);
        bytes.Add(0x00);
    }

    /// <summary>
    /// An animated WebP: one solid opaque frame per colour, each shown for
    /// <paramref name="durationMs"/>. Skia cannot encode animated WebP, so each frame is a
    /// Skia-encoded lossless still whose image chunks are wrapped in an ANMF chunk of a
    /// VP8X/ANIM RIFF container (loop count 0, forever).
    /// </summary>
    public static byte[] AnimatedWebp(int width, int height, int durationMs, params SKColor[] colours)
    {
        var body = new List<byte>();
        body.AddRange("WEBP"u8.ToArray());

        var vp8x = new List<byte> { 0x02, 0, 0, 0 }; // flags: animation
        AddU24(vp8x, width - 1);
        AddU24(vp8x, height - 1);
        AddChunk(body, "VP8X", vp8x);

        AddChunk(body, "ANIM", new List<byte> { 0, 0, 0, 0, 0, 0 }); // background colour, loop count 0

        foreach (var colour in colours)
        {
            byte[] still;
            using (var bitmap = RenderSurface.New(width, height, colour))
            using (var pixmap = bitmap.PeekPixels())
            using (var data = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100))
                ?? throw new InvalidOperationException("WebP encoding failed"))
            {
                still = data.ToArray();
            }

            var anmf = new List<byte>();
            AddU24(anmf, 0); // X offset / 2
            AddU24(anmf, 0); // Y offset / 2
            AddU24(anmf, width - 1);
            AddU24(anmf, height - 1);
            AddU24(anmf, durationMs);
            anmf.Add(0x02); // do not blend, do not dispose
            // The still's chunks after "RIFF", size and "WEBP" (VP8L for an opaque lossless image).
            anmf.AddRange(still.AsSpan(12).ToArray());
            AddChunk(body, "ANMF", anmf);
        }

        var riff = new List<byte>();
        riff.AddRange("RIFF"u8.ToArray());
        var size = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, body.Count);
        riff.AddRange(size);
        riff.AddRange(body);
        return riff.ToArray();
    }

    private static void AddChunk(List<byte> target, string fourCc, List<byte> payload)
    {
        target.AddRange(System.Text.Encoding.ASCII.GetBytes(fourCc));
        var size = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, payload.Count);
        target.AddRange(size);
        target.AddRange(payload);
        if (payload.Count % 2 == 1)
        {
            target.Add(0);
        }
    }

    private static void AddU24(List<byte> target, int value)
    {
        target.Add((byte)(value & 0xFF));
        target.Add((byte)((value >> 8) & 0xFF));
        target.Add((byte)((value >> 16) & 0xFF));
    }

    /// <summary>
    /// Writes an animated PNG (APNG) whose frames alternate red / blue. Skia encodes the two
    /// stills; their IDAT data is re-wrapped with acTL / fcTL / fdAT chunks (full-frame, 1/10 s).
    /// </summary>
    public static void SaveMultiFrameApng(string path, int width, int height, int frames)
    {
        byte[][] idat = new byte[2][];
        byte[] header = Array.Empty<byte>();
        for (int c = 0; c < 2; c++)
        {
            using var bitmap = RenderSurface.New(width, height, c == 0 ? SKColors.Red : SKColors.Blue);
            var png = SkiaPng(bitmap);
            var data = new List<byte>();
            int pos = 8;
            while (pos + 8 <= png.Length)
            {
                int len = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(pos));
                var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
                if (type == "IHDR" && c == 0)
                {
                    header = png.AsSpan(pos + 8, len).ToArray();
                }
                else if (type == "IDAT")
                {
                    data.AddRange(png.AsSpan(pos + 8, len).ToArray());
                }

                pos += 12 + len;
            }

            idat[c] = data.ToArray();
        }

        using var file = File.Create(path);
        file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        WritePngChunk(file, "IHDR", header);
        var actl = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(actl, frames);
        WritePngChunk(file, "acTL", actl); // play count 0 = forever
        int seq = 0;
        for (int i = 0; i < frames; i++)
        {
            var fctl = new byte[26];
            BinaryPrimitives.WriteInt32BigEndian(fctl, seq++);
            BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(4), width);
            BinaryPrimitives.WriteInt32BigEndian(fctl.AsSpan(8), height);
            BinaryPrimitives.WriteInt16BigEndian(fctl.AsSpan(20), 1);  // delay numerator
            BinaryPrimitives.WriteInt16BigEndian(fctl.AsSpan(22), 10); // delay denominator
            WritePngChunk(file, "fcTL", fctl);
            var pixels = idat[i % 2];
            if (i == 0)
            {
                WritePngChunk(file, "IDAT", pixels);
            }
            else
            {
                var fdat = new byte[4 + pixels.Length];
                BinaryPrimitives.WriteInt32BigEndian(fdat, seq++);
                pixels.CopyTo(fdat, 4);
                WritePngChunk(file, "fdAT", fdat);
            }
        }

        WritePngChunk(file, "IEND", Array.Empty<byte>());
    }

    private static void WritePngChunk(Stream stream, string type, byte[] body)
    {
        var typed = new byte[4 + body.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(typed, 0);
        body.CopyTo(typed, 4);
        var len = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, body.Length);
        stream.Write(len);
        stream.Write(typed);
        var crc = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        stream.Write(crc);
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

    /// <summary>A hand-built <paramref name="width"/>x<paramref name="height"/> 24-bit BMP filled with one colour (rows padded to 4 bytes).</summary>
    public static byte[] Bmp24(int width, int height, byte red, byte green, byte blue)
    {
        int stride = ((width * 3) + 3) / 4 * 4;
        var bytes = new byte[54 + (stride * height)];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);  // pixel data offset
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);  // BITMAPINFOHEADER
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);   // planes
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 24);  // bits per pixel
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34), stride * height);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = 54 + (y * stride) + (x * 3);
                bytes[o] = blue;
                bytes[o + 1] = green;
                bytes[o + 2] = red;
            }
        }

        return bytes;
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
