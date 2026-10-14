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
    /// so only the shadow lands. The device matrix is built from px, py and the rotation
    /// directly (both callers enter with an identity CTM): getTransform/DOMMatrix do not
    /// exist on older engines, and a throw there aborted the whole render mid-save.
    /// </summary>
    [Fact]
    public void ShadowPassPaintsNoGlyphs()
    {
        var draw = FunctionBody("drawTextLayer");
        Assert.Contains("drawTextShadowOnly(ctx, layer, fontPx, k, px, py);", draw, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(draw, @"fillText\("));
        Assert.DoesNotContain("shadowColor", draw, StringComparison.Ordinal);

        var shadow = FunctionBody("drawTextShadowOnly");
        Assert.Contains("var r = (layer.Rotation || 0) * Math.PI / 180, c = Math.cos(r), s = Math.sin(r);", shadow, StringComparison.Ordinal);
        Assert.Contains("ctx.setTransform(c, s, -s, c, px - shift, py);", shadow, StringComparison.Ordinal);
        Assert.Contains("Math.abs(px)", shadow, StringComparison.Ordinal);
        var page = ConfigPage();
        Assert.DoesNotContain("DOMMatrix", page, StringComparison.Ordinal);
        Assert.DoesNotContain("getTransform", page, StringComparison.Ordinal);
        Assert.Contains("ctx.shadowOffsetX = Math.max(-50, Math.min(50, layer.Shadow.OffsetX)) * k + shift;", shadow, StringComparison.Ordinal);
        Assert.Contains("ctx.shadowOffsetY = Math.max(-50, Math.min(50, layer.Shadow.OffsetY)) * k;", shadow, StringComparison.Ordinal);
        Assert.Contains("ctx.shadowBlur = Math.max(0, Math.min(50, layer.Shadow.Blur)) * k;", shadow, StringComparison.Ordinal);
    }

    /// <summary>
    /// An uploaded font is registered as a single weight-400 FontFace, and the server
    /// renders custom fonts at their own weight (CreateFont ignores Weight for them).
    /// Requesting e.g. 700 from the canvas made the browser synthesise a bold that the
    /// applied cover never has. Only while the custom face is actually ready, though: while
    /// it is loading or failed, fontFamilyFor falls back to Noto Sans, and the server's own
    /// fallback (CreateFont) renders Noto at layer.Weight.
    /// </summary>
    [Fact]
    public void CustomFontsAreRequestedAtTheirOwnWeight()
    {
        var body = FunctionBody("canvasFont");
        Assert.Contains("var entry = layer.FontPath && _customFonts[layer.FontPath];", body, StringComparison.Ordinal);
        Assert.Contains("var weight = entry && entry.state === 'ready' ? 400 : layer.Weight;", body, StringComparison.Ordinal);
        Assert.Contains("entry.state === 'ready'", FunctionBody("fontFamilyFor"), StringComparison.Ordinal);
        Assert.Contains("return weight + ' ' + fontPx + 'px ' + fontFamilyFor(layer);", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// With fewer than two Stops the server draws StartColor to EndColor (BuildColorStops),
    /// but the canvas drew black to white and the stop editor showed the brand colours without
    /// writing them back: three different gradients for one legacy template. The client
    /// fallback must be the server's, including its defaults.
    /// </summary>
    [Fact]
    public void GradientFallbackMatchesTheServer()
    {
        var page = ConfigPage();
        var start = Regex.Match(page, @"var GRADIENT_FALLBACK_START = '(#[0-9a-fA-F]{6})';");
        var end = Regex.Match(page, @"var GRADIENT_FALLBACK_END = '(#[0-9a-fA-F]{6})';");
        Assert.True(start.Success && end.Success, "Gradient fallback constants not found.");

        var defaults = new GradientSettings();
        Assert.Equal(defaults.StartColor, start.Groups[1].Value, ignoreCase: true);
        Assert.Equal(defaults.EndColor, end.Groups[1].Value, ignoreCase: true);

        var stops = FunctionBody("gradientStops");
        Assert.Contains("gradientFallbackStops(g)", stops, StringComparison.Ordinal);
        Assert.DoesNotContain("'#ffffff'", stops, StringComparison.Ordinal);
    }

    /// <summary>
    /// Migration runs on every load path (init, template load, undo/redo snapshot), the
    /// same three places as the other client normalizers, so the document, the stop editor
    /// and the canvas all hold the two colours the server will draw.
    /// </summary>
    [Fact]
    public void GradientStopsAreMigratedOnEveryLoadPath()
    {
        var calls = Regex.Matches(ConfigPage(), @"normalizeGradientStops\(doc\);").Count;
        Assert.True(calls == 3, $"Expected normalizeGradientStops(doc) at init, template load and restoreSnapshot; found {calls}.");
        Assert.Contains("normalizeGradientStops(doc);", FunctionBody("restoreSnapshot"), StringComparison.Ordinal);
        Assert.Contains("g.Stops = gradientFallbackStops(g);", FunctionBody("normalizeGradientStops"), StringComparison.Ordinal);
    }

    /// <summary>
    /// A null entry in Stops is skipped by the server (BuildColorStops, DocumentMigration)
    /// before the two-stop check. The client sorted it (a.Position on null throws) or counted
    /// it as a real stop. Both client paths now drop nulls first.
    /// </summary>
    [Fact]
    public void NullGradientStopsAreDroppedBeforeTheTwoStopCheck()
    {
        var normalize = FunctionBody("normalizeGradientStops");
        Assert.Contains("g.Stops = (g.Stops || []).filter(Boolean);", normalize, StringComparison.Ordinal);
        Assert.True(normalize.IndexOf(".filter(Boolean)", StringComparison.Ordinal) < normalize.IndexOf("< 2", StringComparison.Ordinal),
            "normalizeGradientStops must drop null stops before the two-stop check.");

        var stops = FunctionBody("gradientStops");
        Assert.Contains("var stops = (g.Stops || []).filter(Boolean).sort(", stops, StringComparison.Ordinal);
    }
}
