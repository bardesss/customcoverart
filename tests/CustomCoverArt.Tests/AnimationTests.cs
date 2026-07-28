using CustomCoverArt.Services;
using MediaBrowser.Common.Configuration;
using NSubstitute;
using SixLabors.ImageSharp;
using Xunit;

namespace CustomCoverArt.Tests;

internal static class AnimationTestHost
{
    public static (CoverArtService Service, string DataDir) New()
    {
        var img = Substitute.For<IImageProcessingService>();
        var paths = Substitute.For<IApplicationPaths>();
        var dataDir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "cca_anim_" + System.Guid.NewGuid().ToString("N"));
        paths.DataPath.Returns(dataDir);
        var svc = new CoverArtService(
            img, paths, Substitute.For<ILoggingService>(), Substitute.For<IMediaItemService>());
        return (svc, dataDir);
    }

    public static CoverArtService NewCoverArtService() => New().Service;
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

    [Fact]
    public async System.Threading.Tasks.Task AnimatedGifBackground_PassesThroughItsFrames()
    {
        var (svc, dataDir) = AnimationTestHost.New();

        // The background must live inside the plugin's sandbox to be honoured.
        var uploads = System.IO.Path.Combine(dataDir, "customcoverart", "uploads");
        System.IO.Directory.CreateDirectory(uploads);
        var gifPath = System.IO.Path.Combine(uploads, "src.gif");

        using (var f2 = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(80, 80, Color.Green))
        using (var f3 = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(80, 80, Color.Blue))
        using (var gif = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(80, 80, Color.Red))
        {
            gif.Frames.AddFrame(f2.Frames.RootFrame);
            gif.Frames.AddFrame(f3.Frames.RootFrame);
            gif.SaveAsGif(gifPath);
        }

        var settings = new CustomCoverArt.Models.CoverArtSettings
        {
            Title = "X",
            ExportWidth = 100,
            ExportHeight = 100,
            OutputFormat = "gif",
            BackgroundSource = "upload",
            BackgroundImagePath = gifPath,
            // Request 20 frames — a true passthrough should ignore that and use the
            // source's own 3 frames instead of truncating/padding.
            Animation = new CustomCoverArt.Models.AnimationSettings
            {
                Enabled = true, KenBurns = false, FrameCount = 20, DelayMs = 80, Loop = true
            }
        };

        var path = await svc.GenerateCoverArtAsync(settings);

        Assert.True(System.IO.File.Exists(path));
        using var outImg = SixLabors.ImageSharp.Image.Load(path);
        Assert.Equal(3, outImg.Frames.Count);

        try { System.IO.File.Delete(path); } catch { }
        try { System.IO.Directory.Delete(dataDir, true); } catch { }
    }

    [Fact]
    public async System.Threading.Tasks.Task AnimatedGif_CapsOversizedWorkingDimensions()
    {
        var settings = new CustomCoverArt.Models.CoverArtSettings
        {
            Title = "Big",
            ExportWidth = 2000,
            ExportHeight = 1000,
            OutputFormat = "gif",
            BackgroundSource = "upload",
            Animation = new CustomCoverArt.Models.AnimationSettings
            {
                Enabled = true, KenBurns = true, FrameCount = 3, DelayMs = 80, ZoomAmount = 0.2f
            }
        };

        var path = await AnimationTestHost.NewCoverArtService().GenerateCoverArtAsync(settings);

        Assert.True(System.IO.File.Exists(path));
        using var img = SixLabors.ImageSharp.Image.Load(path);
        // Longest side is capped at 1280 (2000 → 1280, 1000 → 640).
        Assert.Equal(1280, img.Width);
        Assert.Equal(640, img.Height);
        Assert.True(img.Frames.Count >= 2);

        try { System.IO.File.Delete(path); } catch { }
    }

    /// <summary>
    /// Regression guard for the font-sizing invariant in DocumentRenderer.RenderTextLayer:
    /// font pixel size MUST be computed as `layer.Size * doc.Canvas.Height` (the
    /// document's declared canvas size), not from the actual pixel buffer height.
    ///
    /// GenerateAnimatedAsync's working-size cap (maxGifSide = 1280) downscales the
    /// working canvas and, via ScaleDocumentForCanvas, points the composed document's
    /// Canvas.Width/Height at that same capped size. Because layer.Size is a fraction
    /// of canvas height (not an absolute pixel value), this keeps the rendered font
    /// proportionally correct automatically. If font sizing were changed to use the
    /// raw pixel-buffer height passed to ComposeDocumentFrame instead of the document's
    /// own declared Canvas.Height, any future caller that composes onto a buffer sized
    /// differently than doc.Canvas would silently mis-size text.
    ///
    /// This test renders the same title/TextSize as a static (non-animated) PNG at
    /// 2000x1000 — the "ground truth" proportion, unaffected by any downscale path —
    /// and as an animated GIF at the same 2000x1000 request (which triggers the 1280
    /// cap). It then measures the rendered glyph's pixel-height fraction of its own
    /// frame in both outputs and asserts they match: the animated/downscaled frame's
    /// text must occupy essentially the same fraction of frame height as the
    /// uncapped static reference. A regression to raw-canvas-height sizing would
    /// shrink the animated fraction by ~36% (1 - 1280/2000), which this test catches.
    /// </summary>
    [Fact]
    public async System.Threading.Tasks.Task AnimatedGif_DownscaledFrame_PreservesProportionalTextSize()
    {
        const string title = "M";
        const int textSize = 200;

        // Ground truth: static (non-animated) render at the full 2000x1000 size.
        // The static path never downscales, so doc.Canvas.Height always equals the
        // actual canvas height here — this fraction is the "correct" proportion.
        var staticSettings = new CustomCoverArt.Models.CoverArtSettings
        {
            Title = title,
            TextSize = textSize,
            TextColor = "#ffffff",
            DimColor = "#000000",
            ExportWidth = 2000,
            ExportHeight = 1000,
            OutputFormat = "png",
        };
        var staticPath = await AnimationTestHost.NewCoverArtService().GenerateCoverArtAsync(staticSettings);
        var staticFraction = MeasureGlyphHeightFraction(staticPath);

        // Same title/TextSize/ExportWidth/ExportHeight, but as an animated GIF —
        // 2000x1000's longest side (2000) exceeds the 1280 cap, so the working
        // frame is downscaled to 1280x640 while ExportHeight (-> doc.Canvas.Height)
        // stays 1000 on the cloned/scaled settings.
        var animatedSettings = new CustomCoverArt.Models.CoverArtSettings
        {
            Title = title,
            TextSize = textSize,
            TextColor = "#ffffff",
            DimColor = "#000000",
            ExportWidth = 2000,
            ExportHeight = 1000,
            OutputFormat = "gif",
            Animation = new CustomCoverArt.Models.AnimationSettings
            {
                Enabled = true, KenBurns = false, FrameCount = 2, DelayMs = 80
            }
        };
        var animatedPath = await AnimationTestHost.NewCoverArtService().GenerateCoverArtAsync(animatedSettings);
        var animatedFraction = MeasureGlyphHeightFraction(animatedPath);

        // Allow generous tolerance for font rendering/antialiasing noise, but a
        // raw-canvas-height regression would shrink the animated fraction to about
        // 0.64x the reference (36% smaller) — well outside this band.
        Assert.True(
            animatedFraction > staticFraction * 0.85,
            $"Downscaled animated glyph fraction {animatedFraction:F3} vs static reference {staticFraction:F3} " +
            "— text shrank more than expected on the capped working frame; font sizing may be using the raw " +
            "canvas height instead of doc.Canvas.Height.");

        try { System.IO.File.Delete(staticPath); } catch { }
        try { System.IO.File.Delete(animatedPath); } catch { }
    }

    /// <summary>Bounding-box height of near-white pixels, as a fraction of image height.</summary>
    private static float MeasureGlyphHeightFraction(string path)
    {
        using var img = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(path);

        int minY = int.MaxValue, maxY = int.MinValue;
        for (int y = 0; y < img.Height; y++)
        {
            for (int x = 0; x < img.Width; x++)
            {
                var p = img[x, y];
                if (p.R > 200 && p.G > 200 && p.B > 200)
                {
                    if (y < minY) { minY = y; }
                    if (y > maxY) { maxY = y; }
                }
            }
        }

        Assert.True(minY <= maxY, $"Expected to find rendered text pixels in {path}.");
        return (maxY - minY + 1) / (float)img.Height;
    }
}
