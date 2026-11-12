using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CustomCoverArt.Services.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SkiaSharp;
using Xunit;

namespace CustomCoverArt.Tests;

public class GifWriterTests
{
    // ---- brief tests -------------------------------------------------------------------

    [Fact]
    public void Write_RoundTripsFrameCountDelaysAndSize()
    {
        var bytes = WriteRgb(loop: true);
        var (codec, frames) = Decode(bytes);
        using (codec)
        {
            Assert.Equal(3, codec.FrameCount);
            Assert.Equal((40, 30), (codec.Info.Width, codec.Info.Height));
            Assert.Equal(new[] { 100, 200, 70 }, codec.FrameInfo.Select(f => f.Duration));
            Assert.Equal((255, 0, 0, 255), Px(frames[0], 20, 15));
            Assert.Equal((0, 255, 0, 255), Px(frames[1], 20, 15));
            Assert.Equal((0, 0, 255, 255), Px(frames[2], 20, 15));
        }

        frames.ForEach(f => f.Dispose());
    }

    [Theory]
    [InlineData(true, -1)]
    [InlineData(false, 0)]
    public void Write_LoopFlag_MapsToRepetitionCount(bool loop, int expected)
    {
        var (codec, frames) = Decode(WriteRgb(loop));
        using (codec)
        {
            Assert.Equal(expected, codec.RepetitionCount);
        }

        frames.ForEach(f => f.Dispose());
    }

    [Fact]
    public void Write_FewColours_IsLossless()
    {
        // 200 distinct colours spread over the whole cube, tiled over 64x64.
        AssertLossless(64, 64, (x, y) => Distinct((x + y * 64) % 200));
    }

    [Fact]
    public void Write_Photo_StaysWithinQuantisationError()
    {
        using var photo = Photo(128, 128, seed: 3);
        var bytes = Write(photo);
        var (codec, frames) = Decode(bytes);
        using (codec)
        {
            long sum = 0;
            var max = 0;
            for (var y = 0; y < 128; y++)
            {
                for (var x = 0; x < 128; x++)
                {
                    var a = Px(photo, x, y);
                    var b = Px(frames[0], x, y);
                    foreach (var d in new[] { a.R - b.R, a.G - b.G, a.B - b.B })
                    {
                        sum += Math.Abs(d);
                        max = Math.Max(max, Math.Abs(d));
                    }
                }
            }

            var mean = sum / (128.0 * 128 * 3);
            Assert.True(mean <= 6, $"mean channel error {mean:F2}");
            Assert.True(max <= 64, $"max channel error {max}");
        }

        frames.ForEach(f => f.Dispose());
    }

