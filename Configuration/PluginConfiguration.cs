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
    /// Gets or sets the default title used when opening the editor.
    /// </summary>
    public string DefaultTitle { get; set; } = "Movies";

    /// <summary>
    /// Gets or sets the maximum upload size in megabytes for background images.
    /// </summary>
    public int MaxUploadSizeMb { get; set; } = 5;

    /// <summary>
    /// Gets or sets the UI language override (empty = auto-detect).
    /// </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the saved design templates (title/target excluded from each).
    /// </summary>
    public List<SavedTemplate> Templates { get; set; } = new();
}
