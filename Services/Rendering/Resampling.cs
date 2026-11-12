using SkiaSharp;

namespace CustomCoverArt.Services.Rendering;

/// <summary>How a bitmap is sampled when it is drawn at another size (spec S5 row 8).</summary>
internal static class Resampling
{
    /// <summary>
    /// Cubic Mitchell when either axis enlarges (smooth, no ringing to speak of); otherwise linear
    /// filtering with linear mipmaps, so a large shrink averages the source instead of skipping pixels.
    /// </summary>
    public static SKSamplingOptions For(int srcW, int srcH, int dstW, int dstH)
        => dstW > srcW || dstH > srcH
            ? new SKSamplingOptions(SKCubicResampler.Mitchell)
            : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);
}