    [Fact]
    public void Write_IsDeterministic()
    {
        using var a = Photo(96, 64, seed: 5);
        using var b = Photo(96, 64, seed: 6);
        var frames = new[] { new GifFrame(a, 4), new GifFrame(b, 9) };
        var first = Write(frames, loop: true);
        var second = Write(frames, loop: true);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Write_ManyCodes_ResetsTheLzwTable()
    {
        var rng = new Random(11);
        var map = new int[256 * 256];
        for (var i = 0; i < map.Length; i++)
        {
            map[i] = rng.Next(256);
        }

        AssertLossless(256, 256, (x, y) => Distinct(map[x + y * 256]), colours: 256);
    }

    [Fact]
    public void Write_SemiTransparentPixels_UseTheTransparentIndex()
    {
        using var bmp = Make(20, 10, (x, _) => x < 10 ? new SKColor(0, 0, 0, 0) : new SKColor(255, 0, 0));
        var (codec, frames) = Decode(Write(bmp));
        using (codec)
        {
            Assert.Equal(0, Px(frames[0], 3, 5).A);
            Assert.Equal((255, 0, 0, 255), Px(frames[0], 15, 5));
        }

        frames.ForEach(f => f.Dispose());
        var gif = Parse(Write(bmp));
        Assert.True(gif.Frames[0].HasTransparency);
    }

    [Fact]
    public void Write_DecodesWithImageSharpToo()
    {
        using var image = SixLabors.ImageSharp.Image.Load(WriteRgb(loop: true));
        Assert.Equal(3, image.Frames.Count);
        Assert.Equal(0, image.Metadata.GetGifMetadata().RepeatCount);
        Assert.Equal(new[] { 10, 20, 7 }, image.Frames.Select(f => f.Metadata.GetGifMetadata().FrameDelay));
    }

    [Fact]
    public void Write_OnePixelFrame_IsValid()
    {
        using var bmp = Make(1, 1, (_, _) => new SKColor(12, 34, 56));
        var (codec, frames) = Decode(Write(bmp));
        using (codec)
        {
            Assert.Equal((1, 1), (codec.Info.Width, codec.Info.Height));
            Assert.Equal((12, 34, 56, 255), Px(frames[0], 0, 0));
        }

        frames.ForEach(f => f.Dispose());
    }

    // ---- palette boundaries -------------------------------------------------------------

    [Fact]
    public void Write_Exactly256Colours_IsLosslessWithoutWu()
    {
        using var bmp = Make(16, 16, (x, y) => Distinct(x + y * 16));
        var palette = WuQuantizer.Build(new[] { bmp });
        Assert.Equal(256, palette.Colors.Count);
        Assert.Equal(-1, palette.TransparentIndex);
        Assert.Equal(8, palette.BitsPerIndex);
        AssertLossless(16, 16, (x, y) => Distinct(x + y * 16), colours: 256);
    }

    [Fact]
    public void Build_257Colours_EngagesWu()
    {
        using var bmp = Make(257, 1, (x, _) => Distinct(x));
        var palette = WuQuantizer.Build(new[] { bmp });
        Assert.InRange(palette.Colors.Count, 2, 256);
        var exact = palette.Colors.Select(c => (int)(uint)c).ToHashSet();
        Assert.Contains(Enumerable.Range(0, 257), i => !exact.Contains((int)(uint)Distinct(i)));
    }

    [Fact]
    public void Write_257Colours_StaysClose()
    {
        using var bmp = Make(257, 3, (x, _) => Distinct(x));
        var (codec, frames) = Decode(Write(bmp));
        using (codec)
        {
            long sum = 0;
            for (var x = 0; x < 257; x++)
            {
                var a = Px(bmp, x, 1);
                var b = Px(frames[0], x, 1);
                sum += Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
            }

            Assert.True(sum / (257.0 * 3) <= 12, $"mean channel error {sum / (257.0 * 3):F2}");
        }

        frames.ForEach(f => f.Dispose());
    }

    [Fact]
    public void Build_255ColoursPlusTransparent_StaysLossless()
    {
        using var bmp = Make(256, 1, (x, _) => x == 255 ? SKColors.Transparent : Distinct(x));
        var palette = WuQuantizer.Build(new[] { bmp });
        Assert.Equal(256, palette.Colors.Count);
        Assert.Equal(255, palette.TransparentIndex);
    }

    [Fact]
    public void Build_256ColoursPlusTransparent_EngagesWuAndKeepsTheSlot()
    {
        using var bmp = Make(257, 1, (x, _) => x == 256 ? SKColors.Transparent : Distinct(x));
        var palette = WuQuantizer.Build(new[] { bmp });
        Assert.True(palette.TransparentIndex >= 0);
        Assert.InRange(palette.Colors.Count, 2, 256);
        Assert.Equal(palette.Colors.Count - 1, palette.TransparentIndex);
    }

    [Fact]
    public void Write_SolidFrame_PadsTheTableAndUsesMinimumCodeSize2()
    {
        using var bmp = Make(9, 7, (_, _) => new SKColor(90, 10, 200));
        var bytes = Write(bmp);
        var gif = Parse(bytes);
        Assert.Equal(1, gif.TableBits);
        Assert.Equal(2, gif.Table.Count);
        Assert.Equal(new SKColor(90, 10, 200), gif.Table[0]);
        Assert.Equal(2, gif.Frames[0].MinCodeSize);
        Assert.All(LzwDecode(gif.Frames[0].Data, 2), i => Assert.Equal(0, i));

        var (codec, frames) = Decode(bytes);
        using (codec)
        {
            Assert.Equal((90, 10, 200, 255), Px(frames[0], 8, 6));
        }

        frames.ForEach(f => f.Dispose());
    }

    [Fact]
    public void Write_AllTransparentFrame_DecodesFullyTransparent()
    {
        using var bmp = Make(12, 5, (_, _) => SKColors.Transparent);
        var gif = Parse(Write(bmp));
        Assert.True(gif.Frames[0].HasTransparency);
        Assert.All(LzwDecode(gif.Frames[0].Data, gif.Frames[0].MinCodeSize), i => Assert.Equal(gif.Frames[0].TransparentIndex, i));

        var (codec, frames) = Decode(Write(bmp));
        using (codec)
        {
            for (var y = 0; y < 5; y++)
            {
                for (var x = 0; x < 12; x++)
                {
                    Assert.Equal(0, Px(frames[0], x, y).A);
                }
            }
        }

        frames.ForEach(f => f.Dispose());
    }

    [Fact]
    public void Write_PremultipliedInput_IsUnpremultiplied()
    {
        // Alpha 200 stays opaque in the GIF; the colour must be the straight colour, not the premultiplied one.
        using var bmp = Make(4, 4, (_, _) => new SKColor(200, 100, 50, 200));
        var (codec, frames) = Decode(Write(bmp));
        using (codec)
        {
            var p = Px(frames[0], 2, 2);
            Assert.Equal(255, p.A);
            Assert.InRange(p.R, 199, 201);
            Assert.InRange(p.G, 99, 101);
            Assert.InRange(p.B, 49, 51);
        }

        frames.ForEach(f => f.Dispose());
    }

    // ---- geometry -----------------------------------------------------------------------

    [Theory]
    [InlineData(1, 37)]
    [InlineData(37, 1)]
    [InlineData(3, 1)]
    [InlineData(1, 300)]
    public void Write_ThinFrames_AreLossless(int w, int h)
        => AssertLossless(w, h, (x, y) => Distinct((x * 7 + y * 3) % 50));

    [Fact]
    public void Write_FrameOfDifferentSize_Throws()
    {
        using var a = Make(4, 4, (_, _) => SKColors.Red);
        using var b = Make(4, 5, (_, _) => SKColors.Red);
        Assert.Throws<ArgumentException>(() => GifWriter.Write(new MemoryStream(), new[] { new GifFrame(a, 1), new GifFrame(b, 1) }, true));
    }

    // ---- stream structure ----------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Write_StreamStructure_FollowsGif89a(bool loop)
    {
        var gif = Parse(WriteRgb(loop));
        Assert.Equal((40, 30), (gif.Width, gif.Height));
        Assert.Equal(loop, gif.HasNetscapeLoop);
        if (loop)
        {
            Assert.Equal(0, gif.LoopCount);
        }

        Assert.Equal(new[] { 10, 20, 7 }, gif.Frames.Select(f => f.Delay));
        Assert.All(gif.Frames, f =>
        {
            Assert.Equal(1, f.Disposal);
            Assert.Equal((0, 0, 40, 30), f.Bounds);
            Assert.False(f.HasLocalTable);
            Assert.False(f.HasTransparency);
        });
    }

    [Fact]
    public void Write_DataSubBlocks_AreFullExceptTheLast()
    {
        using var photo = Photo(200, 150, seed: 8);
        var gif = Parse(Write(photo));
        var lengths = gif.Frames[0].BlockLengths;
        Assert.True(lengths.Count > 2);
        Assert.All(lengths.Take(lengths.Count - 1), l => Assert.Equal(255, l));
        Assert.InRange(lengths[^1], 1, 255);
    }

    [Fact]
    public void Write_IndexStream_DecodesBitExact()
    {
        // An independent LZW decoder over a frame large enough for several table resets.
        var rng = new Random(21);
        var map = new int[300 * 211];
        for (var i = 0; i < map.Length; i++)
        {
            map[i] = rng.Next(100);
        }

        using var bmp = Make(300, 211, (x, y) => Distinct(map[x + y * 300]));
        var gif = Parse(Write(bmp));
        var indices = LzwDecode(gif.Frames[0].Data, gif.Frames[0].MinCodeSize);
        Assert.Equal(map.Length, indices.Length);
        for (var i = 0; i < map.Length; i++)
        {
            Assert.Equal(Distinct(map[i]), gif.Table[indices[i]]);
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-5, 0)]
    [InlineData(65535, 65535)]
    [InlineData(70000, 65535)]
    public void Write_Delay_IsClampedToTheGifRange(int delay, int expected)
    {
        using var bmp = Make(2, 2, (_, _) => SKColors.Red);
        var gif = Parse(Write(new[] { new GifFrame(bmp, delay) }, loop: false));
        Assert.Equal(expected, gif.Frames[0].Delay);
    }

    // ---- validation ---------------------------------------------------------------------

    [Fact]
    public void Write_InvalidArguments_Throw()
    {
        using var ok = Make(2, 2, (_, _) => SKColors.Red);
        var frames = new[] { new GifFrame(ok, 1) };
        Assert.Throws<ArgumentNullException>(() => GifWriter.Write(null!, frames, true));
        Assert.Throws<ArgumentNullException>(() => GifWriter.Write(new MemoryStream(), null!, true));
        Assert.Throws<ArgumentException>(() => GifWriter.Write(new MemoryStream(), Array.Empty<GifFrame>(), true));
        Assert.Throws<ArgumentException>(() => GifWriter.Write(new MemoryStream(), new[] { new GifFrame(null!, 1) }, true));
    }

    [Theory]
    [InlineData(SKColorType.Bgra8888, SKAlphaType.Premul)]
    [InlineData(SKColorType.Rgba8888, SKAlphaType.Unpremul)]
    [InlineData(SKColorType.Rgba8888, SKAlphaType.Opaque)]
    public void Write_WrongPixelFormat_Throws(SKColorType type, SKAlphaType alpha)
    {
        using var bmp = new SKBitmap(new SKImageInfo(2, 2, type, alpha));
        Assert.Throws<ArgumentException>(() => GifWriter.Write(new MemoryStream(), new[] { new GifFrame(bmp, 1) }, true));
    }

    [Fact]
    public void Write_RejectsBadFrameBeforeWritingAnything()
    {
        using var a = Make(4, 4, (_, _) => SKColors.Red);
        using var b = Make(5, 4, (_, _) => SKColors.Red);
        var output = new MemoryStream();
        Assert.Throws<ArgumentException>(() => GifWriter.Write(output, new[] { new GifFrame(a, 1), new GifFrame(b, 1) }, true));
        Assert.Equal(0, output.Length);
    }

    // ---- LZW and palette units -----------------------------------------------------------

    [Theory]
    [InlineData(2, 0)]
    [InlineData(2, 1)]
    [InlineData(2, 50_000)]
    [InlineData(3, 9_000)]
    [InlineData(4, 4_097)]
    [InlineData(5, 20_000)]
    [InlineData(8, 1)]
    [InlineData(8, 100_000)]
    public void Lzw_RoundTripsRandomIndices(int minCodeSize, int count)
    {
        var rng = new Random(minCodeSize * 1000 + count);
        var indices = new byte[count];
        for (var i = 0; i < count; i++)
        {
            // Mix runs and noise so both long strings and table growth are exercised.
            indices[i] = (byte)(i % 97 < 40 ? (i / 97) % (1 << minCodeSize) : rng.Next(1 << minCodeSize));
        }

        var output = new MemoryStream();
        GifLzw.Encode(indices, minCodeSize, output);
        var bytes = output.ToArray();
        Assert.Equal(minCodeSize, bytes[0]);
        var (data, lengths) = ReadSubBlocks(bytes, 1, out var end);
        Assert.Equal(bytes.Length, end);
        Assert.All(lengths.Take(Math.Max(0, lengths.Count - 1)), l => Assert.Equal(255, l));
        Assert.Equal(indices, LzwDecode(data, minCodeSize));
    }

    [Fact]
    public void Lzw_LongSingleRun_RoundTrips()
    {
        var indices = new byte[1_000_000];
        var output = new MemoryStream();
        GifLzw.Encode(indices, 2, output);
        var bytes = output.ToArray();
        var (data, _) = ReadSubBlocks(bytes, 1, out _);
        Assert.Equal(indices, LzwDecode(data, 2));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(9)]
    public void Lzw_MinCodeSizeOutOfRange_Throws(int minCodeSize)
        => Assert.Throws<ArgumentOutOfRangeException>(() => GifLzw.Encode(new byte[] { 0 }, minCodeSize, new MemoryStream()));

    [Fact]
    public void Lzw_IndexOutOfRange_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => GifLzw.Encode(new byte[] { 0, 1, 4 }, 2, new MemoryStream()));

