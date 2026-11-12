using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CustomCoverArt.Common;
using CustomCoverArt.Controllers;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using CustomCoverArt.Services.Rendering;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// The renderer-unavailable state (spec S4.4): what /health reports, what every rendering or decoding
/// action answers when the host's SkiaSharp cannot be used, and that the plugin's entry types never
/// name a Skia type, so the plugin still loads when SkiaSharp.dll cannot be resolved.
/// </summary>
public sealed class RendererHealthTests : IDisposable
{
    private const string Unavailable = "The server's image engine (SkiaSharp) is not available on this Jellyfin installation.";

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "cca_rh_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    private static string Json(object? value) => JsonSerializer.Serialize(value);

    private static IFormFile File(string name, byte[] bytes) => new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name);

    private static byte[] Png()
    {
        using var bitmap = new SkiaSharp.SKBitmap(8, 8);
        bitmap.Erase(SkiaSharp.SKColors.Teal);
        return TestImages.SkiaPng(bitmap);
    }

    private IApplicationPaths Paths()
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.DataPath.Returns(_dataDir);
        return paths;
    }

    [Fact]
    public async Task Health_ReportsTheRenderer()
    {
        var result = await ControllerHost.New().HealthCheck();

        Assert.True(result.Success);
        using var doc = JsonDocument.Parse(Json(result.Data));
        Assert.True(doc.RootElement.GetProperty("RendererAvailable").GetBoolean());
        var version = doc.RootElement.GetProperty("RendererVersion").GetString();
        Assert.False(string.IsNullOrEmpty(version));
        Assert.StartsWith("119.", version);
        Assert.Equal(SkiaRuntime.NativeVersion, version);
    }

    [Fact]
    public async Task Health_WhenRendererUnavailable_ReportsItWithoutAVersion()
    {
        SkiaRuntime.OverrideForTests = () => false;
        try
        {
            var result = await ControllerHost.New().HealthCheck();

            Assert.True(result.Success, "Health itself must keep answering when the renderer is unavailable.");
            using var doc = JsonDocument.Parse(Json(result.Data));
            Assert.False(doc.RootElement.GetProperty("RendererAvailable").GetBoolean());
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("RendererVersion").ValueKind);
            Assert.True(doc.RootElement.TryGetProperty("PluginReady", out _));
        }
        finally { SkiaRuntime.OverrideForTests = null; }
    }

    [Fact]
    public async Task Generate_WhenRendererUnavailable_ThrowsTheClearMessage()
    {
        var service = new CoverArtService(Paths(), new CapturingLogger<CoverArtService>(), Substitute.For<IMediaItemService>());
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 64, Height = 64, Format = "png" } };

        SkiaRuntime.OverrideForTests = () => false;
        try
        {
            var ex = await Assert.ThrowsAsync<CoverValidationException>(() => service.GenerateFromDocumentAsync(doc));
            Assert.Equal(Unavailable, ex.Message);
        }
        finally { SkiaRuntime.OverrideForTests = null; }

        var generated = PluginPaths.Generated(Paths());
        Assert.Empty(Directory.Exists(generated) ? Directory.GetFiles(generated) : Array.Empty<string>());
    }

    /// <summary>The guard runs before any input checks, so even a request that would fail validation hears the engine message.</summary>
    [Fact]
    public async Task Generate_WhenRendererUnavailable_RefusesBeforeLookingAtTheDocument()
    {
        var service = new CoverArtService(Paths(), new CapturingLogger<CoverArtService>(), Substitute.For<IMediaItemService>());
        var invalid = new CoverDocument { Canvas = new CanvasSettings { Width = 0, Height = -5 } };

        SkiaRuntime.OverrideForTests = () => false;
        try
        {
            var ex = await Assert.ThrowsAsync<CoverValidationException>(() => service.GenerateFromDocumentAsync(invalid));
            Assert.Equal(Unavailable, ex.Message);
            Assert.Equal(0, invalid.Canvas.Width);
        }
        finally { SkiaRuntime.OverrideForTests = null; }
    }

    [Fact]
    public async Task Upload_WhenRendererUnavailable_IsRefusedWithTheClearMessage()
    {
        var controller = ControllerHost.New(dataPath: _dataDir);
        var png = Png();

        SkiaRuntime.OverrideForTests = () => false;
        ApiResponse<string> result;
        try
        {
            result = await controller.UploadImage(File("cover.png", png));
        }
        finally { SkiaRuntime.OverrideForTests = null; }

        Assert.False(result.Success);
        Assert.Equal(Unavailable, result.ErrorMessage);
        var uploads = PluginPaths.Uploads(Paths());
        Assert.False(Directory.Exists(uploads) && Directory.EnumerateFileSystemEntries(uploads).Any(), "a refused upload must write nothing");
    }

    [Fact]
    public async Task Upload_WhenRendererAvailable_StillSavesTheFile()
    {
        var controller = ControllerHost.New(dataPath: _dataDir);

        var result = await controller.UploadImage(File("cover.png", Png()));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(System.IO.File.Exists(result.Data));
    }

    public static TheoryData<string> GuardedActions => new() { "preview", "apply", "batchApply", "uploadImage", "uploadFont" };

    /// <summary>Every action that renders or decodes answers with the same words, and none of them reaches the service.</summary>
    [Theory]
    [MemberData(nameof(GuardedActions))]
    public async Task GuardedAction_WhenRendererUnavailable_AnswersWithTheSameMessage(string action)
    {
        var coverArt = Substitute.For<ICoverArtService>();
        var library = Substitute.For<ILibraryDetectionService>();
        var controller = ControllerHost.New(coverArt: coverArt, library: library, dataPath: _dataDir);
        var document = new CoverDocument { Canvas = new CanvasSettings { Width = 64, Height = 64, Format = "png" } };
        var target = Guid.NewGuid().ToString();

        SkiaRuntime.OverrideForTests = () => false;
        string? message;
        try
        {
            message = action switch
            {
                "preview" => ErrorOf(Assert.IsType<BadRequestObjectResult>(await controller.GeneratePreviewDocument(document))),
                "apply" => FailureOf(await controller.ApplyDocument(new ApplyDocumentRequest { LibraryId = target, Document = document })),
                "batchApply" => FailureOf(await controller.BatchApply(new BatchApplyRequest
                {
                    Document = document,
                    Targets = { new BatchTargetRef { Id = target } },
                })),
                "uploadImage" => FailureOf(await controller.UploadImage(File("cover.png", Png()))),
                "uploadFont" => FailureOf(await controller.UploadFont(File("font.ttf", FontFixtures.Bytes("sub.ttf")))),
                _ => throw new ArgumentOutOfRangeException(nameof(action)),
            };
        }
        finally { SkiaRuntime.OverrideForTests = null; }

        Assert.Equal(Unavailable, message);
        await coverArt.DidNotReceiveWithAnyArgs().GenerateFromDocumentAsync(default!);
        await library.DidNotReceiveWithAnyArgs().UpdateLibraryCoverArtAsync(default!, default!);
        Assert.False(Directory.Exists(PluginPaths.Base(Paths())) && Directory.EnumerateFiles(PluginPaths.Base(Paths()), "*", SearchOption.AllDirectories).Any());
    }

    private static string? ErrorOf(BadRequestObjectResult result)
    {
        using var doc = JsonDocument.Parse(Json(result.Value));
        return doc.RootElement.GetProperty("error").GetString();
    }

    private static string? FailureOf<T>(ApiResponse<T> response)
    {
        Assert.False(response.Success);
        return response.ErrorMessage;
    }

    /// <summary>Restore, backups, templates and health need no image engine, so they keep working (S4.4).</summary>
    [Fact]
    public async Task NonRenderingEndpoints_KeepWorkingWhileUnavailable()
    {
        var library = Substitute.For<ILibraryDetectionService>();
        var target = Guid.NewGuid().ToString();
        library.RestoreOriginalCoverArtAsync(target).Returns(true);
        library.HasBackup(target).Returns(true);
        library.ResetBackup(target).Returns(BackupResetOutcome.Deleted);
        var controller = ControllerHost.New(library: library, dataPath: _dataDir);

        SkiaRuntime.OverrideForTests = () => false;
        try
        {
            Assert.True((await controller.RestoreOriginal(target)).Success);
            var hasBackup = controller.HasBackup(target);
            Assert.True(hasBackup.Success);
            Assert.True(hasBackup.Data);
            Assert.True((await controller.ResetBackup(target)).Success);
            Assert.True(controller.GetTemplates().Success);
            Assert.True((await controller.HealthCheck()).Success);
        }
        finally { SkiaRuntime.OverrideForTests = null; }

        await library.Received(1).RestoreOriginalCoverArtAsync(target);
        library.Received(1).ResetBackup(target);
    }

    public static TheoryData<Exception> ProbeFailures => new()
    {
        new DllNotFoundException("libSkiaSharp"),
        new TypeInitializationException("SkiaSharp.SKObject", null),
        new InvalidOperationException("The version of the native libSkiaSharp library (116.0) is incompatible"),
        new FileNotFoundException("Could not load file or assembly 'SkiaSharp, Version=3.119.0.0'"),
        new FileLoadException("SkiaSharp could not be loaded"),
        new EntryPointNotFoundException("sk_version_get_milestone"),
        new BadImageFormatException("libSkiaSharp is for another architecture"),
        new TypeLoadException("Could not load type 'SkiaSharp.SKPMColor' from assembly 'SkiaSharp'."),
        new MissingMethodException("Method not found: 'Boolean SkiaSharp.SkiaSharpVersion.CheckNativeLibraryCompatible(Boolean)'."),
        new MissingFieldException("Field not found: 'SkiaSharp.SKColors.Black'."),
    };

    [Theory]
    [MemberData(nameof(ProbeFailures))]
    public void Evaluate_ProbeThatThrows_ReportsUnavailableAndLogsOnce(Exception failure)
    {
        var log = new CapturingLogger<RendererHealthTests>();

        var result = SkiaRuntime.Evaluate(() => throw failure, log);

        Assert.False(result.Available);
        Assert.Null(result.Version);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains(failure.GetType().Name, entry.Message);
        Assert.Contains(failure.Message, entry.Message);
        Assert.StartsWith("Image engine unavailable on ", entry.Message);
        Assert.EndsWith(". Cover rendering is disabled; the rest of the server is not affected.", entry.Message);
    }

    [Fact]
    public void Evaluate_ProbeThatSucceeds_ReportsTheVersionAndLogsNothing()
    {
        var log = new CapturingLogger<RendererHealthTests>();

        var result = SkiaRuntime.Evaluate(() => "119.0", log);

        Assert.True(result.Available);
        Assert.Equal("119.0", result.Version);
        Assert.Empty(log.Entries);
    }

    /// <summary>Many requests arriving together on a fresh server run the native probe once, not once each.</summary>
    [Fact]
    public void State_EvaluatesTheProbeOnce_UnderConcurrentFirstAccess()
    {
        var calls = 0;
        var state = SkiaRuntime.CreateState(
            () =>
            {
                Interlocked.Increment(ref calls);
                Thread.Sleep(50); // widen the window in which other threads arrive
                return "119.0";
            },
            () => new CapturingLogger<RendererHealthTests>());

        const int threads = 8;
        using var barrier = new Barrier(threads);
        var seen = new (bool Available, string? Version)[threads];
        var workers = Enumerable.Range(0, threads).Select(i => new Thread(() =>
        {
            barrier.SignalAndWait();
            seen[i] = state.Value;
        })).ToList();
        workers.ForEach(t => t.Start());
        workers.ForEach(t => t.Join());

        Assert.Equal(1, calls);
        Assert.All(seen, s => Assert.Equal((true, "119.0"), s));
    }

    [Fact]
    public void State_AFailedProbe_IsAlsoCachedAndLoggedOnce()
    {
        var calls = 0;
        var log = new CapturingLogger<RendererHealthTests>();
        var state = SkiaRuntime.CreateState(
            () =>
            {
                calls++;
                throw new DllNotFoundException("libSkiaSharp");
            },
            () => log);

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal((false, (string?)null), state.Value);
        }

        Assert.Equal(1, calls);
        Assert.Single(log.Entries);
    }

    [Fact]
    public void Override_IsScopedToTheCallingFlow()
    {
        SkiaRuntime.OverrideForTests = () => false;
        try
        {
            Assert.False(SkiaRuntime.IsAvailable);
            var elsewhere = true;
            var thread = new Thread(() => elsewhere = SkiaRuntime.IsAvailable);
            thread.UnsafeStart();
            thread.Join();
            Assert.True(elsewhere, "an override in one test must not switch the renderer off for tests running in parallel");
        }
        finally { SkiaRuntime.OverrideForTests = null; }

        Assert.True(SkiaRuntime.IsAvailable);
    }

    // -------------------------------------------------------------------------------------
    // Entry types: Jellyfin calls Assembly.GetTypes() on the plugin and builds these types,
    // so a Skia type in any of their signatures would fail the whole plugin when SkiaSharp.dll
    // cannot be resolved, instead of degrading to the unavailable state.
    // -------------------------------------------------------------------------------------

    public static IEnumerable<object[]> EntryTypes()
    {
        var fixedTypes = new[] { typeof(Plugin), typeof(PluginServiceRegistrator), typeof(CustomCoverArtController), typeof(SkiaRuntime) };
        var interfaces = typeof(Plugin).Assembly.GetTypes()
            .Where(t => t.IsInterface && t.Namespace == "CustomCoverArt.Services");
        return fixedTypes.Concat(interfaces).Select(t => new object[] { t });
    }

    [Fact]
    public void EntryTypes_IncludeEveryServiceInterface()
    {
        var names = EntryTypes().Select(o => ((Type)o[0]).Name).ToList();
        Assert.Contains(nameof(ICoverArtService), names);
        Assert.Contains(nameof(IImageProcessingService), names);
        Assert.Contains(nameof(ILibraryDetectionService), names);
        Assert.True(names.Count >= 10, string.Join(", ", names));
    }

    [Theory]
    [MemberData(nameof(EntryTypes))]
    public void PluginEntryTypes_DoNotExposeSkiaTypes(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var found = new List<string>();

        void Check(Type? t, string where)
        {
            if (t is null)
            {
                return;
            }

            if (t.HasElementType)
            {
                Check(t.GetElementType(), where);
                return;
            }

            if (t.Namespace is { } ns && (ns.StartsWith("SkiaSharp", StringComparison.Ordinal) || ns.StartsWith("HarfBuzzSharp", StringComparison.Ordinal)))
            {
                found.Add($"{where}: {t.FullName}");
            }

            if (t.IsGenericType)
            {
                foreach (var arg in t.GetGenericArguments())
                {
                    Check(arg, where + " (generic argument)");
                }
            }
        }

        for (var b = type.BaseType; b is not null; b = b.BaseType)
        {
            Check(b, "base type");
        }

        foreach (var i in type.GetInterfaces())
        {
            Check(i, "interface");
        }

        foreach (var f in type.GetFields(all))
        {
            Check(f.FieldType, "field " + f.Name);
        }

        foreach (var p in type.GetProperties(all))
        {
            Check(p.PropertyType, "property " + p.Name);
        }

        foreach (var c in type.GetConstructors(all))
        {
            foreach (var p in c.GetParameters())
            {
                Check(p.ParameterType, "constructor parameter " + p.Name);
            }
        }

        foreach (var m in type.GetMethods(all))
        {
            Check(m.ReturnType, "return of " + m.Name);
            foreach (var p in m.GetParameters())
            {
                Check(p.ParameterType, $"parameter {p.Name} of {m.Name}");
            }
        }

        Assert.True(found.Count == 0, type.Name + " exposes Skia types: " + string.Join("; ", found));
    }

    /// <summary>The reflection check itself catches a Skia type (so a pass above means something).</summary>
    [Fact]
    public void SkiaTypeCheck_FlagsATypeThatDoesExposeSkia()
    {
        Assert.Throws<Xunit.Sdk.TrueException>(() => PluginEntryTypes_DoNotExposeSkiaTypes(typeof(CoverArtService)));
    }
}
