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

    // ---- "cover" fit: the fitted image is LARGER than the destination footprint ----
    // A 1000x1500 poster fitted to a 1000x1000 canvas has 500px the fit crops away.
    // That crop slack is pannable, so repositioning works at the default Scale=1.

    [Fact]
    public void CoverFit_Identity_CentersFootprintSizedWindow()
    {
        // Identity must reproduce the plain centre-crop: a footprint-sized window,
        // centred over the 500px of vertical crop slack.
        var r = DocumentRenderer.TransformedSourceRect(1000, 1500, 1000, 1000, new BackgroundTransform());
        Assert.Equal(1000, r.Width);
        Assert.Equal(1000, r.Height);
        Assert.Equal(0, r.X);
        Assert.Equal(250, r.Y);
    }

    [Fact]
    public void CoverFit_PansAcrossFitCropAtScale1()
    {
        // The regression this guards: at Scale=1 the offset used to be a no-op because
        // slack was derived from zoom alone. It must now traverse the fit's own crop.
        var top = DocumentRenderer.TransformedSourceRect(1000, 1500, 1000, 1000, new BackgroundTransform { OffsetY = -1f });
        Assert.Equal(0, top.Y);
        Assert.Equal(1000, top.Height);

        var bottom = DocumentRenderer.TransformedSourceRect(1000, 1500, 1000, 1000, new BackgroundTransform { OffsetY = 1f });
        Assert.Equal(500, bottom.Y); // far edge == slackY
        Assert.Equal(1500, bottom.Y + bottom.Height); // still in-bounds
    }

    [Fact]
    public void CoverFit_ZoomSlackAndCropSlackCombine()
    {
        // Scale=2 over a 1000x1000 footprint gives a 500x500 window; the pannable
        // slack is then the FULL remainder of the fitted image, not just the zoom's.
        var r = DocumentRenderer.TransformedSourceRect(1000, 1500, 1000, 1000, new BackgroundTransform { Scale = 2f });
        Assert.Equal(500, r.Width);
        Assert.Equal(500, r.Height);
        Assert.Equal(250, r.X);  // slackX 500, centred
        Assert.Equal(500, r.Y);  // slackY 1000, centred
    }

    [Fact]
    public void WindowNeverExceedsFittedImage()
    {
        // "contain" fits the image INSIDE the canvas, so the footprint can exceed the
        // fitted image on an axis. The window must clamp to the image, never oversample.
        var r = DocumentRenderer.TransformedSourceRect(600, 900, 1000, 1000, new BackgroundTransform());
        Assert.Equal(600, r.Width);
        Assert.Equal(900, r.Height);
        Assert.Equal(0, r.X);
        Assert.Equal(0, r.Y);
    }
}