    [Fact]
    public void Palette_IndexOf_MatchesBruteForceNearest()
    {
        using var photo = Photo(128, 128, seed: 13);
        var palette = WuQuantizer.Build(new[] { photo });
        var rng = new Random(17);
        for (var i = 0; i < 20_000; i++)
        {
            var (r, g, b) = ((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
            var best = -1;
            var bestD = int.MaxValue;
            for (var j = 0; j < palette.Colors.Count; j++)
            {
                if (j == palette.TransparentIndex)
                {
                    continue;
                }

                var c = palette.Colors[j];
                var d = (c.Red - r) * (c.Red - r) + (c.Green - g) * (c.Green - g) + (c.Blue - b) * (c.Blue - b);
                if (d < bestD)
                {
                    bestD = d;
                    best = j;
                }
            }

            Assert.Equal(best, palette.IndexOf(r, g, b));
        }
    }

    [Fact]
    public void Palette_IndexOf_NeverReturnsTheTransparentIndex()
    {
        using var bmp = Make(3, 1, (x, _) => x == 0 ? SKColors.Transparent : x == 1 ? SKColors.Black : SKColors.White);
        var palette = WuQuantizer.Build(new[] { bmp });
        Assert.Equal(2, palette.TransparentIndex);
        Assert.NotEqual(2, palette.IndexOf(0, 0, 0));
        Assert.Equal(new SKColor(0, 0, 0), palette.Colors[palette.IndexOf(1, 1, 1)]);
    }

    [Fact]
    public void Build_SamplesEveryFrame()
    {
        using var a = Make(8, 8, (_, _) => SKColors.Red);
        using var b = Make(8, 8, (_, _) => SKColors.Blue);
        var palette = WuQuantizer.Build(new[] { a, b });
        Assert.Equal(new[] { new SKColor(0, 0, 255), new SKColor(255, 0, 0) }, palette.Colors);
    }

    // ---- helpers ------------------------------------------------------------------------

    private static byte[] WriteRgb(bool loop)
    {
        using var r = Make(40, 30, (_, _) => new SKColor(255, 0, 0));
        using var g = Make(40, 30, (_, _) => new SKColor(0, 255, 0));
        using var b = Make(40, 30, (_, _) => new SKColor(0, 0, 255));
        return Write(new[] { new GifFrame(r, 10), new GifFrame(g, 20), new GifFrame(b, 7) }, loop);
    }

    private static byte[] Write(SKBitmap bitmap) => Write(new[] { new GifFrame(bitmap, 10) }, loop: false);

    private static byte[] Write(IReadOnlyList<GifFrame> frames, bool loop)
    {
        using var ms = new MemoryStream();
        GifWriter.Write(ms, frames, loop);
        return ms.ToArray();
    }

    private static void AssertLossless(int w, int h, Func<int, int, SKColor> pixel, int colours = 0)
    {
        using var bmp = Make(w, h, pixel);
        var bytes = Write(bmp);
        var (codec, frames) = Decode(bytes);
        using (codec)
        {
            for (var y = 0; y < h; y++)
            {
                for (var x = 0; x < w; x++)
                {
                    var c = pixel(x, y);
                    Assert.Equal((c.Red, c.Green, c.Blue, (byte)255), ToBytes(Px(frames[0], x, y)));
                }
            }
        }

        frames.ForEach(f => f.Dispose());
        if (colours > 0)
        {
            Assert.Equal(colours, Parse(bytes).Table.Count);
        }
    }

    private static (byte, byte, byte, byte) ToBytes((int R, int G, int B, int A) p) => ((byte)p.R, (byte)p.G, (byte)p.B, (byte)p.A);

    /// <summary>Colour number <paramref name="i"/> of a fixed set of 257+ distinct colours spread over the cube.</summary>
    private static SKColor Distinct(int i) => new((byte)(i * 37 % 256), (byte)(i * 101 % 251), (byte)((i * 13 + i / 7 * 29) % 256));

    private static SKBitmap Photo(int w, int h, int seed)
    {
        var rng = new Random(seed);
        return Make(w, h, (x, y) =>
        {
            int N() => rng.Next(-12, 13);
            return new SKColor(
                Clamp(x * 255 / (w - 1) + N()),
                Clamp(y * 255 / (h - 1) + N()),
                Clamp(((x + y) * 255 / (w + h - 2) / 2) + 64 + N()));
        });
    }

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);

    /// <summary>A premultiplied RGBA8888 bitmap from straight colours (premultiplied like the renderer).</summary>
    private static SKBitmap Make(int w, int h, Func<int, int, SKColor> pixel)
    {
        var bmp = RenderSurface.New(w, h);
        var span = bmp.GetPixelSpan();
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = pixel(x, y);
                var o = y * bmp.RowBytes + x * 4;
                var a = c.Alpha;
                span[o] = (byte)((c.Red * a + 127) / 255);
                span[o + 1] = (byte)((c.Green * a + 127) / 255);
                span[o + 2] = (byte)((c.Blue * a + 127) / 255);
                span[o + 3] = a;
            }
        }

