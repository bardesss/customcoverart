using System.Collections.Generic;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CustomCoverArt.Services;

/// <summary>
/// Builds a full-bleed grid mosaic from a set of poster image files.
/// Pure image work — no Jellyfin API calls. Never throws on bad/missing files.
/// </summary>
public static class CollageComposer
{
    public static int ColumnsFor(string density) => (density ?? "medium").ToLowerInvariant() switch
    {
        "sparse" => 4,
        "dense" => 8,
        _ => 6,
    };

    public static Image<Rgba32> BuildCollage(
        IReadOnlyList<string> posterPaths, int width, int height, string density, int seed)
    {
        var canvas = new Image<Rgba32>(width, height, Color.FromRgb(18, 18, 18));
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
                    // Header-only check first, so a decompression-bomb poster can't
                    // exhaust memory during the full decode below.
                    var info = Image.Identify(path);
                    if ((long)info.Width * info.Height > 8192L * 8192L)
                    {
                        continue;
                    }

                    using var poster = Image.Load<Rgba32>(path);
                    poster.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Size = new Size(tileW, tileH),
                        Mode = ResizeMode.Crop, // cover-crop into the tile
                    }));
                    canvas.Mutate(x => x.DrawImage(poster, new Point(c * tileW, r * tileH), 1f));
                }
                catch
                {
                    // Skip unreadable poster; leave the dark canvas showing through.
                }
            }
        }

        return canvas;
    }
}
