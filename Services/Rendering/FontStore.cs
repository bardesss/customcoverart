using System.Collections.Concurrent;
using CustomCoverArt.Models;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace CustomCoverArt.Services.Rendering;

/// <summary>
/// Owns every typeface the renderer draws with: the six bundled Noto Sans faces and uploaded
/// custom fonts. Typefaces are shared native objects handed to concurrent renders, so nothing
/// in the cache is ever disposed; a replaced file version is left to the finalizer.
/// </summary>
internal static class FontStore
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".ttf", ".otf", ".ttc" };

    private static readonly ConcurrentDictionary<string, Lazy<SKTypeface>> BundledFaces = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, (DateTime WriteTimeUtc, SKTypeface Typeface)> CustomFaces = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DateTime> BrokenFiles = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> Warned = new(StringComparer.Ordinal);
    private static readonly object LoadLock = new();

    private static ReadOnlySpan<byte> TrueTypeMagic => new byte[] { 0x00, 0x01, 0x00, 0x00 };

    /// <summary>Extensions a custom font may have (matched case-insensitively).</summary>
    public static IReadOnlySet<string> SupportedExtensions => Extensions;

    /// <summary>
    /// True when <paramref name="head"/> starts with the sfnt signature of TrueType (0x00010000),
    /// OpenType/CFF (OTTO), Apple TrueType (true) or a collection (ttcf).
    /// </summary>
    public static bool IsSupportedSignature(ReadOnlySpan<byte> head)
    {
        return head.StartsWith(TrueTypeMagic)
            || head.StartsWith("OTTO"u8)
            || head.StartsWith("true"u8)
            || head.StartsWith("ttcf"u8);
    }

    /// <summary>The Noto Sans face for a weight, loaded once from the embedded resources.</summary>
    public static SKTypeface Bundled(FontWeight weight)
    {
        var faceName = weight switch
        {
            FontWeight.Light => "NotoSans-Light",
            FontWeight.Medium => "NotoSans-Medium",
            FontWeight.SemiBold => "NotoSans-SemiBold",
            FontWeight.Bold => "NotoSans-Bold",
            FontWeight.ExtraBold => "NotoSans-ExtraBold",
            _ => "NotoSans-Regular", // Normal and any unmapped value
        };

        return BundledFaces.GetOrAdd(faceName, static name => new Lazy<SKTypeface>(() => LoadBundled(name))).Value;
    }

    /// <summary>
    /// The face a layer draws with: its uploaded font, or the bundled face at the layer's weight
    /// when it has none or the file cannot be used (see <see cref="TryGetCustom"/>).
    /// </summary>
    public static SKTypeface Resolve(CoverLayer layer)
    {
        if (!string.IsNullOrEmpty(layer.FontPath) && TryGetCustom(layer.FontPath, out var custom))
        {
            return custom;
        }

        return Bundled(layer.Weight);
    }

    /// <summary>
    /// Loads (or reuses) the typeface of a custom font file, face 0 for collections. False, with
    /// one Warning per path per process, when the extension is no longer supported or the file
    /// is missing, has the wrong signature, fails the sanity check or is refused by Skia.
    /// </summary>
    public static bool TryGetCustom(string path, out SKTypeface typeface)
    {
        typeface = null!;
        if (!Extensions.Contains(Path.GetExtension(path)))
        {
            NoteUnusable(path);
            return false;
        }

        try
        {
            var writeTime = File.GetLastWriteTimeUtc(path);
            if (CustomFaces.TryGetValue(path, out var cached) && cached.WriteTimeUtc == writeTime)
            {
                typeface = cached.Typeface;
                return true;
            }

            if (BrokenFiles.TryGetValue(path, out var brokenAt) && brokenAt == writeTime)
            {
                return false;
            }

            lock (LoadLock)
            {
                if (CustomFaces.TryGetValue(path, out cached) && cached.WriteTimeUtc == writeTime)
                {
                    typeface = cached.Typeface;
                    return true;
                }

                // A missing file is remembered like a broken one: its write time reads as the
                // 1601 sentinel until it reappears, so later calls stop at the check above
                // instead of throwing and catching FileNotFoundException on every render.
                var loaded = LoadCustom(path);
                if (loaded is null)
                {
                    BrokenFiles[path] = writeTime;
                    NoteUnreadable(path);
                    return false;
                }

                BrokenFiles.TryRemove(path, out _);
                CustomFaces[path] = (writeTime, loaded);
                typeface = loaded;
                return true;
            }
        }
        catch (Exception)
        {
            // Permissions, a sharing violation, a native loader that threw: not remembered, as
            // these can be transient, but warned about once like any unreadable font.
            NoteUnreadable(path);
            return false;
        }
    }

    /// <summary>True when the current version of the file at <paramref name="path"/> is cached.</summary>
    public static bool IsCustomFontCached(string path)
        => CustomFaces.TryGetValue(path, out var cached) && cached.WriteTimeUtc == File.GetLastWriteTimeUtc(path);

    /// <summary>
    /// Logs, once per path per process, that a font is not being used because of its format. A path
    /// whose extension is not supported reports the upper-cased extension as its format, a path
    /// without one "unreadable".
    /// </summary>
    internal static void NoteUnusable(string path)
    {
        if (!Warned.TryAdd(path, 0))
        {
            return;
        }

        var extension = Path.GetExtension(path);
        var format = Extensions.Contains(extension) || extension.Length == 0
            ? "unreadable"
            : extension.TrimStart('.').ToUpperInvariant();
        RenderLog.Logger.LogWarning(
            "Custom font {Name} is in a format this version no longer reads ({Format}); rendering with Noto Sans. Re-upload it as TTF or OTF.",
            Path.GetFileName(path),
            format);
    }

    /// <summary>
    /// Logs, once per path per process, that a font with a supported extension is missing, damaged
    /// or refused, so the user knows to upload it again rather than convert it.
    /// </summary>
    private static void NoteUnreadable(string path)
    {
        if (!Warned.TryAdd(path, 0))
        {
            return;
        }

        RenderLog.Logger.LogWarning(
            "Custom font {Name} could not be read; rendering with Noto Sans. Re-upload it.",
            Path.GetFileName(path));
    }

    internal static void ResetWarningsForTests() => Warned.Clear();

    /// <summary>Routes this store's warnings on the calling flow to <paramref name="logger"/>, until disposed.</summary>
    internal static IDisposable CaptureLogForTests(ILogger logger) => RenderLog.Use(logger);

    private static SKTypeface LoadBundled(string faceName)
    {
        var resource = $"CustomCoverArt.Resources.fonts.{faceName}.ttf";
        using var stream = typeof(FontStore).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Bundled font not found: {resource}");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        using var data = SKData.CreateCopy(buffer.ToArray());
        return SKTypeface.FromData(data, 0)
            ?? throw new InvalidOperationException($"Bundled font could not be loaded: {resource}");
    }

    private static SKTypeface? LoadCustom(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        if (!IsSupportedSignature(bytes) || !SfntSanity.LooksValid(bytes))
        {
            return null;
        }

        using var data = SKData.CreateCopy(bytes);
        return SKTypeface.FromData(data, 0);
    }
}
