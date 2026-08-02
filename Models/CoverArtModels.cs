namespace CustomCoverArt.Models;

public class LibraryInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // Movies, TV Shows, Music, etc.
    public string? CurrentCoverArtPath { get; set; }
    public bool HasCustomCover { get; set; }
    public DateTime? LastModified { get; set; }
}

/// <summary>Cover-art generation settings, sent per-request from the config page.</summary>
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

    // Background source: "upload" (default) or "collage".
    public string BackgroundSource { get; set; } = "upload";
    public CollageSettings? Collage { get; set; }
    public AnimationSettings? Animation { get; set; }
}

/// <summary>String constants for CoverArtSettings.BackgroundSource.</summary>
/// <summary>
/// What supplies the background. One value answers the question — before v3.3.0.0 this was
/// split across Source and a separate Gradient.IsEnabled checkbox, so "what is my
/// background?" had two overlapping answers.
/// </summary>
public static class BackgroundSources
{
    /// <summary>An image: uploaded from disk, or a library poster copied into the uploads dir.</summary>
    public const string Upload = "upload";
    public const string Collage = "collage";
    public const string Gradient = "gradient";
    public const string Solid = "solid";
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

    /// <summary>Document-native design (Phase 1+). Null for templates saved before the canvas
    /// engine shipped; the client migrates those from <see cref="Settings"/> (Task 9).</summary>
    public CoverDocument? Document { get; set; }
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
    /// <summary>Name of a saved template to use; if null, <see cref="Document"/> or <see cref="Settings"/> is used.</summary>
    public string? TemplateName { get; set; }

    /// <summary>
    /// The layered design to apply. Takes precedence over <see cref="Settings"/>, which
    /// can only express a single title layer — batching a multi-layer design through
    /// the flat model would silently drop every extra text layer and logo.
    /// </summary>
    public CoverDocument? Document { get; set; }

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

public enum TextAlign
{
    Left,
    Center,
    Right
}

public enum TextBaseline
{
    Top,
    Middle,
    Bottom
}

public enum FontWeight
{
    Light = 300,
    Normal = 400,
    Medium = 500,
    SemiBold = 600,
    Bold = 700,
    ExtraBold = 800
}

public class GradientSettings
{
    public bool IsEnabled { get; set; } = false;
    public GradientType Type { get; set; } = GradientType.Linear;
    public string StartColor { get; set; } = "#aa5cc3"; // Jellyfin brand purple
    public string EndColor { get; set; } = "#00a4dc";   // Jellyfin brand blue
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

public enum GradientType
{
    Linear,
    Radial
}

public class ApiResponse<T>
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public T? Data { get; set; }
}

public class ApplyCoverArtRequest
{
    public string LibraryId { get; set; } = string.Empty;
    public CoverArtSettings Settings { get; set; } = new();
}

/// <summary>Request to render and apply a document-native design to a library.</summary>
public class ApplyDocumentRequest
{
    public string LibraryId { get; set; } = string.Empty;
    public CoverDocument Document { get; set; } = new();
}

public class ValidationResult
{
    public bool IsValid { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public string? WarningMessage { get; set; }
}

public class MediaItemInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // "Movie", "Series", "Season", "Episode"
    public string? Year { get; set; }
    public string? Overview { get; set; }
    public string LibraryId { get; set; } = string.Empty;
    public string LibraryName { get; set; } = string.Empty;
    public string? SeriesName { get; set; } // For seasons/episodes
    public int? SeasonNumber { get; set; } // For seasons/episodes
    public int? EpisodeNumber { get; set; } // For episodes
}

public class ItemSearchRequest
{
    public string Query { get; set; } = string.Empty;
    public string? LibraryId { get; set; }
    public string[]? ItemTypes { get; set; } // ["Movie", "Series", "Season"]
    public int PageSize { get; set; } = 20;
    public int Page { get; set; } = 1;
}

public class ItemSearchResponse
{
    public IEnumerable<MediaItemInfo> Items { get; set; } = Enumerable.Empty<MediaItemInfo>();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
}
