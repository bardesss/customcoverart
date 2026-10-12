using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.PixelFormats;

namespace CustomCoverArt.Services;

/// <summary>
/// The one place the plugin decodes an image it did not create (uploads, posters, logos).
/// It identifies the file first (header and frame metadata only, no pixel buffers) and
/// refuses anything whose decoded size would exceed <see cref="MaxPixelBudget"/>, counting
/// frames as well as width × height. A 2000-frame GIF passes a W×H-only check but would
/// decode every frame. It then decodes with <see cref="DecoderOptions.MaxFrames"/>, so a
/// still cover never pays for an animation's other frames.
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
    /// Whether decoding at most <paramref name="maxFrames"/> frames stays within
    /// <paramref name="pixelBudget"/>. The per-frame product is checked on its own first,
    /// so the multiplication by the frame count can never overflow.
    /// </summary>
    public static bool IsWithinBudget(ImageInfo info, int maxFrames, long pixelBudget = MaxPixelBudget)
    {
        if (info.Width <= 0 || info.Height <= 0)
        {
            return false;
        }

        var perFrame = (long)info.Width * info.Height;
        if (perFrame > pixelBudget)
        {
            return false;
        }

        var frames = Math.Min(FrameCount(info), Math.Max(1, maxFrames));
        return perFrame * frames <= pixelBudget;
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
            return Image.Load(Options(maxFrames), path);
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
            return Image.Load<Rgba32>(Options(maxFrames), path);
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
