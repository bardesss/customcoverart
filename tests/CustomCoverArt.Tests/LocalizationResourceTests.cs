using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using CustomCoverArt.Services;
using NSubstitute;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// The server-side strings ship as embedded JSON, one file per language. These read the
/// files out of the built assembly rather than off disk, so they catch a language that
/// was written but never embedded, as well as one that drifted out of sync.
/// </summary>
public class LocalizationResourceTests
{
    /// <summary>
    /// The keys the server actually asks for. GetString falls back to returning the key
    /// itself, so a typo or an over-eager cleanup surfaces as "errors.file_too_large"
    /// rendered verbatim in the UI instead of a thrown exception.
    /// </summary>
    private static readonly string[] KeysUsedByServerCode =
    {
        "errors.no_file_uploaded",
        "errors.file_too_large",
        "errors.font_too_large",
        "errors.invalid_file_format",
        "errors.too_many_uploads",
        "errors.suspicious_content",
    };

    private static Dictionary<string, string> Load(string code)
    {
        using var stream = typeof(Plugin).Assembly
            .GetManifestResourceStream($"CustomCoverArt.Resources.{code}.json");
        Assert.True(stream is not null, $"Resources/{code}.json is not embedded in the assembly.");

        using var reader = new System.IO.StreamReader(stream!);
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.ReadToEnd());
        Assert.NotNull(map);
        return map!;
    }

    private static string Placeholders(string value) =>
        string.Join(",", Regex.Matches(value, @"\{\d\}").Select(m => m.Value).OrderBy(v => v, System.StringComparer.Ordinal));

    [Fact]
    public void EverySupportedLanguage_ShipsAnEmbeddedResourceFile()
    {
        Assert.Contains("en", LocalizationService.SupportedLanguageCodes);

        foreach (var code in LocalizationService.SupportedLanguageCodes)
        {
            Assert.NotEmpty(Load(code));
        }
    }

    [Fact]
    public void EverySupportedLanguage_HasTheSameKeysAndPlaceholdersAsEnglish()
    {
        var en = Load("en");

        foreach (var code in LocalizationService.SupportedLanguageCodes.Where(c => c != "en"))
        {
            var map = Load(code);

            var missing = en.Keys.Except(map.Keys).ToList();
            var extra = map.Keys.Except(en.Keys).ToList();
            Assert.True(missing.Count == 0, $"{code}.json is missing keys defined in en.json: " + string.Join(", ", missing));
            Assert.True(extra.Count == 0, $"{code}.json defines keys absent from en.json: " + string.Join(", ", extra));

            var drifted = en.Keys.Where(k => Placeholders(en[k]) != Placeholders(map[k])).ToList();
            Assert.True(drifted.Count == 0, $"{code}.json has placeholder drift vs en.json: " + string.Join(", ", drifted));
        }
    }

    [Fact]
    public void EveryKeyTheServerCodeUses_ResolvesToRealText()
    {
        var service = new LocalizationService(Substitute.For<ILoggingService>());

        foreach (var key in KeysUsedByServerCode)
        {
            var text = service.GetString(key, 5);
            Assert.NotEqual(key, text);
            Assert.NotEmpty(text);
        }
    }

    [Fact]
    public void EnglishResource_CarriesNoKeyTheServerCodeNeverAsksFor()
    {
        var unused = Load("en").Keys.Except(KeysUsedByServerCode).OrderBy(k => k, System.StringComparer.Ordinal).ToList();
        Assert.True(unused.Count == 0, "Resource keys defined but never requested: " + string.Join(", ", unused));
    }

    [Fact]
    public void LoadedLanguages_MatchTheSupportedList()
    {
        var service = new LocalizationService(Substitute.For<ILoggingService>());

        Assert.Equal(
            LocalizationService.SupportedLanguageCodes.OrderBy(c => c, System.StringComparer.Ordinal),
            service.GetSupportedLanguages().OrderBy(c => c, System.StringComparer.Ordinal));
    }
}
