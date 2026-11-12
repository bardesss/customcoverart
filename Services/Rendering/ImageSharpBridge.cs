using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;

namespace CustomCoverArt.Services.Rendering;

/// <summary>
/// Temporary pixel bridge between ImageSharp and SkiaSharp while the renderers migrate
/// one by one. Deleted when the last ImageSharp call site is gone.
/// </summary>
internal static class ImageSharpBridge
{
    /// <summary>Copies the first frame of any ImageSharp image into a premultiplied RGBA8888 bitmap.</summary>
    public static SKBitmap ToSkia(Image image)
    {
        using var rgba = image.CloneAs<Rgba32>();
        var bitmap = RenderSurface.New(rgba.Width, rgba.Height);
        try
        {
            CopyIn(rgba, bitmap);
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>Copies a bitmap into a new straight-alpha ImageSharp image.</summary>
    public static Image<Rgba32> ToImageSharp(SKBitmap bitmap)
    {
        var image = new Image<Rgba32>(bitmap.Width, bitmap.Height);
        try
        {
            CopyOut(bitmap, image);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Copies every decoded frame into one straight-alpha ImageSharp image. For a GIF source each
    /// frame also gets its <see cref="GifFrameMetadata.FrameDelay"/> (centiseconds) from the
    /// codec's duration, so the animated passthrough keeps GIF timing; other sources (animated
    /// WebP) leave it 0 and the document's delay applies, as with ImageSharp's own decoder (spec 6.2).
    /// </summary>
    public static Image<Rgba32> ToImageSharp(DecodedImage decoded)
    {
        ArgumentNullException.ThrowIfNull(decoded);
        var image = ToImageSharp(decoded.RootFrame);
        try
        {
            SetGifDelay(decoded, 0, image.Frames.RootFrame);
            for (var i = 1; i < decoded.Frames.Count; i++)
            {
                using var frame = ToImageSharp(decoded.Frames[i]);
                var added = image.Frames.AddFrame(frame.Frames.RootFrame);
                SetGifDelay(decoded, i, added);
            }

            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The first frame of an untrusted image through <see cref="SafeImageLoader"/>'s guard, as an
    /// ImageSharp image (logos, poster tiles), or null when the guard refuses it.
    /// </summary>
    public static Image<Rgba32>? TryLoadStill(string path)
    {
        using var decoded = SafeImageLoader.TryDecode(path, maxFrames: 1);
        return decoded is null ? null : ToImageSharp(decoded.RootFrame);
    }

    /// <summary>Every decoded frame of an untrusted image (see <see cref="ToImageSharp(DecodedImage)"/>), or null when the guard refuses it.</summary>
    public static Image<Rgba32>? TryLoad(string path, int maxFrames)
    {
        using var decoded = SafeImageLoader.TryDecode(path, maxFrames);
        return decoded is null ? null : ToImageSharp(decoded);
    }

    private static void SetGifDelay(DecodedImage decoded, int index, ImageFrame frame)
    {
        if (decoded.Kind == ImageKind.Gif)
        {
            frame.Metadata.GetGifMetadata().FrameDelay = decoded.FrameDurationsMs[index] / 10;
        }
    }

    /// <summary>Copies <paramref name="canvas"/> into a bitmap, runs <paramref name="draw"/> on it, and copies the result back.</summary>
    public static void DrawOnto(Image<Rgba32> canvas, Action<SKBitmap> draw)
    {
        using var bitmap = ToSkia(canvas);
        draw(bitmap);
        CopyOut(bitmap, canvas);
    }

    private static void CopyIn(Image<Rgba32> src, SKBitmap dst)
    {
        var w = src.Width;
        // Spans cannot be captured by the lambda, so stage through an array.
        var bytes = new byte[w * src.Height * 4];
        src.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var o = y * w * 4;
                for (var x = 0; x < w; x++)
                {
                    var p = row[x];
                    var a = p.A;
                    bytes[o] = Premul(p.R, a);
                    bytes[o + 1] = Premul(p.G, a);
                    bytes[o + 2] = Premul(p.B, a);
                    bytes[o + 3] = a;
                    o += 4;
                }
            }
        });
        bytes.CopyTo(dst.GetPixelSpan());
    }

    private static void CopyOut(SKBitmap src, Image<Rgba32> dst)
    {
        // The byte walk below assumes premultiplied RGBA8888, tightly packed, same size.
        if (src.ColorType != SKColorType.Rgba8888 || src.AlphaType != SKAlphaType.Premul)
        {
            throw new ArgumentException($"Expected an Rgba8888/Premul bitmap, got {src.ColorType}/{src.AlphaType}.", nameof(src));
        }

        if (src.RowBytes != src.Width * 4 || src.Width != dst.Width || src.Height != dst.Height)
        {
            throw new ArgumentException($"Bitmap {src.Width}x{src.Height} (row bytes {src.RowBytes}) does not match a tightly packed {dst.Width}x{dst.Height} target.", nameof(src));
        }

        var w = dst.Width;
        var bytes = src.GetPixelSpan().ToArray();
        dst.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                var o = y * w * 4;
                for (var x = 0; x < w; x++)
                {
                    var a = bytes[o + 3];
                    row[x] = new Rgba32(Unpremul(bytes[o], a), Unpremul(bytes[o + 1], a), Unpremul(bytes[o + 2], a), a);
                    o += 4;
                }
            }
        });
    }

    private static byte Premul(byte c, byte a)
        => a == 255 ? c : (byte)((c * a + 127) / 255);

    private static byte Unpremul(byte c, byte a)
    {
        if (a == 255)
        {
            return c;
        }

        if (a == 0)
        {
            return 0;
        }

        return (byte)Math.Min(255, (c * 255 + a / 2) / a);
    }
}
