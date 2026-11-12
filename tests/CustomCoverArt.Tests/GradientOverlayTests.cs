using System.Text.Json;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using CustomCoverArt.Services.Rendering;
using SkiaSharp;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// The background gradient overlay: a linear multi-stop gradient with per-stop alpha,
/// composited over the finished background and under the layers. The alpha is the whole
/// point, and these pin that it survives compositing rather than being flattened to opaque.
/// </summary>
public class GradientOverlayTests
{
    [Fact]
    public void BuildGradientStops_AppliesStopAlpha()
    {
        var g = new GradientSettings
        {
            Stops = new()
            {
                new GradientStop { Color = "#ffffff", Position = 0f, Alpha = 0f },
                new GradientStop { Color = "#ffffff", Position = 1f, Alpha = 0.5f }
            }
        };

        var stops = DocumentRenderer.BuildGradientStops(g);

        Assert.Equal(0, stops.Colors[0].Alpha);
        // 0.5f * 255 = 127.5; the float->byte rounding lands on 127 or 128.
        // A wide band here would also pass a wrong scale factor (e.g. treating Alpha as
        // already 0..255, or applying it to the wrong channel), so this is deliberately
        // tight around the two rounding candidates rather than a generous margin.
        Assert.InRange(stops.Colors[1].Alpha, 127, 128);
    }

    /// <summary>
    /// Out-of-range Alpha must saturate rather than wrap or throw. BuildGradientStops clamps it
    /// (through <see cref="SkiaColors.WithAlpha"/>); a byte cast of 2 * 255 would wrap instead.
    /// </summary>
    [Fact]
    public void BuildGradientStops_OutOfRangeAlpha_SaturatesToOpaqueAndTransparent()
    {
        var g = new GradientSettings
        {
            Stops = new()
            {
                new GradientStop { Color = "#ffffff", Position = 0f, Alpha = 2f },
                new GradientStop { Color = "#ffffff", Position = 1f, Alpha = -1f }
            }
        };

        var stops = DocumentRenderer.BuildGradientStops(g);

        Assert.Equal(255, stops.Colors[0].Alpha);
        Assert.Equal(0, stops.Colors[1].Alpha);
    }

    /// <summary>Alpha defaults to 1, so every gradient written before overlays existed is opaque.</summary>
    [Fact]
    public void BuildGradientStops_DefaultAlpha_IsFullyOpaque()
    {
        var g = new GradientSettings
        {
            Stops = new()
            {
                new GradientStop { Color = "#ff0000", Position = 0f },
                new GradientStop { Color = "#0000ff", Position = 1f }
            }
        };

        var stops = DocumentRenderer.BuildGradientStops(g);

        Assert.Equal(255, stops.Colors[0].Alpha);
        Assert.Equal(255, stops.Colors[1].Alpha);
    }

    /// <summary>
    /// forceLinear is what keeps an overlay linear regardless of the Type field it inherits
    /// from the reused GradientSettings type. At angle 0 a linear ramp runs left to right, so
    /// every column is one colour top to bottom; a radial fill centred mid-canvas would not be.
    /// </summary>
    [Fact]
    public void CreateGradientShader_ForceLinear_IgnoresRadialType()
    {
        var g = new GradientSettings
        {
            Type = GradientType.Radial,
            Angle = 0f,
            CenterX = 0.5f,
            CenterY = 0.5f,
            Radius = 0.5f,
            Stops = new()
            {
                new GradientStop { Color = "#000000", Position = 0f },
                new GradientStop { Color = "#ffffff", Position = 1f }
            }
        };

        using var linear = GradientShaderTests.Fill(g, 40, 40, forceLinear: true);

        for (var x = 0; x < 40; x++)
        {
            var top = TestPixels.Px(linear, x, 0);
            for (var y = 1; y < 40; y++)
            {
                Assert.Equal(top, TestPixels.Px(linear, x, y));
            }
        }

        Assert.True(TestPixels.Px(linear, 0, 20).R < 15, "Left edge should be the near (black) stop.");
        Assert.True(TestPixels.Px(linear, 39, 20).R > 240, "Right edge should be the far (white) stop.");

        // Control: without forceLinear the same settings are radial, so a column is not uniform.
        using var radial = GradientShaderTests.Fill(g, 40, 40);
        Assert.NotEqual(TestPixels.Px(radial, 20, 0), TestPixels.Px(radial, 20, 20));
    }

