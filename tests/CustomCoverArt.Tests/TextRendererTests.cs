using System;
using System.IO;
using System.Linq;
using System.Numerics;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using CustomCoverArt.Services.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SkiaSharp;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// Text drawn with SkiaSharp and HarfBuzz (spec 6.4). Everything here is statistical (ink
/// centroids, extents, mass) or compares two renders made on the same machine, never exact
/// glyph pixels, because rasterisation differs between Windows and Linux (spec 7.4 rule 5).
/// </summary>
// Some cases load custom and legacy fonts, which reach FontStore's warn-once set and log target.
[Collection("FontStoreState")]
public class TextRendererTests
{
    private const int Px = 300;

    private static CoverDocument Doc(int canvasHeight = Px)
        => new() { Canvas = new CanvasSettings { Width = Px, Height = canvasHeight } };

    // White "HOH" (symmetric glyphs, no descenders) centred at the canvas centre, 60 px.
    private static CoverLayer Layer(Action<CoverLayer>? tweak = null)
    {
        var layer = new CoverLayer { Type = "text", Content = "HOH", Color = "#ffffff", Size = 0.2f, X = 0.5f, Y = 0.5f };
        tweak?.Invoke(layer);
        return layer;
    }

    private static SKBitmap Render(CoverLayer layer, CoverDocument? doc = null)
    {
        var bmp = RenderSurface.New(Px, Px, SKColors.Black);
        TextRenderer.RenderTextLayer(bmp, layer, doc ?? Doc());
        return bmp;
    }

    private readonly record struct Ink(double Mass, double Mx, double My, int MinX, int MaxX, int MinY, int MaxY);

    private static Ink Measure(SKBitmap bmp, Func<Rgba, double> weight)
    {
        double m = 0, sx = 0, sy = 0;
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        for (var y = 0; y < bmp.Height; y++)
        {
            for (var x = 0; x < bmp.Width; x++)
            {
                var w = weight(TestPixels.Px(bmp, x, y));
                if (w <= 0) { continue; }
                m += w; sx += w * x; sy += w * y;
                if (w > 64) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
            }
        }

        Assert.True(m > 0, "Nothing to measure: the weighted ink is empty.");
        return new Ink(m, sx / m, sy / m, minX, maxX, minY, maxY);
    }

    // White glyphs over black: green is the glyphs alone (a red shadow or outline has none).
    private static double GlyphInk(Rgba p) => p.G;

    // A pure-red shadow over black is (255a, 0, 0); white glyphs have R == G.
    private static double ShadowInk(Rgba p) => p.R - p.G;

    private static bool SameBytes(SKBitmap a, SKBitmap b) => a.GetPixelSpan().SequenceEqual(b.GetPixelSpan());

    private static SKTypeface Regular => FontStore.Bundled(FontWeight.Normal);

    // ---- Shaping ----

    [Fact]
    public void Shaping_AppliesKerning()
    {
        var av = TextRenderer.Shape(Regular, 80, "AV");
        var a = TextRenderer.Shape(Regular, 80, "A");
        var v = TextRenderer.Shape(Regular, 80, "V");
        using (av) using (a) using (v)
        {
            Assert.True(av.Width < a.Width + v.Width - 1, $"AV={av.Width}, A+V={a.Width + v.Width}: the kern pair was not applied.");
        }
    }

    [Fact]
    public void Shape_EmptyText_HasNoBlobAndZeroWidth()
    {
        var line = TextRenderer.Shape(Regular, 40, string.Empty);

        Assert.Null(line.Blob);
        Assert.Null(line.Path);
        Assert.Equal(0f, line.Width);
    }

    [Fact]
    public void Shape_PathHoldsEveryOutlinedGlyph_AndSpacesAddNone()
    {
        using var word = TextRenderer.Shape(Regular, 40, "HOH");
        using var spaced = TextRenderer.Shape(Regular, 40, "H O H");
        using var spaces = TextRenderer.Shape(Regular, 40, "   ");

        Assert.NotNull(word.Path);
        Assert.False(word.Path!.IsEmpty);
        Assert.True(spaces.Path!.IsEmpty);
        // The spaces only move the glyphs apart: same contours, wider bounds.
        Assert.Equal(word.Path.PointCount, spaced.Path!.PointCount);
        Assert.True(spaced.Path.Bounds.Width > word.Path.Bounds.Width);
        // The path's ink lies inside the shaped advance (with Noto's side bearings).
        Assert.InRange(word.Path.Bounds.Left, 0f, 0.15f * 40);
        Assert.InRange(word.Path.Bounds.Right, word.Width - (0.15f * 40), word.Width);
    }

