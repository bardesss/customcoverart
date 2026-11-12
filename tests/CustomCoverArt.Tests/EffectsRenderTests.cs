using CustomCoverArt.Models;
using CustomCoverArt.Services;
using CustomCoverArt.Services.Rendering;
using System;
using SkiaSharp;
using Xunit;

namespace CustomCoverArt.Tests;

public class EffectsRenderTests
{
    private static SKBitmap Canvas(int w, int h, string hex) => RenderSurface.New(w, h, SkiaColors.Parse(hex, SKColors.Black));

    private static Rgba P(SKBitmap b, int x, int y) => TestPixels.Px(b, x, y);

    private static (int R, int G, int B) Rgb(SKBitmap b, int x, int y)
    {
        var p = P(b, x, y);
        return (p.R, p.G, p.B);
    }

    private static byte[] Bytes(SKBitmap b) => b.GetPixelSpan().ToArray();

    [Fact]
    public void Grain_IsDeterministicForSameSeed()
    {
        using var a = Canvas(64, 64, "#808080");
        using var b = Canvas(64, 64, "#808080");
        var g = new GrainSettings { Enabled = true, Amount = 0.3f, Seed = 999 };
        EffectsComposer.ApplyGrain(a, g);
        EffectsComposer.ApplyGrain(b, g);
        Assert.Equal(Bytes(a), Bytes(b)); // same seed => identical noise
    }

    /// <summary>A different seed must actually change the noise, or the seed is decorative.</summary>
    [Fact]
    public void Grain_DiffersBetweenSeeds()
    {
        using var a = Canvas(64, 64, "#808080");
        using var b = Canvas(64, 64, "#808080");
        EffectsComposer.ApplyGrain(a, new GrainSettings { Enabled = true, Amount = 0.3f, Seed = 1 });
        EffectsComposer.ApplyGrain(b, new GrainSettings { Enabled = true, Amount = 0.3f, Seed = 2 });

        var differing = 0;
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                if (P(a, x, y) != P(b, x, y)) { differing++; }
        Assert.True(differing > 1000, $"Different seeds should give different noise (differing={differing}).");
    }

    /// <summary>
    /// Pinned on the implementation that shipped before the SkiaSharp port (spec 7.1, Exact class: the arithmetic and the
    /// seeded walk are unchanged), so the Skia port must reproduce these bytes.
    /// </summary>
    [Fact]
    public void Grain_OnOpaqueCanvas_MatchesPinnedBytes()
    {
        using var img = Canvas(16, 16, "#808080");
        EffectsComposer.ApplyGrain(img, new GrainSettings { Enabled = true, Amount = 0.5f, Seed = 7 });
        Assert.Equal((120, 120, 120), Rgb(img, 0, 0));
        Assert.Equal((154, 154, 154), Rgb(img, 5, 3));
        Assert.Equal((108, 108, 108), Rgb(img, 15, 15));
        Assert.Equal((149, 149, 149), Rgb(img, 8, 8));
    }

    /// <summary>Pinned on the implementation that shipped before the SkiaSharp port (Exact class).</summary>
    [Fact]
    public void Vignette_OnOpaqueCanvas_MatchesPinnedBytes()
    {
        using var img = Canvas(32, 20, "#c0c0c0");
        EffectsComposer.ApplyVignette(img, new VignetteSettings { Enabled = true, Amount = 0.8f, Softness = 0.5f, Color = "#000000" });
        Assert.Equal((38, 38, 38), Rgb(img, 0, 0));
        Assert.Equal((192, 192, 192), Rgb(img, 16, 10));
        Assert.Equal((60, 60, 60), Rgb(img, 31, 19));
        Assert.Equal((133, 133, 133), Rgb(img, 4, 15));
    }

    /// <summary>Grain must not punch holes in the cover: alpha stays untouched.</summary>
    [Fact]
    public void Grain_PreservesAlpha()
    {
        using var img = Canvas(16, 16, "#808080");
        EffectsComposer.ApplyGrain(img, new GrainSettings { Enabled = true, Amount = 1f, Seed = 7 });
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                Assert.Equal(255, P(img, x, y).A);
    }

