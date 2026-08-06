# Gradient Overlay Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a multi-stop linear gradient with per-stop alpha that composites over the finished background and under the text and logo layers, so a cover can fade into solid colour.

**Architecture:** The overlay reuses the existing `GradientSettings` type, adding an `Alpha` field to `GradientStop`. The server renders it into its own transparent `Rgba32` buffer and composites that onto the canvas from `ComposeDocumentFrame`, immediately after `ApplySoftLight`. The client canvas mirrors the same slot in `renderDocument`. The existing gradient-stop editor is parameterised by container and document target so the background gradient and the overlay each get an instance.

**Tech Stack:** .NET 8 / Jellyfin 10.11 plugin · SixLabors.ImageSharp 3.1.12 · SixLabors.ImageSharp.Drawing 2.1.7 · xUnit · vanilla JS in a single embedded `configPage.html`

**Spec:** `docs/superpowers/specs/2026-08-06-gradient-overlay-design.md`

## Global Constraints

- Branch is `feat/gradient-overlay`, already created off `main`.
- **Existing documents and saved templates must render byte-identically.** `GradientStop.Alpha` defaults to `1f`; the overlay is off when `Overlay` is null or `IsEnabled` is false.
- **The overlay renders linear only.** `Type`, `CenterX`, `CenterY` and `Radius` exist on the reused `GradientSettings` type but are inert on an overlay. The UI never writes them.
- **Fewer than two stops means off.** The overlay must NOT inherit `BuildColorStops`'s `StartColor`/`EndColor` fallback — an opaque black→white ramp would obliterate the poster.
- Angle 90° is top→bottom; stop 0 is the top edge.
- `Alpha` and `Position` are stored `0..1` in the document and presented as whole percentages in the UI.
- New `data-i18n` keys go in the **in-page `I18N` object** in `configPage.html` (`en:` at line 840, `nl:` at line 896), NOT in `Resources/*.json`.
- Run tests with `dotnet test tests/CustomCoverArt.Tests/` from the repo root.
- Commit after every task.

---

## File Structure

| File | Responsibility | Change |
|---|---|---|
| `Models/CoverArtModels.cs` | `GradientStop.Alpha` | Modify (~line 184) |
| `Models/CoverDocument.cs` | `BackgroundLayer.Overlay` | Modify (~line 33) |
| `Services/DocumentRenderer.cs` | `CreateGradientBrush`, `BuildColorStops` alpha, `ApplyGradientOverlay`, call site | Modify (lines 23-54, 297-348) |
| `tests/CustomCoverArt.Tests/GradientOverlayTests.cs` | All server-side overlay tests | Create |
| `tests/CustomCoverArt.Tests/ConfigPageStructureTests.cs` | New control ids | Modify (line 42) |
| `Configuration/configPage.html` | Markup, i18n, stop-editor parameterisation, `drawGradientOverlay`, presets, palette wiring | Modify |
| `CHANGELOG.md`, `CustomCoverArt.csproj` | v3.4.0.0 release notes and version | Modify |
| `README.md` | Feature documentation | Modify |

Tasks 1-4 are server-side and independently testable. Tasks 5-8 are client-side. Task 9 is the release.

---

### Task 1: Add `Alpha` to `GradientStop` and honour it in `BuildColorStops`

**Files:**
- Modify: `Models/CoverArtModels.cs:184-188`
- Modify: `Services/DocumentRenderer.cs:333-348`
- Test: `tests/CustomCoverArt.Tests/GradientOverlayTests.cs` (create)

**Interfaces:**
- Consumes: nothing
- Produces: `GradientStop.Alpha` (float, 0..1, default 1f). `DocumentRenderer.BuildColorStops(GradientSettings)` keeps its signature and return type `ColorStop[]`, now applying stop alpha.

- [ ] **Step 1: Write the failing test**

Create `tests/CustomCoverArt.Tests/GradientOverlayTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/CustomCoverArt.Tests/ --filter "FullyQualifiedName~GradientOverlayTests"`
Expected: FAIL to compile — `'GradientStop' does not contain a definition for 'Alpha'`.

- [ ] **Step 3: Add the `Alpha` field**

In `Models/CoverArtModels.cs`, inside `public class GradientStop`, after the `Position` property:

```csharp
    /// <summary>
    /// Per-stop opacity, 0..1. Defaults to 1 so every gradient written before the
    /// background overlay existed renders byte-for-byte as it always did.
    /// </summary>
    public float Alpha { get; set; } = 1f;
```

- [ ] **Step 4: Apply the alpha in `BuildColorStops`**

In `Services/DocumentRenderer.cs`, in `BuildColorStops`, change the explicit-stops branch. Replace:

```csharp
                .Select(s => new ColorStop(Math.Clamp(s.Position, 0f, 1f), SafeColor(s.Color, Color.Gray)))
```

with:

```csharp
                .Select(s => new ColorStop(
                    Math.Clamp(s.Position, 0f, 1f),
                    SafeColor(s.Color, Color.Gray).WithAlpha(Math.Clamp(s.Alpha, 0f, 1f))))
```

`WithAlpha` is already used in this file (line 387), so no new API surface.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/CustomCoverArt.Tests/ --filter "FullyQualifiedName~GradientOverlayTests"`
Expected: PASS, 2 tests.

- [ ] **Step 6: Run the whole suite to confirm nothing regressed**

Run: `dotnet test tests/CustomCoverArt.Tests/`
Expected: PASS. `BackgroundSourceTests` and `DocumentRenderTests` exercise gradient backgrounds and must be unaffected, because `Alpha` defaults to 1.

- [ ] **Step 7: Commit**

```bash
git add Models/CoverArtModels.cs Services/DocumentRenderer.cs tests/CustomCoverArt.Tests/GradientOverlayTests.cs
git commit -m "feat: add per-stop alpha to gradient stops"
```

---

### Task 2: Extract the shared gradient brush

Pure refactor — no behaviour change. Splitting it out first means Task 3 cannot accidentally fork the geometry.

**Files:**
- Modify: `Services/DocumentRenderer.cs:297-327`

**Interfaces:**
- Consumes: `BuildColorStops` from Task 1.
- Produces: `internal static Brush CreateGradientBrush(GradientSettings g, int width, int height, bool forceLinear = false)`. Returns a `LinearGradientBrush` when `forceLinear` is true or `g.Type != GradientType.Radial`, else a `RadialGradientBrush`.

- [ ] **Step 1: Write the failing test**

Append to `tests/CustomCoverArt.Tests/GradientOverlayTests.cs`, inside the class:

```csharp
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
```

Add `using SixLabors.ImageSharp.Processing;` and `using SixLabors.ImageSharp.Drawing.Processing;` to the file's usings.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/CustomCoverArt.Tests/ --filter "CreateGradientBrush_ForceLinear_IgnoresRadialType"`
Expected: FAIL to compile — `'DocumentRenderer' does not contain a definition for 'CreateGradientBrush'`.

- [ ] **Step 3: Extract the method**

In `Services/DocumentRenderer.cs`, replace the whole body of `ApplyGradientBackground` (lines 297-327) with:

