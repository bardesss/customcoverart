# Guided Editor UX + Phase 4 Polish — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Spec:** `docs/superpowers/specs/2026-08-01-guided-editor-ux-design.md`. **Depends on Phases 1–3** (`CoverDocument`, canvas engine, layers, effects — all shipped in v3.2.0.0).

**Goal:** Turn the nine-card configuration page into five numbered accordion steps with an essentials/advanced split, make the whole page genuinely usable on a phone, and fold in Phase 4's undo/redo and preview modes.

**Architecture:** The existing `<section class="ccaCard">` blocks are **regrouped in place** into five `<section class="ccaStep">` wrappers — every element id and its bound handler is preserved, so the canvas engine, `syncControlsFromDocument` and all Phase 1–3 behaviour keep working untouched. A small step controller owns `.ccaStepBody` and nothing else. The one model change is `Background.Source` absorbing the separate gradient checkbox.

**Tech Stack:** .NET 9, SixLabors.ImageSharp, xUnit + NSubstitute, vanilla JS + CSS in the embedded `Configuration/configPage.html`.

## Global Constraints

- Inherits all Phase 1–3 constraints: the coordinate contract (canvas backing store = export px; layer X/Y normalized 0–1; text `Size` = fraction of canvas height), auth, path sandboxing, server-side clamps.
- **Blob-URL canvas-source invariant:** any bitmap drawn to the canvas must come from `URL.createObjectURL(File|Blob)`, never a server path or `ApiClient.getImageUrl`. A tainted canvas breaks `extractPalette`'s `getImageData`.
- **en/nl sync:** every new `data-i18n` key must exist in BOTH the `en` and `nl` blocks of `I18N`. `PresetTests.EveryI18nKeyUsedInMarkup_ExistsInBothLanguages` enforces this — it will fail the build if you forget.
- Version: bump `<Version>` to `3.3.0.0`. The 4th component is always `0` in this repo.
- **Merging a PR ships nothing unless `<Version>` is bumped** — `release.yml` gates on "is `v<Version>` already released?" and no-ops with a green check otherwise.
- Build: `& "$env:USERPROFILE\.dotnet\dotnet.exe" build -c Release` must report **0 warnings, 0 errors**.
- Test: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal`. Baseline entering this plan: **105 passing**.
- After editing `configPage.html`, syntax-check the script:
  ```bash
  node -e "const fs=require('fs');const h=fs.readFileSync('Configuration/configPage.html','utf8');fs.writeFileSync('cca_check.js', h.match(/<script type=\"text\/javascript\">([\s\S]*?)<\/script>/)[1]);" && node --check cca_check.js && rm cca_check.js
  ```
- Mobile acceptance: no horizontal page scroll at **360 × 640**; every interactive target ≥ **44 × 44 CSS px**.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `Models/CoverArtModels.cs` | `BackgroundSources` constants | Add `Gradient`, `Solid` |
| `Services/DocumentMigration.cs` | Document normalization + legacy migration | Add `NormalizeBackgroundSource` |
| `Services/DocumentRenderer.cs` | Compositor | `CreateGradientBackground` switches on `Source` |
| `Configuration/configPage.html` | The entire UI | Steps, advanced split, mobile, undo/redo, preview modes |
| `tests/.../BackgroundSourceTests.cs` | Migration + render coverage | Create |
| `tests/.../ConfigPageStructureTests.cs` | Element-id pin + step well-formedness | Create |
| `CHANGELOG.md`, `README.md`, `CustomCoverArt.csproj` | Release | Task 9 |

Tasks 1–2 are the model change, 3–4 the restructure, 5–6 mobile, 7–8 the Phase 4 features, 9 the release. **Tasks 1–4 must be done in order** (later tasks depend on the step DOM). Tasks 5–8 are independent of each other.

---

## Task 1: Background source consolidation (server)

**Files:**
- Modify: `Models/CoverArtModels.cs:58-62`
- Modify: `Services/DocumentMigration.cs` (inside `Normalize`)
- Modify: `Services/DocumentRenderer.cs:274-283` (`CreateGradientBackground`)
- Test: `tests/CustomCoverArt.Tests/BackgroundSourceTests.cs`

**Interfaces:**
- Produces: `BackgroundSources.Gradient = "gradient"`, `BackgroundSources.Solid = "solid"`; `DocumentMigration.NormalizeBackgroundSource(BackgroundLayer bg)` (public static, void, mutates in place, called from `Normalize`).
- Consumes: existing `BackgroundSources.Upload`/`Collage`, `BackgroundLayer.Gradient`, `BackgroundLayer.ImagePath`.

> **Design:** `upload` keeps its existing value and now simply means "an image", so documents that already say `upload` need no migration. Migration rules are evaluated in order, first match wins: `collage` stays; non-empty `ImagePath` → `upload`; `Gradient.IsEnabled` → `gradient`; everything else (including `"none"`, `"poster"`, empty) → `solid`.

- [ ] **Step 1: Write the failing test**

```csharp
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CustomCoverArt.Tests;

public class BackgroundSourceTests
{
    private static BackgroundLayer Bg(string source, string imagePath = "", bool? gradientEnabled = null)
    {
        var bg = new BackgroundLayer { Source = source, ImagePath = imagePath };
        bg.Gradient = gradientEnabled is null ? null : new GradientSettings { IsEnabled = gradientEnabled.Value };
        return bg;
    }

    [Theory]
    [InlineData("collage", "", null, "collage")]                 // collage is untouched
    [InlineData("collage", "/x/y.png", true, "collage")]          // ...even with an image set
    [InlineData("upload", "/x/y.png", null, "upload")]            // an image wins
    [InlineData("poster", "/x/y.png", true, "upload")]            // legacy "poster" with an image
    [InlineData("upload", "", true, "gradient")]                  // no image, gradient on
    [InlineData("none", "", true, "gradient")]
    [InlineData("upload", "", false, "solid")]                    // no image, gradient off
    [InlineData("none", "", null, "solid")]
    [InlineData("", "", null, "solid")]
    [InlineData("poster", "", null, "solid")]
    public void NormalizeBackgroundSource_FollowsTheMigrationTable(
        string source, string imagePath, bool? gradientEnabled, string expected)
    {
        var bg = Bg(source, imagePath, gradientEnabled);
        DocumentMigration.NormalizeBackgroundSource(bg);
        Assert.Equal(expected, bg.Source);
    }

    [Fact]
    public void Normalize_AppliesTheBackgroundSourceMigration()
    {
        var doc = new CoverDocument();
        doc.Background.Source = "none";
        doc.Background.Gradient = new GradientSettings { IsEnabled = true };

        DocumentMigration.Normalize(doc);

        Assert.Equal(BackgroundSources.Gradient, doc.Background.Source);
    }

