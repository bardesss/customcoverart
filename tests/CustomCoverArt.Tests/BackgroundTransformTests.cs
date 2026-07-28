using CustomCoverArt.Models;
using CustomCoverArt.Services;
using SixLabors.ImageSharp;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// <see cref="DocumentRenderer.TransformedSourceRect"/> computes the sub-rectangle of the
/// fitted background to draw, given the user's pan/zoom (<see cref="BackgroundTransform"/>),
/// so the client canvas and server render frame the background identically.
/// </summary>
public class BackgroundTransformTests
{
    [Fact]
    public void Identity_ReturnsFullFittedRect()
    {
        var r = DocumentRenderer.TransformedSourceRect(1000, 1000, 1000, 1000, new BackgroundTransform());
        Assert.Equal(0, r.X);
        Assert.Equal(0, r.Y);
        Assert.Equal(1000, r.Width);
        Assert.Equal(1000, r.Height);
    }

    [Fact]
    public void Scale2_CropsHalfCentered()
    {
        var r = DocumentRenderer.TransformedSourceRect(1000, 1000, 1000, 1000, new BackgroundTransform { Scale = 2f });
        Assert.Equal(500, r.Width);
        Assert.Equal(500, r.Height);
        Assert.Equal(250, r.X); // centered
        Assert.Equal(250, r.Y);
    }

    [Fact]
    public void Pan_ClampsInsideImage()
    {
        // Scale=2 halves each dimension (500x500 crop), leaving 500px of slack per axis.
        // OffsetX=5 clamps to +1 (fully panned right), pushing the crop to the far-right
        // edge (X == slackX == 500) while Y stays centered (slackY / 2 == 250).
        var r = DocumentRenderer.TransformedSourceRect(1000, 1000, 1000, 1000, new BackgroundTransform { Scale = 2f, OffsetX = 5f });
        Assert.Equal(500, r.Width);
        Assert.Equal(500, r.Height);
        Assert.Equal(500, r.X); // clamped to slackX (far-right)
        Assert.Equal(250, r.Y); // centered vertically
        Assert.True(r.X >= 0 && r.X + r.Width <= 1000); // stays in-bounds
    }
}
