using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using CustomCoverArt.Services.Rendering;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SkiaSharp;
using Xunit;

namespace CustomCoverArt.Tests;

internal static class AnimationTestHost
{
    public static (CoverArtService Service, string DataDir) New(IMediaItemService? media = null)
    {
        var paths = Substitute.For<IApplicationPaths>();
        var dataDir = Path.Combine(Path.GetTempPath(), "cca_anim_" + Guid.NewGuid().ToString("N"));
        paths.DataPath.Returns(dataDir);
        var svc = new CoverArtService(
            paths, NullLogger<CoverArtService>.Instance, media ?? Substitute.For<IMediaItemService>());
        return (svc, dataDir);
    }

    public static CoverArtService NewCoverArtService() => New().Service;

    /// <summary>The sandboxed uploads folder of a host's data dir (backgrounds must live there).</summary>
    public static string Uploads(string dataDir)
    {
        var uploads = Path.Combine(dataDir, "customcoverart", "uploads");
        Directory.CreateDirectory(uploads);
        return uploads;
    }
}

/// <summary>
/// A GIF render output read back with <see cref="SKCodec"/> (not the engine under test): frame
/// count, per-frame durations, repetition count and every frame composited to the full canvas.
/// </summary>
internal sealed class GifReadBack : IDisposable
{
    private GifReadBack(byte[] bytes, int width, int height, int[] durationsMs, int repetitionCount, List<SKBitmap> frames)
    {
        Bytes = bytes;
        Width = width;
        Height = height;
        DurationsMs = durationsMs;
        RepetitionCount = repetitionCount;
        Frames = frames;
    }

    public byte[] Bytes { get; }

    public int Width { get; }

    public int Height { get; }

    public int FrameCount => Frames.Count;

    public int[] DurationsMs { get; }

    /// <summary>Skia convention: -1 loops forever, 0 plays once.</summary>
    public int RepetitionCount { get; }

    public List<SKBitmap> Frames { get; }

    public bool HasNetscapeBlock => IndexOf(Bytes, "NETSCAPE2.0"u8.ToArray()) >= 0;

