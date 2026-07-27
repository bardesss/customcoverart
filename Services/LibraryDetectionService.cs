using System.Threading;
using CustomCoverArt.Models;
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

    public LibraryDetectionService(
        ILibraryManager libraryManager,
        ILoggingService loggingService)
    {
        _libraryManager = libraryManager;
        _loggingService = loggingService;
    }

    public Task<IEnumerable<LibraryInfo>> GetLibrariesAsync()
    {
        var libraries = new List<LibraryInfo>();

        try
        {
            // Top-level libraries are the CollectionFolder children of the root.
            var collectionFolders = _libraryManager.RootFolder?.Children?
                .OfType<CollectionFolder>() ?? Enumerable.Empty<CollectionFolder>();

            foreach (var folder in collectionFolders)
            {
                try
                {
                    libraries.Add(ToLibraryInfo(folder));
                }
                catch (Exception ex)
                {
                    _loggingService.LogError("Error processing library folder {FolderId}: {Error}",
                        folder?.Id, ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to get libraries: {Error}", ex.Message);
        }

        return Task.FromResult<IEnumerable<LibraryInfo>>(libraries);
    }

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

    public async Task<string?> BackupCurrentCoverArtAsync(string libraryId)
    {
        try
        {
            if (!Guid.TryParse(libraryId, out var id))
            {
                return null;
            }

            var item = _libraryManager.GetItemById<BaseItem>(id);
            if (item is null || !item.HasImage(ImageType.Primary))
            {
                return null;
            }

            var currentPath = item.GetImagePath(ImageType.Primary, 0);
            if (string.IsNullOrEmpty(currentPath) || !File.Exists(currentPath))
            {
                return null;
            }

            var backupPath = Path.Combine(
                Path.GetDirectoryName(currentPath) ?? string.Empty,
                $"backup_{Path.GetFileNameWithoutExtension(currentPath)}_{Guid.NewGuid():N}{Path.GetExtension(currentPath)}");

            File.Copy(currentPath, backupPath);
            return backupPath;
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> RestoreCoverArtAsync(string libraryId, string backupPath)
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
    {
        if (folder is not CollectionFolder collectionFolder)
        {
            return "Other";
        }

        var type = collectionFolder.CollectionType?.ToString()?.ToLowerInvariant();

        return type switch
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
            _ => "Other"
        };
    }
}
