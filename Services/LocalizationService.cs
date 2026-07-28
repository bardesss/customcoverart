using System.Globalization;
using System.Text.Json;

namespace CustomCoverArt.Services;

public interface ILocalizationService
{
    string GetString(string key, params object[] args);
    string GetCurrentLanguage();
    IEnumerable<string> GetSupportedLanguages();
}

public class LocalizationService : ILocalizationService
{
    private readonly ILoggingService _loggingService;
    private readonly Dictionary<string, Dictionary<string, string>> _translations;
    private string _currentLanguage = "en";
    private readonly string _fallbackLanguage = "en";

    public LocalizationService(ILoggingService loggingService)
    {
        _loggingService = loggingService;
        _translations = new Dictionary<string, Dictionary<string, string>>();
        
        LoadTranslations();
        DetectSystemLanguage();
    }

    public string GetString(string key, params object[] args)
    {
        try
        {
            // Try current language first
            if (_translations.TryGetValue(_currentLanguage, out var currentLangDict) &&
                currentLangDict.TryGetValue(key, out var currentLangValue))
            {
                return FormatString(currentLangValue, args);
            }

            // Fallback to English
            if (_currentLanguage != _fallbackLanguage &&
                _translations.TryGetValue(_fallbackLanguage, out var fallbackDict) &&
                fallbackDict.TryGetValue(key, out var fallbackValue))
            {
                _loggingService.LogDebug("Using fallback translation for key: {Key}", key);
                return FormatString(fallbackValue, args);
            }

            // If no translation found, return the key itself
            _loggingService.LogWarning("Translation not found for key: {Key} in language: {Language}", key, _currentLanguage);
            return key;
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Error getting translation for key: {Key}", ex, key);
            return key;
        }
    }

    public string GetCurrentLanguage()
    {
        return _currentLanguage;
    }

    public IEnumerable<string> GetSupportedLanguages()
    {
        return _translations.Keys.ToList();
    }

    private void LoadTranslations()
    {
        try
        {
            // Load English translations (default)
            LoadLanguageFile("en");
            
            // Load Dutch translations
            LoadLanguageFile("nl");
            
            _loggingService.LogInformation("Loaded translations for {Count} languages: {Languages}", 
                _translations.Count, string.Join(", ", _translations.Keys));
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to load translations", ex);
        }
    }

    private void LoadLanguageFile(string languageCode)
    {
        try
        {
            var resourceName = $"CustomCoverArt.Resources.{languageCode}.json";
            var assembly = typeof(LocalizationService).Assembly;
            
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                _loggingService.LogWarning("Translation file not found: {ResourceName}", resourceName);
                return;
            }

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            
            var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (translations != null)
            {
                _translations[languageCode] = translations;
                _loggingService.LogDebug("Loaded {Count} translations for language: {Language}", 
                    translations.Count, languageCode);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to load language file: {LanguageCode}", ex, languageCode);
        }
    }

    private void DetectSystemLanguage()
    {
        try
        {
            // Try to detect Jellyfin system language
            var systemLanguage = Environment.GetEnvironmentVariable("JELLYFIN_LANGUAGE") ??
                               Environment.GetEnvironmentVariable("LANG") ??
                               CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

            // Normalize language code
            systemLanguage = systemLanguage?.Split('-')[0].Split('_')[0].ToLowerInvariant();

            if (!string.IsNullOrEmpty(systemLanguage) && _translations.ContainsKey(systemLanguage))
            {
                _currentLanguage = systemLanguage;
                _loggingService.LogInformation("Detected system language: {Language}", systemLanguage);
            }
            else
            {
                _loggingService.LogInformation("Using default language: {Language}", _fallbackLanguage);
            }
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to detect system language", ex);
        }
    }

    private static string FormatString(string format, object[] args)
    {
        if (args == null || args.Length == 0)
            return format;

        try
        {
            return string.Format(format, args);
        }
        catch
        {
            return format;
        }
    }
}

