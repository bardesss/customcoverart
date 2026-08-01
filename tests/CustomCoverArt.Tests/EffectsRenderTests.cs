using CustomCoverArt.Models;
using CustomCoverArt.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CustomCoverArt.Tests;

public class EffectsRenderTests
{
    [Fact]
    public void Grain_IsDeterministicForSameSeed()
    {
        using var a = new Image<Rgba32>(64, 64, Color.Gray);
        using var b = new Image<Rgba32>(64, 64, Color.Gray);
        var g = new GrainSettings { Enabled = true, Amount = 0.3f, Seed = 999 };
        EffectsComposer.ApplyGrain(a, g);
        EffectsComposer.ApplyGrain(b, g);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                Assert.Equal(a[x, y], b[x, y]); // same seed => identical noise
    }

    /// <summary>A different seed must actually change the noise, or the seed is decorative.</summary>
    [Fact]
    public void Grain_DiffersBetweenSeeds()
    {
        using var a = new Image<Rgba32>(64, 64, Color.Gray);
        using var b = new Image<Rgba32>(64, 64, Color.Gray);
        EffectsComposer.ApplyGrain(a, new GrainSettings { Enabled = true, Amount = 0.3f, Seed = 1 });
        EffectsComposer.ApplyGrain(b, new GrainSettings { Enabled = true, Amount = 0.3f, Seed = 2 });

        var differing = 0;
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                if (a[x, y] != b[x, y]) { differing++; }
        Assert.True(differing > 1000, $"Different seeds should give different noise (differing={differing}).");
    }

    /// <summary>Grain must not punch holes in the cover — alpha stays untouched.</summary>
    [Fact]
    public void Grain_PreservesAlpha()
    {
        using var img = new Image<Rgba32>(16, 16, Color.Gray);
        EffectsComposer.ApplyGrain(img, new GrainSettings { Enabled = true, Amount = 1f, Seed = 7 });
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                Assert.Equal(255, img[x, y].A);
    }

    [Fact]
    public void Vignette_DarkensCornersMoreThanCenter()
    {
        using var img = new Image<Rgba32>(100, 100, Color.White);
        EffectsComposer.ApplyVignette(img, new VignetteSettings { Enabled = true, Amount = 0.8f, Softness = 0.4f });
        Assert.True(img[0, 0].R < img[50, 50].R, "Corner should be darker than center.");
    }

    /// <summary>Amount 0 is the identity: the effect being "enabled" must not tint anything by itself.</summary>
    [Fact]
    public void Vignette_ZeroAmount_LeavesImageUntouched()
    {
        using var img = new Image<Rgba32>(40, 40, Color.White);
        EffectsComposer.ApplyVignette(img, new VignetteSettings { Enabled = true, Amount = 0f, Softness = 0.5f });
        for (int y = 0; y < 40; y++)
            for (int x = 0; x < 40; x++)
                Assert.Equal(255, img[x, y].R);
    }

    [Fact]
    public void Border_PaintsEdgePixels()
    {
        using var img = new Image<Rgba32>(100, 100, Color.Black);
        EffectsComposer.DrawBorder(img, new BorderSettings { Enabled = true, Color = "#ff0000", Thickness = 5, Radius = 0 });
        Assert.True(img[2, 50].R > 200, "Left edge should show the red border.");
        Assert.Equal((byte)0, img[50, 50].R); // center untouched
    }

    /// <summary>A rounded border must clear the very corner while still painting the edge mid-span.</summary>
    [Fact]
    public void Border_WithRadius_LeavesTheCornerClear()
    {
        using var img = new Image<Rgba32>(100, 100, Color.Black);
        EffectsComposer.DrawBorder(img, new BorderSettings { Enabled = true, Color = "#ff0000", Thickness = 4, Radius = 20 });
        Assert.True(img[2, 50].R > 200, "Mid-edge should still be painted.");
        Assert.True(img[1, 1].R < 60, "The square corner should be outside a rounded frame.");
    }

    [Fact]
    public void Border_Double_PaintsASecondInnerLine()
    {
        using var single = new Image<Rgba32>(100, 100, Color.Black);
        EffectsComposer.DrawBorder(single, new BorderSettings { Enabled = true, Color = "#ff0000", Thickness = 4, Gap = 6 });
        using var dbl = new Image<Rgba32>(100, 100, Color.Black);
        EffectsComposer.DrawBorder(dbl, new BorderSettings { Enabled = true, Color = "#ff0000", Thickness = 4, Gap = 6, Double = true });

        int Red(Image<Rgba32> i)
        {
            var n = 0;
            for (var y = 0; y < 100; y++)
                for (var x = 0; x < 100; x++)
                    if (i[x, y].R > 200) { n++; }
            return n;
        }

        Assert.True(Red(dbl) > Red(single), "The double border should paint strictly more pixels.");
    }

    [Fact]
    public void SoftLight_TintsTowardsItsColour()
    {
        using var img = new Image<Rgba32>(20, 20, Color.Black);
        EffectsComposer.ApplySoftLight(img, new SoftLightSettings { Enabled = true, Color = "#ffffff", Opacity = 0.5f });
        Assert.InRange(img[10, 10].R, 100, 160);
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

        using var canvas = new Image<Rgba32>(120, 120);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        // Border wins at the edge.
        Assert.True(canvas[3, 60].G > 200 && canvas[3, 60].R < 60, "Border must be drawn over the soft-light wash.");

        // Text is visible on top of an opaque red wash somewhere in the middle band.
        var whiteFound = false;
        for (var y = 40; y < 80 && !whiteFound; y++)
            for (var x = 20; x < 100; x++)
                if (canvas[x, y].R > 200 && canvas[x, y].G > 200 && canvas[x, y].B > 200) { whiteFound = true; break; }
        Assert.True(whiteFound, "Layers must be drawn over the soft-light wash.");
    }

    /// <summary>
    /// Effects that are switched ON but turned all the way DOWN must be byte-for-byte
    /// identity. Without the zero guards, "enabled at amount 0" still runs a blend pass
    /// and the rounding drifts the image — so a user dragging a slider back to 0 would
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

        using var baseline = new Image<Rgba32>(80, 80);
        DocumentRenderer.ComposeDocumentFrame(baseline, null, Doc());

        var zeroed = Doc();
        zeroed.Effects = new EffectSettings
        {
            Border = new BorderSettings { Enabled = true, Thickness = 0 },
            Vignette = new VignetteSettings { Enabled = true, Amount = 0f },
            Grain = new GrainSettings { Enabled = true, Amount = 0f },
            SoftLight = new SoftLightSettings { Enabled = true, Opacity = 0f }
        };
        using var again = new Image<Rgba32>(80, 80);
        DocumentRenderer.ComposeDocumentFrame(again, null, zeroed);

        for (var y = 0; y < 80; y++)
            for (var x = 0; x < 80; x++)
                Assert.Equal(baseline[x, y], again[x, y]);
    }
}
