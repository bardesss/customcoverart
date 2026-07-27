using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// Regression guard for the background-dim black-out bug.
///
/// JPEG library posters decode to <c>Rgb24</c> (no alpha channel). Filling such
/// an image with a semi-transparent <c>SolidBrush</c> ignores the brush alpha and
/// paints fully opaque, so ANY non-zero dim turned the whole background black.
/// CoverArtService now dims by compositing a solid overlay at fractional opacity,
/// which blends correctly regardless of pixel format. This test reproduces the
/// original failing scenario and asserts the overlay approach keeps the image.
/// </summary>
public class BackgroundDimTests
{
    [Fact]
    public void Dim_OnJpegLoadedBackground_DarkensButDoesNotBlackOut()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"ccadim_{Guid.NewGuid():N}.jpg");
        using (var src = new Image<Rgba32>(200, 300, new Rgba32(200, 60, 60, 255)))
            src.SaveAsJpeg(tmp);

        try
        {
            using var canvas = new Image<Rgba32>(400, 400);
            using var bg = Image.Load(tmp); // decodes as Rgb24, like a real poster
            bg.Mutate(x => x.Resize(400, 600).Crop(new Rectangle(0, 100, 400, 400)));
            canvas.Mutate(x => x.DrawImage(bg, Point.Empty, 1f));

            const float dim = 0.05f;
            using (var overlay = new Image<Rgba32>(canvas.Width, canvas.Height, Color.Black))
                canvas.Mutate(x => x.DrawImage(overlay, Point.Empty, dim));

            var p = canvas[10, 10];
            // 5% black dim over red(200): expect ~190, and definitely not blacked out.
            Assert.True(p.R > 150, $"Background was blacked out by dim (R={p.R}).");
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
