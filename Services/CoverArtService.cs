using CustomCoverArt.Common;
using CustomCoverArt.Models;
using MediaBrowser.Common.Configuration;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
// ImageSharp.Drawing also defines a `Path` type; this file only uses System.IO.Path.
using Path = System.IO.Path;

namespace CustomCoverArt.Services;

/// <summary>
/// Service for managing cover art operations
/// </summary>
public class CoverArtService : ICoverArtService
{
    private readonly IImageProcessingService _imageProcessingService;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILoggingService _loggingService;
    private readonly IMediaItemService _mediaItemService;
    private readonly string _outputDirectory;

    public CoverArtService(
        IImageProcessingService imageProcessingService,
        IApplicationPaths applicationPaths,
        ILoggingService loggingService,
        IMediaItemService mediaItemService)
    {
        _imageProcessingService = imageProcessingService;
        _applicationPaths = applicationPaths;
        _loggingService = loggingService;
        _mediaItemService = mediaItemService;

        // Data location comes from Jellyfin's application paths (via DI) — no
        // more guessing filesystem locations or falling back to temp.
        _outputDirectory = PluginPaths.Generated(applicationPaths);
        Directory.CreateDirectory(_outputDirectory);
    }

    public async Task<string> GenerateCoverArtAsync(CoverArtSettings settings)
    {
        try
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            if (settings.ExportWidth <= 0 || settings.ExportHeight <= 0)
                throw new ArgumentException("Invalid dimensions");

            // Housekeeping: drop stale preview/generated files.
            PruneOldGenerated();

            // Security: background image and font paths come from the client.
            // Only honour them if they resolve inside our own data directory,
            // otherwise ignore them (prevents reading arbitrary server files).
            if (!PluginPaths.IsInsideBase(_applicationPaths, settings.BackgroundImagePath))
            {
                if (!string.IsNullOrEmpty(settings.BackgroundImagePath))
                {
                    _loggingService.LogWarning("Background path rejected (outside plugin data dir): {Path}", settings.BackgroundImagePath);
                }
                settings.BackgroundImagePath = string.Empty;
            }

            if (!PluginPaths.IsInsideBase(_applicationPaths, settings.CustomFontPath))
            {
                settings.CustomFontPath = string.Empty;
            }

            // Clamp client-controlled effect sizes. The outline is drawn as an
            // O(n^2) grid of text passes, so an unbounded width could hang the
            // request; the others just guard against absurd input.
            settings.TextSize = Math.Clamp(settings.TextSize, 8, 1024);
            settings.TextOutlineWidth = Math.Clamp(settings.TextOutlineWidth, 0, 10);
            settings.BackgroundBlur = Math.Clamp(settings.BackgroundBlur, 0f, 100f);
            settings.BackgroundDim = Math.Clamp(settings.BackgroundDim, 0f, 1f);
            settings.TextShadowOffsetX = Math.Clamp(settings.TextShadowOffsetX, -50, 50);
            settings.TextShadowOffsetY = Math.Clamp(settings.TextShadowOffsetY, -50, 50);

            // Validate dimensions and estimated file size
            var dimensionValidation = ImageProcessingService.ValidateCoverArtDimensions(
                settings.ExportWidth, settings.ExportHeight, settings.OutputFormat);
            
            if (!dimensionValidation.IsValid)
            {
                throw new ArgumentException(dimensionValidation.ErrorMessage);
            }

            // A large-file-size estimate (dimensionValidation.WarningMessage) is
            // non-fatal and intentionally ignored here.

            // Auto-determine optimal format if not explicitly set
            if (string.IsNullOrEmpty(settings.OutputFormat) || settings.OutputFormat == "auto")
            {
                settings.OutputFormat = await _imageProcessingService.DetermineOptimalFormatAsync(settings);
            }

            // Whitelist the output format. The extension is NEVER derived from the
            // raw client string (which could contain path-traversal segments) —
            // anything that is not "gif" becomes "png".
            settings.OutputFormat = settings.OutputFormat?.ToLowerInvariant() == "gif" ? "gif" : "png";
            var extension = settings.OutputFormat;

            var fileName = $"cover_{Guid.NewGuid():N}.{extension}";
            var outputPath = Path.Combine(_outputDirectory, fileName);

            // Load background image if provided (path already sandboxed above).
            Image? backgroundImage = null;
            try
            {
                if (settings.BackgroundSource == BackgroundSources.Collage && settings.Collage is not null
                    && !string.IsNullOrEmpty(settings.Collage.SourceId))
                {
                    // Build a full-bleed grid mosaic from the target's child posters.
                    var posters = await _mediaItemService
                        .GetPosterPathsAsync(settings.Collage.SourceId, 60)
                        .ConfigureAwait(false);

                    backgroundImage = CollageComposer.BuildCollage(
                        posters, settings.ExportWidth, settings.ExportHeight,
                        settings.Collage.Density, settings.Collage.Seed);
                }
                else if (!string.IsNullOrEmpty(settings.BackgroundImagePath))
                {
                    if (!File.Exists(settings.BackgroundImagePath))
                    {
                        _loggingService.LogWarning("Background file not found: {Path}", settings.BackgroundImagePath);
                    }
                    else
                    {
                        try
                        {
                            // Reject oversize source images before a full decode
                            // (decompression-bomb guard).
                            var info = Image.Identify(settings.BackgroundImagePath);
                            const long maxSourcePixels = 8192L * 8192L;
                            if ((long)info.Width * info.Height <= maxSourcePixels)
                            {
                                backgroundImage = await Image.LoadAsync(settings.BackgroundImagePath);
                            }
                            else
                            {
                                _loggingService.LogWarning("Background image ignored, too large: {Width}x{Height}", info.Width, info.Height);
                            }
                        }
                        catch (Exception ex)
                        {
                            _loggingService.LogWarning("Failed to decode background {Path}: {Error}", settings.BackgroundImagePath, ex.Message);
                        }
                    }
                }

                // Animated GIF export: build multiple frames instead of one image.
                if (settings.Animation?.Enabled == true)
                {
                    return await GenerateAnimatedAsync(settings, backgroundImage).ConfigureAwait(false);
                }

                // Create new image with specified dimensions and composite one frame.
                using var image = new Image<Rgba32>(settings.ExportWidth, settings.ExportHeight);
                ComposeFrame(image, backgroundImage, settings);

                // Save image with retry mechanism
                await SaveImageWithRetryAsync(image, outputPath, settings);

                return outputPath;
            }
            finally
            {
                // Always release the decoded background image.
                backgroundImage?.Dispose();
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to generate cover art: {ex.Message}", ex);
        }
    }

