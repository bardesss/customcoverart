using CustomCoverArt.Models;
using Microsoft.AspNetCore.Http;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Jpeg;

namespace CustomCoverArt.Services;

    /// <summary>
    /// Service for image processing operations
    /// </summary>
    public class ImageProcessingService : IImageProcessingService
    {
        private readonly ILocalizationService _localizationService;

        public ImageProcessingService(ILocalizationService localizationService)
        {
            _localizationService = localizationService;
        }
    public async Task<byte[]> ProcessImageAsync(byte[] imageData, CoverArtSettings settings)
    {
        try
        {
            using var image = Image.Load(imageData);
            
            // Resize image if needed
            if (settings.ExportScale != 1.0f)
            {
                var newWidth = (int)(image.Width * settings.ExportScale);
                var newHeight = (int)(image.Height * settings.ExportScale);
                image.Mutate(x => x.Resize(newWidth, newHeight));
            }

            // Convert to specified format
            using var memoryStream = new MemoryStream();
            
            if (settings.OutputFormat?.ToLowerInvariant() == "gif")
            {
                await image.SaveAsync(memoryStream, new GifEncoder());
            }
            else
            {
                await image.SaveAsync(memoryStream, new PngEncoder());
            }

            return memoryStream.ToArray();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to process image: {ex.Message}", ex);
        }
    }

    public async Task<byte[]> ProcessGifAsync(byte[] gifData, CoverArtSettings settings)
    {
        try
        {
            using var gif = Image.Load(gifData);
            
            // For GIFs, we'll process the first frame
            // In a more advanced implementation, you might want to process all frames
            using var firstFrame = gif.Frames.CloneFrame(0);
            
            // Apply the same processing as regular images
            if (settings.ExportScale != 1.0f)
            {
                var newWidth = (int)(firstFrame.Width * settings.ExportScale);
                var newHeight = (int)(firstFrame.Height * settings.ExportScale);
                firstFrame.Mutate(x => x.Resize(newWidth, newHeight));
            }

            using var memoryStream = new MemoryStream();
            await firstFrame.SaveAsync(memoryStream, new PngEncoder());
            
            return memoryStream.ToArray();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to process GIF: {ex.Message}", ex);
        }
    }

    public async Task<bool> IsGifImageAsync(byte[] imageData)
    {
        try
        {
            using var image = Image.Load(imageData);
            return image.Metadata.DecodedImageFormat?.Name?.ToLowerInvariant() == "gif";
        }
        catch
        {
            return false;
        }
    }

    public Task<(int width, int height)> GetImageDimensionsAsync(byte[] imageData)
    {
        try
        {
            // Identify reads only the header — it does NOT fully decode the
            // pixels, so a small file that declares huge dimensions (a
            // decompression bomb) cannot exhaust memory here.
            var info = Image.Identify(imageData);
            return Task.FromResult((info.Width, info.Height));
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to get image dimensions: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Determines the output format for "auto". The generator always produces a
    /// single (non-animated) frame, so PNG is used unconditionally: it is 24-bit
    /// and lossless, whereas GIF is limited to 256 colours and would badly band
    /// photographic backgrounds, blurs and gradients. Users can still force GIF
    /// explicitly, but "auto" never chooses it.
    /// </summary>
    public Task<string> DetermineOptimalFormatAsync(CoverArtSettings settings)
    {
        return Task.FromResult("png");
    }

    /// <summary>
    /// Calculates text complexity score (0.0 = simple, 1.0 = complex)
    /// </summary>
    private static float CalculateTextComplexity(CoverArtSettings settings)
    {
        var complexity = 0.0f;

        // Text length factor
        var textLength = settings.Title?.Length ?? 0;
        if (textLength > 50) complexity += 0.3f;
        else if (textLength > 20) complexity += 0.1f;

        // Font weight factor
        if (settings.TextWeight >= FontWeight.Bold) complexity += 0.2f;

        // Text effects factor
        if (settings.TextShadow) complexity += 0.2f;
        if (settings.TextOutline) complexity += 0.2f;

        // Background complexity factor
        if (settings.BackgroundBlur > 2) complexity += 0.1f;
        if (settings.BackgroundGradient?.IsEnabled == true) complexity += 0.1f;

        return Math.Min(complexity, 1.0f);
    }

    /// <summary>
    /// Validates if the uploaded file is a supported image format with enhanced security
    /// </summary>
    public static bool IsValidImageFormat(string fileName)
    {
        var supportedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tiff", ".tif" };
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        
        // Check for double extensions (security risk)
        if (fileName.Count(c => c == '.') > 1)
        {
            return false;
        }
        
        // Check for suspicious characters
        var suspiciousChars = new[] { "<", ">", ":", "\"", "|", "?", "*", "\\", "/" };
        if (suspiciousChars.Any(c => fileName.Contains(c)))
        {
            return false;
        }
        
        return supportedExtensions.Contains(extension);
    }

    /// <summary>
    /// Enhanced file validation with virus scanning simulation
    /// </summary>
    public async Task<ValidationResult> ValidateFileAsync(IFormFile file)
    {
        var result = new ValidationResult { IsValid = true };

        // Basic file checks
        if (file == null || file.Length == 0)
        {
            result.IsValid = false;
            result.ErrorMessage = _localizationService.GetString("errors.no_file_uploaded");
            return result;
        }

        // File size check
        if (!IsValidFileSize(file.Length, maxSizeInMB: 5))
        {
            result.IsValid = false;
            result.ErrorMessage = _localizationService.GetString("errors.file_too_large", 5);
            return result;
        }

        // File format check
        if (!IsValidImageFormat(file.FileName))
        {
            result.IsValid = false;
            result.ErrorMessage = _localizationService.GetString("errors.invalid_file_format");
            return result;
        }

        // Content-based validation. This is NOT antivirus — it verifies the
        // bytes are actually a decodable image and rejects obvious executable
        // content, which is what closes the "wrong file with an image extension"
        // gap that an extension check alone leaves open.
        result = await ScanContentAsync(file);

        return result;
    }

    /// <summary>
    /// Reads the whole upload into memory and confirms it is a genuine image,
    /// rejecting executable magic bytes and anything ImageSharp cannot decode.
    /// </summary>
    private async Task<ValidationResult> ScanContentAsync(IFormFile file)
    {
        var result = new ValidationResult { IsValid = true };

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            using var stream = file.OpenReadStream();
            await stream.CopyToAsync(ms);
            bytes = ms.ToArray();
        }

        // Reject known executable / bytecode headers up front.
        var suspiciousPatterns = new[]
        {
            new byte[] { 0x4D, 0x5A },             // PE executable (MZ)
            new byte[] { 0x7F, 0x45, 0x4C, 0x46 }, // ELF executable
            new byte[] { 0xCA, 0xFE, 0xBA, 0xBE }  // Java class file
        };

        foreach (var pattern in suspiciousPatterns)
        {
            if (StartsWith(bytes, pattern))
            {
                result.IsValid = false;
                result.ErrorMessage = _localizationService.GetString("errors.suspicious_content");
                return result;
            }
        }

        // The real check: the bytes must decode as a supported image with sane
        // dimensions. Image.Identify does not fully decode pixels, so it is cheap.
        try
        {
            var info = Image.Identify(bytes);
            if (info is null || info.Width <= 0 || info.Height <= 0)
            {
                result.IsValid = false;
                result.ErrorMessage = _localizationService.GetString("errors.invalid_file_format");
            }
        }
        catch
        {
            result.IsValid = false;
            result.ErrorMessage = _localizationService.GetString("errors.invalid_file_format");
        }

        return result;
    }

    /// <summary>
    /// Checks whether <paramref name="data"/> begins with <paramref name="prefix"/>.
    /// </summary>
    private static bool StartsWith(byte[] data, byte[] prefix)
    {
        if (data.Length < prefix.Length)
        {
            return false;
        }

        for (int i = 0; i < prefix.Length; i++)
        {
            if (data[i] != prefix[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Validates if the file size is within acceptable limits
    /// </summary>
    public static bool IsValidFileSize(long fileSizeInBytes, long maxSizeInMB = 5)
    {
        var maxSizeInBytes = maxSizeInMB * 1024 * 1024;
        return fileSizeInBytes <= maxSizeInBytes;
    }

    /// <summary>
    /// Validates generated cover art dimensions and estimated file size
    /// </summary>
    public static ValidationResult ValidateCoverArtDimensions(int width, int height, string outputFormat)
    {
        var result = new ValidationResult { IsValid = true };

        // Maximum dimensions (based on Jellyfin best practices)
        const int maxDimension = 2048;
        const int minDimension = 100;

        if (width > maxDimension || height > maxDimension)
        {
            result.IsValid = false;
            result.ErrorMessage = $"Dimensions too large. Maximum: {maxDimension}x{maxDimension} pixels";
            return result;
        }

        if (width < minDimension || height < minDimension)
        {
            result.IsValid = false;
            result.ErrorMessage = $"Dimensions too small. Minimum: {minDimension}x{minDimension} pixels";
            return result;
        }

        // Estimate file size and warn if too large
        var estimatedSizeMB = EstimateFileSize(width, height, outputFormat);
        if (estimatedSizeMB > 2) // 2MB warning threshold
        {
            result.WarningMessage = $"Large file size estimated: {estimatedSizeMB:F1}MB. Consider reducing dimensions.";
        }

        return result;
    }

    /// <summary>
    /// Estimates file size based on dimensions and format
    /// </summary>
    private static double EstimateFileSize(int width, int height, string outputFormat)
    {
        var pixels = width * height;
        
        return outputFormat?.ToLowerInvariant() switch
        {
            "gif" => pixels * 0.0003, // ~0.3 bytes per pixel for GIF
            "png" => pixels * 0.0008, // ~0.8 bytes per pixel for PNG
            _ => pixels * 0.0008      // Default to PNG estimate
        };
    }

    /// <summary>
    /// Sanitizes filename to prevent path traversal attacks
    /// </summary>
    public static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", fileName.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
        
        // Limit length
        if (sanitized.Length > 100)
        {
            var extension = Path.GetExtension(sanitized);
            var nameWithoutExtension = Path.GetFileNameWithoutExtension(sanitized);
            sanitized = nameWithoutExtension.Substring(0, 100 - extension.Length) + extension;
        }
        
        return sanitized;
    }
}
