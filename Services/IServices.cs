using CustomCoverArt.Models;
using Microsoft.AspNetCore.Http;

namespace CustomCoverArt.Services;

public interface ILibraryDetectionService
{
    Task<IEnumerable<LibraryInfo>> GetLibrariesAsync();

    /// <summary>Lists cover-art targets of the given type: "library", "collection" or "playlist".</summary>
    Task<IEnumerable<LibraryInfo>> GetTargetsAsync(string type);

    Task<LibraryInfo?> GetLibraryByIdAsync(string libraryId);
    Task<bool> UpdateLibraryCoverArtAsync(string libraryId, string coverArtPath);
    /// <summary>
    /// Copies the target's current primary image to its restore point, once. Applying may
    /// only proceed unless this returns <see cref="BackupOutcome.Failed"/>.
    /// </summary>
    Task<BackupOutcome> BackupCurrentCoverArtAsync(string libraryId);

    /// <summary>Whether an original-cover restore point exists for a target.</summary>
    bool HasBackup(string libraryId);

    /// <summary>
    /// Deletes a target's restore point (in either id spelling) so the next apply backs up
    /// whatever the target shows then. Touches nothing outside <c>backups/&lt;that target&gt;</c>.
    /// Throws <see cref="ArgumentException"/> for a malformed id.
    /// </summary>
    BackupResetOutcome ResetBackup(string targetId);

    /// <summary>Restore a target's original (pre-plugin) primary image, if backed up.</summary>
    Task<bool> RestoreOriginalCoverArtAsync(string libraryId);
}

/// <summary>Result of backing up a target's current cover before it is replaced.</summary>
public enum BackupOutcome
{
    /// <summary>A restore point exists (just made, or kept from an earlier apply).</summary>
    BackedUp,

    /// <summary>Nothing would be lost: no current local image, or it is plugin-owned
    /// (detached before saving, so Jellyfin's image saver never deletes it).</summary>
    NotNeeded,

    /// <summary>The current image is not ours and could not be copied; replacing it
    /// could lose the original for good.</summary>
    Failed,
}

/// <summary>Result of <see cref="ILibraryDetectionService.ResetBackup"/>.</summary>
public enum BackupResetOutcome
{
    /// <summary>The backup folder(s) were deleted.</summary>
    Deleted,

    /// <summary>There was no backup to delete.</summary>
    NoBackup,

    /// <summary>The target's current image IS the backup file (a pre-4.0.2.0 restore pointed
    /// the item straight at it), so deleting it would blank the cover. Nothing was deleted.</summary>
    InUse,
}

public interface ICoverArtService
{
    Task<string> GenerateCoverArtAsync(CoverArtSettings settings);

    /// <summary>Renders a CoverDocument to a file and returns its path.</summary>
    Task<string> GenerateFromDocumentAsync(CoverDocument document);

    /// <summary>Persists the cover into the per-library folder and returns that
    /// stable path (or null on failure) so it can be applied to the library.</summary>
    Task<string?> SaveCoverArtAsync(string libraryId, string coverArtPath);

    /// <summary>Deletes every file in the target's output folder except <paramref name="keepPath"/>.
    /// Call only after the target has been pointed at <paramref name="keepPath"/>.</summary>
    void PruneLibraryOutputs(string libraryId, string keepPath);
}

public interface IImageProcessingService
{
    Task<ValidationResult> ValidateFileAsync(IFormFile file);

    // NOTE: IsValidFileSize / ValidateCoverArtDimensions are pure helpers exposed
    // as public static methods on ImageProcessingService (called statically), so
    // they are intentionally NOT part of this interface.
}

public interface IMediaItemService
{
    Task<ItemSearchResponse> SearchItemsAsync(ItemSearchRequest request);

    /// <summary>Primary-image file paths of a target's child items (for poster collages).</summary>
    Task<IReadOnlyList<string>> GetPosterPathsAsync(string parentId, int max);
}
