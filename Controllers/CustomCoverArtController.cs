using System.IO;
using CustomCoverArt.Common;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace CustomCoverArt.Controllers;

/// <summary>
/// API controller for Custom Cover Art operations. All endpoints require an
/// authenticated Jellyfin administrator (the "RequiresElevation" policy, which
/// is the framework-provided policy — no custom auth filter needed).
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("CustomCoverArt")]
public class CustomCoverArtController : ControllerBase
{
    private readonly ILibraryDetectionService _libraryService;
    private readonly ICoverArtService _coverArtService;
    private readonly IImageProcessingService _imageProcessingService;
    private readonly IMediaItemService _mediaItemService;
    private readonly IUserContextService _userContextService;
    private readonly ILoggingService _loggingService;
    private readonly IRateLimitingService _rateLimitingService;
    private readonly IStartupValidationService _startupValidationService;
    private readonly ILocalizationService _localizationService;
    private readonly IApplicationPaths _applicationPaths;

    public CustomCoverArtController(
        ILibraryDetectionService libraryService,
        ICoverArtService coverArtService,
        IImageProcessingService imageProcessingService,
        IMediaItemService mediaItemService,
        IUserContextService userContextService,
        ILoggingService loggingService,
        IRateLimitingService rateLimitingService,
        IStartupValidationService startupValidationService,
        ILocalizationService localizationService,
        IApplicationPaths applicationPaths)
    {
        _libraryService = libraryService;
        _coverArtService = coverArtService;
        _imageProcessingService = imageProcessingService;
        _mediaItemService = mediaItemService;
        _userContextService = userContextService;
        _loggingService = loggingService;
        _rateLimitingService = rateLimitingService;
        _startupValidationService = startupValidationService;
        _localizationService = localizationService;
        _applicationPaths = applicationPaths;
    }

    /// <summary>Get all available libraries.</summary>
    [HttpGet("libraries")]
    public async Task<ApiResponse<IEnumerable<LibraryInfo>>> GetLibraries()
    {
        try
        {
            var libraries = await _libraryService.GetLibrariesAsync().ConfigureAwait(false);
            return Success(libraries);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to get libraries", ex);
            return Fail<IEnumerable<LibraryInfo>>(_localizationService.GetString("errors.unexpected_error", ex.Message));
        }
    }

    /// <summary>Get a library by ID.</summary>
    [HttpGet("libraries/{libraryId}")]
    public async Task<ApiResponse<LibraryInfo>> GetLibrary(string libraryId)
    {
        try
        {
            var library = await _libraryService.GetLibraryByIdAsync(libraryId).ConfigureAwait(false);
            return library is null
                ? Fail<LibraryInfo>("Library not found")
                : Success(library);
        }
        catch (Exception ex)
        {
            return Fail<LibraryInfo>(ex.Message);
        }
    }

    /// <summary>Generate cover art with the given settings; returns the file path.</summary>
    [HttpPost("generate")]
    public async Task<ApiResponse<string>> GenerateCoverArt([FromBody] CoverArtSettings settings)
    {
        try
        {
            var coverArtPath = await _coverArtService.GenerateCoverArtAsync(settings).ConfigureAwait(false);
            return Success(coverArtPath);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to generate cover art", ex);
            return Fail<string>(ex.Message);
        }
    }