    /// <summary>
    /// On a translucent canvas the pixels are premultiplied, so a channel above alpha would be an invalid
    /// colour. Both per-pixel effects must cap channels at alpha, leave alpha alone and not throw.
    /// </summary>
    [Theory]
    [InlineData("vignette")]
    [InlineData("grain")]
    public void PerPixelEffects_OnPartialAlphaCanvas_NeverExceedAlpha(string effect)
    {
        using var img = RenderSurface.New(48, 48, new SKColor(200, 100, 50, 128));
        using var transparent = RenderSurface.New(48, 48, SKColors.Transparent);
        foreach (var bmp in new[] { img, transparent })
        {
            if (effect == "vignette")
            {
                EffectsComposer.ApplyVignette(bmp, new VignetteSettings { Enabled = true, Amount = 1f, Softness = 1f, Color = "#ffffff" });
            }
            else
            {
                EffectsComposer.ApplyGrain(bmp, new GrainSettings { Enabled = true, Amount = 1f, Seed = 11 });
            }
        }

        var span = img.GetPixelSpan();
        for (var i = 0; i < span.Length; i += 4)
        {
            Assert.Equal(128, span[i + 3]);
            Assert.True(span[i] <= 128 && span[i + 1] <= 128 && span[i + 2] <= 128, $"channel above alpha at byte {i}");
        }

        var t = transparent.GetPixelSpan();
        for (var i = 0; i < t.Length; i++)
        {
            Assert.Equal(0, t[i]); // fully transparent stays fully transparent
        }
    }

    [Fact]
    public void Grain_ZeroAmount_LeavesBytesUnchanged()
    {
        using var img = Canvas(30, 30, "#123456");
        var before = Bytes(img);
        EffectsComposer.ApplyGrain(img, new GrainSettings { Enabled = true, Amount = 0f, Seed = 5 });
        Assert.Equal(before, Bytes(img));
    }

    [Fact]
    public void Vignette_DarkensCornersMoreThanCenter()
    {
        using var img = Canvas(100, 100, "#ffffff");
        EffectsComposer.ApplyVignette(img, new VignetteSettings { Enabled = true, Amount = 0.8f, Softness = 0.4f });
        Assert.True(P(img, 0, 0).R < P(img, 50, 50).R, "Corner should be darker than center.");
    }

    /// <summary>Amount 0 is the identity: the effect being "enabled" must not tint anything by itself.</summary>
    [Fact]
    public void Vignette_ZeroAmount_LeavesImageUntouched()
    {
        using var img = Canvas(40, 40, "#ffffff");
        var before = Bytes(img);
        EffectsComposer.ApplyVignette(img, new VignetteSettings { Enabled = true, Amount = 0f, Softness = 0.5f });
        Assert.Equal(before, Bytes(img));
    }

    [Fact]
    public void Vignette_UnparseableColour_FallsBackToBlack()
    {
        using var img = Canvas(40, 40, "#ffffff");
        EffectsComposer.ApplyVignette(img, new VignetteSettings { Enabled = true, Amount = 1f, Softness = 1f, Color = "not-a-colour" });
        Assert.Equal((0, 0, 0), Rgb(img, 0, 0));
    }

    [Fact]
    public void Border_PaintsEdgePixels()
    {
        using var img = Canvas(100, 100, "#000000");
        EffectsComposer.DrawBorder(img, new BorderSettings { Enabled = true, Color = "#ff0000", Thickness = 5, Radius = 0 });
        Assert.True(P(img, 2, 50).R > 200, "Left edge should show the red border.");
        Assert.Equal((byte)0, P(img, 50, 50).R); // center untouched
    }

    /// <summary>A pixel-aligned square border has no antialiasing: exactly t pixels of solid colour on every side.</summary>
    [Fact]
    public void Border_Square_IsExactlyThicknessWide()
    {
        using var img = Canvas(60, 40, "#000000");
        EffectsComposer.DrawBorder(img, new BorderSettings { Enabled = true, Color = "#ff0000", Thickness = 5 });
        Assert.Equal((255, 0, 0), Rgb(img, 0, 20));
        Assert.Equal((255, 0, 0), Rgb(img, 4, 20));
        Assert.Equal((0, 0, 0), Rgb(img, 5, 20));
        Assert.Equal((255, 0, 0), Rgb(img, 59, 20));
        Assert.Equal((255, 0, 0), Rgb(img, 30, 4));
        Assert.Equal((0, 0, 0), Rgb(img, 30, 5));
        Assert.Equal((255, 0, 0), Rgb(img, 30, 39));
        Assert.Equal((255, 0, 0), Rgb(img, 0, 0)); // square corner is filled
    }