```csharp
    internal static void ApplyGradientBackground(Image<Rgba32> image, GradientSettings gradient)
    {
        var brush = CreateGradientBrush(gradient, image.Width, image.Height);
        image.Mutate(x => x.Fill(brush));
    }

    /// <summary>
    /// Builds the gradient brush: radial when the settings ask for it, otherwise a linear
    /// ramp along a line through the centre at <c>Angle</c> degrees, long enough to span
    /// the canvas corner to corner.
    ///
    /// <paramref name="forceLinear"/> exists for the background OVERLAY, which reuses this
    /// same settings type but is deliberately linear-only — see the spec's inert-fields note.
    /// Shared with <see cref="ApplyGradientOverlay"/> so the two cannot drift apart.
    /// </summary>
    internal static Brush CreateGradientBrush(GradientSettings gradient, int width, int height, bool forceLinear = false)
    {
        var stops = BuildColorStops(gradient);

        if (!forceLinear && gradient.Type == GradientType.Radial)
        {
            var centerX = gradient.CenterX * width;
            var centerY = gradient.CenterY * height;
            var radius = Math.Max(1f, gradient.Radius * Math.Min(width, height));

            return new RadialGradientBrush(new PointF(centerX, centerY), radius, GradientRepetitionMode.None, stops);
        }

        var rad = gradient.Angle * Math.PI / 180.0;
        var dx = (float)Math.Cos(rad);
        var dy = (float)Math.Sin(rad);
        var cx = width / 2f;
        var cy = height / 2f;
        var half = (Math.Abs(dx) * width + Math.Abs(dy) * height) / 2f;

        var p0 = new PointF(cx - dx * half, cy - dy * half);
        var p1 = new PointF(cx + dx * half, cy + dy * half);

        return new LinearGradientBrush(p0, p1, GradientRepetitionMode.None, stops);
    }
```

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test tests/CustomCoverArt.Tests/`
Expected: PASS. This is a pure refactor; `BackgroundSourceTests` and `DocumentRenderTests` prove the background gradient still renders identically.

- [ ] **Step 5: Commit**

```bash
git add Services/DocumentRenderer.cs tests/CustomCoverArt.Tests/GradientOverlayTests.cs
git commit -m "refactor: extract CreateGradientBrush from ApplyGradientBackground"
```

---

### Task 3: Add `Overlay` to the model and render it

The core of the feature. Step 1's blend assertion is the most important test in the plan — it is the guard against ImageSharp flattening the brush alpha.

**Files:**
- Modify: `Models/CoverDocument.cs:33`
- Modify: `Services/DocumentRenderer.cs` (add method; call site at lines 34-37)
- Test: `tests/CustomCoverArt.Tests/GradientOverlayTests.cs`

**Interfaces:**
- Consumes: `CreateGradientBrush` (Task 2), `GradientStop.Alpha` (Task 1).
- Produces: `BackgroundLayer.Overlay` (`GradientSettings?`, null means off) and `internal static void DocumentRenderer.ApplyGradientOverlay(Image<Rgba32> canvas, GradientSettings? overlay)`.

- [ ] **Step 1: Write the failing tests**

Append to `GradientOverlayTests.cs`, inside the class:

```csharp
    /// <summary>A 40x40 document whose background is solid white, with no layers.</summary>
    private static CoverDocument WhiteDoc()
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 40, Height = 40 } };
        doc.Background.Source = BackgroundSources.Solid;
        doc.Background.DimColor = "#ffffff";
        return doc;
    }

    /// <summary>A bottom-fade overlay: transparent at the top, fully opaque black at the bottom.</summary>
    private static GradientSettings BottomFadeBlack() => new()
    {
        IsEnabled = true,
        Angle = 90f,
        Stops = new()
        {
            new GradientStop { Color = "#000000", Position = 0f, Alpha = 0f },
            new GradientStop { Color = "#000000", Position = 1f, Alpha = 1f }
        }
    };

    /// <summary>
    /// THE important one. Filling the canvas directly with a semi-transparent brush is the
    /// trap that once blacked out dimmed backgrounds — ImageSharp's Fill ignores brush alpha
    /// on alpha-less pixel formats. If that happens here the middle row goes solid black
    /// instead of mid-grey, so this assertion is the regression guard for the whole feature.
    /// </summary>
    [Fact]
    public void ApplyGradientOverlay_RampsAlphaAcrossTheCanvas()
    {
        var doc = WhiteDoc();
        doc.Background.Overlay = BottomFadeBlack();

        using var canvas = new Image<Rgba32>(40, 40);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        Assert.True(canvas[20, 0].R > 230, "Top must stay near the white background.");
        Assert.True(canvas[20, 39].R < 25, "Bottom must reach the opaque overlay colour.");
        Assert.InRange(canvas[20, 20].R, 90, 165);   // a genuine blend, not 0 and not 255
    }

    [Fact]
    public void ApplyGradientOverlay_NullOrDisabled_LeavesTheCanvasUntouched()
    {
        using var withoutOverlay = new Image<Rgba32>(40, 40);
        DocumentRenderer.ComposeDocumentFrame(withoutOverlay, null, WhiteDoc());

        var disabledDoc = WhiteDoc();
        disabledDoc.Background.Overlay = BottomFadeBlack();
        disabledDoc.Background.Overlay.IsEnabled = false;

        using var withDisabled = new Image<Rgba32>(40, 40);
        DocumentRenderer.ComposeDocumentFrame(withDisabled, null, disabledDoc);

        Assert.Equal(withoutOverlay[20, 39], withDisabled[20, 39]);
        Assert.Equal(withoutOverlay[20, 20], withDisabled[20, 20]);
    }

    /// <summary>
    /// Guards the fallback divergence: BuildColorStops falls back to an opaque black-to-white
    /// ramp when there are fewer than two stops, which for an overlay would obliterate the
    /// background. Fewer than two stops must mean "off" instead.
    /// </summary>
    [Fact]
    public void ApplyGradientOverlay_FewerThanTwoStops_IsANoOp()
    {
        var doc = WhiteDoc();
        doc.Background.Overlay = new GradientSettings
        {
            IsEnabled = true,
            Angle = 90f,
            Stops = new() { new GradientStop { Color = "#000000", Position = 1f, Alpha = 1f } }
        };

        using var canvas = new Image<Rgba32>(40, 40);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        Assert.True(canvas[20, 39].R > 230, "A one-stop overlay must not paint anything.");
    }

    /// <summary>
    /// The overlay lives in ComposeDocumentFrame, not inside ApplyBackgroundLayer, precisely
    /// so it works for every source. ApplyBackgroundLayer runs only on the image path.
    /// </summary>
    [Theory]
    [InlineData(BackgroundSources.Solid)]
    [InlineData(BackgroundSources.Gradient)]
    [InlineData(BackgroundSources.Collage)]
    [InlineData(BackgroundSources.Upload)]
    public void ApplyGradientOverlay_AppliesToEverySource(string source)
    {
        var doc = WhiteDoc();
        doc.Background.Source = source;
        doc.Background.Gradient = new GradientSettings
        {
            IsEnabled = true,
            Stops = new() { new GradientStop { Color = "#ffffff", Position = 0f },
                            new GradientStop { Color = "#ffffff", Position = 1f } }
        };
        doc.Background.Overlay = BottomFadeBlack();

        using var canvas = new Image<Rgba32>(40, 40);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        Assert.True(canvas[20, 39].R < 25, $"Overlay must apply for source '{source}'.");
    }

    /// <summary>Type is inert on an overlay: a radial one still renders as a linear ramp.</summary>
    [Fact]
    public void ApplyGradientOverlay_RadialType_StillRendersLinear()
    {
        var doc = WhiteDoc();
        doc.Background.Overlay = BottomFadeBlack();
        doc.Background.Overlay.Type = GradientType.Radial;

        using var canvas = new Image<Rgba32>(40, 40);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        // Under a linear 90-degree ramp the whole bottom row is opaque, corners included.
        // A radial brush would leave the bottom corners far lighter than the bottom centre.
        Assert.True(canvas[0, 39].R < 25 && canvas[39, 39].R < 25, "Bottom corners must be opaque, so the ramp is linear.");
    }

    /// <summary>Ordering: the overlay sits over soft-light and under the text.</summary>
    [Fact]
    public void ApplyGradientOverlay_SitsUnderTextLayers()
    {
        var doc = WhiteDoc();
        doc.Background.Overlay = BottomFadeBlack();
        doc.Effects.SoftLight = new SoftLightSettings { Enabled = true, Color = "#ffffff", Opacity = 1f };
        doc.Layers.Add(new CoverLayer
        {
            Id = "t", Type = "text", Content = "MMMM", Color = "#ff0000",
            X = 0.5f, Y = 0.9f, Size = 0.25f, Align = TextAlign.Center
        });

        using var canvas = new Image<Rgba32>(40, 40);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        // A fully opaque white soft-light wash would erase the overlay if it ran after it.
        Assert.True(canvas[20, 39].R < 60, "Overlay must be applied after soft-light.");

        // Somewhere in the text band a red text pixel must have survived the overlay.
        var foundRed = false;
        for (var y = 28; y < 40 && !foundRed; y++)
        {
            for (var x = 0; x < 40; x++)
            {
                if (canvas[x, y].R > 120 && canvas[x, y].G < 80) { foundRed = true; break; }
            }
        }
        Assert.True(foundRed, "Text must be drawn on top of the overlay.");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/CustomCoverArt.Tests/ --filter "FullyQualifiedName~GradientOverlayTests"`
Expected: FAIL to compile — `'BackgroundLayer' does not contain a definition for 'Overlay'`.

- [ ] **Step 3: Add the model field**

In `Models/CoverDocument.cs`, in `class BackgroundLayer`, immediately after the `Gradient` property (line 33):

```csharp
    /// <summary>
    /// Optional colour gradient composited OVER the finished background and UNDER the
    /// layers, for the "poster fading into solid colour" look. Null means no overlay.
    /// Linear only: the Type/Center/Radius fields it inherits from the reused
    /// GradientSettings type are inert here — see ApplyGradientOverlay.
    /// </summary>
    public GradientSettings? Overlay { get; set; }
```

- [ ] **Step 4: Add the render method**

In `Services/DocumentRenderer.cs`, immediately after `CreateGradientBrush`:

```csharp
    /// <summary>
    /// Composites the background overlay: a linear multi-stop gradient whose stops carry
    /// their own alpha, drawn over the finished background and under the layers.
    ///
    /// Deliberately does NOT use BuildColorStops' Start/End fallback. That fallback yields
    /// an OPAQUE black-to-white ramp, which for a background gradient is a reasonable
    /// default and for an overlay would obliterate the poster. Fewer than two stops = off.
    /// </summary>
    internal static void ApplyGradientOverlay(Image<Rgba32> canvas, GradientSettings? overlay)
    {
        if (overlay is null || !overlay.IsEnabled) { return; }
        if (overlay.Stops is not { Count: >= 2 }) { return; }

        var brush = CreateGradientBrush(overlay, canvas.Width, canvas.Height, forceLinear: true);

        // Rendered into its OWN transparent Rgba32 buffer, then composited. Filling the
        // canvas directly with a semi-transparent brush is the trap that once blacked out
        // dimmed backgrounds (see ApplyBackgroundLayer): Fill ignores brush alpha on
        // alpha-less pixel formats. This buffer is explicitly Rgba32, and SrcOver onto a
        // zero-alpha destination resolves exactly to the source, so the default graphics
        // options are already correct — no custom GraphicsOptions needed.
        using var scrim = new Image<Rgba32>(canvas.Width, canvas.Height);
        scrim.Mutate(x => x.Fill(brush));
        canvas.Mutate(x => x.DrawImage(scrim, Point.Empty, 1f));
    }
```

- [ ] **Step 5: Wire it into the compositing order**

In `Services/DocumentRenderer.cs`, in `ComposeDocumentFrame`, after the `ApplySoftLight` call (line 36) and before the `foreach (var layer in doc.Layers)` loop, insert:

```csharp
        // The overlay goes last before the layers: its colour must be authoritative (a
        // soft-light wash after it would desaturate it) and it must be the last thing
        // under the text, which is the whole legibility contract. It lives HERE rather
        // than in ApplyBackgroundLayer so it applies to all four background sources —
        // ApplyBackgroundLayer runs only on the image path.
        ApplyGradientOverlay(canvas, doc.Background.Overlay);
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/CustomCoverArt.Tests/ --filter "FullyQualifiedName~GradientOverlayTests"`
Expected: PASS, 10 tests.

If `ApplyGradientOverlay_RampsAlphaAcrossTheCanvas` fails with the middle pixel at 0 or 255, the transparent-buffer assumption is wrong. Fix it by passing explicit options rather than weakening the test:

```csharp
        var options = new DrawingOptions
        {
            GraphicsOptions = new GraphicsOptions
            {
                AlphaCompositionMode = PixelAlphaCompositionMode.Src,
                ColorBlendingMode = PixelColorBlendingMode.Normal,
                BlendPercentage = 1f
            }
        };
        scrim.Mutate(x => x.Fill(options, brush));
```

- [ ] **Step 7: Run the whole suite**

Run: `dotnet test tests/CustomCoverArt.Tests/`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add Models/CoverDocument.cs Services/DocumentRenderer.cs tests/CustomCoverArt.Tests/GradientOverlayTests.cs
git commit -m "feat: composite a gradient overlay over the background"
```

---

### Task 4: Confirm round-tripping and back-compat

No production code expected. This task exists because "existing designs are unaffected" is the feature's main promise and deserves its own gate.

**Files:**
- Test: `tests/CustomCoverArt.Tests/GradientOverlayTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-3.
- Produces: nothing.

- [ ] **Step 1: Write the tests**

Append to `GradientOverlayTests.cs`, inside the class. Add `using System.Text.Json;` to the usings.

```csharp
    /// <summary>
    /// A document POSTed without an Overlay (every document written before this release)
    /// must deserialize with Overlay null and survive Normalize, which is the null-guard
    /// chokepoint for client-supplied documents.
    /// </summary>
    [Fact]
    public void Normalize_DocumentWithoutOverlay_LeavesItNull()
    {
        var json = """
        {"Canvas":{"Width":40,"Height":40},
         "Background":{"Source":"solid","DimColor":"#ffffff"},
         "Layers":[]}
        """;

        var doc = JsonSerializer.Deserialize<CoverDocument>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })!;

        DocumentMigration.Normalize(doc);

        Assert.Null(doc.Background.Overlay);
    }

    /// <summary>Stops written before Alpha existed deserialize as fully opaque.</summary>
    [Fact]
    public void Deserialize_StopWithoutAlpha_DefaultsToOpaque()
    {
        var json = """{"Color":"#ff0000","Position":0.5}""";

        var stop = JsonSerializer.Deserialize<GradientStop>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        })!;

        Assert.Equal(1f, stop.Alpha);
    }

    /// <summary>The overlay survives a serialize/deserialize round trip with its alphas.</summary>
    [Fact]
    public void Overlay_RoundTripsThroughJson()
    {
        var doc = WhiteDoc();
        doc.Background.Overlay = BottomFadeBlack();

        var json = JsonSerializer.Serialize(doc);
        var back = JsonSerializer.Deserialize<CoverDocument>(json)!;

        Assert.NotNull(back.Background.Overlay);
        Assert.True(back.Background.Overlay!.IsEnabled);
        Assert.Equal(2, back.Background.Overlay.Stops.Count);
        Assert.Equal(0f, back.Background.Overlay.Stops[0].Alpha);
        Assert.Equal(1f, back.Background.Overlay.Stops[1].Alpha);
    }
