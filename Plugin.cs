using System;
using System.Collections.Generic;
using System.Globalization;
using CustomCoverArt.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace CustomCoverArt;

/// <summary>
/// The Custom Cover Art plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Jellyfin application paths (provided by the server).</param>
    /// <param name="xmlSerializer">Jellyfin XML serializer (provided by the server).</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Custom Cover Art";

    /// <inheritdoc />
    public override string Description =>
        "Create and manage custom cover art for Jellyfin libraries with advanced text effects and presets.";

    // Stable, unique plugin identifier. Keep this constant across releases so
    // Jellyfin recognises upgrades of the same plugin.
    /// <inheritdoc />
    public override Guid Id => Guid.Parse("b8f4e2a1-7c3d-4e5f-9a2b-1d6c8e0f3a4b");

    /// <summary>Plugin-page name of the editor's controller script (configPage.js).
    /// configPage.html names it in data-controller="__plugin/customcoverartjs".</summary>
    public const string ScriptPageName = "customcoverartjs";

    /// <summary>Plugin-page name of the editor's stylesheet (configPage.css), linked from
    /// configPage.html as configurationpage?name=customcoverartcss.</summary>
    public const string StylePageName = "customcoverartcss";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;

        // The editor. Keep it first: the dashboard's plugin list links to a plugin's
        // first page.
        yield return new PluginPageInfo
        {
            Name = Name,
            DisplayName = Name,
            EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", ns),

            // Surface the page as its own entry in the dashboard's left navigation
            // (not only via the Plugins list). MenuIcon is a Material Icons name.
            EnableInMainMenu = true,
            MenuIcon = "image"
        };

        // The editor's controller. Jellyfin Web imports it as an ES module from
        // /web/ConfigurationPage?name=customcoverartjs; the content type comes from the
        // .js extension.
        yield return new PluginPageInfo
        {
            Name = ScriptPageName,
            EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.js", ns)
        };

        // The editor's stylesheet, served as text/css from the .css extension.
        yield return new PluginPageInfo
        {
            Name = StylePageName,
            EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.css", ns)
        };
    }
}