    [Fact]
    public void Border_UnparseableColour_FallsBackToWhite()
    {
        using var img = Canvas(40, 40, "#000000");
        EffectsComposer.DrawBorder(img, new BorderSettings { Enabled = true, Color = "zzz", Thickness = 4 });
        Assert.Equal((255, 255, 255), Rgb(img, 1, 20));
    }

    /// <summary>A rounded border must clear the very corner while still painting the edge mid-span.</summary>
    [Fact]
    public void Border_WithRadius_LeavesTheCornerClear()
    {
        using var img = Canvas(100, 100, "#000000");
        EffectsComposer.DrawBorder(img, new BorderSettings { Enabled = true, Color = "#ff0000", Thickness = 4, Radius = 20 });
        Assert.True(P(img, 2, 50).R > 200, "Mid-edge should still be painted.");
        Assert.True(P(img, 1, 1).R < 60, "The square corner should be outside a rounded frame.");
    }

    /// <summary>A radius far beyond half the short side clamps to a pill: no throw, corners clear, edges painted, middle untouched.</summary>
    [Fact]
    public void Border_RadiusBeyondHalfTheSide_ClampsToAPill()
    {
        using var img = Canvas(100, 60, "#000000");
        EffectsComposer.DrawBorder(img, new BorderSettings { Enabled = true, Color = "#ff0000", Thickness = 4, Radius = 9999 });
        Assert.True(P(img, 1, 1).R < 60, "Corner is outside the pill.");
        Assert.True(P(img, 50, 1).R > 200, "Top edge mid-span is painted.");
        Assert.True(P(img, 1, 30).R > 200, "Left cap is painted.");
        Assert.Equal((0, 0, 0), Rgb(img, 50, 30));
    }

    [Fact]
    public void Border_ThicknessAbove64_IsClampedTo64()
    {
        using var img = Canvas(200, 200, "#000000");
        EffectsComposer.DrawBorder(img, new BorderSettings { Enabled = true, Color = "#ff0000", Thickness = 5000 });
        Assert.Equal((255, 0, 0), Rgb(img, 63, 100));
        Assert.Equal((0, 0, 0), Rgb(img, 64, 100));
    }

    /// <summary>Negative and zero thickness draw nothing at all.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-7)]
    public void Border_NonPositiveThickness_DrawsNothing(int thickness)
    {
        using var img = Canvas(50, 50, "#336699");
        var before = Bytes(img);
        EffectsComposer.DrawBorder(img, new BorderSettings { Enabled = true, Color = "#ff0000", Thickness = thickness });
        Assert.Equal(before, Bytes(img));
    }

    /// <summary>
    /// A border wider than the canvas leaves no centre line to stroke (the inset path collapses), so it draws
    /// nothing rather than throwing, exactly as the earlier implementation did.
    /// </summary>
    [Fact]
    public void Border_WiderThanTheCanvas_DoesNotThrowAndDrawsNothing()
    {
        using var img = Canvas(40, 40, "#336699");
        var before = Bytes(img);
        EffectsComposer.DrawBorder(img, new BorderSettings { Enabled = true, Color = "#ff0000", Thickness = 64, Double = true, Gap = 32 });
        Assert.Equal(before, Bytes(img));
    }

    /// <summary>Wider than half the canvas but still with a path: the stroke swallows the whole canvas.</summary>
    [Fact]
    public void Border_WiderThanHalfTheCanvas_CoversTheCanvas()
    {
        using var img = Canvas(100, 100, "#000000");
        EffectsComposer.DrawBorder(img, new BorderSettings { Enabled = true, Color = "#ff0000", Thickness = 64 });
        Assert.Equal((255, 0, 0), Rgb(img, 50, 50));
        Assert.Equal((255, 0, 0), Rgb(img, 0, 0));
        Assert.Equal((255, 0, 0), Rgb(img, 99, 99));
    }

