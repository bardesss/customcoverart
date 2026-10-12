using CustomCoverArt.Models;
using CustomCoverArt.Services;
using Xunit;

namespace CustomCoverArt.Tests;

public class ScaleDocumentTests
{
    /// <summary>
    /// The GIF working-size cap shrinks the canvas, so every absolute pixel value must shrink
    /// with it, or blur and border come out visibly heavier on the GIF than in the preview.
    /// </summary>
    [Fact]
    public void ScalesBackgroundBlurAndBorder()
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 2000, Height = 2000 } };
        doc.Background.Blur = 10f;
        doc.Effects.Border = new BorderSettings { Enabled = true, Thickness = 8, Radius = 20, Gap = 6 };

        var scaled = CoverArtService.ScaleDocumentForCanvas(doc, 1000, 1000, 0.5f);

        Assert.Equal(5f, scaled.Background.Blur, 3);
        Assert.Equal(4, scaled.Effects.Border.Thickness);
        Assert.Equal(10, scaled.Effects.Border.Radius);
        Assert.Equal(3, scaled.Effects.Border.Gap);
        Assert.Equal(10f, doc.Background.Blur, 3); // source untouched
    }

    [Fact]
    public void ANonZeroBorderNeverScalesAway()
    {
        var doc = new CoverDocument();
        doc.Effects.Border = new BorderSettings { Thickness = 1, Gap = 1 };

        var scaled = CoverArtService.ScaleDocumentForCanvas(doc, 100, 100, 0.3f);

        Assert.Equal(1, scaled.Effects.Border.Thickness);
        Assert.Equal(1, scaled.Effects.Border.Gap);
    }
}