```

- [ ] **Step 2: Run them**

Run: `dotnet test tests/CustomCoverArt.Tests/ --filter "FullyQualifiedName~GradientOverlayTests"`
Expected: PASS, 13 tests. If `Normalize_DocumentWithoutOverlay_LeavesItNull` fails, something added an unwanted `??=` for `Overlay` in `DocumentMigration.Normalize` — remove it; null is the correct "off" state.

- [ ] **Step 3: Commit**

```bash
git add tests/CustomCoverArt.Tests/GradientOverlayTests.cs
git commit -m "test: pin overlay back-compat and JSON round-tripping"
```

---

### Task 5: Parameterise the gradient-stop editor

Pure refactor of the client, no new controls yet. Doing it before the overlay UI means the overlay gets a working stop editor for free instead of a copied one.

**Files:**
- Modify: `Configuration/configPage.html:2542-2558` (`syncGradientStopsToDoc`, `rebuildGradientStopsUI`)
- Modify: `Configuration/configPage.html:2931-2973` (`collectGradientStops`, `addGradientStop`)
- Modify: `Configuration/configPage.html:3012-3019` (`onControlChange`)

**Interfaces:**
- Consumes: nothing.
- Produces, all in the page script:
  - `collectStops(containerId)` → array of `{Color, Position, Alpha}`
  - `addStopRow(containerId, color, posPercent, alphaPercent, withAlpha)` → void
  - `rebuildStopsUI(containerId, stops, withAlpha, defaults)` → void, where `defaults` is `[{color, pos, alpha}, {color, pos, alpha}]` with `pos`/`alpha` as whole percentages, used when fewer than two stops exist
  - `syncStopsToDoc(containerId, target)` → void, where `target` is the object owning a `Stops` array

- [ ] **Step 1: Replace the four functions**

Replace `syncGradientStopsToDoc` and `rebuildGradientStopsUI` (lines 2542-2558) with:

```javascript
        // Reads stop DOM rows into a document object that owns a Stops array. Parameterised
        // by container so the background gradient and the background OVERLAY can each have
        // their own editor without the two forking into near-duplicate copies.
        function syncStopsToDoc(containerId, target) {
            if (!target) { return; }
            target.Stops = collectStops(containerId);
        }

        function syncGradientStopsToDoc() {
            syncStopsToDoc('ccaGradientStops', doc.Background.Gradient);
        }

        // Rebuilds stop DOM rows from a document stop list. `withAlpha` adds the per-stop
        // alpha slider (overlay only); `defaults` seeds two stops when there are fewer than two.
        function rebuildStopsUI(containerId, stops, withAlpha, defaults) {
            var container = el(containerId);
            if (!container) { return; }
            container.innerHTML = '';
            (stops || []).forEach(function (st) {
                var a = pick(st, 'Alpha');
                addStopRow(containerId,
                    pick(st, 'Color') || '#888888',
                    Math.round((pick(st, 'Position') || 0) * 100),
                    Math.round((a === undefined || a === null ? 1 : a) * 100),
                    withAlpha);
            });
            if (container.querySelectorAll('.ccaStop').length < 2) {
                addStopRow(containerId, defaults[0].color, defaults[0].pos, defaults[0].alpha, withAlpha);
                addStopRow(containerId, defaults[1].color, defaults[1].pos, defaults[1].alpha, withAlpha);
            }
        }

        function rebuildGradientStopsUI(stops) {
            rebuildStopsUI('ccaGradientStops', stops, false, [
                { color: '#aa5cc3', pos: 0, alpha: 100 },
                { color: '#00a4dc', pos: 100, alpha: 100 }
            ]);
        }
