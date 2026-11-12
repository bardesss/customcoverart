using SkiaSharp;

namespace CustomCoverArt.Services.Rendering;

internal static class RenderSurface
{
    /// <summary>A transparent RGBA8888 premultiplied bitmap with no colour space (no colour management).</summary>
    public static SKBitmap New(int width, int height) => New(width, height, SKColors.Transparent);

    /// <summary>An RGBA8888 premultiplied bitmap with no colour space, cleared to <paramref name="fill"/>.</summary>
    public static SKBitmap New(int width, int height, SKColor fill)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        bitmap.Erase(fill);
        return bitmap;
    }
}
