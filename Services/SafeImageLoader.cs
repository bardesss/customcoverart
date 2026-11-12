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

    /// <summary>
    /// Largest encoded file the loader reads, far above any real poster or upload (uploads are
    /// capped at a few MB). Library poster paths are not upload-capped, so without this a huge
    /// file would be read in full before the codec refused it.
    /// </summary>
    internal const long MaxEncodedFileBytes = 64L * 1024 * 1024;

    /// <summary>Header-only inspection of a file; null when it is missing or not a supported image.</summary>
    public static ImageProbe? TryProbe(string path)
    {
        try
        {
            using var opened = OpenFile(path);
            return opened?.Probe;
        }
        catch
        {
            return null;
        }
    }

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
    public static bool CanDecodeFirstFrame(byte[]? bytes) => ProbeAndDecodeFirstFrame(bytes).FirstFrameDecodes;

    /// <summary>
    /// The upload check in one pass over one codec: the probe (null when the bytes are not a
    /// supported image), and whether frame 0 decodes completely. Frame 0 is decoded only when a
    /// single frame fits <paramref name="pixelBudget"/>; otherwise the second value is false.
    /// </summary>
    internal static (ImageProbe? Probe, bool FirstFrameDecodes) ProbeAndDecodeFirstFrame(byte[]? bytes, long pixelBudget = MaxPixelBudget)
    {
        try
        {
            using var opened = Open(bytes);
            if (opened is null)
            {
                return (null, false);
            }

            if (!IsWithinBudget(opened.Probe, maxFrames: 1, pixelBudget))
            {
                return (opened.Probe, false);
            }

            using var decoded = DecodeFrames(opened, 1);
            return (opened.Probe, decoded is not null);
        }
        catch
        {
            return (null, false);
        }
    }

    /// <summary>
    /// Decodes up to <paramref name="maxFrames"/> frames (fewer when the budget or the file says
    /// so), or null. A first frame that does not decode completely gives null; a later frame
    /// that fails ends the list there.
    /// </summary>
    public static DecodedImage? TryDecode(string path, int maxFrames, long pixelBudget = MaxPixelBudget)
    {
        try
        {
            using var opened = OpenFile(path);
            return Decode(opened, maxFrames, pixelBudget);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>As <see cref="TryDecode(string, int, long)"/> for bytes already in memory.</summary>
    internal static DecodedImage? TryDecode(byte[]? bytes, int maxFrames, long pixelBudget = MaxPixelBudget)
    {
        try
        {
            using var opened = Open(bytes);
            return Decode(opened, maxFrames, pixelBudget);
        }
        catch
        {
            return null;
        }
    }

    private static DecodedImage? Decode(OpenedCodec? opened, int maxFrames, long pixelBudget)
        => opened is null || !IsWithinBudget(opened.Probe, maxFrames, pixelBudget)
            ? null
            : DecodeFrames(opened, FramesToDecode(opened.Probe, maxFrames, pixelBudget));

    /// <summary>
    /// Reads an encoded image file into native memory, but only after the cheap checks pass: the
    /// file is at most <paramref name="maxBytes"/> long and its first bytes carry an allowed
    /// signature. Nothing past the 12-byte head is read for a file that fails them. The body is
    /// read once, straight into the <see cref="SKData"/> the codec uses. Not
    /// <see cref="SKData.Create(string)"/>: that maps the file, and a library poster truncated by
    /// another process while mapped would fault the whole server instead of failing one decode.
    /// A file that shrinks while it is read gives null.
    /// </summary>
    internal static SKData? TryReadEncodedFile(string? path, out ImageKind kind, long maxBytes = MaxEncodedFileBytes)
    {
        kind = default;
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
        var length = stream.Length;
        if (length <= 0 || length > maxBytes)
        {
            return null;
        }

        var head = new byte[ImageSignature.HeadLength];
        var headRead = ReadUpTo(stream, head);
        if (ImageSignature.Detect(head.AsSpan(0, headRead)) is not { } detected)
        {
            return null;
        }

        var data = SKData.Create(length);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(head, 0, data.Data, headRead);
            var buffer = new byte[(int)Math.Min(81920L, length)];
            long offset = headRead;
            while (offset < length)
            {
                var n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length - offset));
                if (n <= 0)
                {
                    data.Dispose();
                    return null;
                }

                System.Runtime.InteropServices.Marshal.Copy(buffer, 0, data.Data + (nint)offset, n);
                offset += n;
            }

            kind = detected;
            return data;
        }
        catch
        {
            data.Dispose();
            throw;
        }
    }

    private static int ReadUpTo(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = stream.Read(buffer, total, buffer.Length - total);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    private static OpenedCodec? OpenFile(string? path)
    {
        var data = TryReadEncodedFile(path, out var kind);
        return data is null ? null : Open(data, kind);
    }

    /// <summary>In-memory bytes: signature and size cap first, then a single native copy for the codec.</summary>
    private static OpenedCodec? Open(byte[]? bytes)
    {
        if (bytes is null || bytes.LongLength > MaxEncodedFileBytes || ImageSignature.Detect(bytes) is not { } kind)
        {
            return null;
        }

        var data = SKData.CreateCopy(bytes);
        return data is null ? null : Open(data, kind);
    }

    /// <summary>
    /// Header-only codec over <paramref name="data"/> (which it takes ownership of) and format
    /// agreement with the sniffed <paramref name="kind"/>. Null when any step refuses.
    /// </summary>
    private static OpenedCodec? Open(SKData data, ImageKind kind)
    {
        SKData? owned = data;
        SKCodec? codec = null;
        try
        {
            codec = SKCodec.Create(owned);
            if (codec is null || !ImageSignature.Matches(kind, codec.EncodedFormat))
            {
                return null;
            }

            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0)
            {
                return null;
            }

            var opened = new OpenedCodec(owned, codec, new ImageProbe(kind, info.Width, info.Height, Math.Max(1, codec.FrameCount)));
            owned = null;
            codec = null;
            return opened;
        }
        finally
        {
            codec?.Dispose();
            owned?.Dispose();
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
