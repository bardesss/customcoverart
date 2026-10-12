using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CustomCoverArt.Services;

public interface ILocalizationService
{
    string GetString(string key, params object[] args);
    string GetCurrentLanguage();
    IEnumerable<string> GetSupportedLanguages();
}

public class LocalizationService : ILocalizationService
{
    /// <summary>
    /// Language codes shipped as Resources/&lt;code&gt;.json. Keep in sync with the
    /// I18N blocks in Configuration/configPage.html, which localizes the UI itself.
    /// </summary>
    public static readonly string[] SupportedLanguageCodes = { "en", "nl", "es" };

    private readonly ILogger<LocalizationService> _logger;
    private readonly Dictionary<string, Dictionary<string, string>> _translations;
    private string _currentLanguage = "en";
    private readonly string _fallbackLanguage = "en";

    public LocalizationService(ILogger<LocalizationService> logger)
    {
        _logger = logger;
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
                _logger.LogDebug("Using fallback translation for key: {Key}", key);
                return FormatString(fallbackValue, args);
            }

            // If no translation found, return the key itself
            _logger.LogWarning("Translation not found for key: {Key} in language: {Language}", key, _currentLanguage);
            return key;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting translation for key: {Key}", key);
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
            // English is the fallback and must load first; the rest are optional.
            // Adding a language means dropping Resources/<code>.json in and listing
            // the code here — the csproj embeds Resources\**\*.json automatically.
            foreach (var code in SupportedLanguageCodes)
            {
                LoadLanguageFile(code);
            }

            _logger.LogInformation("Loaded translations for {Count} languages: {Languages}", 
                _translations.Count, string.Join(", ", _translations.Keys));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load translations");
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
                _logger.LogWarning("Translation file not found: {ResourceName}", resourceName);
                return;
            }

            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            
            var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (translations != null)
            {
                _translations[languageCode] = translations;
                _logger.LogDebug("Loaded {Count} translations for language: {Language}", 
                    translations.Count, languageCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load language file: {LanguageCode}", languageCode);
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
                _logger.LogInformation("Detected system language: {Language}", systemLanguage);
            }
            else
            {
                _logger.LogInformation("Using default language: {Language}", _fallbackLanguage);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to detect system language");
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

