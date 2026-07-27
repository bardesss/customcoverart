using CustomCoverArt.Common;
using CustomCoverArt.Models;
using MediaBrowser.Common.Configuration;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
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
    private readonly string _outputDirectory;

    public CoverArtService(IImageProcessingService imageProcessingService, IApplicationPaths applicationPaths)
    {
        _imageProcessingService = imageProcessingService;
        _applicationPaths = applicationPaths;

        // Data location comes from Jellyfin's application paths (via DI) — no
        // more guessing filesystem locations or falling back to temp.
        _outputDirectory = PluginPaths.Generated(applicationPaths);
        Directory.CreateDirectory(_outputDirectory);
    }

    public async Task<string> GenerateCoverArtAsync(CoverArtSettings settings)
    {
        try
        {
            // Validate settings
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));

            if (settings.ExportWidth <= 0 || settings.ExportHeight <= 0)
                throw new ArgumentException("Invalid dimensions");

            // Security: background image and font paths come from the client.
            // Only honour them if they resolve inside our own data directory,
            // otherwise ignore them (prevents reading arbitrary server files).
            if (!PluginPaths.IsInsideBase(_applicationPaths, settings.BackgroundImagePath))
            {
                settings.BackgroundImagePath = string.Empty;
            }

            if (!PluginPaths.IsInsideBase(_applicationPaths, settings.CustomFontPath))
            {
                settings.CustomFontPath = string.Empty;
            }

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

            // Create output filename with correct extension
            var extension = settings.OutputFormat?.ToLowerInvariant() ?? "png";
            var fileName = $"cover_{Guid.NewGuid():N}.{extension}";
            var outputPath = Path.Combine(_outputDirectory, fileName);

            // Load background image if provided with null safety
            Image? backgroundImage = null;
            if (!string.IsNullOrEmpty(settings.BackgroundImagePath) && File.Exists(settings.BackgroundImagePath))
            {
                try
                {
                    backgroundImage = await Image.LoadAsync(settings.BackgroundImagePath);
                }
                catch (Exception ex)
                {
                    // Log error but continue without background image
                    // This will be handled by the fallback mechanism
                }
            }

            // Create new image with specified dimensions
            using var image = new Image<Rgba32>(settings.ExportWidth, settings.ExportHeight);
            
            // Apply background
            if (backgroundImage != null)
            {
                await ApplyBackgroundAsync(image, backgroundImage, settings);
            }
            else
            {
                // Create gradient background if no image provided
                await CreateGradientBackgroundAsync(image, settings);
            }

            // Apply text overlay with fallback
            await ApplyTextOverlayWithFallbackAsync(image, settings);

            // Save image with retry mechanism
            await SaveImageWithRetryAsync(image, outputPath, settings);

            return outputPath;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to generate cover art: {ex.Message}", ex);
        }
    }

    public async Task<bool> SaveCoverArtAsync(string libraryId, string coverArtPath)
    {
        try
        {
            if (!File.Exists(coverArtPath))
                return false;

            // Create library-specific directory
            var libraryDirectory = Path.Combine(_outputDirectory, "Libraries", libraryId);
            Directory.CreateDirectory(libraryDirectory);

            // Copy cover art to library directory
            var fileName = Path.GetFileName(coverArtPath);
            var destinationPath = Path.Combine(libraryDirectory, fileName);
            
            File.Copy(coverArtPath, destinationPath, overwrite: true);

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<byte[]?> GetCoverArtAsync(string libraryId)
    {
        try
        {
            var libraryDirectory = Path.Combine(_outputDirectory, "Libraries", libraryId);
            if (!Directory.Exists(libraryDirectory))
                return null;

            // Search for both PNG and GIF files
            var pngFiles = Directory.GetFiles(libraryDirectory, "*.png");
            var gifFiles = Directory.GetFiles(libraryDirectory, "*.gif");
            var allFiles = pngFiles.Concat(gifFiles).ToArray();
            
            if (allFiles.Length == 0)
                return null;

            // Get the most recent file
            var latestFile = allFiles.OrderByDescending(f => File.GetCreationTime(f)).First();
            return await File.ReadAllBytesAsync(latestFile);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> DeleteCoverArtAsync(string libraryId)
    {
        try
        {
            var libraryDirectory = Path.Combine(_outputDirectory, "Libraries", libraryId);
            if (!Directory.Exists(libraryDirectory))
                return true;

            Directory.Delete(libraryDirectory, recursive: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task ApplyBackgroundAsync(Image<Rgba32> image, Image backgroundImage, CoverArtSettings settings)
    {
        // Check if we should preserve original aspect ratio
        var preserveAspectRatio = settings.DimensionPreset == "custom";
        
        if (preserveAspectRatio)
        {
            // For custom dimensions, fit the background to cover the entire canvas while maintaining aspect ratio
            var scaleX = (float)image.Width / backgroundImage.Width;
            var scaleY = (float)image.Height / backgroundImage.Height;
            var scale = Math.Max(scaleX, scaleY); // Use the larger scale to ensure full coverage
            
            var newWidth = (int)(backgroundImage.Width * scale);
            var newHeight = (int)(backgroundImage.Height * scale);
            
            backgroundImage.Mutate(x => x.Resize(newWidth, newHeight));
            
            // Center the background image
            var offsetX = (newWidth - image.Width) / 2;
            var offsetY = (newHeight - image.Height) / 2;
            
            // Crop to fit the canvas
            backgroundImage.Mutate(x => x.Crop(new Rectangle(offsetX, offsetY, image.Width, image.Height)));
        }
        else
        {
            // For presets, resize to exact dimensions (may distort aspect ratio)
            backgroundImage.Mutate(x => x.Resize(image.Width, image.Height));
        }

        // Apply blur effect if specified
        if (settings.BackgroundBlur > 0)
        {
            backgroundImage.Mutate(x => x.GaussianBlur(settings.BackgroundBlur));
        }

        // Apply dimming effect
        if (settings.BackgroundDim > 0)
        {
            var dimPixel = Color.ParseHex(settings.DimColor).ToPixel<Rgba32>();
            var dimBrush = new SolidBrush(Color.FromRgba(
                dimPixel.R,
                dimPixel.G,
                dimPixel.B,
                (byte)(255 * settings.BackgroundDim)
            ));

            backgroundImage.Mutate(x => x.Fill(dimBrush));
        }

        // Draw background onto main image
        image.Mutate(x => x.DrawImage(backgroundImage, Point.Empty, 1f));
    }

    private static async Task CreateGradientBackgroundAsync(Image<Rgba32> image, CoverArtSettings settings)
    {
        if (settings.BackgroundGradient?.IsEnabled == true)
        {
            await ApplyGradientBackgroundAsync(image, settings.BackgroundGradient);
        }
        else
        {
            var backgroundColor = Color.ParseHex(settings.DimColor);
            image.Mutate(x => x.Fill(backgroundColor));
        }
    }

    private static async Task ApplyTextOverlayAsync(Image<Rgba32> image, CoverArtSettings settings)
    {
        // Use custom font if provided, otherwise system fonts with fallback options
        var font = CreateFont(settings);

        // Parse text color
        var textColor = Color.ParseHex(settings.TextColor);

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
            var shadowColor = Color.ParseHex(settings.TextShadowColor);
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
            var outlineColor = Color.ParseHex(settings.TextOutlineColor);
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

    /// <summary>
    /// Creates a font with custom font support and fallback options
    /// </summary>
    private static Font CreateFont(CoverArtSettings settings)
    {
        // Try custom font first if provided
        if (!string.IsNullOrEmpty(settings.CustomFontPath) && File.Exists(settings.CustomFontPath))
        {
            try
            {
                var fontCollection = new FontCollection();
                var fontFamily = fontCollection.Add(settings.CustomFontPath);
                return fontFamily.CreateFont(settings.TextSize, (FontStyle)(int)settings.TextWeight);
            }
            catch
            {
                // Fall back to system fonts if custom font fails
            }
        }

        // Try common system fonts in order of preference
        var fontNames = new[] { "Arial", "Segoe UI", "Helvetica", "Tahoma", "Verdana", "DejaVu Sans" };
        
        foreach (var fontName in fontNames)
        {
            try
            {
                return SystemFonts.CreateFont(fontName, settings.TextSize, (FontStyle)(int)settings.TextWeight);
            }
            catch
            {
                // Continue to next font if this one fails
                continue;
            }
        }
        
        // If all specific fonts fail, use the first available system font
        try
        {
            var availableFonts = SystemFonts.Families.ToList();
            if (availableFonts.Any())
            {
                return SystemFonts.CreateFont(availableFonts.First().Name, settings.TextSize, (FontStyle)(int)settings.TextWeight);
            }
        }
        catch
        {
            // If even this fails, we'll throw an exception
        }
        
        throw new InvalidOperationException("No suitable fonts found on the system");
    }

    /// <summary>
    /// Applies gradient background to the image
    /// </summary>
    private static async Task ApplyGradientBackgroundAsync(Image<Rgba32> image, GradientSettings gradient)
    {
        var startColor = Color.ParseHex(gradient.StartColor);
        var endColor = Color.ParseHex(gradient.EndColor);

        if (gradient.Type == GradientType.Linear)
        {
            // Create linear gradient
            var gradientBrush = new LinearGradientBrush(
                new PointF(0, 0),
                new PointF((float)Math.Cos(gradient.Angle * Math.PI / 180) * image.Width,
                          (float)Math.Sin(gradient.Angle * Math.PI / 180) * image.Height),
                GradientRepetitionMode.None,
                new ColorStop(0f, startColor),
                new ColorStop(1f, endColor)
            );

            image.Mutate(x => x.Fill(gradientBrush));
        }
        else if (gradient.Type == GradientType.Radial)
        {
            // Create radial gradient
            var centerX = gradient.CenterX * image.Width;
            var centerY = gradient.CenterY * image.Height;
            var radius = gradient.Radius * Math.Min(image.Width, image.Height);

            var gradientBrush = new RadialGradientBrush(
                new PointF(centerX, centerY),
                radius,
                GradientRepetitionMode.None,
                new ColorStop(0f, startColor),
                new ColorStop(1f, endColor)
            );

            image.Mutate(x => x.Fill(gradientBrush));
        }
    }

    /// <summary>
    /// Applies text overlay with fallback mechanisms
    /// </summary>
    private static async Task ApplyTextOverlayWithFallbackAsync(Image<Rgba32> image, CoverArtSettings settings)
    {
        try
        {
            await ApplyTextOverlayAsync(image, settings);
        }
        catch (Exception ex)
        {
            // Fallback: Create simple text overlay without advanced features
            try
            {
                var font = SystemFonts.CreateFont("Arial", Math.Max(12, settings.TextSize * 0.5f));
                var textColor = Color.ParseHex(settings.TextColor);
                var position = new PointF(image.Width / 2f, image.Height / 2f);

                image.Mutate(x => x.DrawText(settings.Title, font, textColor, position));
            }
            catch
            {
                // Ultimate fallback: Just fill with the dim/background color
                var backgroundColor = Color.ParseHex(settings.DimColor);
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
                return; // Success
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