    /// <summary>Generate and stream a preview image.</summary>
    [HttpPost("preview")]
    public async Task<IActionResult> GeneratePreview([FromBody] CoverArtSettings settings)
    {
        try
        {
            var coverArtPath = await _coverArtService.GenerateCoverArtAsync(settings).ConfigureAwait(false);
            var imageBytes = await System.IO.File.ReadAllBytesAsync(coverArtPath).ConfigureAwait(false);
            var contentType = settings.OutputFormat?.ToLowerInvariant() == "gif" ? "image/gif" : "image/png";
            return File(imageBytes, contentType);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to generate preview", ex);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Apply cover art to a library (used by the config page).</summary>
    [HttpPost("apply")]
    public async Task<ApiResponse<bool>> Apply([FromBody] ApplyCoverArtRequest request)
    {
        return await ApplyInternal(request.LibraryId, request.Settings).ConfigureAwait(false);
    }

    /// <summary>Apply cover art to a specific library by id.</summary>
    [HttpPost("libraries/{libraryId}/apply")]
    public async Task<ApiResponse<bool>> ApplyToLibrary(string libraryId, [FromBody] CoverArtSettings settings)
    {
        return await ApplyInternal(libraryId, settings).ConfigureAwait(false);
    }

    private async Task<ApiResponse<bool>> ApplyInternal(string libraryId, CoverArtSettings settings)
    {
        try
        {
            var userName = _userContextService.GetCurrentUserName();
            _loggingService.LogInformation("User {UserName} applying cover art to library {LibraryId}", userName, libraryId);

            var coverArtPath = await _coverArtService.GenerateCoverArtAsync(settings).ConfigureAwait(false);

            var saved = await _coverArtService.SaveCoverArtAsync(libraryId, coverArtPath).ConfigureAwait(false);
            if (!saved)
            {
                return Fail<bool>("Failed to save cover art");
            }

            var updated = await _libraryService.UpdateLibraryCoverArtAsync(libraryId, coverArtPath).ConfigureAwait(false);
            if (!updated)
            {
                return Fail<bool>("Failed to update library cover art");
            }

            return Success(true);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to apply cover art to library {LibraryId}", ex, libraryId);
            return Fail<bool>(ex.Message);
        }
    }

    /// <summary>Upload a background image.</summary>
    [HttpPost("upload")]
    public async Task<ApiResponse<string>> UploadImage(IFormFile file)
    {
        return await SaveUploadAsync(
            file,
            directory: PluginPaths.Uploads(_applicationPaths),
            filePrefix: string.Empty,
            rateLimitAction: "upload",
            maxRequests: 5,
            window: TimeSpan.FromMinutes(1),
            validate: f => _imageProcessingService.ValidateFileAsync(f)).ConfigureAwait(false);
    }

    /// <summary>Upload a custom font.</summary>
    [HttpPost("uploadFont")]
    public async Task<ApiResponse<string>> UploadFont(IFormFile file)
    {
        return await SaveUploadAsync(
            file,
            directory: PluginPaths.Fonts(_applicationPaths),
            filePrefix: "font_",
            rateLimitAction: "uploadFont",
            maxRequests: 3,
            window: TimeSpan.FromMinutes(5),
            validate: ValidateFontAsync).ConfigureAwait(false);
    }

    private Task<ValidationResult> ValidateFontAsync(IFormFile file)
    {
        var result = new ValidationResult { IsValid = true };

        var allowedExtensions = new[] { ".ttf", ".otf", ".woff", ".woff2" };
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!allowedExtensions.Contains(extension))
        {
            result.IsValid = false;
            result.ErrorMessage = _localizationService.GetString("errors.invalid_file_format");
        }
        else if (file.Length > 5 * 1024 * 1024)
        {
            result.IsValid = false;
            result.ErrorMessage = _localizationService.GetString("errors.font_too_large");
        }

        return Task.FromResult(result);
    }

    private async Task<ApiResponse<string>> SaveUploadAsync(
        IFormFile file,
        string directory,
        string filePrefix,
        string rateLimitAction,
        int maxRequests,
        TimeSpan window,
        Func<IFormFile, Task<ValidationResult>> validate)
    {
        var userName = _userContextService.GetCurrentUserName();
        var clientId = _userContextService.GetCurrentUserId()
            ?? HttpContext.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";

        try
        {
            if (!_rateLimitingService.IsAllowed(clientId, rateLimitAction, maxRequests, window))
            {
                _loggingService.LogWarning("Rate limit exceeded for {Action} by {UserName}", rateLimitAction, userName);
                return Fail<string>(_localizationService.GetString("errors.too_many_uploads"));
            }

            if (file is null || file.Length == 0)
            {
                return Fail<string>(_localizationService.GetString("errors.no_file_uploaded"));
            }

            var validation = await validate(file).ConfigureAwait(false);
            if (!validation.IsValid)
            {
                return Fail<string>(validation.ErrorMessage);
            }

            _rateLimitingService.RecordRequest(clientId, rateLimitAction);

            Directory.CreateDirectory(directory);
            var sanitized = ImageProcessingService.SanitizeFileName(file.FileName);
            var fileName = $"{filePrefix}{Guid.NewGuid():N}_{sanitized}";
            var filePath = Path.Combine(directory, fileName);

            await using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream).ConfigureAwait(false);
            }

            _loggingService.LogInformation("User {UserName} uploaded {FileName}", userName, fileName);
            return Success(filePath);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Upload failed for user {UserName}", ex, userName);
            return Fail<string>(_localizationService.GetString("errors.error_saving_file", ex.Message));
        }
    }

    /// <summary>Get the dimensions of an uploaded image.</summary>
    [HttpPost("getImageDimensions")]
    public async Task<ApiResponse<ImageDimensionsDto>> GetImageDimensions(IFormFile file)
    {
        try
        {
            if (file is null || file.Length == 0)
            {
                return Fail<ImageDimensionsDto>(_localizationService.GetString("errors.no_file_uploaded"));
            }

            using var memoryStream = new MemoryStream();
            await file.CopyToAsync(memoryStream).ConfigureAwait(false);

            var (width, height) = await _imageProcessingService
                .GetImageDimensionsAsync(memoryStream.ToArray()).ConfigureAwait(false);

            return Success(new ImageDimensionsDto { Width = width, Height = height });
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to get image dimensions", ex);
            return Fail<ImageDimensionsDto>(ex.Message);
        }
    }

    /// <summary>Get media items from a specific library.</summary>
    [HttpGet("libraries/{libraryId}/items")]
    public async Task<ApiResponse<IEnumerable<MediaItemInfo>>> GetLibraryItems(string libraryId)
    {
        try
        {
            var items = await _mediaItemService.GetLibraryItemsAsync(libraryId).ConfigureAwait(false);
            return Success(items);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to get library items", ex);
            return Fail<IEnumerable<MediaItemInfo>>(ex.Message);
        }
    }

    /// <summary>Search media items across libraries.</summary>
    [HttpPost("search/items")]
    public async Task<ApiResponse<ItemSearchResponse>> SearchItems([FromBody] ItemSearchRequest request)
    {
        try
        {
            var result = await _mediaItemService.SearchItemsAsync(request).ConfigureAwait(false);
            return Success(result);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to search items", ex);
            return Fail<ItemSearchResponse>(ex.Message);
        }
    }

    /// <summary>Get the cover art image for a specific media item.</summary>
    [HttpGet("items/{itemId}/cover")]
    public async Task<IActionResult> GetItemCoverArt(string itemId)
    {
        try
        {
            var coverArtBytes = await _mediaItemService.GetItemCoverArtAsync(itemId).ConfigureAwait(false);
            if (coverArtBytes is null || coverArtBytes.Length == 0)
            {
                return NotFound(new { error = "Cover art not found" });
            }

            return File(coverArtBytes, DetectImageContentType(coverArtBytes));
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to get item cover art", ex);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Copies a library item's primary image into the plugin's uploads directory
    /// and returns the resulting path, so it can be used as a background. (The
    /// path must live inside the plugin data dir to pass the sandbox check in
    /// cover-art generation — a raw item URL would be rejected.)
    /// </summary>
    [HttpPost("items/{itemId}/useAsBackground")]
    public async Task<ApiResponse<string>> UseItemAsBackground(string itemId)
    {
        try
        {
            var sourcePath = await _mediaItemService.GetItemImageSourcePathAsync(itemId).ConfigureAwait(false);
            if (string.IsNullOrEmpty(sourcePath) || !System.IO.File.Exists(sourcePath))
            {
                return Fail<string>("Selected item has no usable image");
            }

            var uploads = PluginPaths.Uploads(_applicationPaths);
            Directory.CreateDirectory(uploads);

            var extension = Path.GetExtension(sourcePath);
            if (string.IsNullOrEmpty(extension))
            {
                extension = ".jpg";
            }

            var destination = Path.Combine(uploads, $"item_{Guid.NewGuid():N}{extension}");
            System.IO.File.Copy(sourcePath, destination, overwrite: true);

            return Success(destination);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to use item {ItemId} as background", ex, itemId);
            return Fail<string>(ex.Message);
        }
    }

    /// <summary>Get recent media items.</summary>
    [HttpGet("recent/items")]
    public async Task<ApiResponse<IEnumerable<MediaItemInfo>>> GetRecentItems([FromQuery] int count = 20)
    {
        try
        {
            var items = await _mediaItemService.GetRecentItemsAsync(count).ConfigureAwait(false);
            return Success(items);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to get recent items", ex);
            return Fail<IEnumerable<MediaItemInfo>>(ex.Message);
        }
    }

    /// <summary>Health check endpoint for monitoring plugin status.</summary>
    [HttpGet("health")]
    public async Task<ApiResponse<object>> HealthCheck()
    {
        try
        {
            if (_startupValidationService.IsPluginReady)
            {
                return Success<object>(new { PluginReady = true, Version = Plugin.Instance?.Version.ToString() });
            }

            var config = await _startupValidationService.ValidateConfigurationAsync().ConfigureAwait(false);
            var deps = await _startupValidationService.ValidateDependenciesAsync().ConfigureAwait(false);
            var perms = await _startupValidationService.ValidatePermissionsAsync().ConfigureAwait(false);

            return Success<object>(new
            {
                PluginReady = config.IsValid && deps.IsValid && perms.IsValid,
                Version = Plugin.Instance?.Version.ToString(),
                ValidationResults = new { Configuration = config, Dependencies = deps, Permissions = perms }
            });
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Health check failed", ex);
            return Fail<object>(ex.Message);
        }
    }

    /// <summary>Get current language and available translations.</summary>
    [HttpGet("language")]
    public ApiResponse<object> GetLanguageInfo()
    {
        try
        {
            return Success<object>(new
            {
                CurrentLanguage = _localizationService.GetCurrentLanguage(),
                SupportedLanguages = _localizationService.GetSupportedLanguages()
            });
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to get language info", ex);
            return Fail<object>(ex.Message);
        }
    }

    private static ApiResponse<T> Success<T>(T data) => new() { Success = true, Data = data };

    private static ApiResponse<T> Fail<T>(string message) => new() { Success = false, ErrorMessage = message };

    /// <summary>Detects an image content type from the file header bytes.</summary>
    private static string DetectImageContentType(byte[] imageBytes)
    {
        if (imageBytes.Length >= 4 &&
            imageBytes[0] == 0x89 && imageBytes[1] == 0x50 && imageBytes[2] == 0x4E && imageBytes[3] == 0x47)
        {
            return "image/png";
        }

        if (imageBytes.Length >= 4 &&
            imageBytes[0] == 0x47 && imageBytes[1] == 0x49 && imageBytes[2] == 0x46 && imageBytes[3] == 0x38)
        {
            return "image/gif";
        }

        if (imageBytes.Length >= 3 &&
            imageBytes[0] == 0xFF && imageBytes[1] == 0xD8 && imageBytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (imageBytes.Length >= 12 &&
            imageBytes[0] == 0x52 && imageBytes[1] == 0x49 && imageBytes[2] == 0x46 && imageBytes[3] == 0x46 &&
            imageBytes[8] == 0x57 && imageBytes[9] == 0x45 && imageBytes[10] == 0x42 && imageBytes[11] == 0x50)
        {
            return "image/webp";
        }

        return "image/jpeg";
    }
}
