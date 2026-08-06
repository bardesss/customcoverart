using System.Text.Json;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// The background gradient overlay: a linear multi-stop gradient with per-stop alpha,
/// composited over the finished background and under the layers. The alpha is the whole
/// point — these pin that it survives compositing rather than being flattened to opaque.
/// </summary>
public class GradientOverlayTests
{
    [Fact]
    public void BuildColorStops_AppliesStopAlpha()
    {
        var g = new GradientSettings
        {
            Stops = new()
            {
                new GradientStop { Color = "#ffffff", Position = 0f, Alpha = 0f },
                new GradientStop { Color = "#ffffff", Position = 1f, Alpha = 0.5f }
            }
        };

        var stops = DocumentRenderer.BuildColorStops(g);

        Assert.Equal(0, stops[0].Color.ToPixel<Rgba32>().A);
        // 0.5f * 255 = 127.5; ImageSharp's own float->byte rounding lands on 127 or 128.
        // A wide band here would also pass a wrong scale factor (e.g. treating Alpha as
        // already 0..255, or applying it to the wrong channel), so this is deliberately
        // tight around the two rounding candidates rather than a generous margin.
        Assert.InRange(stops[1].Color.ToPixel<Rgba32>().A, 127, 128);
    }

    /// <summary>
    /// Out-of-range Alpha must saturate rather than wrap or throw. This is NOT a guard on
    /// the explicit <c>Math.Clamp</c> in <c>BuildColorStops</c> — ImageSharp 3.1.12's own
    /// <c>Color.WithAlpha</c> already saturates its float input (verified empirically:
    /// <c>WithAlpha(2f).A == 255</c>, <c>WithAlpha(-1f).A == 0</c>), so an implementation
    /// with the clamp removed produces byte-identical output for these inputs and this test
    /// would still pass. The clamp stays in production code as belt-and-braces documentation
    /// of intent, and in case a future ImageSharp version stops saturating; this test instead
    /// pins the observable end-to-end contract, whichever layer currently enforces it.
    /// </summary>
    [Fact]
    public void BuildColorStops_OutOfRangeAlpha_SaturatesToOpaqueAndTransparent()
    {
        var g = new GradientSettings
        {
            Stops = new()
            {
                new GradientStop { Color = "#ffffff", Position = 0f, Alpha = 2f },
                new GradientStop { Color = "#ffffff", Position = 1f, Alpha = -1f }
            }
        };

        var stops = DocumentRenderer.BuildColorStops(g);

        Assert.Equal(255, stops[0].Color.ToPixel<Rgba32>().A);
        Assert.Equal(0, stops[1].Color.ToPixel<Rgba32>().A);
    }

    /// <summary>Alpha defaults to 1, so every gradient written before overlays existed is opaque.</summary>
    [Fact]
    public void BuildColorStops_DefaultAlpha_IsFullyOpaque()
    {
        var g = new GradientSettings
        {
            Stops = new()
            {
                new GradientStop { Color = "#ff0000", Position = 0f },
                new GradientStop { Color = "#0000ff", Position = 1f }
            }
        };

        var stops = DocumentRenderer.BuildColorStops(g);

        Assert.Equal(255, stops[0].Color.ToPixel<Rgba32>().A);
        Assert.Equal(255, stops[1].Color.ToPixel<Rgba32>().A);
    }

