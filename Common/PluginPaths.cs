using System.IO;
using MediaBrowser.Common.Configuration;

namespace CustomCoverArt.Common;

/// <summary>
/// Resolves the plugin's data directories from Jellyfin's application paths.
/// This replaces the previous approach of guessing/probing filesystem
/// locations, which was fragile and could fall back to the system temp folder.
/// </summary>
public static class PluginPaths
{
    private const string PluginFolderName = "customcoverart";

    /// <summary>Base data directory for the plugin, under Jellyfin's data path.</summary>
    public static string Base(IApplicationPaths paths) =>
        Path.Combine(paths.DataPath, PluginFolderName);

    /// <summary>Directory for generated cover art.</summary>
    public static string Generated(IApplicationPaths paths) =>
        Path.Combine(Base(paths), "generated");

    /// <summary>Directory for uploaded background images.</summary>
    public static string Uploads(IApplicationPaths paths) =>
        Path.Combine(Base(paths), "uploads");

    /// <summary>Directory for uploaded custom fonts.</summary>
    public static string Fonts(IApplicationPaths paths) =>
        Path.Combine(Base(paths), "fonts");

    /// <summary>Directory for original cover-art backups (per-target, restore points).</summary>
    public static string Backups(IApplicationPaths paths) =>
        Path.Combine(Base(paths), "backups");

    /// <summary>Creates all plugin directories if they do not exist.</summary>
    public static void EnsureCreated(IApplicationPaths paths)
    {
        Directory.CreateDirectory(Generated(paths));
        Directory.CreateDirectory(Uploads(paths));
        Directory.CreateDirectory(Fonts(paths));
    }

    /// <summary>
    /// Returns true if <paramref name="candidatePath"/> resolves to a location
    /// inside the plugin's base data directory. Used to reject client-supplied
    /// paths that try to escape the sandbox (path-traversal / arbitrary read).
    /// </summary>
    public static bool IsInsideBase(IApplicationPaths paths, string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        var baseFull = Path.GetFullPath(Base(paths))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        string candidateFull;
        try
        {
            candidateFull = Path.GetFullPath(candidatePath);
        }
        catch
        {
            return false;
        }

        // Match the filesystem's case behaviour: case-insensitive on Windows,
        // case-sensitive elsewhere (Linux, where Jellyfin usually runs).
        var comparison = System.OperatingSystem.IsWindows()
            ? System.StringComparison.OrdinalIgnoreCase
            : System.StringComparison.Ordinal;

        return candidateFull.StartsWith(baseFull, comparison);
    }
}
