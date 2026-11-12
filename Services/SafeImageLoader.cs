using CustomCoverArt.Services.Rendering;
using SkiaSharp;

namespace CustomCoverArt.Services;

/// <summary>
/// The one place the plugin decodes an image it did not create (uploads, posters, logos).
/// <list type="number">
/// <item>The first bytes must carry one of five signatures (<see cref="ImageSignature"/>):
/// PNG, JPEG, GIF, BMP or WebP. Nothing else ever reaches a Skia codec, whatever its name.</item>
/// <item><see cref="SKCodec"/> then reads the header only (no pixel buffer), and its
/// <c>EncodedFormat</c> must agree with the signature.</item>
/// <item>An image whose single frame exceeds <see cref="MaxPixelBudget"/> is refused, and the
/// number of decoded frames is clamped so frames × width × height stays within it. A
/// 2000-frame GIF passes a W×H-only check but would decode every frame.</item>
/// <item>Frames decode into RGBA8888 premultiplied bitmaps with no colour space, so no
/// embedded ICC profile is applied or kept, and EXIF orientation and other metadata are
/// never read.</item>
/// </list>
/// Total by design: a missing, corrupt, truncated or oversized file yields <c>null</c>, never an exception.
/// </summary>
public static class SafeImageLoader
{
    /// <summary>Most pixels, summed over decoded frames, that one image may decode to (~268 MB as RGBA8888).</summary>
    public const long MaxPixelBudget = 8192L * 8192L;

    /// <summary>Most frames ever decoded from an animated source (the GIF passthrough cap).</summary>
    public const int MaxAnimatedFrames = 60;

    /// <summary>Frames the file declares, at least one.</summary>
    public static int FrameCount(ImageProbe probe) => Math.Max(1, probe.FrameCount);

    /// <summary>
    /// Whether the image can be decoded at all: it has a size and a single frame fits
    /// <paramref name="pixelBudget"/>. Long animations are not refused; they are clamped
    /// to fewer frames by <see cref="FramesToDecode"/>. <paramref name="maxFrames"/> is
    /// accepted for call-site clarity and does not affect the result.
    /// </summary>
    public static bool IsWithinBudget(ImageProbe probe, int maxFrames, long pixelBudget = MaxPixelBudget)
        => probe.Width > 0 && probe.Height > 0 && (long)probe.Width * probe.Height <= pixelBudget;

    /// <summary>
    /// Frames to decode: min(requested, declared, budget / pixels-per-frame), at least 1.
    /// Division keeps the arithmetic overflow-safe. Only meaningful when
    /// <see cref="IsWithinBudget"/> holds.
    /// </summary>
    public static int FramesToDecode(ImageProbe probe, int maxFrames, long pixelBudget = MaxPixelBudget)
    {
        var perFrame = Math.Max(1L, (long)probe.Width * probe.Height);
        var affordable = pixelBudget / perFrame;
        var requested = (long)Math.Max(1, maxFrames);
        var frames = Math.Min(Math.Min(FrameCount(probe), requested), affordable);
        return (int)Math.Max(1L, frames);
    }

    /// <summary>Header-only inspection of a file; null when it is missing or not a supported image.</summary>
    public static ImageProbe? TryProbe(string path) => TryProbe(TryReadFile(path));

