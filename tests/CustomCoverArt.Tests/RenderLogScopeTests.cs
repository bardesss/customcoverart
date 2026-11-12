using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using CustomCoverArt.Services.Rendering;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// The per-render logging scope: a render's warnings reach the logger of the service that started it,
/// the scope ends with the render, and a warn-once warning is never spent where nobody can hear it.
/// </summary>
[Collection("FontStoreState")]
public sealed class RenderLogScopeTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "cca_rls_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    private CoverArtService NewService(ILogger<CoverArtService> logger)
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.DataPath.Returns(_dataDir);
        return new CoverArtService(paths, logger, Substitute.For<IMediaItemService>());
    }

    /// <summary>A file with a supported extension and garbage content, inside the sandboxed fonts folder.</summary>
    private string BrokenFont(string name)
    {
        var dir = Path.Combine(_dataDir, "customcoverart", "fonts");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        return path;
    }

    private static CoverDocument TextDocument(string fontPath)
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 160, Height = 120, Format = "png" } };
        doc.Layers.Add(new CoverLayer { Type = "text", Content = "Hi", Size = 0.3f, Color = "#ffffff", FontPath = fontPath });
        return doc;
    }

    [Fact]
    public void IsActive_OnlyInsideAScope()
    {
        Assert.False(RenderLog.IsActive);
        using (RenderLog.Use(NullLogger.Instance))
        {
            Assert.True(RenderLog.IsActive);
        }

        Assert.False(RenderLog.IsActive);
    }

    [Fact]
    public async Task Generate_RestoresTheCallersScopeWhenItFinishes()
    {
        var outer = new CapturingLogger<RenderLogScopeTests>();
        var service = NewService(new CapturingLogger<CoverArtService>());

        using (RenderLog.Use(outer))
        {
            await service.GenerateFromDocumentAsync(TextDocument(string.Empty));
            Assert.Same(outer, RenderLog.Logger);
        }

        await service.GenerateFromDocumentAsync(TextDocument(string.Empty));
        Assert.False(RenderLog.IsActive, "a finished render must not leave its logger on the caller's flow");
    }

    [Fact]
    public async Task Generate_RestoresTheCallersScopeWhenItFails()
    {
        var outer = new CapturingLogger<RenderLogScopeTests>();
        var service = NewService(new CapturingLogger<CoverArtService>());

        using (RenderLog.Use(outer))
        {
            var invalid = new CoverDocument { Canvas = new CanvasSettings { Width = 0, Height = 0 } };
            await Assert.ThrowsAsync<CoverValidationException>(() => service.GenerateFromDocumentAsync(invalid));
            Assert.Same(outer, RenderLog.Logger);
        }
    }

    /// <summary>Renders on separate threads, released together, each hear only their own font's warning.</summary>
    [Fact]
    public void ConcurrentRenders_EachHearOnlyTheirOwnFontWarning()
    {
        const int renders = 4;
        var fonts = Enumerable.Range(0, renders).Select(i => BrokenFont($"broken_{i}_{Guid.NewGuid():N}.ttf")).ToArray();
        var logs = Enumerable.Range(0, renders).Select(_ => new CapturingLogger<CoverArtService>()).ToArray();
        var services = logs.Select(NewService).ToArray();
        using var barrier = new Barrier(renders);
        var errors = new Exception?[renders];

        var threads = Enumerable.Range(0, renders).Select(i => new Thread(() =>
        {
            try
            {
                barrier.SignalAndWait();
                services[i].GenerateFromDocumentAsync(TextDocument(fonts[i])).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                errors[i] = ex;
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        Assert.All(errors, e => Assert.Null(e));
        for (var i = 0; i < renders; i++)
        {
            var own = Path.GetFileName(fonts[i]);
            var warning = Assert.Single(logs[i].Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("broken_"));
            Assert.Contains(own, warning.Message);
        }
    }

    /// <summary>A lookup outside any render has nobody listening, so it must not spend the path's one warning.</summary>
    [Fact]
    public void UnsupportedFormat_ResolvedOutsideARender_StillWarnsInTheNextRender()
    {
        FontStore.ResetWarningsForTests();
        var path = Path.Combine(_dataDir, "legacy_" + Guid.NewGuid().ToString("N") + ".woff2");

        Assert.False(FontStore.TryGetCustom(path, out _));

        var log = new CapturingLogger<RenderLogScopeTests>();
        using (FontStore.CaptureLogForTests(log))
        {
            Assert.False(FontStore.TryGetCustom(path, out _));
            Assert.False(FontStore.TryGetCustom(path, out _));
        }

        var warning = Assert.Single(log.Entries);
        Assert.Contains("WOFF2", warning.Message);
    }

    [Fact]
    public void MissingFont_ResolvedOutsideARender_StillWarnsInTheNextRender()
    {
        FontStore.ResetWarningsForTests();
        var path = Path.Combine(_dataDir, "gone_" + Guid.NewGuid().ToString("N") + ".ttf");

        Assert.False(FontStore.TryGetCustom(path, out _));

        var log = new CapturingLogger<RenderLogScopeTests>();
        using (FontStore.CaptureLogForTests(log))
        {
            Assert.False(FontStore.TryGetCustom(path, out _));
            Assert.False(FontStore.TryGetCustom(path, out _));
        }

        var warning = Assert.Single(log.Entries);
        Assert.Contains("could not be read", warning.Message);
    }

    [Fact]
    public void BrokenFont_ResolvedOutsideARender_StillWarnsInTheNextRender()
    {
        FontStore.ResetWarningsForTests();
        var path = BrokenFont("outside_" + Guid.NewGuid().ToString("N") + ".ttf");

        Assert.False(FontStore.TryGetCustom(path, out _));

        var log = new CapturingLogger<RenderLogScopeTests>();
        using (FontStore.CaptureLogForTests(log))
        {
            Assert.False(FontStore.TryGetCustom(path, out _));
        }

        Assert.Single(log.Entries);
    }
}
