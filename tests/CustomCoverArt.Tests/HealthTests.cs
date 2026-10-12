using System;
using System.IO;
using System.Threading.Tasks;
using CustomCoverArt.Common;
using CustomCoverArt.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CustomCoverArt.Tests;

public class HealthTests
{
    private static (StartupValidationService Svc, IApplicationPaths Paths) Host(string dataPath)
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.DataPath.Returns(dataPath);
        return (new StartupValidationService(NullLogger<StartupValidationService>.Instance, paths), paths);
    }

    /// <summary>The probe ran on every /health call (three dirs plus a %TEMP% dir). A success is now cached.</summary>
    [Fact]
    public async Task Success_IsCached_AndLeavesNoProbeFiles()
    {
        var dataDir = Path.Combine(Path.GetTempPath(), "cca_health_" + Guid.NewGuid().ToString("N"));
        var (svc, paths) = Host(dataDir);
        try
        {
            Assert.True(await svc.IsPluginReadyAsync());
            var baseDir = PluginPaths.Base(paths);
            Assert.Empty(Directory.GetFiles(baseDir));

            Directory.Delete(dataDir, true);
            Assert.True(await svc.IsPluginReadyAsync());
            Assert.False(Directory.Exists(baseDir), "A cached success must not probe the disk again.");
        }
        finally { try { Directory.Delete(dataDir, true); } catch { } }
    }

    [Fact]
    public async Task UnwritableDataDir_IsNotReady()
    {
        var blocker = Path.Combine(Path.GetTempPath(), "cca_block_" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(blocker, "a file where a directory should be");
        try
        {
            var (svc, _) = Host(Path.Combine(blocker, "sub"));
            Assert.False(await svc.IsPluginReadyAsync());
        }
        finally { File.Delete(blocker); }
    }

    /// <summary>The response carried exception messages with full server paths.</summary>
    [Fact]
    public async Task HealthEndpoint_ReturnsOnlyReadinessAndVersion()
    {
        var startup = Substitute.For<IStartupValidationService>();
        startup.IsPluginReadyAsync().Returns(false);

        var result = await ControllerHost.New(startup: startup).HealthCheck();

        Assert.True(result.Success);
        var json = System.Text.Json.JsonSerializer.Serialize(result.Data);
        Assert.Contains("\"PluginReady\":false", json);
        Assert.DoesNotContain("ValidationResults", json);
        Assert.DoesNotContain("ErrorMessage", json);
    }
}
