using System.Collections.Generic;
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using CustomCoverArt.Services.Rendering;
using SkiaSharp;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// The gradient shader's geometry (direction of each angle, radial centre, clamping past the
/// ends) and the stop list it is built from.
/// </summary>
public class GradientShaderTests
{
    /// <summary>Fills a new <paramref name="w"/> x <paramref name="h"/> surface with the settings' shader.</summary>
    internal static SKBitmap Fill(GradientSettings g, int w, int h, bool forceLinear = false)
    {
        var bitmap = RenderSurface.New(w, h);
        using var canvas = new SKCanvas(bitmap);
        using var shader = DocumentRenderer.CreateGradientShader(g, w, h, forceLinear);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(0, 0, w, h, paint);
        return bitmap;
    }

    private static GradientSettings BlackToWhite(float angle) => new()
    {
        IsEnabled = true,
        Angle = angle,
        Stops = new()
        {
            new GradientStop { Color = "#000000", Position = 0f },
            new GradientStop { Color = "#ffffff", Position = 1f }
        }
    };

    /// <summary>
    /// The angle is the direction of travel from the first stop to the last, CSS-canvas style with
    /// y down: 0 runs left to right, 90 top to bottom, 180 right to left, 270 bottom to top.
    /// </summary>
    [Theory]
    [InlineData(0f, 1, 50, 98, 50)]
    [InlineData(90f, 50, 1, 50, 98)]
    [InlineData(180f, 98, 50, 1, 50)]
    [InlineData(270f, 50, 98, 50, 1)]
    public void LinearAngle_RunsFromTheFirstStopToTheLast(float angle, int darkX, int darkY, int lightX, int lightY)
    {
        using var img = Fill(BlackToWhite(angle), 100, 100);

        Assert.True(TestPixels.Px(img, darkX, darkY).R < 10, $"angle {angle}: ({darkX},{darkY}) should be the first (black) stop");
        Assert.True(TestPixels.Px(img, lightX, lightY).R > 245, $"angle {angle}: ({lightX},{lightY}) should be the last (white) stop");
        // Half way along the ramp is mid grey.
        Assert.InRange(TestPixels.Px(img, 50, 50).R, 120, 136);
    }

    /// <summary>The line spans the canvas corner to corner along its angle, so a 45 degree ramp reaches both stops in the corners.</summary>
    [Fact]
    public void LinearAngle45_SpansCornerToCorner()
    {
        using var img = Fill(BlackToWhite(45f), 100, 60);

        Assert.True(TestPixels.Px(img, 0, 0).R < 6);
        Assert.True(TestPixels.Px(img, 99, 59).R > 249);
        // Perpendicular to the ramp the colour is constant: (60, 0) and (0, 60) sit on one line.
        Assert.True(TestPixels.Near(TestPixels.Px(img, 59, 0), TestPixels.Px(img, 0, 59), 1));
    }

    /// <summary>A 0.25-0.75 two-stop ramp clamps beyond its ends (no repeat, no transparency).</summary>
    [Fact]
    public void GradientShader_ClampsBeyondTheEnds()
    {
        var g = new GradientSettings
        {
            Angle = 0f,
            Stops = new()
            {
                new GradientStop { Color = "#ff0000", Position = 0.25f },
                new GradientStop { Color = "#0000ff", Position = 0.75f }
            }
        };

        using var img = Fill(g, 100, 10);

        for (var x = 0; x < 25; x++)
        {
            Assert.Equal(new Rgba(255, 0, 0, 255), TestPixels.Px(img, x, 5));
        }

        for (var x = 76; x < 100; x++)
        {
            Assert.Equal(new Rgba(0, 0, 255, 255), TestPixels.Px(img, x, 5));
        }

        // Between the stops it is a genuine mix of the two.
        var mid = TestPixels.Px(img, 50, 5);
        Assert.InRange(mid.R, 100, 155);
        Assert.InRange(mid.B, 100, 155);
    }

    /// <summary>The middle stop of three is reached at its position.</summary>
    [Fact]
    public void ThreeStops_HitTheMiddleStopAtItsPosition()
    {
        var g = new GradientSettings
        {
            Angle = 0f,
            Stops = new()
            {
                new GradientStop { Color = "#ff0000", Position = 0f },
                new GradientStop { Color = "#00ff00", Position = 0.3f },
                new GradientStop { Color = "#0000ff", Position = 1f }
            }
        };

        using var img = Fill(g, 100, 4);

        Assert.True(TestPixels.Near(TestPixels.Px(img, 30, 2), new Rgba(0, 255, 0, 255), 6));
    }

