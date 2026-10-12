using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;

namespace CustomCoverArt.Services;

/// <summary>
/// The one place the plugin decodes an image it did not create (uploads, posters, logos).
/// It identifies the file first (header and frame metadata only, no pixel buffers) and
/// refuses an image whose single frame exceeds <see cref="MaxPixelBudget"/>, and clamps the
/// number of decoded frames so frames × width × height stays within it. A 2000-frame GIF
/// passes a W×H-only check but would decode every frame. It decodes with
/// <see cref="DecoderOptions.MaxFrames"/>, so a still cover never pays for an animation's
/// other frames, and a long animation is truncated rather than rejected.
/// Total by design: a missing, corrupt or oversized file yields <c>null</c>, never an exception.
/// </summary>
public static class SafeImageLoader
{
    /// <summary>Most pixels, summed over decoded frames, that one image may decode to (~268 MB as Rgba32).</summary>
    public const long MaxPixelBudget = 8192L * 8192L;

    /// <summary>Most frames ever decoded from an animated source (the GIF passthrough cap).</summary>
    public const int MaxAnimatedFrames = 60;

    /// <summary>Frames the file declares. Formats that report no frame metadata count as one.</summary>
    public static int FrameCount(ImageInfo info) => Math.Max(1, info.FrameMetadataCollection.Count);

    /// <summary>
    /// Whether the image can be decoded at all: it has a size and a single frame fits
    /// <paramref name="pixelBudget"/>. Long animations are not refused; they are clamped
    /// to fewer frames by <see cref="FramesToDecode"/>. <paramref name="maxFrames"/> is
    /// accepted for call-site clarity and does not affect the result.
    /// </summary>
    public static bool IsWithinBudget(ImageInfo info, int maxFrames, long pixelBudget = MaxPixelBudget)
        => info.Width > 0 && info.Height > 0 && (long)info.Width * info.Height <= pixelBudget;

    /// <summary>
    /// Frames to decode: min(requested, declared, budget / pixels-per-frame), at least 1.
    /// Division keeps the arithmetic overflow-safe. Only meaningful when
    /// <see cref="IsWithinBudget"/> holds.
    /// </summary>
    public static int FramesToDecode(ImageInfo info, int maxFrames, long pixelBudget = MaxPixelBudget)
    {
        var perFrame = Math.Max(1L, (long)info.Width * info.Height);
        var affordable = pixelBudget / perFrame;
        var frames = Math.Min(Math.Min(FrameCount(info), Math.Max(1, maxFrames)), affordable);
        return (int)Math.Max(1L, frames);
    }

    /// <summary>Header-only inspection; null when the file is missing or not a supported image.</summary>
    public static ImageInfo? TryIdentify(string path)
    {
        try
        {
            return File.Exists(path) ? Image.Identify(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Decodes in the file's native pixel format (JPEGs stay Rgb24), or null.</summary>
    public static Image? TryLoad(string path, int maxFrames, long pixelBudget = MaxPixelBudget)
    {
        var info = TryIdentify(path);
        if (info is null || !IsWithinBudget(info, maxFrames, pixelBudget))
        {
            return null;
        }

        try
        {
            return Image.Load(Options(FramesToDecode(info, maxFrames, pixelBudget)), path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Decodes as Rgba32 (for compositing sources such as logos and poster tiles), or null.</summary>
    public static Image<Rgba32>? TryLoadRgba32(string path, int maxFrames, long pixelBudget = MaxPixelBudget)
    {
        var info = TryIdentify(path);
        if (info is null || !IsWithinBudget(info, maxFrames, pixelBudget))
        {
            return null;
        }

        try
        {
            return Image.Load<Rgba32>(Options(FramesToDecode(info, maxFrames, pixelBudget)), path);
        }
        catch
        {
            return null;
        }
    }

    private static DecoderOptions Options(int maxFrames) => new()
    {
        MaxFrames = (uint)Math.Max(1, maxFrames),
    };
}
