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
        Assert.InRange(stops[1].Color.ToPixel<Rgba32>().A, 120, 136);
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
}
