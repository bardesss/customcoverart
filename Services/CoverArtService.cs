using CustomCoverArt.Common;
using CustomCoverArt.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
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
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<CoverArtService> _logger;
    private readonly IMediaItemService _mediaItemService;
    private readonly string _outputDirectory;

    public CoverArtService(
        IApplicationPaths applicationPaths,
        ILogger<CoverArtService> logger,
        IMediaItemService mediaItemService)
    {
        _applicationPaths = applicationPaths;
        _logger = logger;
        _mediaItemService = mediaItemService;

        // Data location comes from Jellyfin's application paths (via DI) — no
        // more guessing filesystem locations or falling back to temp.
        _outputDirectory = PluginPaths.Generated(applicationPaths);
        Directory.CreateDirectory(_outputDirectory);
    }

    public Task<string> GenerateCoverArtAsync(CoverArtSettings settings)
        => GenerateFromDocumentAsync(DocumentMigration.FromSettings(settings));

    public async Task<string> GenerateFromDocumentAsync(CoverDocument document)
    {
        try
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));

            // Defensive input hygiene: a client can POST a partial/malformed document
            // (e.g. "transform": null or "layers": [null, ...]). Normalize the nested
            // objects this renderer dereferences unconditionally so a bad body degrades
            // gracefully instead of a 500. Shared with NormalizeTemplate's Document
            // handling so every client-facing CoverDocument entry point is protected.
            DocumentMigration.Normalize(document);

            if (document.Canvas.Width <= 0 || document.Canvas.Height <= 0)
                throw new ArgumentException("Invalid dimensions");

            // Housekeeping: drop stale preview/generated files.
            PruneOldGenerated();

            // Security: background image and font paths come from the client.
            // Only honour them if they resolve inside our own data directory,
            // otherwise ignore them (prevents reading arbitrary server files).
            if (!PluginPaths.IsInsideBase(_applicationPaths, document.Background.ImagePath))
            {
                if (!string.IsNullOrEmpty(document.Background.ImagePath))
                {
                    _logger.LogWarning("Background path rejected (outside plugin data dir): {Path}", document.Background.ImagePath);
                }
                document.Background.ImagePath = string.Empty;
            }

            // Bound the layer list before compositing: each layer is a full draw pass
            // (an outline is itself an O(n^2) grid of text passes), so an unbounded
            // Layers array is a cheap way to burn render threads.
            const int maxLayers = 40;
            if (document.Layers.Count > maxLayers)
            {
                _logger.LogWarning("Dropping {Count} excess layers (max {Max})", document.Layers.Count - maxLayers, maxLayers);
                document.Layers = document.Layers.Take(maxLayers).ToList();
            }

            foreach (var layer in document.Layers)
            {
                if (!PluginPaths.IsInsideBase(_applicationPaths, layer.FontPath))
                {
                    layer.FontPath = string.Empty;
                }

                // Same rule as the background: an image layer path comes from the client,
                // so honour it only inside our own data dir. Otherwise any readable file
                // on the server could be composited into a cover and downloaded.
                if (layer.Type == "image" && !PluginPaths.IsInsideBase(_applicationPaths, layer.ImagePath))
                {
                    if (!string.IsNullOrEmpty(layer.ImagePath))
                    {
                        _logger.LogWarning("Layer image rejected (outside plugin data dir): {Path}", layer.ImagePath);
                    }
                    layer.ImagePath = string.Empty;
                }
            }

            // Clamp client-controlled background effect sizes (guards against
            // absurd input). Per-layer text/outline clamps (the outline is drawn
            // as an O(n^2) grid of text passes, so an unbounded width could hang
            // the request) already happen inside DocumentRenderer — do not
            // double-clamp them here.
            document.Background.Blur = Math.Clamp(document.Background.Blur, 0f, 100f);
            document.Background.Dim = Math.Clamp(document.Background.Dim, 0f, 1f);

            // Validate dimensions and estimated file size
            var dimensionValidation = ImageProcessingService.ValidateCoverArtDimensions(
                document.Canvas.Width, document.Canvas.Height, document.Canvas.Format);

            if (!dimensionValidation.IsValid)
            {
                throw new ArgumentException(dimensionValidation.ErrorMessage);
            }

            // A large-file-size estimate (dimensionValidation.WarningMessage) is
            // non-fatal and intentionally ignored here.

            // Whitelist the output format. The extension is NEVER derived from the
            // raw client string (which could contain path-traversal segments) —
            // anything that is not "gif" becomes "png". (This subsumes the old
            // "auto"-format optimizer: DetermineOptimalFormatAsync always resolved
            // to "png" unconditionally, so this mapping is exactly equivalent.)
            var outputFormat = document.Canvas.Format?.ToLowerInvariant() == "gif" ? "gif" : "png";
            document.Canvas.Format = outputFormat;

            var fileName = $"cover_{Guid.NewGuid():N}.{outputFormat}";
            var outputPath = Path.Combine(_outputDirectory, fileName);

            // Load background image if provided (path already sandboxed above).
            Image? backgroundImage = null;
            try
            {
                if (document.Background.Source == BackgroundSources.Collage && document.Background.Collage is not null
                    && !string.IsNullOrEmpty(document.Background.Collage.SourceId))
                {
                    // Build a full-bleed grid mosaic from the target's child posters.
                    var posters = await _mediaItemService
                        .GetPosterPathsAsync(document.Background.Collage.SourceId, 60)
                        .ConfigureAwait(false);

                    backgroundImage = CollageComposer.BuildCollage(
                        posters, document.Canvas.Width, document.Canvas.Height,
                        document.Background.Collage.Density, document.Background.Collage.Seed);
                }
                else if (!string.IsNullOrEmpty(document.Background.ImagePath))
                {
                    // Identify-then-decode through the shared guard: rejects decompression
                    // bombs (frames × width × height over budget) and decodes only the frames
                    // this render uses. A still cover takes ONE frame of an animated source,
                    // so the resize/blur in ApplyBackgroundLayer no longer runs on frames that
                    // are never drawn.
                    backgroundImage = SafeImageLoader.TryLoad(document.Background.ImagePath, BackgroundMaxFrames(document));
                    if (backgroundImage is null)
                    {
                        _logger.LogWarning("Background image ignored (missing, undecodable or too large): {Path}", document.Background.ImagePath);
                    }
                }

                // Animated GIF export: build multiple frames instead of one image.
                if (document.Background.Animation?.Enabled == true)
                {
                    return await GenerateAnimatedAsync(document, backgroundImage).ConfigureAwait(false);
                }

                // Create new image with specified dimensions and composite one frame.
                using var image = new Image<Rgba32>(document.Canvas.Width, document.Canvas.Height);
                ComposeFrame(image, backgroundImage, document);

                // Save image with retry mechanism
                await SaveImageWithRetryAsync(image, outputPath, outputFormat);

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

    /// <summary>
    /// Frames to decode from the background: all of them (up to the passthrough cap) only
    /// when exporting an animated GIF, otherwise just the first.
    /// </summary>
    internal static int BackgroundMaxFrames(CoverDocument document)
        => document.Background.Animation?.Enabled == true ? SafeImageLoader.MaxAnimatedFrames : 1;

    // Clones a document for a downscaled animated working canvas. Layer.Size is
    // already a fraction of Canvas.Height, so pointing Canvas.Width/Height at the
    // actual (capped) working dimensions keeps text proportional automatically —
    // no separate text-size scaling needed (unlike the legacy flat TextSize,
    // which was an absolute pixel value tied to the ORIGINAL ExportHeight and
    // required pre-shrinking to compensate). Shadow/outline offsets, background blur and
    // the border are absolute pixel values, so those are scaled explicitly to
    // preserve their visual proportions on the smaller canvas.
    internal static CoverDocument ScaleDocumentForCanvas(CoverDocument doc, int width, int height, float scale)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(doc);
        var c = System.Text.Json.JsonSerializer.Deserialize<CoverDocument>(json) ?? doc;
        c.Canvas.Width = width;
        c.Canvas.Height = height;
        foreach (var layer in c.Layers)
        {
            layer.Shadow.Blur = (int)System.Math.Round(layer.Shadow.Blur * scale);
            layer.Shadow.OffsetX = (int)System.Math.Round(layer.Shadow.OffsetX * scale);
            layer.Shadow.OffsetY = (int)System.Math.Round(layer.Shadow.OffsetY * scale);
            layer.Outline.Width = (int)System.Math.Round(layer.Outline.Width * scale);
        }
        // Background blur and the border are absolute pixels too.
        c.Background.Blur *= scale;
        var border = c.Effects.Border;
        border.Thickness = ScaleNonZero(border.Thickness, scale);
        border.Radius = (int)System.Math.Round(border.Radius * scale);
        border.Gap = ScaleNonZero(border.Gap, scale);
        return c;
    }

    // A border the user switched on stays at least 1px instead of rounding away.
    private static int ScaleNonZero(int value, float scale)
        => value <= 0 ? value : System.Math.Max(1, (int)System.Math.Round(value * scale));

    /// <summary>Source crop rectangle for a Ken Burns frame at progress t (0..1), at the source's own aspect.</summary>
    public static Rectangle KenBurnsCrop(int srcW, int srcH, float t, float zoomAmount, string direction)
        => KenBurnsCrop(srcW, srcH, srcW, srcH, t, zoomAmount, direction);

    /// <summary>
    /// Source crop rectangle for a Ken Burns frame at progress t (0..1). The window has the
    /// CANVAS aspect (<paramref name="dstW"/>:<paramref name="dstH"/>): the largest such window
    /// that fits inside the source, shrunk by the zoom and kept centred. It is then resized to
    /// the canvas without distortion. Cropping at the source aspect and resizing to the canvas
    /// stretched any background whose shape differed from the cover.
    /// </summary>
    public static Rectangle KenBurnsCrop(int srcW, int srcH, int dstW, int dstH, float t, float zoomAmount, string direction)
    {
        var z = System.Math.Clamp(zoomAmount, 0f, 1f);
        // progress from wide (0) to tight (1)
        var p = (direction ?? "in").ToLowerInvariant() == "out" ? 1f - t : t;
        p = System.Math.Clamp(p, 0f, 1f);

        // scale goes 1.0 (full) → 1/(1+z) (tight)
        var scale = 1f - p * (1f - 1f / (1f + z));

        // Largest canvas-aspect window inside the source.
        int baseW, baseH;
        if (dstW <= 0 || dstH <= 0)
        {
            baseW = srcW;
            baseH = srcH;
        }
        else if ((long)srcW * dstH > (long)srcH * dstW)
        {
            // Source is wider than the canvas: full height, trimmed width.
            baseH = srcH;
            baseW = (int)System.Math.Round(srcH * (double)dstW / dstH);
        }
        else
        {
            // Source is taller (or equal): full width, trimmed height.
            baseW = srcW;
            baseH = (int)System.Math.Round(srcW * (double)dstH / dstW);
        }

        var w = System.Math.Clamp((int)System.Math.Round(baseW * scale), 1, System.Math.Max(1, srcW));
        var h = System.Math.Clamp((int)System.Math.Round(baseH * scale), 1, System.Math.Max(1, srcH));
        var x = (srcW - w) / 2;
        var y = (srcH - h) / 2;
        return new Rectangle(x, y, w, h);
    }

    // Best-effort cleanup of stale generated/preview files so the folder does
    // not grow without bound (preview fires on every UI tweak). Only prunes the
    // flat generated root; the per-library folders under Libraries/ are pruned by PruneLibraryOutputs after each successful apply.
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

    public void PruneLibraryOutputs(string libraryId, string keepPath)
    {
        string libraryDirectory;
        try
        {
            libraryDirectory = LibraryDir(libraryId);
        }
        catch (ArgumentException)
        {
            return; // not a GUID: nothing of ours to prune
        }

        if (!Directory.Exists(libraryDirectory))
        {
            return;
        }

        var keep = Path.GetFullPath(keepPath);
        foreach (var file in Directory.EnumerateFiles(libraryDirectory))
        {
            if (string.Equals(Path.GetFullPath(file), keep, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // In use: the next successful apply retries it.
            }
            catch (UnauthorizedAccessException)
            {
                // Not ours to delete; leave it.
            }
        }
    }

    /// <summary>
    /// Composites one frame (background + layers) onto the supplied canvas. Shared
    /// by the single-image path and the animated-GIF path (which calls it per frame).
    /// Delegates directly to the shared <see cref="DocumentRenderer"/> compositor.
    /// </summary>
    private static void ComposeFrame(Image<Rgba32> canvas, Image? background, CoverDocument document)
    {
        DocumentRenderer.ComposeDocumentFrame(canvas, background, document);
    }

    /// <summary>
    /// Builds an animated GIF: either passing through an animated-source background
    /// frame-by-frame, or applying a Ken Burns pan/zoom to a static background.
    /// Frame count is clamped to [2, 30]; loop uses the GIF repeat count.
    /// </summary>
    private async Task<string> GenerateAnimatedAsync(CoverDocument document, Image? background)
    {
        var w = document.Canvas.Width;
        var h = document.Canvas.Height;

        // An animated GIF costs N× a static render and Jellyfin re-processes very
        // large library images poorly (a full-size multi-frame GIF often fails to
        // appear at all). Cap the working size so the render stays fast and the
        // file stays applyable; text scales with the canvas so it looks the same.
        const int maxGifSide = 1280;
        var composeDocument = document;
        var longestSide = System.Math.Max(w, h);
        if (longestSide > maxGifSide)
        {
            var canvasScale = maxGifSide / (float)longestSide;
            w = System.Math.Max(1, (int)System.Math.Round(w * canvasScale));
            h = System.Math.Max(1, (int)System.Math.Round(h * canvasScale));
            composeDocument = ScaleDocumentForCanvas(document, w, h, canvasScale);
        }

        // Passthrough when the source background is itself animated.
        var animatedSource = background is not null && background.Frames.Count > 1;
        var kenBurns = !animatedSource && background is not null && document.Background.Animation!.KenBurns;

        // An animated source drives its own frame count (so its motion isn't
        // truncated or padded); Ken Burns / static sources use the requested count.
        var frameCount = animatedSource
            ? System.Math.Clamp(background!.Frames.Count, 2, 60)
            : System.Math.Clamp(document.Background.Animation!.FrameCount, 2, 30);
        var delayCentis = System.Math.Max(2, document.Background.Animation!.DelayMs / 10); // GIF delay is 1/100s

        // A still background without Ken Burns gives the same picture on every frame:
        // compose it once and repeat it, instead of cloning the full-size source and
        // re-running the whole compositor for each frame. ApplyBackgroundLayer mutates the
        // background it is given, which is fine here because it is used exactly once.
        Image<Rgba32>? staticFrame = null;
        Image<Rgba32>? output = null;
        try
        {
            if (!animatedSource && !kenBurns)
            {
                staticFrame = new Image<Rgba32>(w, h);
                ComposeFrame(staticFrame, background, composeDocument);
            }

            for (int i = 0; i < frameCount; i++)
            {
                var t = frameCount == 1 ? 0f : i / (float)(frameCount - 1);
                var frameDelay = delayCentis;

                using var frameCanvas = staticFrame is not null ? staticFrame.Clone() : new Image<Rgba32>(w, h);
                if (staticFrame is null)
                {
                    Image? frameBg = null;
                    try
                    {
                        if (animatedSource)
                        {
                            var srcIndex = i % background!.Frames.Count;
                            // One clone of one frame, in the source's own pixel format.
                            frameBg = background.Frames.CloneFrame(srcIndex);
                            // Preserve the source GIF's own timing so playback speed matches.
                            var srcDelay = background.Frames[srcIndex].Metadata.GetGifMetadata().FrameDelay;
                            if (srcDelay > 0) { frameDelay = srcDelay; }
                        }
                        else
                        {
                            var crop = KenBurnsCrop(background!.Width, background.Height, w, h, t,
                                document.Background.Animation.ZoomAmount, document.Background.Animation.Direction);
                            // Crop first, then resize: the clone is the small window, never the full source.
                            frameBg = background.Clone(x => x.Crop(crop).Resize(w, h));
                        }

                        ComposeFrame(frameCanvas, frameBg, composeDocument);
                    }
                    finally
                    {
                        frameBg?.Dispose();
                    }
                }

                if (output is null)
                {
                    output = frameCanvas.Clone();
                    var gm = output.Metadata.GetGifMetadata();
                    gm.RepeatCount = (ushort)(document.Background.Animation!.Loop ? 0 : 1);
                    output.Frames.RootFrame.Metadata.GetGifMetadata().FrameDelay = frameDelay;
                }
                else
                {
                    var added = frameCanvas.Frames.RootFrame;
                    added.Metadata.GetGifMetadata().FrameDelay = frameDelay;
                    output.Frames.AddFrame(added);
                }
            }

            var outputPath = Path.Combine(_outputDirectory, $"cover_{Guid.NewGuid():N}.gif");
            await output!.SaveAsync(outputPath, new GifEncoder()).ConfigureAwait(false);
            return outputPath;
        }
        finally
        {
            staticFrame?.Dispose();
            output?.Dispose();
        }
    }

    /// <summary>
    /// Saves image with retry mechanism for transient failures
    /// </summary>
    private static async Task SaveImageWithRetryAsync(Image<Rgba32> image, string outputPath, string outputFormat)
    {
        const int maxRetries = 3;
        const int delayMs = 100;

        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                if (outputFormat?.ToLowerInvariant() == "gif")
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
