using SkiaSharp;

namespace CustomCoverArt.Services.Rendering;

/// <summary>
/// One global GIF palette for all frames: the sampled colours as is when they fit, otherwise
/// Xiaolin Wu's variance-minimising quantizer (Graphics Gems II, 1991) over 5-bit channels.
/// </summary>
internal static class WuQuantizer
{
    private const int MaxSamples = 1_048_576;
    private const int Side = 33;

    public static GifPalette Build(IReadOnlyList<SKBitmap> frames, int maxColors = 256)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxColors, 2);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxColors, 256);

        var m = new Moments();
        var distinct = new HashSet<int>();
        var tooMany = false;
        var needsTransparent = false;
        long total = 0;
        foreach (var frame in frames)
        {
            total += (long)frame.Width * frame.Height;
        }

        var stride = Math.Max(1, (total + MaxSamples - 1) / MaxSamples);
        long offset = 0; // flattened index of the frame's first pixel
        foreach (var frame in frames)
        {
            var pixels = frame.GetPixelSpan();
            long count = (long)frame.Width * frame.Height;
            var first = (stride - (offset % stride)) % stride;
            for (var i = first; i < count; i += stride)
            {
                var o = (int)((i / frame.Width * frame.RowBytes) + (i % frame.Width * 4));
                var (r, g, b, a) = Pixels.Unpremultiply(pixels, o);
                if (a < 128)
                {
                    needsTransparent = true;
                    continue;
                }

                m.Add(r, g, b);
                if (!tooMany && distinct.Add((r << 16) | (g << 8) | b) && distinct.Count > maxColors)
                {
                    tooMany = true;
                }
            }

            offset += count;
        }

        var opaqueSlots = needsTransparent ? maxColors - 1 : maxColors;
        List<SKColor> colors;
        if (!tooMany && distinct.Count <= opaqueSlots)
        {
            colors = distinct.Order().Select(p => new SKColor((byte)(p >> 16), (byte)(p >> 8), (byte)p)).ToList();
        }
        else
        {
            colors = m.Quantize(opaqueSlots);
        }

        if (colors.Count == 0)
        {
            colors.Add(SKColors.Black); // nothing opaque sampled: keep one colour for unsampled opaque pixels
        }

        var transparentIndex = -1;
        if (needsTransparent)
        {
            transparentIndex = colors.Count;
            colors.Add(SKColors.Transparent);
        }

        return new GifPalette(colors, transparentIndex);
    }

    private static int I(int r, int g, int b) => (((r * Side) + g) * Side) + b;

    private struct Box
    {
        public int R0, R1, G0, G1, B0, B1;

        public readonly int Volume => (R1 - R0) * (G1 - G0) * (B1 - B0);
    }

    private sealed class Moments
    {
        private readonly long[] _w = new long[Side * Side * Side];
        private readonly long[] _r = new long[Side * Side * Side];
        private readonly long[] _g = new long[Side * Side * Side];
        private readonly long[] _b = new long[Side * Side * Side];
        private readonly long[] _m2 = new long[Side * Side * Side];

        public void Add(int r, int g, int b)
        {
            var i = I((r >> 3) + 1, (g >> 3) + 1, (b >> 3) + 1);
            _w[i]++;
            _r[i] += r;
            _g[i] += g;
            _b[i] += b;
            _m2[i] += (r * r) + (g * g) + (b * b);
        }

        public List<SKColor> Quantize(int maxColors)
        {
            foreach (var m in new[] { _w, _r, _g, _b, _m2 })
            {
                Accumulate(m);
            }

            var boxes = new Box[maxColors];
            var variance = new double[maxColors];
            boxes[0] = new Box { R1 = 32, G1 = 32, B1 = 32 };
            var count = maxColors;
            var next = 0;
            for (var i = 1; i < maxColors; i++)
            {
                if (Cut(ref boxes[next], ref boxes[i]))
                {
                    variance[next] = boxes[next].Volume > 1 ? Variance(boxes[next]) : 0;
                    variance[i] = boxes[i].Volume > 1 ? Variance(boxes[i]) : 0;
                }
                else
                {
                    variance[next] = 0;
                    i--;
                }

                next = 0;
                var best = variance[0];
                for (var k = 1; k <= i; k++)
                {
                    if (variance[k] > best)
                    {
                        best = variance[k];
                        next = k;
                    }
                }

                if (best <= 0)
                {
                    count = i + 1;
                    break;
                }
            }

            var colors = new List<SKColor>(count);
            for (var i = 0; i < count; i++)
            {
                var w = Vol(boxes[i], _w);
                if (w > 0)
                {
                    colors.Add(new SKColor(Mean(_r), Mean(_g), Mean(_b)));
                }

                byte Mean(long[] m) => (byte)((Vol(boxes[i], m) + (w / 2)) / w);
            }

            return colors;
        }

        /// <summary>Turns per-cell sums into cumulative sums over [0..r, 0..g, 0..b].</summary>
        private static void Accumulate(long[] m)
        {
            var area = new long[Side];
            for (var r = 1; r < Side; r++)
            {
                Array.Clear(area);
                for (var g = 1; g < Side; g++)
                {
                    long line = 0;
                    for (var b = 1; b < Side; b++)
                    {
                        line += m[I(r, g, b)];
                        area[b] += line;
                        m[I(r, g, b)] = m[I(r - 1, g, b)] + area[b];
                    }
                }
            }
        }

        private static long Vol(in Box c, long[] m)
            => m[I(c.R1, c.G1, c.B1)] - m[I(c.R1, c.G1, c.B0)] - m[I(c.R1, c.G0, c.B1)] + m[I(c.R1, c.G0, c.B0)]
             - m[I(c.R0, c.G1, c.B1)] + m[I(c.R0, c.G1, c.B0)] + m[I(c.R0, c.G0, c.B1)] - m[I(c.R0, c.G0, c.B0)];

        /// <summary>The part of <see cref="Vol"/> that does not depend on the cut position along <paramref name="dir"/>.</summary>
        private static long Bottom(in Box c, int dir, long[] m) => dir switch
        {
            0 => -m[I(c.R0, c.G1, c.B1)] + m[I(c.R0, c.G1, c.B0)] + m[I(c.R0, c.G0, c.B1)] - m[I(c.R0, c.G0, c.B0)],
            1 => -m[I(c.R1, c.G0, c.B1)] + m[I(c.R1, c.G0, c.B0)] + m[I(c.R0, c.G0, c.B1)] - m[I(c.R0, c.G0, c.B0)],
            _ => -m[I(c.R1, c.G1, c.B0)] + m[I(c.R1, c.G0, c.B0)] + m[I(c.R0, c.G1, c.B0)] - m[I(c.R0, c.G0, c.B0)],
        };

        private static long Top(in Box c, int dir, int pos, long[] m) => dir switch
        {
            0 => m[I(pos, c.G1, c.B1)] - m[I(pos, c.G1, c.B0)] - m[I(pos, c.G0, c.B1)] + m[I(pos, c.G0, c.B0)],
            1 => m[I(c.R1, pos, c.B1)] - m[I(c.R1, pos, c.B0)] - m[I(c.R0, pos, c.B1)] + m[I(c.R0, pos, c.B0)],
            _ => m[I(c.R1, c.G1, pos)] - m[I(c.R1, c.G0, pos)] - m[I(c.R0, c.G1, pos)] + m[I(c.R0, c.G0, pos)],
        };

        private double Variance(in Box c)
        {
            double r = Vol(c, _r), g = Vol(c, _g), b = Vol(c, _b);
            return Vol(c, _m2) - (((r * r) + (g * g) + (b * b)) / Vol(c, _w));
        }

        private double Maximize(in Box c, int dir, int first, int last, out int cut, long wholeR, long wholeG, long wholeB, long wholeW)
        {
            long baseR = Bottom(c, dir, _r), baseG = Bottom(c, dir, _g), baseB = Bottom(c, dir, _b), baseW = Bottom(c, dir, _w);
            var max = 0.0;
            cut = -1;
            for (var i = first; i < last; i++)
            {
                double halfR = baseR + Top(c, dir, i, _r), halfG = baseG + Top(c, dir, i, _g), halfB = baseB + Top(c, dir, i, _b);
                double halfW = baseW + Top(c, dir, i, _w);
                if (halfW == 0)
                {
                    continue;
                }

                var temp = ((halfR * halfR) + (halfG * halfG) + (halfB * halfB)) / halfW;
                halfR = wholeR - halfR;
                halfG = wholeG - halfG;
                halfB = wholeB - halfB;
                halfW = wholeW - halfW;
                if (halfW == 0)
                {
                    continue;
                }

                temp += ((halfR * halfR) + (halfG * halfG) + (halfB * halfB)) / halfW;
                if (temp > max)
                {
                    max = temp;
                    cut = i;
                }
            }

            return max;
        }

        private bool Cut(ref Box set1, ref Box set2)
        {
            long wr = Vol(set1, _r), wg = Vol(set1, _g), wb = Vol(set1, _b), ww = Vol(set1, _w);
            var maxR = Maximize(set1, 0, set1.R0 + 1, set1.R1, out var cutR, wr, wg, wb, ww);
            var maxG = Maximize(set1, 1, set1.G0 + 1, set1.G1, out var cutG, wr, wg, wb, ww);
            var maxB = Maximize(set1, 2, set1.B0 + 1, set1.B1, out var cutB, wr, wg, wb, ww);
            int dir;
            if (maxR >= maxG && maxR >= maxB)
            {
                if (cutR < 0)
                {
                    return false; // nothing left to split
                }

                dir = 0;
            }
            else
            {
                dir = maxG >= maxB ? 1 : 2;
            }

            set2.R1 = set1.R1;
            set2.G1 = set1.G1;
            set2.B1 = set1.B1;
            (set2.R0, set2.G0, set2.B0) = (set1.R0, set1.G0, set1.B0);
            switch (dir)
            {
                case 0:
                    set2.R0 = set1.R1 = cutR;
                    break;
                case 1:
                    set2.G0 = set1.G1 = cutG;
                    break;
                default:
                    set2.B0 = set1.B1 = cutB;
                    break;
            }

            return true;
        }
    }
}