    [Fact]
    public void Border_Double_PaintsASecondInnerLine()
    {
        using var single = Canvas(100, 100, "#000000");
        EffectsComposer.DrawBorder(single, new BorderSettings { Enabled = true, Color = "#ff0000", Thickness = 4, Gap = 6 });
        using var dbl = Canvas(100, 100, "#000000");
        EffectsComposer.DrawBorder(dbl, new BorderSettings { Enabled = true, Color = "#ff0000", Thickness = 4, Gap = 6, Double = true });

        int Red(SKBitmap i)
        {
            var n = 0;
            for (var y = 0; y < 100; y++)
                for (var x = 0; x < 100; x++)
                    if (P(i, x, y).R > 200) { n++; }
            return n;
        }

        Assert.True(Red(dbl) > Red(single), "The double border should paint strictly more pixels.");
    }

    /// <summary>
    /// Double geometry: an outer ring of t, a gap, then a ring of t/2 starting at t + gap. They are two separate
    /// rings with background showing between them and inside the inner one.
    /// </summary>
    [Fact]
    public void Border_Double_IsTwoSeparateRingsWithAGap()
    {
        using var img = Canvas(100, 100, "#000000");
        EffectsComposer.DrawBorder(img, new BorderSettings { Enabled = true, Color = "#ff0000", Thickness = 6, Gap = 4, Double = true });
        Assert.Equal((255, 0, 0), Rgb(img, 0, 50));
        Assert.Equal((255, 0, 0), Rgb(img, 5, 50)); // outer ring is 0..5
        Assert.Equal((0, 0, 0), Rgb(img, 6, 50));   // gap 6..9
        Assert.Equal((0, 0, 0), Rgb(img, 9, 50));
        Assert.Equal((255, 0, 0), Rgb(img, 10, 50)); // inner ring 10..12 (thickness 3)
        Assert.Equal((255, 0, 0), Rgb(img, 12, 50));
        Assert.Equal((0, 0, 0), Rgb(img, 13, 50));
        Assert.Equal((0, 0, 0), Rgb(img, 50, 50));
        Assert.Equal((255, 0, 0), Rgb(img, 50, 11)); // same on the top edge
        Assert.Equal((0, 0, 0), Rgb(img, 50, 8));
    }

    /// <summary>A second frame is skipped when the first leaves no room inside it.</summary>
    [Fact]
    public void Border_Double_SkipsTheInnerLineWhenItDoesNotFit()
    {
        using var single = Canvas(30, 30, "#000000");
        EffectsComposer.DrawBorder(single, new BorderSettings { Enabled = true, Color = "#ff0000", Thickness = 10, Gap = 6 });
        using var dbl = Canvas(30, 30, "#000000");
        EffectsComposer.DrawBorder(dbl, new BorderSettings { Enabled = true, Color = "#ff0000", Thickness = 10, Gap = 6, Double = true });
        Assert.Equal(Bytes(single), Bytes(dbl)); // inset 16 * 2 >= 30
    }

    [Fact]
    public void SoftLight_TintsTowardsItsColour()
    {
        using var img = Canvas(20, 20, "#000000");
        EffectsComposer.ApplySoftLight(img, new SoftLightSettings { Enabled = true, Color = "#ffffff", Opacity = 0.5f });
        Assert.InRange(P(img, 10, 10).R, 100, 160);
    }

    /// <summary>
    /// Pins the soft-light arithmetic (1/255f scaling, fused multiply-add, round half to even). Expected values
    /// were captured from the pre-port renderer's DrawImage(overlay, opacity) over an opaque backdrop (spec 7.1, Exact);
    /// the first row is corpus case 07.
    /// </summary>
    [Theory]
    [InlineData("#c0392b", "#ffe9a8", 0.3f, 211, 110, 80)]
    [InlineData("#000000", "#ffffff", 0.5f, 128, 128, 128)]
    [InlineData("#1e6ec8", "#ff8000", 0.25f, 86, 114, 150)]
    [InlineData("#ffffff", "#0a141e", 0.7f, 84, 90, 98)]
    [InlineData("#1163c9", "#c83296", 0.05f, 26, 97, 198)]
    [InlineData("#783cf0", "#010203", 0.9f, 13, 8, 27)]
    [InlineData("#333333", "#ff00ff", 0.55f, 163, 23, 163)]
    public void SoftLight_FractionalOpacity_MatchesPinnedBytes(string backdrop, string colour, float opacity, int r, int g, int b)
    {
        using var img = Canvas(24, 8, backdrop);
        EffectsComposer.ApplySoftLight(img, new SoftLightSettings { Enabled = true, Color = colour, Opacity = opacity });
        Assert.Equal((r, g, b), Rgb(img, 3, 3));
        Assert.Equal(255, P(img, 3, 3).A);
    }