    // Resolves the per-library output directory. Validating the id as a GUID
    // prevents a client-supplied libraryId from containing path-traversal
    // segments (e.g. "../../..") that would escape the output directory.
    private string LibraryDir(string libraryId)
    {
        if (!Guid.TryParse(libraryId, out var id))
        {
            throw new ArgumentException("Invalid library id", nameof(libraryId));
        }

        return Path.Combine(_outputDirectory, "Libraries", id.ToString("N"));
    }

    // Parses a #rrggbb colour, falling back to a default instead of throwing so
    // a malformed colour in the request body cannot abort the whole render.
    private static Color SafeColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        try
        {
            return Color.ParseHex(hex);
        }
        catch
        {
            return fallback;
        }
    }

    // Clones settings with text-related pixel sizes scaled to a resized canvas, so
    // a downscaled animated frame keeps the same visual proportions as the preview.
    private static CoverArtSettings ScaleTextForCanvas(CoverArtSettings s, float scale)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        var c = System.Text.Json.JsonSerializer.Deserialize<CoverArtSettings>(json) ?? s;
        c.TextSize = System.Math.Max(8, (int)System.Math.Round(s.TextSize * scale));
        c.TextShadowBlur = (int)System.Math.Round(s.TextShadowBlur * scale);
        c.TextShadowOffsetX = (int)System.Math.Round(s.TextShadowOffsetX * scale);
        c.TextShadowOffsetY = (int)System.Math.Round(s.TextShadowOffsetY * scale);
        c.TextOutlineWidth = (int)System.Math.Round(s.TextOutlineWidth * scale);
        return c;
    }

    /// <summary>Source crop rectangle for a Ken Burns frame at progress t (0..1).</summary>
    public static Rectangle KenBurnsCrop(int srcW, int srcH, float t, float zoomAmount, string direction)
    {
        var z = System.Math.Clamp(zoomAmount, 0f, 1f);
        // progress from wide (0) to tight (1)
        var p = (direction ?? "in").ToLowerInvariant() == "out" ? 1f - t : t;
        p = System.Math.Clamp(p, 0f, 1f);

        // scale goes 1.0 (full) → 1/(1+z) (tight)
        var scale = 1f - p * (1f - 1f / (1f + z));
        var w = (int)System.Math.Round(srcW * scale);
        var h = (int)System.Math.Round(srcH * scale);
        var x = (srcW - w) / 2;
        var y = (srcH - h) / 2;
        return new Rectangle(x, y, w, h);
    }

    // Best-effort cleanup of stale generated/preview files so the folder does
    // not grow without bound (preview fires on every UI tweak). Only prunes the
    // flat generated root; the per-library copies under Libraries/ are kept.
    private void PruneOldGenerated()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-6);
            foreach (var file in Directory.EnumerateFiles(_outputDirectory))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // Ignore individual files we cannot delete.
                }
            }
        }
        catch
        {
            // Directory missing or inaccessible — nothing to prune.
        }
    }

    public Task<string?> SaveCoverArtAsync(string libraryId, string coverArtPath)
    {
        try
        {
            if (!File.Exists(coverArtPath))
                return Task.FromResult<string?>(null);

            // Create library-specific directory (libraryId validated as a GUID).
            var libraryDirectory = LibraryDir(libraryId);
            Directory.CreateDirectory(libraryDirectory);

            // Copy the cover into the stable per-library location and return that
            // path — this is what gets applied to the library, NOT the transient
            // file in the generated root (which the cleanup may later prune).
            var destinationPath = Path.Combine(libraryDirectory, Path.GetFileName(coverArtPath));
            File.Copy(coverArtPath, destinationPath, overwrite: true);

            return Task.FromResult<string?>(destinationPath);
        }
        catch
        {
            return Task.FromResult<string?>(null);
        }
    }

    /// <summary>
    /// Composites one frame (background + text) onto the supplied canvas. Shared
    /// by the single-image path and the animated-GIF path (which calls it per frame).
    /// </summary>
    private static void ComposeFrame(Image<Rgba32> canvas, Image? background, CoverArtSettings settings)
    {
        if (background is not null)
        {
            ApplyBackground(canvas, background, settings);
        }
        else
        {
            CreateGradientBackground(canvas, settings);
        }

        ApplyTextOverlayWithFallback(canvas, settings);
    }

    /// <summary>
    /// Builds an animated GIF: either passing through an animated-source background
    /// frame-by-frame, or applying a Ken Burns pan/zoom to a static background.
    /// Frame count is clamped to [2, 30]; loop uses the GIF repeat count.
    /// </summary>
    private async Task<string> GenerateAnimatedAsync(CoverArtSettings settings, Image? background)
    {
        var w = settings.ExportWidth;
        var h = settings.ExportHeight;

        // An animated GIF costs N× a static render and Jellyfin re-processes very
        // large library images poorly (a full-size multi-frame GIF often fails to
        // appear at all). Cap the working size so the render stays fast and the
        // file stays applyable; text scales with the canvas so it looks the same.
        const int maxGifSide = 1280;
        var composeSettings = settings;
        var longestSide = System.Math.Max(w, h);
        if (longestSide > maxGifSide)
        {
            var canvasScale = maxGifSide / (float)longestSide;
            w = System.Math.Max(1, (int)System.Math.Round(w * canvasScale));
            h = System.Math.Max(1, (int)System.Math.Round(h * canvasScale));
            composeSettings = ScaleTextForCanvas(settings, canvasScale);
        }

        // Passthrough when the source background is itself animated.
        var animatedSource = background is not null && background.Frames.Count > 1;

        // An animated source drives its own frame count (so its motion isn't
        // truncated or padded); Ken Burns / static sources use the requested count.
        var frameCount = animatedSource
            ? System.Math.Clamp(background!.Frames.Count, 2, 60)
            : System.Math.Clamp(settings.Animation!.FrameCount, 2, 30);
        var delayCentis = System.Math.Max(2, settings.Animation!.DelayMs / 10); // GIF delay is 1/100s

        Image<Rgba32>? output = null;
        try
        {
            for (int i = 0; i < frameCount; i++)
            {
                var t = frameCount == 1 ? 0f : i / (float)(frameCount - 1);
                var frameDelay = delayCentis;

                using var frameCanvas = new Image<Rgba32>(w, h);
                Image? frameBg = null;
                Image<Rgba32>? tempBg = null;
                try
                {
                    if (animatedSource)
                    {
                        var srcIndex = i % background!.Frames.Count;
                        tempBg = background.Frames.CloneFrame(srcIndex).CloneAs<Rgba32>();
                        frameBg = tempBg;
                        // Preserve the source GIF's own timing so playback speed matches.
                        var srcDelay = background.Frames[srcIndex].Metadata.GetGifMetadata().FrameDelay;
                        if (srcDelay > 0) { frameDelay = srcDelay; }
                    }
                    else if (settings.Animation.KenBurns && background is not null)
                    {
                        var crop = KenBurnsCrop(background.Width, background.Height, t,
                            settings.Animation.ZoomAmount, settings.Animation.Direction);
                        tempBg = background.CloneAs<Rgba32>();
                        tempBg.Mutate(x => x.Crop(crop).Resize(w, h));
                        frameBg = tempBg;
                    }
                    else if (background is not null)
                    {
                        tempBg = background.CloneAs<Rgba32>();
                        frameBg = tempBg;
                    }

                    ComposeFrame(frameCanvas, frameBg, composeSettings);

                    if (output is null)
                    {
                        output = frameCanvas.Clone();
                        var gm = output.Metadata.GetGifMetadata();
                        gm.RepeatCount = (ushort)(settings.Animation.Loop ? 0 : 1);
                        output.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = frameDelay;
                    }
                    else
                    {
                        var added = frameCanvas.Frames.RootFrame;
                        added.Metadata.GetGifMetadata().FrameDelay = frameDelay;
                        output.Frames.AddFrame(added);
                    }
                }
                finally
                {
                    tempBg?.Dispose();
                }
            }

            var outputPath = Path.Combine(_outputDirectory, $"cover_{Guid.NewGuid():N}.gif");
            await output!.SaveAsync(outputPath, new GifEncoder()).ConfigureAwait(false);
            return outputPath;
        }
        finally
        {
            output?.Dispose();
        }
    }

    private static void ApplyBackground(Image<Rgba32> image, Image backgroundImage, CoverArtSettings settings)
    {
        var fit = (settings.BackgroundFit ?? "cover").Trim().ToLowerInvariant();
        var baseColor = SafeColor(settings.DimColor, Color.Black);

        if (fit == "stretch")
        {
            // Distort to fill the whole canvas exactly.
            backgroundImage.Mutate(x => x.Resize(image.Width, image.Height));
            if (settings.BackgroundBlur > 0)
            {
                backgroundImage.Mutate(x => x.GaussianBlur(settings.BackgroundBlur));
            }
            image.Mutate(x => x.DrawImage(backgroundImage, Point.Empty, 1f));
        }
        else if (fit == "contain")
        {
            // Fit the whole image inside the canvas (like background-size: contain),
            // letterboxing the remainder with the base colour.
            image.Mutate(x => x.Fill(baseColor));

            var scale = Math.Min((float)image.Width / backgroundImage.Width,
                                 (float)image.Height / backgroundImage.Height);
            var w = Math.Max(1, (int)Math.Round(backgroundImage.Width * scale));
            var h = Math.Max(1, (int)Math.Round(backgroundImage.Height * scale));
            backgroundImage.Mutate(x => x.Resize(w, h));
            if (settings.BackgroundBlur > 0)
            {
                backgroundImage.Mutate(x => x.GaussianBlur(settings.BackgroundBlur));
            }

            var px = (image.Width - w) / 2;
            var py = (image.Height - h) / 2;
            image.Mutate(x => x.DrawImage(backgroundImage, new Point(px, py), 1f));
        }
        else
        {
            // "cover" (default): scale to fill the canvas keeping the source aspect
            // ratio, then centre-crop (like CSS background-size: cover).
            var scale = Math.Max((float)image.Width / backgroundImage.Width,
                                 (float)image.Height / backgroundImage.Height);
            var newWidth = Math.Max(image.Width, (int)Math.Ceiling(backgroundImage.Width * scale));
            var newHeight = Math.Max(image.Height, (int)Math.Ceiling(backgroundImage.Height * scale));

            backgroundImage.Mutate(x => x.Resize(newWidth, newHeight));

            var offsetX = (newWidth - image.Width) / 2;
            var offsetY = (newHeight - image.Height) / 2;
            backgroundImage.Mutate(x => x.Crop(new Rectangle(offsetX, offsetY, image.Width, image.Height)));

            if (settings.BackgroundBlur > 0)
            {
                backgroundImage.Mutate(x => x.GaussianBlur(settings.BackgroundBlur));
            }
            image.Mutate(x => x.DrawImage(backgroundImage, Point.Empty, 1f));
        }

        // Apply dimming by compositing a solid overlay at fractional opacity.
        // IMPORTANT: do NOT use Fill() with a semi-transparent SolidBrush here —
        // on backgrounds decoded without an alpha channel (JPEG posters load as
        // Rgb24) Fill ignores the brush alpha and paints fully opaque, which
        // blacked out the entire background for any non-zero dim. DrawImage with
        // an opacity blends correctly on every pixel format. Text is drawn later,
        // so only the background is dimmed.
        if (settings.BackgroundDim > 0)
        {
            using var dimOverlay = new Image<Rgba32>(image.Width, image.Height, baseColor);
            image.Mutate(x => x.DrawImage(dimOverlay, Point.Empty, settings.BackgroundDim));
        }
    }

    private static void CreateGradientBackground(Image<Rgba32> image, CoverArtSettings settings)
    {
        if (settings.BackgroundGradient?.IsEnabled == true)
        {
            ApplyGradientBackground(image, settings.BackgroundGradient);
        }
        else
        {
            var backgroundColor = SafeColor(settings.DimColor, Color.Black);
            image.Mutate(x => x.Fill(backgroundColor));
        }
    }

    private static void ApplyTextOverlay(Image<Rgba32> image, CoverArtSettings settings)
    {
        // Use custom font if provided, otherwise system fonts with fallback options
        var font = CreateFont(settings);

        // Parse text color
        var textColor = SafeColor(settings.TextColor, Color.White);

        // Calculate text position
        var textPosition = CalculateTextPosition(image, settings);

        // Create text options
        var textOptions = new RichTextOptions(font)
        {
            Origin = textPosition,
            HorizontalAlignment = GetHorizontalAlignment(settings.TextAlign),
            VerticalAlignment = GetVerticalAlignment(settings.TextBaseline)
        };

        // Apply text effects
        if (settings.TextShadow)
        {
            var shadowColor = SafeColor(settings.TextShadowColor, Color.Black);
            var shadowPosition = new PointF(
                textPosition.X + settings.TextShadowOffsetX,
                textPosition.Y + settings.TextShadowOffsetY
            );

            var shadowOptions = new RichTextOptions(font)
            {
                Origin = shadowPosition,
                HorizontalAlignment = textOptions.HorizontalAlignment,
                VerticalAlignment = textOptions.VerticalAlignment
            };

            image.Mutate(x => x.DrawText(shadowOptions, settings.Title, shadowColor));
        }

        if (settings.TextOutline)
        {
            var outlineColor = SafeColor(settings.TextOutlineColor, Color.Black);
            // Draw outline by drawing text multiple times with slight offsets
            for (int x = -settings.TextOutlineWidth; x <= settings.TextOutlineWidth; x++)
            {
                for (int y = -settings.TextOutlineWidth; y <= settings.TextOutlineWidth; y++)
                {
                    if (x == 0 && y == 0) continue;

                    var outlinePosition = new PointF(textPosition.X + x, textPosition.Y + y);
                    var outlineOptions = new RichTextOptions(font)
                    {
                        Origin = outlinePosition,
                        HorizontalAlignment = textOptions.HorizontalAlignment,
                        VerticalAlignment = textOptions.VerticalAlignment
                    };

                    image.Mutate(ctx => ctx.DrawText(outlineOptions, settings.Title, outlineColor));
                }
            }
        }

        // Draw main text
        image.Mutate(x => x.DrawText(textOptions, settings.Title, textColor));
    }

    private static PointF CalculateTextPosition(Image<Rgba32> image, CoverArtSettings settings)
    {
        var paddingX = (int)(image.Width * settings.TextPadding);
        var paddingY = (int)(image.Height * settings.TextPadding);

        return settings.TextAlign switch
        {
            TextAlign.Left => new PointF(paddingX, image.Height / 2f),
            TextAlign.Right => new PointF(image.Width - paddingX, image.Height / 2f),
            TextAlign.Center => new PointF(image.Width / 2f, image.Height / 2f),
            _ => new PointF(image.Width / 2f, image.Height / 2f)
        };
    }

    private static HorizontalAlignment GetHorizontalAlignment(TextAlign textAlign)
    {
        return textAlign switch
        {
            TextAlign.Left => HorizontalAlignment.Left,
            TextAlign.Right => HorizontalAlignment.Right,
            TextAlign.Center => HorizontalAlignment.Center,
            _ => HorizontalAlignment.Center
        };
    }

    private static VerticalAlignment GetVerticalAlignment(TextBaseline textBaseline)
    {
        return textBaseline switch
        {
            TextBaseline.Top => VerticalAlignment.Top,
            TextBaseline.Bottom => VerticalAlignment.Bottom,
            TextBaseline.Middle => VerticalAlignment.Center,
            _ => VerticalAlignment.Center
        };
    }

    // Bundled fonts (Noto Sans, SIL OFL-1.1 — the same family Jellyfin's web UI
    // uses), embedded so text ALWAYS renders, even on Jellyfin Docker images
    // that ship no system fonts. Every UI weight has its own face so the font
    // weight control actually changes the rendered thickness.
    private static readonly FontCollection BundledFonts = new();
    private static readonly object FontLock = new();
    private static readonly Dictionary<FontWeight, FontFamily> BundledFamilies = new();

    private static FontFamily BundledFamily(FontWeight weight)
    {
        lock (FontLock)
        {
            if (BundledFamilies.TryGetValue(weight, out var existing))
            {
                return existing;
            }

            var faceName = weight switch
            {
                FontWeight.Light => "NotoSans-Light",
                FontWeight.Medium => "NotoSans-Medium",
                FontWeight.SemiBold => "NotoSans-SemiBold",
                FontWeight.Bold => "NotoSans-Bold",
                FontWeight.ExtraBold => "NotoSans-ExtraBold",
                _ => "NotoSans-Regular" // Normal and any unmapped value
            };

            var resource = $"CustomCoverArt.Resources.fonts.{faceName}.ttf";
            using var stream = typeof(CoverArtService).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"Bundled font not found: {resource}");

            var family = BundledFonts.Add(stream);
            BundledFamilies[weight] = family;
            return family;
        }
    }

    /// <summary>
    /// Creates a font: a client-uploaded custom font if one was provided,
    /// otherwise the bundled Noto Sans face for the requested weight.
    /// </summary>
    private static Font CreateFont(CoverArtSettings settings)
    {
        // Custom uploaded font takes priority (its path is already sandboxed).
        if (!string.IsNullOrEmpty(settings.CustomFontPath) && File.Exists(settings.CustomFontPath))
        {
            try
            {
                var fontCollection = new FontCollection();
                var fontFamily = fontCollection.Add(settings.CustomFontPath);
                return fontFamily.CreateFont(settings.TextSize);
            }
            catch
            {
                // Fall back to the bundled font if the custom one fails to load.
            }
        }

        return BundledFamily(settings.TextWeight).CreateFont(settings.TextSize);
    }

    /// <summary>
    /// Applies a multi-stop gradient background (linear at a given angle, or radial).
    /// </summary>
    private static void ApplyGradientBackground(Image<Rgba32> image, GradientSettings gradient)
    {
        var stops = BuildColorStops(gradient);

        if (gradient.Type == GradientType.Radial)
        {
            var centerX = gradient.CenterX * image.Width;
            var centerY = gradient.CenterY * image.Height;
            var radius = Math.Max(1f, gradient.Radius * Math.Min(image.Width, image.Height));

            var brush = new RadialGradientBrush(new PointF(centerX, centerY), radius, GradientRepetitionMode.None, stops);
            image.Mutate(x => x.Fill(brush));
        }
        else
        {
            // Linear: a line through the centre at `Angle` degrees, long enough to
            // span the whole canvas so the gradient covers it corner to corner.
            var rad = gradient.Angle * Math.PI / 180.0;
            var dx = (float)Math.Cos(rad);
            var dy = (float)Math.Sin(rad);
            var cx = image.Width / 2f;
            var cy = image.Height / 2f;
            var half = (Math.Abs(dx) * image.Width + Math.Abs(dy) * image.Height) / 2f;

            var p0 = new PointF(cx - dx * half, cy - dy * half);
            var p1 = new PointF(cx + dx * half, cy + dy * half);

            var brush = new LinearGradientBrush(p0, p1, GradientRepetitionMode.None, stops);
            image.Mutate(x => x.Fill(brush));
        }
    }

    /// <summary>
    /// Builds the colour stops from the settings — the explicit Stops list if it
    /// has 2+ entries, otherwise the Start/End colours as a fallback.
    /// </summary>
    private static ColorStop[] BuildColorStops(GradientSettings gradient)
    {
        if (gradient.Stops is { Count: >= 2 })
        {
            return gradient.Stops
                .OrderBy(s => s.Position)
                .Select(s => new ColorStop(Math.Clamp(s.Position, 0f, 1f), SafeColor(s.Color, Color.Gray)))
                .ToArray();
        }

        return new[]
        {
            new ColorStop(0f, SafeColor(gradient.StartColor, Color.Black)),
            new ColorStop(1f, SafeColor(gradient.EndColor, Color.White))
        };
    }

    /// <summary>
    /// Applies text overlay with fallback mechanisms
    /// </summary>
    private static void ApplyTextOverlayWithFallback(Image<Rgba32> image, CoverArtSettings settings)
    {
        try
        {
            ApplyTextOverlay(image, settings);
        }
        catch
        {
            // Fallback: simple text overlay without advanced effects.
            try
            {
                var font = BundledFamily(FontWeight.Normal).CreateFont(Math.Max(12, settings.TextSize * 0.5f));
                var textColor = SafeColor(settings.TextColor, Color.White);
                var position = new PointF(image.Width / 2f, image.Height / 2f);

                image.Mutate(x => x.DrawText(settings.Title, font, textColor, position));
            }
            catch
            {
                // Ultimate fallback: Just fill with the dim/background color
                var backgroundColor = SafeColor(settings.DimColor, Color.Black);
                image.Mutate(x => x.Fill(backgroundColor));
            }
        }
    }

    /// <summary>
    /// Saves image with retry mechanism for transient failures
    /// </summary>
    private static async Task SaveImageWithRetryAsync(Image<Rgba32> image, string outputPath, CoverArtSettings settings)
    {
        const int maxRetries = 3;
        const int delayMs = 100;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                if (settings.OutputFormat?.ToLowerInvariant() == "gif")
                {
                    await image.SaveAsync(outputPath, new SixLabors.ImageSharp.Formats.Gif.GifEncoder());
                }
                else
                {
                    await image.SaveAsync(outputPath, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                }
                return;
            }
            catch (IOException) when (attempt < maxRetries - 1)
            {
                // Retry on IO exceptions (file locked, etc.)
                await Task.Delay(delayMs * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < maxRetries - 1)
            {
                // Retry on permission issues
                await Task.Delay(delayMs * (attempt + 1));
            }
        }

        // If all retries failed, throw the last exception
        throw new InvalidOperationException($"Failed to save image after {maxRetries} attempts");
    }
}
