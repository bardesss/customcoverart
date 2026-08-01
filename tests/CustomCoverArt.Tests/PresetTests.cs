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
    /// Every plan for this project carries an "en/nl sync" constraint that has only ever
    /// been checked by eye. A key present in the markup but missing from a language
    /// silently renders English text in a Dutch UI.
    /// </summary>
    [Fact]
    public void EveryI18nKeyUsedInMarkup_ExistsInBothLanguages()
    {
        var page = ConfigPage();

        var enStart = page.IndexOf("            en: {", System.StringComparison.Ordinal);
        var nlStart = page.IndexOf("            nl: {", System.StringComparison.Ordinal);
        Assert.True(enStart > 0 && nlStart > enStart, "Could not locate the en/nl I18N blocks.");
        var nlEnd = page.IndexOf("\n        };", nlStart, System.StringComparison.Ordinal);
        Assert.True(nlEnd > nlStart, "Could not locate the end of the I18N object.");

        var en = page.Substring(enStart, nlStart - enStart);
        var nl = page.Substring(nlStart, nlEnd - nlStart);

        var used = Regex.Matches(page, @"data-i18n(?:-ph)?=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();

        Assert.NotEmpty(used);

        var missingEn = used.Where(k => !en.Contains($"'{k}':", System.StringComparison.Ordinal)).ToList();
        var missingNl = used.Where(k => !nl.Contains($"'{k}':", System.StringComparison.Ordinal)).ToList();

        Assert.True(missingEn.Count == 0, "Keys used in markup but missing from en: " + string.Join(", ", missingEn));
        Assert.True(missingNl.Count == 0, "Keys used in markup but missing from nl: " + string.Join(", ", missingNl));
    }
}
