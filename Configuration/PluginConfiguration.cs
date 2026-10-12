using System.Collections.Generic;
using CustomCoverArt.Models;
using MediaBrowser.Model.Plugins;

namespace CustomCoverArt.Configuration;

/// <summary>
/// Plugin configuration. Cover-art generation parameters are transient (sent
/// per-request from the UI), so this only stores small persistent preferences.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the maximum background-image upload size in megabytes (clamped to 1..50).
    /// Not shown on the configuration page; edit the plugin's XML configuration to change it.
    /// </summary>
    public int MaxUploadSizeMb { get; set; } = 5;

    /// <summary>
    /// Gets or sets the saved design templates (title/target excluded from each).
    /// </summary>
    public List<SavedTemplate> Templates { get; set; } = new();
}