    /// <summary>Header-only inspection of in-memory bytes (uploads); null when not a supported image.</summary>
    public static ImageProbe? TryProbe(byte[]? bytes)
    {
        try
        {
            using var opened = Open(bytes);
            return opened?.Probe;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Proves the bytes are an image by decoding their first frame, which must succeed
    /// completely. A valid header followed by junk opens a codec but fails here. False for
    /// anything over <see cref="MaxPixelBudget"/>, so this never allocates more than one budget.
    /// </summary>
    public static bool CanDecodeFirstFrame(byte[]? bytes)
    {
        try
        {
            using var decoded = TryDecode(bytes, maxFrames: 1);
            return decoded is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Decodes up to <paramref name="maxFrames"/> frames (fewer when the budget or the file says
    /// so), or null. A first frame that does not decode completely gives null; a later frame
    /// that fails ends the list there.
    /// </summary>
    public static DecodedImage? TryDecode(string path, int maxFrames, long pixelBudget = MaxPixelBudget)
        => TryDecode(TryReadFile(path), maxFrames, pixelBudget);

    /// <summary>As <see cref="TryDecode(string, int, long)"/> for bytes already in memory.</summary>
    internal static DecodedImage? TryDecode(byte[]? bytes, int maxFrames, long pixelBudget = MaxPixelBudget)
    {
        try
        {
            using var opened = Open(bytes);
            if (opened is null || !IsWithinBudget(opened.Probe, maxFrames, pixelBudget))
            {
                return null;
            }

            return DecodeFrames(opened, FramesToDecode(opened.Probe, maxFrames, pixelBudget));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the file into managed memory. Not <see cref="SKData.Create(string)"/>: that maps the
    /// file, and a library poster truncated by another process while mapped would fault the whole
    /// server instead of failing one decode.
    /// </summary>
    private static byte[]? TryReadFile(string path)
    {
        try
        {
            return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : File.ReadAllBytes(path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Signature check, header-only codec, format agreement. Null when any step refuses.</summary>
    private static OpenedCodec? Open(byte[]? bytes)
    {
        if (bytes is null || ImageSignature.Detect(bytes) is not { } kind)
        {
            return null;
        }

        SKData? data = null;
        SKCodec? codec = null;
        try
        {
            data = SKData.CreateCopy(bytes);
            codec = data is null ? null : SKCodec.Create(data);
            if (data is null || codec is null || !ImageSignature.Matches(kind, codec.EncodedFormat))
            {
                return null;
            }

            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0)
            {
                return null;
            }

            var opened = new OpenedCodec(data, codec, new ImageProbe(kind, info.Width, info.Height, Math.Max(1, codec.FrameCount)));
            data = null;
            codec = null;
            return opened;
        }
        finally
        {
            codec?.Dispose();
            data?.Dispose();
        }
    }

    /// <summary>
    /// Decodes frames 0..count-1 in order. A frame that depends on an earlier one (GIF/WebP
    /// disposal) starts as a copy of that frame, as <see cref="SKCodecOptions"/> requires.
    /// Every bitmap that is not handed out is disposed.
    /// </summary>
    private static DecodedImage? DecodeFrames(OpenedCodec opened, int count)
    {
        var (codec, probe) = (opened.Codec, opened.Probe);
        var frameInfo = codec.FrameInfo;
        var target = new SKImageInfo(probe.Width, probe.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var frames = new List<SKBitmap>(count);
        var durations = new List<int>(count);
        DecodedImage? result = null;
        try
        {
            for (var i = 0; i < count; i++)
            {
                var required = i < frameInfo.Length ? frameInfo[i].RequiredFrame : -1;
                if (i > 0 && i >= frameInfo.Length)
                {
                    break; // the codec reported fewer frame records than frames
                }

                if (required >= frames.Count)
                {
                    break; // depends on a frame that did not decode
                }

                var bitmap = RenderSurface.New(probe.Width, probe.Height);
                try
                {
                    if (required >= 0)
                    {
                        frames[required].GetPixelSpan().CopyTo(bitmap.GetPixelSpan());
                    }

                    var status = codec.GetPixels(target, bitmap.GetPixels(), bitmap.RowBytes, new SKCodecOptions(i, required));
                    if (status != SKCodecResult.Success)
                    {
                        bitmap.Dispose();
                        if (i == 0)
                        {
                            return null;
                        }

                        break;
                    }

                    frames.Add(bitmap);
                }
                catch
                {
                    bitmap.Dispose();
                    throw;
                }

                durations.Add(i < frameInfo.Length ? Math.Max(0, frameInfo[i].Duration) : 0);
            }

            if (frames.Count == 0)
            {
                return null;
            }

            result = new DecodedImage(probe.Kind, probe.Width, probe.Height, frames.ToArray(), durations.ToArray(), codec.RepetitionCount);
            return result;
        }
        finally
        {
            if (result is null)
            {
                foreach (var frame in frames)
                {
                    frame.Dispose();
                }
            }
        }
    }

    /// <summary>An open codec, the data it reads from, and what its header said.</summary>
    private sealed class OpenedCodec : IDisposable
    {
        public OpenedCodec(SKData data, SKCodec codec, ImageProbe probe)
        {
            Data = data;
            Codec = codec;
            Probe = probe;
        }

        public SKData Data { get; }

        public SKCodec Codec { get; }

        public ImageProbe Probe { get; }

        public void Dispose()
        {
            Codec.Dispose();
            Data.Dispose();
        }
    }
}
