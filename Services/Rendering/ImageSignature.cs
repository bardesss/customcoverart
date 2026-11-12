using SkiaSharp;

namespace CustomCoverArt.Services.Rendering;

/// <summary>The only image formats the plugin ever decodes (the formats uploads accept).</summary>
public enum ImageKind
{
    /// <summary>PNG (an APNG decodes as its default image).</summary>
    Png,

    /// <summary>JPEG.</summary>
    Jpeg,

    /// <summary>GIF87a or GIF89a, still or animated.</summary>
    Gif,

    /// <summary>Windows bitmap.</summary>
    Bmp,

    /// <summary>WebP in a RIFF container, still or animated.</summary>
    Webp,
}

/// <summary>
/// Magic-byte allow-list that runs before any Skia codec sees untrusted bytes. Skia would also
/// open ICO, WBMP, HEIF, AVIF and RAW files, and the plugin never meant to run those codecs, so
/// anything that does not start with one of the five signatures below is refused by content,
/// whatever its file name. After the codec opens, its <c>EncodedFormat</c> must still agree
/// (<see cref="Matches"/>).
/// </summary>
internal static class ImageSignature
{
    /// <summary>Bytes of the file head that <see cref="Detect"/> needs to recognise every kind (WebP: <c>RIFF....WEBP</c>).</summary>
    public const int HeadLength = 12;

    private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    /// <summary>The five decodable kinds.</summary>
    public static IReadOnlyList<ImageKind> Allowed { get; } =
        Array.AsReadOnly(new[] { ImageKind.Png, ImageKind.Jpeg, ImageKind.Gif, ImageKind.Bmp, ImageKind.Webp });

    /// <summary>The kind whose signature <paramref name="head"/> starts with, or null when it matches none (or is too short).</summary>
    public static ImageKind? Detect(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(PngMagic))
        {
            return ImageKind.Png;
        }

        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
        {
            return ImageKind.Jpeg;
        }

        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8))
        {
            return ImageKind.Gif;
        }

        if (head.StartsWith("BM"u8))
        {
            return ImageKind.Bmp;
        }

        if (head.Length >= HeadLength && head.StartsWith("RIFF"u8) && head.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return ImageKind.Webp;
        }

        return null;
    }

    /// <summary>Whether the codec Skia picked for the bytes is the one the signature promised.</summary>
    public static bool Matches(ImageKind kind, SKEncodedImageFormat format) => kind switch
    {
        ImageKind.Png => format == SKEncodedImageFormat.Png,
        ImageKind.Jpeg => format == SKEncodedImageFormat.Jpeg,
        ImageKind.Gif => format == SKEncodedImageFormat.Gif,
        ImageKind.Bmp => format == SKEncodedImageFormat.Bmp,
        ImageKind.Webp => format == SKEncodedImageFormat.Webp,
        _ => false,
    };
}
