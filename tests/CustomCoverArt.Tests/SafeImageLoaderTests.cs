using System;
using System.IO;
using System.Threading.Tasks;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using CustomCoverArt.Services.Rendering;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using SkiaSharp;
using Xunit;

namespace CustomCoverArt.Tests;

public class SafeImageLoaderTests
{
    [Fact]
    public void HugeLogicalScreen_IsOverBudget_AndNeverDecoded()
    {
        var path = TestImages.TempPath(".gif");
        File.WriteAllBytes(path, TestImages.GifWithScreen(65535, 65535));
        try
        {
            var info = SafeImageLoader.TryProbe(path);
            Assert.NotNull(info);
            Assert.Equal((65535, 65535), (info!.Width, info.Height));
            Assert.False(SafeImageLoader.IsWithinBudget(info, maxFrames: 1));
            Assert.Null(SafeImageLoader.TryDecode(path, maxFrames: 1));
        }
        finally { File.Delete(path); }
    }

    /// <summary>Frames count against the budget: an over-budget animation is clamped, not refused.</summary>
    [Fact]
    public void FramesCountAgainstTheBudget_AreClampedNotRefused()
    {
        var path = TestImages.TempPath(".gif");
        TestImages.SaveMultiFrameGif(path, 32, 32, frames: 10);
        try
        {
            var info = SafeImageLoader.TryProbe(path);
            Assert.NotNull(info);
            Assert.Equal(10, SafeImageLoader.FrameCount(info!));

            const long budget = 32 * 32 * 5; // room for five frames
            Assert.True(SafeImageLoader.IsWithinBudget(info!, maxFrames: 60, budget));
            Assert.Equal(5, SafeImageLoader.FramesToDecode(info!, maxFrames: 60, budget));
            Assert.Equal(1, SafeImageLoader.FramesToDecode(info!, maxFrames: 1, budget));

            using var loaded = SafeImageLoader.TryDecode(path, maxFrames: 60, budget);
            Assert.NotNull(loaded);
            Assert.Equal(5, loaded!.Frames.Count);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Skia's PNG codec returns only the default image of an APNG, so an APNG background
    /// becomes a still (spec 6.2, accepted change). A plain PNG stays one frame.
    /// </summary>
    [Fact]
    public void Apng_DecodesAsAStill_AndPlainPngStaysOneFrame()
    {
        var apng = TestImages.Fixture("apng-2frames.png");
        var still = TestImages.TempPath(".png");
        TestImages.SaveMultiFrameApng(still, 16, 16, frames: 1);
        var generated = TestImages.TempPath(".png");
        TestImages.SaveMultiFrameApng(generated, 16, 16, frames: 3);
        try
        {
            // The generated 3-frame APNG is a valid PNG to Skia and decodes as a one-frame still (red first).
            Assert.Equal(1, SafeImageLoader.TryProbe(generated)!.FrameCount);
            using var generatedStill = SafeImageLoader.TryDecode(generated, maxFrames: 60);
            Assert.NotNull(generatedStill);
            Assert.Single(generatedStill!.Frames);
            Assert.True(TestPixels.Px(generatedStill.RootFrame, 8, 8).R > 200);

            var probe = SafeImageLoader.TryProbe(apng);
            Assert.NotNull(probe);
            Assert.Equal(ImageKind.Png, probe!.Kind);
            Assert.Equal(1, probe.FrameCount);

            using var animated = SafeImageLoader.TryDecode(apng, maxFrames: 60);
            Assert.NotNull(animated);
            Assert.Single(animated!.Frames);
            Assert.True(TestPixels.Px(animated.RootFrame, 8, 8).R > 200, "The still must be the first (red) frame.");

            using var plain = SafeImageLoader.TryDecode(still, maxFrames: 60);
            Assert.NotNull(plain);
            Assert.Single(plain!.Frames);
        }
        finally { File.Delete(still); File.Delete(generated); }
    }

    /// <summary>A frame that alone exceeds the budget is still refused.</summary>
    [Fact]
    public void SingleFrameOverBudget_IsRefused()
    {
        var path = TestImages.TempPath(".gif");
        TestImages.SaveMultiFrameGif(path, 32, 32, frames: 3);
        try
        {
            var info = SafeImageLoader.TryProbe(path);
            Assert.NotNull(info);
            Assert.False(SafeImageLoader.IsWithinBudget(info!, maxFrames: 60, pixelBudget: 32 * 32 - 1));
            Assert.Null(SafeImageLoader.TryDecode(path, maxFrames: 60, pixelBudget: 32 * 32 - 1));
        }
        finally { File.Delete(path); }
    }

    /// <summary>A long animation whose frames fit individually is accepted at upload.</summary>
    [Fact]
    public async Task Upload_AcceptsLongAnimationWhoseFramesFit()
    {
        // Header-only: 4096x4096 screen x 5 frames = 84M pixels, over the 67M budget, but one frame fits.
        var loc = Substitute.For<ILocalizationService>();
        loc.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        var bytes = TestImages.GifWithScreenFrames(4096, 4096, 5);
        Assert.True(SafeImageLoader.FrameCount(SafeImageLoader.TryProbe(bytes)!) >= 2, "test GIF must declare several frames");
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "long.gif");

        var result = await new ImageProcessingService(loc).ValidateFileAsync(file);

        Assert.True(result.IsValid, result.ErrorMessage);
    }

    [Fact]
    public void DecodesOnlyMaxFrames()
    {
        var path = TestImages.TempPath(".gif");
        TestImages.SaveMultiFrameGif(path, 16, 16, frames: 10);
        try
        {
            using var still = SafeImageLoader.TryDecode(path, maxFrames: 1);
            Assert.NotNull(still);
            Assert.Single(still!.Frames);

            using var some = SafeImageLoader.TryDecode(path, maxFrames: 4);
            Assert.NotNull(some);
            Assert.Equal(4, some!.Frames.Count);
            Assert.True(TestPixels.Px(some.RootFrame, 8, 8).R > 200, "Root frame must be the first (red) frame.");
            Assert.True(TestPixels.Px(some.Frames[1], 8, 8).B > 200, "Second frame must be blue.");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PlainPng_CountsAsOneFrameAndLoads()
    {
        var path = TestImages.TempPath(".png");
        using (var bitmap = RenderSurface.New(20, 20, SKColors.Green)) { File.WriteAllBytes(path, TestImages.SkiaPng(bitmap)); }
        try
        {
            Assert.Equal(1, SafeImageLoader.FrameCount(SafeImageLoader.TryProbe(path)!));
            using var loaded = SafeImageLoader.TryDecode(path, maxFrames: 1);
            Assert.NotNull(loaded);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingOrGarbage_ReturnsNull_NeverThrows()
    {
        Assert.Null(SafeImageLoader.TryDecode("/no/such/file.png", 1));
        Assert.Null(SafeImageLoader.TryProbe("/no/such/file.png"));
        var path = TestImages.TempPath(".png");
        File.WriteAllText(path, "not an image");
        try
        {
            Assert.Null(SafeImageLoader.TryDecode(path, 1));
            Assert.Null(SafeImageLoader.TryProbe(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Upload_RejectsAnImageTooLargeToProcess()
    {
        var loc = Substitute.For<ILocalizationService>();
        loc.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        var bytes = TestImages.GifWithScreen(65535, 65535);
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "bomb.gif");

        var result = await new ImageProcessingService(loc).ValidateFileAsync(file);

        Assert.False(result.IsValid);
        Assert.Equal("errors.image_too_large", result.ErrorMessage);
    }

    /// <summary>An APNG upload stays accepted; it renders as its first frame.</summary>
    [Fact]
    public async Task Upload_AcceptsAnApng()
    {
        var loc = Substitute.For<ILocalizationService>();
        loc.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        var path = TestImages.TempPath(".png");
        TestImages.SaveMultiFrameApng(path, 16, 16, frames: 3);
        try
        {
            var bytes = File.ReadAllBytes(path);
            var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "anim.png");

            var result = await new ImageProcessingService(loc).ValidateFileAsync(file);

            Assert.True(result.IsValid, result.ErrorMessage);
        }
        finally { File.Delete(path); }
    }

    /// <summary>PNG signature + a syntactically valid IHDR chunk header, then <paramref name="tail"/>.</summary>
    private static byte[] PngHeadThen(int width, int height, byte[] tail)
    {
        var head = new byte[]
        {
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            (byte)(width >> 24), (byte)(width >> 16), (byte)(width >> 8), (byte)width,
            (byte)(height >> 24), (byte)(height >> 16), (byte)(height >> 8), (byte)height,
        };
        var all = new byte[head.Length + tail.Length];
        head.CopyTo(all, 0);
        tail.CopyTo(all, head.Length);
        return all;
    }

    private static async Task<ValidationResult> UploadAsync(byte[] bytes)
    {
        var loc = Substitute.For<ILocalizationService>();
        loc.GetString(Arg.Any<string>(), Arg.Any<object[]>()).Returns(ci => (string)ci[0]);
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "x.png");
        return await new ImageProcessingService(loc).ValidateFileAsync(file);
    }

    [Fact]
    public async Task Upload_RejectsPngHeaderFollowedByGarbage()
    {
        var bytes = PngHeadThen(16, 16, new byte[64]);

        var result = await UploadAsync(bytes);

        Assert.False(result.IsValid);
        Assert.Equal("errors.invalid_file_format", result.ErrorMessage);

        var path = TestImages.TempPath(".png");
        File.WriteAllBytes(path, bytes);
        try { Assert.Null(SafeImageLoader.TryDecode(path, maxFrames: 1)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Upload_RejectsPngHeaderPolyglotWithScript()
    {
        var tail = System.Text.Encoding.UTF8.GetBytes("<html><script>alert(document.cookie)</script></html>");

        var result = await UploadAsync(PngHeadThen(16, 16, tail));

        Assert.False(result.IsValid);
        Assert.Equal("errors.invalid_file_format", result.ErrorMessage);
    }

    /// <summary>
    /// A PNG whose IHDR (well-formed, correct CRC) declares 65535×65535. libpng needs a valid
    /// IHDR before the header reader reports a size, so the fixture carries one; the IDAT body is junk.
    /// </summary>
    [Fact]
    public async Task OversizePngHeader_IsTooLarge_AndNeverLoaded()
    {
        var bytes = TestImages.PngWithValidIhdr(65535, 65535, new byte[64]);

        var result = await UploadAsync(bytes);

        Assert.False(result.IsValid);
        Assert.Equal("errors.image_too_large", result.ErrorMessage);

        var path = TestImages.TempPath(".png");
        File.WriteAllBytes(path, bytes);
        try { Assert.Null(SafeImageLoader.TryDecode(path, maxFrames: 1)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TryIdentify_NullBytes_ReturnsNull()
    {
        Assert.Null(SafeImageLoader.TryProbe((byte[])null!));
        Assert.False(SafeImageLoader.CanDecodeFirstFrame(null));
        Assert.Null(SafeImageLoader.TryProbe((string)null!));
        Assert.Null(SafeImageLoader.TryDecode((string)null!, 1));
    }

    /// <summary>A2: a still cover decodes one frame; only an animated export takes up to 60.</summary>
    [Fact]
    public void BackgroundMaxFrames_IsOneUnlessAnimating()
    {
        var doc = new CoverDocument();
        Assert.Equal(1, CoverArtService.BackgroundMaxFrames(doc));

        doc.Background.Animation = new AnimationSettings { Enabled = true };
        Assert.Equal(SafeImageLoader.MaxAnimatedFrames, CoverArtService.BackgroundMaxFrames(doc));
    }

    /// <summary>Guard (passes before and after): a bomb background degrades to the base colour.</summary>
    [Fact]
    public async Task BombBackground_FallsBackToBaseColour()
    {
        var (svc, dataDir) = AnimationTestHost.New();
        try
        {
            var uploads = Path.Combine(dataDir, "customcoverart", "uploads");
            Directory.CreateDirectory(uploads);
            var bg = Path.Combine(uploads, "bomb.gif");
            File.WriteAllBytes(bg, TestImages.GifWithScreen(65535, 65535));

            var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 100, Height = 100, Format = "png" } };
            doc.Background.ImagePath = bg;
            doc.Background.DimColor = "#ff0000";

            var output = await svc.GenerateFromDocumentAsync(doc);
            using var img = SKBitmap.Decode(output);
            var px = img.GetPixel(50, 50);
            Assert.True(px.Red > 200 && px.Green < 50);
        }
        finally { try { Directory.Delete(dataDir, true); } catch { } }
    }

    public static TheoryData<string> UnusualPixelFormats => new() { "gray8-png", "rgba16-png", "paletted-gif" };

    /// <summary>
    /// Review focus 2: sources in pixel formats other than 8-bit RGBA (greyscale PNG, 16-bit
    /// PNG, a paletted GIF with a transparent index) all come out as RGBA8888 with the source colour.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnusualPixelFormats))]
    public void Decode_UnusualPixelFormats_LoadAsRgba(string kind)
    {
        byte[] bytes;
        Rgba expected;
        (int X, int Y) spot = (1, 1);
        switch (kind)
        {
            case "gray8-png":
                using (var gray = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Gray8, SKAlphaType.Opaque)))
                {
                    gray.Erase(new SKColor(128, 128, 128));
                    bytes = TestImages.SkiaPng(gray);
                }

                Assert.Equal(0, bytes[25] & 0x02); // IHDR colour type has no colour bit: really greyscale
                expected = new Rgba(128, 128, 128, 255);
                break;
            case "rgba16-png":
                // Skia 3.119 has no PNG encoding for Rgba16161616; from RgbaF16 it writes a 16-bit PNG.
                using (var deep = new SKBitmap(new SKImageInfo(8, 8, SKColorType.RgbaF16, SKAlphaType.Unpremul)))
                {
                    deep.Erase(new SKColor(200, 100, 50, 255));
                    bytes = TestImages.SkiaPng(deep);
                }

                Assert.Equal(16, bytes[24]); // IHDR bit depth: really 16-bit
                expected = new Rgba(200, 100, 50, 255);
                break;
            default:
                bytes = TestImages.PalettedGifWithTransparentIndex();
                expected = new Rgba(255, 0, 0, 255);
                break;
        }

        var path = TestImages.TempPath(kind.EndsWith("gif", StringComparison.Ordinal) ? ".gif" : ".png");
        File.WriteAllBytes(path, bytes);
        try
        {
            using var decoded = SafeImageLoader.TryDecode(path, maxFrames: 1);
            Assert.NotNull(decoded);
            Assert.Equal(SKColorType.Rgba8888, decoded!.RootFrame.ColorType);
            var px = TestPixels.Px(decoded.RootFrame, spot.X, spot.Y);
            Assert.True(TestPixels.Near(px, expected, 1), $"{kind}: got {px}, expected {expected}");
            if (kind == "paletted-gif")
            {
                Assert.Equal(0, TestPixels.Px(decoded.RootFrame, 3, 1).A);
            }
        }
        finally { File.Delete(path); }
    }
}