    public static GifReadBack Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data) ?? throw new InvalidOperationException($"Not decodable: {path}");
        Assert.Equal(SKEncodedImageFormat.Gif, codec.EncodedFormat);
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var frameInfo = codec.FrameInfo;
        var count = Math.Max(1, codec.FrameCount);
        var frames = new List<SKBitmap>(count);
        for (var i = 0; i < count; i++)
        {
            var required = i < frameInfo.Length ? frameInfo[i].RequiredFrame : -1;
            var bitmap = new SKBitmap(info);
            bitmap.Erase(SKColors.Transparent);
            if (required >= 0)
            {
                frames[required].GetPixelSpan().CopyTo(bitmap.GetPixelSpan());
            }

            var result = codec.GetPixels(info, bitmap.GetPixels(), bitmap.RowBytes, new SKCodecOptions(i, required));
            Assert.Equal(SKCodecResult.Success, result);
            frames.Add(bitmap);
        }

        var durations = frameInfo.Length == 0 ? new[] { 0 } : frameInfo.Select(f => f.Duration).ToArray();
        return new GifReadBack(bytes, info.Width, info.Height, durations, codec.RepetitionCount, frames);
    }

    public void Dispose()
    {
        foreach (var frame in Frames)
        {
            frame.Dispose();
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        return -1;
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
    public async Task GeneratesMultiFrameGif_WithKenBurns()
    {
        // A settings-built document without a background file + Ken Burns.
        var settings = new CoverArtSettings
        {
            Title = "Test",
            ExportWidth = 200,
            ExportHeight = 200,
            OutputFormat = "gif",
            BackgroundSource = "upload",
            Animation = new AnimationSettings
            {
                Enabled = true, KenBurns = true, FrameCount = 6, DelayMs = 80, Loop = true, ZoomAmount = 0.2f
            }
        };

        var svc = AnimationTestHost.NewCoverArtService();
        var path = await svc.GenerateFromDocumentAsync(DocumentMigration.FromSettings(settings));
        try
        {
            Assert.EndsWith(".gif", path);
            using var gif = GifReadBack.Read(path);
            Assert.Equal((200, 200), (gif.Width, gif.Height));
            Assert.Equal(6, gif.FrameCount);
            Assert.All(gif.DurationsMs, d => Assert.Equal(80, d));
            Assert.Equal(-1, gif.RepetitionCount);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public async Task AnimatedGifBackground_PassesThroughItsFrames()
    {
        var (svc, dataDir) = AnimationTestHost.New();
        try
        {
            var gifPath = Path.Combine(AnimationTestHost.Uploads(dataDir), "src.gif");
            TestImages.SaveGif(gifPath, 80, 80, new[] { (SKColors.Red, 0), (SKColors.Lime, 0), (SKColors.Blue, 0) }, loop: true);

            var settings = new CoverArtSettings
            {
                Title = "X",
                ExportWidth = 100,
                ExportHeight = 100,
                OutputFormat = "gif",
                BackgroundSource = "upload",
                BackgroundImagePath = gifPath,
                // Request 20 frames. A true passthrough should ignore that and use the
                // source's own 3 frames instead of truncating/padding.
                Animation = new AnimationSettings
                {
                    Enabled = true, KenBurns = false, FrameCount = 20, DelayMs = 80, Loop = true
                }
            };

            var path = await svc.GenerateFromDocumentAsync(DocumentMigration.FromSettings(settings));

            using var gif = GifReadBack.Read(path);
            Assert.Equal(3, gif.FrameCount);
            // The source frames have no delay of their own, so the document's 80 ms applies.
            Assert.Equal(new[] { 80, 80, 80 }, gif.DurationsMs);
            Assert.Equal(-1, gif.RepetitionCount);
            // Each frame shows its own source frame (dim darkens, but the hue stays).
            AssertDominant(TestPixels.Px(gif.Frames[0], 10, 90), 'R');
            AssertDominant(TestPixels.Px(gif.Frames[1], 10, 90), 'G');
            AssertDominant(TestPixels.Px(gif.Frames[2], 10, 90), 'B');
        }
        finally { try { Directory.Delete(dataDir, true); } catch { } }
    }

    [Fact]
    public async Task AnimatedGif_CapsOversizedWorkingDimensions()
    {
        var settings = new CoverArtSettings
        {
            Title = "Big",
            ExportWidth = 2000,
            ExportHeight = 1000,
            OutputFormat = "gif",
            BackgroundSource = "upload",
            Animation = new AnimationSettings
            {
                Enabled = true, KenBurns = true, FrameCount = 3, DelayMs = 80, ZoomAmount = 0.2f
            }
        };

        var path = await AnimationTestHost.NewCoverArtService().GenerateFromDocumentAsync(DocumentMigration.FromSettings(settings));
        try
        {
            using var gif = GifReadBack.Read(path);
            // Longest side is capped at 1280 (2000 → 1280, 1000 → 640).
            Assert.Equal(1280, gif.Width);
            Assert.Equal(640, gif.Height);
            Assert.Equal(3, gif.FrameCount);
            Assert.All(gif.Frames, f => Assert.Equal((1280, 640), (f.Width, f.Height)));
        }
        finally { try { File.Delete(path); } catch { } }
    }

    /// <summary>
    /// Regression guard for the font-sizing invariant in TextRenderer.RenderTextLayer:
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
    /// 2000x1000 (the "ground truth" proportion, unaffected by any downscale path)
    /// and as an animated GIF at the same 2000x1000 request (which triggers the 1280
    /// cap). It then measures the rendered glyph's pixel-height fraction of its own
    /// frame in both outputs and asserts they match: the animated/downscaled frame's
    /// text must occupy essentially the same fraction of frame height as the
    /// uncapped static reference. A regression to raw-canvas-height sizing would
    /// shrink the animated fraction by ~36% (1 - 1280/2000), which this test catches.
    /// </summary>
    [Fact]
    public async Task AnimatedGif_DownscaledFrame_PreservesProportionalTextSize()
    {
        const string title = "M";
        const int textSize = 200;

        // Ground truth: static (non-animated) render at the full 2000x1000 size.
        var staticSettings = new CoverArtSettings
        {
            Title = title,
            TextSize = textSize,
            TextColor = "#ffffff",
            DimColor = "#000000",
            ExportWidth = 2000,
            ExportHeight = 1000,
            OutputFormat = "png",
        };
        var staticPath = await AnimationTestHost.NewCoverArtService().GenerateFromDocumentAsync(DocumentMigration.FromSettings(staticSettings));
        var staticFraction = MeasureGlyphHeightFraction(staticPath);

        // Same title/TextSize/ExportWidth/ExportHeight, but as an animated GIF: the working
        // frame is downscaled to 1280x640.
        var animatedSettings = new CoverArtSettings
        {
            Title = title,
            TextSize = textSize,
            TextColor = "#ffffff",
            DimColor = "#000000",
            ExportWidth = 2000,
            ExportHeight = 1000,
            OutputFormat = "gif",
            Animation = new AnimationSettings
            {
                Enabled = true, KenBurns = false, FrameCount = 2, DelayMs = 80
            }
        };
        var animatedPath = await AnimationTestHost.NewCoverArtService().GenerateFromDocumentAsync(DocumentMigration.FromSettings(animatedSettings));
        var animatedFraction = MeasureGlyphHeightFraction(animatedPath);

        // A raw-canvas-height regression would shrink the animated fraction to about 0.64x.
        Assert.True(
            animatedFraction > staticFraction * 0.85,
            $"Downscaled animated glyph fraction {animatedFraction:F3} vs static reference {staticFraction:F3}. " +
            "Text shrank more than expected on the capped working frame; font sizing may be using the raw " +
            "canvas height instead of doc.Canvas.Height.");

        try { File.Delete(staticPath); } catch { }
        try { File.Delete(animatedPath); } catch { }
    }

    /// <summary>Bounding-box height of near-white pixels, as a fraction of image height.</summary>
    private static float MeasureGlyphHeightFraction(string path)
    {
        using var img = TestPixels.Load(path);

        int minY = int.MaxValue, maxY = int.MinValue;
        for (int y = 0; y < img.Height; y++)
        {
            for (int x = 0; x < img.Width; x++)
            {
                var p = TestPixels.Px(img, x, y);
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

    [Fact]
    public void KenBurnsCrop_WideSourceSquareCanvas_CropsAtCanvasAspect()
    {
        var wide = CoverArtService.KenBurnsCrop(2000, 1000, 500, 500, 0f, 0.2f, "in");
        Assert.Equal(new PixelRect(500, 0, 1000, 1000), wide);

        var tight = CoverArtService.KenBurnsCrop(2000, 1000, 500, 500, 1f, 0.2f, "in");
        Assert.Equal(833, tight.Width);
        Assert.Equal(833, tight.Height);
        Assert.Equal((2000 - 833) / 2, tight.X);
        Assert.Equal((1000 - 833) / 2, tight.Y);
    }

    [Fact]
    public void KenBurnsCrop_ExtremeStrip_StaysInsideSourceAndNonEmpty()
    {
        foreach (var t in new[] { 0f, 0.5f, 1f })
        {
            var r = CoverArtService.KenBurnsCrop(4000, 10, 100, 1000, t, 1f, "in");
            Assert.True(r.Width >= 1 && r.Height >= 1, $"empty crop at t={t}");
            Assert.True(r.X >= 0 && r.Y >= 0 && r.Right <= 4000 && r.Bottom <= 10, $"crop {r} escapes the source at t={t}");
        }
    }

    /// <summary>
    /// A4: a 2:1 source on a square Ken Burns cover was squashed (whole source resized to the
    /// canvas). With a canvas-aspect crop, the centre square shows only the middle stripe.
    /// </summary>
    [Fact]
    public async Task KenBurns_WideSource_IsCroppedNotSquashed()
    {
        var (svc, dataDir) = AnimationTestHost.New();
        try
        {
            var bgPath = Path.Combine(AnimationTestHost.Uploads(dataDir), "stripes.png");
            using (var bg = TestPixels.Painted(200, 100, (x, _) => x < 50 ? SKColors.Red : x < 150 ? SKColors.Lime : SKColors.Blue))
            {
                TestPixels.SavePng(bg, bgPath);
            }

            var doc = KenBurnsDoc(bgPath, 100, 100, frameCount: 2, zoom: 0f);

            var path = await svc.GenerateFromDocumentAsync(doc);
            using var gif = GifReadBack.Read(path);
            foreach (var frame in gif.Frames)
            {
                var leftEdge = TestPixels.Px(frame, 5, 50);
                Assert.True(leftEdge.G > 150 && leftEdge.R < 100,
                    $"Left edge should be the green middle stripe, got {leftEdge} - background was squashed.");
            }
        }
        finally { try { Directory.Delete(dataDir, true); } catch { } }
    }

    /// <summary>
    /// Ken Burns zooms in over the frames: on a source with a red centre square on blue, the red
    /// area grows from the first frame to the last, and the first frame is the whole source.
    /// </summary>
    [Fact]
    public async Task KenBurns_ZoomIn_GrowsTheCentreAcrossFrames()
    {
        var (svc, dataDir) = AnimationTestHost.New();
        try
        {
            var bgPath = Path.Combine(AnimationTestHost.Uploads(dataDir), "target.png");
            using (var bg = TestPixels.Painted(200, 200, (x, y) => x is >= 60 and < 140 && y is >= 60 and < 140 ? SKColors.Red : SKColors.Blue))
            {
                TestPixels.SavePng(bg, bgPath);
            }

            var doc = KenBurnsDoc(bgPath, 100, 100, frameCount: 4, zoom: 0.5f);
            var path = await svc.GenerateFromDocumentAsync(doc);

            using var gif = GifReadBack.Read(path);
            Assert.Equal(4, gif.FrameCount);
            var red = gif.Frames.Select(f => CountWhere(f, p => p.R > 128 && p.B < 128)).ToList();
            for (var i = 1; i < red.Count; i++)
            {
                Assert.True(red[i] > red[i - 1], $"red area should grow frame by frame: {string.Join(", ", red)}");
            }

            // Frame 0 is the whole 200 px source on 100 px: the 80 px square becomes ~40 px (1600 px²).
            Assert.InRange(red[0], 1400, 1800);
            // The last frame shows the central 133 px window: the square becomes ~60 px (3600 px²).
            Assert.InRange(red[^1], 3300, 3900);
        }
        finally { try { Directory.Delete(dataDir, true); } catch { } }
    }

    /// <summary>
    /// A5 regression guard: a still background without Ken Burns is composed once and repeated,
    /// so every frame is pixel-identical.
    /// </summary>
    [Fact]
    public async Task StaticAnimatedCover_RepeatsOneComposedFrame()
    {
        var settings = new CoverArtSettings
        {
            Title = "S", ExportWidth = 120, ExportHeight = 120, OutputFormat = "gif",
            Animation = new AnimationSettings { Enabled = true, KenBurns = false, FrameCount = 5, DelayMs = 80 }
        };

        var path = await AnimationTestHost.NewCoverArtService().GenerateFromDocumentAsync(DocumentMigration.FromSettings(settings));
        try
        {
            using var gif = GifReadBack.Read(path);
            Assert.Equal(5, gif.FrameCount);
            Assert.All(gif.DurationsMs, d => Assert.Equal(80, d));
            var first = TestPixels.Bytes(gif.Frames[0]);
            for (int f = 1; f < gif.FrameCount; f++)
            {
                Assert.Equal(first, TestPixels.Bytes(gif.Frames[f]));
            }
        }
        finally { try { File.Delete(path); } catch { } }
    }

    // ------------------------------------------------------------- passthrough

    /// <summary>
    /// Review focus 4: a GIF whose second frame covers only part of the canvas (disposal "do not
    /// dispose") must reach the cover composited over the first frame, not as a lone pixel on
    /// transparency.
    /// </summary>
    [Fact]
    public async Task Passthrough_PartialFramesWithDisposal_AreComposited()
    {
        var (svc, dataDir) = AnimationTestHost.New();
        try
        {
            var gifPath = Path.Combine(AnimationTestHost.Uploads(dataDir), "partial.gif");
            File.WriteAllBytes(gifPath, TestImages.GifTwoFramesPartial());

            var doc = PassthroughDoc(gifPath, 100, 100);
            var path = await svc.GenerateFromDocumentAsync(doc);

            using var gif = GifReadBack.Read(path);
            Assert.Equal(2, gif.FrameCount);
            // Source delays (10 cs) are kept.
            Assert.Equal(new[] { 100, 100 }, gif.DurationsMs);

            AssertNear(TestPixels.Px(gif.Frames[0], 37, 37), new Rgba(255, 0, 0, 255), 40);
            AssertNear(TestPixels.Px(gif.Frames[1], 5, 5), new Rgba(255, 0, 0, 255), 40);
            AssertNear(TestPixels.Px(gif.Frames[1], 90, 90), new Rgba(255, 0, 0, 255), 40);
            AssertNear(TestPixels.Px(gif.Frames[1], 37, 80), new Rgba(255, 0, 0, 255), 40);
            // Source pixel (1, 1) of 4 covers 25..50 on a 100 px cover; its centre is (37, 37). The
            // 25x enlargement is a cubic filter, so the lone pixel is soft: blue dominates at its centre.
            AssertDominant(TestPixels.Px(gif.Frames[1], 37, 37), 'B');
            Assert.True(TestPixels.Px(gif.Frames[1], 37, 37).B > 150);
        }
        finally { try { Directory.Delete(dataDir, true); } catch { } }
    }

    /// <summary>
    /// Spec 6.2: only a GIF source supplies its own delay. An animated WebP's frame durations
    /// (500 ms here) are ignored and the document's delay applies, as with ImageSharp.
    /// </summary>
    [Fact]
    public async Task Passthrough_WebpSource_UsesTheDocumentDelay()
    {
        var (svc, dataDir) = AnimationTestHost.New();
        try
        {
            var webpPath = Path.Combine(AnimationTestHost.Uploads(dataDir), "anim.webp");
            File.WriteAllBytes(webpPath, TestImages.AnimatedWebp(16, 16, 500, SKColors.Red, SKColors.Blue));
            using (var probe = SafeImageLoader.TryDecode(webpPath, 60))
            {
                Assert.NotNull(probe);
                Assert.Equal(ImageKind.Webp, probe!.Kind);
                Assert.Equal(new[] { 500, 500 }, probe.FrameDurationsMs);
            }

            var doc = PassthroughDoc(webpPath, 100, 100, delayMs: 70);
            var path = await svc.GenerateFromDocumentAsync(doc);

            using var gif = GifReadBack.Read(path);
            Assert.Equal(2, gif.FrameCount);
            Assert.Equal(new[] { 70, 70 }, gif.DurationsMs);
            AssertNear(TestPixels.Px(gif.Frames[0], 50, 50), new Rgba(255, 0, 0, 255), 40);
            AssertNear(TestPixels.Px(gif.Frames[1], 50, 50), new Rgba(0, 0, 255, 255), 40);
        }
        finally { try { Directory.Delete(dataDir, true); } catch { } }
    }

    /// <summary>
    /// Spec 6.2 (accepted change): Skia decodes only the default image of an APNG, so an APNG
    /// background takes the static-repeat path: every frame is the same picture.
    /// </summary>
    [Fact]
    public async Task ApngBackground_IsTreatedAsAStill()
    {
        var (svc, dataDir) = AnimationTestHost.New();
        try
        {
            var apngPath = Path.Combine(AnimationTestHost.Uploads(dataDir), "anim.png");
            File.Copy(TestImages.Fixture("apng-2frames.png"), apngPath);

            var doc = PassthroughDoc(apngPath, 100, 100);
            doc.Background.Animation!.FrameCount = 4;
            var path = await svc.GenerateFromDocumentAsync(doc);

            using var gif = GifReadBack.Read(path);
            Assert.Equal(4, gif.FrameCount);
            Assert.Equal(new[] { 80, 80, 80, 80 }, gif.DurationsMs);
            var first = TestPixels.Bytes(gif.Frames[0]);
            Assert.All(gif.Frames, f => Assert.Equal(first, TestPixels.Bytes(f)));
            // The still is the APNG's first (red) frame.
            Assert.True(TestPixels.Px(gif.Frames[0], 50, 50).R > 200);
        }
        finally { try { Directory.Delete(dataDir, true); } catch { } }
    }

    /// <summary>A GIF source with per-frame delays keeps each of them; a zero delay takes the document's.</summary>
    [Fact]
    public async Task Passthrough_GifMixedDelays_KeepsEachDelay()
    {
        var (svc, dataDir) = AnimationTestHost.New();
        try
        {
            var gifPath = Path.Combine(AnimationTestHost.Uploads(dataDir), "mixed.gif");
            TestImages.SaveGif(gifPath, 20, 20,
                new[] { (SKColors.Red, 3), (SKColors.Lime, 7), (SKColors.Blue, 25), (SKColors.Yellow, 0), (SKColors.White, 1) },
                loop: false);

            var doc = PassthroughDoc(gifPath, 100, 100, delayMs: 90);
            var path = await svc.GenerateFromDocumentAsync(doc);

            using var gif = GifReadBack.Read(path);
            Assert.Equal(5, gif.FrameCount);
            // 3, 7, 25 cs kept; 0 cs → the document's 9 cs; a source 1 cs passes through unclamped, as before.
            Assert.Equal(new[] { 30, 70, 250, 90, 10 }, gif.DurationsMs);
            // Loop comes from the document (on), never from the source (plays once).
            Assert.Equal(-1, gif.RepetitionCount);
        }
        finally { try { Directory.Delete(dataDir, true); } catch { } }
    }

    /// <summary>A source longer than the 60-frame cap is cut to 60 frames, in order.</summary>
    [Fact]
    public async Task Passthrough_MoreFramesThanTheCap_IsClampedTo60()
    {
        var (svc, dataDir) = AnimationTestHost.New();
        try
        {
            var gifPath = Path.Combine(AnimationTestHost.Uploads(dataDir), "long.gif");
            var frames = Enumerable.Range(0, 70).Select(i => (i % 2 == 0 ? SKColors.Red : SKColors.Blue, 4)).ToArray();
            TestImages.SaveGif(gifPath, 8, 8, frames, loop: true);

            var doc = PassthroughDoc(gifPath, 100, 100);
            var path = await svc.GenerateFromDocumentAsync(doc);

            using var gif = GifReadBack.Read(path);
            Assert.Equal(60, gif.FrameCount);
            Assert.All(gif.DurationsMs, d => Assert.Equal(40, d));
            for (var i = 0; i < 60; i++)
            {
                var px = TestPixels.Px(gif.Frames[i], 50, 50);
                Assert.True(i % 2 == 0 ? px.R > 200 && px.B < 50 : px.B > 200 && px.R < 50, $"frame {i}: {px}");
            }
        }
        finally { try { Directory.Delete(dataDir, true); } catch { } }
    }

    /// <summary>
    /// An animated source smaller (and of another aspect) than the canvas is scaled to cover the
    /// canvas in every frame: each frame is its source colour edge to edge.
    /// </summary>
    [Fact]
    public async Task Passthrough_SmallSource_IsScaledInEveryFrame()
    {
        var (svc, dataDir) = AnimationTestHost.New();
        try
        {
            var gifPath = Path.Combine(AnimationTestHost.Uploads(dataDir), "small.gif");
            var colours = new[] { SKColors.Red, SKColors.Lime, SKColors.Blue };
            TestImages.SaveGif(gifPath, 10, 12, colours.Select(c => (c, 5)).ToArray(), loop: true);

            var doc = PassthroughDoc(gifPath, 100, 150);
            var path = await svc.GenerateFromDocumentAsync(doc);

            using var gif = GifReadBack.Read(path);
            Assert.Equal((100, 150), (gif.Width, gif.Height));
            Assert.Equal(3, gif.FrameCount);
            for (var f = 0; f < 3; f++)
            {
                var want = new Rgba(colours[f].Red, colours[f].Green, colours[f].Blue, 255);
                foreach (var (x, y) in new[] { (0, 0), (99, 0), (0, 149), (99, 149), (50, 75) })
                {
                    AssertNear(TestPixels.Px(gif.Frames[f], x, y), want, 2);
                }
            }
        }
        finally { try { Directory.Delete(dataDir, true); } catch { } }
    }

    // ------------------------------------------------------- delays, counts, loop

    /// <summary>A document delay under 2 cs (20 ms) is raised to 2 cs; others are truncated to whole cs.</summary>
    [Theory]
    [InlineData(0, 20)]
    [InlineData(5, 20)]
    [InlineData(19, 20)]
    [InlineData(20, 20)]
    [InlineData(29, 20)]
    [InlineData(35, 30)]
    [InlineData(1000, 1000)]
    public async Task DocumentDelay_IsClampedToTwoCentiseconds(int delayMs, int expectedMs)
    {
        var doc = StaticDoc(100, 100, frameCount: 2, delayMs: delayMs, loop: true);
        var path = await AnimationTestHost.NewCoverArtService().GenerateFromDocumentAsync(doc);
        try
        {
            using var gif = GifReadBack.Read(path);
            Assert.Equal(new[] { expectedMs, expectedMs }, gif.DurationsMs);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    /// <summary>Ken Burns and still sources use the requested frame count, clamped to 2..30.</summary>
    [Theory]
    [InlineData(-5, 2)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(17, 17)]
    [InlineData(30, 30)]
    [InlineData(31, 30)]
    [InlineData(500, 30)]
    public async Task KenBurns_FrameCount_IsClamped(int requested, int expected)
    {
        var (svc, dataDir) = AnimationTestHost.New();
        try
        {
            var bgPath = Path.Combine(AnimationTestHost.Uploads(dataDir), "bg.png");
            using (var bg = TestPixels.Painted(24, 24, (x, y) => new SKColor((byte)(x * 10), (byte)(y * 10), 90)))
            {
                TestPixels.SavePng(bg, bgPath);
            }

            var doc = KenBurnsDoc(bgPath, 100, 100, frameCount: requested, zoom: 0.3f);
            var path = await svc.GenerateFromDocumentAsync(doc);

            using var gif = GifReadBack.Read(path);
            Assert.Equal(expected, gif.FrameCount);
            Assert.All(gif.DurationsMs, d => Assert.Equal(80, d));
        }
        finally { try { Directory.Delete(dataDir, true); } catch { } }
    }

    [Theory]
    [InlineData(true, -1)]
    [InlineData(false, 0)]
    public async Task Loop_ComesFromTheDocument(bool loop, int expectedRepetitionCount)
    {
        var doc = StaticDoc(100, 100, frameCount: 3, delayMs: 80, loop: loop);
        var path = await AnimationTestHost.NewCoverArtService().GenerateFromDocumentAsync(doc);
        try
        {
            using var gif = GifReadBack.Read(path);
            Assert.Equal(expectedRepetitionCount, gif.RepetitionCount);
            // Loop off writes no NETSCAPE2.0 block at all (plays once), as before.
            Assert.Equal(loop, gif.HasNetscapeBlock);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    /// <summary>A source that loops forever does not make a loop-off cover loop.</summary>
    [Fact]
    public async Task Passthrough_LoopOff_IgnoresTheSourcesLoop()
    {
        var (svc, dataDir) = AnimationTestHost.New();
        try
        {
            var gifPath = Path.Combine(AnimationTestHost.Uploads(dataDir), "looping.gif");
            TestImages.SaveGif(gifPath, 8, 8, new[] { (SKColors.Red, 5), (SKColors.Blue, 5) }, loop: true);

            var doc = PassthroughDoc(gifPath, 100, 100);
            doc.Background.Animation!.Loop = false;
            var path = await svc.GenerateFromDocumentAsync(doc);

            using var gif = GifReadBack.Read(path);
            Assert.Equal(0, gif.RepetitionCount);
            Assert.False(gif.HasNetscapeBlock);
        }
        finally { try { Directory.Delete(dataDir, true); } catch { } }
    }

    // ----------------------------------------------------------- determinism etc.

    [Fact]
    public async Task AnimatedOutput_IsDeterministic()
    {
        var (svc, dataDir) = AnimationTestHost.New();
        try
        {
            var uploads = AnimationTestHost.Uploads(dataDir);
            var bgPath = Path.Combine(uploads, "bg.png");
            using (var bg = TestImages.NoiseBitmap(60, 80, seed: 3))
            {
                TestPixels.SavePng(bg, bgPath);
            }

            var gifPath = Path.Combine(uploads, "src.gif");
            TestImages.SaveGif(gifPath, 30, 30, new[] { (SKColors.Red, 6), (SKColors.Teal, 9), (SKColors.Orange, 12) }, loop: true);

            foreach (var build in new Func<CoverDocument>[] { () => KenBurnsDoc(bgPath, 100, 140, 5, 0.4f), () => PassthroughDoc(gifPath, 100, 140) })
            {
                var one = await svc.GenerateFromDocumentAsync(WithText(build()));
                var two = await svc.GenerateFromDocumentAsync(WithText(build()));
                Assert.NotEqual(one, two);
                Assert.Equal(File.ReadAllBytes(one), File.ReadAllBytes(two));
            }
        }
        finally { try { Directory.Delete(dataDir, true); } catch { } }
    }

    /// <summary>A collage background (a still built from posters) animates like any still: Ken Burns frames.</summary>
    [Fact]
    public async Task CollageBackground_KenBurns_ProducesTheRequestedFrames()
    {
        var media = Substitute.For<IMediaItemService>();
        var (svc, dataDir) = AnimationTestHost.New(media);
        try
        {
            var poster = Path.Combine(AnimationTestHost.Uploads(dataDir), "poster.png");
            using (var bmp = RenderSurface.New(100, 150, new SKColor(220, 40, 40)))
            {
                TestPixels.SavePng(bmp, poster);
            }

            media.GetPosterPathsAsync(Arg.Any<string>(), Arg.Any<int>()).Returns(Task.FromResult<IReadOnlyList<string>>(new[] { poster }));
            var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 160, Height = 120, Format = "gif" } };
            doc.Background.Source = BackgroundSources.Collage;
            doc.Background.Collage = new CollageSettings { SourceId = "lib", Density = "sparse", Seed = 1 };
            doc.Background.Dim = 0f;
            doc.Background.Animation = new AnimationSettings { Enabled = true, KenBurns = true, FrameCount = 3, DelayMs = 60, ZoomAmount = 0.2f };

            var path = await svc.GenerateFromDocumentAsync(doc);

            using var gif = GifReadBack.Read(path);
            Assert.Equal((160, 120), (gif.Width, gif.Height));
            Assert.Equal(3, gif.FrameCount);
            Assert.Equal(new[] { 60, 60, 60 }, gif.DurationsMs);
            var px = TestPixels.Px(gif.Frames[0], 20, 60);
            Assert.True(px.R > 150 && px.G < 100, $"collage tile expected, got {px}");
        }
        finally { try { Directory.Delete(dataDir, true); } catch { } }
    }

    /// <summary>Plan Task 11: the render service builds GIFs on SkiaSharp only, no ImageSharp or bridge copies.</summary>
    [Fact]
    public void CoverArtService_UsesNoImageSharp()
    {
        var source = RepoFiles.Read("Services/CoverArtService.cs");
        Assert.DoesNotContain("SixLabors", source);
        Assert.DoesNotContain("ImageSharpBridge", source);
        Assert.Contains("GifWriter.Write(", source);
    }

    // ------------------------------------------------------------------ helpers

    private static CoverDocument StaticDoc(int w, int h, int frameCount, int delayMs, bool loop)
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = w, Height = h, Format = "gif" } };
        doc.Background.Dim = 0f;
        doc.Background.Animation = new AnimationSettings { Enabled = true, KenBurns = false, FrameCount = frameCount, DelayMs = delayMs, Loop = loop };
        return doc;
    }

    private static CoverDocument KenBurnsDoc(string bgPath, int w, int h, int frameCount, float zoom)
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = w, Height = h, Format = "gif" } };
        doc.Background.ImagePath = bgPath;
        doc.Background.Dim = 0f;
        doc.Background.Animation = new AnimationSettings
        {
            Enabled = true, KenBurns = true, ZoomAmount = zoom, FrameCount = frameCount, DelayMs = 80, Direction = "in", Loop = true
        };
        return doc;
    }

    private static CoverDocument PassthroughDoc(string bgPath, int w, int h, int delayMs = 80)
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = w, Height = h, Format = "gif" } };
        doc.Background.ImagePath = bgPath;
        doc.Background.Dim = 0f;
        doc.Background.Animation = new AnimationSettings { Enabled = true, KenBurns = false, FrameCount = 20, DelayMs = delayMs, Loop = true };
        return doc;
    }

    private static CoverDocument WithText(CoverDocument doc)
    {
        doc.Layers.Add(new CoverLayer { Type = "text", Content = "Ab", Size = 0.2f, Color = "#ffffff" });
        return doc;
    }

    private static int CountWhere(SKBitmap bitmap, Func<Rgba, bool> predicate)
    {
        var n = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (predicate(TestPixels.Px(bitmap, x, y)))
                {
                    n++;
                }
            }
        }

        return n;
    }

    private static void AssertNear(Rgba actual, Rgba expected, int tolerance)
        => Assert.True(TestPixels.Near(actual, expected, tolerance), $"expected ~{expected} (±{tolerance}), got {actual}");

    private static void AssertDominant(Rgba p, char channel)
    {
        var ok = channel switch
        {
            'R' => p.R > p.G + 40 && p.R > p.B + 40,
            'G' => p.G > p.R + 40 && p.G > p.B + 40,
            _ => p.B > p.R + 40 && p.B > p.G + 40,
        };
        Assert.True(ok, $"expected {channel} to dominate, got {p}");
    }
}
