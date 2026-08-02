using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// Structural guards over the embedded config page. The element-id pin is the important
/// one: regrouping ~3,000 lines of markup in place is exactly the operation that silently
/// drops a control, and a dropped control means a dead handler with no error anywhere —
/// <c>el('ccaFoo')</c> simply returns null and the feature quietly stops working.
/// </summary>
public class ConfigPageStructureTests
{
    private static string ConfigPage()
    {
        using var stream = typeof(Plugin).Assembly
            .GetManifestResourceStream("CustomCoverArt.Configuration.configPage.html");
        Assert.NotNull(stream);
        using var reader = new System.IO.StreamReader(stream!);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Just the markup, stopping at the script block. Element-id assertions must not see
    /// JS comments: the canvas engine's header comment mentions
    /// <c>&lt;canvas id="ccaCanvas"&gt;</c>, which would both fake a duplicate and let a
    /// genuinely deleted control pass unnoticed.
    /// </summary>
    private static string Markup()
    {
        var page = ConfigPage();
        var scriptAt = page.IndexOf("<script type=\"text/javascript\">", System.StringComparison.Ordinal);
        Assert.True(scriptAt > 0, "Could not find the script block.");
        return page.Substring(0, scriptAt);
    }

    // Every cca* element id present at the start of the guided-editor restructure, minus
    // ccaPreviewSpinner (dead since Phase 1, removed here) and ccaGradient (replaced by the
    // background source dropdown). Add to this list when you add a control; never remove
    // from it without deleting the control deliberately.
    private static readonly string[] RequiredIds = new[]
    {
        "ccaAddImage", "ccaAddStop", "ccaAddText", "ccaAnimDelay", "ccaAnimDelayVal", "ccaAnimDir",
        "ccaAnimFrames", "ccaAnimFramesVal", "ccaAnimHint", "ccaAnimRow", "ccaApplyBtn",
        "ccaAutoPalette", "ccaBatchApply", "ccaBatchList", "ccaBatchStatus", "ccaBgAdjust",
        "ccaBgFit", "ccaBgImage", "ccaBgImageBtn", "ccaBgImageName", "ccaBgSource", "ccaBlur",
        "ccaBlurVal", "ccaBrowseBtn", "ccaBrowserClose", "ccaBrowserGrid", "ccaBrowserModal",
        "ccaBrowserNext", "ccaBrowserPage", "ccaBrowserPrev", "ccaBrowserSearch", "ccaBrowserType",
        "ccaCanvas", "ccaCollageDensity", "ccaCollageRow", "ccaCollageShuffle", "ccaCustomDims",
        "ccaDim", "ccaDimColor", "ccaDimVal", "ccaDownloadBtn", "ccaFont", "ccaFontBtn",
        "ccaFontName", "ccaFormat", "ccaFxBorder", "ccaFxBorderColor", "ccaFxBorderDouble",
        "ccaFxBorderGap", "ccaFxBorderGapRow", "ccaFxBorderGapVal", "ccaFxBorderRadius",
        "ccaFxBorderRadiusVal", "ccaFxBorderRow", "ccaFxBorderThickness", "ccaFxBorderThicknessVal",
        "ccaFxGrain", "ccaFxGrainAmount", "ccaFxGrainAmountVal", "ccaFxGrainRow", "ccaFxSoftLight",
        "ccaFxSoftLightColor", "ccaFxSoftLightOpacity", "ccaFxSoftLightOpacityVal",
        "ccaFxSoftLightRow", "ccaFxVignette", "ccaFxVignetteAmount", "ccaFxVignetteAmountVal",
        "ccaFxVignetteRow", "ccaFxVignetteSoftness", "ccaFxVignetteSoftnessVal", "ccaGradientAngle",
        "ccaGradientAngleRow", "ccaGradientAngleVal", "ccaGradientOpts", "ccaGradientStops",
        "ccaGradientType", "ccaHeight", "ccaKenBurns", "ccaLayerImage", "ccaLayerList",
        "ccaLayerOpacity", "ccaLayerOpacityVal", "ccaLayerRotation", "ccaLayerRotationVal",
        "ccaLibrarySelect", "ccaNoLayerHint", "ccaOutline", "ccaPreset", "ccaPresetJf",
        "ccaPreviewPlaceholder", "ccaRestoreBtn", "ccaRestoreHint", "ccaSelectedBg",
        "ccaSelectedBgName", "ccaServerRender", "ccaServerRenderImg", "ccaServerRenderWrap",
        "ccaSettings", "ccaShadow", "ccaSwatches", "ccaTargetType", "ccaTemplateDelete",
        "ccaTemplateName", "ccaTemplateSave", "ccaTemplateSelect", "ccaTextAlign", "ccaTextColor",
        "ccaTextSize", "ccaTextSizeVal", "ccaTextWeight", "ccaTitle", "ccaUndo", "ccaRedo",
        "ccaUploadControls", "ccaWidth"
    };

    [Fact]
    public void NoControlWasLostInTheRestructure()
    {
        var markup = Markup();
        var missing = RequiredIds
            .Where(id => !markup.Contains($"id=\"{id}\"", System.StringComparison.Ordinal))
            .ToList();
        Assert.True(missing.Count == 0, "Element ids missing from configPage.html: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryIdAppearsExactlyOnce()
    {
        var markup = Markup();
        var duplicated = RequiredIds
            .Where(id => Regex.Matches(markup, $"id=\"{id}\"").Count > 1)
            .ToList();
        Assert.True(duplicated.Count == 0, "Duplicate element ids: " + string.Join(", ", duplicated));
    }

    [Fact]
    public void EveryStepIsWellFormed()
    {
        var page = Markup();
        var steps = Regex.Matches(page, @"<section class=""ccaStep"" data-step=""(\d)"">");
        Assert.Equal(5, steps.Count);

        foreach (Match step in steps)
        {
            var n = step.Groups[1].Value;
            Assert.Contains($"aria-controls=\"ccaStepBody{n}\"", page, System.StringComparison.Ordinal);
            Assert.Contains($"id=\"ccaStepBody{n}\"", page, System.StringComparison.Ordinal);
        }

        // One header and one body per step, no more.
        Assert.Equal(5, Regex.Matches(page, @"class=""ccaStepHead""").Count);
        Assert.Equal(5, Regex.Matches(page, @"class=""ccaStepBody""").Count);
    }

    [Fact]
    public void StepsAreNumberedOneThroughFive()
    {
        var page = Markup();
        var numbers = Regex.Matches(page, @"<section class=""ccaStep"" data-step=""(\d)"">")
            .Select(m => m.Groups[1].Value)
            .OrderBy(s => s)
            .ToArray();
        Assert.Equal(new[] { "1", "2", "3", "4", "5" }, numbers);
    }

    [Fact]
    public void DeadPreviewSpinnerIsGone()
    {
        // Unused since Phase 1 — the canvas renders synchronously, so nothing ever showed it.
        Assert.DoesNotContain("ccaPreviewSpinner", ConfigPage(), System.StringComparison.Ordinal);
    }
}
