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
    Task<string?> BackupCurrentCoverArtAsync(string libraryId);

    /// <summary>Whether an original-cover restore point exists for a target.</summary>
    bool HasBackup(string libraryId);

    /// <summary>Restore a target's original (pre-plugin) primary image, if backed up.</summary>
    Task<bool> RestoreOriginalCoverArtAsync(string libraryId);
}

public interface ICoverArtService
{
    Task<string> GenerateCoverArtAsync(CoverArtSettings settings);

    /// <summary>Renders a CoverDocument to a file and returns its path.</summary>
    Task<string> GenerateFromDocumentAsync(CoverDocument document);

    /// <summary>Persists the cover into the per-library folder and returns that
    /// stable path (or null on failure) so it can be applied to the library.</summary>
    Task<string?> SaveCoverArtAsync(string libraryId, string coverArtPath);
}

public interface IImageProcessingService
{
    Task<string> DetermineOptimalFormatAsync(CoverArtSettings settings);
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