    /// <summary>A 40x40 document whose background is solid white, with no layers.</summary>
    private static CoverDocument WhiteDoc()
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 40, Height = 40 } };
        doc.Background.Source = BackgroundSources.Solid;
        doc.Background.DimColor = "#ffffff";
        return doc;
    }

    /// <summary>A bottom-fade overlay: transparent at the top, fully opaque black at the bottom.</summary>
    private static GradientSettings BottomFadeBlack() => new()
    {
        IsEnabled = true,
        Angle = 90f,
        Stops = new()
        {
            new GradientStop { Color = "#000000", Position = 0f, Alpha = 0f },
            new GradientStop { Color = "#000000", Position = 1f, Alpha = 1f }
        }
    };

    /// <summary>
    /// THE important one. Filling the canvas directly with a semi-transparent brush is the
    /// trap that once blacked out dimmed backgrounds: a fill that ignores brush alpha
    /// on alpha-less pixel formats paints it opaque. If that happens here the middle row goes solid black
    /// instead of mid-grey, so this assertion is the regression guard for the whole feature.
    /// </summary>
    [Fact]
    public void ApplyGradientOverlay_RampsAlphaAcrossTheCanvas()
    {
        var doc = WhiteDoc();
        doc.Background.Overlay = BottomFadeBlack();

        using var canvas = RenderSurface.New(40, 40);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        Assert.True(TestPixels.Px(canvas, 20, 0).R > 230, "Top must stay near the white background.");
        Assert.True(TestPixels.Px(canvas, 20, 39).R < 25, "Bottom must reach the opaque overlay colour.");
        Assert.InRange(TestPixels.Px(canvas, 20, 20).R, 90, 165);   // a genuine blend, not 0 and not 255
    }

    [Fact]
    public void ApplyGradientOverlay_NullOrDisabled_LeavesTheCanvasUntouched()
    {
        using var withoutOverlay = RenderSurface.New(40, 40);
        DocumentRenderer.ComposeDocumentFrame(withoutOverlay, null, WhiteDoc());

        var disabledDoc = WhiteDoc();
        disabledDoc.Background.Overlay = BottomFadeBlack();
        disabledDoc.Background.Overlay.IsEnabled = false;

        using var withDisabled = RenderSurface.New(40, 40);
        DocumentRenderer.ComposeDocumentFrame(withDisabled, null, disabledDoc);

        Assert.Equal(TestPixels.Bytes(withoutOverlay), TestPixels.Bytes(withDisabled));
    }

    /// <summary>
    /// Guards the fallback divergence: BuildColorStops falls back to an opaque ramp between
    /// StartColor/EndColor (Jellyfin brand purple/blue by default) when there are fewer than
    /// two stops, which for an overlay would obliterate the background. Fewer than two stops
    /// must mean "off" instead.
    /// </summary>
    [Fact]
    public void ApplyGradientOverlay_FewerThanTwoStops_IsANoOp()
    {
        var doc = WhiteDoc();
        doc.Background.Overlay = new GradientSettings
        {
            IsEnabled = true,
            Angle = 90f,
            Stops = new() { new GradientStop { Color = "#000000", Position = 1f, Alpha = 1f } }
        };

        using var canvas = RenderSurface.New(40, 40);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        Assert.True(TestPixels.Px(canvas, 20, 39).R > 230, "A one-stop overlay must not paint anything.");
    }

    /// <summary>
    /// The overlay lives in ComposeDocumentFrame, not inside ApplyBackgroundLayer, precisely
    /// so it works for every source. ApplyBackgroundLayer runs only on the image path, which
    /// ComposeDocumentFrame selects by whether a background bitmap was passed in,
    /// not by <c>Source</c>. So the Upload case must actually be given one, or that path
    /// (and the asymmetry this test exists to guard) never runs.
    /// </summary>
    [Theory]
    [InlineData(BackgroundSources.Solid)]
    [InlineData(BackgroundSources.Gradient)]
    [InlineData(BackgroundSources.Collage)]
    [InlineData(BackgroundSources.Upload)]
    public void ApplyGradientOverlay_AppliesToEverySource(string source)
    {
        var doc = WhiteDoc();
        doc.Background.Source = source;
        doc.Background.Gradient = new GradientSettings
        {
            IsEnabled = true,
            Stops = new() { new GradientStop { Color = "#ffffff", Position = 0f },
                            new GradientStop { Color = "#ffffff", Position = 1f } }
        };
        doc.Background.Overlay = BottomFadeBlack();

        // Only Upload goes through ApplyBackgroundLayer (the image path); the other three
        // sources render via CreateGradientBackground with no background image at all.
        using SKBitmap? background = source == BackgroundSources.Upload
            ? RenderSurface.New(10, 10, SKColors.White)
            : null;

        using var canvas = RenderSurface.New(40, 40);
        DocumentRenderer.ComposeDocumentFrame(canvas, background, doc);

        Assert.True(TestPixels.Px(canvas, 20, 39).R < 25, $"Overlay must apply for source '{source}'.");
    }

    /// <summary>Type is inert on an overlay: a radial one still renders as a linear ramp.</summary>
    [Fact]
    public void ApplyGradientOverlay_RadialType_StillRendersLinear()
    {
        var doc = WhiteDoc();
        doc.Background.Overlay = BottomFadeBlack();
        doc.Background.Overlay.Type = GradientType.Radial;

        using var canvas = RenderSurface.New(40, 40);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        // Under a linear 90-degree ramp the whole bottom row is opaque, corners included.
        // A radial brush would leave the bottom corners far lighter than the bottom centre.
        Assert.True(TestPixels.Px(canvas, 0, 39).R < 25 && TestPixels.Px(canvas, 39, 39).R < 25, "Bottom corners must be opaque, so the ramp is linear.");
    }

    /// <summary>Ordering: the overlay sits over soft-light and under the text.</summary>
    [Fact]
    public void ApplyGradientOverlay_SitsUnderTextLayers()
    {
        var doc = WhiteDoc();
        doc.Background.Overlay = BottomFadeBlack();
        doc.Effects.SoftLight = new SoftLightSettings { Enabled = true, Color = "#ffffff", Opacity = 1f };
        doc.Layers.Add(new CoverLayer
        {
            Id = "t", Type = "text", Content = "MMMM", Color = "#ff0000",
            X = 0.5f, Y = 0.9f, Size = 0.25f, Align = TextAlign.Center
        });

        using var canvas = RenderSurface.New(40, 40);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        // A fully opaque white soft-light wash would erase the overlay if it ran after it.
        Assert.True(TestPixels.Px(canvas, 20, 39).R < 60, "Overlay must be applied after soft-light.");

        // Somewhere in the text band a red text pixel must have survived the overlay.
        var foundRed = false;
        for (var y = 28; y < 40 && !foundRed; y++)
        {
            for (var x = 0; x < 40; x++)
            {
                if (TestPixels.Px(canvas, x, y).R > 120 && TestPixels.Px(canvas, x, y).G < 80) { foundRed = true; break; }
            }
        }
        Assert.True(foundRed, "Text must be drawn on top of the overlay.");
    }

    /// <summary>
    /// A document POSTed without an Overlay (every document written before this release)
    /// must deserialize with Overlay null and survive Normalize, which is the null-guard
    /// chokepoint for client-supplied documents.
    /// </summary>
    [Fact]
    public void Normalize_DocumentWithoutOverlay_LeavesItNull()
    {
        var json = """
        {"Canvas":{"Width":40,"Height":40},
         "Background":{"Source":"solid","DimColor":"#ffffff"},
         "Layers":[]}
        """;

        var doc = JsonSerializer.Deserialize<CoverDocument>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })!;

        DocumentMigration.Normalize(doc);

        Assert.Null(doc.Background.Overlay);
    }

    /// <summary>Stops written before Alpha existed deserialize as fully opaque.</summary>
    [Fact]
    public void Deserialize_StopWithoutAlpha_DefaultsToOpaque()
    {
        var json = """{"Color":"#ff0000","Position":0.5}""";

        var stop = JsonSerializer.Deserialize<GradientStop>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })!;

        Assert.Equal(1f, stop.Alpha);
    }

    /// <summary>The overlay survives a serialize/deserialize round trip with its alphas.</summary>
    [Fact]
    public void Overlay_RoundTripsThroughJson()
    {
        var doc = WhiteDoc();
        doc.Background.Overlay = BottomFadeBlack();

        var json = JsonSerializer.Serialize(doc);
        var back = JsonSerializer.Deserialize<CoverDocument>(json)!;

        Assert.NotNull(back.Background.Overlay);
        Assert.True(back.Background.Overlay!.IsEnabled);
        Assert.Equal(2, back.Background.Overlay.Stops.Count);
        Assert.Equal(0f, back.Background.Overlay.Stops[0].Alpha);
        Assert.Equal(1f, back.Background.Overlay.Stops[1].Alpha);
    }
}
