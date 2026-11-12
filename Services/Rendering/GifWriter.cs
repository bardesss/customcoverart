using SkiaSharp;

namespace CustomCoverArt.Services.Rendering;

/// <summary>One animation frame: premultiplied RGBA8888 pixels and a delay in hundredths of a second.</summary>
internal readonly record struct GifFrame(SKBitmap Pixels, int DelayCentiseconds);

/// <summary>
/// A GIF89a encoder (SkiaSharp cannot encode GIF): one global palette for all frames, Floyd-Steinberg
/// dithering, full-canvas frames with disposal 1, and a NETSCAPE2.0 loop block when looping.
/// The same frames always give the same bytes.
/// </summary>
internal static class GifWriter
{
    public static void Write(Stream output, IReadOnlyList<GifFrame> frames, bool loop)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count == 0)
        {
            throw new ArgumentException("A GIF needs at least one frame.", nameof(frames));
        }

        var bitmaps = new SKBitmap[frames.Count];
        for (var i = 0; i < frames.Count; i++)
        {
            var bitmap = frames[i].Pixels ?? throw new ArgumentException($"Frame {i} has no pixels.", nameof(frames));
            if (bitmap.ColorType != SKColorType.Rgba8888 || bitmap.AlphaType != SKAlphaType.Premul)
            {
                throw new ArgumentException($"Frame {i} is {bitmap.ColorType}/{bitmap.AlphaType}; expected Rgba8888/Premul.", nameof(frames));
            }

            if (bitmap.Width is < 1 or > ushort.MaxValue || bitmap.Height is < 1 or > ushort.MaxValue)
            {
                throw new ArgumentException($"Frame {i} is {bitmap.Width}x{bitmap.Height}; a GIF holds 1..65535 per side.", nameof(frames));
            }

            if (i > 0 && (bitmap.Width != bitmaps[0].Width || bitmap.Height != bitmaps[0].Height))
            {
                throw new ArgumentException($"Frame {i} is {bitmap.Width}x{bitmap.Height}; all frames must match the first ({bitmaps[0].Width}x{bitmaps[0].Height}).", nameof(frames));
            }

            bitmaps[i] = bitmap;
        }

        int width = bitmaps[0].Width, height = bitmaps[0].Height;
        var palette = WuQuantizer.Build(bitmaps);
        var bits = palette.BitsPerIndex;

        var header = new List<byte>(800);
        header.AddRange("GIF89a"u8);
        AddU16(header, width);
        AddU16(header, height);
        header.Add((byte)(0x80 | (7 << 4) | (bits - 1))); // global table, colour resolution 8 bits, table size
        header.Add(0); // background colour index
        header.Add(0); // pixel aspect ratio: not given
        for (var i = 0; i < 1 << bits; i++)
        {
            var c = i < palette.Colors.Count ? palette.Colors[i] : SKColors.Black;
            header.Add(c.Red);
            header.Add(c.Green);
            header.Add(c.Blue);
        }

        if (loop)
        {
            header.AddRange([0x21, 0xFF, 11]);
            header.AddRange("NETSCAPE2.0"u8);
            header.AddRange([3, 1, 0, 0, 0]); // sub-block: loop count 0 (forever), terminator
        }

        output.Write(header.ToArray());

        var indices = new byte[width * height];
        var frameHeader = new List<byte>(20);
        for (var f = 0; f < bitmaps.Length; f++)
        {
            Dither(bitmaps[f], palette, indices);
            frameHeader.Clear();
            var transparent = palette.TransparentIndex >= 0;
            frameHeader.AddRange([0x21, 0xF9, 4, (byte)((1 << 2) | (transparent ? 1 : 0))]); // disposal 1: do not dispose
            AddU16(frameHeader, Math.Clamp(frames[f].DelayCentiseconds, 0, ushort.MaxValue));
            frameHeader.Add(transparent ? (byte)palette.TransparentIndex : (byte)0);
            frameHeader.Add(0);
            frameHeader.Add(0x2C);
            AddU16(frameHeader, 0);
            AddU16(frameHeader, 0);
            AddU16(frameHeader, width);
            AddU16(frameHeader, height);
            frameHeader.Add(0); // no local table, not interlaced
            output.Write(frameHeader.ToArray());
            GifLzw.Encode(indices, Math.Max(2, bits), output);
        }

        output.WriteByte(0x3B);
    }

    /// <summary>
    /// Maps a frame to palette indices with Floyd-Steinberg error diffusion (7/16, 3/16, 5/16, 1/16),
    /// left to right on every row. Pixels with alpha below 128 take the transparent index, if any,
    /// and neither receive nor spread error.
    /// </summary>
    private static void Dither(SKBitmap bitmap, GifPalette palette, byte[] indices)
    {
        int width = bitmap.Width, height = bitmap.Height, rowBytes = bitmap.RowBytes;
        ReadOnlySpan<byte> pixels = bitmap.GetPixelSpan();
        var transparent = palette.TransparentIndex;
        var colors = palette.Colors;

        // Errors in sixteenths, three channels per pixel, one pixel of padding on each side.
        var current = new int[(width + 2) * 3];
        var below = new int[(width + 2) * 3];
        for (var y = 0; y < height; y++)
        {
            Array.Clear(below);
            var row = y * rowBytes;
            var outRow = y * width;
            for (var x = 0; x < width; x++)
            {
                var (r, g, b, a) = Pixels.Unpremultiply(pixels, row + (x * 4));
                if (transparent >= 0 && a < 128)
                {
                    indices[outRow + x] = (byte)transparent;
                    continue;
                }

                var e = (x + 1) * 3;
                r = Math.Clamp(r + ((current[e] + 8) >> 4), 0, 255);
                g = Math.Clamp(g + ((current[e + 1] + 8) >> 4), 0, 255);
                b = Math.Clamp(b + ((current[e + 2] + 8) >> 4), 0, 255);
                var index = palette.IndexOf((byte)r, (byte)g, (byte)b);
                indices[outRow + x] = index;

                var c = colors[index];
                for (var ch = 0; ch < 3; ch++)
                {
                    var error = (ch == 0 ? r - c.Red : ch == 1 ? g - c.Green : b - c.Blue);
                    var at = e + ch;
                    current[at + 3] += error * 7;
                    below[at - 3] += error * 3;
                    below[at] += error * 5;
                    below[at + 3] += error;
                }
            }

            (current, below) = (below, current);
        }
    }

    private static void AddU16(List<byte> bytes, int value)
    {
        bytes.Add((byte)value);
        bytes.Add((byte)(value >> 8));
    }
}
