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

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = string.Format(
                CultureInfo.InvariantCulture,
                "{0}.Configuration.configPage.html",
                GetType().Namespace)
        };
    }
}
