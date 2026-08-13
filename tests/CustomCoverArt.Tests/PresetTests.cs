using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CustomCoverArt.Models;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// The config page is a static embedded resource, so these read the shipped page and
/// assert against it. That makes them real drift guards rather than documentation:
/// asserting the expected constants against themselves would pass no matter what the
/// page actually does.
/// </summary>
public class PresetTests
{
    private static string ConfigPage()
    {
        using var stream = typeof(Plugin).Assembly
            .GetManifestResourceStream("CustomCoverArt.Configuration.configPage.html");
        Assert.NotNull(stream);
        using var reader = new System.IO.StreamReader(stream!);
        return reader.ReadToEnd();
    }

    private static string PresetValue(string page, string key)
    {
        var block = Regex.Match(page, @"var JELLYFIN_PRESET = \{(.*?)\};", RegexOptions.Singleline);
        Assert.True(block.Success, "JELLYFIN_PRESET literal not found in configPage.html.");

        var m = Regex.Match(block.Groups[1].Value, key + @"\s*:\s*'?([^,'\r\n]+)'?");
        Assert.True(m.Success, $"JELLYFIN_PRESET.{key} not found.");
        return m.Groups[1].Value.Trim();
    }

    /// <summary>Pins the preset the config page applies, so the values can't drift unnoticed.</summary>
    [Fact]
    public void JellyfinPreset_MatchesTheDocumentedValues()
    {
        var page = ConfigPage();

        Assert.Equal("#0b0b10", PresetValue(page, "gradientStart"));
        Assert.Equal("#1a1a24", PresetValue(page, "gradientEnd"));
        Assert.Equal("90", PresetValue(page, "gradientAngle"));
        Assert.Equal("#ffffff", PresetValue(page, "textColor"));
        Assert.Equal("0.11", PresetValue(page, "textSize"));
    }

    /// <summary>
    /// The preset's weight is written as a raw number in JS but has to land on the C#
    /// FontWeight enum — this fails if either side moves.
    /// </summary>
    [Fact]
    public void JellyfinPreset_TextWeight_IsTheBoldEnumValue()
    {
        Assert.Equal(((int)FontWeight.Bold).ToString(), PresetValue(ConfigPage(), "textWeight"));
    }

    /// <summary>
    /// Parses the I18N object out of the shipped page into one key/value map per locale.
    /// Discovers the locale blocks rather than naming them, so adding a language is a
    /// change to configPage.html alone.
    /// </summary>
    private static Dictionary<string, Dictionary<string, string>> I18nLocales()
    {
        var page = ConfigPage();

        var start = page.IndexOf("var I18N = {", System.StringComparison.Ordinal);
        Assert.True(start > 0, "I18N object not found in configPage.html.");
        var end = page.IndexOf("\n        };", start, System.StringComparison.Ordinal);
        Assert.True(end > start, "Could not locate the end of the I18N object.");
        var body = page.Substring(start, end - start);

        var heads = Regex.Matches(body, @"^\s{12}([a-z]{2}): \{", RegexOptions.Multiline);
        Assert.True(heads.Count >= 2, "Expected at least two locale blocks in the I18N object.");

        var locales = new Dictionary<string, Dictionary<string, string>>();
        for (var i = 0; i < heads.Count; i++)
        {
            var from = heads[i].Index + heads[i].Length;
            var to = i + 1 < heads.Count ? heads[i + 1].Index : body.Length;

            var code = heads[i].Groups[1].Value;
            var map = new Dictionary<string, string>();
            foreach (Match m in Regex.Matches(body.Substring(from, to - from), @"'([A-Za-z0-9_.]+)'\s*:\s*'((?:[^'\\]|\\.)*)'"))
            {
                Assert.False(map.ContainsKey(m.Groups[1].Value), $"Duplicate key '{m.Groups[1].Value}' in the {code} block.");
                map[m.Groups[1].Value] = m.Groups[2].Value;
            }

            Assert.NotEmpty(map);
            locales[code] = map;
        }

        return locales;
    }

    private static string Placeholders(string value) =>
        string.Join(",", Regex.Matches(value, @"\{\d\}").Select(m => m.Value).OrderBy(v => v, System.StringComparer.Ordinal));

