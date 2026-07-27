using CustomCoverArt.Models;
using Microsoft.AspNetCore.Http;

namespace CustomCoverArt.Services;

/// <summary>
/// Service for detecting and managing Jellyfin libraries
/// </summary>
public interface ILibraryDetectionService
{
    Task<IEnumerable<LibraryInfo>> GetLibrariesAsync();
    Task<LibraryInfo?> GetLibraryByIdAsync(string libraryId);
    Task<bool> UpdateLibraryCoverArtAsync(string libraryId, string coverArtPath);
    Task<string?> BackupCurrentCoverArtAsync(string libraryId);
    Task<bool> RestoreCoverArtAsync(string libraryId, string backupPath);
}

/// <summary>
/// Service for managing cover art operations
/// </summary>
public interface ICoverArtService
{
    Task<string> GenerateCoverArtAsync(CoverArtSettings settings);
    Task<bool> SaveCoverArtAsync(string libraryId, string coverArtPath);
    Task<byte[]?> GetCoverArtAsync(string libraryId);
    Task<bool> DeleteCoverArtAsync(string libraryId);
}

/// <summary>
/// Service for image processing operations
/// </summary>
public interface IImageProcessingService
{
    Task<byte[]> ProcessImageAsync(byte[] imageData, CoverArtSettings settings);
    Task<byte[]> ProcessGifAsync(byte[] gifData, CoverArtSettings settings);
    Task<bool> IsGifImageAsync(byte[] imageData);
    Task<(int width, int height)> GetImageDimensionsAsync(byte[] imageData);
    Task<string> DetermineOptimalFormatAsync(CoverArtSettings settings);
    Task<ValidationResult> ValidateFileAsync(IFormFile file);

    // NOTE: IsValidFileSize / ValidateCoverArtDimensions are pure helpers exposed
    // as public static methods on ImageProcessingService (called statically), so
    // they are intentionally NOT part of this interface.
}

/// <summary>
/// Service for browsing and managing media items from Jellyfin libraries
/// </summary>
public interface IMediaItemService
{
    Task<IEnumerable<MediaItemInfo>> GetLibraryItemsAsync(string libraryId);
    Task<MediaItemInfo?> GetItemByIdAsync(string itemId);
    Task<ItemSearchResponse> SearchItemsAsync(ItemSearchRequest request);
    Task<byte[]?> GetItemCoverArtAsync(string itemId);
    Task<string?> GetItemCoverArtUrlAsync(string itemId);
    Task<string?> GetItemImageSourcePathAsync(string itemId);
    Task<IEnumerable<MediaItemInfo>> GetRecentItemsAsync(int count = 20);
}

