using System;
using System.Text.RegularExpressions;
using CustomCoverArt.Models;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// Structural guards for the canvas preview's parity with the server renderer. There is
/// no JS test runner, so these pin the shape of the drawing code that keeps the preview
/// honest. Kept apart from ConfigPageStructureTests so editor-UX work on that file does
/// not collide with this one.
/// </summary>
public class RenderParityPageTests
{
    private static string ConfigPage()
    {
        using var stream = typeof(Plugin).Assembly
            .GetManifestResourceStream("CustomCoverArt.Configuration.configPage.html");
        Assert.NotNull(stream);
        using var reader = new System.IO.StreamReader(stream!);
        return reader.ReadToEnd();
    }

    // A top-level script function from its declaration to its closing brace, which sits at
    // the script's 8-space function indentation (the convention ConfigPageStructureTests
    // already relies on; nested blocks close deeper, so they never match).
    private static string FunctionBody(string name)
    {
        var page = ConfigPage();
        var start = page.IndexOf("function " + name + "(", StringComparison.Ordinal);
        Assert.True(start > 0, $"function {name} not found in configPage.html.");
        var end = page.IndexOf("\n        }", start, StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find the end of function {name}.");
        return page.Substring(start, end - start);
    }

    /// <summary>
    /// Font size, shadow and outline are pixel values authored at doc.Canvas.Height. The
    /// in-context thumbnails (320x180, 200x200, 150x225) used to draw them at full size, so
    /// a 1080-tall design's title overflowed its 180px thumbnail. Everything now scales by
    /// H / doc.Canvas.Height, the factor ScaleDocumentForCanvas uses on the server.
    /// </summary>
    [Fact]
    public void TextPixelValuesAreScaledToTheTargetCanvas()
    {
        Assert.Contains("H / doc.Canvas.Height", FunctionBody("textScale"), StringComparison.Ordinal);
        // The server clamps AFTER scaling: Clamp(Size * scaledCanvasHeight, 8, 1024).
        Assert.Contains("Math.max(8, Math.min(1024, layer.Size * H))", FunctionBody("textFontPx"), StringComparison.Ordinal);

        var draw = FunctionBody("drawTextLayer");
        Assert.Contains("var k = textScale(H);", draw, StringComparison.Ordinal);
        Assert.Contains("var fontPx = textFontPx(layer, H);", draw, StringComparison.Ordinal);
        // Server order: round the scaled width, then clamp to [0, 10].
        Assert.Contains("Math.min(10, Math.max(0, Math.round(", draw, StringComparison.Ordinal);
        Assert.Contains("ctx.lineWidth = outlineWidth * 2;", draw, StringComparison.Ordinal);

        Assert.Contains("var fontPx = textFontPx(layer, H);", FunctionBody("textLayerBounds"), StringComparison.Ordinal);

        // The unscaled expression lives in exactly one place now: textFontPx.
        var raw = Regex.Matches(ConfigPage(), @"layer\.Size \* (doc\.Canvas\.Height|H)\)").Count;
        Assert.True(raw == 1, $"Expected the font-size expression only inside textFontPx; found {raw}.");
    }

    /// <summary>
    /// A shadowed fillText paints the glyphs too, and drawTextLayer's real fill then paints
    /// them again, so at Opacity &lt; 1 the preview's text came out denser than the server's
    /// single fill. The shadow pass now moves the glyphs off the bitmap in device space and
    /// pulls the shadow back by the same amount. Canvas shadow offsets ignore the transform,
    /// so only the shadow lands.
    /// </summary>
    [Fact]
    public void ShadowPassPaintsNoGlyphs()
    {
        var draw = FunctionBody("drawTextLayer");
        Assert.Contains("drawTextShadowOnly(ctx, layer, fontPx, k);", draw, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(draw, @"fillText\("));
        Assert.DoesNotContain("shadowColor", draw, StringComparison.Ordinal);

        var shadow = FunctionBody("drawTextShadowOnly");
        Assert.Contains("ctx.setTransform(new DOMMatrix([1, 0, 0, 1, -shift, 0]).multiply(m));", shadow, StringComparison.Ordinal);
        Assert.Contains("ctx.shadowOffsetX = Math.max(-50, Math.min(50, layer.Shadow.OffsetX)) * k + shift;", shadow, StringComparison.Ordinal);
        Assert.Contains("ctx.shadowOffsetY = Math.max(-50, Math.min(50, layer.Shadow.OffsetY)) * k;", shadow, StringComparison.Ordinal);
        Assert.Contains("ctx.shadowBlur = Math.max(0, Math.min(50, layer.Shadow.Blur)) * k;", shadow, StringComparison.Ordinal);
    }

    /// <summary>
    /// An uploaded font is registered as a single weight-400 FontFace, and the server
    /// renders custom fonts at their own weight (CreateFont ignores Weight for them).
    /// Requesting e.g. 700 from the canvas made the browser synthesise a bold that the
    /// applied cover never has.
    /// </summary>
    [Fact]
    public void CustomFontsAreRequestedAtTheirOwnWeight()
    {
        var body = FunctionBody("canvasFont");
        Assert.Contains("var weight = layer.FontPath ? 400 : layer.Weight;", body, StringComparison.Ordinal);
        Assert.Contains("return weight + ' ' + fontPx + 'px ' + fontFamilyFor(layer);", body, StringComparison.Ordinal);
    }
}