    /// <summary>
    /// forceLinear is what keeps an overlay linear regardless of the Type field it inherits
    /// from the reused GradientSettings type. Rendering both into a canvas is the only
    /// observable difference: a radial brush centred mid-canvas leaves the corners at the
    /// far stop, a 90-degree linear one leaves the whole top row at the near stop.
    /// </summary>
    [Fact]
    public void CreateGradientBrush_ForceLinear_IgnoresRadialType()
    {
        var g = new GradientSettings
        {
            Type = GradientType.Radial,
            Angle = 90f,
            CenterX = 0.5f,
            CenterY = 0.5f,
            Radius = 0.5f,
            Stops = new()
            {
                new GradientStop { Color = "#000000", Position = 0f },
                new GradientStop { Color = "#ffffff", Position = 1f }
            }
        };

        using var linear = new Image<Rgba32>(40, 40);
        linear.Mutate(x => x.Fill(DocumentRenderer.CreateGradientBrush(g, 40, 40, forceLinear: true)));

        // Linear at 90 degrees runs top to bottom: the top row is the near (black) stop
        // and the bottom row the far (white) one, both edges to edge.
        Assert.True(linear[5, 0].R < 40 && linear[35, 0].R < 40, "Top row should be the near stop across its width.");
        Assert.True(linear[5, 39].R > 215 && linear[35, 39].R > 215, "Bottom row should be the far stop across its width.");
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
    /// trap that once blacked out dimmed backgrounds — ImageSharp's Fill ignores brush alpha
    /// on alpha-less pixel formats. If that happens here the middle row goes solid black
    /// instead of mid-grey, so this assertion is the regression guard for the whole feature.
    /// </summary>
    [Fact]
    public void ApplyGradientOverlay_RampsAlphaAcrossTheCanvas()
    {
        var doc = WhiteDoc();
        doc.Background.Overlay = BottomFadeBlack();

        using var canvas = new Image<Rgba32>(40, 40);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        Assert.True(canvas[20, 0].R > 230, "Top must stay near the white background.");
        Assert.True(canvas[20, 39].R < 25, "Bottom must reach the opaque overlay colour.");
        Assert.InRange(canvas[20, 20].R, 90, 165);   // a genuine blend, not 0 and not 255
    }

    [Fact]
    public void ApplyGradientOverlay_NullOrDisabled_LeavesTheCanvasUntouched()
    {
        using var withoutOverlay = new Image<Rgba32>(40, 40);
        DocumentRenderer.ComposeDocumentFrame(withoutOverlay, null, WhiteDoc());

        var disabledDoc = WhiteDoc();
        disabledDoc.Background.Overlay = BottomFadeBlack();
        disabledDoc.Background.Overlay.IsEnabled = false;

        using var withDisabled = new Image<Rgba32>(40, 40);
        DocumentRenderer.ComposeDocumentFrame(withDisabled, null, disabledDoc);

        Assert.Equal(withoutOverlay[20, 39], withDisabled[20, 39]);
        Assert.Equal(withoutOverlay[20, 20], withDisabled[20, 20]);
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

        using var canvas = new Image<Rgba32>(40, 40);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        Assert.True(canvas[20, 39].R > 230, "A one-stop overlay must not paint anything.");
    }

    /// <summary>
    /// The overlay lives in ComposeDocumentFrame, not inside ApplyBackgroundLayer, precisely
    /// so it works for every source. ApplyBackgroundLayer runs only on the image path — which
    /// ComposeDocumentFrame selects by whether a background <see cref="Image"/> was passed in,
    /// not by <c>Source</c> — so the Upload case must actually be given one, or that path
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
        using Image<Rgba32>? background = source == BackgroundSources.Upload
            ? new Image<Rgba32>(10, 10, Color.White)
            : null;

        using var canvas = new Image<Rgba32>(40, 40);
        DocumentRenderer.ComposeDocumentFrame(canvas, background, doc);

        Assert.True(canvas[20, 39].R < 25, $"Overlay must apply for source '{source}'.");
    }

    /// <summary>Type is inert on an overlay: a radial one still renders as a linear ramp.</summary>
    [Fact]
    public void ApplyGradientOverlay_RadialType_StillRendersLinear()
    {
        var doc = WhiteDoc();
        doc.Background.Overlay = BottomFadeBlack();
        doc.Background.Overlay.Type = GradientType.Radial;

        using var canvas = new Image<Rgba32>(40, 40);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        // Under a linear 90-degree ramp the whole bottom row is opaque, corners included.
        // A radial brush would leave the bottom corners far lighter than the bottom centre.
        Assert.True(canvas[0, 39].R < 25 && canvas[39, 39].R < 25, "Bottom corners must be opaque, so the ramp is linear.");
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

        using var canvas = new Image<Rgba32>(40, 40);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        // A fully opaque white soft-light wash would erase the overlay if it ran after it.
        Assert.True(canvas[20, 39].R < 60, "Overlay must be applied after soft-light.");

        // Somewhere in the text band a red text pixel must have survived the overlay.
        var foundRed = false;
        for (var y = 28; y < 40 && !foundRed; y++)
        {
            for (var x = 0; x < 40; x++)
            {
                if (canvas[x, y].R > 120 && canvas[x, y].G < 80) { foundRed = true; break; }
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