        return bmp;
    }

    /// <summary>Unpremultiplied RGBA of any premul or unpremul RGBA8888 bitmap.</summary>
    private static (int R, int G, int B, int A) Px(SKBitmap bmp, int x, int y)
    {
        var s = bmp.GetPixelSpan();
        var o = y * bmp.RowBytes + x * 4;
        int r = s[o], g = s[o + 1], b = s[o + 2], a = s[o + 3];
        if (bmp.AlphaType == SKAlphaType.Premul && a is > 0 and < 255)
        {
            (r, g, b) = (Math.Min(255, (r * 255 + a / 2) / a), Math.Min(255, (g * 255 + a / 2) / a), Math.Min(255, (b * 255 + a / 2) / a));
        }

        return (r, g, b, a);
    }

    /// <summary>Decodes every frame (composited per <c>RequiredFrame</c>) as unpremultiplied RGBA8888.</summary>
    private static (SKCodec Codec, List<SKBitmap> Frames) Decode(byte[] bytes)
    {
        var codec = SKCodec.Create(new SKMemoryStream(bytes));
        Assert.NotNull(codec);
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var frames = new List<SKBitmap>();
        for (var i = 0; i < codec.FrameCount; i++)
        {
            var required = codec.FrameInfo[i].RequiredFrame;
            var bmp = new SKBitmap(info);
            if (required >= 0)
            {
                frames[required].GetPixelSpan().CopyTo(bmp.GetPixelSpan());
            }
            else
            {
                bmp.Erase(SKColors.Transparent);
            }

            var result = codec.GetPixels(info, bmp.GetPixels(), new SKCodecOptions(i, required));
            Assert.Equal(SKCodecResult.Success, result);
            frames.Add(bmp);
        }

        if (codec.FrameCount == 0)
        {
            var bmp = new SKBitmap(info);
            Assert.Equal(SKCodecResult.Success, codec.GetPixels(info, bmp.GetPixels()));
            frames.Add(bmp);
        }

        return (codec, frames);
    }

    private sealed record ParsedFrame(
        int Disposal, bool HasTransparency, int TransparentIndex, int Delay,
        (int X, int Y, int W, int H) Bounds, bool HasLocalTable, int MinCodeSize, byte[] Data, List<int> BlockLengths);

    private sealed record ParsedGif(
        int Width, int Height, int TableBits, List<SKColor> Table, bool HasNetscapeLoop, int LoopCount, List<ParsedFrame> Frames);

    /// <summary>A strict GIF89a walker: fails on anything this writer must not produce.</summary>
    private static ParsedGif Parse(byte[] b)
    {
        Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(b, 0, 6));
        int U16(int at) => b[at] | (b[at + 1] << 8);
        var width = U16(6);
        var height = U16(8);
        var flags = b[10];
        Assert.True((flags & 0x80) != 0, "global colour table flag");
        Assert.Equal(7, (flags >> 4) & 7);
        var bits = (flags & 7) + 1;
        var table = new List<SKColor>();
        var p = 13;
        for (var i = 0; i < 1 << bits; i++, p += 3)
        {
            table.Add(new SKColor(b[p], b[p + 1], b[p + 2]));
        }

        var netscape = false;
        var loopCount = -1;
        var frames = new List<ParsedFrame>();
        (int Disposal, bool Transparent, int Index, int Delay)? gce = null;
        while (true)
        {
            var introducer = b[p++];
            if (introducer == 0x3B)
            {
                Assert.Equal(b.Length, p);
                break;
            }

            if (introducer == 0x21)
            {
                var label = b[p++];
                if (label == 0xF9)
                {
                    Assert.Equal(4, b[p]);
                    var packed = b[p + 1];
                    gce = ((packed >> 2) & 7, (packed & 1) != 0, b[p + 4], U16(p + 2));
                    Assert.Equal(0, b[p + 5]);
                    p += 6;
                }
                else if (label == 0xFF)
                {
                    Assert.Empty(frames);
                    Assert.Equal(11, b[p]);
                    Assert.Equal("NETSCAPE2.0", System.Text.Encoding.ASCII.GetString(b, p + 1, 11));
                    Assert.Equal(3, b[p + 12]);
                    Assert.Equal(1, b[p + 13]);
                    loopCount = U16(p + 14);
                    Assert.Equal(0, b[p + 16]);
                    netscape = true;
                    p += 17;
                }
                else
                {
                    Assert.Fail($"unexpected extension 0x{label:X2}");
                }

                continue;
            }

            Assert.Equal(0x2C, introducer);
            Assert.NotNull(gce);
            var bounds = (U16(p), U16(p + 2), U16(p + 4), U16(p + 6));
            var local = (b[p + 8] & 0x80) != 0;
            Assert.Equal(0, b[p + 8] & 0x40); // not interlaced
            var minCodeSize = b[p + 9];
            var (data, lengths) = ReadSubBlocks(b, p + 10, out p);
            frames.Add(new ParsedFrame(gce.Value.Disposal, gce.Value.Transparent, gce.Value.Index, gce.Value.Delay, bounds, local, minCodeSize, data, lengths));
            gce = null;
        }

        return new ParsedGif(width, height, bits, table, netscape, loopCount, frames);
    }

    private static (byte[] Data, List<int> Lengths) ReadSubBlocks(byte[] b, int p, out int end)
    {
        var data = new List<byte>();
        var lengths = new List<int>();
        while (b[p] != 0)
        {
            var n = b[p];
            lengths.Add(n);
            data.AddRange(b.AsSpan(p + 1, n).ToArray());
            p += 1 + n;
        }

        end = p + 1;
        return (data.ToArray(), lengths);
    }

    /// <summary>An independent GIF LZW decoder; strict about invalid codes and trailing data.</summary>
    private static byte[] LzwDecode(byte[] data, int minCodeSize)
    {
        var clear = 1 << minCodeSize;
        var eoi = clear + 1;
        var prefix = new int[4096];
        var suffix = new byte[4096];
        var first = new byte[4096];
        var length = new int[4096];
        for (var i = 0; i < clear; i++)
        {
            suffix[i] = first[i] = (byte)i;
            length[i] = 1;
        }

        var output = new List<byte>();
        var width = minCodeSize + 1;
        var next = eoi + 1;
        var prev = -1;
        long bitPos = 0;
        var sawClearFirst = false;
        var stack = new byte[4096];
        while (true)
        {
            Assert.True(bitPos + width <= data.Length * 8L, "ran out of data before the end code");
            var code = 0;
            for (var i = 0; i < width; i++, bitPos++)
            {
                code |= ((data[bitPos >> 3] >> (int)(bitPos & 7)) & 1) << i;
            }

            if (!sawClearFirst)
            {
                Assert.Equal(clear, code);
                sawClearFirst = true;
            }

            if (code == clear)
            {
                width = minCodeSize + 1;
                next = eoi + 1;
                prev = -1;
                continue;
            }

            if (code == eoi)
            {
                break;
            }

            if (prev == -1)
            {
                Assert.True(code < clear, $"first code {code} after clear is not a literal");
                output.Add((byte)code);
                prev = code;
                continue;
            }

            Assert.True(code <= next && code != clear && code != eoi, $"invalid code {code} (next {next})");
            var firstChar = code < next ? first[code] : first[prev];
            if (next < 4096)
            {
                prefix[next] = prev;
                suffix[next] = firstChar;
                first[next] = first[prev];
                length[next] = length[prev] + 1;
                next++;
                if (next == 1 << width && width < 12)
                {
                    width++;
                }
            }

            var n = length[code];
            for (int c = code, k = n - 1; k >= 0; k--)
            {
                stack[k] = suffix[c];
                c = prefix[c];
            }

            output.AddRange(stack.AsSpan(0, n).ToArray());
            prev = code;
        }

        Assert.Equal((bitPos + 7) / 8, data.Length); // nothing after the end code but padding
        return output.ToArray();
    }
}