    /// <summary>On a translucent (premultiplied) canvas the wash raises alpha as SrcOver does and keeps RGB &lt;= A.</summary>
    [Fact]
    public void SoftLight_OnTranslucentCanvas_RaisesAlphaAndKeepsChannelsWithinIt()
    {
        using var img = RenderSurface.New(10, 10, new SKColor(200, 100, 50, 128));
        EffectsComposer.ApplySoftLight(img, new SoftLightSettings { Enabled = true, Color = "#ffffff", Opacity = 0.5f });
        var span = img.GetPixelSpan();
        for (var i = 0; i < span.Length; i += 4)
        {
            Assert.InRange(span[i + 3], 191, 192); // 0.5 + 0.502 * 0.5 = 0.751
            Assert.True(span[i] <= span[i + 3] && span[i + 1] <= span[i + 3] && span[i + 2] <= span[i + 3]);
        }

        Assert.InRange(span[0], 176, 178); // premultiplied: 1 * 0.5 + (100/255) * 0.5
    }

    /// <summary>Vignette colour is premultiplied on translucent pixels: half-way to white lands at 0.5*base + 0.5*alpha.</summary>
    [Fact]
    public void Vignette_OnTranslucentPixel_BlendsAPremultipliedColour()
    {
        using var img = RenderSurface.New(40, 40, new SKColor(200, 100, 50, 128)); // premul 100, 50, 25
        EffectsComposer.ApplyVignette(img, new VignetteSettings { Enabled = true, Amount = 0.5f, Softness = 1f, Color = "#ffffff" });
        var s = img.GetPixelSpan(); // pixel (0,0): d = 1, edge = 1, a = 0.5
        Assert.InRange(s[0], 113, 115); // 100*0.5 + 128*0.5
        Assert.InRange(s[1], 88, 90);   // 50*0.5 + 64
        Assert.InRange(s[2], 75, 77);   // 25*0.5 + 64
        Assert.Equal(128, s[3]);
    }

    /// <summary>Grain on a half-covered pixel moves premultiplied channels by about half as much as on an opaque one.</summary>
    [Fact]
    public void Grain_OnTranslucentPixels_ScalesNoiseWithAlpha()
    {
        using var img = RenderSurface.New(32, 32, new SKColor(200, 200, 200, 128)); // premul 100
        EffectsComposer.ApplyGrain(img, new GrainSettings { Enabled = true, Amount = 1f, Seed = 5 });
        var s = img.GetPixelSpan();
        var maxDelta = 0;
        for (var i = 0; i < s.Length; i += 4)
        {
            maxDelta = Math.Max(maxDelta, Math.Abs(s[i] - 100));
            Assert.Equal(s[i], s[i + 1]);
            Assert.Equal(128, s[i + 3]);
        }

        Assert.InRange(maxDelta, 20, 33); // full-alpha noise reaches 64; half alpha reaches 32
    }

    [Fact]
    public void Border_OnTranslucentCanvas_PaintsOpaqueEdgeAndLeavesTheInsideAlone()
    {
        using var img = RenderSurface.New(40, 40, new SKColor(200, 100, 50, 128));
        var centre = Bytes(img).AsSpan(((20 * 40) + 20) * 4, 4).ToArray();
        EffectsComposer.DrawBorder(img, new BorderSettings { Enabled = true, Color = "#ff0000", Thickness = 4 });
        Assert.Equal(new Rgba(255, 0, 0, 255), P(img, 1, 20));
        Assert.Equal(centre, Bytes(img).AsSpan(((20 * 40) + 20) * 4, 4).ToArray());
    }

