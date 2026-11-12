using System.Collections.Generic;
using CustomCoverArt.Services.Rendering;
using SkiaSharp;

namespace CustomCoverArt.Services;

/// <summary>
/// Builds a full-bleed grid mosaic from a set of poster image files.
/// Pure image work with no Jellyfin API calls. Never throws on bad/missing files.
/// </summary>
public static class CollageComposer
{
    public static int ColumnsFor(string density) => (density ?? "medium").ToLowerInvariant() switch
    {
        "sparse" => 4,
        "dense" => 8,
        _ => 6,
    };

    public static SKBitmap BuildCollage(
        IReadOnlyList<string> posterPaths, int width, int height, string density, int seed)
    {
        var canvas = RenderSurface.New(width, height, new SKColor(18, 18, 18));
        if (posterPaths is null || posterPaths.Count == 0)
        {
            return canvas;
        }

        var cols = ColumnsFor(density);
        var tileW = (int)System.Math.Ceiling(width / (double)cols);
        var tileH = (int)System.Math.Ceiling(tileW * 3.0 / 2.0); // 2:3 poster aspect
        var rows = (int)System.Math.Ceiling(height / (double)tileH);

        // Deterministic shuffle by seed.
        var order = new List<int>();
        for (int i = 0; i < posterPaths.Count; i++) order.Add(i);
        var rng = new System.Random(seed);
        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        try
        {
            using var target = new SKCanvas(canvas);
            using var paint = new SKPaint { IsAntialias = true };
            int tileIndex = 0;
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    // Repeat posters to fill the grid.
                    var path = posterPaths[order[tileIndex % order.Count]];
                    tileIndex++;
                    try
                    {
                        // Shared decompression-bomb guard; a tile is a still, so one frame.
                        using var poster = SafeImageLoader.TryDecode(path, maxFrames: 1);
                        if (poster is null)
                        {
                            continue;
                        }

                        DrawCoverCropped(target, paint, poster.RootFrame, c * tileW, r * tileH, tileW, tileH);
                    }
                    catch
                    {
                        // Skip unreadable poster; leave the dark canvas showing through.
                    }
                }
            }
        }
        catch
        {
            canvas.Dispose();
            throw;
        }

        return canvas;
    }

    /// <summary>
    /// Draws <paramref name="poster"/> into the tile at (<paramref name="x"/>, <paramref name="y"/>): the
    /// centred source window with the tile's aspect, scaled to fill it (cover-crop).
    /// </summary>
    private static void DrawCoverCropped(SKCanvas target, SKPaint paint, SKBitmap poster, int x, int y, int tileW, int tileH)
    {
        var scale = System.Math.Max(tileW / (double)poster.Width, tileH / (double)poster.Height);
        var cropW = System.Math.Min(poster.Width, tileW / scale);
        var cropH = System.Math.Min(poster.Height, tileH / scale);
        var src = SKRect.Create((float)((poster.Width - cropW) / 2), (float)((poster.Height - cropH) / 2), (float)cropW, (float)cropH);
        var dst = SKRect.Create(x, y, tileW, tileH);

        using var pixmap = poster.PeekPixels();
        if (pixmap is null)
        {
            return;
        }

        using var image = SKImage.FromPixels(pixmap);
        var sampling = Resampling.For((int)System.Math.Round(cropW), (int)System.Math.Round(cropH), tileW, tileH);
        target.DrawImage(image, src, dst, sampling, paint);
    }
}
