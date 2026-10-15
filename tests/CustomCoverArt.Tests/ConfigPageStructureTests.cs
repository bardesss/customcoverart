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

    // Body of a top-level `function name(` in the page script (8-space indent), up to its
    // closing brace.
    private static string FunctionBody(string page, string name)
    {
        var start = page.IndexOf("function " + name + "(", System.StringComparison.Ordinal);
        Assert.True(start > 0, $"function {name} not found.");
        var end = page.IndexOf("\n        }", start, System.StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find the end of {name}.");
        return page.Substring(start, end - start);
    }

    // Body of an event handler registered inside bindEvents (12-space indent).
    private static string HandlerBody(string page, string anchor)
    {
        var start = page.IndexOf(anchor, System.StringComparison.Ordinal);
        Assert.True(start > 0, $"{anchor} not found.");
        var end = page.IndexOf("\n            });", start, System.StringComparison.Ordinal);
        Assert.True(end > start, $"Could not find the end of {anchor}.");
        return page.Substring(start, end - start);
    }

    /// <summary>The page's one &lt;style&gt; block.</summary>
    private static string Styles()
    {
        var page = ConfigPage();
        var start = page.IndexOf("<style>", System.StringComparison.Ordinal);
        Assert.True(start > 0, "Could not find the style block.");
        var end = page.IndexOf("</style>", start, System.StringComparison.Ordinal);
        Assert.True(end > start, "Could not find the end of the style block.");
        return page.Substring(start, end - start);
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
        "ccaBrowserNext", "ccaBrowserPage", "ccaBrowserPrev", "ccaBrowserSearch", "ccaBrowserTitle", "ccaBrowserType",
        "ccaCanvas", "ccaCollageDensity", "ccaCollageLiveTvHint", "ccaCollageRow", "ccaCollageShuffle",
        "ccaCustomDims",
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
        "ccaLibrarySelect", "ccaNoLayerHint", "ccaOutline",
        "ccaOverlay", "ccaOverlayAddStop", "ccaOverlayAngle", "ccaOverlayAngleVal",
        "ccaOverlayOpts", "ccaOverlayPreset", "ccaOverlayStops",
        "ccaPreset", "ccaPresetJf",
        "ccaPreviewPlaceholder", "ccaRestoreBtn", "ccaRestoreHint", "ccaSelectedBg",
        "ccaSelectedBgName", "ccaServerRender", "ccaServerRenderImg", "ccaServerRenderWrap",
        "ccaSettings", "ccaShadow", "ccaSwatches", "ccaTargetType", "ccaTemplateDelete",
        "ccaTemplateName", "ccaTemplateSave", "ccaTemplateSelect", "ccaTextAlign", "ccaTextColor",
        "ccaContexts", "ccaTextSize", "ccaTextSizeVal", "ccaTextWeight", "ccaTitle", "ccaUndo", "ccaRedo",
        "ccaUploadControls", "ccaWidth",
        "ccaResetBackupBtn", "ccaCollageSpinner", "ccaCollageHint", "ccaGifHolder"
    };

    /// <summary>
    /// Live TV has no child posters, so updateBackgroundVisibility disables the collage
    /// option and force-reverts an already-selected collage back to an image. Both are
    /// correct and both used to happen in total silence — the user's background choice
    /// changed under them with nothing said. The explanation string existed all along and
    /// was bound to nothing, so this pins the binding rather than the string.
    /// </summary>
    [Fact]
    public void LiveTvCollageHint_ExistsAndIsOwnedByTheBackgroundVisibilityFunction()
    {
        Assert.Contains("id=\"ccaCollageLiveTvHint\"", Markup(), System.StringComparison.Ordinal);
        Assert.Contains("data-i18n=\"bg.collage.livetv\"", Markup(), System.StringComparison.Ordinal);

        // The codebase's one-owner-per-element rule: the function that hides the collage
        // controls is the only thing allowed to show the hint explaining why.
        var page = ConfigPage();
        var fn = page.IndexOf("function updateBackgroundVisibility()", System.StringComparison.Ordinal);
        Assert.True(fn > 0, "updateBackgroundVisibility not found.");
        var end = page.IndexOf("\n        }", fn, System.StringComparison.Ordinal);
        Assert.True(end > fn, "Could not find the end of updateBackgroundVisibility.");

        var body = page.Substring(fn, end - fn);
        Assert.Contains("ccaCollageLiveTvHint", body, System.StringComparison.Ordinal);

        var owners = Regex.Matches(page, @"ccaCollageLiveTvHint").Count;
        Assert.True(owners == 2, $"Expected ccaCollageLiveTvHint in exactly the markup and its owner; found {owners} references.");
    }

    /// <summary>
    /// Jellyfin 12 no longer accepts the legacy <c>X-Emby-Token</c> header: a request
    /// carrying it is challenged by <c>CustomAuthenticationHandler</c> and comes back as a
    /// 401 with an EMPTY body, so the page's <c>.json()</c> throws "Unexpected end of JSON
    /// input" and every plugin call fails with an error that names neither auth nor the
    /// status code. Verified against a live 12.0.0 server: X-Emby-Token → 401,
    /// the MediaBrowser Authorization scheme → 200.
    ///
    /// The scheme is what Jellyfin Web itself sends and is accepted by 10.11 and 12 alike,
    /// so this is a straight replacement, not a version fork.
    /// </summary>
    [Fact]
    public void AuthenticatesWithTheMediaBrowserAuthorizationScheme()
    {
        var page = ConfigPage();
        // Match the header being SENT (a quoted key in a headers object), not the name
        // appearing in the comment above authHeader() that explains why it is gone.
        Assert.DoesNotContain("'X-Emby-Token'", page, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"X-Emby-Token\"", page, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MediaBrowser Client=", page, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Every authenticated request must go through the one header builder. A raw
    /// <c>fetch(</c> that assembles its own headers is how the poster-image download at the
    /// end of the browser flow kept the dead token header after the API calls were fixed.
    /// </summary>
    [Fact]
    public void NoFetchBuildsItsOwnAuthorizationHeader()
    {
        var page = ConfigPage();
        // The builder's own return statement is the one legitimate place the literal appears.
        var occurrences = Regex.Matches(page, @"MediaBrowser Client=").Count;
        Assert.True(occurrences == 1,
            $"Expected the MediaBrowser header to be built in exactly one place; found {occurrences}.");
    }

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

    /// <summary>
    /// TIFF is not offered: the server refuses it, and browsers cannot draw it on the canvas.
    /// </summary>
    [Fact]
    public void BackgroundUpload_DoesNotOfferTiff()
    {
        var page = ConfigPage();
        Assert.DoesNotContain(".tif", page, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TIFF", page);
    }

    /// <summary>
    /// Reset backup deletes the target's restore point, so it must ask first, call the
    /// server's DELETE route, and leave both buttons in step with what is left.
    /// </summary>
    [Fact]
    public void ResetBackup_AsksFirst_DeletesTheBackup_AndRefreshesTheButtons()
    {
        var page = ConfigPage();
        Assert.Contains("data-i18n=\"restore.reset\"", Markup(), System.StringComparison.Ordinal);

        var handler = HandlerBody(page, "el('ccaResetBackupBtn').addEventListener('click'");
        Assert.Contains("confirmAction(t('restore.resetConfirm'))", handler, System.StringComparison.Ordinal);
        Assert.Contains("'/backup', 'DELETE')", handler, System.StringComparison.Ordinal);
        Assert.Contains(".then(refreshRestoreState)", handler, System.StringComparison.Ordinal);

        Assert.Contains("ccaResetBackupBtn", FunctionBody(page, "refreshRestoreState"), System.StringComparison.Ordinal);
    }

    [Fact]
    public void ConfirmAction_UsesJellyfinsDialog_WithTheBrowsersAsFallback()
    {
        var body = FunctionBody(ConfigPage(), "confirmAction");
        Assert.Contains("Dashboard.confirm(", body, System.StringComparison.Ordinal);
        Assert.Contains("window.confirm(", body, System.StringComparison.Ordinal);
    }

    /// <summary>The first apply creates the backup, so Restore and Reset come alive right away.</summary>
    [Fact]
    public void ApplyingRefreshesTheRestoreButtons()
    {
        Assert.Contains("refreshRestoreState();", FunctionBody(ConfigPage(), "applyCoverArt"), System.StringComparison.Ordinal);
    }

    /// <summary>A slow answer for a previous target must not enable the buttons of the current one.</summary>
    [Fact]
    public void RefreshRestoreState_IgnoresAnAnswerForAnotherTarget()
    {
        var body = FunctionBody(ConfigPage(), "refreshRestoreState");
        Assert.Contains("var requestedId = state.libraryId;", body, System.StringComparison.Ordinal);
        Assert.Contains("if (requestedId !== state.libraryId) { return; }", body, System.StringComparison.Ordinal);
    }

    /// <summary>Switching the collage's target drops the old target's posters at once.</summary>
    [Fact]
    public void CollageSourceChange_ReleasesTheCurrentMosaic()
    {
        var body = FunctionBody(ConfigPage(), "syncCollageBitmap");
        Assert.Contains("_collageSourceId", body, System.StringComparison.Ordinal);
        Assert.Contains("releaseCollageBitmap();", body, System.StringComparison.Ordinal);
        Assert.Contains("var _collageSourceId", ConfigPage(), System.StringComparison.Ordinal);
    }

    /// <summary>A batch can include the selected target, so its buttons need a refresh afterwards.</summary>
    [Fact]
    public void BatchApply_RefreshesTheRestoreButtons()
    {
        var handler = HandlerBody(ConfigPage(), "el('ccaBatchApply').addEventListener('click'");
        Assert.Contains("refreshRestoreState();", handler, System.StringComparison.Ordinal);
    }

    [Fact]
    public void DestructiveActionsAskFirst()
    {
        var page = ConfigPage();
        Assert.Contains("confirmAction(t('restore.confirm'))", page, System.StringComparison.Ordinal);
        Assert.Contains("confirmAction(t('restore.resetConfirm'))", page, System.StringComparison.Ordinal);
        Assert.Contains("confirmAction(t('tpl.confirmDelete', name))", page, System.StringComparison.Ordinal);
        Assert.Contains("confirmAction(t('batch.confirm', targets.length))", page, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Batch apply used to have no guard and no error path: a second click started a
    /// second batch, and a failure left "Applying…" on screen forever.
    /// </summary>
    [Fact]
    public void BatchApplyCannotRunTwiceAndAlwaysFinishes()
    {
        var handler = HandlerBody(ConfigPage(), "el('ccaBatchApply').addEventListener('click'");
        Assert.Contains("state.batchRunning", handler, System.StringComparison.Ordinal);
        Assert.Contains("btn.disabled = true", handler, System.StringComparison.Ordinal);
        Assert.Contains(".catch(", handler, System.StringComparison.Ordinal);
        Assert.Contains("t('batch.fail'", handler, System.StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreRefreshesItsHint_AndStatusLinesAreAnnounced()
    {
        var restore = HandlerBody(ConfigPage(), "el('ccaRestoreBtn').addEventListener('click'");
        Assert.Contains(".then(refreshRestoreState)", restore, System.StringComparison.Ordinal);
        var markup = Markup();
        Assert.Matches(@"id=""ccaRestoreHint""[^>]*aria-live=""polite""", markup);
        Assert.Matches(@"id=""ccaBatchStatus""[^>]*aria-live=""polite""", markup);
    }

    /// <summary>
    /// The canvas draws dim, blur, pan/zoom, overlay, effects and layers itself. The server
    /// must therefore return the bare mosaic only, or each would be applied twice.
    /// </summary>
    [Fact]
    public void CollagePreview_AsksTheServerForTheBareMosaicOnly()
    {
        var body = FunctionBody(ConfigPage(), "collageRequestDocument");
        foreach (var expected in new[]
        {
            "Layers: []", "Effects: defaultEffects()", "Overlay: null", "Gradient: null",
            "Blur: 0, Dim: 0", "OffsetX: 0, OffsetY: 0, Scale: 1", "Format: 'png'",
            "Collage: buildCollageFromControls()",
        })
        {
            Assert.Contains(expected, body, System.StringComparison.Ordinal);
        }
    }

    /// <summary>Each request is a full server-side collage, so only collage inputs may trigger one.</summary>
    [Fact]
    public void CollagePreview_IsKeyedOnTheCollageInputsAlone()
    {
        var page = ConfigPage();
        var key = FunctionBody(page, "collageKey");
        foreach (var input in new[] { "c.SourceId", "c.Density", "c.Seed", "doc.Canvas.Width", "doc.Canvas.Height" })
        {
            Assert.Contains(input, key, System.StringComparison.Ordinal);
        }
        foreach (var unrelated in new[] { "Layers", "Effects", "Transform", "Dim", "Blur" })
        {
            Assert.DoesNotContain(unrelated, key, System.StringComparison.Ordinal);
        }

        Assert.Contains("syncCollageBitmap();", FunctionBody(page, "renderDocument"), System.StringComparison.Ordinal);
        Assert.Contains("debounce(fetchCollageBitmap, 400)", page, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A slow, stale response must never overwrite a newer one: fast typing in the poster
    /// search rendered whichever page arrived last, and a quick Type switch could fill the
    /// target and batch lists with the previous type's targets. (The backup check and the
    /// collage preview have their own guards: requestedId and _collageToken.)
    /// </summary>
    [Fact]
    public void ListRequestsAreLatestWins()
    {
        var page = ConfigPage();
        Assert.Contains("_requestSeq[channel] === n", FunctionBody(page, "requestGuard"), System.StringComparison.Ordinal);
        foreach (var fn in new[] { "loadLibraries", "loadBatchList", "loadBrowserItems" })
        {
            var body = FunctionBody(page, fn);
            Assert.Contains("requestGuard(", body, System.StringComparison.Ordinal);
            Assert.Contains("if (!isCurrent()) { return; }", body, System.StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CollagePreview_DropsStaleAnswers_AndRevokesItsUrls()
    {
        var page = ConfigPage();
        var fetch = FunctionBody(page, "fetchCollageBitmap");
        Assert.Contains("token !== _collageToken", fetch, System.StringComparison.Ordinal);
        Assert.Contains("URL.revokeObjectURL(url)", fetch, System.StringComparison.Ordinal);
        Assert.Contains("URL.revokeObjectURL(_collageUrl)", FunctionBody(page, "releaseCollageBitmap"), System.StringComparison.Ordinal);
        Assert.Contains("_collageToken++", FunctionBody(page, "syncCollageBitmap"), System.StringComparison.Ordinal);
    }

    [Fact]
    public void TheCanvasPaintsTheBitmapOfTheChosenSource()
    {
        var page = ConfigPage();
        Assert.Contains("collageBgEl", FunctionBody(page, "activeBgBitmap"), System.StringComparison.Ordinal);
        var paint = FunctionBody(page, "paintBackgroundOnly");
        Assert.Contains("activeBgBitmap()", paint, System.StringComparison.Ordinal);
        Assert.DoesNotContain("bgImageEl", paint, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// One rAF loop at ~15 fps, only while an animated GIF is the visible background and the
    /// user has not asked for reduced motion; every way of leaving stops it.
    /// </summary>
    [Fact]
    public void GifPreview_RunsOneThrottledLoop_OnlyWhileItCanBeSeen()
    {
        var page = ConfigPage();
        var wanted = FunctionBody(page, "gifLoopWanted");
        Assert.Contains("_ccaAnimatedGif", wanted, System.StringComparison.Ordinal);
        Assert.Contains("activeBgBitmap() === bgImageEl", wanted, System.StringComparison.Ordinal);
        Assert.Contains("isEditorOnScreen()", wanted, System.StringComparison.Ordinal);
        Assert.Contains("_reducedMotion", wanted, System.StringComparison.Ordinal);
        Assert.Contains("matchMedia('(prefers-reduced-motion: reduce)')", page, System.StringComparison.Ordinal);
        Assert.Contains("document.hidden", FunctionBody(page, "isEditorOnScreen"), System.StringComparison.Ordinal);

        var update = FunctionBody(page, "updateGifLoop");
        Assert.Contains("if (!_gifLoopId)", update, System.StringComparison.Ordinal);
        Assert.Contains("stopGifLoop()", update, System.StringComparison.Ordinal);

        var tick = FunctionBody(page, "gifTick");
        Assert.Contains("GIF_FRAME_MS", tick, System.StringComparison.Ordinal);
        Assert.Contains("renderDocument(true)", tick, System.StringComparison.Ordinal);
        Assert.Contains("var GIF_FRAME_MS = 1000 / 15;", page, System.StringComparison.Ordinal);

        var listeners = FunctionBody(page, "bindGifLoopListeners");
        Assert.Contains("'visibilitychange'", listeners, System.StringComparison.Ordinal);
        Assert.Contains("'pagehide'", listeners, System.StringComparison.Ordinal);
        Assert.Contains("bindGifLoopListeners();", FunctionBody(page, "bindEvents"), System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The palette and in-context refreshers are 300 ms debounces: called 15 times a second
    /// they would never fire, so animation frames must skip them.
    /// </summary>
    [Fact]
    public void GifPreview_AnimationFramesSkipTheDebouncedExtras()
    {
        var render = FunctionBody(ConfigPage(), "renderDocument");
        Assert.Contains("function renderDocument(animFrame)", render, System.StringComparison.Ordinal);
        Assert.Contains("if (!animFrame)", render, System.StringComparison.Ordinal);
        Assert.Contains("updateGifLoop();", render, System.StringComparison.Ordinal);
    }

    [Fact]
    public void GifPreview_BothBackgroundPathsWatchForAnimation()
    {
        // Every background bitmap goes through setBackgroundBitmap, which flags a GIF: the
        // upload, the poster pick, and the undo/template re-hydration alike.
        var page = ConfigPage();
        Assert.Contains("watchForAnimatedGif(img, blob);", FunctionBody(page, "setBackgroundBitmap"), System.StringComparison.Ordinal);
        Assert.Contains("setBackgroundBitmap(file, isCurrent);", page, System.StringComparison.Ordinal);
        Assert.Contains("setBackgroundBitmap(posterBlob, isCurrent);", page, System.StringComparison.Ordinal);
        Assert.Contains("isAnimatedGif(buf)", FunctionBody(page, "watchForAnimatedGif"), System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The steps stopped being .ccaCard in the guided-editor redesign, which silently
    /// orphaned every .ccaCard-scoped field rule: controls after a grid sat flush, and the
    /// rest fell back to Jellyfin's 1.8em .inputContainer margin.
    /// </summary>
    [Fact]
    public void StepSpacingIsScopedToStepBodies_NotTheRetiredCardClass()
    {
        var css = Styles();
        Assert.DoesNotContain(".ccaCard .inputContainer", css, System.StringComparison.Ordinal);
        Assert.DoesNotContain(".ccaCard > .inputContainer", css, System.StringComparison.Ordinal);
        Assert.DoesNotContain(".ccaCard > .ccaFileRow", css, System.StringComparison.Ordinal);
        Assert.Contains(".ccaStepBody .inputContainer", css, System.StringComparison.Ordinal);
        Assert.Contains(".ccaStepBody .ccaGrid", css, System.StringComparison.Ordinal);
        Assert.Contains(".ccaStepBody > :last-child", css, System.StringComparison.Ordinal);
        // A dropdown and the button beside it share one height.
        Assert.Contains(".ccaFileRow .ccaGrow ~ .emby-button", css, System.StringComparison.Ordinal);
    }

    [Fact]
    public void NoInputContainerIsNestedDirectlyInAnother()
    {
        Assert.DoesNotMatch(@"class=""inputContainer[^""]*"">\s*<div class=""inputContainer""", Markup());
    }

    /// <summary>
    /// Jellyfin reuses the page: switching the display language from Dutch back to
    /// English used to leave every Dutch label in place because English was skipped.
    /// </summary>
    [Fact]
    public void ApplyI18n_RunsForEnglishToo_AndTranslatesAttributes()
    {
        var page = ConfigPage();
        Assert.DoesNotContain("LANG === 'en'", FunctionBody(page, "applyI18n"), System.StringComparison.Ordinal);
        Assert.Contains("['data-i18n-aria', 'aria-label']", page, System.StringComparison.Ordinal);
        Assert.Contains("['data-i18n-alt', 'alt']", page, System.StringComparison.Ordinal);
    }

    [Fact]
    public void NoHardCodedEnglishIsLeftInTheMarkupOrDefaults()
    {
        var markup = Markup();
        Assert.DoesNotContain("value=\"Movies\"", markup, System.StringComparison.Ordinal);
        Assert.DoesNotContain("alt=\"Server render\"", markup, System.StringComparison.Ordinal);
        Assert.DoesNotContain(">Page 1<", markup, System.StringComparison.Ordinal);
        var page = ConfigPage();
        Assert.DoesNotContain("Content: 'Movies'", page, System.StringComparison.Ordinal);
        Assert.DoesNotContain("Layers card above", page, System.StringComparison.Ordinal);
        Assert.DoesNotContain("kaart Lagen", page, System.StringComparison.Ordinal);
        Assert.DoesNotContain("tarjeta Capas", page, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyButtonNamesTheTargetType()
    {
        var page = ConfigPage();
        var body = FunctionBody(page, "applyLabelFor");
        foreach (var key in new[] { "action.applyCollection", "action.applyPlaylist", "action.applyLivetv", "action.apply" })
        {
            Assert.Contains("t('" + key + "')", body, System.StringComparison.Ordinal);
        }
        Assert.Contains("applyLabelFor(", FunctionBody(page, "updateUI"), System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A 401/413/429 has an empty or HTML body; parsing it first produced "Unexpected end
    /// of JSON input", which names neither the status nor the cause. A 400 from the render
    /// endpoints carries the server's own validation message, which must reach the user.
    /// </summary>
    [Fact]
    public void JsonRequestsCheckTheStatusBeforeParsing()
    {
        var page = ConfigPage();
        var readJson = FunctionBody(page, "readJson");
        Assert.Contains("r.ok", readJson, System.StringComparison.Ordinal);
        Assert.Contains("responseError(r)", readJson, System.StringComparison.Ordinal);
        var responseError = FunctionBody(page, "responseError");
        Assert.Contains("requestError(r.status)", responseError, System.StringComparison.Ordinal);
        Assert.Contains("r.status === 400", responseError, System.StringComparison.Ordinal);
        Assert.Contains("then(readJson", FunctionBody(page, "jsonFetch"), System.StringComparison.Ordinal);
        Assert.Contains("then(readJson", FunctionBody(page, "uploadFile"), System.StringComparison.Ordinal);
        Assert.Contains("responseError(r)", FunctionBody(page, "showServerRender"), System.StringComparison.Ordinal);
        Assert.DoesNotContain("r.json()", page, System.StringComparison.Ordinal);
    }

    [Fact]
    public void NoUntranslatedErrorTextReachesTheUser()
    {
        var page = ConfigPage();
        foreach (var raw in new[]
        {
            // Quoted: the collage fetch's internal 'collage render returned ' is never shown.
            "'render returned ", "image fetch returned", "Upload rejected", "Poster background failed",
            "No image URL", "the downloaded image was empty",
        })
        {
            Assert.DoesNotContain(raw, page, System.StringComparison.Ordinal);
        }

        // A raw exception message is English (or a browser's "Failed to fetch"); the one
        // place allowed to read it is errorText, which every catch goes through.
        var reads = Regex.Matches(page, @"\be\.message\b").Count;
        Assert.True(reads == 1, $"Expected e.message only inside errorText; found {reads}.");
        Assert.Contains("e.message", FunctionBody(page, "errorText"), System.StringComparison.Ordinal);
    }

    /// <summary>Each of these used to drop a failure on the floor or report success regardless.</summary>
    [Fact]
    public void ListLoadsAndTemplateCallsHandleFailure()
    {
        var page = ConfigPage();
        Assert.Contains(".catch(", FunctionBody(page, "refreshRestoreState"), System.StringComparison.Ordinal);
        Assert.Contains(".catch(", FunctionBody(page, "loadTemplates"), System.StringComparison.Ordinal);
        Assert.Contains(".catch(", FunctionBody(page, "loadBatchList"), System.StringComparison.Ordinal);
        Assert.Matches(@"'CustomCoverArt/templates', 'POST'[\s\S]{0,300}?dataOrThrow\(res\)[\s\S]{0,300}?\.catch\(", page);
        Assert.Matches(@"'CustomCoverArt/templates/' \+ encodeURIComponent\(name\), 'DELETE'\)[\s\S]{0,300}?dataOrThrow\(res\)[\s\S]{0,300}?\.catch\(", page);
        Assert.Matches(@"'/restore', 'POST'\)[\s\S]{0,400}?\.catch\(", page);
    }

    /// <summary>
    /// Undo and template load replaced the document and nulled the background bitmap, so
    /// the canvas showed the gradient/flat fallback while the server still rendered the
    /// image (and an animated GIF background stopped moving).
    /// </summary>
    [Fact]
    public void UndoAndTemplateLoadKeepOrRefetchTheBackground()
    {
        var page = ConfigPage();
        var restore = FunctionBody(page, "restoreSnapshot");
        Assert.Contains("hydrateBackgroundImage(previousImagePath)", restore, System.StringComparison.Ordinal);
        Assert.DoesNotContain("bgImageEl = null", restore, System.StringComparison.Ordinal);
        Assert.True(Regex.Matches(page, @"hydrateBackgroundImage\(previousImagePath\)").Count >= 2,
            "The template load must re-hydrate the background too.");
        var hydrate = FunctionBody(page, "hydrateBackgroundImage");
        Assert.Contains("CustomCoverArt/layerImage?path=", hydrate, System.StringComparison.Ordinal);
        Assert.Contains("setBackgroundBitmap(blob, isCurrent)", hydrate, System.StringComparison.Ordinal);
    }

    [Fact]
    public void BackgroundObjectUrlsAreRevokedAndRacesDropped()
    {
        var page = ConfigPage();
        var setter = FunctionBody(page, "setBackgroundBitmap");
        Assert.Contains("if (!isCurrent()) { URL.revokeObjectURL(url); return; }", setter, System.StringComparison.Ordinal);
        Assert.Contains("clearBackgroundBitmap();", setter, System.StringComparison.Ordinal);
        Assert.Contains("URL.revokeObjectURL(_bgImageUrl)", FunctionBody(page, "clearBackgroundBitmap"), System.StringComparison.Ordinal);
        // The leaky inline pattern (an object URL assigned straight to an <img>, never revoked).
        Assert.DoesNotContain("img.src = URL.createObjectURL(file)", page, System.StringComparison.Ordinal);
        Assert.DoesNotContain("img.src = URL.createObjectURL(posterBlob)", page, System.StringComparison.Ordinal);
        Assert.Contains("requestGuard('bg')", FunctionBody(page, "selectBrowserItem"), System.StringComparison.Ordinal);
        // bgImageEl has exactly two writers besides its declaration.
        Assert.Equal(3, Regex.Matches(page, @"bgImageEl = ").Count);
    }

    [Fact]
    public void FileNameLabelsFollowTheDocument()
    {
        var page = ConfigPage();
        Assert.Contains("syncFileNames()", FunctionBody(page, "syncControlsFromDocument"), System.StringComparison.Ordinal);
        var sync = FunctionBody(page, "syncFileNames");
        Assert.Contains("ccaFontName", sync, System.StringComparison.Ordinal);
        Assert.Contains("ccaBgImageName", sync, System.StringComparison.Ordinal);
        Assert.Contains("ccaSelectedBg", sync, System.StringComparison.Ordinal);
    }

    [Fact]
    public void PosterBrowserIsARealDialog()
    {
        var markup = Markup();
        Assert.Matches(@"class=""ccaModalBox""[^>]*role=""dialog""", markup);
        Assert.Matches(@"class=""ccaModalBox""[^>]*aria-modal=""true""", markup);
        Assert.Matches(@"class=""ccaModalBox""[^>]*aria-labelledby=""ccaBrowserTitle""", markup);
        Assert.Matches(@"id=""ccaBrowserClose""[^>]*data-i18n-aria=""modal.close""", markup);

        var page = ConfigPage();
        Assert.Contains("createElement('button')", FunctionBody(page, "renderBrowserItems"), System.StringComparison.Ordinal);
        Assert.Contains("'Escape'", FunctionBody(page, "onBrowserKeydown"), System.StringComparison.Ordinal);
        Assert.Contains(".focus()", FunctionBody(page, "closeBrowser"), System.StringComparison.Ordinal);
        Assert.Contains(".focus()", FunctionBody(page, "openBrowser"), System.StringComparison.Ordinal);
    }

    /// <summary>A hard-coded dark box with inherited text colour is unreadable on the light theme.</summary>
    [Fact]
    public void ModalAndStickyBarsFollowTheTheme()
    {
        var css = Styles();
        Assert.DoesNotMatch(@"background:\s*#1b1b1b", css);
        Assert.Matches(@"\.ccaModalBox \{[^}]*background: var\(--cca-surface", css);
        Assert.Matches(@"\.ccaModalBox \{[^}]*color: var\(--cca-text", css);
        Assert.DoesNotContain("background: var(--theme-body-background-color, #101010);", css, System.StringComparison.Ordinal);
        Assert.Contains("--cca-surface", FunctionBody(ConfigPage(), "applyThemeSurface"), System.StringComparison.Ordinal);
    }

    [Fact]
    public void LayersPanelIsKeyboardUsableAndLabelled()
    {
        var page = ConfigPage();
        var panel = FunctionBody(page, "renderLayersPanel");
        Assert.Contains("name.setAttribute('data-act', 'pick')", panel, System.StringComparison.Ordinal);
        Assert.Contains("var name = document.createElement('button')", panel, System.StringComparison.Ordinal);
        Assert.Contains("vis.setAttribute('aria-pressed'", panel, System.StringComparison.Ordinal);
        Assert.Contains("b.setAttribute('aria-label', t('layer.' + act))", panel, System.StringComparison.Ordinal);
        Assert.Contains("more.setAttribute('aria-expanded'", panel, System.StringComparison.Ordinal);
        Assert.Contains("restoreLayerFocus(box, prevFocus)", panel, System.StringComparison.Ordinal);
        Assert.Matches(@"id=""ccaLayerList""[^>]*role=""list""", Markup());
    }

    [Fact]
    public void CanvasIsFocusableAndArrowKeysNudgeTheSelectedLayer()
    {
        Assert.Matches(@"id=""ccaCanvas""[^>]*tabindex=""0""", Markup());
        Assert.Matches(@"id=""ccaCanvas""[^>]*data-i18n-aria=""canvas.label""", Markup());
        var page = ConfigPage();
        var keys = FunctionBody(page, "onCanvasKeydown");
        Assert.Contains("ArrowLeft", keys, System.StringComparison.Ordinal);
        Assert.Contains("ev.shiftKey ? 10 : 1", keys, System.StringComparison.Ordinal);
        Assert.Contains("cv.addEventListener('keydown', onCanvasKeydown)", page, System.StringComparison.Ordinal);
    }

    [Fact]
    public void StopRowsAndSwatchesAreLabelled()
    {
        var page = ConfigPage();
        var row = FunctionBody(page, "addStopRow");
        Assert.Contains("t('stop.color')", row, System.StringComparison.Ordinal);
        Assert.Contains("t('stop.position')", row, System.StringComparison.Ordinal);
        Assert.Contains("t('stop.remove')", row, System.StringComparison.Ordinal);
        Assert.Contains("updateStopRemoveButtons(container)", row, System.StringComparison.Ordinal);
        Assert.Contains("rows.length <= 2", FunctionBody(page, "updateStopRemoveButtons"), System.StringComparison.Ordinal);
        Assert.Contains("t('fx.swatch', hex)", FunctionBody(page, "renderSwatches"), System.StringComparison.Ordinal);
    }
}