```

- [ ] **Step 2: Replace `collectGradientStops` and `addGradientStop`**

Replace lines 2931-2973 with:

```javascript
        // NOTE the .ccaStopPos selector. Overlay rows carry a SECOND range input for alpha,
        // so the old input[type="range"] lookup would silently read whichever came first.
        function collectStops(containerId) {
            var stops = [];
            var container = el(containerId);
            if (!container) { return stops; }
            container.querySelectorAll('.ccaStop').forEach(function (row) {
                var alphaEl = row.querySelector('.ccaStopAlpha');
                stops.push({
                    Color: row.querySelector('input[type="color"]').value,
                    Position: (parseFloat(row.querySelector('.ccaStopPos').value) || 0) / 100,
                    Alpha: alphaEl ? (parseFloat(alphaEl.value) || 0) / 100 : 1
                });
            });
            return stops;
        }

        function collectGradientStops() {
            return collectStops('ccaGradientStops');
        }

        function addStopRow(containerId, color, posPercent, alphaPercent, withAlpha) {
            var container = el(containerId);
            if (!container) { return; }
            var row = document.createElement('div');
            row.className = 'ccaStop';

            var c = document.createElement('input');
            c.type = 'color';
            c.value = color || '#888888';

            var p = document.createElement('input');
            p.type = 'range';
            p.min = '0'; p.max = '100'; p.step = '1';
            p.value = (posPercent == null ? 50 : posPercent);
            p.className = 'ccaRange ccaStopPos';

            var a = null;
            if (withAlpha) {
                a = document.createElement('input');
                a.type = 'range';
                a.min = '0'; a.max = '100'; a.step = '1';
                a.value = (alphaPercent == null ? 100 : alphaPercent);
                a.className = 'ccaRange ccaStopAlpha';
                a.title = t('bg.overlay.alpha');
            }

            var rm = document.createElement('button');
            rm.type = 'button';
            rm.className = 'ccaStopRemove';
            rm.textContent = '✕';
            rm.addEventListener('click', function () {
                if (container.querySelectorAll('.ccaStop').length > 2) {
                    container.removeChild(row);
                    onStopsEdited(containerId);
                    scheduleRender();
                }
            });

            row.appendChild(c);
            row.appendChild(p);
            if (a) { row.appendChild(a); }
            row.appendChild(rm);
            container.appendChild(row);
        }

        function addGradientStop(color, posPercent) {
            addStopRow('ccaGradientStops', color, posPercent, 100, false);
        }

        // One place that knows which document object a given stop container edits.
        // Task 6 extends this for the overlay container.
        function onStopsEdited(containerId) {
            if (containerId === 'ccaGradientStops') { syncGradientStopsToDoc(); }
        }
