using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using CustomCoverArt.Services.Rendering;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using SkiaSharp;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// Edge cases of the SKCodec decode guard beyond the ported 4.1.3.0 tests: inputs that are
/// empty, short, mislabelled, truncated or random must give null (never an exception), budgets
/// hold before any pixels exist, and every bitmap that comes out has the one pixel layout the
/// renderer and the bridge assume.
/// </summary>
public class DecodeGuardTests
{
    private static ILocalizationService Loc()
    {
        var loc = Substitute.For<ILocalizationService>();
        loc.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        return loc;
    }

    private static Task<ValidationResult> UploadAsync(byte[] bytes, string name = "x.png")
        => new ImageProcessingService(Loc()).ValidateFileAsync(new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name));

    private static T WithFile<T>(byte[] bytes, string ext, Func<string, T> body)
    {
        var path = TestImages.TempPath(ext);
        File.WriteAllBytes(path, bytes);
        try { return body(path); }
        finally { File.Delete(path); }
    }

    /// <summary>A GIF of <paramref name="frames"/> 32×32 frames drawn from four fixed colours (so the palette is lossless and identical whatever the frame count).</summary>
    private static byte[] FourColourGif(int frames, int seed = 7)
    {
        var colours = new[] { SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.White };
        var bitmaps = new List<SKBitmap>();
        try
        {
            var rng = new Random(seed);
            for (var f = 0; f < frames; f++)
            {
                var bmp = RenderSurface.New(32, 32);
                for (var y = 0; y < 32; y++)
                {
                    for (var x = 0; x < 32; x++)
                    {
                        // Every frame uses all four colours, so the global palette never changes.
                        bmp.SetPixel(x, y, x < 4 ? colours[y % 4] : colours[rng.Next(4)]);
                    }
                }

                bitmaps.Add(bmp);
            }

            using var ms = new MemoryStream();
            GifWriter.Write(ms, bitmaps.Select(b => new GifFrame(b, 5)).ToList(), loop: true);
            return ms.ToArray();
        }
        finally
        {
            bitmaps.ForEach(b => b.Dispose());
        }
    }

    private static void AssertCanonical(DecodedImage decoded)
    {
        Assert.NotEmpty(decoded.Frames);
        Assert.Equal(decoded.Frames.Count, decoded.FrameDurationsMs.Count);
        Assert.Same(decoded.Frames[0], decoded.RootFrame);
        foreach (var frame in decoded.Frames)
        {
            Assert.Equal(SKColorType.Rgba8888, frame.ColorType);
            Assert.Equal(SKAlphaType.Premul, frame.AlphaType);
            Assert.Null(frame.ColorSpace);
            Assert.Equal(decoded.Width, frame.Width);
            Assert.Equal(decoded.Height, frame.Height);
            Assert.Equal(frame.Width * 4, frame.RowBytes);
        }
    }

    /// <summary>A PNG whose IHDR says 65535×65535 is measured from its header and refused before a pixel buffer exists.</summary>
    [Fact]
    public void OversizePngHeader_IsMeasuredButNeverDecoded()
    {
        var bytes = TestImages.PngWithValidIhdr(65535, 65535, new byte[64]);

        var probe = SafeImageLoader.TryProbe(bytes);
        Assert.NotNull(probe);
        Assert.Equal(new ImageProbe(ImageKind.Png, 65535, 65535, 1), probe);
        Assert.False(SafeImageLoader.IsWithinBudget(probe!, maxFrames: 1));
        Assert.False(SafeImageLoader.CanDecodeFirstFrame(bytes));
        Assert.Null(SafeImageLoader.TryDecode(bytes, maxFrames: 1));
    }

    /// <summary>The budget boundary is inclusive: exactly W×H pixels decodes, one less does not.</summary>
    [Fact]
    public void Budget_IsInclusiveAtExactlyOneFrame()
    {
        using var bmp = TestImages.NoiseBitmap(10, 10, seed: 1);
        var bytes = TestImages.SkiaPng(bmp);

        using (var ok = SafeImageLoader.TryDecode(bytes, maxFrames: 1, pixelBudget: 100))
        {
            Assert.NotNull(ok);
        }

        Assert.Null(SafeImageLoader.TryDecode(bytes, maxFrames: 1, pixelBudget: 99));
    }

    /// <summary>An animation that declares more frames than the cap decodes exactly the cap.</summary>
    [Fact]
    public void AnimatedGif_MoreFramesThanTheCap_DecodesOnlyTheCap()
    {
        var bytes = TestImages.GifWithScreenFrames(16, 16, 70);

        var probe = SafeImageLoader.TryProbe(bytes);
        Assert.NotNull(probe);
        Assert.Equal(70, probe!.FrameCount);
        Assert.Equal(SafeImageLoader.MaxAnimatedFrames, SafeImageLoader.FramesToDecode(probe, SafeImageLoader.MaxAnimatedFrames));

        using var decoded = WithFile(bytes, ".gif", p => SafeImageLoader.TryDecode(p, SafeImageLoader.MaxAnimatedFrames));
        Assert.NotNull(decoded);
        Assert.Equal(SafeImageLoader.MaxAnimatedFrames, decoded!.Frames.Count);
        AssertCanonical(decoded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(11)]
    public async Task EmptyOrShorterThanAHeader_IsRefusedEverywhere(int length)
    {
        var png = File.ReadAllBytes(TestImages.Fixture("icc-valid.png"));
        var bytes = png.Take(length).ToArray();

        Assert.Null(SafeImageLoader.TryProbe(bytes));
        Assert.False(SafeImageLoader.CanDecodeFirstFrame(bytes));
        Assert.Null(SafeImageLoader.TryDecode(bytes, maxFrames: 1));
        Assert.Null(WithFile(bytes, ".png", p => SafeImageLoader.TryProbe(p)));
        Assert.Null(WithFile(bytes, ".png", p => SafeImageLoader.TryDecode(p, 1)));

        if (length > 0)
        {
            var result = await UploadAsync(bytes);
            Assert.False(result.IsValid);
            Assert.Equal("errors.invalid_file_format", result.ErrorMessage);
        }
    }

    /// <summary>A real WebP with its inner fourcc changed is a RIFF container of something else: refused.</summary>
    [Fact]
    public async Task WebpWithWrongInnerFourcc_IsRefused()
    {
        var bytes = File.ReadAllBytes(TestImages.Fixture("icc-valid.webp"));
        Assert.True(SafeImageLoader.CanDecodeFirstFrame(bytes)); // sanity: the original decodes
        "WAVE"u8.CopyTo(bytes.AsSpan(8));

        Assert.Null(ImageSignature.Detect(bytes));
        Assert.Null(SafeImageLoader.TryProbe(bytes));
        Assert.False(SafeImageLoader.CanDecodeFirstFrame(bytes));
        Assert.Null(SafeImageLoader.TryDecode(bytes, 1));
        Assert.False((await UploadAsync(bytes, "x.webp")).IsValid);
    }

    /// <summary>A PNG signature in front of a JPEG body: sniffed as PNG, so the JPEG codec never gets it, and the PNG codec refuses it.</summary>
    [Fact]
    public async Task PngSignatureFollowedByJpegBody_IsRefused()
    {
        var jpeg = File.ReadAllBytes(TestImages.Fixture("icc-valid.jpg"));
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.Concat(jpeg).ToArray();

        Assert.Equal(ImageKind.Png, ImageSignature.Detect(bytes));
        Assert.Null(SafeImageLoader.TryProbe(bytes));
        Assert.False(SafeImageLoader.CanDecodeFirstFrame(bytes));
        Assert.Null(SafeImageLoader.TryDecode(bytes, 1));
        var result = await UploadAsync(bytes);
        Assert.False(result.IsValid);
        Assert.Equal("errors.invalid_file_format", result.ErrorMessage);
    }

    public static TheoryData<string> TruncatableStills => new() { "png", "jpeg", "webp", "gif" };

    /// <summary>
    /// A still cut off half-way through its pixel data still has a readable header, but frame 0
    /// comes back IncompleteInput: no half image is ever returned, and upload refuses it.
    /// </summary>
    [Theory]
    [MemberData(nameof(TruncatableStills))]
    public async Task TruncatedFirstFrame_IsNull(string format)
    {
        byte[] full;
        using (var bmp = TestImages.NoiseBitmap(64, 64, seed: 3))
        {
            full = format switch
            {
                "png" => TestImages.SkiaPng(bmp),
                "jpeg" => bmp.Encode(SKEncodedImageFormat.Jpeg, 90).ToArray(),
                "webp" => bmp.Encode(SKEncodedImageFormat.Webp, 90).ToArray(),
                _ => GifOf(bmp),
            };
        }

        using (var whole = SafeImageLoader.TryDecode(full, 1))
        {
            Assert.NotNull(whole); // sanity: the untruncated file decodes
        }

        var cut = full.Take(full.Length / 2).ToArray();
        Assert.NotNull(SafeImageLoader.TryProbe(cut)); // header intact
        Assert.Null(SafeImageLoader.TryDecode(cut, 1));
        Assert.False(SafeImageLoader.CanDecodeFirstFrame(cut));
        var result = await UploadAsync(cut);
        Assert.False(result.IsValid);
        Assert.Equal("errors.invalid_file_format", result.ErrorMessage);
    }

    private static byte[] GifOf(SKBitmap bmp)
    {
        using var ms = new MemoryStream();
        GifWriter.Write(ms, new[] { new GifFrame(bmp, 0) }, loop: false);
        return ms.ToArray();
    }

    /// <summary>
    /// An animation cut off inside its third frame keeps the frames that decoded completely: the
    /// list is truncated, the image is not refused.
    /// </summary>
    [Fact]
    public void TruncatedLaterFrame_TruncatesTheList()
    {
        var two = FourColourGif(2);
        var three = FourColourGif(3);
        var prefix = two.Length - 1; // everything but the trailer
        Assert.Equal(two.Take(prefix), three.Take(prefix)); // the third frame starts right there
        var thirdLength = three.Length - 1 - prefix;
        var cut = three.Take(prefix + (thirdLength / 2)).ToArray();

        using var complete = SafeImageLoader.TryDecode(three, 60);
        Assert.NotNull(complete);
        Assert.Equal(3, complete!.Frames.Count);

        using var decoded = SafeImageLoader.TryDecode(cut, 60);
        Assert.NotNull(decoded);
        Assert.Equal(2, decoded!.Frames.Count);
        AssertCanonical(decoded);
        for (var f = 0; f < 2; f++)
        {
            Assert.Equal(TestPixels.Px(complete.Frames[f], 17, 9), TestPixels.Px(decoded.Frames[f], 17, 9));
        }
    }

    public static TheoryData<string> CanonicalSources => new()
    {
        "icc-valid.jpg", "icc-valid.png", "icc-valid.webp", "apng-2frames.png", "gif-animated", "bmp", "gray8-png", "paletted-gif",
    };

    /// <summary>Whatever the source format, every decoded frame is RGBA8888 premultiplied, tightly packed, with no colour space.</summary>
    [Theory]
    [MemberData(nameof(CanonicalSources))]
    public void EveryDecodedFrame_IsRgba8888PremulWithoutColourSpace(string source)
    {
        byte[] bytes = source switch
        {
            "gif-animated" => FourColourGif(3),
            "bmp" => BmpBytes(),
            "gray8-png" => GrayPng(),
            "paletted-gif" => TestImages.PalettedGifWithTransparentIndex(),
            _ => File.ReadAllBytes(TestImages.Fixture(source)),
        };

        using var decoded = SafeImageLoader.TryDecode(bytes, 60);
        Assert.NotNull(decoded);
        AssertCanonical(decoded!);
    }

    private static byte[] BmpBytes()
    {
        using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(5, 3, SixLabors.ImageSharp.Color.Green);
        using var ms = new MemoryStream();
        SixLabors.ImageSharp.ImageExtensions.SaveAsBmp(img, ms);
        return ms.ToArray();
    }

    private static byte[] GrayPng()
    {
        using var gray = new SKBitmap(new SKImageInfo(7, 5, SKColorType.Gray8, SKAlphaType.Opaque));
        gray.Erase(new SKColor(90, 90, 90));
        return TestImages.SkiaPng(gray);
    }

    /// <summary>Disposing a decoded image releases every frame, and a second Dispose is harmless.</summary>
    [Fact]
    public void Dispose_ReleasesEveryFrame()
    {
        var decoded = SafeImageLoader.TryDecode(FourColourGif(3), 60);
        Assert.NotNull(decoded);
        var frames = decoded!.Frames.ToList();
        Assert.All(frames, f => Assert.NotEqual(IntPtr.Zero, f.Handle));

        decoded.Dispose();
        decoded.Dispose();

        Assert.All(frames, f => Assert.Equal(IntPtr.Zero, f.Handle));
    }

    /// <summary>GIF timing is carried through; a still reports one zero duration.</summary>
    [Fact]
    public void Durations_ComeFromTheCodec()
    {
        using var gif = SafeImageLoader.TryDecode(FourColourGif(3), 60);
        Assert.Equal(new[] { 50, 50, 50 }, gif!.FrameDurationsMs);

        using var still = SafeImageLoader.TryDecode(File.ReadAllBytes(TestImages.Fixture("icc-valid.png")), 60);
        Assert.Equal(new[] { 0 }, still!.FrameDurationsMs);
    }

    /// <summary>
    /// Seeded fuzz: a valid magic prefix followed by random bytes, and real files with random
    /// bytes overwritten or cut short. Whatever comes in, the guard returns a result or null and
    /// never throws; anything it does return has the canonical layout.
    /// </summary>
    [Fact]
    public void RandomBytes_NeverThrow()
    {
        var prefixes = new[]
        {
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
            new byte[] { 0xFF, 0xD8, 0xFF },
            "GIF89a"u8.ToArray(),
            "GIF87a"u8.ToArray(),
            "BM"u8.ToArray(),
            "RIFF\0\0\0\0WEBPVP8 "u8.ToArray(),
        };

        byte[] seedGif = FourColourGif(2);
        byte[] seedPng, seedJpeg, seedWebp;
        using (var bmp = TestImages.NoiseBitmap(12, 12, seed: 5))
        {
            seedPng = TestImages.SkiaPng(bmp);
            seedJpeg = bmp.Encode(SKEncodedImageFormat.Jpeg, 80).ToArray();
            seedWebp = bmp.Encode(SKEncodedImageFormat.Webp, 80).ToArray();
        }

        var seeds = new[] { seedGif, seedPng, seedJpeg, seedWebp, BmpBytes(), TestImages.PalettedGifWithTransparentIndex() };
        var rng = new Random(20261112);
        var decodedCount = 0;
        for (var i = 0; i < 400; i++)
        {
            byte[] input;
            if (i % 2 == 0)
            {
                var prefix = prefixes[rng.Next(prefixes.Length)];
                input = new byte[prefix.Length + rng.Next(0, 96)];
                prefix.CopyTo(input, 0);
                for (var k = prefix.Length; k < input.Length; k++)
                {
                    input[k] = (byte)rng.Next(256);
                }
            }
            else
            {
                var seed = seeds[rng.Next(seeds.Length)];
                input = seed.Take(rng.Next(1, seed.Length + 1)).ToArray();
                var flips = rng.Next(1, 6);
                for (var k = 0; k < flips; k++)
                {
                    // Keep the magic intact so the bytes reach the codec.
                    var at = rng.Next(Math.Min(12, input.Length), Math.Max(13, input.Length));
                    if (at < input.Length)
                    {
                        input[at] = (byte)rng.Next(256);
                    }
                }
            }

            var probe = SafeImageLoader.TryProbe(input);
            var canDecode = SafeImageLoader.CanDecodeFirstFrame(input);
            using var decoded = SafeImageLoader.TryDecode(input, 8, pixelBudget: 4096L * 4096L);
            if (decoded is not null)
            {
                decodedCount++;
                Assert.NotNull(probe);
                AssertCanonical(decoded);
            }

            if (canDecode)
            {
                Assert.NotNull(probe);
            }
        }

        // The loop must actually exercise the decoders, not only the signature check.
        Assert.True(decodedCount > 0, "no fuzz input decoded");
    }
}
