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

    // Returns true if the caller has exceeded the limit for this action.
    // Records the attempt on every call (so failed attempts also count).
    private bool RateLimited(string action, int maxRequests, TimeSpan window)
    {
        var clientId = _userContextService.GetCurrentUserId()
            ?? HttpContext.Connection.RemoteIpAddress?.ToString()
            ?? "unknown";

        if (!_rateLimitingService.IsAllowed(clientId, action, maxRequests, window))
        {
            return true;
        }

        _rateLimitingService.RecordRequest(clientId, action);
        return false;
    }

    /// <summary>Get cover-art targets of a type: "library", "collection" or "playlist".</summary>
    [HttpGet("targets/{type}")]
    public async Task<ApiResponse<IEnumerable<LibraryInfo>>> GetTargets(string type)
    {
        try
        {
            var targets = await _libraryService.GetTargetsAsync(type).ConfigureAwait(false);
            return Success(targets);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to get targets", ex);
            return Fail<IEnumerable<LibraryInfo>>("Failed to load targets.");
        }
    }

    /// <summary>Generate and stream a preview image.</summary>
    [HttpPost("preview")]
    public async Task<IActionResult> GeneratePreview([FromBody] CoverArtSettings settings)
    {
        // Higher limit: the config page renders a live preview on every adjustment.
        if (RateLimited("preview", maxRequests: 240, TimeSpan.FromMinutes(1)))
        {
            return StatusCode(429, new { error = "Too many requests" });
        }

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
            return BadRequest(new { error = "Failed to generate preview." });
        }
    }

    /// <summary>Generate and stream a preview image from a document-native design.</summary>
    [HttpPost("document/preview")]
    public async Task<IActionResult> GeneratePreviewDocument([FromBody] CoverDocument document)
    {
        // Higher limit: the canvas renders a live preview on every adjustment.
        if (RateLimited("preview", maxRequests: 240, TimeSpan.FromMinutes(1)))
        {
            return StatusCode(429, new { error = "Too many requests" });
        }

        try
        {
            var coverArtPath = await _coverArtService.GenerateFromDocumentAsync(document).ConfigureAwait(false);
            var imageBytes = await System.IO.File.ReadAllBytesAsync(coverArtPath).ConfigureAwait(false);
            var contentType = document.Canvas.Format?.ToLowerInvariant() == "gif" ? "image/gif" : "image/png";
            return File(imageBytes, contentType);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to generate document preview", ex);
            return BadRequest(new { error = "Failed to generate preview." });
        }
    }

    /// <summary>Apply a document-native design to a library.</summary>
    [HttpPost("document/apply")]
    public async Task<ApiResponse<bool>> ApplyDocument([FromBody] ApplyDocumentRequest request)
    {
        // Applying renders a full cover (up to a 30-frame GIF) and writes to disk.
        if (RateLimited("apply", maxRequests: 30, TimeSpan.FromMinutes(1)))
        {
            return Fail<bool>(_localizationService.GetString("errors.too_many_uploads"));
        }

        // Validate the library id up front, before it is used anywhere.
        if (!Guid.TryParse(request.LibraryId, out _))
        {
            return Fail<bool>("Invalid library id");
        }

        try
        {
            var userName = _userContextService.GetCurrentUserName() ?? "unknown";
            _loggingService.LogInformation("User {UserName} applying document cover art to library {LibraryId}", userName, request.LibraryId);

            var coverArtPath = await _coverArtService.GenerateFromDocumentAsync(request.Document).ConfigureAwait(false);

            // Persist into the per-library folder; apply THAT stable path.
            var savedPath = await _coverArtService.SaveCoverArtAsync(request.LibraryId, coverArtPath).ConfigureAwait(false);
            if (savedPath is null)
            {
                return Fail<bool>("Failed to save cover art");
            }

            // Preserve the target's current image once, so Restore can undo later.
            await _libraryService.BackupCurrentCoverArtAsync(request.LibraryId).ConfigureAwait(false);

            var updated = await _libraryService.UpdateLibraryCoverArtAsync(request.LibraryId, savedPath).ConfigureAwait(false);
            if (!updated)
            {
                return Fail<bool>("Failed to update library cover art");
            }

            return Success(true);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to apply document to {LibraryId}", ex, request.LibraryId);
            return Fail<bool>("Failed to apply cover art.");
        }
    }

    /// <summary>Apply cover art to a library (used by the config page).</summary>
    [HttpPost("apply")]
    public async Task<ApiResponse<bool>> Apply([FromBody] ApplyCoverArtRequest request)
    {
        // Applying renders a full cover (up to a 30-frame GIF) and writes to disk.
        if (RateLimited("apply", maxRequests: 30, TimeSpan.FromMinutes(1)))
        {
            return Fail<bool>(_localizationService.GetString("errors.too_many_uploads"));
        }

        return await ApplyInternal(request.LibraryId, request.Settings).ConfigureAwait(false);
    }

    private async Task<ApiResponse<bool>> ApplyInternal(string libraryId, CoverArtSettings settings)
    {
        // Validate the library id up front, before it is used anywhere.
        if (!Guid.TryParse(libraryId, out _))
        {
            return Fail<bool>("Invalid library id");
        }

        try
        {
            var userName = _userContextService.GetCurrentUserName() ?? "unknown";
            _loggingService.LogInformation("User {UserName} applying cover art to library {LibraryId}", userName, libraryId);

            var coverArtPath = await _coverArtService.GenerateCoverArtAsync(settings).ConfigureAwait(false);

            // Persist into the per-library folder; apply THAT stable path.
            var savedPath = await _coverArtService.SaveCoverArtAsync(libraryId, coverArtPath).ConfigureAwait(false);
            if (savedPath is null)
            {
                return Fail<bool>("Failed to save cover art");
            }

            // Preserve the target's current image once, so Restore can undo later.
            await _libraryService.BackupCurrentCoverArtAsync(libraryId).ConfigureAwait(false);

            var updated = await _libraryService.UpdateLibraryCoverArtAsync(libraryId, savedPath).ConfigureAwait(false);
            if (!updated)
            {
                return Fail<bool>("Failed to update library cover art");
            }

            return Success(true);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to apply cover art to library {LibraryId}", ex, libraryId);
            return Fail<bool>("Failed to apply cover art.");
        }
    }

    /// <summary>Whether a restore point (original cover backup) exists for a target.</summary>
    [HttpGet("targets/{id}/backup")]
    public ApiResponse<bool> HasBackup(string id)
    {
        if (!Guid.TryParse(id, out _))
        {
            return Fail<bool>("Invalid target id");
        }

        return Success(_libraryService.HasBackup(id));
    }

    /// <summary>Restore a target's original (pre-plugin) primary image.</summary>
    [HttpPost("targets/{type}/{id}/restore")]
    public async Task<ApiResponse<bool>> RestoreOriginal(string type, string id)
    {
        if (!Guid.TryParse(id, out _))
        {
            return Fail<bool>("Invalid target id");
        }

        try
        {
            var ok = await _libraryService.RestoreOriginalCoverArtAsync(id).ConfigureAwait(false);
            return ok ? Success(true) : Fail<bool>("No original cover backup found for this target.");
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to restore original cover for {Id}", ex, id);
            return Fail<bool>("Failed to restore original cover.");
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

    private async Task<ValidationResult> ValidateFontAsync(IFormFile file)
    {
        var result = new ValidationResult { IsValid = true };

        var allowedExtensions = new[] { ".ttf", ".otf", ".ttc", ".woff", ".woff2" };
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!allowedExtensions.Contains(extension))
        {
            result.IsValid = false;
            result.ErrorMessage = _localizationService.GetString("errors.invalid_file_format");
            return result;
        }

        if (file.Length > 5 * 1024 * 1024)
        {
            result.IsValid = false;
            result.ErrorMessage = _localizationService.GetString("errors.font_too_large");
            return result;
        }

        // Verify a real font signature (magic bytes), not just the extension.
        var header = new byte[4];
        await using var stream = file.OpenReadStream();
        var read = await stream.ReadAsync(header.AsMemory(0, 4)).ConfigureAwait(false);
        if (read < 4 || !IsFontSignature(header))
        {
            result.IsValid = false;
            result.ErrorMessage = _localizationService.GetString("errors.invalid_file_format");
        }

        return result;
    }

    private static bool IsFontSignature(byte[] h)
    {
        bool Eq(byte a, byte b, byte c, byte d) => h[0] == a && h[1] == b && h[2] == c && h[3] == d;
        return Eq(0x00, 0x01, 0x00, 0x00) // TrueType (TTF)
            || Eq(0x4F, 0x54, 0x54, 0x4F) // 'OTTO' (OpenType/CFF)
            || Eq(0x74, 0x72, 0x75, 0x65) // 'true'
            || Eq(0x74, 0x74, 0x63, 0x66) // 'ttcf' (font collection)
            || Eq(0x77, 0x4F, 0x46, 0x46) // 'wOFF'
            || Eq(0x77, 0x4F, 0x46, 0x32); // 'wOF2'
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
        var userName = _userContextService.GetCurrentUserName() ?? "unknown";
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
            return Fail<string>("Could not save the uploaded file.");
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
            return Fail<ItemSearchResponse>("Failed to search items.");
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
            return Fail<object>("Health check failed.");
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
            return Fail<object>("Failed to get language info.");
        }
    }

    /// <summary>
    /// Streams a bundled Noto Sans weight so the config-page canvas can register
    /// the SAME faces the server renders with (via the FontFace API), keeping the
    /// live client preview visually aligned with the authoritative server output.
    /// Inherits the class-level RequiresElevation policy — no anonymous access.
    /// </summary>
    [HttpGet("font/{weight:int}")]
    public IActionResult GetFont(int weight)
    {
        var face = weight switch
        {
            300 => "NotoSans-Light",
            500 => "NotoSans-Medium",
            600 => "NotoSans-SemiBold",
            700 => "NotoSans-Bold",
            800 => "NotoSans-ExtraBold",
            _ => "NotoSans-Regular"
        };

        var res = $"CustomCoverArt.Resources.fonts.{face}.ttf";
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream(res);
        if (stream is null)
        {
            return NotFound();
        }

        return File(stream, "font/ttf");
    }

    /// <summary>
    /// Streams a previously uploaded image back to the config page so a logo layer
    /// restored from a saved template can be drawn on the client canvas.
    ///
    /// The canvas may only ever be fed blob: URLs (a tainted canvas breaks
    /// getImageData), which is why the page fetches this endpoint as a Blob rather
    /// than pointing an &lt;img src&gt; at a server path.
    ///
    /// Scope is deliberately narrower than the plugin data dir: only the uploads
    /// folder, so this can never be turned into a reader for backups or generated
    /// output. Inherits the class-level RequiresElevation policy.
    /// </summary>
    [HttpGet("layerImage")]
    public IActionResult GetLayerImage([FromQuery] string path)
    {
        var uploads = Path.GetFullPath(PluginPaths.Uploads(_applicationPaths))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        string full;
        try
        {
            full = Path.GetFullPath(path ?? string.Empty);
        }
        catch
        {
            return BadRequest();
        }

        var comparison = System.OperatingSystem.IsWindows()
            ? System.StringComparison.OrdinalIgnoreCase
            : System.StringComparison.Ordinal;

        if (!full.StartsWith(uploads, comparison) || !System.IO.File.Exists(full))
        {
            return NotFound();
        }

        var contentType = Path.GetExtension(full).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => null
        };

        if (contentType is null)
        {
            return NotFound();
        }

        return PhysicalFile(full, contentType);
    }

    /// <summary>Strip title and target-specific fields so a template is reusable across targets.</summary>
    public static SavedTemplate NormalizeTemplate(SavedTemplate template)
    {
        template.Name = (template.Name ?? string.Empty).Trim();
        template.Settings.Title = string.Empty;
        if (template.Settings.Collage is not null)
        {
            template.Settings.Collage.SourceId = string.Empty;
        }

        if (template.Document is not null)
        {
            // A client can POST a Document with null Layers/Background (System.Text.Json
            // does not enforce non-null on non-nullable reference types); normalize it
            // through the same helper GenerateFromDocumentAsync uses before dereferencing.
            DocumentMigration.Normalize(template.Document);

            var titleLayer = template.Document.Layers.FirstOrDefault(l => l.Id == "title");
            if (titleLayer is not null)
            {
                titleLayer.Content = string.Empty;
            }

            if (template.Document.Background.Collage is not null)
            {
                template.Document.Background.Collage.SourceId = string.Empty;
            }
        }

        return template;
    }

    /// <summary>Clone base settings for one batch target: title = target name, collage source = target id.</summary>
    public static CoverArtSettings BuildBatchSettings(CoverArtSettings baseSettings, string targetName, string targetId)
    {
        // Shallow JSON clone to avoid mutating the shared base settings.
        var json = System.Text.Json.JsonSerializer.Serialize(baseSettings);
        var clone = System.Text.Json.JsonSerializer.Deserialize<CoverArtSettings>(json) ?? new CoverArtSettings();
        clone.Title = targetName;
        if (clone.BackgroundSource == BackgroundSources.Collage && clone.Collage is not null)
        {
            clone.Collage.SourceId = targetId;
        }
        return clone;
    }

    /// <summary>Apply one design to many targets, auto-titling each from the target's name.</summary>
    [HttpPost("batchApply")]
    public async Task<ApiResponse<List<BatchApplyResult>>> BatchApply([FromBody] BatchApplyRequest request)
    {
        if (request is null || request.Targets.Count == 0)
        {
            return Fail<List<BatchApplyResult>>("No targets selected.");
        }

        // Each target is a full render + disk write; bound the fan-out and the rate.
        if (request.Targets.Count > 100)
        {
            return Fail<List<BatchApplyResult>>("Too many targets (max 100).");
        }

        if (RateLimited("batchApply", maxRequests: 5, TimeSpan.FromMinutes(1)))
        {
            return Fail<List<BatchApplyResult>>(_localizationService.GetString("errors.too_many_uploads"));
        }

        // Resolve the base design: a named template, or inline settings.
        CoverArtSettings? baseSettings = request.Settings;
        if (!string.IsNullOrWhiteSpace(request.TemplateName))
        {
            var tpl = Plugin.Instance?.Configuration.Templates
                .FirstOrDefault(t => string.Equals(t.Name, request.TemplateName, StringComparison.OrdinalIgnoreCase));
            if (tpl is null)
            {
                return Fail<List<BatchApplyResult>>("Template not found: " + request.TemplateName);
            }
            baseSettings = tpl.Settings;
        }

        if (baseSettings is null)
        {
            return Fail<List<BatchApplyResult>>("No template or settings provided.");
        }

        var results = new List<BatchApplyResult>();
        foreach (var target in request.Targets)
        {
            var result = new BatchApplyResult { Id = target.Id };
            if (!Guid.TryParse(target.Id, out _))
            {
                result.Success = false;
                result.Error = "Invalid id";
                results.Add(result);
                continue;
            }

            var info = await _libraryService.GetLibraryByIdAsync(target.Id).ConfigureAwait(false);
            var name = info?.Name ?? "Cover";
            result.Name = name;

            var settings = BuildBatchSettings(baseSettings, name, target.Id);
            var applied = await ApplyInternal(target.Id, settings).ConfigureAwait(false);
            result.Success = applied.Success;
            result.Error = applied.ErrorMessage;
            results.Add(result);
        }

        return Success(results);
    }

    /// <summary>List saved design templates.</summary>
    [HttpGet("templates")]
    public ApiResponse<List<SavedTemplate>> GetTemplates()
    {
        var list = Plugin.Instance?.Configuration.Templates ?? new List<SavedTemplate>();
        return Success(list);
    }

    /// <summary>Save (upsert by name) a design template.</summary>
    [HttpPost("templates")]
    public ApiResponse<bool> SaveTemplate([FromBody] SavedTemplate template)
    {
        if (template is null || string.IsNullOrWhiteSpace(template.Name))
        {
            return Fail<bool>("Template name is required.");
        }

        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null)
        {
            return Fail<bool>("Plugin not initialized.");
        }

        var normalized = NormalizeTemplate(template);
        cfg.Templates.RemoveAll(t => string.Equals(t.Name, normalized.Name, StringComparison.OrdinalIgnoreCase));
        cfg.Templates.Add(normalized);
        Plugin.Instance!.SaveConfiguration();
        return Success(true);
    }

    /// <summary>Delete a design template by name.</summary>
    [HttpDelete("templates/{name}")]
    public ApiResponse<bool> DeleteTemplate(string name)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null)
        {
            return Fail<bool>("Plugin not initialized.");
        }

        cfg.Templates.RemoveAll(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        Plugin.Instance!.SaveConfiguration();
        return Success(true);
    }

    private static ApiResponse<T> Success<T>(T data) => new() { Success = true, Data = data };

    private static ApiResponse<T> Fail<T>(string message) => new() { Success = false, ErrorMessage = message };
}
