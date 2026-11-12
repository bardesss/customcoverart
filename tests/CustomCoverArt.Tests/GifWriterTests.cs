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

    // ---- frame differencing (controller ruling R14) ---------------------------------------

    /// <summary>
    /// Differenced frames decode (composited through RequiredFrame) to exactly the pixels of a
    /// full-canvas encoding of the same frames: partial change, repeat, total change, transparency
    /// appearing and disappearing, and a change touching the canvas corners.
    /// </summary>
    [Fact]
    public void Differencing_DecodesIdenticalToFullFrames()
    {
        using var photo = Photo(64, 48, seed: 4);
        using var patched = Copy(photo, (x, y, c) => x is >= 10 and < 20 && y is >= 30 and < 35 ? new SKColor(250, 10, 200) : c);
        using var other = Photo(64, 48, seed: 9);
        using var holed = Copy(other, (x, y, c) => x is >= 40 and < 50 && y is >= 5 and < 15 ? SKColors.Transparent : c);
        using var corners = Copy(other, (x, y, c) => (x, y) is (0, 0) or (63, 47) ? SKColors.Yellow : c);
        var frames = new[]
        {
            new GifFrame(photo, 5), new GifFrame(patched, 6), new GifFrame(patched, 7), new GifFrame(other, 8),
            new GifFrame(holed, 9), new GifFrame(other, 10), new GifFrame(corners, 11), new GifFrame(holed, 12),
        };

        var differenced = WriteWith(frames, loop: true, differencing: true);
        var full = WriteWith(frames, loop: true, differencing: false);
        Assert.True(differenced.Length < full.Length, $"{differenced.Length} vs {full.Length}");

        var (dc, df) = Decode(differenced);
        var (fc, ff) = Decode(full);
        using (dc)
        using (fc)
        {
            Assert.Equal(frames.Length, dc.FrameCount);
            Assert.Equal(fc.FrameInfo.Select(f => f.Duration), dc.FrameInfo.Select(f => f.Duration));
            Assert.Equal(fc.RepetitionCount, dc.RepetitionCount);
            for (var i = 0; i < frames.Length; i++)
            {
                Assert.True(df[i].GetPixelSpan().SequenceEqual(ff[i].GetPixelSpan()), $"frame {i} differs");
            }
        }

        df.ForEach(f => f.Dispose());
        ff.ForEach(f => f.Dispose());
    }

    /// <summary>A still repeated many times costs about one frame: every repeat is a 1x1 sub-image.</summary>
    [Fact]
    public void Differencing_IdenticalFrames_ShrinkToAboutOneFrame()
    {
        using var photo = Photo(120, 90, seed: 2);
        var one = Write(new[] { new GifFrame(photo, 10) }, loop: true);
        var ten = Write(Enumerable.Repeat(new GifFrame(photo, 10), 10).ToList(), loop: true);

        // Each repeat: GCE (8) + descriptor (10) + a few bytes of LZW.
        Assert.InRange(ten.Length - one.Length, 1, 9 * 30);
        var gif = Parse(ten);
        Assert.Equal(10, gif.Frames.Count);
        Assert.Equal((0, 0, 120, 90), gif.Frames[0].Bounds);
        Assert.All(gif.Frames.Skip(1), f => Assert.Equal((0, 0, 1, 1), f.Bounds));
    }

    /// <summary>The repeated 1x1 frame re-draws the previous index at (0, 0).</summary>
    [Fact]
    public void Differencing_NoChangeFrame_CarriesThePreviousIndex()
    {
        using var bmp = Make(8, 6, (x, _) => x == 0 ? SKColors.Blue : SKColors.Red);
        var gif = Parse(Write(new[] { new GifFrame(bmp, 4), new GifFrame(bmp, 4) }, loop: false));
        var index = LzwDecode(gif.Frames[1].Data, gif.Frames[1].MinCodeSize);
        Assert.Single(index);
        Assert.Equal(SKColors.Blue, gif.Table[index[0]]);
    }

    /// <summary>A change confined to a small region is written as exactly its bounding box.</summary>
    [Fact]
    public void Differencing_SmallChange_WritesItsBoundingBox()
    {
        using var a = Make(60, 40, (_, _) => SKColors.Red);
        using var b = Make(60, 40, (x, y) => (x, y) is (10, 5) or (22, 17) ? SKColors.Lime : SKColors.Red);
        using var c = Make(60, 40, (x, y) => (x, y) is (59, 39) ? SKColors.Blue : (x, y) is (10, 5) or (22, 17) ? SKColors.Lime : SKColors.Red);
        var gif = Parse(Write(new[] { new GifFrame(a, 3), new GifFrame(b, 4), new GifFrame(c, 5) }, loop: true));

        Assert.Equal((0, 0, 60, 40), gif.Frames[0].Bounds);
        Assert.Equal((10, 5, 13, 13), gif.Frames[1].Bounds);
        Assert.Equal((59, 39, 1, 1), gif.Frames[2].Bounds);

        var region = LzwDecode(gif.Frames[1].Data, gif.Frames[1].MinCodeSize);
        Assert.Equal(13 * 13, region.Length);
        Assert.Equal(SKColors.Lime, gif.Table[region[0]]);            // (10, 5)
        Assert.Equal(SKColors.Lime, gif.Table[region[(12 * 13) + 12]]); // (22, 17)
        Assert.Equal(SKColors.Red, gif.Table[region[1]]);
        Assert.Equal(new[] { 3, 4, 5 }, gif.Frames.Select(f => f.Delay));
        Assert.All(gif.Frames, f => Assert.Equal(1, f.Disposal));
    }

    /// <summary>The first frame always covers the canvas, also for a single frame and for repeats.</summary>
    [Fact]
    public void Differencing_FirstFrameIsAlwaysFull()
    {
        using var bmp = Make(7, 5, (_, _) => SKColors.Teal);
        Assert.Equal((0, 0, 7, 5), Parse(Write(bmp)).Frames[0].Bounds);
        Assert.Equal((0, 0, 7, 5), Parse(Write(new[] { new GifFrame(bmp, 1), new GifFrame(bmp, 1) }, loop: true)).Frames[0].Bounds);
    }

    /// <summary>Differencing keeps frame count, every delay (including 0) and the loop flag.</summary>
    [Theory]
    [InlineData(true, -1)]
    [InlineData(false, 0)]
    public void Differencing_KeepsCountDelaysAndLoop(bool loop, int repetition)
    {
        using var photo = Photo(30, 20, seed: 6);
        var delays = new[] { 5, 0, 7, 3, 250 };
        var bytes = Write(delays.Select(d => new GifFrame(photo, d)).ToList(), loop);
        var (codec, frames) = Decode(bytes);
        using (codec)
        {
            Assert.Equal(delays.Length, codec.FrameCount);
            Assert.Equal(delays.Select(d => d * 10), codec.FrameInfo.Select(f => f.Duration));
            Assert.Equal(repetition, codec.RepetitionCount);
        }

        frames.ForEach(f => f.Dispose());
        Assert.Equal(delays, Parse(bytes).Frames.Select(f => f.Delay));
    }

    [Fact]
    public void Differencing_IsDeterministic()
    {
        using var a = Photo(50, 40, seed: 1);
        using var b = Copy(a, (x, y, c) => x > 30 && y < 10 ? SKColors.Orange : c);
        var frames = new[] { new GifFrame(a, 4), new GifFrame(b, 4), new GifFrame(b, 4), new GifFrame(a, 4) };
        Assert.Equal(Write(frames, loop: true), Write(frames, loop: true));
    }

    [Fact]
    public void ChangedBounds_FindsTheTightRectangle()
    {
        var prev = new byte[5 * 4];
        Assert.Equal(new PixelRect(0, 0, 1, 1), GifWriter.ChangedBounds(prev, prev.ToArray(), 5, 4));

        var one = prev.ToArray();
        one[(2 * 5) + 3] = 1;
        Assert.Equal(new PixelRect(3, 2, 1, 1), GifWriter.ChangedBounds(prev, one, 5, 4));

        var spread = prev.ToArray();
        spread[(0 * 5) + 4] = 2;
        spread[(3 * 5) + 1] = 2;
        Assert.Equal(new PixelRect(1, 0, 4, 4), GifWriter.ChangedBounds(prev, spread, 5, 4));

        var all = Enumerable.Repeat((byte)7, 20).ToArray();
        Assert.Equal(new PixelRect(0, 0, 5, 4), GifWriter.ChangedBounds(prev, all, 5, 4));
    }

    // ---- repeated frames (controller ruling R16) -----------------------------------------

    /// <summary>
    /// N references to one bitmap decode exactly like N distinct identical copies (the palette is
    /// sampled once for the references, N times for the copies; uniform weights give the same palette).
    /// </summary>
    [Fact]
    public void Repeats_DecodeLikeDistinctCopies()
    {
        // Under the 1,048,576-pixel sample cap, so copies and references both sample every pixel.
        using var photo = Photo(64, 48, seed: 12);
        var copies = Enumerable.Range(0, 6).Select(_ => Copy(photo, (_, _, c) => c)).ToList();
        try
        {
            var byReference = Write(Enumerable.Repeat(new GifFrame(photo, 7), 6).ToList(), loop: true);
            var byCopy = Write(copies.Select(c => new GifFrame(c, 7)).ToList(), loop: true);

            var (rc, rf) = Decode(byReference);
            var (cc, cf) = Decode(byCopy);
            using (rc)
            using (cc)
            {
                Assert.Equal(6, rc.FrameCount);
                Assert.Equal(cc.FrameInfo.Select(f => f.Duration), rc.FrameInfo.Select(f => f.Duration));
                for (var i = 0; i < 6; i++)
                {
                    Assert.True(rf[i].GetPixelSpan().SequenceEqual(cf[i].GetPixelSpan()), $"frame {i} differs");
                }
            }

            rf.ForEach(f => f.Dispose());
            cf.ForEach(f => f.Dispose());
        }
        finally
        {
            copies.ForEach(c => c.Dispose());
        }
    }

    /// <summary>
    /// Over the sample cap, 6 distinct copies sample with a 6x wider stride than one bitmap, so their
    /// palette can differ slightly; references sample the bitmap once, which is exactly the palette
    /// and pixels of encoding that bitmap alone.
    /// </summary>
    [Fact]
    public void Repeats_OverTheSampleCap_DecodeLikeTheBitmapAlone()
    {
        using var photo = Photo(1100, 1000, seed: 12);
        var alone = Write(new[] { new GifFrame(photo, 7) }, loop: true);
        var repeated = Write(Enumerable.Repeat(new GifFrame(photo, 7), 6).ToList(), loop: true);

        var (ac, af) = Decode(alone);
        var (rc, rf) = Decode(repeated);
        using (ac)
        using (rc)
        {
            Assert.Equal(6, rc.FrameCount);
            Assert.All(rf, f => Assert.True(f.GetPixelSpan().SequenceEqual(af[0].GetPixelSpan())));
        }

        af.ForEach(f => f.Dispose());
        rf.ForEach(f => f.Dispose());
    }

    /// <summary>A bitmap repeated by reference is dithered once; distinct bitmaps each once; A, B, A three times.</summary>
    [Fact]
    public void Repeats_AreDitheredOnce()
    {
        using var a = Photo(40, 30, seed: 3);
        using var b = Photo(40, 30, seed: 4);

        GifWriter.DitherCount = 0;
        Write(Enumerable.Repeat(new GifFrame(a, 5), 30).ToList(), loop: true);
        Assert.Equal(1, GifWriter.DitherCount);

        GifWriter.DitherCount = 0;
        Write(new[] { new GifFrame(a, 5), new GifFrame(b, 5), new GifFrame(a, 5), new GifFrame(a, 5) }, loop: true);
        Assert.Equal(3, GifWriter.DitherCount);

        GifWriter.DitherCount = 0;
        WriteWith(Enumerable.Repeat(new GifFrame(a, 5), 4).ToList(), loop: false, differencing: false);
        Assert.Equal(1, GifWriter.DitherCount);
    }

    /// <summary>The palette samples a repeated bitmap once: the same palette as for that bitmap alone.</summary>
    [Fact]
    public void Build_RepeatedReference_IsSampledOnce()
    {
        using var photo = Photo(1100, 1000, seed: 5);
        var alone = WuQuantizer.Build(new[] { photo });
        var repeated = WuQuantizer.Build(Enumerable.Repeat(photo, 30).ToList());
        Assert.Equal(alone.Colors, repeated.Colors);
        Assert.Equal(alone.TransparentIndex, repeated.TransparentIndex);
    }

    private static byte[] WriteWith(IReadOnlyList<GifFrame> frames, bool loop, bool differencing)
    {
        using var ms = new MemoryStream();
        GifWriter.Write(ms, frames, loop, differencing);
        return ms.ToArray();
    }

    /// <summary>A copy of <paramref name="source"/> with <paramref name="map"/>(x, y, straight colour) applied.</summary>
    private static SKBitmap Copy(SKBitmap source, Func<int, int, SKColor, SKColor> map)
    {
        var px = new SKColor[source.Width, source.Height];
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var (r, g, b, a) = Px(source, x, y);
                px[x, y] = new SKColor((byte)r, (byte)g, (byte)b, (byte)a);
            }
        }

        return Make(source.Width, source.Height, (x, y) => map(x, y, px[x, y]));
    }

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