    /// <summary>A radial gradient is darkest (its first stop) at its centre and lightens outwards in every direction.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Radial_CentreIsTheFirstStop(bool invert)
    {
        var g = new GradientSettings
        {
            Type = GradientType.Radial,
            CenterX = 0.3f,
            CenterY = 0.6f,
            Radius = 0.5f,
            Stops = new()
            {
                new GradientStop { Color = invert ? "#ffffff" : "#000000", Position = 0f },
                new GradientStop { Color = invert ? "#000000" : "#ffffff", Position = 1f }
            }
        };

        using var img = Fill(g, 100, 100);

        // The extreme pixel (min, or max when inverted) is at the centre (30, 60).
        var bestX = -1;
        var bestY = -1;
        var best = invert ? -1 : 256;
        for (var y = 0; y < 100; y++)
        {
            for (var x = 0; x < 100; x++)
            {
                var r = TestPixels.Px(img, x, y).R;
                if (invert ? r > best : r < best)
                {
                    best = r;
                    bestX = x;
                    bestY = y;
                }
            }
        }

        Assert.InRange(bestX, 29, 30);
        Assert.InRange(bestY, 59, 60);

        // Radius 0.5 of the shorter side = 50 px: at 50 px out the last stop is reached and held.
        var edge = invert ? 0 : 255;
        Assert.Equal(edge, TestPixels.Px(img, 30, 5).R);
        Assert.Equal(edge, TestPixels.Px(img, 85, 60).R);
        Assert.Equal(edge, TestPixels.Px(img, 99, 0).R);
    }

    /// <summary>Stops are sorted by position and their positions clamped into 0..1, as before.</summary>
    [Fact]
    public void BuildGradientStops_SortsAndClampsPositions()
    {
        var g = new GradientSettings
        {
            Stops = new()
            {
                new GradientStop { Color = "#0000ff", Position = 1.7f },
                new GradientStop { Color = "#ff0000", Position = -0.4f },
                new GradientStop { Color = "#00ff00", Position = 0.4f }
            }
        };

        var stops = DocumentRenderer.BuildGradientStops(g);

        Assert.Equal(new[] { 0f, 0.4f, 1f }, stops.Positions);
        Assert.Equal(new[] { new SKColor(255, 0, 0), new SKColor(0, 255, 0), new SKColor(0, 0, 255) }, stops.Colors);
    }

    /// <summary>Fewer than two stops fall back to Start/End; an unparsable stop colour becomes grey.</summary>
    [Fact]
    public void BuildGradientStops_FallsBackToStartEnd_AndBadColoursToGrey()
    {
        var fallback = DocumentRenderer.BuildGradientStops(new GradientSettings
        {
            StartColor = "#112233",
            EndColor = "#445566",
            Stops = new() { new GradientStop { Color = "#ffffff", Position = 0.5f } }
        });
        Assert.Equal(new[] { 0f, 1f }, fallback.Positions);
        Assert.Equal(new[] { new SKColor(0x11, 0x22, 0x33), new SKColor(0x44, 0x55, 0x66) }, fallback.Colors);

        var unparsable = DocumentRenderer.BuildGradientStops(new GradientSettings
        {
            StartColor = "nope",
            EndColor = "#zz",
            Stops = new List<GradientStop>()
        });
        Assert.Equal(new[] { SKColors.Black, SKColors.White }, unparsable.Colors);

        var badStop = DocumentRenderer.BuildGradientStops(new GradientSettings
        {
            Stops = new()
            {
                new GradientStop { Color = "red", Position = 0f },
                new GradientStop { Color = "#000000", Position = 1f }
            }
        });
        Assert.Equal(new SKColor(128, 128, 128), badStop.Colors[0]);
    }

    /// <summary>A shader is produced (never null) even for degenerate settings: a zero radius and a 1x1 target.</summary>
    [Fact]
    public void CreateGradientShader_DegenerateInput_StillFills()
    {
        var g = BlackToWhite(0f);
        g.Type = GradientType.Radial;
        g.Radius = 0f;

        using var img = Fill(g, 1, 1);

        Assert.Equal(255, TestPixels.Px(img, 0, 0).A);
    }
}