    /// <summary>
    /// The fill is a plain path fill, not Skia's glyph-mask text pipeline (whose gamma and contrast
    /// tables depend on the text colour, the size and the platform build): one glyph drawn by the
    /// renderer is byte-identical to the same glyph filled from <see cref="SKFont.GetTextPath(string, SKPoint)"/>.
    /// </summary>
    [Theory]
    [InlineData("H", 0.2f)]
    [InlineData("g", 0.11f)]
    [InlineData("W", 0.6f)]
    public void Fill_IsTheGlyphPath_ByteForByte(string glyph, float size)
    {
        var pixels = size * Px;
        using var actual = Render(Layer(l => { l.Content = glyph; l.Size = size; }));

        using var line = TextRenderer.Shape(Regular, pixels, glyph);
        using var font = new SKFont(Regular, pixels) { Edging = SKFontEdging.Antialias, Subpixel = true, Hinting = SKFontHinting.None, LinearMetrics = true };
        using var expected = RenderSurface.New(Px, Px, SKColors.Black);
        using (var canvas = new SKCanvas(expected))
        using (var paint = new SKPaint { IsAntialias = true, Color = SKColors.White })
        {
            canvas.Translate(150f - (line.Width / 2f), 150f - ((line.Ascent + line.Descent) / 2f));
            using var path = font.GetTextPath(glyph, new SKPoint(0, 0));
            canvas.DrawPath(path, paint);
        }

        Assert.True(SameBytes(expected, actual));
    }

    /// <summary>
    /// Skia's glyph masks drew white Light text up to 28 % heavier and black text up to 21 % lighter
    /// than its outline. A path fill does not depend on the colour: white on black and black on
    /// white cover the same area.
    /// </summary>
    [Theory]
    [InlineData(FontWeight.Light, 0.06f)]
    [InlineData(FontWeight.Light, 0.12f)]
    [InlineData(FontWeight.ExtraBold, 0.12f)]
    public void Fill_InkDoesNotDependOnTheTextColour(FontWeight weight, float size)
    {
        var doc = Doc();
        using var light = RenderSurface.New(Px, Px, SKColors.Black);
        TextRenderer.RenderTextLayer(light, Layer(l => { l.Weight = weight; l.Size = size; l.Content = "Midnight"; }), doc);
        using var dark = RenderSurface.New(Px, Px, SKColors.White);
        TextRenderer.RenderTextLayer(dark, Layer(l => { l.Weight = weight; l.Size = size; l.Content = "Midnight"; l.Color = "#000000"; }), doc);

        var whiteInk = Measure(light, p => p.G);
        var blackInk = Measure(dark, p => 255 - p.G);

        Assert.InRange(whiteInk.Mass / blackInk.Mass, 0.99, 1.01);
    }

    [Fact]
    public void Weight_InkGrowsFromLightThroughRegularToExtraBold()
    {
        using var light = Render(Layer(l => l.Weight = FontWeight.Light));
        using var regular = Render(Layer(l => l.Weight = FontWeight.Normal));
        using var extraBold = Render(Layer(l => l.Weight = FontWeight.ExtraBold));

        var l = Measure(light, GlyphInk).Mass;
        var r = Measure(regular, GlyphInk).Mass;
        var x = Measure(extraBold, GlyphInk).Mass;
        Assert.True(l < r * 0.95, $"Light {l:F0} should be lighter than Regular {r:F0}.");
        Assert.True(r < x * 0.8, $"Regular {r:F0} should be lighter than ExtraBold {x:F0}.");
    }

