namespace CustomCoverArt.Models;

/// <summary>
/// Represents a Jellyfin library
/// </summary>
public class LibraryInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // Movies, TV Shows, Music, etc.
    public string? CurrentCoverArtPath { get; set; }
    public bool HasCustomCover { get; set; }
    public DateTime? LastModified { get; set; }
}

/// <summary>
/// Cover art generation settings
/// </summary>
public class CoverArtSettings
{
    public string Title { get; set; } = "Movies";
    public string BackgroundImagePath { get; set; } = string.Empty;
    public int TextSize { get; set; } = 120;
    public FontWeight TextWeight { get; set; } = FontWeight.Normal;
    public string CustomFontPath { get; set; } = string.Empty;
    public float BackgroundDim { get; set; } = 0.25f;
    public float BackgroundBlur { get; set; } = 0f;

    /// <summary>How the background image fills the canvas: "cover" (fill + crop),
    /// "contain" (fit whole image, letterboxed) or "stretch" (distort to fill).</summary>
    public string BackgroundFit { get; set; } = "cover";
    public string TextColor { get; set; } = "#ffffff";
    public string DimColor { get; set; } = "#000000";
    public GradientSettings? BackgroundGradient { get; set; }
    public TextAlign TextAlign { get; set; } = TextAlign.Center;
    public TextBaseline TextBaseline { get; set; } = TextBaseline.Middle;
    public float TextPadding { get; set; } = 0.05f;
    
    // Text Effects
    public bool TextShadow { get; set; } = false;
    public string TextShadowColor { get; set; } = "#000000";
    public int TextShadowBlur { get; set; } = 4;
    public int TextShadowOffsetX { get; set; } = 2;
    public int TextShadowOffsetY { get; set; } = 2;
    public bool TextOutline { get; set; } = false;
    public string TextOutlineColor { get; set; } = "#000000";
    public int TextOutlineWidth { get; set; } = 2;
    
    // Export Settings
    public int ExportWidth { get; set; } = 1400;
    public int ExportHeight { get; set; } = 1400;
    public float ExportScale { get; set; } = 1.0f;
    public string OutputFormat { get; set; } = "auto";
    public string DimensionPreset { get; set; } = "cover";

    // Background source: "upload" (default), "libraryPoster", or "collage".
    public string BackgroundSource { get; set; } = "upload";
    public CollageSettings? Collage { get; set; }
    public AnimationSettings? Animation { get; set; }

    // Backward compatibility property
    public bool IsGif => OutputFormat?.ToLowerInvariant() == "gif";
}

/// <summary>String constants for CoverArtSettings.BackgroundSource.</summary>
public static class BackgroundSources
{
    public const string Upload = "upload";
    public const string LibraryPoster = "libraryPoster";
    public const string Collage = "collage";
}

/// <summary>Auto poster-collage background settings.</summary>
public class CollageSettings
{
    /// <summary>The target whose child items supply the posters (a library/collection/playlist id).</summary>
    public string SourceId { get; set; } = string.Empty;
    public string SourceType { get; set; } = "library";
    /// <summary>Grid density preset: "sparse", "medium" or "dense".</summary>
    public string Density { get; set; } = "medium";
    /// <summary>Deterministic shuffle seed so preview and apply match; the Shuffle button changes it.</summary>
    public int Seed { get; set; } = 0;
}

/// <summary>Animated-GIF export settings.</summary>
public class AnimationSettings
{
    public bool Enabled { get; set; } = false;
    /// <summary>Ken Burns pan/zoom on the (static) background. Ignored when the background is itself an animated GIF.</summary>
    public bool KenBurns { get; set; } = false;
    /// <summary>Fractional zoom over the whole animation (0.15 = 15%).</summary>
    public float ZoomAmount { get; set; } = 0.15f;
    /// <summary>"in" or "out".</summary>
    public string Direction { get; set; } = "in";
    public int FrameCount { get; set; } = 20;
    /// <summary>Per-frame delay in milliseconds.</summary>
    public int DelayMs { get; set; } = 80;
    public bool Loop { get; set; } = true;
}

/// <summary>A saved design template. Title and target are intentionally excluded from the design.</summary>
public class SavedTemplate
{
    public string Name { get; set; } = string.Empty;
    public CoverArtSettings Settings { get; set; } = new();
}

/// <summary>A single batch-apply target reference.</summary>
public class BatchTargetRef
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = "library";
}

/// <summary>Request to apply one design to many targets at once.</summary>
public class BatchApplyRequest
{
    /// <summary>Name of a saved template to use; if null, <see cref="Settings"/> is used.</summary>
    public string? TemplateName { get; set; }
    public CoverArtSettings? Settings { get; set; }
    public List<BatchTargetRef> Targets { get; set; } = new();
}