/// <summary>A GIF colour table plus a nearest-colour lookup that never picks the transparent slot.</summary>
internal sealed class GifPalette
{
    private readonly SKColor[] _colors;
    private const int MemoBits = 16;
    private readonly int[]?[] _cells = new int[]?[32 * 32 * 32];
    private readonly int[] _memoKeys = new int[1 << MemoBits];
    private readonly byte[] _memoValues = new byte[1 << MemoBits];

    public GifPalette(IReadOnlyList<SKColor> colors, int transparentIndex)
    {
        ArgumentNullException.ThrowIfNull(colors);
        ArgumentOutOfRangeException.ThrowIfZero(colors.Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(colors.Count, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(transparentIndex, -1);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(transparentIndex, colors.Count);
        if (transparentIndex >= 0 && colors.Count == 1)
        {
            throw new ArgumentException("A palette needs at least one opaque colour.", nameof(colors));
        }

        _colors = colors.ToArray();
        TransparentIndex = transparentIndex;
        Array.Fill(_memoKeys, -1);
        var bits = 1;
        while (1 << bits < _colors.Length)
        {
            bits++;
        }

        BitsPerIndex = bits;
    }

    public IReadOnlyList<SKColor> Colors => _colors;

    /// <summary>The transparent slot, or -1 when the palette has none.</summary>
    public int TransparentIndex { get; }

    /// <summary>Bits per index (1..8): the colour table holds 2^BitsPerIndex entries after padding.</summary>
    public int BitsPerIndex { get; }

    /// <summary>
    /// The opaque entry nearest by squared RGB distance (lowest index on ties). Each 8x8x8 cell
    /// caches the entries that can be nearest anywhere inside it, so the search stays exact; a
    /// direct-mapped memo keyed by the full colour (bounded at 64K entries) skips repeat searches.
    /// </summary>
    public byte IndexOf(byte r, byte g, byte b)
    {
        var rgb = (r << 16) | (g << 8) | b;
        var slot = (int)(((uint)rgb * 0x9E3779B1u) >> (32 - MemoBits));
        if (_memoKeys[slot] == rgb)
        {
            return _memoValues[slot];
        }

        var index = Search(r, g, b);
        _memoKeys[slot] = rgb;
        _memoValues[slot] = index;
        return index;
    }

    private byte Search(byte r, byte g, byte b)
    {
        var cell =_cells[((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3)] ??= Candidates(r >> 3 << 3, g >> 3 << 3, b >> 3 << 3);
        var best = 0;
        var bestDistance = int.MaxValue;
        for (var k = 0; k < cell.Length; k++)
        {
            var i = cell[k] & 0xFF;
            if (cell[k] >> 8 > bestDistance)
            {
                break; // sorted by distance to the cell: nothing further on can be nearer
            }

            var c = _colors[i];
            int dr = c.Red - r, dg = c.Green - g, db = c.Blue - b;
            var d = (dr * dr) + (dg * dg) + (db * db);
            if (d < bestDistance || (d == bestDistance && i < best))
            {
                bestDistance = d;
                best = i;
            }
        }

        return (byte)best;
    }

    /// <summary>
    /// The entries that can be nearest somewhere in the cell (their nearest point in the cell is no
    /// farther than the best farthest-corner distance), packed as (distance to the cell &lt;&lt; 8) | index
    /// and sorted, so a lookup can stop early.
    /// </summary>
    private int[] Candidates(int r0, int g0, int b0)
    {
        var limit = int.MaxValue;
        for (var i = 0; i < _colors.Length; i++)
        {
            if (i != TransparentIndex)
            {
                var c = _colors[i];
                limit = Math.Min(limit, Far(c.Red, r0) + Far(c.Green, g0) + Far(c.Blue, b0));
            }
        }

        var list = new List<int>();
        for (var i = 0; i < _colors.Length; i++)
        {
            var c = _colors[i];
            var near = Near(c.Red, r0) + Near(c.Green, g0) + Near(c.Blue, b0);
            if (i != TransparentIndex && near <= limit)
            {
                list.Add((near << 8) | i);
            }
        }

        list.Sort();
        return list.ToArray();

        static int Near(int v, int lo)
        {
            var d = v < lo ? lo - v : v > lo + 7 ? v - lo - 7 : 0;
            return d * d;
        }

        static int Far(int v, int lo)
        {
            var d = Math.Max(Math.Abs(v - lo), Math.Abs(v - lo - 7));
            return d * d;
        }
    }
}

/// <summary>Reads straight (unpremultiplied) RGBA from premultiplied RGBA8888 pixels.</summary>
internal static class Pixels
{
    public static (int R, int G, int B, int A) Unpremultiply(ReadOnlySpan<byte> pixels, int offset)
    {
        int r = pixels[offset], g = pixels[offset + 1], b = pixels[offset + 2], a = pixels[offset + 3];
        if (a == 255)
        {
            return (r, g, b, a);
        }

        if (a == 0)
        {
            return (0, 0, 0, 0);
        }

        var half = a / 2;
        return (Math.Min(255, ((r * 255) + half) / a), Math.Min(255, ((g * 255) + half) / a), Math.Min(255, ((b * 255) + half) / a), a);
    }
}
