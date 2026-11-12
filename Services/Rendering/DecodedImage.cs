using SkiaSharp;

namespace CustomCoverArt.Services.Rendering;

/// <summary>Header facts of an image, read without decoding any pixels.</summary>
/// <param name="Kind">The format, as sniffed and confirmed by the codec.</param>
/// <param name="Width">Width in pixels (for GIF, the logical screen).</param>
/// <param name="Height">Height in pixels (for GIF, the logical screen).</param>
/// <param name="FrameCount">Frames the file declares; at least 1. An APNG counts as 1 (Skia decodes its default image only).</param>
public sealed record ImageProbe(ImageKind Kind, int Width, int Height, int FrameCount);

/// <summary>
/// Decoded frames of an image: each an RGBA8888 premultiplied bitmap of the full canvas size
/// with no colour space, already composited by the codec (GIF/WebP disposal and blending).
/// Owns its frames; disposing it disposes them.
/// </summary>
public sealed class DecodedImage : IDisposable
{
    private readonly SKBitmap[] _frames;
    private bool _disposed;

    internal DecodedImage(ImageKind kind, int width, int height, SKBitmap[] frames, int[] frameDurationsMs, int repetitionCount)
    {
        if (frames.Length == 0 || frames.Length != frameDurationsMs.Length)
        {
            throw new ArgumentException("Need at least one frame and one duration per frame.", nameof(frames));
        }

        Kind = kind;
        Width = width;
        Height = height;
        _frames = frames;
        Frames = Array.AsReadOnly(frames);
        FrameDurationsMs = Array.AsReadOnly(frameDurationsMs);
        RepetitionCount = repetitionCount;
    }

    /// <summary>The source format.</summary>
    public ImageKind Kind { get; }

    /// <summary>Width of every frame.</summary>
    public int Width { get; }

    /// <summary>Height of every frame.</summary>
    public int Height { get; }

    /// <summary>The decoded frames, first frame first.</summary>
    public IReadOnlyList<SKBitmap> Frames { get; }

    /// <summary>Per-frame display time in milliseconds as the codec reports it (0 for a still).</summary>
    public IReadOnlyList<int> FrameDurationsMs { get; }

    /// <summary>Skia's repetition count: -1 loops forever, 0 plays once, n repeats n more times.</summary>
    public int RepetitionCount { get; }

    /// <summary>The first frame.</summary>
    public SKBitmap RootFrame => Frames[0];

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var frame in _frames)
        {
            frame.Dispose();
        }
    }
}
