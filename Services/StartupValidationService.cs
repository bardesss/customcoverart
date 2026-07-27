using CustomCoverArt.Common;
using CustomCoverArt.Models;
using MediaBrowser.Common.Configuration;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Drawing.Processing;

namespace CustomCoverArt.Services;

/// <summary>
/// Service for validating plugin configuration and dependencies at startup
/// </summary>
public interface IStartupValidationService
{
    Task<ValidationResult> ValidateConfigurationAsync();
    Task<ValidationResult> ValidateDependenciesAsync();
    Task<ValidationResult> ValidatePermissionsAsync();
    bool IsPluginReady { get; }
}

/// <summary>
/// Implementation of startup validation service
/// </summary>
public class StartupValidationService : IStartupValidationService
{
    private readonly ILoggingService _loggingService;
    private readonly IApplicationPaths _applicationPaths;
    private bool _isPluginReady = false;

    public StartupValidationService(ILoggingService loggingService, IApplicationPaths applicationPaths)
    {
        _loggingService = loggingService;
        _applicationPaths = applicationPaths;
    }

    public bool IsPluginReady => _isPluginReady;

    public async Task<ValidationResult> ValidateConfigurationAsync()
    {
        try
        {
            var result = new ValidationResult { IsValid = true };

            // Check if required directories can be created
            var testDirectories = new[]
            {
                PluginPaths.Generated(_applicationPaths),
                PluginPaths.Uploads(_applicationPaths),
                PluginPaths.Fonts(_applicationPaths)
            };

            foreach (var dir in testDirectories)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    _loggingService.LogDebug("Directory validation successful: {Directory}", dir);
                }
                catch (Exception ex)
                {
                    result.IsValid = false;
                    result.ErrorMessage = $"Cannot create directory {dir}: {ex.Message}";
                    _loggingService.LogError("Directory validation failed: {Directory}, Error: {Error}", dir, ex.Message);
                    return result;
                }
            }

            // Check if we can write to directories
            foreach (var dir in testDirectories)
            {
                try
                {
                    var testFile = Path.Combine(dir, $"test_{Guid.NewGuid():N}.tmp");
                    await File.WriteAllTextAsync(testFile, "test");
                    File.Delete(testFile);
                }
                catch (Exception ex)
                {
                    result.IsValid = false;
                    result.ErrorMessage = $"Cannot write to directory {dir}: {ex.Message}";
                    _loggingService.LogError("Write permission validation failed: {Directory}, Error: {Error}", dir, ex.Message);
                    return result;
                }
            }

            _loggingService.LogInformation("Configuration validation completed successfully");
            return result;
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Configuration validation failed: {Error}", ex.Message);
            return new ValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Configuration validation failed: {ex.Message}"
            };
        }
    }

    public async Task<ValidationResult> ValidateDependenciesAsync()
    {
        try
        {
            var result = new ValidationResult { IsValid = true };

            // Test ImageSharp functionality
            try
            {
                using var testImage = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(10, 10);
                testImage.Mutate(x => x.Fill(SixLabors.ImageSharp.Color.Red));
                
                using var memoryStream = new MemoryStream();
                await testImage.SaveAsync(memoryStream, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                
                if (memoryStream.Length == 0)
                {
                    result.IsValid = false;
                    result.ErrorMessage = "ImageSharp PNG encoding test failed";
                    return result;
                }
            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.ErrorMessage = $"ImageSharp validation failed: {ex.Message}";
                _loggingService.LogError("ImageSharp validation failed: {Error}", ex.Message);
                return result;
            }

            // Test font availability
            try
            {
                var testFont = SixLabors.Fonts.SystemFonts.CreateFont("Arial", 12);
                if (testFont == null)
                {
                    result.IsValid = false;
                    result.ErrorMessage = "No system fonts available";
                    _loggingService.LogWarning("No system fonts available");
                }
            }
            catch (Exception ex)
            {
                result.WarningMessage = $"Font system validation warning: {ex.Message}";
                _loggingService.LogWarning("Font system validation warning: {Error}", ex.Message);
            }

            _loggingService.LogInformation("Dependencies validation completed successfully");
            return result;
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Dependencies validation failed: {Error}", ex.Message);
            return new ValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Dependencies validation failed: {ex.Message}"
            };
        }
    }

    public async Task<ValidationResult> ValidatePermissionsAsync()
    {
        try
        {
            var result = new ValidationResult { IsValid = true };

            // Test file system permissions
            var testPath = Path.Combine(Path.GetTempPath(), $"CustomCoverArt_test_{Guid.NewGuid():N}");
            
            try
            {
                // Test create directory
                Directory.CreateDirectory(testPath);
                
                // Test create file
                var testFile = Path.Combine(testPath, "test.txt");
                await File.WriteAllTextAsync(testFile, "test");
                
                // Test read file
                var content = await File.ReadAllTextAsync(testFile);
                if (content != "test")
                {
                    result.IsValid = false;
                    result.ErrorMessage = "File read/write test failed";
                    return result;
                }
                
                // Test delete file
                File.Delete(testFile);
                
                // Test delete directory
                Directory.Delete(testPath);
            }
            catch (Exception ex)
            {
                result.IsValid = false;
                result.ErrorMessage = $"File system permissions test failed: {ex.Message}";
                _loggingService.LogError("File system permissions test failed: {Error}", ex.Message);
                return result;
            }

            _loggingService.LogInformation("Permissions validation completed successfully");
            return result;
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Permissions validation failed: {Error}", ex.Message);
            return new ValidationResult
            {
                IsValid = false,
                ErrorMessage = $"Permissions validation failed: {ex.Message}"
            };
        }
    }
}

