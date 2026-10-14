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

    // --- E3: reset backup ---------------------------------------------------------------

    [Fact]
    public void IsInside_IsTrueOnlyBelowTheDirectory()
    {
        var dir = Path.Combine(_dataDir, "customcoverart", "backups", "abc");
        Assert.True(PluginPaths.IsInside(dir, Path.Combine(dir, "original.png")));
        Assert.False(PluginPaths.IsInside(dir, dir + "def" + Path.DirectorySeparatorChar + "original.png"));
        Assert.False(PluginPaths.IsInside(dir, Path.Combine(dir, "..", "other", "original.png")));
        Assert.False(PluginPaths.IsInside(dir, ""));
    }

    [Fact]
    public void Reset_DeletesTheTargetsBackupInBothSpellings_AndNothingElse()
    {
        var h = NewHost();
        WriteFile(Path.Combine(BackupDir(h, "N"), "original.png"), new byte[] { 1 });
        WriteFile(Path.Combine(BackupDir(h, "D"), "original.png"), new byte[] { 2 });
        var sibling = WriteFile(Path.Combine(PluginPaths.Backups(h.Paths), Guid.NewGuid().ToString("N"), "original.png"), new byte[] { 3 });
        var stray = WriteFile(Path.Combine(PluginPaths.Backups(h.Paths), "readme.txt"), new byte[] { 4 });
        var applied = WriteFile(Path.Combine(PluginPaths.Base(h.Paths), "applied", h.Item.Id.ToString("N") + ".sha256"), new byte[] { 5 });

        Assert.Equal(BackupResetOutcome.Deleted, h.Svc.ResetBackup(h.Item.Id.ToString("D")));

        Assert.False(Directory.Exists(BackupDir(h, "N")));
        Assert.False(Directory.Exists(BackupDir(h, "D")));
        Assert.True(File.Exists(sibling));
        Assert.True(File.Exists(stray));
        Assert.True(File.Exists(applied));
        Assert.False(h.Svc.HasBackup(h.Item.Id.ToString()));
    }

    [Fact]
    public void Reset_WithoutABackup_ReportsNoBackup()
    {
        var h = NewHost();
        Assert.Equal(BackupResetOutcome.NoBackup, h.Svc.ResetBackup(h.Item.Id.ToString("N")));
    }

    /// <summary>
    /// A restore made before 4.0.2.0 pointed the item straight at the backup file. Deleting
    /// the folder then would delete the image the target is showing.
    /// </summary>
    [Fact]
    public void Reset_RefusesWhileTheTargetShowsTheBackupFileItself()
    {
        var h = NewHost();
        var backup = WriteFile(Path.Combine(BackupDir(h, "N"), "original.png"), new byte[] { 1 });
        h.Item.SetImage(new ItemImageInfo { Path = backup, Type = ImageType.Primary }, 0);

        Assert.Equal(BackupResetOutcome.InUse, h.Svc.ResetBackup(h.Item.Id.ToString("N")));
        Assert.True(File.Exists(backup));
    }

    [Fact]
    public async Task Reset_ThenApply_SavesTheThenCurrentCoverAsTheNewOriginal()
    {
        var h = NewHost();
        WriteFile(Path.Combine(BackupDir(h, "N"), "original.png"), new byte[] { 1 }); // a wrong backup, e.g. an early test cover
        ShowForeignImage(h, new byte[] { 8, 8 });                                     // the cover the user set in the dashboard

        Assert.Equal(BackupResetOutcome.Deleted, h.Svc.ResetBackup(h.Item.Id.ToString("N")));
        Assert.Equal(BackupOutcome.BackedUp, await h.Svc.BackupCurrentCoverArtAsync(h.Item.Id.ToString("N")));
        Assert.Equal(new byte[] { 8, 8 }, File.ReadAllBytes(Path.Combine(BackupDir(h, "N"), "original.jpg")));
    }

    [Fact]
    public void Reset_RejectsAMalformedId()
    {
        var h = NewHost();
        Assert.Throws<ArgumentException>(() => h.Svc.ResetBackup("../x"));
    }

    // --- E2: plugin covers are never the original; E4: say what happened -------------------

    private static string AppliedRecord(Host h)
        => Path.Combine(PluginPaths.Base(h.Paths), "applied", h.Item.Id.ToString("N") + ".sha256");

    /// <summary>
    /// After 4.0.2.0 an applied cover lives in Jellyfin's own store, outside our data dir, so
    /// only the applied-cover record can tell it apart from the user's original.
    /// </summary>
    [Fact]
    public async Task Backup_SkipsTheCoverThisPluginApplied()
    {
        var h = NewHost();
        var id = h.Item.Id.ToString("N");
        Assert.True(await h.Svc.UpdateLibraryCoverArtAsync(id, RenderedCover(h, new byte[] { 1, 2, 3 })));

        Assert.Equal(BackupOutcome.NotNeeded, await h.Svc.BackupCurrentCoverArtAsync(id));
        Assert.False(h.Svc.HasBackup(id));
    }

    /// <summary>
    /// Jellyfin may re-encode what it is given (seen live: a restored poster.png differed from
    /// its backup), so the record must hash the stored file, not our rendered source.
    /// </summary>
    [Fact]
    public async Task TheAppliedRecord_HashesTheFileJellyfinStored_NotTheRenderedSource()
    {
        var h = NewHost(reencode: b => b.Concat(new byte[] { 0xFF }).ToArray());
        var id = h.Item.Id.ToString("N");
        var source = new byte[] { 1, 2, 3 };
        Assert.True(await h.Svc.UpdateLibraryCoverArtAsync(id, RenderedCover(h, source)));

        var record = File.ReadAllText(AppliedRecord(h)).Trim();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(h.StorePoster))), record);
        Assert.NotEqual(Convert.ToHexString(SHA256.HashData(source)), record);
        Assert.Equal(BackupOutcome.NotNeeded, await h.Svc.BackupCurrentCoverArtAsync(id));
    }

    [Fact]
    public async Task Backup_SkipsAnImageInsideThePluginDataFolder()
    {
        var h = NewHost();
        var ours = WriteFile(Path.Combine(PluginPaths.Generated(h.Paths), "Libraries", h.Item.Id.ToString("N"), "old.png"), new byte[] { 4 });
        h.Item.SetImage(new ItemImageInfo { Path = ours, Type = ImageType.Primary }, 0);

        Assert.Equal(BackupOutcome.NotNeeded, await h.Svc.BackupCurrentCoverArtAsync(h.Item.Id.ToString()));
        Assert.False(h.Svc.HasBackup(h.Item.Id.ToString()));
    }

    /// <summary>
    /// After a restore the target shows its original. If the record still claimed it, a
    /// Reset backup followed by an apply would replace the original without a copy.
    /// </summary>
    [Fact]
    public async Task Restore_ClearsTheAppliedRecord_SoTheRestoredOriginalCountsAsForeignAgain()
    {
        var h = NewHost();
        var id = h.Item.Id.ToString("N");
        ShowForeignImage(h, new byte[] { 5, 5 });
        Assert.Equal(BackupOutcome.BackedUp, await h.Svc.BackupCurrentCoverArtAsync(id));
        Assert.True(await h.Svc.UpdateLibraryCoverArtAsync(id, RenderedCover(h, new byte[] { 1 })));
        Assert.True(File.Exists(AppliedRecord(h)));

        Assert.True(await h.Svc.RestoreOriginalCoverArtAsync(id));

        Assert.False(File.Exists(AppliedRecord(h)));
        Assert.Equal(BackupResetOutcome.Deleted, h.Svc.ResetBackup(id));
        Assert.Equal(BackupOutcome.BackedUp, await h.Svc.BackupCurrentCoverArtAsync(id));
        Assert.Equal(new byte[] { 5, 5 }, File.ReadAllBytes(Directory.GetFiles(BackupDir(h, "N"), "original.*").Single()));
    }

    /// <summary>Reset must not undo E2: the cover the plugin applied last is still not an original.</summary>
    [Fact]
    public async Task Reset_KeepsTheAppliedRecord_SoAPluginCoverIsStillNotSavedAsTheOriginal()
    {
        var h = NewHost();
        var id = h.Item.Id.ToString("N");
        ShowForeignImage(h, new byte[] { 5 });
        Assert.Equal(BackupOutcome.BackedUp, await h.Svc.BackupCurrentCoverArtAsync(id));
        Assert.True(await h.Svc.UpdateLibraryCoverArtAsync(id, RenderedCover(h, new byte[] { 1 })));

        Assert.Equal(BackupResetOutcome.Deleted, h.Svc.ResetBackup(id));

        Assert.Equal(BackupOutcome.NotNeeded, await h.Svc.BackupCurrentCoverArtAsync(id));
        Assert.False(h.Svc.HasBackup(id));
    }

    [Fact]
    public async Task BackupAndRestore_SayWhatTheyDid()
    {
        var log = new CapturingLogger<LibraryDetectionService>();
        var h = NewHost(log);
        var id = h.Item.Id.ToString("N");
        var foreign = ShowForeignImage(h, new byte[] { 5 });
        var backup = Path.Combine(BackupDir(h, "N"), "original.jpg");

        await h.Svc.BackupCurrentCoverArtAsync(id);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("Backed up", StringComparison.Ordinal)
            && e.Message.Contains(foreign, StringComparison.Ordinal)
            && e.Message.Contains(backup, StringComparison.Ordinal));

        Assert.True(await h.Svc.RestoreOriginalCoverArtAsync(id));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("Restored", StringComparison.Ordinal)
            && e.Message.Contains(id, StringComparison.Ordinal)
            && e.Message.Contains(backup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Backup_SaysWhyItSkippedAPluginCover()
    {
        var log = new CapturingLogger<LibraryDetectionService>();
        var h = NewHost(log);
        var id = h.Item.Id.ToString("N");
        Assert.True(await h.Svc.UpdateLibraryCoverArtAsync(id, RenderedCover(h, new byte[] { 1 })));

        await h.Svc.BackupCurrentCoverArtAsync(id);

        Assert.Contains(log.Entries, e => e.Level == LogLevel.Information
            && e.Message.Contains("Not backing up", StringComparison.Ordinal)
            && e.Message.Contains("applied last", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, true); } catch { }
    }
}
