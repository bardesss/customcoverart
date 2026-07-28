using System;
using System.IO;
using System.Threading.Tasks;
using CustomCoverArt.Services;
using MediaBrowser.Common.Configuration;
using NSubstitute;
using Xunit;

namespace CustomCoverArt.Tests;

public class BackupRestoreTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "cca-backup-" + Guid.NewGuid().ToString("N"));

    private IApplicationPaths Paths()
    {
        var p = Substitute.For<IApplicationPaths>();
        p.DataPath.Returns(_dataDir);
        return p;
    }

    [Fact]
    public void HasBackup_FalseWhenNoBackupFileExists()
    {
        var svc = new LibraryDetectionService(
            Substitute.For<MediaBrowser.Controller.Library.ILibraryManager>(),
            Substitute.For<ILoggingService>(),
            Paths());

        Assert.False(svc.HasBackup(Guid.NewGuid().ToString()));
    }

    [Fact]
    public void HasBackup_TrueAfterBackupFilePlaced()
    {
        var paths = Paths();
        var id = Guid.NewGuid().ToString();
        var dir = Path.Combine(CustomCoverArt.Common.PluginPaths.Backups(paths), id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "original.png"), "x");

        var svc = new LibraryDetectionService(
            Substitute.For<MediaBrowser.Controller.Library.ILibraryManager>(),
            Substitute.For<ILoggingService>(), paths);

        Assert.True(svc.HasBackup(id));
    }

    [Fact]
    public async Task RestoreOriginal_ReturnsFalseWhenNoBackup()
    {
        var svc = new LibraryDetectionService(
            Substitute.For<MediaBrowser.Controller.Library.ILibraryManager>(),
            Substitute.For<ILoggingService>(), Paths());

        Assert.False(await svc.RestoreOriginalCoverArtAsync(Guid.NewGuid().ToString()));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, true); } catch { }
    }
}