/// <summary>Per-target outcome from a batch apply.</summary>
public class BatchApplyResult
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Dimension presets for different Jellyfin cover art types
/// </summary>
public static class DimensionPresets
{
    public static readonly Dictionary<string, (int width, int height, string description)> Presets = new()
    {
        ["cover"] = (1400, 1400, "Square Cover (Music Albums)"),
        ["poster"] = (1000, 1500, "Portrait Poster (Movies/TV)"),
        ["banner"] = (1920, 540, "Wide Banner (TV Shows)"),
        ["custom"] = (960, 540, "Custom Dimensions")
    };
    
    public static (int width, int height) GetPreset(string presetName)
    {
        return Presets.TryGetValue(presetName?.ToLowerInvariant() ?? "cover", out var preset) 
            ? (preset.width, preset.height) 
            : (1400, 1400);
    }
    
    public static string GetDescription(string presetName)
    {
        return Presets.TryGetValue(presetName?.ToLowerInvariant() ?? "cover", out var preset) 
            ? preset.description 
            : "Square Cover (Music Albums)";
    }
}

/// <summary>
/// Text alignment options
/// </summary>
public enum TextAlign
{
    Left,
    Center,
    Right
}

/// <summary>
/// Text baseline options
/// </summary>
public enum TextBaseline
{
    Top,
    Middle,
    Bottom
}

/// <summary>
/// Font weight options
/// </summary>
public enum FontWeight
{
    Light = 300,
    Normal = 400,
    Medium = 500,
    SemiBold = 600,
    Bold = 700,
    ExtraBold = 800
}

/// <summary>
/// Gradient settings for background
/// </summary>
public class GradientSettings
{
    public bool IsEnabled { get; set; } = false;
    public GradientType Type { get; set; } = GradientType.Linear;
    public string StartColor { get; set; } = "#000000";
    public string EndColor { get; set; } = "#ffffff";
    public float Angle { get; set; } = 0f; // For linear gradients (0-360 degrees)
    public float CenterX { get; set; } = 0.5f; // For radial gradients (0-1)
    public float CenterY { get; set; } = 0.5f; // For radial gradients (0-1)
    public float Radius { get; set; } = 0.5f; // For radial gradients (0-1)

    /// <summary>Ordered colour stops (2+). Takes priority over Start/End colour.</summary>
    public List<GradientStop> Stops { get; set; } = new();
}

/// <summary>A single gradient colour stop.</summary>
public class GradientStop
{
    public string Color { get; set; } = "#000000";
    public float Position { get; set; } = 0f; // 0..1
}

/// <summary>
/// Gradient type options
/// </summary>
public enum GradientType
{
    Linear,
    Radial
}

/// <summary>
/// API response wrapper
/// </summary>
public class ApiResponse<T>
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public T? Data { get; set; }
}

/// <summary>
/// Request model for applying cover art
/// </summary>
public class ApplyCoverArtRequest
{
    public string LibraryId { get; set; } = string.Empty;
    public CoverArtSettings Settings { get; set; } = new();
}

/// <summary>
/// Image dimensions returned by the getImageDimensions endpoint.
/// (A named DTO instead of a ValueTuple so it serializes as width/height.)
/// </summary>
public class ImageDimensionsDto
{
    public int Width { get; set; }
    public int Height { get; set; }
}

/// <summary>
/// File validation result
/// </summary>
public class ValidationResult
{
    public bool IsValid { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string? WarningMessage { get; set; }
}

/// <summary>
/// Information about a media item for cover art browsing
/// </summary>
public class MediaItemInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // "Movie", "Series", "Season", "Episode"
    public string? Year { get; set; }
    public string? Overview { get; set; }
    public string? ThumbnailUrl { get; set; }
    public string? CoverArtUrl { get; set; }
    public string LibraryId { get; set; } = string.Empty;
    public string LibraryName { get; set; } = string.Empty;
    public string? SeriesName { get; set; } // For seasons/episodes
    public int? SeasonNumber { get; set; } // For seasons/episodes
    public int? EpisodeNumber { get; set; } // For episodes
}

/// <summary>
/// Request for searching media items
/// </summary>
public class ItemSearchRequest
{
    public string Query { get; set; } = string.Empty;
    public string? LibraryId { get; set; }
    public string[]? ItemTypes { get; set; } // ["Movie", "Series", "Season"]
    public int PageSize { get; set; } = 20;
    public int Page { get; set; } = 1;
}

/// <summary>
/// Response for item search with pagination
/// </summary>
public class ItemSearchResponse
{
    public IEnumerable<MediaItemInfo> Items { get; set; } = Enumerable.Empty<MediaItemInfo>();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
}