    [Fact]
    public void Shape_NullText_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => TextRenderer.Shape(Regular, 40, null!));
    }

    [Fact]
    public void Shape_WidthScalesWithSize_AndMetricsAreTheFontsAscentAndDescent()
    {
        var small = TextRenderer.Shape(Regular, 40, "Hello");
        var large = TextRenderer.Shape(Regular, 80, "Hello");
        using (small) using (large)
        {
            Assert.NotNull(small.Blob);
            Assert.InRange(large.Width / small.Width, 1.98, 2.02);
            Assert.True(small.Ascent < 0, "Skia's ascent is above the baseline, so negative.");
            Assert.True(small.Descent > 0, "Skia's descent is below the baseline, so positive.");

            using var font = new SKFont(Regular, 40);
            Assert.Equal(font.Metrics.Ascent, small.Ascent, 3);
            Assert.Equal(font.Metrics.Descent, small.Descent, 3);
        }
    }

    [Fact]
    public void Shape_SpacesOnly_HasWidthButNoInk()
    {
        var line = TextRenderer.Shape(Regular, 40, "   ");
        using (line)
        {
            Assert.True(line.Width > 10, $"Three spaces should advance, got {line.Width}.");
        }

        using var bmp = Render(Layer(l => l.Content = "   "));
        using var blank = RenderSurface.New(Px, Px, SKColors.Black);
        Assert.True(SameBytes(blank, bmp));
    }

    // ---- Placement ----

    [Fact]
    public void MiddleBaseline_CentresInkOnTheAnchor()
    {
        using var bmp = Render(Layer());

        var ink = Measure(bmp, GlyphInk);

        Assert.InRange(ink.My, 147.0, 153.0);
        Assert.InRange(ink.Mx, 148.0, 152.0);
    }

    [Fact]
    public void MiddleBaseline_FollowsTheAnchorY()
    {
        using var high = Render(Layer(l => l.Y = 0.25f));
        using var low = Render(Layer(l => l.Y = 0.75f));

        Assert.InRange(Measure(low, GlyphInk).My - Measure(high, GlyphInk).My, 149.0, 151.0);
    }

    /// <summary>
    /// Left puts the start of the advance at the anchor, right its end, centre its middle. The
    /// H's side bearing keeps the ink a few pixels inside the advance, never outside it.
    /// </summary>
    [Fact]
    public void Alignment_PlacesTheInkEdgesAtTheAnchor()
    {
        const float size = 60f;
        var line = TextRenderer.Shape(Regular, size, "HOH");
        line.Dispose();
        using var left = Render(Layer(l => l.Align = TextAlign.Left));
        using var centre = Render(Layer(l => l.Align = TextAlign.Center));
        using var right = Render(Layer(l => l.Align = TextAlign.Right));

        var l = Measure(left, GlyphInk);
        var c = Measure(centre, GlyphInk);
        var r = Measure(right, GlyphInk);

        // Left: ink starts just right of the anchor (150) and ends just short of anchor + width.
        Assert.InRange(l.MinX, 150, 150 + (0.15 * size));
        Assert.InRange(l.MaxX, 150 + line.Width - (0.15 * size), 150 + line.Width);
        // Right: the mirror image.
        Assert.InRange(r.MaxX, 150 - (0.15 * size), 150);
        Assert.InRange(r.MinX, 150 - line.Width, 150 - line.Width + (0.15 * size));
        // The three differ by exactly half the shaped width each.
        Assert.InRange(l.Mx - c.Mx, (line.Width / 2) - 1, (line.Width / 2) + 1);
        Assert.InRange(c.Mx - r.Mx, (line.Width / 2) - 1, (line.Width / 2) + 1);
        // Alignment never moves the line vertically.
        Assert.InRange(l.My - r.My, -0.5, 0.5);
    }

    [Theory]
    [InlineData(37f)]
    [InlineData(90f)]
    [InlineData(180f)]
    [InlineData(270f)]
    [InlineData(-45f)]
    public void Rotation_KeepsTheInkCentroidAtTheAnchor(float degrees)
    {
        using var flat = Render(Layer());
        using var turned = Render(Layer(l => l.Rotation = degrees));

        var f = Measure(flat, GlyphInk);
        var t = Measure(turned, GlyphInk);

        Assert.InRange(t.Mx, 147.0, 153.0);
        Assert.InRange(t.My, 147.0, 153.0);
        Assert.InRange(t.Mass / f.Mass, 0.95, 1.05);
    }

    [Fact]
    public void Rotation_TurnsTheLineAboutTheAnchorClockwise()
    {
        // Left-aligned at the centre: unrotated the ink runs to the right of the anchor; turned
        // 90° clockwise (canvas y points down) it runs downwards from it.
        using var turned = Render(Layer(l => { l.Align = TextAlign.Left; l.Rotation = 90f; }));

        var t = Measure(turned, GlyphInk);

        Assert.True(t.My > 170, $"Rotated 90° the line should hang below the anchor, centroid y={t.My:F1}.");
        Assert.InRange(t.Mx, 140.0, 160.0);
    }

    [Fact]
    public void OffCanvasText_DrawsNothingAndDoesNotThrow()
    {
        using var bmp = Render(Layer(l => { l.X = 5f; l.Y = -3f; }));
        using var blank = RenderSurface.New(Px, Px, SKColors.Black);

        Assert.True(SameBytes(blank, bmp));
    }

    // ---- Size and opacity ----

    [Fact]
    public void FontSize_ClampsUpTo8Pixels()
    {
        // Canvas height 16: Size 0.25 asks for 4 px and must draw exactly what 8 px draws.
        using var tiny = Render(Layer(l => l.Size = 0.25f), Doc(16));
        using var eight = Render(Layer(l => l.Size = 0.5f), Doc(16));
        using var sixteen = Render(Layer(l => l.Size = 1f), Doc(16));

        Assert.True(SameBytes(eight, tiny), "A 4 px request should render at the 8 px floor.");
        Assert.False(SameBytes(eight, sixteen));
        Assert.True(Measure(tiny, GlyphInk).Mass > 0);
    }

    [Fact]
    public void FontSize_ClampsDownTo1024Pixels()
    {
        // Canvas height 1024: Size 2 asks for 2048 px and must draw exactly what 1024 px draws.
        using var huge = Render(Layer(l => { l.Size = 2f; l.Content = "I"; }), Doc(1024));
        using var cap = Render(Layer(l => { l.Size = 1f; l.Content = "I"; }), Doc(1024));
        using var half = Render(Layer(l => { l.Size = 0.5f; l.Content = "I"; }), Doc(1024));

        Assert.True(SameBytes(cap, huge), "A 2048 px request should render at the 1024 px ceiling.");
        Assert.False(SameBytes(cap, half));
    }

    [Fact]
    public void FontSize_FollowsTheDocumentCanvasHeight_NotTheBitmap()
    {
        // The bitmap is always 300 px; only the document's declared height changes.
        using var small = Render(Layer(l => l.Content = "H"), Doc(300));
        using var big = Render(Layer(l => l.Content = "H"), Doc(600));

        var s = Measure(small, GlyphInk);
        var b = Measure(big, GlyphInk);
        Assert.InRange((b.MaxY - b.MinY + 1) / (double)(s.MaxY - s.MinY + 1), 1.9, 2.1);
    }

    [Fact]
    public void OpacityZero_DrawsNothing_EvenWithShadowAndOutline()
    {
        using var bmp = Render(Layer(l =>
        {
            l.Opacity = 0f;
            l.Shadow = new TextShadowSettings { Enabled = true, Color = "#ff0000", Blur = 6, OffsetX = 5, OffsetY = 5 };
            l.Outline = new TextOutlineSettings { Enabled = true, Color = "#00ff00", Width = 4 };
        }));
        using var blank = RenderSurface.New(Px, Px, SKColors.Black);

        Assert.True(SameBytes(blank, bmp));
    }

    [Fact]
    public void Opacity_ScalesTheGlyphInkLinearly()
    {
        using var full = Render(Layer());
        using var half = Render(Layer(l => l.Opacity = 0.5f));

        Assert.InRange(Measure(half, GlyphInk).Mass / Measure(full, GlyphInk).Mass, 0.48, 0.52);
    }

    [Fact]
    public void Colour_IsTheLayerColour_AndAMalformedOneFallsBackToWhite()
    {
        using var red = Render(Layer(l => { l.Content = "I"; l.Size = 0.6f; l.Color = "#ff0000"; }));
        using var bad = Render(Layer(l => { l.Content = "I"; l.Size = 0.6f; l.Color = "not a colour"; }));

        Assert.Equal(new Rgba(255, 0, 0, 255), TestPixels.Px(red, 150, 150));
        Assert.Equal(new Rgba(255, 255, 255, 255), TestPixels.Px(bad, 150, 150));
    }

    // ---- Shadow ----

    /// <summary>
    /// Canvas shadow offsets live in screen space (HTML spec), so on a layer turned 45° the shadow
    /// still falls (dx, dy) away on screen, not along the rotated axes. The offset is perpendicular
    /// to the rotated line so no shadow hides under a glyph.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void ShadowOffset_StaysInScreenSpace_At45Degrees(int blur)
    {
        using var bmp = Render(Layer(l =>
        {
            l.Content = "HELLO";
            l.Size = 0.1f;
            l.Rotation = 45f;
            l.Shadow = new TextShadowSettings { Enabled = true, Color = "#ff0000", Blur = blur, OffsetX = -28, OffsetY = 28 };
        }));

        var glyph = Measure(bmp, GlyphInk);
        var shadow = Measure(bmp, ShadowInk);

        Assert.InRange(shadow.Mx - glyph.Mx, -30.0, -26.0);
        Assert.InRange(shadow.My - glyph.My, 26.0, 30.0);
    }

    [Fact]
    public void Shadow_OffsetIsClampedTo50Pixels()
    {
        using var far = Render(Layer(l =>
        {
            l.Content = "I"; l.Y = 0.25f;
            l.Shadow = new TextShadowSettings { Enabled = true, Color = "#ff0000", Blur = 0, OffsetX = 0, OffsetY = 500 };
        }));

        var glyph = Measure(far, GlyphInk);
        var shadow = Measure(far, ShadowInk);
        Assert.InRange(shadow.My - glyph.My, 48.0, 52.0);
    }

    [Fact]
    public void Shadow_IsDrawnUnderTheFill()
    {
        // A shadow offset by 1 px sits almost entirely under the glyph: the stem centre stays white.
        using var bmp = Render(Layer(l =>
        {
            l.Content = "I"; l.Size = 0.6f;
            l.Shadow = new TextShadowSettings { Enabled = true, Color = "#ff0000", Blur = 0, OffsetX = 1, OffsetY = 1 };
        }));

        Assert.Equal(new Rgba(255, 255, 255, 255), TestPixels.Px(bmp, 150, 150));
    }

    [Fact]
    public void Shadow_AtHalfOpacity_IsHalfStrength()
    {
        // A hard shadow far enough below that it never touches the glyphs.
        CoverLayer L(float opacity) => Layer(l =>
        {
            l.Content = "I"; l.Size = 0.15f; l.Y = 0.3f; l.Opacity = opacity;
            l.Shadow = new TextShadowSettings { Enabled = true, Color = "#ff0000", Blur = 0, OffsetX = 0, OffsetY = 50 };
        });
        using var full = Render(L(1f));
        using var half = Render(L(0.5f));

        Assert.InRange(Measure(half, ShadowInk).Mass / Measure(full, ShadowInk).Mass, 0.48, 0.52);
    }

    [Fact]
    public void BlurredShadow_KeepsItsMass_AndOnlySpreads()
    {
        CoverLayer L(int blur) => Layer(l =>
        {
            l.Y = 0.3f;
            l.Shadow = new TextShadowSettings { Enabled = true, Color = "#ff0000", Blur = blur, OffsetX = 0, OffsetY = 80 };
        });
        using var hard = Render(L(0));
        using var soft = Render(L(10));

        var h = Measure(hard, ShadowInk);
        var s = Measure(soft, ShadowInk);
        Assert.InRange(s.Mass / h.Mass, 0.95, 1.05);
        Assert.InRange(s.Mx - h.Mx, -1.0, 1.0);
        Assert.InRange(s.My - h.My, -1.0, 1.0);
        Assert.True(s.MaxX - s.MinX > h.MaxX - h.MinX, "A blurred shadow should reach further than the hard one.");
    }

    // ---- Outline ----

    /// <summary>
    /// The canvas strokes the outline along the glyph contour only, so under a half-transparent
    /// fill the stem's interior shows no outline colour, while just outside the edge it does.
    /// </summary>
    [Fact]
    public void Outline_UnderHalfOpacityFill_LeavesTheGlyphInteriorClear()
    {
        using var bmp = Render(Layer(l =>
        {
            l.Content = "I";
            l.Size = 200f / Px;
            l.Opacity = 0.5f;
            l.Outline = new TextOutlineSettings { Enabled = true, Color = "#ff0000", Width = 2 };
        }));

        // The stem's left edge: the first column whose green (fill only) is at least half its peak.
        var y = 150;
        var centre = TestPixels.Px(bmp, 150, y);
        var edge = Enumerable.Range(0, 150).First(x => TestPixels.Px(bmp, x, y).G >= centre.G / 2);
        var outside = TestPixels.Px(bmp, edge - 1, y);

        Assert.True(centre.G > 100, $"The stem centre should be half-white fill, got {centre}.");
        Assert.True(centre.R - centre.G <= 2, $"No outline under the interior: {centre}.");
        Assert.True(outside.R > 40, $"1 px outside the stem edge the outline shows: {outside}.");
    }

    /// <summary>
    /// A stroke of width 2 x Width centred on the contour reaches Width px beyond the glyphs on
    /// every side, not only horizontally.
    /// </summary>
    [Fact]
    public void Outline_ReachesWidthPixelsBeyondTheGlyphs_OnEverySide()
    {
        using var plain = Render(Layer());
        using var outlined = Render(Layer(l => l.Outline = new TextOutlineSettings { Enabled = true, Color = "#ff0000", Width = 5 }));

        // White fill and red outline both have full red.
        var p = Measure(plain, px => px.R);
        var o = Measure(outlined, px => px.R);
        Assert.InRange(p.MinX - o.MinX, 4, 6);
        Assert.InRange(o.MaxX - p.MaxX, 4, 6);
        Assert.InRange(p.MinY - o.MinY, 4, 6);
        Assert.InRange(o.MaxY - p.MaxY, 4, 6);
    }

    [Fact]
    public void Outline_WidthIsClampedTo10()
    {
        CoverLayer L(int width) => Layer(l => l.Outline = new TextOutlineSettings { Enabled = true, Color = "#ff0000", Width = width });
        using var ten = Render(L(10));
        using var huge = Render(L(400));

        Assert.True(SameBytes(ten, huge));
    }

    [Fact]
    public void Outline_DisabledOrZeroWidth_DrawsOnlyTheFill()
    {
        using var plain = Render(Layer());
        using var disabled = Render(Layer(l => l.Outline = new TextOutlineSettings { Enabled = false, Color = "#ff0000", Width = 6 }));
        using var zero = Render(Layer(l => l.Outline = new TextOutlineSettings { Enabled = true, Color = "#ff0000", Width = 0 }));

        Assert.True(SameBytes(plain, disabled));
        Assert.True(SameBytes(plain, zero));
    }

    // ---- Fonts ----

    /// <summary>
    /// The fixtures are subsets of Noto Sans Regular, so a layer that asks for ExtraBold but names
    /// one of them draws Regular-weight ink: the custom face is used and the weight is ignored.
    /// </summary>
    [Theory]
    [InlineData("sub.ttf")]
    [InlineData("sub.otf")]
    [InlineData("sub.ttc")]
    public void CustomFont_IsTheFaceThatDraws(string fixture)
    {
        using var dir = new TempFolder();
        var path = dir.Copy(fixture, "custom" + Path.GetExtension(fixture));
        using var custom = Render(Layer(l => { l.FontPath = path; l.Weight = FontWeight.ExtraBold; }));
        using var extraBold = Render(Layer(l => l.Weight = FontWeight.ExtraBold));
        using var regular = Render(Layer(l => l.Weight = FontWeight.Normal));

        var c = Measure(custom, GlyphInk).Mass;
        var x = Measure(extraBold, GlyphInk).Mass;
        var r = Measure(regular, GlyphInk).Mass;
        Assert.True(c < x * 0.85, $"Custom ink {c:F0} should be well under ExtraBold {x:F0}.");
        Assert.InRange(c / r, 0.95, 1.05);
    }

    [Fact]
    public void LegacyWoff2Font_RendersInNotoAtTheLayerWeight()
    {
        using var dir = new TempFolder();
        var path = dir.Copy("sub.woff2", "old.woff2");
        var log = new CapturingLogger<TextRendererTests>();
        using var capture = FontStore.CaptureLogForTests(log);
        FontStore.ResetWarningsForTests();

        using var legacy = Render(Layer(l => { l.FontPath = path; l.Weight = FontWeight.Bold; }));
        using var again = Render(Layer(l => { l.FontPath = path; l.Weight = FontWeight.Bold; }));
        using var bold = Render(Layer(l => l.Weight = FontWeight.Bold));

        Assert.True(SameBytes(bold, legacy));
        Assert.True(SameBytes(bold, again));
        Assert.Single(log.Entries, e => e.Message.Contains("old.woff2"));
    }

    [Fact]
    public void Weight_ChangesTheInk()
    {
        using var light = Render(Layer(l => l.Weight = FontWeight.Light));
        using var extraBold = Render(Layer(l => l.Weight = FontWeight.ExtraBold));

        Assert.True(Measure(extraBold, GlyphInk).Mass > Measure(light, GlyphInk).Mass * 1.4);
    }

    // ---- Robustness ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\U0001F600")]
    [InlineData("W500")]
    [InlineData("é")]
    [InlineData("שלום")]
    [InlineData("CONTROL")]
    [InlineData("LONE-SURROGATE")]
    public void EdgeContent_NeverThrows(string content)
    {
        // Tokens for strings an attribute cannot carry (or a test name should not).
        content = content switch
        {
            "W500" => new string('W', 500),
            "CONTROL" => "\0\t\n\r",
            "LONE-SURROGATE" => "A\ud800B",
            _ => content,
        };
        var doc = Doc();
        using var bmp = RenderSurface.New(Px, Px, SKColors.Black);

        var ex = Record.Exception(() => TextRenderer.RenderTextLayerWithFallback(bmp, Layer(l =>
        {
            l.Content = content;
            l.Shadow = new TextShadowSettings { Enabled = true, Color = "#ff0000", Blur = 4, OffsetX = 3, OffsetY = 3 };
            l.Outline = new TextOutlineSettings { Enabled = true, Color = "#ff0000", Width = 2 };
        }), doc));
        Assert.Null(ex);

        // A second layer drawn afterwards still appears: green "I" in the top-left quarter.
        TextRenderer.RenderTextLayerWithFallback(bmp, Layer(l => { l.Content = "I"; l.Color = "#00ff00"; l.X = 0.1f; l.Y = 0.1f; }), doc);
        var green = Measure(bmp, p => p.G > 200 && p.R < 50 ? p.G : 0);
        Assert.InRange(green.Mx, 20.0, 40.0);
        Assert.InRange(green.My, 20.0, 40.0);
    }

    [Fact]
    public void RenderTextLayer_ThrowsOnABrokenLayer()
    {
        using var bmp = RenderSurface.New(Px, Px, SKColors.Black);

        Assert.NotNull(Record.Exception(() => TextRenderer.RenderTextLayer(bmp, Layer(l => l.Shadow = null!), Doc())));
    }

    /// <summary>The effects-free fallback draws plain Regular text centred on the canvas, whatever the anchor.</summary>
    [Fact]
    public void Fallback_DrawsPlainTextCentredOnTheCanvas()
    {
        using var bmp = RenderSurface.New(Px, Px, SKColors.Black);
        var layer = Layer(l => { l.Size = 0.4f; l.X = 0.1f; l.Y = 0.9f; l.Shadow = null!; l.Color = "#ff0000"; l.Opacity = 0.2f; });

        TextRenderer.RenderTextLayerWithFallback(bmp, layer, Doc());

        var ink = Measure(bmp, p => p.R);
        Assert.InRange(ink.Mx, 147.0, 153.0);
        Assert.InRange(ink.My, 147.0, 153.0);
        // Opacity is not applied on the fallback path: the stems are full red.
        Assert.Contains(Enumerable.Range(0, Px).Select(x => TestPixels.Px(bmp, x, 150)), p => p.R == 255 && p.G == 0);
        // Half the 120 px size: Noto's 0.714 em cap height at 60 px is about 43 px.
        Assert.InRange(ink.MaxY - ink.MinY + 1, 40, 46);
    }

    [Fact]
    public void Fallback_WhenEvenThePlainDrawFails_LeavesTheCanvasUntouched()
    {
        using var bmp = RenderSurface.New(80, 80, SKColors.Green);
        using var before = RenderSurface.New(80, 80, SKColors.Green);
        var layer = new CoverLayer { Type = "text", Content = null!, Shadow = null! };

        var ex = Record.Exception(() => TextRenderer.RenderTextLayerWithFallback(bmp, layer, Doc()));

        Assert.Null(ex);
        Assert.True(SameBytes(before, bmp));
    }

    [Theory]
    [InlineData(100f, 1400, 1024f)]
    [InlineData(0.001f, 100, 8f)]
    [InlineData(0.2f, 1000, 100f)]
    public void FallbackFontPixelSize_IsHalfTheLayerSize_Clamped(float size, int canvasHeight, float expected)
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = canvasHeight, Height = canvasHeight } };

        Assert.Equal(expected, TextRenderer.FallbackFontPixelSize(new CoverLayer { Size = size }, doc), 3);
    }

    [Fact]
    public void Render_IsDeterministic()
    {
        CoverLayer L() => Layer(l =>
        {
            l.Rotation = 23f;
            l.Shadow = new TextShadowSettings { Enabled = true, Color = "#ff0000", Blur = 9, OffsetX = 4, OffsetY = -6 };
            l.Outline = new TextOutlineSettings { Enabled = true, Color = "#0000ff", Width = 3 };
        });
        using var a = Render(L());
        using var b = Render(L());

        Assert.True(SameBytes(a, b));
    }

    [Fact]
    public void ConcurrentRenders_MatchASerialRender()
    {
        using var serial = Render(Layer(l => l.Shadow = new TextShadowSettings { Enabled = true, Color = "#ff0000", Blur = 5 }));

        var same = Enumerable.Range(0, 16).AsParallel().Select(_ =>
        {
            using var bmp = Render(Layer(l => l.Shadow = new TextShadowSettings { Enabled = true, Color = "#ff0000", Blur = 5 }));
            return SameBytes(serial, bmp);
        }).ToArray();

        Assert.All(same, Assert.True);
    }

    /// <summary>The ImageSharp entry point (until Task 9) draws exactly what the Skia one does on an opaque canvas.</summary>
    [Fact]
    public void DocumentRendererBridge_DrawsTheSameAsTextRenderer()
    {
        var layer = Layer(l => l.Shadow = new TextShadowSettings { Enabled = true, Color = "#ff0000", Blur = 4, OffsetX = 3, OffsetY = 3 });
        using var skia = RenderSurface.New(Px, Px, SKColors.Black);
        TextRenderer.RenderTextLayerWithFallback(skia, layer, Doc());
        using var sharp = new Image<Rgba32>(Px, Px, SixLabors.ImageSharp.Color.Black);

        DocumentRenderer.RenderTextLayerWithFallback(sharp, layer, Doc());

        for (var y = 0; y < Px; y += 3)
        {
            for (var x = 0; x < Px; x++)
            {
                var s = TestPixels.Px(skia, x, y);
                var i = sharp[x, y];
                Assert.Equal(new Rgba(s.R, s.G, s.B, s.A), new Rgba(i.R, i.G, i.B, i.A));
            }
        }
    }

    // ---- Matrices ----

    [Fact]
    public void ToSkMatrix_MapsPointsLikeSystemNumerics()
    {
        var m = Matrix3x2.CreateRotation(0.7f, new Vector2(120, 80)) * Matrix3x2.CreateTranslation(5, -9) * Matrix3x2.CreateScale(1.5f, 0.75f);
        var sk = TextRenderer.ToSkMatrix(m);

        Assert.Equal(1f, sk.Persp2);
        Assert.Equal(0f, sk.Persp0);
        Assert.Equal(0f, sk.Persp1);
        foreach (var p in new[] { new Vector2(0, 0), new Vector2(10, 0), new Vector2(0, 10), new Vector2(-37.5f, 211f) })
        {
            var expected = Vector2.Transform(p, m);
            var actual = sk.MapPoint(p.X, p.Y);
            Assert.Equal(expected.X, actual.X, 3);
            Assert.Equal(expected.Y, actual.Y, 3);
        }
    }

    [Fact]
    public void ShadowTransform_UnrotatedLayer_IsIdentity()
    {
        Assert.True(TextRenderer.ShadowTransform(Matrix3x2.Identity, 7f, -3f).IsIdentity);
    }

    [Fact]
    public void ShadowTransform_KeepsTheOffsetAnchorFixed_AndRotatesAboutIt()
    {
        var anchor = new Vector2(150, 150);
        var rotate = Matrix3x2.CreateRotation(MathF.PI / 2, anchor);
        var t = TextRenderer.ShadowTransform(rotate, 10, -4);

        // The shadow is drawn at anchor + d; that point stays put on screen ...
        var moved = Vector2.Transform(anchor + new Vector2(10, -4), t);
        Assert.Equal(160f, moved.X, 3);
        Assert.Equal(146f, moved.Y, 3);
        // ... and a point 20 px right of it turns 90° clockwise about it.
        var arm = Vector2.Transform(anchor + new Vector2(30, -4), t);
        Assert.Equal(160f, arm.X, 3);
        Assert.Equal(166f, arm.Y, 3);
    }
}
