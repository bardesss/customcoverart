using CustomCoverArt.Models;
using Microsoft.AspNetCore.Http;
using SixLabors.ImageSharp;

namespace CustomCoverArt.Services;

public class ImageProcessingService : IImageProcessingService
{
    private readonly ILocalizationService _localizationService;

    public ImageProcessingService(ILocalizationService localizationService)
    {
        _localizationService = localizationService;
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

    public static bool IsValidImageFormat(string fileName)
    {
        var supportedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tiff", ".tif" };
        var extension = Path.GetExtension(fileName).ToLowerInvariant();

        // Reject double extensions and path/shell metacharacters.
        if (fileName.Count(c => c == '.') > 1)
        {
            return false;
        }

        var suspiciousChars = new[] { "<", ">", ":", "\"", "|", "?", "*", "\\", "/" };
        if (suspiciousChars.Any(c => fileName.Contains(c)))
        {
            return false;
        }

        return supportedExtensions.Contains(extension);
    }

    public async Task<ValidationResult> ValidateFileAsync(IFormFile file)
    {
        var result = new ValidationResult { IsValid = true };

        if (file == null || file.Length == 0)
        {
            result.IsValid = false;
            result.ErrorMessage = _localizationService.GetString("errors.no_file_uploaded");
            return result;
        }

        if (!IsValidFileSize(file.Length, maxSizeInMB: 5))
        {
            result.IsValid = false;
            result.ErrorMessage = _localizationService.GetString("errors.file_too_large", 5);
            return result;
        }

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
        return await ScanContentAsync(file);
    }

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

        // The real check: the bytes must decode as a supported image. Image.Identify
        // reads only the header, so it is cheap and not a decompression-bomb risk.
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

    public static bool IsValidFileSize(long fileSizeInBytes, long maxSizeInMB = 5)
    {
        var maxSizeInBytes = maxSizeInMB * 1024 * 1024;
        return fileSizeInBytes <= maxSizeInBytes;
    }

    /// <summary>Validates generated cover-art dimensions and estimates the file size.</summary>
    public static ValidationResult ValidateCoverArtDimensions(int width, int height, string outputFormat)
    {
        var result = new ValidationResult { IsValid = true };

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

        var estimatedSizeMB = EstimateFileSize(width, height, outputFormat);
        if (estimatedSizeMB > 2)
        {
            result.WarningMessage = $"Large file size estimated: {estimatedSizeMB:F1}MB. Consider reducing dimensions.";
        }

        return result;
    }

    private static double EstimateFileSize(int width, int height, string outputFormat)
    {
        var pixels = width * height;

        return outputFormat?.ToLowerInvariant() switch
        {
            "gif" => pixels * 0.0003,
            "png" => pixels * 0.0008,
            _ => pixels * 0.0008
        };
    }

    /// <summary>Sanitizes a filename to prevent path traversal (strips separators, caps length).</summary>
    public static string SanitizeFileName(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", fileName.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));

        if (sanitized.Length > 100)
        {
            var extension = Path.GetExtension(sanitized);
            var nameWithoutExtension = Path.GetFileNameWithoutExtension(sanitized);
            sanitized = nameWithoutExtension.Substring(0, 100 - extension.Length) + extension;
        }

        return sanitized;
    }
}