    /// <summary>
    /// Source "solid" must fill with the base colour even when a stale Gradient.IsEnabled
    /// is left over from before the migration, or switching to Solid would do nothing.
    /// </summary>
    [Fact]
    public void ComposeDocumentFrame_SolidSource_FillsBaseColour_IgnoringStaleGradientFlag()
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 40, Height = 40 } };
        doc.Background.Source = BackgroundSources.Solid;
        doc.Background.DimColor = "#ff0000";
        doc.Background.Gradient = new GradientSettings
        {
            IsEnabled = true,
            Stops = new() { new GradientStop { Color = "#0000ff", Position = 0 },
                            new GradientStop { Color = "#0000ff", Position = 1 } }
        };

        using var canvas = new Image<Rgba32>(40, 40);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        Assert.True(canvas[20, 20].R > 200 && canvas[20, 20].B < 60, "Solid must win over a stale gradient flag.");
    }

    /// <summary>Back-compat: a document that never migrated still renders its gradient.</summary>
    [Fact]
    public void ComposeDocumentFrame_LegacyUploadSourceWithGradient_StillDrawsGradient()
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 40, Height = 40 } };
        doc.Background.Source = BackgroundSources.Upload;   // legacy shape, no ImagePath
        doc.Background.DimColor = "#000000";
        doc.Background.Gradient = new GradientSettings
        {
            IsEnabled = true,
            Stops = new() { new GradientStop { Color = "#00ff00", Position = 0 },
                            new GradientStop { Color = "#00ff00", Position = 1 } }
        };

        using var canvas = new Image<Rgba32>(40, 40);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        Assert.True(canvas[20, 20].G > 200, "A pre-migration document must keep rendering its gradient.");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal --filter "FullyQualifiedName~BackgroundSourceTests"`
Expected: FAIL — `BackgroundSources.Gradient` and `NormalizeBackgroundSource` do not exist.

- [ ] **Step 3: Add the constants**

In `Models/CoverArtModels.cs`, replace the `BackgroundSources` class:

```csharp
/// <summary>
/// What supplies the background. One value answers the question — before v3.3.0.0 this
/// was split across Source and a separate Gradient.IsEnabled checkbox, so "what is my
/// background?" had two overlapping answers.
/// </summary>
public static class BackgroundSources
{
    /// <summary>An image: uploaded from disk, or a library poster copied into the uploads dir.</summary>
    public const string Upload = "upload";
    public const string Collage = "collage";
    public const string Gradient = "gradient";
    public const string Solid = "solid";
}
```

- [ ] **Step 4: Add the migration**

In `Services/DocumentMigration.cs`, add the method and call it from `Normalize` (right after the `doc.Background.Transform ??= ...` line):

```csharp
    /// <summary>
    /// Collapses the legacy "Source + separate Gradient.IsEnabled" pair into a single
    /// Source value. Rules are evaluated in order, first match wins — see the design doc
    /// migration table. Safe to run repeatedly: an already-migrated document is unchanged.
    /// </summary>
    public static void NormalizeBackgroundSource(BackgroundLayer bg)
    {
        var source = (bg.Source ?? string.Empty).Trim().ToLowerInvariant();

        // A collage supplies the whole background and never had the ambiguity.
        if (source == BackgroundSources.Collage) { bg.Source = BackgroundSources.Collage; return; }

        // An image beats everything else: it is what actually renders.
        if (!string.IsNullOrEmpty(bg.ImagePath)) { bg.Source = BackgroundSources.Upload; return; }

        // No image: the gradient flag decides, exactly as the renderer used to.
        bg.Source = bg.Gradient?.IsEnabled == true ? BackgroundSources.Gradient : BackgroundSources.Solid;
    }
```

Call site inside `Normalize`:

```csharp
        doc.Background.Transform ??= new BackgroundTransform();
        NormalizeBackgroundSource(doc.Background);
```

- [ ] **Step 5: Switch the renderer on Source**

Replace `CreateGradientBackground` in `Services/DocumentRenderer.cs`:

```csharp
    internal static void CreateGradientBackground(Image<Rgba32> image, BackgroundLayer bg)
    {
        // Source is authoritative post-migration. The IsEnabled fallback keeps a document
        // that never went through Normalize (an older client POSTing directly) rendering
        // exactly as it did before — but an explicit "solid" always wins, so switching to
        // Solid in the UI cannot be silently overridden by a stale flag.
        var useGradient = bg.Source == BackgroundSources.Gradient
            || (bg.Source != BackgroundSources.Solid && bg.Gradient?.IsEnabled == true);

        if (useGradient && bg.Gradient is not null)
        {
            ApplyGradientBackground(image, bg.Gradient);
        }
        else
        {
            var backgroundColor = SafeColor(bg.DimColor, Color.Black);
            image.Mutate(x => x.Fill(backgroundColor));
        }
    }
```

- [ ] **Step 6: Run tests**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal`
Expected: PASS, 105 baseline + 14 new = **119**.

- [ ] **Step 7: Commit**

```bash
git add Models/CoverArtModels.cs Services/DocumentMigration.cs Services/DocumentRenderer.cs tests/CustomCoverArt.Tests/BackgroundSourceTests.cs
git commit -m "feat(ux): Background.Source absorbs the separate gradient flag"
```

---

## Task 2: Background source in the client

**Files:**
- Modify: `Configuration/configPage.html`