    /// <summary>Odd thickness with a radius and a double line: inner ring is t/2 wide and its radius shrinks by the inset.</summary>
    [Fact]
    public void Border_Double_WithRadiusAndOddThickness()
    {
        using var img = Canvas(100, 100, "#000000");
        EffectsComposer.DrawBorder(img, new BorderSettings { Enabled = true, Color = "#ff0000", Thickness = 5, Gap = 3, Double = true, Radius = 20 });
        Assert.Equal((255, 0, 0), Rgb(img, 4, 50));  // outer ring 0..4
        Assert.Equal((0, 0, 0), Rgb(img, 6, 50));    // gap 5..7
        Assert.Equal((255, 0, 0), Rgb(img, 8, 50));  // inner ring 8..9 (thickness 2)
        Assert.Equal((255, 0, 0), Rgb(img, 9, 50));
        Assert.Equal((0, 0, 0), Rgb(img, 11, 50));
        Assert.Equal((0, 0, 0), Rgb(img, 0, 0));     // outer corner rounded away

        // The corner block holds fewer red pixels than the same frame with square corners.
        using var square = Canvas(100, 100, "#000000");
        EffectsComposer.DrawBorder(square, new BorderSettings { Enabled = true, Color = "#ff0000", Thickness = 5, Gap = 3, Double = true, Radius = 0 });
        int Red(SKBitmap b)
        {
            var n = 0;
            for (var y = 0; y < 14; y++)
                for (var x = 0; x < 14; x++)
                    if (P(b, x, y).R > 128) { n++; }
            return n;
        }

        Assert.True(Red(img) < Red(square) - 20, $"rounded {Red(img)} vs square {Red(square)}");
    }

    [Fact]
    public void SoftLight_FullOpacity_ReplacesTheCanvasColour()
    {
        using var img = Canvas(20, 20, "#102030");
        EffectsComposer.ApplySoftLight(img, new SoftLightSettings { Enabled = true, Color = "#ff8000", Opacity = 1f });
        Assert.Equal((255, 128, 0), Rgb(img, 0, 0));
        Assert.Equal((255, 128, 0), Rgb(img, 19, 19));
        Assert.Equal(255, P(img, 5, 5).A);
    }

    [Fact]
    public void SoftLight_DisabledOrZeroOpacity_LeavesBytesUnchanged()
    {
        using var img = Canvas(20, 20, "#102030");
        var before = Bytes(img);
        EffectsComposer.ApplySoftLight(img, new SoftLightSettings { Enabled = false, Color = "#ffffff", Opacity = 1f });
        EffectsComposer.ApplySoftLight(img, new SoftLightSettings { Enabled = true, Color = "#ffffff", Opacity = 0f });
        Assert.Equal(before, Bytes(img));
    }

    /// <summary>
    /// <see cref="EffectsComposer.Apply"/> runs vignette, then grain, then border. Grain-then-vignette gives other
    /// bytes (the clamp at 0 and the blend do not commute), so equality with the manual sequence pins the order.
    /// </summary>
    [Fact]
    public void Apply_RunsVignetteThenGrainThenBorder()
    {
        var fx = new EffectSettings
        {
            Vignette = new VignetteSettings { Enabled = true, Amount = 1f, Softness = 1f, Color = "#000000" },
            Grain = new GrainSettings { Enabled = true, Amount = 1f, Seed = 3 },
            Border = new BorderSettings { Enabled = true, Color = "#00ff00", Thickness = 6 },
        };

        using var applied = Canvas(64, 64, "#ffffff");
        EffectsComposer.Apply(applied, fx);

        using var manual = Canvas(64, 64, "#ffffff");
        EffectsComposer.ApplyVignette(manual, fx.Vignette);
        EffectsComposer.ApplyGrain(manual, fx.Grain);
        EffectsComposer.DrawBorder(manual, fx.Border);
        Assert.Equal(Bytes(manual), Bytes(applied));

        using var swapped = Canvas(64, 64, "#ffffff");
        EffectsComposer.ApplyGrain(swapped, fx.Grain);
        EffectsComposer.ApplyVignette(swapped, fx.Vignette);
        EffectsComposer.DrawBorder(swapped, fx.Border);
        Assert.NotEqual(Bytes(swapped), Bytes(applied));

        // Border outermost: its pixels are the pure colour, with no grain or vignette on top.
        Assert.Equal((0, 255, 0), Rgb(applied, 2, 32));
        Assert.Equal((0, 255, 0), Rgb(applied, 0, 0));
    }

    [Fact]
    public void Apply_SkipsDisabledEffects()
    {
        using var img = Canvas(40, 40, "#445566");
        var before = Bytes(img);
        EffectsComposer.Apply(img, new EffectSettings
        {
            Vignette = new VignetteSettings { Enabled = false, Amount = 1f },
            Grain = new GrainSettings { Enabled = false, Amount = 1f },
            Border = new BorderSettings { Enabled = false, Thickness = 10 },
        });
        Assert.Equal(before, Bytes(img));
    }

