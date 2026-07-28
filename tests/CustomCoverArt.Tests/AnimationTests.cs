using CustomCoverArt.Services;
using MediaBrowser.Common.Configuration;
using NSubstitute;
using SixLabors.ImageSharp;
using Xunit;

namespace CustomCoverArt.Tests;

internal static class AnimationTestHost
{
    public static CoverArtService NewCoverArtService()
    {
        var img = Substitute.For<IImageProcessingService>();
        var paths = Substitute.For<IApplicationPaths>();
        paths.DataPath.Returns(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "cca_anim_" + System.Guid.NewGuid().ToString("N")));
        return new CoverArtService(
            img, paths, Substitute.For<ILoggingService>(), Substitute.For<IMediaItemService>());
    }
}

public class AnimationTests
{
    [Fact]
    public void KenBurnsCrop_ZoomIn_StartsFullFrame()
    {
        var r = CoverArtService.KenBurnsCrop(1000, 1000, 0f, 0.2f, "in");
        Assert.Equal(0, r.X);
        Assert.Equal(0, r.Y);
        Assert.Equal(1000, r.Width);
        Assert.Equal(1000, r.Height);
    }

    [Fact]
    public void KenBurnsCrop_ZoomIn_EndsTighterAndCentered()
    {
        var r = CoverArtService.KenBurnsCrop(1000, 1000, 1f, 0.2f, "in");
        // 1/1.2 ≈ 0.8333 → ~833px, centered.
        Assert.InRange(r.Width, 820, 840);
        Assert.InRange(r.Height, 820, 840);
        Assert.True(r.X > 0 && r.Y > 0);
        Assert.Equal(r.X, (1000 - r.Width) / 2);
    }

    [Fact]
    public void KenBurnsCrop_ZoomOut_IsReverseOfZoomIn()
    {
        var inStart = CoverArtService.KenBurnsCrop(1000, 1000, 0f, 0.2f, "out");
        // "out" at t=0 is the tight end.
        Assert.InRange(inStart.Width, 820, 840);
    }

    [Fact]
    public async System.Threading.Tasks.Task GeneratesMultiFrameGif_WithKenBurns()
    {
        // Build settings with a gradient background (no file needed) + Ken Burns.
        var settings = new CustomCoverArt.Models.CoverArtSettings
        {
            Title = "Test",
            ExportWidth = 200,
            ExportHeight = 200,
            OutputFormat = "gif",
            BackgroundSource = "upload",
            Animation = new CustomCoverArt.Models.AnimationSettings
            {
                Enabled = true, KenBurns = true, FrameCount = 6, DelayMs = 80, Loop = true, ZoomAmount = 0.2f
            }
        };

        var svc = AnimationTestHost.NewCoverArtService();
        var path = await svc.GenerateCoverArtAsync(settings);

        Assert.True(System.IO.File.Exists(path));
        using var img = SixLabors.ImageSharp.Image.Load(path);
        Assert.True(img.Frames.Count >= 2);

        try { System.IO.File.Delete(path); } catch { }
    }
}
