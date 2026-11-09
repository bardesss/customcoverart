using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MediaBrowser.Model.Net;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// The config page ships as three plugin pages: the markup, its controller script and
/// its stylesheet. Jellyfin Web imports the script as an ES module named by the page
/// element's data-controller, and serves each file with a content type derived from its
/// extension. A wrong name, a missing resource or a wrong MIME type all fail the same way
/// on a live server — a blank, unstyled page with nothing in the plugin's log — so they
/// are pinned here.
/// </summary>
public class ConfigPageSplitTests
{
    private static readonly string[] JavaScriptMimeTypes =
        { "text/javascript", "application/javascript", "application/x-javascript" };

    // GetPages reads only Name and the type's namespace. Skipping the constructor keeps
    // the static Plugin.Instance (which other tests expect to be null) untouched.
    private static Plugin BarePlugin() => (Plugin)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));

    [Fact]
    public void GetPages_ListsTheEditorFirst_ThenItsScriptAndStyles()
    {
        var pages = BarePlugin().GetPages().ToList();

        Assert.Equal(3, pages.Count);

        Assert.Equal("Custom Cover Art", pages[0].Name);
        Assert.Equal("CustomCoverArt.Configuration.configPage.html", pages[0].EmbeddedResourcePath);
        Assert.True(pages[0].EnableInMainMenu);

        Assert.Equal("customcoverartjs", Plugin.ScriptPageName);
        Assert.Equal(Plugin.ScriptPageName, pages[1].Name);
        Assert.Equal("CustomCoverArt.Configuration.configPage.js", pages[1].EmbeddedResourcePath);
        Assert.False(pages[1].EnableInMainMenu);

        Assert.Equal("customcoverartcss", Plugin.StylePageName);
        Assert.Equal(Plugin.StylePageName, pages[2].Name);
        Assert.Equal("CustomCoverArt.Configuration.configPage.css", pages[2].EmbeddedResourcePath);
        Assert.False(pages[2].EnableInMainMenu);
    }

    [Fact]
    public void EveryRegisteredPageIsEmbedded()
    {
        foreach (var page in BarePlugin().GetPages())
        {
            using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(page.EmbeddedResourcePath);
            Assert.True(stream is not null, $"{page.Name}: {page.EmbeddedResourcePath} is not embedded.");
        }
    }

    /// <summary>
    /// DashboardController.GetDashboardConfigurationPage answers with
    /// MimeTypes.GetMimeType(EmbeddedResourcePath). Browsers refuse a module script without
    /// a JavaScript MIME type and ignore a stylesheet that is not text/css.
    /// </summary>
    [Fact]
    public void JellyfinServesTheScriptAsJavaScript_AndTheStylesAsCss()
    {
        var js = (MimeTypes.GetMimeType("CustomCoverArt.Configuration.configPage.js") ?? string.Empty).Split(';')[0].Trim();
        Assert.Contains(js, JavaScriptMimeTypes);

        var css = (MimeTypes.GetMimeType("CustomCoverArt.Configuration.configPage.css") ?? string.Empty).Split(';')[0].Trim();
        Assert.Equal("text/css", css);
    }

    [Fact]
    public void Markup_NamesItsControllerAndLinksItsStyles_AndCarriesNoInlineCode()
    {
        var html = ConfigPageSource.Html();

        var pageTag = Regex.Match(html, @"<div id=""CustomCoverArtConfigPage""[^>]*>");
        Assert.True(pageTag.Success, "Page element not found.");
        Assert.Contains("data-role=\"page\"", pageTag.Value, StringComparison.Ordinal);
        Assert.Contains("data-controller=\"__plugin/" + Plugin.ScriptPageName + "\"", pageTag.Value, StringComparison.Ordinal);

        // Jellyfin Web keeps only div[data-role="page"] from the fetched HTML, so the
        // stylesheet link has to live inside the page element.
        var link = html.IndexOf("<link rel=\"stylesheet\" href=\"configurationpage?name=" + Plugin.StylePageName + "\" />", StringComparison.Ordinal);
        Assert.True(link > pageTag.Index, "The stylesheet link must sit inside the page element.");

        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The link's href is relative to the document. Clients that bundle their own copy of
    /// Jellyfin Web (desktop and TV apps, a separately hosted web client) resolve it
    /// against their own origin, where it 404s, while the controller still loads because
    /// Jellyfin Web fetches it through ApiClient.getUrl. So the controller, first thing,
    /// points the link at the server the same way (Dashboard.getConfigurationResourceUrl
    /// builds the same URL), guarded so a missing ApiClient keeps the relative fallback.
    /// </summary>
    [Fact]
    public void Script_PointsTheStylesheetLinkAtTheServer_BeforeAnythingElse()
    {
        var js = ConfigPageSource.Js();

        var strict = js.IndexOf("'use strict';", StringComparison.Ordinal);
        var find = js.IndexOf("view.querySelector('link[rel=\"stylesheet\"][href*=\"" + Plugin.StylePageName + "\"]')", StringComparison.Ordinal);
        var guard = js.IndexOf("if (cssLink && window.ApiClient && typeof ApiClient.getUrl === 'function') {", StringComparison.Ordinal);
        var rewrite = js.IndexOf("cssLink.href = ApiClient.getUrl('web/ConfigurationPage', { name: '" + Plugin.StylePageName + "' });", StringComparison.Ordinal);

        Assert.True(strict > 0, "'use strict' not found.");
        Assert.True(find > strict, "The controller must look up the stylesheet link in its view.");
        Assert.True(guard > find, "The rewrite must be guarded.");
        Assert.True(rewrite > guard, "The controller must point the link at the server via ApiClient.getUrl.");
        Assert.True(rewrite < js.IndexOf("// 1. i18n", StringComparison.Ordinal),
            "The rewrite must run before anything else in the controller.");
    }

    [Fact]
    public void Script_IsAControllerModule_ScopedToItsView()
    {
        var js = ConfigPageSource.Js();

        // Jellyfin Web runs `new module.default(view, params)`: one default export, and a
        // plain function (an arrow function cannot be called with `new`).
        Assert.Single(Regex.Matches(js, @"^export ", RegexOptions.Multiline));
        Assert.Single(Regex.Matches(js, @"^export default function \(view\) \{\r?$", RegexOptions.Multiline));
        Assert.EndsWith("}", js.TrimEnd(), StringComparison.Ordinal);

        // The view element is handed in; looking the page up by id could find a stale
        // cached copy of the view.
        Assert.DoesNotContain("document.querySelector('#CustomCoverArtConfigPage')", js, StringComparison.Ordinal);
        Assert.Contains("view.addEventListener('pageshow', function () {", js, StringComparison.Ordinal);
        Assert.Contains("page.dataset.ccaInit", js, StringComparison.Ordinal);
    }

    [Fact]
    public void Styles_AreAPlainStylesheet()
    {
        var css = ConfigPageSource.Css();
        Assert.StartsWith("@charset \"UTF-8\";", css, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"</?(style|script)\b", css);
        Assert.Contains("#CustomCoverArtConfigPage .ccaLayout", css, StringComparison.Ordinal);
    }

    /// <summary>
    /// The script is laid out in numbered sections. Each anchor is a declaration that
    /// defines its section; finding it elsewhere means the layout drifted.
    /// </summary>
    [Fact]
    public void ScriptSectionsAppearInOrder_AndHoldTheirAnchors()
    {
        var js = ConfigPageSource.Js();
        string[] titles =
        {
            "1. i18n", "2. api", "3. State and document model", "4. Canvas engine",
            "5. History", "6. Bindings", "7. Poster browser modal", "8. Page lifecycle"
        };

        // Banner titles sit alone on a line at the body's 8-space indentation; the file
        // header lists the sections too, but at column 0, so it cannot match.
        var at = titles
            .Select(t => Regex.Match(js, "^        // " + Regex.Escape(t) + @"\r?$", RegexOptions.Multiline))
            .Select(m => m.Success ? m.Index : -1)
            .ToArray();
        for (var i = 0; i < titles.Length; i++)
        {
            Assert.True(at[i] > 0, $"Section banner '{titles[i]}' not found.");
            if (i > 0)
            {
                Assert.True(at[i] > at[i - 1], $"Section '{titles[i]}' comes before '{titles[i - 1]}'.");
            }
        }

        (string Anchor, int Section)[] anchors =
        {
            ("var I18N = {", 0),
            ("function jsonFetch(", 1),
            ("function requestGuard(", 1),
            ("function confirmAction(", 1),
            ("var doc = defaultDocument();", 2),
            ("function normalizeGradientStops(", 2),
            ("function syncCollageBitmap(", 3),
            ("function updateGifLoop(", 3),
            ("function drawTextLayer(", 3),
            ("function textScale(", 3),
            ("function undo(", 4),
            ("function markClean(", 4),
            ("function bindEvents(", 5),
            ("function onCanvasPointerDown(", 5),
            ("function onDocumentKeydown(", 5),
            ("function openBrowser(", 6),
            ("view.addEventListener('pageshow'", 7),
        };
        foreach (var (anchor, section) in anchors)
        {
            var idx = js.IndexOf(anchor, StringComparison.Ordinal);
            Assert.True(idx > at[section], $"'{anchor}' should be in section '{titles[section]}'.");
            if (section + 1 < titles.Length)
            {
                Assert.True(idx < at[section + 1], $"'{anchor}' should be in section '{titles[section]}'.");
            }
        }
    }
}