**Interfaces:**
- Produces: `normalizeBackgroundSource(d)` (client mirror of the server migration, same rules), `updateBackgroundVisibility()` (shows only the chosen source's controls).
- Consumes: `doc.Background`, `scheduleRender`, existing `updateCollageVisibility`.

> **Design:** `updateBackgroundVisibility` replaces `updateCollageVisibility` as the single owner of which background controls are visible. The client also keeps `Gradient.IsEnabled` in sync with `Source` on every change, so a rolled-back plugin still renders correctly.

- [ ] **Step 1: Extend the source dropdown**

Replace the `ccaBgSource` `<select>` options:

```html
<select is="emby-select" id="ccaBgSource" class="emby-select">
    <option value="upload" selected data-i18n="bg.source.image">Image</option>
    <option value="collage" data-i18n="bg.source.collage">Poster collage from this target</option>
    <option value="gradient" data-i18n="bg.source.gradient">Gradient</option>
    <option value="solid" data-i18n="bg.source.solid">Solid colour</option>
</select>
```

Add i18n keys to BOTH `en` and `nl`:

```javascript
// en
'bg.source.image': 'Image', 'bg.source.gradient': 'Gradient', 'bg.source.solid': 'Solid colour',
// nl
'bg.source.image': 'Afbeelding', 'bg.source.gradient': 'Verloop', 'bg.source.solid': 'Effen kleur',
```

The existing `ccaGradient` checkbox and its label are **removed** from the markup — the source dropdown replaces it. Its id disappears from the page, so remove `ccaGradient` from the pin list in Task 3 as well.

- [ ] **Step 2: Add the client migration**

```javascript
        // Client mirror of DocumentMigration.NormalizeBackgroundSource. Same rules, same
        // order. Runs on load only (init and template load) — never on every change, or it
        // would flip the user's chosen source out from under them mid-edit.
        function normalizeBackgroundSource(d) {
            var bg = d.Background;
            var source = String(bg.Source || '').trim().toLowerCase();
            if (source === 'collage') { bg.Source = 'collage'; return d; }
            if (bg.ImagePath) { bg.Source = 'upload'; return d; }
            bg.Source = (bg.Gradient && bg.Gradient.IsEnabled) ? 'gradient' : 'solid';
            return d;
        }
```

Call it immediately after each existing `normalizeEffects(...)` call site.

- [ ] **Step 3: Replace updateCollageVisibility**

```javascript
        // Single owner of which background controls are visible: only the chosen source's
        // own controls are shown. Anything belonging to an unchosen source is hidden
        // outright rather than demoted to "advanced".
        function updateBackgroundVisibility() {
            var source = el('ccaBgSource').value;
            var type = el('ccaTargetType') ? el('ccaTargetType').value : 'library';

            // Live TV has no child posters to build a mosaic from.
            var collageOpt = el('ccaBgSource').querySelector('option[value="collage"]');
            if (collageOpt) { collageOpt.disabled = (type === 'livetv'); }
            if (type === 'livetv' && source === 'collage') {
                el('ccaBgSource').value = 'upload';
                doc.Background.Source = 'upload';
                source = 'upload';
            }

            el('ccaUploadControls').style.display = source === 'upload' ? '' : 'none';
            el('ccaCollageRow').style.display = source === 'collage' ? '' : 'none';
            el('ccaGradientOpts').style.display = source === 'gradient' ? '' : 'none';
            el('ccaGradientAngleRow').style.display =
                (source === 'gradient' && el('ccaGradientType').value === '0') ? '' : 'none';
            updateUI();
        }
```

Delete the old `updateCollageVisibility` and replace **every** call to it with `updateBackgroundVisibility()`. In `updateUI()`, delete the two lines that set `ccaGradientOpts` and `ccaGradientAngleRow` display — `updateBackgroundVisibility` now owns them, and leaving both would make them fight.

- [ ] **Step 4: Bind the source change**

Replace the `ccaBgSource` change handler:

```javascript
            el('ccaBgSource').addEventListener('change', function () {
                doc.Background.Source = this.value;
                // Two-way compatibility: an older server reading this document decides on
                // IsEnabled, so keep it in step with Source.
                if (doc.Background.Gradient) {
                    doc.Background.Gradient.IsEnabled = (this.value === 'gradient');
                }
                // A gradient or solid background must not keep painting a stale bitmap.
                if (this.value === 'gradient' || this.value === 'solid') {
                    bgImageEl = null;
                    doc.Background.ImagePath = '';
                    state.backgroundImagePath = '';
                }
                updateBackgroundVisibility();
                scheduleRender();
            });
```

In `syncControlsFromDocument`, replace `el('ccaBgSource').value = bg.Source || 'upload';` with the same line plus removing the now-deleted `el('ccaGradient').checked = ...` lines, and call `updateBackgroundVisibility()` instead of `updateCollageVisibility()`.

`applyJellyfinPreset` must also set `doc.Background.Source = 'gradient'` (it already clears the image and enables the gradient).

- [ ] **Step 5: Verify and commit**

Run the `node --check` snippet from Global Constraints, then:
`& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal` (the i18n test covers the three new keys).
Expected: 119 passing.

```bash
git add Configuration/configPage.html
git commit -m "feat(ux): background source drives which controls are shown"
```

---

## Task 3: Five accordion steps

**Files:**
- Modify: `Configuration/configPage.html`
- Test: `tests/CustomCoverArt.Tests/ConfigPageStructureTests.cs`

**Interfaces:**
- Produces: DOM contract `<section class="ccaStep" data-step="N">` → `<button class="ccaStepHead" aria-expanded aria-controls="ccaStepBodyN">` + `<div class="ccaStepBody" id="ccaStepBodyN" hidden>`; JS `initSteps()`, `openStep(n)`, `restoreLastStep()`.
- Consumes: the existing `.ccaCard` sections, unchanged internally.

> **Design:** Regroup, don't rewrite. Each existing card's *inner* markup moves verbatim into a step body. `.ccaStepBody` is touched ONLY by the step controller — `updateBackgroundVisibility`, `updateEffectsVisibility`, `updateAnimVisibility` and the `.ccaTextOnly`/`.ccaImageOnly` toggles all write `style.display` on elements *inside* it, and two owners of the same element would fight.

**Step → existing card mapping:**

| Step | `data-step` | Existing cards moved in (in order) |
|---|---|---|
| Target & start | 1 | Target card; the **load** half of Templates (`ccaTemplateSelect`, `ccaTemplateDelete`) |
| Background | 2 | Background card |
| Text & logos | 3 | Layers card; Selected-layer card (`ccaSettings`) |
| Effects | 4 | Effects card |
| Output & apply | 5 | Output card; the **save** half of Templates (`ccaTemplateName`, `ccaTemplateSave`); Batch apply card |

The Preview card is NOT a step — it stays in the right-hand `.ccaPreviewCol`.

- [ ] **Step 1: Write the failing structure test**

```csharp
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace CustomCoverArt.Tests;

/// <summary>
/// Structural guards over the embedded config page. The element-id pin is the important
/// one: regrouping ~3,000 lines of markup in place is exactly the operation that silently
/// drops a control, and a dropped control means a dead handler with no error anywhere.
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

    // Every cca* element id present at the start of the guided-editor restructure, minus
    // ccaPreviewSpinner (dead since Phase 1, removed here) and ccaGradient (replaced by the
    // background source dropdown in Task 2). Add to this list when you add a control;
    // never remove from it without deleting the control deliberately.
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
        "ccaTextSize", "ccaTextSizeVal", "ccaTextWeight", "ccaTitle", "ccaUploadControls", "ccaWidth"
    };

    [Fact]
    public void NoControlWasLostInTheRestructure()
    {
        var page = ConfigPage();
        var missing = RequiredIds.Where(id => !page.Contains($"id=\"{id}\"", System.StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0, "Element ids missing from configPage.html: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryIdAppearsExactlyOnce()
    {
        var page = ConfigPage();
        var duplicated = RequiredIds
            .Where(id => Regex.Matches(page, $"id=\"{id}\"").Count > 1)
            .ToList();
        Assert.True(duplicated.Count == 0, "Duplicate element ids: " + string.Join(", ", duplicated));
    }

    [Fact]
    public void EveryStepIsWellFormed()
    {
        var page = ConfigPage();
        var steps = Regex.Matches(page, @"<section class=""ccaStep"" data-step=""(\d)"">(.*?)</section>",
            RegexOptions.Singleline);

        Assert.Equal(5, steps.Count);

        foreach (Match step in steps)
        {
            var n = step.Groups[1].Value;
            var body = step.Groups[2].Value;
            Assert.True(Regex.IsMatch(body, @"class=""ccaStepHead""" ), $"Step {n} has no header.");
            Assert.Contains($"aria-controls=\"ccaStepBody{n}\"", body, System.StringComparison.Ordinal);
            Assert.Contains($"id=\"ccaStepBody{n}\"", body, System.StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DeadPreviewSpinnerIsGone()
    {
        Assert.DoesNotContain("ccaPreviewSpinner", ConfigPage(), System.StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal --filter "FullyQualifiedName~ConfigPageStructureTests"`
Expected: FAIL — `EveryStepIsWellFormed` finds 0 steps; `DeadPreviewSpinnerIsGone` fails.

- [ ] **Step 3: Regroup the markup**

For each row of the step→card mapping above, wrap the moved content:

```html
<section class="ccaStep" data-step="2">
    <button type="button" class="ccaStepHead" aria-expanded="false" aria-controls="ccaStepBody2">
        <span class="ccaStepNum">2</span>
        <span class="ccaStepTitle" data-i18n="step.background">Background</span>
        <span class="ccaStepChevron" aria-hidden="true">▾</span>
    </button>
    <div class="ccaStepBody" id="ccaStepBody2" hidden>
        <!-- the existing Background card's inner markup, verbatim, minus its .ccaCardHead -->
    </div>
</section>
```

Rules while moving:
- Move the **inner** markup of each card; drop the old `.ccaCardHead` divs (the step header replaces them).
- Do not rename, retype or restructure any control. Ids, classes and attributes stay byte-identical.
- Delete `<div class="ccaPreviewSpinner" id="ccaPreviewSpinner">…</div>` and its CSS rule (dead since Phase 1).
- Steps 1, 3 and 5 hold two or three former cards each: separate them inside the body with `<div class="ccaStepGroup">` and a small `<div class="ccaStepGroupHead">` label so they don't run together.

Add i18n keys for the five step titles to BOTH languages:

```javascript
// en
'step.target': 'Target & start', 'step.background': 'Background', 'step.text': 'Text & logos',
'step.effects': 'Effects', 'step.output': 'Output & apply',
// nl
'step.target': 'Doel & start', 'step.background': 'Achtergrond', 'step.text': 'Tekst & logo\'s',
'step.effects': 'Effecten', 'step.output': 'Uitvoer & toepassen',
```

- [ ] **Step 4: Add the step CSS**

```css
        #CustomCoverArtConfigPage .ccaStep { margin: 0 0 .6em; border: 1px solid rgba(127,127,127,.2); border-radius: 10px; overflow: hidden; }
        #CustomCoverArtConfigPage .ccaStepHead {
            display: flex; align-items: center; gap: .75em; width: 100%;
            min-height: 3em; padding: .8em 1em; box-sizing: border-box;
            background: transparent; border: 0; color: inherit; cursor: pointer;
            font-size: 1.05em; text-align: left;
        }
        #CustomCoverArtConfigPage .ccaStepHead:hover { background: rgba(127,127,127,.08); }
        #CustomCoverArtConfigPage .ccaStepNum {
            flex: 0 0 auto; display: inline-flex; align-items: center; justify-content: center;
            width: 1.8em; height: 1.8em; border-radius: 50%;
            background: var(--theme-primary-color, #00a4dc); color: #fff; font-size: .85em;
        }
        #CustomCoverArtConfigPage .ccaStepTitle { flex: 1 1 auto; }
        #CustomCoverArtConfigPage .ccaStepChevron { flex: 0 0 auto; opacity: .6; transition: transform .18s ease; }
        #CustomCoverArtConfigPage .ccaStepHead[aria-expanded="true"] .ccaStepChevron { transform: rotate(180deg); }
        #CustomCoverArtConfigPage .ccaStepBody { padding: 0 1em 1.1em; }
        #CustomCoverArtConfigPage .ccaStepBody[hidden] { display: none; }
        #CustomCoverArtConfigPage .ccaStepGroup { margin-top: 1.1em; }
        #CustomCoverArtConfigPage .ccaStepGroupHead { font-size: .9em; opacity: .6; margin-bottom: .5em; }
        @media (prefers-reduced-motion: reduce) {
            #CustomCoverArtConfigPage .ccaStepChevron { transition: none; }
        }
```

- [ ] **Step 5: Implement the step controller**

```javascript
        // --- Step accordion -----------------------------------------------
        // Owns .ccaStepBody and NOTHING else. Every other visibility helper
        // (updateBackgroundVisibility, updateEffectsVisibility, updateAnimVisibility, the
        // .ccaTextOnly/.ccaImageOnly toggles) writes style.display on elements INSIDE a
        // body; if this controller touched those too, the two owners would fight and a
        // control could reappear inside a collapsed step.
        var STEP_KEY = 'cca.lastStep';

        function openStep(n) {
            page.querySelectorAll('.ccaStep').forEach(function (section) {
                var isTarget = section.getAttribute('data-step') === String(n);
                var head = section.querySelector('.ccaStepHead');
                var body = section.querySelector('.ccaStepBody');
                head.setAttribute('aria-expanded', isTarget ? 'true' : 'false');
                if (isTarget) { body.removeAttribute('hidden'); } else { body.setAttribute('hidden', ''); }
            });
            try { localStorage.setItem(STEP_KEY, String(n)); } catch (e) { /* private mode */ }
        }

        function currentOpenStep() {
            var open = page.querySelector('.ccaStepHead[aria-expanded="true"]');
            return open ? open.parentNode.getAttribute('data-step') : null;
        }

        function initSteps() {
            page.querySelectorAll('.ccaStep').forEach(function (section) {
                var n = section.getAttribute('data-step');
                section.querySelector('.ccaStepHead').addEventListener('click', function () {
                    // Clicking the open step collapses it: all-closed is a valid state, and
                    // it is how you get the canvas to itself on a phone.
                    if (currentOpenStep() === n) {
                        section.querySelector('.ccaStepBody').setAttribute('hidden', '');
                        this.setAttribute('aria-expanded', 'false');
                        return;
                    }
                    openStep(n);
                });
            });
            restoreLastStep();
        }

        // Step 1 when there is nothing selected yet, otherwise wherever you left off.
        function restoreLastStep() {
            var saved = null;
            try { saved = localStorage.getItem(STEP_KEY); } catch (e) { /* ignore */ }
            var exists = saved && page.querySelector('.ccaStep[data-step="' + saved + '"]');
            openStep((state.libraryId && exists) ? saved : '1');
        }
```

Call `initSteps()` from the `pageshow` handler, after `bindEvents()` and before `syncControlsFromDocument()`.

- [ ] **Step 6: Run tests + syntax check**

Run the `node --check` snippet, then the full suite.
Expected: **123 passing** (119 + 4 structure tests).

- [ ] **Step 7: Commit**

```bash
git add Configuration/configPage.html tests/CustomCoverArt.Tests/ConfigPageStructureTests.cs
git commit -m "feat(ux): five numbered accordion steps"
```

---

## Task 4: Essentials / Advanced split

**Files:**
- Modify: `Configuration/configPage.html`

**Interfaces:**
- Produces: `<div class="ccaAdv">` → `<button class="ccaAdvHead" aria-expanded="false">` + `<div class="ccaAdvBody" hidden>`; JS `initAdvanced()`.
- Consumes: the step bodies from Task 3.

> **Design:** One `.ccaAdv` block per step, always the last thing in the step body. It is independent of the step controller: opening a step does not open its advanced block, and advanced state is not persisted.

**What moves into each step's Advanced block:**

| Step | Advanced contents |
|---|---|
| 1 Target & start | — (no advanced block) |
| 2 Background | `ccaBlur` + label, `ccaDimColor` + label. (The spec also lists "gradient centre/radius" here — those exist on the model but have never had controls, and this plan does not add them. YAGNI: nobody has asked for them.) |
| 3 Text & logos | `ccaTextWeight`, the shadow/outline checks (`ccaShadow`, `ccaOutline`), the custom-font row (`ccaFontBtn`/`ccaFont`/`ccaFontName`), `ccaLayerOpacity`, `ccaLayerRotation` |
| 4 Effects | `ccaFxVignetteSoftness`, `ccaFxBorderRadius`, `ccaFxBorderDouble`, `ccaFxBorderGap`, `ccaFxSoftLightColor`, `ccaFxBorderColor` |
| 5 Output & apply | `ccaCustomDims`, the animation row (`ccaAnimRow`, `ccaAnimHint`), the template-save group, the batch-apply group |

Everything else stays in the visible part of its step.

- [ ] **Step 1: Add the markup wrapper**

At the end of each step body that has advanced content:

```html
<div class="ccaAdv">
    <button type="button" class="ccaAdvHead" aria-expanded="false">
        <span class="ccaStepChevron" aria-hidden="true">▾</span>
        <span data-i18n="ui.advanced">Advanced</span>
    </button>
    <div class="ccaAdvBody" hidden>
        <!-- the controls listed for this step, moved verbatim -->
    </div>
</div>
```

i18n, both languages: `'ui.advanced': 'Advanced'` / `'ui.advanced': 'Geavanceerd'`.

- [ ] **Step 2: Add the CSS**

```css
        #CustomCoverArtConfigPage .ccaAdv { margin-top: 1.2em; border-top: 1px solid rgba(127,127,127,.18); }
        #CustomCoverArtConfigPage .ccaAdvHead {
            display: flex; align-items: center; gap: .5em; width: 100%;
            min-height: 2.75em; padding: .6em 0; box-sizing: border-box;
            background: transparent; border: 0; color: inherit; cursor: pointer;
            font-size: .95em; opacity: .75; text-align: left;
        }
        #CustomCoverArtConfigPage .ccaAdvHead:hover { opacity: 1; }
        #CustomCoverArtConfigPage .ccaAdvHead[aria-expanded="true"] .ccaStepChevron { transform: rotate(180deg); }
        #CustomCoverArtConfigPage .ccaAdvBody[hidden] { display: none; }
```

- [ ] **Step 3: Implement the toggle**

```javascript
        // Advanced disclosures are independent of the step accordion and of each other:
        // opening a step does not reveal its advanced controls, and the state is not
        // persisted — "advanced" should feel closed by default every visit.
        function initAdvanced() {
            page.querySelectorAll('.ccaAdvHead').forEach(function (head) {
                head.addEventListener('click', function () {
                    var body = this.parentNode.querySelector('.ccaAdvBody');
                    var open = this.getAttribute('aria-expanded') === 'true';
                    this.setAttribute('aria-expanded', open ? 'false' : 'true');
                    if (open) { body.setAttribute('hidden', ''); } else { body.removeAttribute('hidden'); }
                });
            });
        }
```

Call `initAdvanced()` immediately after `initSteps()`.

- [ ] **Step 4: Verify nothing broke**

Run the `node --check` snippet and the full suite. The id-pin test proves no control was lost while moving them into the advanced bodies.
Expected: **123 passing**.

- [ ] **Step 5: Commit**

```bash
git add Configuration/configPage.html
git commit -m "feat(ux): essentials up front, advanced collapsed per step"
```

---

## Task 5: Canvas handles and hit-testing in screen space

**Files:**
- Modify: `Configuration/configPage.html`

**Interfaces:**
- Produces: `canvasScale()` (canvas px per CSS px), `handleSize(H)` reimplemented, `hitTolerance(ev)`.
- Consumes: `layerHandles`, `hitTestHandle`, `hitTestLayer`, `drawSelectionHandles` from Phase 2.

> **Design:** This is the mobile bug that CSS cannot fix. `handleSize(H)` currently returns `max(7, H/55)` in BACKING-STORE pixels. A 1400px-tall canvas displayed at 340px on a phone scales by ~0.24, so a 25px handle renders ~6 CSS px across — far below the 44px touch target. The fix is to pick the size in SCREEN pixels and convert back into canvas units.

- [ ] **Step 1: Add the scale helper**

```javascript
        // Canvas backing-store pixels per CSS pixel. >1 whenever the canvas is displayed
        // smaller than its export size, which is almost always — and dramatically so on a
        // phone (a 1400px canvas at ~340px wide scales by ~4).
        function canvasScale() {
            var cv = el('ccaCanvas');
            if (!cv) { return 1; }
            var rect = cv.getBoundingClientRect();
            if (!rect.width || !cv.width) { return 1; }
            return cv.width / rect.width;
        }
```

- [ ] **Step 2: Reimplement handleSize and add hitTolerance**

Replace the Phase 2 `handleSize`:

```javascript
        // Handle size chosen in SCREEN pixels, then converted into canvas units so it
        // renders at a constant on-screen size no matter how far the canvas is scaled
        // down. The old backing-store-relative version shrank with the display and left
        // handles a few pixels across on a phone.
        var HANDLE_SCREEN_PX = 11;      // drawn size
        var TOUCH_TARGET_PX = 44;       // minimum comfortable touch target
        function handleSize(H) {
            var scale = canvasScale();
            var byScreen = HANDLE_SCREEN_PX * scale;
            // Never let it grow so large on a tiny canvas that the handles swamp the layer.
            return Math.max(6, Math.min(byScreen, H / 8));
        }

        // Grab radius. Touch needs the full 44px target; a mouse can be precise, so it
        // uses the drawn handle size and does not get a sloppy oversized hit area.
        function hitTolerance(ev) {
            var scale = canvasScale();
            var isTouch = ev && (ev.pointerType === 'touch' || ev.pointerType === 'pen');
            return isTouch ? (TOUCH_TARGET_PX / 2) * scale : HANDLE_SCREEN_PX * scale;
        }
```

- [ ] **Step 3: Thread the event through hit-testing**

`hitTestHandle(pt)` becomes `hitTestHandle(pt, ev)` and uses the tolerance:

```javascript
        function hitTestHandle(pt, ev) {
            var layer = selectedLayer();
            var cv = el('ccaCanvas');
            if (!layer || !layer.Visible || !cv) { return null; }
            var ctx = cv.getContext('2d');
            var r = layerRect(ctx, layer, cv.width, cv.height);
            var tol = hitTolerance(ev);
            var found = null;
            layerHandles(r, cv.height).forEach(function (p) {
                var dx = pt.x - p.x, dy = pt.y - p.y;
                if (!found && Math.sqrt(dx * dx + dy * dy) <= tol) { found = p; }
            });
            return found ? { handle: found, rect: r, layer: layer } : null;
        }
```

Update its one call site in `onCanvasPointerDown` to `hitTestHandle(pt, ev)`.

In `hitTestLayer(pt)` the text padding is also backing-store relative; add the event and widen it for touch:

```javascript
        function hitTestLayer(pt, ev) {
            var cv = el('ccaCanvas'); if (!cv) { return null; }
            var ctx = cv.getContext('2d');
            var touchPad = hitTolerance(ev) * 0.5;
            for (var i = doc.Layers.length - 1; i >= 0; i--) {
                var layer = doc.Layers[i];
                if (!layer.Visible) { continue; }
                var r = layerRect(ctx, layer, cv.width, cv.height);
                var local = canvasToRect(r, pt);
                var pad = layer.Type === 'text' ? Math.max(touchPad, r.h * 0.3) : touchPad;
                if (local.x >= r.lx - pad && local.x <= r.lx + r.w + pad &&
                    local.y >= r.ly - pad && local.y <= r.ly + r.h + pad) {
                    return layer;
                }
            }
            return null;
        }
```

Update its call site to `hitTestLayer(pt, ev)`.

- [ ] **Step 4: Re-render on resize**

Handle sizes now depend on the CSS display size, so a viewport change must redraw:

```javascript
            window.addEventListener('resize', debounce(function () { scheduleRender(); }, 150));
```

Add inside `bindEvents()`.

- [ ] **Step 5: Verify**

Run the `node --check` snippet and the full suite (**123 passing** — this task is client-only).

Manual: open the page, narrow the browser to 360px wide, select a logo layer, confirm the corner handles stay comfortably grabbable and resize still tracks the cursor.

- [ ] **Step 6: Commit**

```bash
git add Configuration/configPage.html
git commit -m "fix(ux): size canvas handles and hit tests in screen space, not backing-store px"
```

---

## Task 6: Mobile layout, layers panel, poster browser

**Files:**
- Modify: `Configuration/configPage.html`

**Interfaces:**
- Consumes: the step DOM from Task 3, `renderLayersPanel` from Phase 2.
- Produces: a `.ccaCompact` body class applied under 700px; no new JS API.

> **Design:** Three things break on a phone beyond general narrowness — the layers row packs five ~30px buttons plus a name, the poster browser is a desktop modal grid, and Apply sits at the very bottom of a long page. Everything else is CSS.

- [ ] **Step 1: Layout and touch targets**

```css
        @media (max-width: 700px) {
            /* Canvas first: it is what you are editing. Sticky so it stays visible while
               you work down the steps. */
            #CustomCoverArtConfigPage .ccaLayout { display: flex; flex-direction: column; }
            #CustomCoverArtConfigPage .ccaPreviewCol {
                order: -1; position: sticky; top: 0; z-index: 5;
                background: var(--theme-body-background-color, #101010);
                padding-bottom: .5em;
            }
            #CustomCoverArtConfigPage .ccaCanvas { max-height: 34vh; }
            #CustomCoverArtConfigPage .ccaPreviewWrap { min-height: 0; padding: .5em; }

            /* Touch targets. */
            #CustomCoverArtConfigPage .ccaStepHead { min-height: 3.4em; }
            #CustomCoverArtConfigPage .emby-select,
            #CustomCoverArtConfigPage input[is="emby-input"] { min-height: 44px; }
            #CustomCoverArtConfigPage input[type="color"] { min-height: 44px; min-width: 44px; }
            #CustomCoverArtConfigPage input[type="range"] { height: 44px; }
            #CustomCoverArtConfigPage .ccaSwatch { width: 44px; height: 44px; }
            #CustomCoverArtConfigPage .emby-button { min-height: 44px; }

            /* Apply stays reachable without scrolling to the end of the page. */
            #CustomCoverArtConfigPage .ccaStickyApply {
                position: sticky; bottom: 0; z-index: 6;
                padding: .6em 0;
                background: var(--theme-body-background-color, #101010);
                border-top: 1px solid rgba(127,127,127,.2);
            }
        }
```

Wrap the Apply/Download/server-render button row in `<div class="ccaStickyApply">`.

- [ ] **Step 2: Compact layers rows**

```css
        @media (max-width: 700px) {
            /* Five ~30px buttons plus a name does not fit. The row becomes tap-to-select
               (the primary action gets the whole row) and the actions move behind one
               toggle, revealed per row. */
            #CustomCoverArtConfigPage .ccaLayerRow { flex-wrap: wrap; min-height: 44px; }
            #CustomCoverArtConfigPage .ccaLayerBtn { width: 44px; height: 44px; }
            #CustomCoverArtConfigPage .ccaLayerRow .ccaLayerActions { display: none; width: 100%; gap: .3em; }
            #CustomCoverArtConfigPage .ccaLayerRow.ccaLayerActionsOpen .ccaLayerActions { display: flex; }
        }
        #CustomCoverArtConfigPage .ccaLayerActions { display: flex; gap: .2em; }
```

In `renderLayersPanel`, wrap the four action buttons in a container and add a toggle:

```javascript
                    var actions = document.createElement('div');
                    actions.className = 'ccaLayerActions';
                    ['up', 'down', 'dup', 'del'].forEach(function (act) {
                        /* ...unchanged button construction, appended to `actions`... */
                        actions.appendChild(b);
                    });

                    // Narrow screens hide the actions behind this toggle so the row's own
                    // tap target (select) is not competing with five small buttons.
                    var more = document.createElement('button');
                    more.type = 'button';
                    more.className = 'ccaLayerBtn ccaLayerMore';
                    more.textContent = '⋯';
                    more.title = t('layer.more');
                    more.addEventListener('click', function (e) {
                        e.stopPropagation();
                        row.classList.toggle('ccaLayerActionsOpen');
                    });

                    row.appendChild(actions);
                    row.appendChild(more);
```

Add CSS so `.ccaLayerMore` is hidden above 700px (`display: none`) and shown below it.
i18n both languages: `'layer.more': 'More actions'` / `'layer.more': 'Meer acties'`.

- [ ] **Step 3: Full-screen poster browser**

The modal's classes are `.ccaModal` (the backdrop, id `ccaBrowserModal`), `.ccaModalBox`,
`.ccaModalHead`, `.ccaModalClose`, `.ccaModalFilters`, and `.ccaPosterGrid` (id
`ccaBrowserGrid`).

```css
        @media (max-width: 700px) {
            /* Fill the screen: a centred dialog on a 360px viewport leaves the grid a few
               tiles wide with the page scrolling behind it. */
            #CustomCoverArtConfigPage .ccaModalBox {
                width: 100%; max-width: none; height: 100%; max-height: none;
                border-radius: 0; display: flex; flex-direction: column;
            }
            #CustomCoverArtConfigPage .ccaModalHead {
                position: sticky; top: 0; z-index: 2;
                background: var(--theme-body-background-color, #101010);
            }
            #CustomCoverArtConfigPage .ccaModalFilters { flex-wrap: wrap; gap: .5em; }
            #CustomCoverArtConfigPage .ccaModalClose { min-width: 44px; min-height: 44px; }
            #CustomCoverArtConfigPage .ccaPosterGrid { grid-template-columns: repeat(2, 1fr); }
        }
```

- [ ] **Step 4: Verify**

Run the `node --check` snippet and the full suite (**123 passing**).

Manual, at 360 × 640 in device emulation: no horizontal scroll anywhere; walk all five steps; open the poster browser and confirm it fills the screen with a two-column grid and a reachable close button; confirm the layers ⋯ toggle reveals the actions; confirm Apply is reachable without scrolling to the bottom.

- [ ] **Step 5: Commit**

```bash
git add Configuration/configPage.html
git commit -m "feat(ux): mobile layout, compact layer rows, full-screen poster browser"
```

---

## Task 7: Undo/redo

**Files:**
- Modify: `Configuration/configPage.html`

**Interfaces:**
- Produces: `history` (`{stack: [], index: -1}`), `snapshot()`, `commit()` (debounced 400 ms), `undo()`, `redo()`, `restoreSnapshot(json)`, `updateHistoryButtons()`.
- Consumes: `doc`, `syncControlsFromDocument`, `renderLayersPanel`, `scheduleRender`, `stripUiKeys`.

> **Design:** Carried forward from the superseded Phase 4 plan, Task 1, with one change: the toolbar sits beside the canvas rather than in the preview card header, so it is reachable from every step. Step navigation is not an edit and must not push history.

- [ ] **Step 1: Add the toolbar**

Immediately above the canvas in `.ccaPreviewCol`:

```html
<div class="ccaHistoryBar">
    <button is="emby-button" type="button" id="ccaUndo" class="raised" disabled title="Ctrl+Z">
        <span data-i18n="ui.undo">Undo</span>
    </button>
    <button is="emby-button" type="button" id="ccaRedo" class="raised" disabled title="Ctrl+Y">
        <span data-i18n="ui.redo">Redo</span>
    </button>
</div>
```

i18n both languages: `'ui.undo': 'Undo'` / `'Ongedaan maken'`, `'ui.redo': 'Redo'` / `'Opnieuw'`.
Add `ccaUndo` and `ccaRedo` to `RequiredIds` in `ConfigPageStructureTests`.

- [ ] **Step 2: Implement the history module**

```javascript
        // --- Undo/redo ----------------------------------------------------
        // A snapshot stack of the serialized document. UI-only keys are stripped so a
        // snapshot cannot capture a decoded <img> or a transient selection.
        var HISTORY_CAP = 50;
        var history = { stack: [], index: -1 };

        function snapshot() {
            var json = JSON.stringify(stripUiKeys(doc));
            if (history.index >= 0 && history.stack[history.index] === json) { return; }
            // Any new edit truncates the redo tail.
            history.stack = history.stack.slice(0, history.index + 1);
            history.stack.push(json);
            if (history.stack.length > HISTORY_CAP) { history.stack.shift(); }
            history.index = history.stack.length - 1;
            updateHistoryButtons();
        }

        // Bursts of slider input collapse into one entry; discrete actions call snapshot()
        // directly so they land immediately.
        var commit = debounce(snapshot, 400);

        function restoreSnapshot(json) {
            var selected = doc._selectedId;
            doc = JSON.parse(json);
            // A snapshot is a plain serialized document, so it needs the same normalization
            // a template load gets before anything dereferences it.
            normalizeEffects(doc);
            normalizeBackgroundSource(doc);
            doc._selectedId = selected;
            // A restored document carries server paths but no decoded bitmaps.
            bgImageEl = null;
            hydrateLayerImages();
            renderLayersPanel();
            syncControlsFromDocument();
            scheduleRender();
        }

        function undo() {
            if (history.index <= 0) { return; }
            history.index--;
            restoreSnapshot(history.stack[history.index]);
            updateHistoryButtons();
        }

        function redo() {
            if (history.index >= history.stack.length - 1) { return; }
            history.index++;
            restoreSnapshot(history.stack[history.index]);
            updateHistoryButtons();
        }

        function updateHistoryButtons() {
            var u = el('ccaUndo'), r = el('ccaRedo');
            if (u) { u.disabled = history.index <= 0; }
            if (r) { r.disabled = history.index >= history.stack.length - 1; }
        }
```

- [ ] **Step 3: Wire the triggers**

In `bindEvents()`:

```javascript
            el('ccaUndo').addEventListener('click', undo);
            el('ccaRedo').addEventListener('click', redo);

            document.addEventListener('keydown', function (ev) {
                if (!(ev.ctrlKey || ev.metaKey)) { return; }
                // Never hijack text editing: the browser's own undo belongs to the field.
                var t = ev.target;
                if (t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) { return; }
                var k = ev.key.toLowerCase();
                if (k === 'z' && !ev.shiftKey) { ev.preventDefault(); undo(); }
                else if (k === 'y' || (k === 'z' && ev.shiftKey)) { ev.preventDefault(); redo(); }
            });
```

Call `commit()` alongside `scheduleRender()` in the control handlers that mutate `doc` (the text/effect/background/output bindings). Call `snapshot()` directly — not `commit()` — at the end of `addTextLayer`, `duplicateLayer`, `deleteLayer`, `moveLayer`, `applyJellyfinPreset`, the template-load handler, and the logo-upload `img.onload`. Call `snapshot()` once in `pageshow` after the initial `syncControlsFromDocument()` so the starting document is the base entry.

Do **not** call either from `openStep`, `initAdvanced`'s toggle, or `renderLayersPanel`.

- [ ] **Step 4: Verify**

Run the `node --check` snippet and the full suite (**123 passing**).

Manual: change the title, drag a slider, add a layer, then Ctrl+Z repeatedly — each undo steps back one logical edit, the slider burst counts as one, and typing in a text field still gets the browser's own undo.

- [ ] **Step 5: Commit**

```bash
git add Configuration/configPage.html tests/CustomCoverArt.Tests/ConfigPageStructureTests.cs
git commit -m "feat(ux): undo/redo over the whole document"
```

---

## Task 8: Preview modes

**Files:**
- Modify: `Configuration/configPage.html`

**Interfaces:**
- Produces: `renderPreviewContexts()`, `drawDocInto(cv, W, H)`.
- Consumes: `paintBackgroundOnly`, `drawTextLayer`, `drawImageLayer`, the effect draw functions.

> **Design:** Carried forward from the superseded Phase 4 plan, Task 3, unchanged. The same `doc` is drawn into small offscreen canvases at each context's aspect ratio; normalized coordinates mean the design re-flows correctly. Presentation only — no server change.

- [ ] **Step 1: Add the strip markup**

In step 5's body, above the apply row:

```html
<div class="inputContainer">
    <label data-i18n="ui.inContext">In context</label>
    <div class="ccaContexts" id="ccaContexts"></div>
    <div class="fieldDescription" data-i18n="ui.inContextHint">How the cover looks at other shapes. The canvas above is the real design.</div>
</div>
```

i18n both languages: `'ui.inContext': 'In context'` / `'In context'`, `'ui.inContextHint'` (translate the sentence), and `'ctx.wide': 'Wide'`/`'Breed'`, `'ctx.square': 'Square'`/`'Vierkant'`, `'ctx.poster': 'Poster'`/`'Poster'`.
Add `ccaContexts` to `RequiredIds`.

- [ ] **Step 2: Implement the renderer**

```javascript
        // --- Preview contexts ---------------------------------------------
        // The SAME document drawn at other aspect ratios. Layer coordinates are
        // normalized, so a design re-flows rather than being letterboxed — which is
        // exactly what you want to check before applying.
        var PREVIEW_CONTEXTS = [
            { key: 'wide', w: 320, h: 180 },
            { key: 'square', w: 200, h: 200 },
            { key: 'poster', w: 150, h: 225 }
        ];

        // Draws background + effects + layers into an arbitrary-sized context, reusing the
        // same functions renderDocument uses so the two cannot drift. Deliberately omits
        // the selection chrome.
        function drawDocInto(ctx, W, H) {
            ctx.clearRect(0, 0, W, H);
            paintBackgroundOnly(ctx, W, H);
            drawSoftLight(ctx, W, H);
            doc.Layers.forEach(function (layer) {
                if (!layer.Visible) { return; }
                if (layer.Type === 'text') { drawTextLayer(ctx, layer, W, H); }
                else if (layer.Type === 'image') { drawImageLayer(ctx, layer, W, H); }
            });
            drawVignette(ctx, W, H);
            drawGrain(ctx, W, H);
            drawBorder(ctx, W, H);
        }

        var renderPreviewContexts = debounce(function () {
            var box = el('ccaContexts');
            if (!box || box.offsetParent === null) { return; }  // step collapsed: skip the work
            box.innerHTML = '';
            PREVIEW_CONTEXTS.forEach(function (c) {
                var wrap = document.createElement('div');
                wrap.className = 'ccaContext';
                var cv = document.createElement('canvas');
                cv.width = c.w; cv.height = c.h;
                drawDocInto(cv.getContext('2d'), c.w, c.h);
                var cap = document.createElement('div');
                cap.className = 'ccaContextCap';
                cap.textContent = t('ctx.' + c.key);
                wrap.appendChild(cv); wrap.appendChild(cap);
                box.appendChild(wrap);
            });
        }, 300);
```

Call `renderPreviewContexts()` at the end of `renderDocument()` and from `openStep` when step 5 is opened.

- [ ] **Step 3: Add the CSS**

```css
        #CustomCoverArtConfigPage .ccaContexts { display: flex; gap: .8em; overflow-x: auto; padding: .4em 0; }
        #CustomCoverArtConfigPage .ccaContext { flex: 0 0 auto; text-align: center; }
        #CustomCoverArtConfigPage .ccaContext canvas {
            display: block; max-width: 100%; border-radius: 6px;
            border: 1px solid rgba(127,127,127,.25);
        }
        #CustomCoverArtConfigPage .ccaContextCap { font-size: .8em; opacity: .6; margin-top: .3em; }
```

- [ ] **Step 4: Verify**

Run the `node --check` snippet and the full suite (**123 passing**).

Manual: open step 5, confirm three thumbnails render and update as you edit; at 360px confirm the strip scrolls horizontally without the page scrolling.

- [ ] **Step 5: Commit**

```bash
git add Configuration/configPage.html tests/CustomCoverArt.Tests/ConfigPageStructureTests.cs
git commit -m "feat(ux): preview the cover at other aspect ratios"
```

---

## Task 9: Release 3.3.0.0

**Files:**
- Modify: `CustomCoverArt.csproj`, `CHANGELOG.md`, `README.md`

- [ ] **Step 1: Bump the version**

`CustomCoverArt.csproj`: `<Version>3.2.0.0</Version>` → `<Version>3.3.0.0</Version>`.

- [ ] **Step 2: CHANGELOG**

Add above the `## 3.2.0.0` heading, written for users, not developers:

```markdown
## 3.3.0.0
The configuration page is now a **guided walkthrough**. Instead of every control at once, the design is laid out as five numbered steps — **Target**, **Background**, **Text & logos**, **Effects**, **Output** — one open at a time, and you can jump straight to any of them. Each step shows the controls most people need, with the rest one click away under **Advanced**. Choosing a background is now a single choice — image, poster collage, gradient or solid colour — instead of a source dropdown and a separate gradient tick that overlapped. The whole page is **properly usable on a phone** now: bigger touch targets, the preview pinned at the top, a full-screen poster browser, and canvas handles you can actually grab. Two more additions: **undo/redo** across the whole design (Ctrl+Z / Ctrl+Y), and an **in-context preview** showing your cover at other shapes before you apply it. Existing designs and saved templates are unaffected.
```

- [ ] **Step 3: README**

Add to the features table:

```markdown
| 🧭 | **Guided steps** | Five numbered steps with essentials up front and advanced controls one click away |
| ↩️ | **Undo / redo** | Ctrl+Z and Ctrl+Y across the whole design |
| 📱 | **Mobile-friendly** | Touch targets, sticky preview, full-screen poster browser, grabbable canvas handles |
```

Rewrite the numbered Usage list to follow the five steps, and note that the background is a single source choice.

- [ ] **Step 4: Full verification**

```
& "$env:USERPROFILE\.dotnet\dotnet.exe" build -c Release     # 0 warnings, 0 errors
& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal   # 123 passing
```
Plus the `node --check` snippet.

- [ ] **Step 5: Commit**

```bash
git add CustomCoverArt.csproj CHANGELOG.md README.md
git commit -m "chore: release 3.3.0.0 — guided editor, mobile, undo/redo"
```

---

## Self-Review (run after all tasks)

- **Spec coverage:** accordion steps (Task 3) · never locked (Task 3 Step 5) · essentials/advanced (Task 4) · templates split across steps 1 and 5 (Task 3 mapping) · regroup-in-place preserving ids (Task 3 test) · background source consolidation + migration + two-way compat (Tasks 1–2) · mobile canvas handles (Task 5) · mobile layout/layers/browser (Task 6) · undo/redo with toolbar beside the canvas (Task 7) · preview modes (Task 8) · dead spinner removed (Task 3) · id pin, step well-formedness, en/nl (Tasks 1–3 tests).
- **Order consistency:** the client's `normalizeBackgroundSource` rules match `DocumentMigration.NormalizeBackgroundSource` exactly — same four branches, same order.
- **Ownership:** `.ccaStepBody` is written only by `openStep`; `.ccaAdvBody` only by `initAdvanced`; background control visibility only by `updateBackgroundVisibility`. Verify no other function writes `style.display` on those elements.
- **Type consistency:** `hitTestHandle(pt, ev)` and `hitTestLayer(pt, ev)` both take the event after Task 5 — check both call sites in `onCanvasPointerDown` were updated.
- **Test count ladder:** 105 → 119 (Task 1) → 123 (Task 3) → 123 through Task 9.
