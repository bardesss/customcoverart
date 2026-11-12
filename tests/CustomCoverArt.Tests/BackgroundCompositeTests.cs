using System;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using CustomCoverArt.Services.Rendering;
using SkiaSharp;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// The image background path of the compositor: the three fits, pan/zoom, blur, and the promise
/// that the background passed in is only read.
/// </summary>
public class BackgroundCompositeTests
{
    private static readonly SKColor Red = new(255, 0, 0);
    private static readonly SKColor Green = new(0, 255, 0);
    private static readonly SKColor Yellow = new(255, 255, 0);
    private static readonly SKColor Blue = new(0, 0, 255);

    private static CoverDocument Doc(int w, int h, string fit, Action<BackgroundLayer>? tweak = null)
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = w, Height = h } };
        doc.Background.Source = BackgroundSources.Upload;
        doc.Background.Fit = fit;
        doc.Background.Dim = 0f;
        doc.Background.DimColor = "#0000ff";
        tweak?.Invoke(doc.Background);
        return doc;
    }

    private static SKBitmap Compose(SKBitmap background, CoverDocument doc)
    {
        var canvas = RenderSurface.New(doc.Canvas.Width, doc.Canvas.Height);
        DocumentRenderer.ComposeDocumentFrame(canvas, background, doc);
        return canvas;
    }

    private static Rgba C(SKColor c) => new(c.Red, c.Green, c.Blue, c.Alpha);

    // Four quadrants: red top-left, green top-right, yellow bottom-left, black bottom-right. No blue anywhere.
    private static SKBitmap Quadrants(int w, int h) => TestPixels.Painted(w, h, (x, y) =>
        y < h / 2 ? (x < w / 2 ? Red : Green) : (x < w / 2 ? Yellow : SKColors.Black));

    private static SKBitmap Noise(int w, int h, int seed)
    {
        var rng = new Random(seed);
        return TestPixels.Painted(w, h, (_, _) => new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256)));
    }

    /// <summary>Contain on a wide image letterboxes top and bottom with the base colour; the image band is centred.</summary>
    [Fact]
    public void Contain_WideImage_LetterboxesWithTheBaseColour()
    {
        using var bg = RenderSurface.New(100, 50, Red);
        using var canvas = Compose(bg, Doc(100, 100, "contain"));

        // Footprint 100x50 at y = 25..74.
        Assert.Equal(C(Blue), TestPixels.Px(canvas, 50, 0));
        Assert.Equal(C(Blue), TestPixels.Px(canvas, 50, 24));
        Assert.Equal(C(Red), TestPixels.Px(canvas, 50, 25));
        Assert.Equal(C(Red), TestPixels.Px(canvas, 0, 50));
        Assert.Equal(C(Red), TestPixels.Px(canvas, 99, 74));
        Assert.Equal(C(Blue), TestPixels.Px(canvas, 50, 75));
        Assert.Equal(C(Blue), TestPixels.Px(canvas, 99, 99));
    }

    /// <summary>Contain on a tall image pillarboxes left and right.</summary>
    [Fact]
    public void Contain_TallImage_Pillarboxes()
    {
        using var bg = RenderSurface.New(30, 120, Red);
        using var canvas = Compose(bg, Doc(100, 100, "contain"));

        // 30x120 scaled by 100/120 -> 25x100, centred at x = 37..61.
        Assert.Equal(C(Blue), TestPixels.Px(canvas, 36, 50));
        Assert.Equal(C(Red), TestPixels.Px(canvas, 37, 50));
        Assert.Equal(C(Red), TestPixels.Px(canvas, 61, 0));
        Assert.Equal(C(Blue), TestPixels.Px(canvas, 62, 99));
    }

    /// <summary>Contain keeps the whole image: each quadrant shows in its own corner of the footprint.</summary>
    [Fact]
    public void Contain_ShowsTheWholeImage()
    {
        using var bg = Quadrants(40, 20);
        using var canvas = Compose(bg, Doc(200, 200, "contain"));

        // Footprint 200x100 at y = 50..149.
        Assert.Equal(C(Red), TestPixels.Px(canvas, 2, 52));
        Assert.Equal(C(Green), TestPixels.Px(canvas, 197, 52));
        Assert.Equal(C(Yellow), TestPixels.Px(canvas, 2, 147));
        Assert.Equal(C(SKColors.Black), TestPixels.Px(canvas, 197, 147));
    }

    /// <summary>Stretch distorts the image to fill the canvas edge to edge: no base colour shows anywhere.</summary>
    [Fact]
    public void Stretch_FillsEdgeToEdge()
    {
        using var bg = Quadrants(20, 20);
        using var canvas = Compose(bg, Doc(100, 60, "stretch"));

        Assert.Equal(C(Red), TestPixels.Px(canvas, 1, 1));
        Assert.Equal(C(Green), TestPixels.Px(canvas, 98, 1));
        Assert.Equal(C(Yellow), TestPixels.Px(canvas, 1, 58));
        Assert.Equal(C(SKColors.Black), TestPixels.Px(canvas, 98, 58));
        for (var y = 0; y < 60; y++)
        {
            for (var x = 0; x < 100; x++)
            {
                var p = TestPixels.Px(canvas, x, y);
                Assert.True(p.B == 0 && p.A == 255, $"({x},{y}) shows the base colour or is not opaque: {p}");
            }
        }
    }

    /// <summary>Cover with the default transform crops dead centre: a same-size image comes through byte for byte.</summary>
    [Fact]
    public void Cover_SameSizeIdentity_IsTheSourceExactly()
    {
        using var bg = Noise(64, 48, 1);
        using var canvas = Compose(bg, Doc(64, 48, "cover"));

        Assert.Equal(TestPixels.Bytes(bg), TestPixels.Bytes(canvas));
    }

    /// <summary>An unknown fit falls back to cover.</summary>
    [Fact]
    public void UnknownFit_BehavesAsCover()
    {
        using var bg = Noise(80, 50, 2);
        using var cover = Compose(bg, Doc(60, 60, "cover"));
        using var unknown = Compose(bg, Doc(60, 60, " Sideways "));

        Assert.Equal(TestPixels.Bytes(cover), TestPixels.Bytes(unknown));
    }

    /// <summary>
    /// A 300x100 image in a 100x100 canvas under cover: OffsetX chooses which third shows, at Scale 1.
    /// -1 is the left edge, +1 the right.
    /// </summary>
    [Theory]
    [InlineData(-1f, 255, 0, 0)]
    [InlineData(0f, 0, 255, 0)]
    [InlineData(1f, 0, 0, 255)]
    public void Cover_OffsetX_PansAcrossTheSlack(float offsetX, byte r, byte g, byte b)
    {
        using var bg = TestPixels.Painted(300, 100, (x, _) => x < 100 ? Red : x < 200 ? Green : Blue);
        using var canvas = Compose(bg, Doc(100, 100, "cover", l => l.Transform = new BackgroundTransform { OffsetX = offsetX }));

        Assert.Equal(new Rgba(r, g, b, 255), TestPixels.Px(canvas, 50, 50));
        Assert.Equal(new Rgba(r, g, b, 255), TestPixels.Px(canvas, 1, 1));
        Assert.Equal(new Rgba(r, g, b, 255), TestPixels.Px(canvas, 98, 98));
    }

    /// <summary>Zoom 2 shows a quarter of the image; the offsets choose which corner (x left/right, y up/down).</summary>
    [Theory]
    [InlineData(-1f, -1f, 255, 0, 0)]
    [InlineData(1f, -1f, 0, 255, 0)]
    [InlineData(-1f, 1f, 255, 255, 0)]
    [InlineData(1f, 1f, 0, 0, 0)]
    public void Cover_Zoom2_OffsetsChooseTheQuadrant(float offsetX, float offsetY, byte r, byte g, byte b)
    {
        using var bg = Quadrants(100, 100);
        using var canvas = Compose(bg, Doc(100, 100, "cover", l => l.Transform = new BackgroundTransform { Scale = 2f, OffsetX = offsetX, OffsetY = offsetY }));

        Assert.Equal(new Rgba(r, g, b, 255), TestPixels.Px(canvas, 50, 50));
        Assert.Equal(new Rgba(r, g, b, 255), TestPixels.Px(canvas, 10, 10));
        Assert.Equal(new Rgba(r, g, b, 255), TestPixels.Px(canvas, 89, 89));
    }

    /// <summary>Zoom enlarges: at Scale 2 centred, the boundary between quadrants sits mid-canvas, as it does unzoomed.</summary>
    [Fact]
    public void Cover_Zoom2_Centred_KeepsTheCentre()
    {
        using var bg = Quadrants(100, 100);
        using var canvas = Compose(bg, Doc(100, 100, "cover", l => l.Transform = new BackgroundTransform { Scale = 2f }));

        // The centred window is 25..74 of the source: the quadrant boundary (50) lands at canvas 50.
        Assert.Equal(C(Red), TestPixels.Px(canvas, 20, 20));
        Assert.Equal(C(Green), TestPixels.Px(canvas, 80, 20));
        Assert.Equal(C(Yellow), TestPixels.Px(canvas, 20, 80));
        Assert.Equal(C(SKColors.Black), TestPixels.Px(canvas, 80, 80));
    }

    /// <summary>Contain with zoom pans inside the letterboxed footprint and never spills into the bars.</summary>
    [Fact]
    public void Contain_Zoom_StaysInsideTheFootprint()
    {
        using var bg = Quadrants(100, 50);
        using var canvas = Compose(bg, Doc(100, 100, "contain", l => l.Transform = new BackgroundTransform { Scale = 2f, OffsetX = 1f, OffsetY = 1f }));

        Assert.Equal(C(Blue), TestPixels.Px(canvas, 50, 24));
        Assert.Equal(C(Blue), TestPixels.Px(canvas, 50, 75));
        Assert.Equal(C(SKColors.Black), TestPixels.Px(canvas, 50, 50));
    }

    /// <summary>Blur 0 (and a negative value from an unclamped caller) is no blur at all, byte for byte.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(-3f)]
    public void Blur_ZeroOrNegative_IsNoBlur(float blur)
    {
        using var bg = Noise(64, 48, 3);
        using var canvas = Compose(bg, Doc(64, 48, "cover", l => l.Blur = blur));

        Assert.Equal(TestPixels.Bytes(bg), TestPixels.Bytes(canvas));
    }

    /// <summary>A positive blur actually blurs: a hard black/white edge becomes a ramp.</summary>
    [Fact]
    public void Blur_SoftensAHardEdge()
    {
        using var bg = TestPixels.Painted(100, 100, (x, _) => x < 50 ? SKColors.Black : SKColors.White);
        using var canvas = Compose(bg, Doc(100, 100, "cover", l => l.Blur = 4f));

        Assert.InRange(TestPixels.Px(canvas, 49, 50).R, 70, 140);
        Assert.InRange(TestPixels.Px(canvas, 50, 50).R, 115, 185);
        Assert.Equal(0, TestPixels.Px(canvas, 10, 50).R);
        Assert.Equal(255, TestPixels.Px(canvas, 90, 50).R);
    }

    /// <summary>
    /// The blur clamps at the image's edges, as the original renderer's did: a flat image stays flat right up to
    /// the canvas edge instead of fading towards transparent or black.
    /// </summary>
    [Theory]
    [InlineData("cover")]
    [InlineData("stretch")]
    public void Blur_ClampsAtTheEdges(string fit)
    {
        using var bg = RenderSurface.New(40, 30, new SKColor(200, 100, 50));
        using var canvas = Compose(bg, Doc(100, 100, fit, l => l.Blur = 10f));

        foreach (var (x, y) in new[] { (0, 0), (99, 0), (0, 99), (99, 99), (50, 0), (0, 50) })
        {
            Assert.True(TestPixels.Near(TestPixels.Px(canvas, x, y), new Rgba(200, 100, 50, 255), 1), $"({x},{y}) = {TestPixels.Px(canvas, x, y)}");
        }
    }

    /// <summary>
    /// Under contain the blur belongs to the footprint: its edges clamp (stay the image colour) and the
    /// letterbox bars stay exactly the base colour, with no bleed either way.
    /// </summary>
    [Fact]
    public void Blur_Contain_StaysInsideTheFootprint()
    {
        using var bg = RenderSurface.New(100, 50, Red);
        using var canvas = Compose(bg, Doc(100, 100, "contain", l => l.Blur = 8f));

        Assert.Equal(C(Blue), TestPixels.Px(canvas, 50, 24));
        Assert.Equal(C(Blue), TestPixels.Px(canvas, 0, 0));
        Assert.Equal(C(Blue), TestPixels.Px(canvas, 50, 75));
        Assert.True(TestPixels.Near(TestPixels.Px(canvas, 50, 25), C(Red), 1), $"footprint top edge {TestPixels.Px(canvas, 50, 25)}");
        Assert.True(TestPixels.Near(TestPixels.Px(canvas, 0, 74), C(Red), 1), $"footprint bottom edge {TestPixels.Px(canvas, 0, 74)}");
    }

    /// <summary>The background bitmap is only read: every fit, zoom, blur and dim leaves its bytes as they were.</summary>
    [Theory]
    [InlineData("cover")]
    [InlineData("contain")]
    [InlineData("stretch")]
    public void Compose_NeverMutatesTheBackground(string fit)
    {
        using var bg = Noise(70, 40, 4);
        var before = TestPixels.Bytes(bg);

        using var canvas = Compose(bg, Doc(100, 120, fit, l =>
        {
            l.Transform = new BackgroundTransform { Scale = 1.7f, OffsetX = 0.3f, OffsetY = -0.6f };
            l.Blur = 5f;
            l.Dim = 0.4f;
            l.Overlay = new GradientSettings
            {
                IsEnabled = true,
                Stops = new() { new GradientStop { Color = "#000000", Alpha = 0f, Position = 0f }, new GradientStop { Color = "#000000", Position = 1f } }
            };
        }));

        Assert.Equal(before, TestPixels.Bytes(bg));
    }

    /// <summary>The same background composes to the same bytes twice (and can be reused across frames).</summary>
    [Fact]
    public void Compose_IsDeterministic_AndTheBackgroundIsReusable()
    {
        using var bg = Noise(90, 70, 5);
        var doc = Doc(64, 80, "cover", l => { l.Blur = 3f; l.Transform = new BackgroundTransform { Scale = 1.3f, OffsetY = 0.5f }; });

        using var a = Compose(bg, doc);
        using var b = Compose(bg, doc);

        Assert.Equal(TestPixels.Bytes(a), TestPixels.Bytes(b));
    }

    /// <summary>A 1x1 background is a legal (if odd) input under every fit.</summary>
    [Theory]
    [InlineData("cover")]
    [InlineData("contain")]
    [InlineData("stretch")]
    public void OnePixelBackground_FillsItsFootprint(string fit)
    {
        using var bg = RenderSurface.New(1, 1, Green);
        using var canvas = Compose(bg, Doc(50, 30, fit));

        Assert.Equal(C(Green), TestPixels.Px(canvas, 25, 15));
    }
}
