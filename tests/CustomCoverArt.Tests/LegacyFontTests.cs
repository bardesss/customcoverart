using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using CustomCoverArt.Services.Rendering;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using Xunit;

namespace CustomCoverArt.Tests;

// Shares FontStore's process-wide warn-once set and log target with FontStoreTests.
[Collection("FontStoreState")]
public class LegacyFontTests
{
    private const string InvalidFontKey = "errors.invalid_font_format";

    [Theory]
    [InlineData("sub.woff2", ".woff2")]
    [InlineData("sub.woff", ".woff")]
    public void LegacyFont_FallsBackToBundledAndLogsOnce(string fixture, string extension)
    {
        using var dir = new TempFolder();
        var path = dir.Copy(fixture, "old" + extension);
        var log = new CapturingLogger<CoverArtService>();
        using var capture = FontStore.CaptureLogForTests(log);
        FontStore.ResetWarningsForTests();
        var layer = new CoverLayer { Type = "text", Content = "Hello", FontPath = path, Weight = FontWeight.SemiBold, Color = "#ffffff", Size = 0.2f };

        for (var i = 0; i < 3; i++)
        {
            Assert.Same(FontStore.Bundled(FontWeight.SemiBold), FontStore.Resolve(layer));
            Assert.Equal("Noto Sans", FontStore.Resolve(layer).FamilyName);
        }

        var warning = Assert.Single(log.Entries, e => e.Message.Contains("old" + extension));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("Re-upload", warning.Message);
        Assert.Contains("(" + extension.TrimStart('.').ToUpperInvariant() + ")", warning.Message);

        // A full render with that layer neither throws nor draws nothing, and does not warn again.
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 200, Height = 100 } };
        doc.Background.DimColor = "#000000";
        doc.Layers.Add(layer);
        using var canvas = RenderSurface.New(200, 100, SKColors.Black);

        var ex = Record.Exception(() => DocumentRenderer.ComposeDocumentFrame(canvas, null, doc));

        Assert.Null(ex);
        Assert.Single(log.Entries, e => e.Message.Contains("old" + extension));
        Assert.Contains(Enumerable.Range(0, 100).SelectMany(y => Enumerable.Range(0, 200).Select(x => TestPixels.Px(canvas, x, y))), p => p.R > 128);
    }

    [Fact]
    public void LegacyFont_EachFileGetsItsOwnWarning()
    {
        using var dir = new TempFolder();
        var a = dir.Copy("sub.woff2", "a.woff2");
        var b = dir.Copy("sub.woff2", "b.woff2");
        var log = new CapturingLogger<CoverArtService>();
        using var capture = FontStore.CaptureLogForTests(log);
        FontStore.ResetWarningsForTests();

        FontStore.Resolve(new CoverLayer { FontPath = a });
        FontStore.Resolve(new CoverLayer { FontPath = b });
        FontStore.Resolve(new CoverLayer { FontPath = a });

        Assert.Equal(2, log.Entries.Count(e => e.Level == LogLevel.Warning && (e.Message.Contains("a.woff2") || e.Message.Contains("b.woff2"))));
    }

    [Fact]
    public void LegacyFont_UppercaseExtensionIsStillLegacy_AndUppercaseTtfIsStillSupported()
    {
        using var dir = new TempFolder();
        var legacy = dir.Copy("sub.woff", "SHOUT.WOFF");
        var modern = dir.Copy("sub.ttf", "SHOUT.TTF");

        Assert.False(FontStore.TryGetCustom(legacy, out _));
        Assert.True(FontStore.TryGetCustom(modern, out _));
    }

    private static async Task<CustomCoverArt.Models.ApiResponse<string>> Upload(byte[] bytes, string name)
    {
        var controller = ControllerHost.New();
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name);
        return await controller.UploadFont(file);
    }

    [Theory]
    [InlineData("sub.woff2")]
    [InlineData("sub.woff")]
    public async Task UploadFont_DroppedOrUnknownFormat_IsRefusedWithTheFontMessage(string fixture)
    {
        var result = await Upload(FontFixtures.Bytes(fixture), fixture);

        Assert.False(result.Success);
        Assert.Equal(InvalidFontKey, result.ErrorMessage);
    }

    [Theory]
    [InlineData("sub.ttf", "cover.ttf")]
    [InlineData("sub.otf", "cover.otf")]
    [InlineData("sub.ttc", "cover.ttc")]
    [InlineData("sub.ttf", "COVER.TTF")]
    public async Task UploadFont_SupportedFormats_AreAccepted(string fixture, string uploadName)
    {
        var result = await Upload(FontFixtures.Bytes(fixture), uploadName);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(result.Data));
        Assert.True(FontStore.TryGetCustom(result.Data!, out _));
    }

    [Fact]
    public async Task UploadFont_Ttf_IsAccepted()
    {
        var result = await Upload(FontFixtures.Bytes("sub.ttf"), "sub.ttf");

        Assert.True(result.Success, result.ErrorMessage);
    }

    [Fact]
    public async Task UploadFont_Otf_IsAccepted()
    {
        var result = await Upload(FontFixtures.Bytes("sub.otf"), "sub.otf");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(File.Exists(result.Data));
    }

    [Theory]
    [InlineData("sub.woff2", "sneaky.ttf")]
    [InlineData("sub.woff", "sneaky.otf")]
    public async Task UploadFont_DroppedFormatUnderASupportedName_IsRefused(string fixture, string name)
    {
        var result = await Upload(FontFixtures.Bytes(fixture), name);

        Assert.False(result.Success);
        Assert.Equal(InvalidFontKey, result.ErrorMessage);
    }

    [Fact]
    public async Task UploadFont_ValidMagicWithADamagedTableDirectory_IsRefused()
    {
        var bytes = FontFixtures.Bytes("sub.ttf");
        FontFixtures.SetOffsetAndLength(bytes, "glyf", 0xFFFFFFF0u, 0x20u);

        var result = await Upload(bytes, "damaged.ttf");

        Assert.False(result.Success);
        Assert.Equal(InvalidFontKey, result.ErrorMessage);
    }

    [Fact]
    public async Task UploadFont_ValidMagicFollowedByGarbage_IsRefused()
    {
        var bytes = new byte[200];
        new Random(3).NextBytes(bytes);
        new byte[] { 0x4F, 0x54, 0x54, 0x4F }.CopyTo(bytes, 0);

        var result = await Upload(bytes, "junk.otf");

        Assert.False(result.Success);
        Assert.Equal(InvalidFontKey, result.ErrorMessage);
    }

    [Fact]
    public async Task UploadFont_NotAFontExtension_IsRefusedWithTheFontMessage()
    {
        var result = await Upload(FontFixtures.Bytes("sub.ttf"), "font.exe");

        Assert.False(result.Success);
        Assert.Equal(InvalidFontKey, result.ErrorMessage);
    }

    [Fact]
    public async Task UploadFont_TooLarge_KeepsItsOwnMessage()
    {
        var result = await Upload(new byte[(5 * 1024 * 1024) + 1], "huge.ttf");

        Assert.False(result.Success);
        Assert.Equal("errors.font_too_large", result.ErrorMessage);
    }

    [Fact]
    public void FontAcceptList_MatchesTheServer()
    {
        var html = RepoFiles.Read("Configuration/configPage.html");
        var input = Regex.Match(html, "<input[^>]*id=\"ccaFont\"[^>]*>");
        Assert.True(input.Success, "#ccaFont input not found");
        var accept = Regex.Match(input.Value, "accept=\"([^\"]*)\"");
        Assert.True(accept.Success, "#ccaFont has no accept attribute");
        var htmlSet = accept.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

        var js = RepoFiles.Read("Configuration/configPage.js");
        var array = Regex.Match(js, @"SUPPORTED_FONT_EXT\s*=\s*\[([^\]]*)\]");
        Assert.True(array.Success, "SUPPORTED_FONT_EXT not found");
        var jsSet = Regex.Matches(array.Groups[1].Value, "'([^']+)'").Select(m => m.Groups[1].Value).ToHashSet();

        Assert.True(FontStore.SupportedExtensions.SetEquals(htmlSet), "accept list: " + string.Join(",", htmlSet));
        Assert.True(FontStore.SupportedExtensions.SetEquals(jsSet), "SUPPORTED_FONT_EXT: " + string.Join(",", jsSet));
    }

    private static string FunctionBody(string js, string name)
    {
        var start = js.IndexOf("function " + name + "(", StringComparison.Ordinal);
        Assert.True(start >= 0, name + " not found");
        var next = js.IndexOf("\n        function ", start + 1, StringComparison.Ordinal);
        return next < 0 ? js[start..] : js[start..next];
    }

    [Fact]
    public void LegacyFontNotice_IsTranslatedAndWired()
    {
        var html = RepoFiles.Read("Configuration/configPage.html");
        var notice = Regex.Match(html, "<div[^>]*id=\"ccaFontLegacy\"[^>]*>");
        Assert.True(notice.Success, "#ccaFontLegacy not found");
        Assert.Contains("data-i18n=\"font.reupload\"", notice.Value);
        Assert.Contains("hidden", notice.Value);

        var js = RepoFiles.Read("Configuration/configPage.js");
        Assert.Equal(3, Regex.Matches(js, @"'font\.reupload'\s*:").Count);
        Assert.Contains("lettertypeformaat", js);   // nl table
        Assert.Contains("Súbela de nuevo", js);     // es table
        Assert.Contains("isLegacyFontPath", FunctionBody(js, "syncControlsFromDocument"));
        Assert.Contains("isLegacyFontPath", FunctionBody(js, "ensureLayerFont"));

        var hints = Regex.Matches(js, @"'hint\.font'\s*:\s*'([^']*)'").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(3, hints.Length);
        Assert.All(hints, h =>
        {
            Assert.DoesNotContain("WOFF", h, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("TTC", h);
        });
    }

    [Theory]
    [InlineData("en", "TTF, OTF and TTC")]
    [InlineData("nl", "TTF-, OTF- en TTC-")]
    [InlineData("es", "TTF, OTF y TTC")]
    public void InvalidFontFormatMessage_ExistsInEveryLanguage_WithoutWoff(string language, string mentions)
    {
        using var doc = JsonDocument.Parse(RepoFiles.Read($"Resources/{language}.json"));

        Assert.True(doc.RootElement.TryGetProperty(InvalidFontKey, out var text));
        Assert.Contains(mentions, text.GetString());
        Assert.DoesNotContain("WOFF", text.GetString(), StringComparison.OrdinalIgnoreCase);
    }
}