```

Note on `t('bg.overlay.alpha')`: `t` is the page's existing translation helper, defined at `configPage.html:986`. The `bg.overlay.alpha` key is added in Task 6. This is not a broken reference in the meantime — no caller passes `withAlpha: true` until Task 6, so the branch is unreachable for the whole of this task.

- [ ] **Step 3: Route the delegated handler through `onStopsEdited`**

Replace the body of `onControlChange` (lines 3012-3019) with:

```javascript
        function onControlChange(e) {
            var target = e.target;
            if (!target || !target.closest) { return; }
            if (target.closest('#ccaGradientStops')) {
                onStopsEdited('ccaGradientStops');
                scheduleRender();
            }
        }
```

- [ ] **Step 4: Run the suite**

Run: `dotnet test tests/CustomCoverArt.Tests/`
Expected: PASS. `ConfigPageStructureTests` and `PresetTests` parse the page and would catch a mangled edit.

- [ ] **Step 5: Verify the background gradient still works by hand**

This refactor touches the existing gradient path, so it must be checked directly. Build and load the plugin, open the config page, set the background source to **Gradient**, then:
- confirm two stop rows appear with a colour swatch and one position slider each (no alpha slider)
- change a stop colour and confirm the canvas updates
- drag a position slider and confirm the canvas updates
- add a third stop, confirm it renders, then remove it

- [ ] **Step 6: Commit**

```bash
git add Configuration/configPage.html
git commit -m "refactor: parameterise the gradient stop editor by container"
```

---

### Task 6: Overlay markup, i18n and client rendering

**Files:**
- Modify: `Configuration/configPage.html:94-116` (markup, after the Dimming block)
- Modify: `Configuration/configPage.html:867-871` (en i18n), `:923-927` (nl i18n)
- Modify: `Configuration/configPage.html:1172-1199` (`defaultDocument`)
- Modify: `Configuration/configPage.html:1439-1465` (`renderDocument`)
- Modify: `Configuration/configPage.html:1521-1527` (`hexToRgba`), `:1830-1838` (`gradientStops`)
- Modify: `tests/CustomCoverArt.Tests/ConfigPageStructureTests.cs:42-69`

**Interfaces:**
- Consumes: `addStopRow`, `rebuildStopsUI`, `collectStops`, `syncStopsToDoc`, `onStopsEdited` (Task 5).
- Produces: element ids `ccaOverlay`, `ccaOverlayOpts`, `ccaOverlayPreset`, `ccaOverlayStops`, `ccaOverlayAddStop`, `ccaOverlayAngle`, `ccaOverlayAngleVal`; functions `drawGradientOverlay(ctx, W, H)`, `overlayStopsCss(g)`, `ensureOverlay()`.

- [ ] **Step 1: Add the control ids to the structure test**

In `tests/CustomCoverArt.Tests/ConfigPageStructureTests.cs`, add to `RequiredIds` (keeping the list's rough alphabetical grouping):

```csharp
        "ccaOverlay", "ccaOverlayAddStop", "ccaOverlayAngle", "ccaOverlayAngleVal",
        "ccaOverlayOpts", "ccaOverlayPreset", "ccaOverlayStops",
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/CustomCoverArt.Tests/ --filter "NoControlWasLostInTheRestructure"`
Expected: FAIL — "Element ids missing from configPage.html: ccaOverlay, ccaOverlayAddStop, ...".

- [ ] **Step 3: Add the markup**

In `Configuration/configPage.html`, immediately after the Dimming `inputContainer` (which closes at line 97) and before `<div id="ccaGradientOpts"`, insert:

```html
                        <div class="inputContainer">
                            <label class="checkboxContainer">
                                <input is="emby-checkbox" type="checkbox" id="ccaOverlay" />
                                <span data-i18n="bg.overlay">Overlay gradient</span>
                            </label>
                            <div class="fieldDescription" data-i18n="hint.overlay">Fades a colour over the background, under your text — for the look where a poster resolves into solid colour.</div>
                        </div>
                        <div id="ccaOverlayOpts" class="ccaSub" style="display:none;">
                            <div class="inputContainer">
                                <label for="ccaOverlayPreset" data-i18n="bg.overlay.preset">Overlay preset</label>
                                <select is="emby-select" id="ccaOverlayPreset" class="emby-select">
                                    <option value="custom" data-i18n="bg.overlay.custom">Custom</option>
                                    <option value="bottom" selected data-i18n="bg.overlay.bottom">Bottom fade</option>
                                    <option value="top" data-i18n="bg.overlay.top">Top fade</option>
                                    <option value="wash" data-i18n="bg.overlay.wash">Full wash</option>
                                    <option value="duotone" data-i18n="bg.overlay.duotone">Duotone</option>
                                </select>
                            </div>
                            <div class="inputContainer">
                                <label data-i18n="bg.overlay.colors">Colours and opacity</label>
                                <div id="ccaOverlayStops"></div>
                                <button is="emby-button" type="button" id="ccaOverlayAddStop" class="raised"><span data-i18n="bg.addcolor">Add colour</span></button>
                            </div>
                        </div>
```

Then add the angle control inside the Background step's existing **Advanced** body (`<div class="ccaAdvBody" hidden>` at line 148):

```html
                                <div class="inputContainer" id="ccaOverlayAngleRow">
                                    <label for="ccaOverlayAngle"><span data-i18n="bg.overlay.angle">Overlay angle</span> <span class="ccaVal" id="ccaOverlayAngleVal">90°</span></label>
                                    <input type="range" class="ccaRange" id="ccaOverlayAngle" min="0" max="360" step="5" value="90" />
                                </div>
```

- [ ] **Step 4: Add the i18n keys**

In the `en:` block, after line 870 (`'bg.angle': 'Angle', ...`):

```javascript
                'bg.overlay': 'Overlay gradient', 'bg.overlay.preset': 'Overlay preset',
                'bg.overlay.custom': 'Custom', 'bg.overlay.bottom': 'Bottom fade',
                'bg.overlay.top': 'Top fade', 'bg.overlay.wash': 'Full wash', 'bg.overlay.duotone': 'Duotone',
                'bg.overlay.colors': 'Colours and opacity', 'bg.overlay.angle': 'Overlay angle',
                'bg.overlay.alpha': 'Opacity',
                'hint.overlay': 'Fades a colour over the background, under your text — for the look where a poster resolves into solid colour.',
```

In the `nl:` block, after line 926 (`'bg.angle': 'Hoek', ...`):

```javascript
                'bg.overlay': 'Verloop-overlay', 'bg.overlay.preset': 'Overlay-voorinstelling',
                'bg.overlay.custom': 'Aangepast', 'bg.overlay.bottom': 'Vervaging onder',
                'bg.overlay.top': 'Vervaging boven', 'bg.overlay.wash': 'Volledige waas', 'bg.overlay.duotone': 'Duotoon',
                'bg.overlay.colors': 'Kleuren en dekking', 'bg.overlay.angle': 'Overlay-hoek',
                'bg.overlay.alpha': 'Dekking',
                'hint.overlay': 'Laat een kleur over de achtergrond vervagen, onder je tekst — voor de stijl waarbij een poster overgaat in een egale kleur.',
