using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CustomCoverArt.Common;
using CustomCoverArt.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// The backup lifecycle against a stand-in for Jellyfin's image store: SaveImage writes what
/// it is given to <c>root/default/&lt;name&gt;/poster.png</c> (outside the plugin's data dir,
/// as on a real server) and points the item at it, which is what Jellyfin's ImageSaver does.
/// </summary>
[Collection("BaseItemStatics")]
public class BackupLifecycleTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "cca-lifecycle-" + Guid.NewGuid().ToString("N"));

    private sealed record Host(LibraryDetectionService Svc, IProviderManager Provider, Folder Item, IApplicationPaths Paths, string StorePoster);

    // reencode simulates Jellyfin writing different bytes than it was given.
    private Host NewHost(ILogger<LibraryDetectionService>? logger = null, Func<byte[], byte[]>? reencode = null)
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.DataPath.Returns(_dataDir);
        var lib = Substitute.For<ILibraryManager>();
        BaseItem.LibraryManager = lib;
        var provider = Substitute.For<IProviderManager>();
        var item = new Folder { Id = Guid.NewGuid(), Name = "Movies" };
        lib.GetItemById<BaseItem>(item.Id).Returns(item);
        var storePoster = Path.Combine(_dataDir, "root", "default", item.Name, "poster.png");
        provider.SaveImage(item, Arg.Any<Stream>(), Arg.Any<string>(), ImageType.Primary, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci =>
            {
                using var ms = new MemoryStream();
                ((Stream)ci[1]).CopyTo(ms);
                var bytes = reencode is null ? ms.ToArray() : reencode(ms.ToArray());
                Directory.CreateDirectory(Path.GetDirectoryName(storePoster)!);
                File.WriteAllBytes(storePoster, bytes);
                item.SetImage(new ItemImageInfo { Path = storePoster, Type = ImageType.Primary }, 0);
            });
        var svc = new LibraryDetectionService(lib, provider, logger ?? NullLogger<LibraryDetectionService>.Instance, paths);
        return new Host(svc, provider, item, paths, storePoster);
    }

    private static string WriteFile(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    // The target shows an image that is not the plugin's (Jellyfin's own, or one the user set).
    private string ShowForeignImage(Host h, byte[] bytes)
    {
        var path = WriteFile(Path.Combine(_dataDir, "metadata", "library", h.Item.Id.ToString("N"), "poster.jpg"), bytes);
        h.Item.SetImage(new ItemImageInfo { Path = path, Type = ImageType.Primary }, 0);
        return path;
    }

    // A rendered cover in the plugin's output folder, ready to apply.
    private static string RenderedCover(Host h, byte[] bytes)
        => WriteFile(Path.Combine(PluginPaths.Generated(h.Paths), "Libraries", h.Item.Id.ToString("N"), "cover_" + Guid.NewGuid().ToString("N") + ".png"), bytes);

    private static string BackupDir(Host h, string format)
        => Path.Combine(PluginPaths.Backups(h.Paths), h.Item.Id.ToString(format));

    // --- E1: backup folder spelling ----------------------------------------------------

    /// <summary>
    /// Libraries send N-format ids, collections/playlists/Live TV D-format. Earlier versions
    /// wrote N folders, 4.0.2.0 wrote D folders: every combination must find the backup.
    /// </summary>
    [Theory]
    [InlineData("N", "N")]
    [InlineData("N", "D")]
    [InlineData("D", "N")]
    [InlineData("D", "D")]
    public async Task ABackupIsFound_WhicheverWayTheIdAndTheFolderAreSpelled(string idFormat, string folderFormat)
    {
        var h = NewHost();
        WriteFile(Path.Combine(BackupDir(h, folderFormat), "original.png"), new byte[] { 7, 7 });
        var id = h.Item.Id.ToString(idFormat);

        Assert.True(h.Svc.HasBackup(id));
        Assert.True(await h.Svc.RestoreOriginalCoverArtAsync(id));
        Assert.Equal(new byte[] { 7, 7 }, File.ReadAllBytes(h.StorePoster));
    }

    [Theory]
    [InlineData("N")]
    [InlineData("D")]
    public async Task NewBackups_AreWrittenUnderTheNFormatId(string idFormat)
    {
        var h = NewHost();
        ShowForeignImage(h, new byte[] { 5 });

        Assert.Equal(BackupOutcome.BackedUp, await h.Svc.BackupCurrentCoverArtAsync(h.Item.Id.ToString(idFormat)));
        Assert.True(File.Exists(Path.Combine(BackupDir(h, "N"), "original.jpg")));
        Assert.False(Directory.Exists(BackupDir(h, "D")));
    }

    /// <summary>
    /// The 4.0.2.0 regression: the first apply wrote a D folder, the second looked in the N
    /// folder, found nothing, collided with the D file and was refused (errors.backup_failed).
    /// </summary>
    [Fact]
    public async Task SecondApply_OnALibraryWithoutAnEarlierBackup_IsNotRefused()
    {
        var h = NewHost();
        var id = h.Item.Id.ToString("N"); // libraries arrive N-format
        ShowForeignImage(h, new byte[] { 5, 5 });

        Assert.Equal(BackupOutcome.BackedUp, await h.Svc.BackupCurrentCoverArtAsync(id));
        Assert.True(await h.Svc.UpdateLibraryCoverArtAsync(id, RenderedCover(h, new byte[] { 1 })));

        Assert.Equal(BackupOutcome.BackedUp, await h.Svc.BackupCurrentCoverArtAsync(id));
        Assert.True(h.Svc.HasBackup(id));
        Assert.Equal(new byte[] { 5, 5 }, File.ReadAllBytes(Directory.GetFiles(BackupDir(h, "N"), "original.*").Single()));
    }

    [Fact]
    public async Task ABackupLeftByVersion4020_IsKept_NotDuplicated()
    {
        var h = NewHost();
        WriteFile(Path.Combine(BackupDir(h, "D"), "original.png"), new byte[] { 9 });
        ShowForeignImage(h, new byte[] { 5 });

        Assert.Equal(BackupOutcome.BackedUp, await h.Svc.BackupCurrentCoverArtAsync(h.Item.Id.ToString("N")));
        Assert.False(Directory.Exists(BackupDir(h, "N")));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, true); } catch { }
    }
}
