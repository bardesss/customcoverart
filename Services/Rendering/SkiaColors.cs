using System.Globalization;
using SkiaSharp;

namespace CustomCoverArt.Services.Rendering;

internal static class SkiaColors
{
    /// <summary>
    /// Parses #rgb, #rgba, #rrggbb or #rrggbbaa (the leading # is optional, surrounding
    /// whitespace is ignored). Anything else, including named colours, returns
    /// <paramref name="fallback"/>. Never throws, so a malformed colour in a request body
    /// cannot abort a render.
    /// </summary>
    public static SKColor Parse(string? hex, SKColor fallback)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return fallback;
        }

        var s = hex.Trim();
        if (s.StartsWith('#'))
        {
            s = s.Substring(1);
        }

        if (s.Length is not (3 or 4 or 6 or 8))
        {
            return fallback;
        }

        foreach (var ch in s)
        {
            if (!Uri.IsHexDigit(ch))
            {
                return fallback;
            }
        }

        var short_ = s.Length <= 4;
        var step = short_ ? 1 : 2;
        byte Channel(int index)
        {
            var part = s.Substring(index * step, step);
            if (short_)
            {
                part += part;
            }

            return byte.Parse(part, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        }

        var hasAlpha = s.Length is 4 or 8;
        return new SKColor(Channel(0), Channel(1), Channel(2), hasAlpha ? Channel(3) : (byte)255);
    }

    /// <summary>Replaces the alpha channel with <paramref name="alpha01"/> (clamped to 0..1).</summary>
    public static SKColor WithAlpha(SKColor c, float alpha01)
    {
        var a = (byte)Math.Round(Math.Clamp(alpha01, 0f, 1f) * 255f);
        return c.WithAlpha(a);
    }
}