```

- [ ] **Step 5: Widen `hexToRgba` and add alpha-aware stops**

Replace `hexToRgba` (lines 1521-1527) with:

```javascript
        // Accepts #rgb, #rrggbb and #rrggbbaa. An 8-digit hex's own alpha is MULTIPLIED
        // with the passed alpha, so a stop's Alpha field composes with a colour that
        // already carries one rather than one silently winning.
        function hexToRgba(hex, alpha) {
            var h = String(hex || '#000000').replace('#', '');
            if (h.length === 3) { h = h[0] + h[0] + h[1] + h[1] + h[2] + h[2]; }
            var baseAlpha = 1;
            if (h.length === 8) {
                baseAlpha = parseInt(h.slice(6, 8), 16) / 255;
                if (isNaN(baseAlpha)) { baseAlpha = 1; }
                h = h.slice(0, 6);
            }
            var n = parseInt(h, 16);
            if (isNaN(n)) { n = 0; }
            var a = Math.max(0, Math.min(1, (alpha == null ? 1 : alpha) * baseAlpha));
            return 'rgba(' + ((n >> 16) & 255) + ',' + ((n >> 8) & 255) + ',' + (n & 255) + ',' + a + ')';
        }
```

Then add, immediately after `gradientStops` (which ends at line 1838):

```javascript
        // Like gradientStops, but each stop's colour is an rgba() string carrying its Alpha.
        // No black->white fallback here: fewer than two stops means the overlay is off,
        // mirroring DocumentRenderer.ApplyGradientOverlay.
        function overlayStopsCss(g) {
            var stops = (g.Stops || []).slice().sort(function (a, b) { return a.Position - b.Position; });
            if (stops.length < 2) { return null; }
            return stops.map(function (s) {
                var a = (s.Alpha === undefined || s.Alpha === null) ? 1 : s.Alpha;
                return {
                    pos: Math.max(0, Math.min(1, s.Position)),
                    color: hexToRgba(s.Color || '#000000', Math.max(0, Math.min(1, a)))
                };
            });
        }
```

- [ ] **Step 6: Add `drawGradientOverlay` and call it**

Add immediately after `overlayStopsCss`:

```javascript
        // Mirrors DocumentRenderer.ApplyGradientOverlay: linear only, geometry identical to
        // drawGradient's linear branch. Called from renderDocument between drawSoftLight and
        // the layer loop, which is the same slot the server uses.
        function drawGradientOverlay(ctx, W, H) {
            var g = doc.Background && doc.Background.Overlay;
            if (!g || !g.IsEnabled) { return; }
            var stops = overlayStopsCss(g);
            if (!stops) { return; }

            var rad = (g.Angle == null ? 90 : g.Angle) * Math.PI / 180;
            var dx = Math.cos(rad), dy = Math.sin(rad);
            var mx = W / 2, my = H / 2;
            var half = (Math.abs(dx) * W + Math.abs(dy) * H) / 2;
            var grad = ctx.createLinearGradient(mx - dx * half, my - dy * half, mx + dx * half, my + dy * half);
            stops.forEach(function (s) { grad.addColorStop(s.pos, s.color); });

            ctx.save();
            ctx.fillStyle = grad;
            ctx.fillRect(0, 0, W, H);
            ctx.restore();
        }
```

In `renderDocument`, insert the call between `drawSoftLight(ctx, W, H);` (line 1454) and the layer `forEach` (line 1457):

```javascript
            // Over the background and soft-light, under the layers — same slot as the server.
            drawGradientOverlay(ctx, W, H);
```

Leave `paintBackgroundOnly` alone. It feeds the auto-palette sampler, and including the overlay would make the palette sample the colour the user just applied and feed itself.

- [ ] **Step 7: Add the overlay to `defaultDocument`**

In `defaultDocument`, inside the `Background` object, after the `Gradient` object's closing brace (line 1186), add:

```javascript
                    Overlay: null,
```

- [ ] **Step 8: Run the suite**

Run: `dotnet test tests/CustomCoverArt.Tests/`
Expected: PASS — `NoControlWasLostInTheRestructure`, `EveryIdAppearsExactlyOnce` and `EveryI18nKeyUsedInMarkup_ExistsInBothLanguages` all now pass with the new controls and keys.

- [ ] **Step 9: Commit**

```bash
git add Configuration/configPage.html tests/CustomCoverArt.Tests/ConfigPageStructureTests.cs
git commit -m "feat: overlay markup, translations and canvas rendering"
```

---

### Task 7: Wire the overlay controls to the document

**Files:**
- Modify: `Configuration/configPage.html` (control handlers, hydration, `onStopsEdited`, `onControlChange`)

**Interfaces:**
- Consumes: everything from Tasks 5 and 6.
- Produces: `ensureOverlay()` → the overlay object on `doc.Background`, creating a default one if absent; `OVERLAY_PRESETS` (object keyed by preset value); `applyOverlayPreset(name)`; `syncOverlayStopsToDoc()`; `hydrateOverlayControls()`.

- [ ] **Step 1: Add the preset table and helpers**

Add near the other background helpers, after `rebuildGradientStopsUI`:

```javascript
        // Presets set POSITIONS AND ALPHAS ONLY, never colours: applying one keeps each
        // existing stop's colour by index, so a user can try every preset without losing
        // the colour they picked. Extra stops take the last existing colour.
        var OVERLAY_PRESETS = {
            bottom:  [{ pos: 0, alpha: 0 }, { pos: 0.45, alpha: 0 }, { pos: 1, alpha: 0.9 }],
            top:     [{ pos: 0, alpha: 0.9 }, { pos: 0.55, alpha: 0 }, { pos: 1, alpha: 0 }],
            wash:    [{ pos: 0, alpha: 0.35 }, { pos: 1, alpha: 0.85 }],
            duotone: [{ pos: 0, alpha: 0.7 }, { pos: 1, alpha: 0.9 }]
        };

        function ensureOverlay() {
            if (!doc.Background.Overlay) {
                doc.Background.Overlay = { IsEnabled: false, Type: 0, Angle: 90, Stops: [] };
            }
            return doc.Background.Overlay;
        }

        function applyOverlayPreset(name) {
            var preset = OVERLAY_PRESETS[name];
            if (!preset) { return; }   // 'custom' keeps whatever is there
            var ov = ensureOverlay();
            var existing = ov.Stops || [];
            var fallback = (existing.length ? existing[existing.length - 1].Color : null)
                || doc.Background.DimColor || '#000000';
            ov.Angle = 90;
            ov.Stops = preset.map(function (p, i) {
                return {
                    Color: (existing[i] && existing[i].Color) || fallback,
                    Position: p.pos,
                    Alpha: p.alpha
                };
            });
            rebuildOverlayStopsUI();
        }

        function rebuildOverlayStopsUI() {
            var ov = ensureOverlay();
            rebuildStopsUI('ccaOverlayStops', ov.Stops, true, [
                { color: doc.Background.DimColor || '#000000', pos: 0, alpha: 0 },
                { color: doc.Background.DimColor || '#000000', pos: 100, alpha: 90 }
            ]);
            // rebuildStopsUI may have seeded defaults; read them back so doc and DOM agree.
            syncOverlayStopsToDoc();
        }

        function syncOverlayStopsToDoc() {
            syncStopsToDoc('ccaOverlayStops', ensureOverlay());
        }
