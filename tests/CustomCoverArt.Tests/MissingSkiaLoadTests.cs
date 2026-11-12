using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// Spec S4.4 for real: the plugin assembly is loaded into a context where SkiaSharp.dll cannot be
/// resolved, as on a Jellyfin install whose SkiaSharp is missing. Loading every type, the probe, the
/// cover service and the guarded controller actions must all degrade to the clear refusal, which only
/// holds if no method compiled before the guard names a Skia type.
/// </summary>
public sealed class MissingSkiaLoadTests : IDisposable
{
    private const string Unavailable = "The server's image engine (SkiaSharp) is not available on this Jellyfin installation.";

    private readonly NoSkiaContext _context = new();
    private readonly Assembly _plugin;
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "cca_noskia_" + Guid.NewGuid().ToString("N"));

    public MissingSkiaLoadTests()
    {
        _plugin = _context.LoadFromAssemblyPath(typeof(Plugin).Assembly.Location);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, true); } catch { }
    }

    /// <summary>
    /// Resolves the plugin's dependencies from the default context, except the Skia family, which cannot
    /// be bound. The runtime swallows an exception thrown from <see cref="Load"/> and falls back to the
    /// default context (where the test host has SkiaSharp), so instead this returns an assembly with the
    /// wrong identity, which the binder rejects with a <see cref="FileLoadException"/>: the same failure
    /// path (an unbindable SkiaSharp reference, surfacing when a method naming its types is compiled) as
    /// a missing SkiaSharp.dll, whose <see cref="FileNotFoundException"/> the probe also catches.
    /// </summary>
    private sealed class NoSkiaContext : AssemblyLoadContext
    {
        public NoSkiaContext()
            : base("no-skia", isCollectible: false)
        {
        }

        public int SkiaRequests { get; private set; }

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is { } n && (n.StartsWith("SkiaSharp", StringComparison.Ordinal) || n.StartsWith("HarfBuzzSharp", StringComparison.Ordinal)))
            {
                SkiaRequests++;
                return typeof(Xunit.Assert).Assembly; // not SkiaSharp: the binder refuses it
            }

            return null;
        }
    }

    private Type PluginType(string fullName) => _plugin.GetType(fullName, throwOnError: true)!;

    private object NewDocument(int width, int height)
    {
        var doc = Activator.CreateInstance(PluginType("CustomCoverArt.Models.CoverDocument"))!;
        var canvas = doc.GetType().GetProperty("Canvas")!.GetValue(doc)!;
        canvas.GetType().GetProperty("Width")!.SetValue(canvas, width);
        canvas.GetType().GetProperty("Height")!.SetValue(canvas, height);
        return doc;
    }

    [Fact]
    public void TheIsolatedPluginReallyHasNoSkiaSharp()
    {
        Assert.NotSame(typeof(Plugin).Assembly, _plugin);
        Assert.Throws<FileLoadException>(() => _context.LoadFromAssemblyName(new AssemblyName("SkiaSharp")));
        Assert.Throws<FileLoadException>(() => _context.LoadFromAssemblyName(new AssemblyName("HarfBuzzSharp")));
    }

    [Fact]
    public void GetTypes_LoadsEveryType()
    {
        var types = _plugin.GetTypes();

        Assert.Contains(types, t => t.Name == "SkiaRuntime");
        Assert.Contains(types, t => t.Name == "CustomCoverArtController");
    }

    [Fact]
    public void Probe_ReportsUnavailable()
    {
        var runtime = PluginType("CustomCoverArt.Services.Rendering.SkiaRuntime");

        Assert.False((bool)runtime.GetProperty("IsAvailable")!.GetValue(null)!);
        Assert.Null(runtime.GetProperty("NativeVersion")!.GetValue(null));
        Assert.True(_context.SkiaRequests > 0, "the probe never tried to load SkiaSharp");
    }

    [Fact]
    public async Task CoverService_RefusesWithTheClearMessage()
    {
        var serviceType = PluginType("CustomCoverArt.Services.CoverArtService");
        var paths = Substitute.For<IApplicationPaths>();
        paths.DataPath.Returns(_dataDir);
        var logger = Activator.CreateInstance(typeof(CapturingLogger<>).MakeGenericType(serviceType))!;
        var entries = (System.Collections.Generic.List<(LogLevel Level, string Message)>)logger.GetType().GetProperty("Entries")!.GetValue(logger)!;
        var service = Activator.CreateInstance(serviceType, paths, logger, null)!;
        var generate = serviceType.GetMethod("GenerateFromDocumentAsync")!;

        for (var i = 0; i < 2; i++)
        {
            var task = (Task<string>)generate.Invoke(service, new[] { NewDocument(400, 600) })!;
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => task);

            Assert.Equal("CustomCoverArt.Services.CoverValidationException", ex.GetType().FullName);
            Assert.Equal(Unavailable, ex.Message);
        }

        // The probe's one Warning reached the service's logger, once for both refusals.
        var warning = Assert.Single(entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.StartsWith("Image engine unavailable on ", warning.Message);
        Assert.Contains("FileLoadException", warning.Message);
    }

    [Fact]
    public async Task ControllerActions_RefuseWithTheClearMessage()
    {
        var controllerType = PluginType("CustomCoverArt.Controllers.CustomCoverArtController");
        var ctor = controllerType.GetConstructors().Single();
        var args = ctor.GetParameters().Select(p => p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null).ToArray();
        var controller = ctor.Invoke(args);

        var preview = await (Task<IActionResult>)controllerType.GetMethod("GeneratePreviewDocument")!.Invoke(controller, new[] { NewDocument(400, 600) })!;
        var bad = Assert.IsType<BadRequestObjectResult>(preview);
        Assert.Contains(Unavailable, System.Text.Json.JsonSerializer.Serialize(bad.Value).Replace("\\u0027", "'"));

        foreach (var action in new[] { "UploadImage", "UploadFont" })
        {
            var task = (Task)controllerType.GetMethod(action)!.Invoke(controller, new object?[] { null })!;
            await task;
            var response = task.GetType().GetProperty("Result")!.GetValue(task)!;
            Assert.Equal(Unavailable, response.GetType().GetProperty("ErrorMessage")!.GetValue(response));
        }
    }
}