    /// <summary>
    /// Every plan for this project carries a "keep the languages in sync" constraint that
    /// had only ever been checked by eye. A key present in the markup but missing from a
    /// language silently renders English text in a translated UI.
    /// </summary>
    [Fact]
    public void EveryI18nKeyUsedInMarkup_ExistsInEveryLanguage()
    {
        var page = ConfigPage();

        var used = Regex.Matches(page, @"data-i18n(?:-ph)?=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.NotEmpty(used);

        foreach (var locale in I18nLocales())
        {
            var missing = used.Where(k => !locale.Value.ContainsKey(k)).ToList();
            Assert.True(missing.Count == 0, $"Keys used in markup but missing from {locale.Key}: " + string.Join(", ", missing));
        }
    }

    /// <summary>
    /// English is the source of truth and the runtime fallback: a key only a translation
    /// defines is unreachable, and drifting {0}/{1} placeholders break string.Format at
    /// exactly the moment an error needs to be readable.
    /// </summary>
    [Fact]
    public void EveryLanguage_DefinesTheSameKeysAndPlaceholdersAsEnglish()
    {
        var locales = I18nLocales();
        Assert.True(locales.ContainsKey("en"), "The I18N object has no en block to compare against.");
        var en = locales["en"];

        foreach (var locale in locales.Where(l => l.Key != "en"))
        {
            var missing = en.Keys.Where(k => !locale.Value.ContainsKey(k)).ToList();
            var extra = locale.Value.Keys.Where(k => !en.ContainsKey(k)).ToList();

            Assert.True(missing.Count == 0, $"{locale.Key} is missing keys defined in en: " + string.Join(", ", missing));
            Assert.True(extra.Count == 0, $"{locale.Key} defines keys absent from en: " + string.Join(", ", extra));

            var drifted = en.Keys
                .Where(k => Placeholders(en[k]) != Placeholders(locale.Value[k]))
                .ToList();
            Assert.True(drifted.Count == 0, $"{locale.Key} has placeholder drift vs en: " + string.Join(", ", drifted));
        }
    }

    /// <summary>
    /// applyI18n returns early for English, so the text sitting in the markup is exactly
    /// what an English user reads — the en block is never applied over it. When the two
    /// drift, English and every translation quietly say different things.
    /// </summary>
    [Fact]
    public void MarkupDefaultText_MatchesTheEnglishDictionary()
    {
        var page = ConfigPage();
        var en = I18nLocales()["en"];

        var mismatched = new List<string>();
        foreach (Match m in Regex.Matches(page, @"<(\w+)([^>]*\sdata-i18n=""([^""]+)""[^>]*)>([^<>]*)</\1>"))
        {
            var key = m.Groups[3].Value;
            if (!en.TryGetValue(key, out var expected))
            {
                continue;
            }

            var markup = System.Net.WebUtility.HtmlDecode(m.Groups[4].Value.Trim());
            if (markup != expected.Replace("\\'", "'", System.StringComparison.Ordinal))
            {
                mismatched.Add($"{key} (markup \"{markup}\" vs en \"{expected}\")");
            }
        }

        Assert.True(mismatched.Count == 0, "Markup text disagrees with the en dictionary: " + string.Join("; ", mismatched));
    }

    /// <summary>
    /// Guards the cleanup that removed the strings left behind by the v3 editor rewrite:
    /// an unused key is dead weight every future translator has to translate.
    /// </summary>
    [Fact]
    public void EveryDefinedI18nKey_IsReachableFromTheMarkupOrScript()
    {
        var page = ConfigPage();

        var used = new HashSet<string>(Regex.Matches(page, @"data-i18n(?:-ph)?=""([^""]+)""").Select(m => m.Groups[1].Value));
        foreach (Match m in Regex.Matches(page, @"[^A-Za-z0-9_]t\(\s*'([^']+)'"))
        {
            used.Add(m.Groups[1].Value);
        }

        // Keys reached by concatenation — t('layer.' + act) — only ever show their prefix
        // in the source, so everything under that prefix counts as reachable.
        var prefixes = Regex.Matches(page, @"[^A-Za-z0-9_]t\(\s*'([A-Za-z0-9_.]+\.)'\s*\+")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        var dead = I18nLocales()["en"].Keys
            .Where(k => !used.Contains(k) && !prefixes.Any(p => k.StartsWith(p, System.StringComparison.Ordinal)))
            .OrderBy(k => k, System.StringComparer.Ordinal)
            .ToList();

        Assert.True(dead.Count == 0, "Translation keys defined but never used: " + string.Join(", ", dead));
    }
}