```

- [ ] **Step 2: Extend `onStopsEdited` and `onControlChange`**

Replace `onStopsEdited` (added in Task 5) with:

```javascript
        function onStopsEdited(containerId) {
            if (containerId === 'ccaGradientStops') { syncGradientStopsToDoc(); }
            else if (containerId === 'ccaOverlayStops') {
                syncOverlayStopsToDoc();
                // A hand-edit means the design no longer matches the named preset.
                var sel = el('ccaOverlayPreset');
                if (sel) { sel.value = 'custom'; }
            }
        }
```

Replace `onControlChange` with:

```javascript
        function onControlChange(e) {
            var target = e.target;
            if (!target || !target.closest) { return; }
            if (target.closest('#ccaGradientStops')) {
                onStopsEdited('ccaGradientStops');
                scheduleRender();
            } else if (target.closest('#ccaOverlayStops')) {
                selectOverlayStopFrom(target);
                onStopsEdited('ccaOverlayStops');
                scheduleRender();
            }
        }
```

- [ ] **Step 3: Add stop selection for the palette**

Add next to the other overlay helpers:

```javascript
        // Which overlay stop a clicked palette swatch recolours. Defaults to the LAST stop:
        // the opaque end is the one people actually recolour.
        var _overlayStopIndex = -1;

        function selectOverlayStopFrom(target) {
            var row = target.closest ? target.closest('.ccaStop') : null;
            var container = el('ccaOverlayStops');
            if (!row || !container) { return; }
            var rows = Array.prototype.slice.call(container.querySelectorAll('.ccaStop'));
            var i = rows.indexOf(row);
            if (i >= 0) { _overlayStopIndex = i; }
        }

        function overlayStopIndex() {
            var ov = doc.Background.Overlay;
            if (!ov || !ov.Stops || !ov.Stops.length) { return -1; }
            if (_overlayStopIndex < 0 || _overlayStopIndex >= ov.Stops.length) {
                return ov.Stops.length - 1;
            }
            return _overlayStopIndex;
        }
```

Wire selection on focus too, so tabbing to a row targets it. Add inside `addStopRow`, just before `container.appendChild(row);`:

```javascript
            if (containerId === 'ccaOverlayStops') {
                row.addEventListener('focusin', function () { selectOverlayStopFrom(row); });
            }
```

- [ ] **Step 4: Extend `applySwatch`**

Replace `applySwatch` (lines 1803-1815) with:

```javascript
        // Where a clicked swatch lands, in order: the selected text layer, then the selected
        // overlay stop when the overlay is on, then the colour wash when it is on. Each
        // updates its own control so the card and the canvas never disagree.
        function applySwatch(hex) {
            var l = selectedLayer();
            if (l && l.Type === 'text') {
                l.Color = hex;
                el('ccaTextColor').value = hex;
            } else if (doc.Background.Overlay && doc.Background.Overlay.IsEnabled && overlayStopIndex() >= 0) {
                var i = overlayStopIndex();
                doc.Background.Overlay.Stops[i].Color = hex;
                var rows = el('ccaOverlayStops').querySelectorAll('.ccaStop');
                if (rows[i]) { rows[i].querySelector('input[type="color"]').value = hex; }
            } else if (doc.Effects.SoftLight.Enabled) {
                doc.Effects.SoftLight.Color = hex;
                el('ccaFxSoftLightColor').value = hex;
            } else {
                return; // nothing sensible to apply it to; leave the design alone
            }
            scheduleRender();
        }
```

Then update the palette hint, which now describes the wrong precedence. In the `en:` block (`configPage.html:866`) replace the `fx.paletteHint` value with:

```javascript
                'fx.paletteHint': 'Click a colour to apply it to the selected text layer — or, with no text layer selected, to the selected overlay stop, then the colour wash.',
```

And in the `nl:` block (`:922`):

```javascript
                'fx.paletteHint': 'Klik op een kleur om die toe te passen op de geselecteerde tekstlaag — of, als er geen tekstlaag geselecteerd is, op de geselecteerde overlay-kleurstop en anders op het kleurwaas.',
```

- [ ] **Step 5: Add the control handlers**

Add alongside the other background control handlers — `ccaDim` is wired at `configPage.html:3211`, `ccaGradientAngle` at `:3229` and `ccaAddStop` at `:3234`. Put these directly after the `ccaAddStop` handler:

```javascript
            el('ccaOverlay').addEventListener('change', function () {
                var on = this.checked;
                ensureOverlay().IsEnabled = on;
                el('ccaOverlayOpts').style.display = on ? '' : 'none';
                var angleRow = el('ccaOverlayAngleRow');
                if (angleRow) { angleRow.style.display = on ? '' : 'none'; }
                if (on && (!doc.Background.Overlay.Stops || doc.Background.Overlay.Stops.length < 2)) {
                    applyOverlayPreset(el('ccaOverlayPreset').value || 'bottom');
                }
                scheduleRender();
            });

            el('ccaOverlayPreset').addEventListener('change', function () {
                applyOverlayPreset(this.value);
                scheduleRender();
            });

            el('ccaOverlayAddStop').addEventListener('click', function () {
                var ov = ensureOverlay();
                var last = ov.Stops && ov.Stops.length ? ov.Stops[ov.Stops.length - 1] : null;
                addStopRow('ccaOverlayStops', (last && last.Color) || doc.Background.DimColor || '#000000', 50, 50, true);
                onStopsEdited('ccaOverlayStops');
                scheduleRender();
            });

            el('ccaOverlayAngle').addEventListener('input', function () {
                ensureOverlay().Angle = parseFloat(this.value) || 0;
                el('ccaOverlayAngleVal').textContent = this.value + '°';
                scheduleRender();
            });
```

- [ ] **Step 6: Hydrate the controls when a document loads**

Add a `hydrateOverlayControls` function and call it from wherever `rebuildGradientStopsUI(g.Stops)` is called during hydration (line 2627):

```javascript
        function hydrateOverlayControls() {
            var ov = doc.Background.Overlay;
            var on = !!(ov && ov.IsEnabled);
            el('ccaOverlay').checked = on;
            el('ccaOverlayOpts').style.display = on ? '' : 'none';
            var angleRow = el('ccaOverlayAngleRow');
            if (angleRow) { angleRow.style.display = on ? '' : 'none'; }

            var angle = (ov && ov.Angle != null) ? ov.Angle : 90;
            el('ccaOverlayAngle').value = angle;
            el('ccaOverlayAngleVal').textContent = angle + '°';

            // A loaded design is whatever it is; naming a preset for it would be a guess.
            el('ccaOverlayPreset').value = 'custom';

            if (on) { rebuildStopsUI('ccaOverlayStops', ov.Stops, true, [
                { color: doc.Background.DimColor || '#000000', pos: 0, alpha: 0 },
                { color: doc.Background.DimColor || '#000000', pos: 100, alpha: 90 }
            ]); }
            _overlayStopIndex = -1;
        }