    /// <summary>
    /// Order is the contract the client mirrors: soft-light sits UNDER the layers,
    /// the border goes on top of everything. Drawing text over an opaque soft-light
    /// wash proves the wash came first; an opaque border covering the same pixel at
    /// the edge proves the border came last.
    /// </summary>
    [Fact]
    public void ComposeDocumentFrame_SoftLightUnderLayers_BorderOverEverything()
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 120, Height = 120 } };
        doc.Background.DimColor = "#000000";
        doc.Effects.SoftLight = new SoftLightSettings { Enabled = true, Color = "#ff0000", Opacity = 1f };
        doc.Effects.Border = new BorderSettings { Enabled = true, Color = "#00ff00", Thickness = 10, Radius = 0 };
        doc.Layers.Add(new CoverLayer { Type = "text", Content = "HHHH", Color = "#ffffff", Size = 0.5f, X = 0.5f, Y = 0.5f });

        using var canvas = RenderSurface.New(120, 120);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        // Border wins at the edge.
        var edge = TestPixels.Px(canvas, 3, 60);
        Assert.True(edge.G > 200 && edge.R < 60, "Border must be drawn over the soft-light wash.");

        // Text is visible on top of an opaque red wash somewhere in the middle band.
        var whiteFound = false;
        for (var y = 40; y < 80 && !whiteFound; y++)
            for (var x = 20; x < 100; x++)
                if (TestPixels.Px(canvas, x, y) is { R: > 200, G: > 200, B: > 200 }) { whiteFound = true; break; }
        Assert.True(whiteFound, "Layers must be drawn over the soft-light wash.");
    }

    /// <summary>
    /// Vignette runs after the layers and before the border: with a black full-strength vignette over a white
    /// text layer's background the corner is black, and the border colour is untouched by the vignette.
    /// </summary>
    [Fact]
    public void ComposeDocumentFrame_VignetteOverLayers_BorderOverVignette()
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 100, Height = 100 } };
        doc.Background.DimColor = "#ffffff";
        doc.Effects.Vignette = new VignetteSettings { Enabled = true, Amount = 1f, Softness = 1f, Color = "#000000" };
        doc.Effects.Border = new BorderSettings { Enabled = true, Color = "#ffffff", Thickness = 8 };

        using var canvas = RenderSurface.New(100, 100);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        Assert.Equal(new Rgba(255, 255, 255, 255), TestPixels.Px(canvas, 0, 0)); // border over the vignetted corner
        Assert.Equal(new Rgba(255, 255, 255, 255), TestPixels.Px(canvas, 4, 50));
        // Just inside the border the vignette has darkened the white background.
        Assert.True(TestPixels.Px(canvas, 8, 50).R < 255 - 40, $"vignette expected under the border's inner edge, got {TestPixels.Px(canvas, 8, 50).R}");
    }

    /// <summary>
    /// Effects that are switched ON but turned all the way DOWN must be byte-for-byte
    /// identity. Without the zero guards, "enabled at amount 0" still runs a blend pass
    /// and the rounding drifts the image, so a user dragging a slider back to 0 would
    /// not get their original cover back.
    /// </summary>
    [Fact]
    public void ComposeDocumentFrame_EnabledButZeroedEffects_ChangeNothing()
    {
        CoverDocument Doc()
        {
            var d = new CoverDocument { Canvas = new CanvasSettings { Width = 80, Height = 80 } };
            d.Background.DimColor = "#123456";
            d.Layers.Add(new CoverLayer { Type = "text", Content = "AB", Color = "#ffffff", Size = 0.3f });
            return d;
        }

        using var baseline = RenderSurface.New(80, 80);
        DocumentRenderer.ComposeDocumentFrame(baseline, null, Doc());

        var zeroed = Doc();
        zeroed.Effects = new EffectSettings
        {
            Border = new BorderSettings { Enabled = true, Thickness = 0 },
            Vignette = new VignetteSettings { Enabled = true, Amount = 0f },
            Grain = new GrainSettings { Enabled = true, Amount = 0f },
            SoftLight = new SoftLightSettings { Enabled = true, Opacity = 0f }
        };
        using var again = RenderSurface.New(80, 80);
        DocumentRenderer.ComposeDocumentFrame(again, null, zeroed);

        Assert.Equal(Bytes(baseline), Bytes(again));
    }
}
