using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using CustomCoverArt.Services.Rendering;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using SkiaSharp;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>The still output path: parallel renders, the PNG it writes, and the save retry.</summary>
public sealed class RenderConcurrencyTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "cca_conc_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    private CoverArtService NewService(ILogger<CoverArtService>? logger = null, IMediaItemService? media = null)
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.DataPath.Returns(_dataDir);
        return new CoverArtService(
            paths,
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CoverArtService>.Instance,
            media ?? Substitute.For<IMediaItemService>());
    }

    private string Uploads()
    {
        var dir = Path.Combine(_dataDir, "customcoverart", "uploads");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static CoverDocument EveryEffectDocument(string fontPath)
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 240, Height = 180, Format = "png" } };
        doc.Background.Gradient = new GradientSettings { IsEnabled = true, Type = GradientType.Linear, StartColor = "#223344", EndColor = "#ccaa88" };
        doc.Effects.Grain.Enabled = true;
        doc.Effects.Vignette.Enabled = true;
        doc.Effects.SoftLight.Enabled = true;
        doc.Effects.Border.Enabled = true;
        doc.Effects.Border.Radius = 10;
        doc.Layers.Add(new CoverLayer
        {
            Type = "text",
            Content = "Hello",
            Size = 0.25f,
            Color = "#ffeecc",
            FontPath = fontPath,
            Shadow = { Enabled = true, Blur = 6, OffsetX = 3, OffsetY = 3 },
            Outline = { Enabled = true, Width = 3, Color = "#102030" },
        });
        return doc;
    }

    private string CopyFont()
    {
        var path = Path.Combine(Uploads(), "sub.ttf");
        File.Copy(FontFixtures.Path("sub.ttf"), path, overwrite: true);
        return path;
    }

    /// <summary>
    /// Runs <paramref name="work"/> on <paramref name="count"/> dedicated threads released together by a barrier
    /// (the pool may have fewer idle threads than that), and reports the most calls in flight at once.
    /// </summary>
    private static (T[] Results, int MaxInFlight) RunTogether<T>(int count, Func<int, T> work)
    {
        var results = new T[count];
        var errors = new Exception?[count];
        var inFlight = 0;
        var maxInFlight = 0;
        using var barrier = new Barrier(count);
        var threads = Enumerable.Range(0, count).Select(i => new Thread(() =>
        {
            try
            {
                barrier.SignalAndWait();
                var now = Interlocked.Increment(ref inFlight);
                int seen;
                while (now > (seen = Volatile.Read(ref maxInFlight)) && Interlocked.CompareExchange(ref maxInFlight, now, seen) != seen)
                {
                }

                try { results[i] = work(i); }
                finally { Interlocked.Decrement(ref inFlight); }
            }
            catch (Exception ex)
            {
                errors[i] = ex;
            }
        })).ToArray();
        foreach (var t in threads) { t.Start(); }
        foreach (var t in threads) { t.Join(); }
        var failure = errors.FirstOrDefault(e => e is not null);
        if (failure is not null)
        {
            throw new AggregateException(errors.Where(e => e is not null).Cast<Exception>());
        }

        return (results, maxInFlight);
    }

    [Fact]
    public void ParallelRenders_ProduceIdenticalBytes()
    {
        var font = CopyFont();
        var services = Enumerable.Range(0, 8).Select(_ => NewService()).ToArray();

        var (outputs, maxInFlight) = RunTogether(8, i => services[i].GenerateFromDocumentAsync(EveryEffectDocument(font)).GetAwaiter().GetResult());

        Assert.True(maxInFlight > 1, $"renders never overlapped (max in flight {maxInFlight})");
        Assert.Equal(8, outputs.Distinct().Count());
        var first = File.ReadAllBytes(outputs[0]);
        Assert.True(first.Length > 100);
        foreach (var path in outputs)
        {
            Assert.True(first.AsSpan().SequenceEqual(File.ReadAllBytes(path)), path);
        }
    }

    [Fact]
    public void ParallelCompose_OnManyThreads_GivesIdenticalPixels()
    {
        var font = CopyFont();
        var reference = Compose(font);

        for (var round = 0; round < 3; round++)
        {
            var (frames, maxInFlight) = RunTogether(8, _ => Compose(font));
            Assert.True(maxInFlight > 1, $"composes never overlapped (max in flight {maxInFlight})");
            foreach (var frame in frames)
            {
                Assert.True(reference.AsSpan().SequenceEqual(frame), "a parallel compose differs from the serial one");
            }
        }

        static byte[] Compose(string fontPath)
        {
            var doc = EveryEffectDocument(fontPath);
            doc.Canvas.Width = 400;
            doc.Canvas.Height = 300;
            using var canvas = RenderSurface.New(400, 300);
            DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);
            return TestPixels.Bytes(canvas);
        }
    }

    [Fact]
    public async Task Render_IsDeterministic()
    {
        var font = CopyFont();
        var svc = NewService();

        var a = await svc.GenerateFromDocumentAsync(EveryEffectDocument(font));
        var b = await svc.GenerateFromDocumentAsync(EveryEffectDocument(font));

        Assert.NotEqual(a, b);
        Assert.True(File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b)));
    }

    [Fact]
    public async Task StillOutput_IsRgbaPngOfTheCanvasSize()
    {
        var svc = NewService();
        var path = await svc.GenerateFromDocumentAsync(EveryEffectDocument(CopyFont()));

        using var codec = SKCodec.Create(path);
        Assert.NotNull(codec);
        Assert.Equal(SKEncodedImageFormat.Png, codec!.EncodedFormat);
        Assert.Equal(240, codec.Info.Width);
        Assert.Equal(180, codec.Info.Height);
        Assert.NotEqual(SKAlphaType.Unknown, codec.Info.AlphaType);
        Assert.StartsWith("\u0089PNG", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path), 0, 4));
    }

    [Fact]
    public async Task StillOutput_OpaqueCanvas_DecodesFullyOpaque()
    {
        var svc = NewService();
        var path = await svc.GenerateFromDocumentAsync(EveryEffectDocument(CopyFont()));

        using var bmp = TestPixels.Load(path);
        var span = bmp.GetPixelSpan();
        for (var i = 3; i < span.Length; i += 4)
        {
            Assert.Equal(255, span[i]);
        }
    }

    [Fact]
    public void Encode_Png_RoundTripsPixelExactThroughSkCodec()
    {
        using var canvas = TestPixels.Painted(37, 23, (x, y) => new SKColor((byte)(x * 7), (byte)(y * 11), (byte)((x * y) % 256), 255));

        var png = CoverArtService.Encode(canvas, "png");

        var file = Path.Combine(_dataDir, "rt.png");
        Directory.CreateDirectory(_dataDir);
        File.WriteAllBytes(file, png);
        using var decoded = TestPixels.Load(file);
        Assert.True(TestPixels.Bytes(canvas).AsSpan().SequenceEqual(TestPixels.Bytes(decoded)));
    }

    [Fact]
    public void Encode_Png_KeepsTranslucentPixelsTranslucent()
    {
        using var canvas = RenderSurface.New(8, 8, new SKColor(200, 100, 50, 128));

        var png = CoverArtService.Encode(canvas, "png");

        using var codec = SKCodec.Create(new MemoryStream(png));
        Assert.NotNull(codec);
        Assert.NotEqual(SKAlphaType.Opaque, codec!.Info.AlphaType);
        var info = new SKImageInfo(8, 8, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        Assert.Equal(SKCodecResult.Success, codec.GetPixels(info, bmp.GetPixels()));
        var px = bmp.GetPixel(4, 4);
        Assert.Equal(128, px.Alpha);
        Assert.InRange(px.Red, 198, 202);
    }

    [Fact]
    public void Encode_Gif_ProducesASingleFrameGifOfTheCanvasSize()
    {
        using var canvas = RenderSurface.New(30, 20, new SKColor(10, 200, 30));

        var gif = CoverArtService.Encode(canvas, "GIF");

        Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(gif, 0, 6));
        using var codec = SKCodec.Create(new MemoryStream(gif));
        Assert.NotNull(codec);
        Assert.Equal(1, codec!.FrameCount);
        Assert.Equal(30, codec.Info.Width);
        Assert.Equal(20, codec.Info.Height);
    }

    [Fact]
    public async Task GifFormatWithoutAnimation_StillWritesAGif()
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 128, Height = 100, Format = "gif" } };

        var path = await NewService().GenerateFromDocumentAsync(doc);

        Assert.EndsWith(".gif", path);
        using var codec = SKCodec.Create(path);
        Assert.Equal(SKEncodedImageFormat.Gif, codec!.EncodedFormat);
        Assert.Equal(128, codec.Info.Width);
    }

    [Fact]
    public async Task CollageBackground_RendersThroughTheService()
    {
        var posterDir = Uploads();
        var poster = Path.Combine(posterDir, "poster.png");
        using (var bmp = RenderSurface.New(100, 150, new SKColor(220, 40, 40)))
        {
            TestPixels.SavePng(bmp, poster);
        }

        var media = Substitute.For<IMediaItemService>();
        media.GetPosterPathsAsync(Arg.Any<string>(), Arg.Any<int>()).Returns(Task.FromResult<IReadOnlyList<string>>(new[] { poster }));
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 400, Height = 300, Format = "png" } };
        doc.Background.Source = BackgroundSources.Collage;
        doc.Background.Collage = new CollageSettings { SourceId = "lib", Density = "sparse", Seed = 1 };
        doc.Background.Dim = 0f;

        var path = await NewService(media: media).GenerateFromDocumentAsync(doc);

        using var bmpOut = TestPixels.Load(path);
        Assert.Equal(400, bmpOut.Width);
        var px = TestPixels.Px(bmpOut, 50, 75);
        Assert.True(px.R > 150 && px.G < 100, $"collage tile expected, got {px}");
    }

    [Fact]
    public async Task GarbageLogo_LogsOneWarningForAStillRender()
    {
        var bad = Path.Combine(Uploads(), "logo.png");
        File.WriteAllBytes(bad, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var log = new CapturingLogger<CoverArtService>();
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 100, Height = 100, Format = "png" } };
        doc.Layers.Add(new CoverLayer { Type = "image", ImagePath = bad, Width = 0.3f, Height = 0.3f });
        doc.Layers.Add(new CoverLayer { Type = "image", ImagePath = Path.Combine(Uploads(), "missing.png"), Width = 0.3f, Height = 0.3f });

        await NewService(log).GenerateFromDocumentAsync(doc);

        // One warning per bad logo, from the up-front check; the renderer never retries.
        Assert.Equal(1, log.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("logo.png")));
        Assert.Equal(1, log.Entries.Count(e => e.Level == LogLevel.Warning && e.Message.Contains("missing.png")));
        Assert.Equal(2, log.Entries.Count(e => e.Level == LogLevel.Warning));
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("skipped"));
    }

    [Fact]
    public async Task GarbageLogo_AnimatedRender_StillLogsOneWarning()
    {
        var bad = Path.Combine(Uploads(), "logo.png");
        File.WriteAllBytes(bad, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var log = new CapturingLogger<CoverArtService>();
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 120, Height = 100, Format = "gif" } };
        // Ken Burns over a real background: every frame is composed, so a logo that is retried
        // per frame would warn once per frame.
        var bg = Path.Combine(Uploads(), "bg.png");
        using (var bgBmp = RenderSurface.New(200, 200, new SKColor(30, 60, 90)))
        {
            TestPixels.SavePng(bgBmp, bg);
        }

        doc.Background.Source = "upload";
        doc.Background.ImagePath = bg;
        doc.Background.Animation = new AnimationSettings { Enabled = true, KenBurns = true, FrameCount = 4, DelayMs = 80 };
        doc.Layers.Add(new CoverLayer { Type = "image", ImagePath = bad, Width = 0.3f, Height = 0.3f });

        await NewService(log).GenerateFromDocumentAsync(doc);

        Assert.Equal(1, log.Entries.Count(e => e.Level == LogLevel.Warning));
    }

    [Fact]
    public async Task GoodLogo_IsNotBlankedByTheUpFrontCheck()
    {
        var logo = Path.Combine(Uploads(), "logo.png");
        using (var bmp = RenderSurface.New(40, 40, new SKColor(255, 0, 0)))
        {
            TestPixels.SavePng(bmp, logo);
        }

        var log = new CapturingLogger<CoverArtService>();
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 100, Height = 100, Format = "png" } };
        doc.Background.DimColor = "#000000";
        doc.Layers.Add(new CoverLayer { Type = "image", ImagePath = logo, X = 0.5f, Y = 0.5f, Width = 0.5f, Height = 0.5f });

        var path = await NewService(log).GenerateFromDocumentAsync(doc);

        using var outBmp = TestPixels.Load(path);
        Assert.Equal(new Rgba(255, 0, 0, 255), TestPixels.Px(outBmp, 50, 50));
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task SaveImageWithRetry_RetriesWhileTheFileIsLockedThenSucceeds()
    {
        Directory.CreateDirectory(_dataDir);
        var target = Path.Combine(_dataDir, "locked.png");
        var bytes = new byte[] { 1, 2, 3, 4 };
        var locked = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        try
        {
            var saving = CoverArtService.SaveImageWithRetryAsync(bytes, target);
            await Task.Delay(50); // well inside the first 100 ms back-off; the last attempt is at 300 ms
            Assert.False(saving.IsCompleted, "should be retrying while the file is locked");
            locked.Dispose();
            await saving;
        }
        finally
        {
            locked.Dispose();
        }

        Assert.Equal(bytes, File.ReadAllBytes(target));
    }

    [Fact]
    public async Task SaveImageWithRetry_GivesUpWhenTheFileStaysLocked()
    {
        Directory.CreateDirectory(_dataDir);
        var target = Path.Combine(_dataDir, "stuck.png");
        using var locked = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);

        await Assert.ThrowsAnyAsync<IOException>(() => CoverArtService.SaveImageWithRetryAsync(new byte[] { 9 }, target));
    }

    [Fact]
    public async Task SaveImageWithRetry_WritesFirstTimeWhenNothingIsWrong()
    {
        Directory.CreateDirectory(_dataDir);
        var target = Path.Combine(_dataDir, "ok.png");

        await CoverArtService.SaveImageWithRetryAsync(new byte[] { 7, 8, 9 }, target);

        Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(target));
    }
}
