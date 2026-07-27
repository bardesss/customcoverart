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
    public float BackgroundDim { get; set; } = 0.4f;
    public float BackgroundBlur { get; set; } = 0f;
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
    
    // Backward compatibility property
    public bool IsGif => OutputFormat?.ToLowerInvariant() == "gif";
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
