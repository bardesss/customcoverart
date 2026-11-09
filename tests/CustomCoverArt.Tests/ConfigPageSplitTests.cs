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
}
