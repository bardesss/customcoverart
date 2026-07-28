using System.Threading;
using CustomCoverArt.Common;
using CustomCoverArt.Models;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace CustomCoverArt.Services;

/// <summary>
/// Service for detecting and managing Jellyfin libraries.
/// </summary>
public class LibraryDetectionService : ILibraryDetectionService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILoggingService _loggingService;
    private readonly IApplicationPaths _applicationPaths;

    public LibraryDetectionService(
        ILibraryManager libraryManager,
        ILoggingService loggingService,
        IApplicationPaths applicationPaths)
    {
        _libraryManager = libraryManager;
        _loggingService = loggingService;
        _applicationPaths = applicationPaths;
    }

    public Task<IEnumerable<LibraryInfo>> GetLibrariesAsync()
    {
        var libraries = new List<LibraryInfo>();

        try
        {
            // GetVirtualFolders() is the canonical list of configured libraries
            // (the same source as Jellyfin's /Library/VirtualFolders API). The
            // library views are NOT children of the physical RootFolder, so the
            // previous RootFolder.Children approach returned nothing.
            foreach (var vf in _libraryManager.GetVirtualFolders())
            {
                try
                {
                    libraries.Add(new LibraryInfo
                    {
                        Id = vf.ItemId ?? string.Empty,
                        Name = vf.Name ?? "Unknown Library",
                        Type = MapCollectionType(vf.CollectionType?.ToString()),
                        HasCustomCover = false,
                        LastModified = DateTime.MinValue
                    });
                }
                catch (Exception ex)
                {
                    _loggingService.LogError("Error processing library {Name}: {Error}", vf.Name ?? "unknown", ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to get libraries: {Error}", ex.Message);
        }

        return Task.FromResult<IEnumerable<LibraryInfo>>(libraries);
    }

    public Task<IEnumerable<LibraryInfo>> GetTargetsAsync(string type)
    {
        try
        {
            return (type ?? "library").ToLowerInvariant() switch
            {
                "collection" => Task.FromResult(QueryItems(BaseItemKind.BoxSet, "Collection")),
                "playlist" => Task.FromResult(QueryItems(BaseItemKind.Playlist, "Playlist")),
                "livetv" => Task.FromResult(QueryLiveTvViews()),
                _ => GetLibrariesAsync()
            };
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to get targets ({Type}): {Error}", type ?? "?", ex.Message);
            return Task.FromResult(Enumerable.Empty<LibraryInfo>());
        }
    }

    private IEnumerable<LibraryInfo> QueryItems(BaseItemKind kind, string typeLabel)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            Recursive = true
        };

        return _libraryManager.GetItemList(query)
            .OrderBy(item => item.Name)
            .Select(item => new LibraryInfo
            {
                Id = item.Id.ToString(),
                Name = item.Name ?? "Unknown",
                Type = typeLabel,
                HasCustomCover = item.HasImage(ImageType.Primary),
                LastModified = item.DateModified
            })
            .ToList();
    }

    // Live TV has no path-based virtual folder, so it never appears in
    // GetVirtualFolders(). Its home-screen tile is a UserView (or, on some
    // setups, a CollectionFolder) whose collection type is livetv. Locate that
    // item so a cover can be applied to it. Best-effort: whether Jellyfin keeps
    // a custom image on a generated view can vary by server/version.
    private IEnumerable<LibraryInfo> QueryLiveTvViews()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.UserView, BaseItemKind.CollectionFolder },
            Recursive = true
        };

        return _libraryManager.GetItemList(query)
            .Where(IsLiveTvView)
            .GroupBy(item => item.Id)
            .Select(group => group.First())
            .OrderBy(item => item.Name)
            .Select(item => new LibraryInfo
            {
                Id = item.Id.ToString(),
                Name = item.Name ?? "Live TV",
                Type = "Live TV",
                HasCustomCover = item.HasImage(ImageType.Primary),
                LastModified = item.DateModified
            })
            .ToList();
    }

    private static bool IsLiveTvView(BaseItem item) => item switch
    {
        UserView view => view.ViewType == CollectionType.livetv || view.CollectionType == CollectionType.livetv,
        CollectionFolder folder => folder.CollectionType == CollectionType.livetv,
        _ => false
    };

    private static string MapCollectionType(string? type) => type?.ToLowerInvariant() switch
    {
        "movies" => "Movies",
        "tvshows" => "TV Shows",
        "music" => "Music",
        "musicvideos" => "Music Videos",
        "homevideos" => "Home Videos",
        "books" => "Books",
        "photos" => "Photos",
        "boxsets" => "Collections",
        "mixed" => "Mixed",
        null or "" => "Other",
        _ => type!
    };

    public Task<LibraryInfo?> GetLibraryByIdAsync(string libraryId)
    {
        if (!Guid.TryParse(libraryId, out var id))
        {
            return Task.FromResult<LibraryInfo?>(null);
        }

        var folder = _libraryManager.GetItemById<BaseItem>(id);
        return Task.FromResult(folder is null ? null : ToLibraryInfo(folder));
    }

    public async Task<bool> UpdateLibraryCoverArtAsync(string libraryId, string coverArtPath)
    {
        try
        {
            if (!Guid.TryParse(libraryId, out var id))
            {
                _loggingService.LogWarning("Invalid library ID format: {LibraryId}", libraryId);
                return false;
            }

            var item = _libraryManager.GetItemById<BaseItem>(id);
            if (item is null)
            {
                _loggingService.LogWarning("Library not found with ID: {LibraryId}", libraryId);
                return false;
            }

            if (!File.Exists(coverArtPath))
            {
                _loggingService.LogWarning("Cover art file does not exist: {Path}", coverArtPath);
                return false;
            }

            // VERIFY: this is the primary Jellyfin-API integration point. Setting
            // the primary image then persisting with ItemUpdateType.ImageUpdate is
            // the 10.11 pattern; confirm against your running server. If the image
            // does not appear, a metadata refresh may also be required.
            item.SetImage(
                new ItemImageInfo { Path = coverArtPath, Type = ImageType.Primary },
                0);

            await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, CancellationToken.None)
                .ConfigureAwait(false);

            _loggingService.LogInformation(
                "Successfully updated cover art for library: {LibraryName} (ID: {LibraryId})",
                item.Name, libraryId);

            return true;
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to update library cover art for ID: {LibraryId}", ex, libraryId);
            return false;
        }
    }

    // A GUID subfolder is the only valid backup location; validating here means a
    // non-GUID targetId can never inject path segments even if a caller forgets to.
    private string BackupPathFor(string targetId, string extension)
        => Path.Combine(PluginPaths.Backups(_applicationPaths), NormalizeTargetId(targetId), "original" + extension);

    private string? ExistingBackupPath(string targetId)
    {
        if (!Guid.TryParse(targetId, out _))
        {
            return null;
        }

        var dir = Path.Combine(PluginPaths.Backups(_applicationPaths), targetId);
        if (!Directory.Exists(dir))
        {
            return null;
        }

        var files = Directory.GetFiles(dir, "original.*");
        return files.Length > 0 ? files[0] : null;
    }

    private static string NormalizeTargetId(string targetId)
        => Guid.TryParse(targetId, out var id) ? id.ToString() : throw new ArgumentException("Invalid target id", nameof(targetId));

    public bool HasBackup(string libraryId) => ExistingBackupPath(libraryId) is not null;

    public Task<string?> BackupCurrentCoverArtAsync(string libraryId)
    {
        try
        {
            if (!Guid.TryParse(libraryId, out var id))
            {
                return Task.FromResult<string?>(null);
            }

            // Never overwrite an existing backup: the first one is the true original.
            var existing = ExistingBackupPath(libraryId);
            if (existing is not null)
            {
                return Task.FromResult<string?>(existing);
            }

            var item = _libraryManager.GetItemById<BaseItem>(id);
            if (item is null || !item.HasImage(ImageType.Primary))
            {
                return Task.FromResult<string?>(null);
            }

            var currentPath = item.GetImagePath(ImageType.Primary, 0);
            if (string.IsNullOrEmpty(currentPath) || !File.Exists(currentPath))
            {
                return Task.FromResult<string?>(null);
            }

            var backupPath = BackupPathFor(libraryId, Path.GetExtension(currentPath));
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Copy(currentPath, backupPath, overwrite: false);
            return Task.FromResult<string?>(backupPath);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to back up cover art for {LibraryId}", ex, libraryId);
            return Task.FromResult<string?>(null);
        }
    }

    public async Task<bool> RestoreOriginalCoverArtAsync(string libraryId)
    {
        var backup = ExistingBackupPath(libraryId);
        if (backup is null)
        {
            return false;
        }

        return await RestoreCoverArtAsync(libraryId, backup).ConfigureAwait(false);
    }

    private async Task<bool> RestoreCoverArtAsync(string libraryId, string backupPath)
    {
        try
        {
            if (!File.Exists(backupPath) || !Guid.TryParse(libraryId, out var id))
            {
                return false;
            }

            var item = _libraryManager.GetItemById<BaseItem>(id);
            if (item is null)
            {
                return false;
            }

            item.SetImage(new ItemImageInfo { Path = backupPath, Type = ImageType.Primary }, 0);
            await item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, CancellationToken.None)
                .ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static LibraryInfo ToLibraryInfo(BaseItem folder)
    {
        var imagePath = folder.HasImage(ImageType.Primary)
            ? folder.GetImagePath(ImageType.Primary, 0)
            : null;

        return new LibraryInfo
        {
            Id = folder.Id.ToString(),
            Name = folder.Name ?? "Unknown Library",
            Type = GetLibraryType(folder),
            CurrentCoverArtPath = imagePath,
            HasCustomCover = !string.IsNullOrEmpty(imagePath),
            LastModified = folder.DateModified
        };
    }

    // Maps Jellyfin's CollectionType to a display label. Uses ToString() rather
    // than referencing individual enum members, so it is resilient to enum
    // member-name/casing differences across Jellyfin versions.
    private static string GetLibraryType(BaseItem folder)
        => folder is CollectionFolder cf ? MapCollectionType(cf.CollectionType?.ToString()) : "Other";
}