```

- [ ] **Step 7: Run the suite**

Run: `dotnet test tests/CustomCoverArt.Tests/`
Expected: PASS.

- [ ] **Step 8: Verify by hand**

Build and load the plugin, then:
- tick **Overlay gradient** — the stop rows appear and the canvas immediately shows a bottom fade
- change a stop colour and confirm the canvas updates and the preset switches to *Custom*
- drag an alpha slider and confirm the fade strength changes
- switch presets and confirm your colour is preserved
- open **Advanced**, drag **Overlay angle** to 270 and confirm the fade flips to the top
- switch the background source between Image, Gradient and Solid and confirm the overlay applies over each
- turn **Auto palette** on with no text layer selected, click a swatch, confirm it recolours the selected overlay stop
- untick the overlay and confirm the cover returns exactly to its previous appearance

- [ ] **Step 9: Commit**

```bash
git add Configuration/configPage.html
git commit -m "feat: wire the overlay controls, presets and palette swatches"
```

---

### Task 8: Verify client/server parity

The canvas is an approximation and the server render is authoritative; this task proves they agree before the feature ships.

**Files:** none modified unless a mismatch is found.

- [ ] **Step 1: Build and load the plugin**

- [ ] **Step 2: Build a design that exercises the feature**

Use an image background, a title text layer near the bottom, and a *Bottom fade* overlay with a saturated colour at ~90% alpha.

- [ ] **Step 3: Compare the two renders**

Click **Show server render** and compare against the canvas. Check specifically:
- the fade starts at the same height
- the solid end is the same colour and reaches the same opacity
- the text sits on top of the overlay in both
- the angle matches (set it to 270 and re-compare)

- [ ] **Step 4: Check a multi-stop and a two-colour overlay**

Add a third stop with a different colour mid-ramp and re-compare. Any divergence is a bug in `overlayStopsCss`/`drawGradientOverlay` versus `BuildColorStops`/`ApplyGradientOverlay` — fix the client to match the server, which is authoritative.

- [ ] **Step 5: Check an animated export**

Set the format to **Animated GIF** with Ken Burns on, apply, and confirm the overlay is present and stationary across frames. It should be, since the animated path calls `ComposeDocumentFrame` per frame.

- [ ] **Step 6: Check a saved template round trip**

Save the design as a template, reload the page, load the template, and confirm the overlay controls and canvas come back identical.

- [ ] **Step 7: Commit any fixes**

```bash
git add -A
git commit -m "fix: client/server parity for the gradient overlay"
```

If no fixes were needed, skip the commit and note that parity was verified.

---

### Task 9: Release as v3.4.0.0

**Files:**
- Modify: `CustomCoverArt.csproj:16`
- Modify: `CHANGELOG.md`
- Modify: `README.md`

- [ ] **Step 1: Bump the version**

In `CustomCoverArt.csproj` line 16, change `<Version>3.3.0.0</Version>` to `<Version>3.4.0.0</Version>`.

- [ ] **Step 2: Add the changelog entry**

In `CHANGELOG.md`, directly under the header paragraph and above `## 3.3.0.0`, add. Match the existing entries' voice: user-facing, one paragraph, bold for the nouns that matter, and the reassurance sentence at the end.

```markdown
## 3.4.0.0
Adds an **overlay gradient** to the Background step — a colour that fades in over your background and sits under your text. It is the missing piece behind the look where a poster **resolves into a solid band of colour** at the bottom, with the title on top of it and readable no matter what the poster shows underneath. Each colour stop has its own **opacity**, so you can go from fully transparent to fully solid, use **two colours** for a duotone fade, or wash the whole cover. Four **presets** — Bottom fade, Top fade, Full wash and Duotone — get you there in one click and keep the colours you have already picked, and the **Auto palette** swatches now recolour the selected overlay stop, so you can tint a cover with a colour taken straight out of its own artwork. The overlay works with every background type — image, poster collage, gradient or solid — and with animated covers. Existing designs and saved templates are unaffected and render exactly as before.
```

- [ ] **Step 3: Document the feature in the README**

Two edits.

First, add a row to the Features table (`README.md:25-53`), directly after the **Gradients** row at line 33:

```markdown
| 🌗 | **Overlay gradient** | Fade a colour in over any background, under your text — each stop with its own opacity, plus one-click presets |
```

Second, add to the **Effects and colours** section (`README.md:192`), after the *Jellyfin style* paragraph at line 199-201 and before the *Auto palette* paragraph:

```markdown
**Overlay gradient** (step 2, under Dimming) fades a colour in over the background and under your
layers — the look where a poster resolves into a solid band of colour with the title sitting on top
of it. Every colour stop carries its own **opacity**, so a fade can run from fully transparent to
fully solid, use two colours for a duotone, or wash the whole cover evenly. Four presets — **Bottom
fade**, **Top fade**, **Full wash** and **Duotone** — set it up in one click and keep the colours
you have already chosen; the angle is under **Advanced**. It works over every background type,
including poster collages and animated covers.
```

Then update the *Auto palette* paragraph at lines 203-205, whose stated behaviour is now out of date. Replace "Click one to recolour the selected text layer, or the colour wash if no text layer is selected." with:

```markdown
Click one to recolour the selected text layer — or, with no text layer selected, the selected
overlay-gradient stop, falling back to the colour wash.
```

- [ ] **Step 4: Run the full suite one last time**

Run: `dotnet test tests/CustomCoverArt.Tests/`
Expected: PASS, all tests.

- [ ] **Step 5: Commit**

```bash
git add CustomCoverArt.csproj CHANGELOG.md README.md
git commit -m "chore: release v3.4.0.0 — background gradient overlay"
```

- [ ] **Step 6: Push and open the PR**

```bash
git push -u origin feat/gradient-overlay
```

Open a PR against `main` titled "Background gradient overlay (v3.4.0.0)", linking the spec at `docs/superpowers/specs/2026-08-06-gradient-overlay-design.md`.

---

## Notes for the implementer

**The one risk that matters.** ImageSharp's `Fill` ignores brush alpha on alpha-less pixel formats. This bit the project before, blacking out every dimmed background — see the comment at `DocumentRenderer.cs:204-209`. The overlay avoids it by rendering into its own explicitly-`Rgba32` buffer. Task 3 Step 6 tells you exactly what to do if the assumption turns out wrong. Do not "fix" a failure there by loosening the assertion.

**Why the overlay is not inside `ApplyBackgroundLayer`.** That method runs only on the image path. Putting the overlay there would silently do nothing for gradient, solid and collage backgrounds — exactly the asymmetry `Dim` already has, documented at `configPage.html:1500-1505`. Task 3's `ApplyGradientOverlay_AppliesToEverySource` is the guard.

**The `.ccaStopPos` selector change in Task 5 is load-bearing.** Overlay rows have two range inputs. The old `input[type="range"]` lookup would read whichever came first, silently corrupting stop positions. That change touches the existing background-gradient path, which is why Task 5 Step 5 checks it by hand.
