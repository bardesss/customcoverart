using CustomCoverArt.Models;
using CustomCoverArt.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
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
}
