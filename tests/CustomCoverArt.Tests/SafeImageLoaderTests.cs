using System;
using System.IO;
using System.Threading.Tasks;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
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
            var info = Image.Identify(path);
            Assert.False(SafeImageLoader.IsWithinBudget(info, maxFrames: 1));
            Assert.Null(SafeImageLoader.TryLoad(path, maxFrames: 1));
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
            var info = Image.Identify(path);
            Assert.Equal(10, SafeImageLoader.FrameCount(info));

            const long budget = 32 * 32 * 5; // room for five frames
            Assert.True(SafeImageLoader.IsWithinBudget(info, maxFrames: 60, budget));
            Assert.Equal(5, SafeImageLoader.FramesToDecode(info, maxFrames: 60, budget));
            Assert.Equal(1, SafeImageLoader.FramesToDecode(info, maxFrames: 1, budget));

            using var loaded = SafeImageLoader.TryLoad(path, maxFrames: 60, budget);
            Assert.NotNull(loaded);
            Assert.Equal(5, loaded!.Frames.Count);
        }
        finally { File.Delete(path); }
    }

    /// <summary>Identify reports no frame metadata for APNG, so an unknown count must not collapse to a still.</summary>
    [Fact]
    public void Apng_KeepsItsFrames_AndPlainPngStaysOneFrame()
    {
        var apng = TestImages.TempPath(".png");
        var still = TestImages.TempPath(".png");
        TestImages.SaveMultiFrameApng(apng, 16, 16, frames: 4);
        TestImages.SaveMultiFrameApng(still, 16, 16, frames: 1);
        try
        {
            using var animated = SafeImageLoader.TryLoad(apng, maxFrames: 60);
            Assert.NotNull(animated);
            Assert.True(animated!.Frames.Count > 1, $"APNG decoded {animated.Frames.Count} frame(s)");

            using var plain = SafeImageLoader.TryLoad(still, maxFrames: 60);
            Assert.NotNull(plain);
            Assert.Equal(1, plain!.Frames.Count);

            // Still bounded by the budget when the declared count is unknown.
            using var clamped = SafeImageLoader.TryLoad(apng, maxFrames: 60, pixelBudget: 16 * 16 * 2);
            Assert.NotNull(clamped);
            Assert.True(clamped!.Frames.Count <= 2);
        }
        finally { File.Delete(apng); File.Delete(still); }
    }

    /// <summary>A frame that alone exceeds the budget is still refused.</summary>
    [Fact]
    public void SingleFrameOverBudget_IsRefused()
    {
        var path = TestImages.TempPath(".gif");
        TestImages.SaveMultiFrameGif(path, 32, 32, frames: 3);
        try
        {
            var info = Image.Identify(path);
            Assert.False(SafeImageLoader.IsWithinBudget(info, maxFrames: 60, pixelBudget: 32 * 32 - 1));
            Assert.Null(SafeImageLoader.TryLoad(path, maxFrames: 60, pixelBudget: 32 * 32 - 1));
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
        Assert.True(SafeImageLoader.FrameCount(Image.Identify(bytes)) >= 2, "test GIF must declare several frames");
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
            using var still = SafeImageLoader.TryLoad(path, maxFrames: 1);
            Assert.NotNull(still);
            Assert.Equal(1, still!.Frames.Count);

            using var some = SafeImageLoader.TryLoadRgba32(path, maxFrames: 4);
            Assert.NotNull(some);
            Assert.Equal(4, some!.Frames.Count);
            Assert.True(some[8, 8].R > 200, "Root frame must be the first (red) frame.");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void PlainPng_CountsAsOneFrameAndLoads()
    {
        var path = TestImages.TempPath(".png");
        using (var img = new Image<Rgba32>(20, 20, Color.Green)) { img.SaveAsPng(path); }
        try
        {
            Assert.Equal(1, SafeImageLoader.FrameCount(Image.Identify(path)));
            using var loaded = SafeImageLoader.TryLoad(path, maxFrames: 1);
            Assert.NotNull(loaded);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingOrGarbage_ReturnsNull_NeverThrows()
    {
        Assert.Null(SafeImageLoader.TryLoad("/no/such/file.png", 1));
        var path = TestImages.TempPath(".png");
        File.WriteAllText(path, "not an image");
        try { Assert.Null(SafeImageLoader.TryLoadRgba32(path, 1)); }
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

    /// <summary>Identify throws on ImageSharp 3.1.12's own APNG output; the upload check must still accept it.</summary>
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
            using var img = Image.Load<Rgba32>(output);
            Assert.True(img[50, 50].R > 200 && img[50, 50].G < 50);
        }
        finally { try { Directory.Delete(dataDir, true); } catch { } }
    }
}
