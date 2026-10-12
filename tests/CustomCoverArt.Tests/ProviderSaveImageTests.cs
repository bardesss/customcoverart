using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CustomCoverArt.Common;
using CustomCoverArt.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// Covers are handed to Jellyfin's own image pipeline (the dashboard's "upload image" path)
/// instead of pointing the item at a file in the plugin's data dir. These tests set the
/// static BaseItem.LibraryManager that UpdateToRepositoryAsync uses; no other test class does.
/// </summary>
public class ProviderSaveImageTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "cca-provider-" + Guid.NewGuid().ToString("N"));

    private sealed record Host(LibraryDetectionService Svc, ILibraryManager Lib, IProviderManager Provider, Folder Item, IApplicationPaths Paths);

    private Host NewHost()
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.DataPath.Returns(_dataDir);
        var lib = Substitute.For<ILibraryManager>();
        BaseItem.LibraryManager = lib;
        var provider = Substitute.For<IProviderManager>();
        var item = new Folder { Id = Guid.NewGuid(), Name = "Movies" };
        lib.GetItemById<BaseItem>(item.Id).Returns(item);
        var svc = new LibraryDetectionService(lib, provider, NullLogger<LibraryDetectionService>.Instance, paths);
        return new Host(svc, lib, provider, item, paths);
    }

    private static string WriteFile(string dir, string name, byte[] bytes)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task Apply_SavesThroughTheProviderManager_AndPersists()
    {
        var h = NewHost();
        var cover = WriteFile(Path.Combine(_dataDir, "customcoverart", "generated"), "cover.png", new byte[] { 1, 2, 3 });
        byte[]? saved = null;
        string? mime = null;
        h.Provider.SaveImage(h.Item, Arg.Any<Stream>(), Arg.Any<string>(), ImageType.Primary, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci =>
            {
                using var ms = new MemoryStream();
                ((Stream)ci[1]).CopyTo(ms);
                saved = ms.ToArray();
                mime = (string)ci[2];
            });

        var ok = await h.Svc.UpdateLibraryCoverArtAsync(h.Item.Id.ToString(), cover);

        Assert.True(ok);
        Assert.Equal(new byte[] { 1, 2, 3 }, saved);
        Assert.Equal("image/png", mime);
        Assert.True(File.Exists(cover), "The stream overload copies; our file must survive.");
        await h.Lib.Received(1).UpdateItemAsync(h.Item, Arg.Any<BaseItem>(), ItemUpdateType.ImageUpdate, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Pre-4.0.2.0 restores pointed the item straight at the backup file. Jellyfin's image
    /// saver deletes an item's previous local image when it saves a new one, so that image
    /// must be detached first, or the restore point itself would be deleted.
    /// </summary>
    [Fact]
    public async Task Restore_DetachesPluginOwnedImageBeforeSaving_AndKeepsTheBackup()
    {
        var h = NewHost();
        var backup = WriteFile(Path.Combine(PluginPaths.Backups(h.Paths), h.Item.Id.ToString()), "original.png", new byte[] { 9, 8, 7 });
        h.Item.SetImage(new ItemImageInfo { Path = backup, Type = ImageType.Primary }, 0);

        string? currentAtSave = "not called";
        byte[]? saved = null;
        h.Provider.SaveImage(h.Item, Arg.Any<Stream>(), Arg.Any<string>(), ImageType.Primary, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci =>
            {
                currentAtSave = h.Item.GetImageInfo(ImageType.Primary, 0)?.Path;
                using var ms = new MemoryStream();
                ((Stream)ci[1]).CopyTo(ms);
                saved = ms.ToArray();
            });

        var ok = await h.Svc.RestoreOriginalCoverArtAsync(h.Item.Id.ToString());

        Assert.True(ok);
        Assert.Null(currentAtSave);
        Assert.Equal(new byte[] { 9, 8, 7 }, saved);
        Assert.True(File.Exists(backup));
        Assert.True(h.Svc.HasBackup(h.Item.Id.ToString()), "Restore must be repeatable.");
    }

    [Fact]
    public async Task Apply_KeepsAnOriginalImageOutsideThePluginDir_ForJellyfinToManage()
    {
        var h = NewHost();
        var foreign = WriteFile(Path.Combine(Path.GetTempPath(), "cca-foreign-" + Guid.NewGuid().ToString("N")), "poster.jpg", new byte[] { 5 });
        h.Item.SetImage(new ItemImageInfo { Path = foreign, Type = ImageType.Primary }, 0);
        var cover = WriteFile(Path.Combine(_dataDir, "customcoverart", "generated"), "cover.gif", new byte[] { 4 });
        string? currentAtSave = "not called";
        string? mime = null;
        h.Provider.SaveImage(h.Item, Arg.Any<Stream>(), Arg.Any<string>(), ImageType.Primary, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => { currentAtSave = h.Item.GetImageInfo(ImageType.Primary, 0)?.Path; mime = (string)ci[2]; });

        try
        {
            Assert.True(await h.Svc.UpdateLibraryCoverArtAsync(h.Item.Id.ToString(), cover));
            Assert.Equal(foreign, currentAtSave);
            Assert.Equal("image/gif", mime);
        }
        finally { Directory.Delete(Path.GetDirectoryName(foreign)!, true); }
    }

    /// <summary>
    /// The detach runs before SaveImage. If SaveImage then fails, the item must not be left
    /// without its image reference.
    /// </summary>
    [Fact]
    public async Task Apply_SaveImageThrows_ReattachesTheDetachedImage()
    {
        var h = NewHost();
        var backup = WriteFile(Path.Combine(PluginPaths.Backups(h.Paths), h.Item.Id.ToString()), "original.png", new byte[] { 9 });
        h.Item.SetImage(new ItemImageInfo { Path = backup, Type = ImageType.Primary }, 0);
        var cover = WriteFile(Path.Combine(_dataDir, "customcoverart", "generated"), "cover.png", new byte[] { 1 });
        h.Provider.SaveImage(h.Item, Arg.Any<Stream>(), Arg.Any<string>(), ImageType.Primary, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("disk full")));

        var ok = await h.Svc.UpdateLibraryCoverArtAsync(h.Item.Id.ToString(), cover);

        Assert.False(ok);
        Assert.Equal(backup, h.Item.GetImageInfo(ImageType.Primary, 0)?.Path);
    }

    [Fact]
    public async Task Backup_ForeignImage_IsBackedUp()
    {
        var h = NewHost();
        var foreign = WriteFile(Path.Combine(_dataDir, "metadata", "library"), "poster.jpg", new byte[] { 5 });
        h.Item.SetImage(new ItemImageInfo { Path = foreign, Type = ImageType.Primary }, 0);

        Assert.Equal(BackupOutcome.BackedUp, await h.Svc.BackupCurrentCoverArtAsync(h.Item.Id.ToString()));
        Assert.True(h.Svc.HasBackup(h.Item.Id.ToString()));
    }

    [Fact]
    public async Task Backup_ItemWithoutAnImage_NeedsNoBackup()
    {
        var h = NewHost();

        Assert.Equal(BackupOutcome.NotNeeded, await h.Svc.BackupCurrentCoverArtAsync(h.Item.Id.ToString()));
    }

    /// <summary>
    /// Jellyfin's image saver deletes the previous local image. If that image is not ours and
    /// could not be copied, applying would destroy the only copy of the original.
    /// </summary>
    [Fact]
    public async Task Backup_ForeignImageThatCannotBeCopied_ReportsFailure()
    {
        var h = NewHost();
        var foreign = WriteFile(Path.Combine(_dataDir, "metadata", "library"), "poster.jpg", new byte[] { 5 });
        h.Item.SetImage(new ItemImageInfo { Path = foreign, Type = ImageType.Primary }, 0);
        BlockBackupsDir(h.Paths);

        Assert.Equal(BackupOutcome.Failed, await h.Svc.BackupCurrentCoverArtAsync(h.Item.Id.ToString()));
    }

    /// <summary>A plugin-owned image is detached before saving, so Jellyfin never deletes it: no backup is required.</summary>
    [Fact]
    public async Task Backup_PluginOwnedImageThatCannotBeCopied_NeedsNoBackup()
    {
        var h = NewHost();
        var ours = WriteFile(Path.Combine(_dataDir, "customcoverart", "generated", "Libraries", h.Item.Id.ToString("N")), "cover.png", new byte[] { 5 });
        h.Item.SetImage(new ItemImageInfo { Path = ours, Type = ImageType.Primary }, 0);
        BlockBackupsDir(h.Paths);

        Assert.Equal(BackupOutcome.NotNeeded, await h.Svc.BackupCurrentCoverArtAsync(h.Item.Id.ToString()));
    }

    // A file where the backups directory should be makes every backup write fail.
    private static void BlockBackupsDir(IApplicationPaths paths)
    {
        var backups = PluginPaths.Backups(paths);
        Directory.CreateDirectory(Path.GetDirectoryName(backups)!);
        File.WriteAllText(backups, "not a directory");
    }

    [Theory]
    [InlineData("a.gif", "image/gif")]
    [InlineData("a.JPG", "image/jpeg")]
    [InlineData("a.jpe", "image/jpeg")]
    [InlineData("a.tbn", "image/jpeg")]
    [InlineData("a.webp", "image/webp")]
    [InlineData("a.bmp", "image/bmp")]
    [InlineData("a.png", "image/png")]
    public void MimeTypeFor_MapsExtensions(string file, string expected)
    {
        Assert.Equal(expected, LibraryDetectionService.MimeTypeFor(file));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, true); } catch { }
    }
}
