# Phase 1 — Canvas Engine + Document Model + Server Parity — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the flat `CoverArtSettings` render model with a layered `CoverDocument`, refactor the server renderer to consume it (honoring a background pan/zoom transform and normalized coordinates), and replace the server-`<img>` preview with a live HTML5 Canvas editor — with the existing single-title design migrating losslessly and rendering pixel-comparably to today.

**Architecture:** One JSON `CoverDocument` is the single source of truth. The **client** (HTML5 Canvas in `configPage.html`) renders it live for editing; the **server** (`CoverArtService` via ImageSharp) renders the *same* document for the applied cover and is authoritative. The legacy `CoverArtSettings` entry point is kept as a thin shim that migrates to a `CoverDocument`, so every existing path (Apply, BatchApply, templates) and every existing test keeps working.

**Tech Stack:** .NET 9, SixLabors.ImageSharp 3.1.12 + ImageSharp.Drawing 2.1.7, xUnit + NSubstitute, vanilla JS (no build step) in an embedded HTML page.

## Global Constraints

- Plugin version lives in `CustomCoverArt.csproj` `<Version>`; bump to `3.0.0.0` for this phase (merging to main auto-releases).
- Target Jellyfin ABI `10.11.0.0`; Jellyfin.* package refs use `ExcludeAssets=runtime` (server provides them) — never change that.
- All controller endpoints keep `[Authorize(Policy = "RequiresElevation")]` and go through the existing `RateLimited(...)` helper.
- Client-supplied image/font paths are honored ONLY if `PluginPaths.IsInsideBase(...)` — never relax this. Uploads reuse the existing `/upload` + `ValidateFileAsync` path.
- Existing clamps/whitelists stay: output format whitelist (`gif` else `png`), `ValidateCoverArtDimensions` (100–2048), decompression-bomb guard (`Image.Identify` before decode, `8192*8192` cap), text-size/outline/blur/dim clamps.
- Coordinate contract (used by BOTH renderers):
  - The client canvas backing store is set to `Canvas.Width × Canvas.Height` (export pixels) and displayed scaled via CSS, so all pixel values match the server exactly.
  - Layer `X`,`Y` = normalized center in `[0,1]` of canvas width/height.
  - Text `Size` = fraction of canvas height in `[0,1]`; pixel size = `Size * Canvas.Height`.
  - Image layer `Width`,`Height` = fraction of canvas width/height in `[0,1]`.
  - Shadow blur/offset and outline width are absolute export pixels.
  - Background `Transform.OffsetX/OffsetY` = normalized pan in `[-1,1]`; `Scale` = multiplier `>= 1`.
- en + nl localization kept in sync in the config page's inline `I18N` and (where applicable) `Resources/en.json`/`Resources/nl.json`.
- New C# files stay small and single-responsibility; do NOT balloon `CoverArtService.cs`.

---

## File Structure

- Create `Models/CoverDocument.cs` — `CoverDocument`, `CanvasSettings`, `BackgroundLayer`, `BackgroundTransform`, `EffectSettings` (empty shell for Phase 3), `CoverLayer`, `TextShadowSettings`, `TextOutlineSettings`. (Reuses existing `GradientSettings`, `CollageSettings`, `AnimationSettings`, `FontWeight`, `TextAlign` from `Models/CoverArtModels.cs`.)
- Create `Services/DocumentMigration.cs` — `static CoverDocument FromSettings(CoverArtSettings s)`. Pure, no I/O.
- Create `Services/DocumentRenderer.cs` — the document-native compositor extracted from `CoverArtService`: `ComposeDocumentFrame(Image<Rgba32> canvas, Image? background, CoverDocument doc)`, `RenderTextLayer(...)`, `ApplyBackgroundLayer(...)`. Keeps `CoverArtService` an orchestrator.
- Modify `Services/IServices.cs` — add `Task<string> GenerateFromDocumentAsync(CoverDocument document)` to `ICoverArtService`.
- Modify `Services/CoverArtService.cs` — add `GenerateFromDocumentAsync`; reimplement `GenerateCoverArtAsync(CoverArtSettings)` as `GenerateFromDocumentAsync(DocumentMigration.FromSettings(settings))`; move compositing into `DocumentRenderer`.
- Modify `Controllers/CustomCoverArtController.cs` — add `POST document/preview` and `POST document/apply` accepting `CoverDocument`; add `Document` to saved templates.
- Modify `Models/CoverArtModels.cs` — add `CoverDocument? Document` to `SavedTemplate`; add `ApplyDocumentRequest`.
- Modify `Configuration/configPage.html` — new canvas engine module (document state, render loop, background drag/zoom/pan), replacing the `<img>` preview; keep all existing cards wired to mutate the document.
- Modify `CustomCoverArt.csproj` — bump `<Version>` to `3.0.0.0`.
- Tests in `tests/CustomCoverArt.Tests/`: `DocumentMigrationTests.cs`, `DocumentRenderTests.cs`, `BackgroundTransformTests.cs`, plus keep all existing tests green.

---

## Task 1: CoverDocument model

**Files:**
- Create: `Models/CoverDocument.cs`
- Test: `tests/CustomCoverArt.Tests/CoverDocumentTests.cs`

**Interfaces:**
- Produces: `CustomCoverArt.Models.CoverDocument` with `int Version`, `CanvasSettings Canvas`, `BackgroundLayer Background`, `EffectSettings Effects`, `List<CoverLayer> Layers`. `CoverLayer` has `string Id, Type, string Content, Color, ImagePath, FontPath; bool Visible; float X,Y,Width,Height,Rotation,Opacity,Size; FontWeight Weight; TextAlign Align; TextShadowSettings Shadow; TextOutlineSettings Outline`. `BackgroundLayer` has `string Source, ImagePath, Fit, DimColor; float Blur, Dim; BackgroundTransform Transform; GradientSettings? Gradient; CollageSettings? Collage; AnimationSettings? Animation`. `BackgroundTransform` has `float OffsetX, OffsetY, Scale`.

- [ ] **Step 1: Write the failing test**

```csharp
using CustomCoverArt.Models;
using Xunit;

namespace CustomCoverArt.Tests;

public class CoverDocumentTests
{
    [Fact]
    public void CoverDocument_HasSafeDefaults()
    {
        var d = new CoverDocument();
        Assert.Equal(2, d.Version);
        Assert.Equal(1400, d.Canvas.Width);
        Assert.Equal(1400, d.Canvas.Height);
        Assert.Equal("upload", d.Background.Source);
        Assert.Equal(1f, d.Background.Transform.Scale);
        Assert.Empty(d.Layers);
        Assert.NotNull(d.Effects);
    }

    [Fact]
    public void CoverLayer_TextDefaults()
    {
        var l = new CoverLayer();
        Assert.Equal("text", l.Type);
        Assert.True(l.Visible);
        Assert.Equal(0.5f, l.X);
        Assert.Equal(0.5f, l.Y);
        Assert.Equal(1f, l.Opacity);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run (from repo root): `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal`
Expected: FAIL — `CoverDocument` / `CoverLayer` do not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
using System.Collections.Generic;

namespace CustomCoverArt.Models;

/// <summary>The layered cover design consumed by both the client canvas and the server renderer.</summary>
public class CoverDocument
{
    public int Version { get; set; } = 2;
    public CanvasSettings Canvas { get; set; } = new();
    public BackgroundLayer Background { get; set; } = new();
    public EffectSettings Effects { get; set; } = new();
    public List<CoverLayer> Layers { get; set; } = new();
}

public class CanvasSettings
{
    public int Width { get; set; } = 1400;
    public int Height { get; set; } = 1400;
    public string Format { get; set; } = "auto";           // auto|png|gif
    public string DimensionPreset { get; set; } = "cover";
}

/// <summary>Background source, effects on it, and the pan/zoom transform.</summary>
public class BackgroundLayer
{
    public string Source { get; set; } = "upload";          // upload|poster|collage|none
    public string ImagePath { get; set; } = string.Empty;
    public string Fit { get; set; } = "cover";              // cover|contain|stretch
    public BackgroundTransform Transform { get; set; } = new();
    public float Blur { get; set; }
    public float Dim { get; set; } = 0.25f;
    public string DimColor { get; set; } = "#000000";
    public GradientSettings? Gradient { get; set; }
    public CollageSettings? Collage { get; set; }
    public AnimationSettings? Animation { get; set; }
}

/// <summary>User pan/zoom applied to the fitted background. Identity = OffsetX/Y 0, Scale 1.</summary>
public class BackgroundTransform
{
    public float OffsetX { get; set; }                      // normalized pan, -1..1
    public float OffsetY { get; set; }
    public float Scale { get; set; } = 1f;                  // >= 1
}

/// <summary>Composition effects. Empty in Phase 1; populated in Phase 3.</summary>
public class EffectSettings
{
    public string? Preset { get; set; }
}

/// <summary>One text or image layer. A single flat type (Type discriminator) keeps System.Text.Json simple.</summary>
public class CoverLayer
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = "text";              // text|image
    public bool Visible { get; set; } = true;
    public float X { get; set; } = 0.5f;                    // normalized center
    public float Y { get; set; } = 0.5f;
    public float Width { get; set; }                        // normalized (image layers)
    public float Height { get; set; }
    public float Rotation { get; set; }                     // degrees
    public float Opacity { get; set; } = 1f;

    // text layer
    public string Content { get; set; } = string.Empty;
    public float Size { get; set; } = 0.086f;               // fraction of canvas height (~120/1400)
    public FontWeight Weight { get; set; } = FontWeight.Normal;
    public string Color { get; set; } = "#ffffff";
    public TextAlign Align { get; set; } = TextAlign.Center;
    public string FontPath { get; set; } = string.Empty;
    public TextShadowSettings Shadow { get; set; } = new();
    public TextOutlineSettings Outline { get; set; } = new();

    // image layer
    public string ImagePath { get; set; } = string.Empty;
}

public class TextShadowSettings
{
    public bool Enabled { get; set; }
    public string Color { get; set; } = "#000000";
    public int Blur { get; set; } = 4;
    public int OffsetX { get; set; } = 2;
    public int OffsetY { get; set; } = 2;
}

public class TextOutlineSettings
{
    public bool Enabled { get; set; }
    public string Color { get; set; } = "#000000";
    public int Width { get; set; } = 2;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal`
Expected: PASS (all tests, existing + new).

- [ ] **Step 5: Commit**

```bash
git add Models/CoverDocument.cs tests/CustomCoverArt.Tests/CoverDocumentTests.cs
git commit -m "feat(phase1): add CoverDocument layered model"
```

---

## Task 2: Migration from CoverArtSettings → CoverDocument

**Files:**
- Create: `Services/DocumentMigration.cs`
- Test: `tests/CustomCoverArt.Tests/DocumentMigrationTests.cs`

**Interfaces:**
- Consumes: `CoverArtSettings` (existing), `CoverDocument` (Task 1).
- Produces: `static CoverDocument DocumentMigration.FromSettings(CoverArtSettings s)`. Maps: canvas dims/format/preset; background source/path/fit/blur/dim/dimcolor/gradient/collage/animation with identity transform; exactly ONE text layer from Title/TextSize/Weight/Color/Align + shadow/outline + font; text `Size = TextSize / ExportHeight`; layer `X,Y` from `TextAlign`+`TextBaseline`+`TextPadding`.

- [ ] **Step 1: Write the failing test**

```csharp
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using Xunit;

namespace CustomCoverArt.Tests;

public class DocumentMigrationTests
{
    [Fact]
    public void FromSettings_MapsCanvasAndBackground()
    {
        var s = new CoverArtSettings
        {
            Title = "Movies", ExportWidth = 1280, ExportHeight = 720,
            OutputFormat = "png", DimensionPreset = "landscape",
            BackgroundDim = 0.4f, BackgroundBlur = 3f, DimColor = "#101010",
            BackgroundFit = "contain", BackgroundImagePath = "" 
        };

        var d = DocumentMigration.FromSettings(s);

        Assert.Equal(1280, d.Canvas.Width);
        Assert.Equal(720, d.Canvas.Height);
        Assert.Equal("landscape", d.Canvas.DimensionPreset);
        Assert.Equal(0.4f, d.Background.Dim);
        Assert.Equal("contain", d.Background.Fit);
        Assert.Equal(1f, d.Background.Transform.Scale);
    }

    [Fact]
    public void FromSettings_CreatesExactlyOneTextLayer()
    {
        var s = new CoverArtSettings { Title = "Movies", TextSize = 120, ExportHeight = 1400, TextColor = "#ffcc00" };
        var d = DocumentMigration.FromSettings(s);

        Assert.Single(d.Layers);
        var layer = d.Layers[0];
        Assert.Equal("text", layer.Type);
        Assert.Equal("Movies", layer.Content);
        Assert.Equal("#ffcc00", layer.Color);
        // 120px on a 1400px-tall canvas => ~0.0857 fraction.
        Assert.InRange(layer.Size, 0.084f, 0.087f);
    }

    [Fact]
    public void FromSettings_LeftAlignMapsToLeftAnchor()
    {
        var s = new CoverArtSettings { Title = "X", TextAlign = TextAlign.Left, TextPadding = 0.05f };
        var d = DocumentMigration.FromSettings(s);
        Assert.Equal(TextAlign.Left, d.Layers[0].Align);
        Assert.InRange(d.Layers[0].X, 0.04f, 0.06f); // near the left padding
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal`
Expected: FAIL — `DocumentMigration` does not exist.

- [ ] **Step 3: Write minimal implementation**

```csharp
using CustomCoverArt.Models;

namespace CustomCoverArt.Services;

/// <summary>Converts a legacy flat CoverArtSettings into a one-text-layer CoverDocument.
/// Pure (no I/O) so every legacy path can route through the document renderer.</summary>
public static class DocumentMigration
{
    public static CoverDocument FromSettings(CoverArtSettings s)
    {
        var doc = new CoverDocument
        {
            Canvas = new CanvasSettings
            {
                Width = s.ExportWidth,
                Height = s.ExportHeight,
                Format = string.IsNullOrEmpty(s.OutputFormat) ? "auto" : s.OutputFormat,
                DimensionPreset = s.DimensionPreset,
            },
            Background = new BackgroundLayer
            {
                Source = s.BackgroundSource,
                ImagePath = s.BackgroundImagePath,
                Fit = s.BackgroundFit,
                Blur = s.BackgroundBlur,
                Dim = s.BackgroundDim,
                DimColor = s.DimColor,
                Gradient = s.BackgroundGradient,
                Collage = s.Collage,
                Animation = s.Animation,
                Transform = new BackgroundTransform(), // identity
            },
        };

        var height = s.ExportHeight <= 0 ? 1400 : s.ExportHeight;
        var (x, y) = AnchorFor(s.TextAlign, s.TextBaseline, s.TextPadding);
        doc.Layers.Add(new CoverLayer
        {
            Id = "title",
            Type = "text",
            Content = s.Title,
            Size = s.TextSize / (float)height,
            Weight = s.TextWeight,
            Color = s.TextColor,
            Align = s.TextAlign,
            FontPath = s.CustomFontPath,
            X = x,
            Y = y,
            Shadow = new TextShadowSettings
            {
                Enabled = s.TextShadow, Color = s.TextShadowColor, Blur = s.TextShadowBlur,
                OffsetX = s.TextShadowOffsetX, OffsetY = s.TextShadowOffsetY,
            },
            Outline = new TextOutlineSettings
            {
                Enabled = s.TextOutline, Color = s.TextOutlineColor, Width = s.TextOutlineWidth,
            },
        });

        return doc;
    }

    private static (float X, float Y) AnchorFor(TextAlign align, TextBaseline baseline, float padding)
    {
        var x = align switch
        {
            TextAlign.Left => padding,
            TextAlign.Right => 1f - padding,
            _ => 0.5f,
        };
        var y = baseline switch
        {
            TextBaseline.Top => padding,
            TextBaseline.Bottom => 1f - padding,
            _ => 0.5f,
        };
        return (x, y);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Services/DocumentMigration.cs tests/CustomCoverArt.Tests/DocumentMigrationTests.cs
git commit -m "feat(phase1): migrate CoverArtSettings to CoverDocument"
```

---

## Task 3: Extract the document renderer (background + text layer)

**Files:**
- Create: `Services/DocumentRenderer.cs`
- Modify: `Services/CoverArtService.cs` (move compositing helpers out; keep orchestration)
- Test: `tests/CustomCoverArt.Tests/DocumentRenderTests.cs`

**Interfaces:**
- Consumes: `CoverDocument` (Task 1). Reuses existing private logic from `CoverArtService` (`ApplyBackground`, `ApplyTextOverlay`, `CreateFont`, `SafeColor`, gradient helpers) — MOVE them into `DocumentRenderer` as internal static methods so `CoverArtService` and future phases share them.
- Produces: `static void DocumentRenderer.ComposeDocumentFrame(Image<Rgba32> canvas, Image? background, CoverDocument doc)` — draws background layer (with transform) then each visible layer in array order. `static void DocumentRenderer.RenderTextLayer(Image<Rgba32> canvas, CoverLayer layer, CoverDocument doc)`. `static Color DocumentRenderer.SafeColor(string?, Color)`.

> **Note for the implementer:** This is a *refactor*, so behavior must not change for the migrated single-title case. The existing `ComposeFrame`/`ApplyBackground`/`ApplyTextOverlay`/`ApplyTextOverlayWithFallback`/`CalculateTextPosition`/`CreateFont`/gradient + bundled-font helpers in `CoverArtService.cs` (lines ~305–751) are the source of truth — move them verbatim into `DocumentRenderer`, then adapt `RenderTextLayer` to read a `CoverLayer` instead of `CoverArtSettings`. Convert normalized layer coords to pixels: `px = layer.X * canvas.Width`, `py = layer.Y * canvas.Height`, `fontPx = layer.Size * canvas.Height`.
>
> **Do not miss the animated call site:** `CoverArtService.GenerateAnimatedAsync` (line ~390) calls `ComposeFrame(frameCanvas, frameBg, ...)` once per GIF frame. After the extraction it must route through `DocumentRenderer.ComposeDocumentFrame(frameCanvas, frameBg, document)` (Task 4 threads the document into the animated path) or the animated-GIF tests break.

- [ ] **Step 1: Write the failing test**

```csharp
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CustomCoverArt.Tests;

public class DocumentRenderTests
{
    [Fact]
    public void ComposeDocumentFrame_GradientBackground_DrawsTextPixels()
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 200, Height = 200 } };
        doc.Background.Gradient = new GradientSettings
        {
            IsEnabled = true,
            Stops = new() { new GradientStop { Color = "#000000", Position = 0 }, new GradientStop { Color = "#000000", Position = 1 } }
        };
        doc.Layers.Add(new CoverLayer { Type = "text", Content = "HELLO", Color = "#ffffff", Size = 0.2f, X = 0.5f, Y = 0.5f });

        using var canvas = new Image<Rgba32>(200, 200);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        // At least one near-white pixel from the text exists over the black gradient.
        var found = false;
        for (int y = 0; y < 200 && !found; y++)
            for (int x = 0; x < 200; x++)
            {
                var p = canvas[x, y];
                if (p.R > 200 && p.G > 200 && p.B > 200) { found = true; break; }
            }
        Assert.True(found, "Expected white text pixels over the black background.");
    }

    [Fact]
    public void ComposeDocumentFrame_HiddenLayer_IsNotDrawn()
    {
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 100, Height = 100 } };
        doc.Background.DimColor = "#000000";
        doc.Layers.Add(new CoverLayer { Type = "text", Content = "X", Color = "#ffffff", Size = 0.5f, Visible = false });

        using var canvas = new Image<Rgba32>(100, 100);
        DocumentRenderer.ComposeDocumentFrame(canvas, null, doc);

        for (int y = 0; y < 100; y++)
            for (int x = 0; x < 100; x++)
                Assert.True(canvas[x, y].R < 40, "Hidden layer must not render.");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal`
Expected: FAIL — `DocumentRenderer` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `Services/DocumentRenderer.cs`. Move the background/gradient/font/text helpers from `CoverArtService.cs` into it as `internal static`, then add the document composition entry points:

```csharp
using CustomCoverArt.Models;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CustomCoverArt.Services;

/// <summary>Compositor: renders a CoverDocument onto an ImageSharp canvas. Shared by
/// the single-image and animated paths; extended by later phases (layers, effects).</summary>
public static class DocumentRenderer
{
    public static void ComposeDocumentFrame(Image<Rgba32> canvas, Image? background, CoverDocument doc)
    {
        if (background is not null)
        {
            ApplyBackgroundLayer(canvas, background, doc.Background);
        }
        else
        {
            CreateGradientBackground(canvas, doc.Background);
        }

        foreach (var layer in doc.Layers)
        {
            if (!layer.Visible) { continue; }
            if (layer.Type == "text") { RenderTextLayerWithFallback(canvas, layer, doc); }
            // image layers: added in Phase 2
        }
    }

    // ... MOVED verbatim from CoverArtService (adapted to take BackgroundLayer/CoverLayer):
    //   ApplyBackgroundLayer(canvas, bg, BackgroundLayer)  <- was ApplyBackground(image, bg, settings)
    //     honoring bg.Fit, bg.Blur, bg.Dim, bg.DimColor, AND bg.Transform (see Task 5)
    //   CreateGradientBackground(canvas, BackgroundLayer)  <- reads bg.Gradient / bg.DimColor
    //   ApplyGradientBackground / BuildColorStops          <- unchanged (take GradientSettings)
    //   RenderTextLayer(canvas, CoverLayer, doc)           <- was ApplyTextOverlay(image, settings)
    //     using px = layer.X*W, py = layer.Y*H, font size = layer.Size*H, layer.Weight/Color/Align,
    //     layer.Shadow.*, layer.Outline.* ; clamp outline width to [0,10]
    //   RenderTextLayerWithFallback                         <- was ApplyTextOverlayWithFallback
    //   CreateFont(CoverLayer)                              <- was CreateFont(settings); font path sandbox
    //   Bundled Noto font collection + SafeColor           <- unchanged
    internal static Color SafeColor(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) { return fallback; }
        try { return Color.ParseHex(hex); } catch { return fallback; }
    }
}
```

Then in `CoverArtService.cs`, delete the moved private methods and call `DocumentRenderer.ComposeDocumentFrame(...)` from `ComposeFrame`. Keep `CoverArtService.ComposeFrame(Image<Rgba32>, Image?, CoverArtSettings)` temporarily delegating via `DocumentMigration.FromSettings` (removed in Task 4).

- [ ] **Step 4: Run test to verify it passes**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal`
Expected: PASS — new render tests pass AND all existing render/animation/dim tests still pass (parity).

- [ ] **Step 5: Commit**

```bash
git add Services/DocumentRenderer.cs Services/CoverArtService.cs tests/CustomCoverArt.Tests/DocumentRenderTests.cs
git commit -m "refactor(phase1): extract DocumentRenderer, render from CoverDocument"
```

---

## Task 4: Route CoverArtService through the document renderer

**Files:**
- Modify: `Services/IServices.cs` (add `GenerateFromDocumentAsync`)
- Modify: `Services/CoverArtService.cs`
- Test: `tests/CustomCoverArt.Tests/DocumentRenderTests.cs` (add end-to-end)

**Interfaces:**
- Produces: `ICoverArtService.GenerateFromDocumentAsync(CoverDocument document) : Task<string>` — validates canvas dims + sandbox paths, loads background (upload/collage as today, keyed off `doc.Background`), composes via `DocumentRenderer`, saves PNG/GIF, returns path.
- `CoverArtService.GenerateCoverArtAsync(CoverArtSettings s)` becomes `=> GenerateFromDocumentAsync(DocumentMigration.FromSettings(s))`.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async System.Threading.Tasks.Task GenerateFromDocumentAsync_ProducesPng()
{
    var svc = AnimationTestHost.NewCoverArtService();
    var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 200, Height = 200, Format = "png" } };
    doc.Background.DimColor = "#222222";
    doc.Layers.Add(new CoverLayer { Type = "text", Content = "Hi", Color = "#ffffff", Size = 0.2f });

    var path = await svc.GenerateFromDocumentAsync(doc);

    Assert.True(System.IO.File.Exists(path));
    Assert.EndsWith(".png", path);
    using var img = SixLabors.ImageSharp.Image.Load(path);
    Assert.Equal(200, img.Width);
    try { System.IO.File.Delete(path); } catch { }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal`
Expected: FAIL — `GenerateFromDocumentAsync` not defined on the interface.

- [ ] **Step 3: Write minimal implementation**

Add to `IServices.cs` in `ICoverArtService`:

```csharp
    /// <summary>Renders a CoverDocument to a file and returns its path.</summary>
    Task<string> GenerateFromDocumentAsync(CoverDocument document);
```

In `CoverArtService.cs`, refactor: rename the body of the current `GenerateCoverArtAsync(CoverArtSettings)` into `GenerateFromDocumentAsync(CoverDocument document)`, replacing every `settings.*` read with `document.Canvas.*` / `document.Background.*` (validation clamps now apply to `document.Background.Blur/Dim` and each text layer's outline/size in `DocumentRenderer`). Load background from `document.Background` (collage uses `document.Background.Collage`, upload uses `document.Background.ImagePath`). Animated path passes the `document`. Then:

```csharp
public Task<string> GenerateCoverArtAsync(CoverArtSettings settings)
    => GenerateFromDocumentAsync(DocumentMigration.FromSettings(settings));
```

Keep the output-format whitelist (`gif` else `png`) reading `document.Canvas.Format`. This drops the `DetermineOptimalFormatAsync("auto")` call, but that method returns `"png"` unconditionally today, so mapping `auto`/anything-not-`gif` → `png` is exactly equivalent — no behavior change (the animated path still always writes `.gif`).

- [ ] **Step 4: Run test to verify it passes**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal`
Expected: PASS — new test + ALL existing tests (they call `GenerateCoverArtAsync`, now routed through the document renderer).

- [ ] **Step 5: Commit**

```bash
git add Services/IServices.cs Services/CoverArtService.cs tests/CustomCoverArt.Tests/DocumentRenderTests.cs
git commit -m "feat(phase1): ICoverArtService.GenerateFromDocumentAsync; legacy path delegates"
```

---

## Task 5: Honor the background pan/zoom transform in the server render

**Files:**
- Modify: `Services/DocumentRenderer.cs` (`ApplyBackgroundLayer`)
- Test: `tests/CustomCoverArt.Tests/BackgroundTransformTests.cs`

**Interfaces:**
- Consumes: `BackgroundLayer.Transform` (`OffsetX`,`OffsetY` in `[-1,1]`, `Scale >= 1`).
- Produces: a static, testable crop helper `static Rectangle DocumentRenderer.TransformedSourceRect(int fittedW, int fittedH, int canvasW, int canvasH, BackgroundTransform t)` — computes the sub-rectangle of the fitted background to draw, so client and server frame the image identically.

> **Contract:** After the background is fitted to `cover` (fills canvas), `Scale` zooms in on it (crop a `1/Scale` region) and `OffsetX/Y` pan the crop within the available slack, clamped so the crop never leaves the image. `Scale = 1, Offset = 0` reproduces today's centered cover exactly (parity).

- [ ] **Step 1: Write the failing test**

```csharp
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using SixLabors.ImageSharp;
using Xunit;

namespace CustomCoverArt.Tests;

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
        var r = DocumentRenderer.TransformedSourceRect(1000, 1000, 1000, 1000, new BackgroundTransform { Scale = 2f, OffsetX = 5f });
        Assert.Equal(500, r.X + r.Width <= 1000 ? r.X : -1); // stays in-bounds
        Assert.True(r.X + r.Width <= 1000 && r.X >= 0);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal`
Expected: FAIL — `TransformedSourceRect` not defined.

- [ ] **Step 3: Write minimal implementation**

```csharp
/// <summary>Sub-rectangle of the fitted background to draw, given pan/zoom. Clamped in-bounds.</summary>
public static Rectangle TransformedSourceRect(int fittedW, int fittedH, int canvasW, int canvasH, BackgroundTransform t)
{
    var scale = System.Math.Max(1f, t.Scale);
    var w = (int)System.Math.Round(fittedW / scale);
    var h = (int)System.Math.Round(fittedH / scale);
    var slackX = fittedW - w;
    var slackY = fittedH - h;
    // Offset -1..1 maps across the available slack; 0 = centered.
    var x = (int)System.Math.Round(slackX / 2f + System.Math.Clamp(t.OffsetX, -1f, 1f) * slackX / 2f);
    var y = (int)System.Math.Round(slackY / 2f + System.Math.Clamp(t.OffsetY, -1f, 1f) * slackY / 2f);
    x = System.Math.Clamp(x, 0, slackX);
    y = System.Math.Clamp(y, 0, slackY);
    return new Rectangle(x, y, w, h);
}
```

In `ApplyBackgroundLayer`, after fitting the background to canvas size (the existing `cover`/`contain`/`stretch` logic produces a `fitted` image at canvas size), apply the transform: `var rect = TransformedSourceRect(fitted.Width, fitted.Height, canvas.Width, canvas.Height, bg.Transform); fitted.Mutate(x => x.Crop(rect).Resize(canvas.Width, canvas.Height));` — only when `bg.Transform` is non-identity (skip the crop/resize when `Scale==1 && OffsetX==0 && OffsetY==0` to preserve exact parity and avoid a needless resample).

- [ ] **Step 4: Run test to verify it passes**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Services/DocumentRenderer.cs tests/CustomCoverArt.Tests/BackgroundTransformTests.cs
git commit -m "feat(phase1): honor background pan/zoom transform in render"
```

---

## Task 6: Document preview + apply endpoints; template document field

**Files:**
- Modify: `Controllers/CustomCoverArtController.cs`
- Modify: `Models/CoverArtModels.cs` (add `ApplyDocumentRequest`; add `CoverDocument? Document` to `SavedTemplate`)
- Test: `tests/CustomCoverArt.Tests/ControllerDocumentTests.cs`

**Interfaces:**
- Produces:
  - `POST CustomCoverArt/document/preview` `[FromBody] CoverDocument` → streams `image/png|gif` (same rate limit as `preview`: 240/min).
  - `POST CustomCoverArt/document/apply` `[FromBody] ApplyDocumentRequest { string LibraryId; CoverDocument Document }` → `ApiResponse<bool>` (same 30/min limit + guid check as `apply`; reuses `ApplyInternal`-equivalent that takes a document).
  - `ApplyDocumentRequest` model; `SavedTemplate.Document` (nullable).

> **Back-compat:** `SaveTemplate` now stores `Document` when present (title stripped: set `Document.Layers[?].Content` for the title layer to empty, mirroring the existing `NormalizeTemplate`). `GetTemplates` returns templates as stored; the client migrates any `Document == null` template from `Settings` client-side (Task 9). BatchApply/legacy apply are unchanged this phase.

- [ ] **Step 1: Write the failing test**

```csharp
using CustomCoverArt.Models;
using Xunit;

namespace CustomCoverArt.Tests;

public class ControllerDocumentTests
{
    [Fact]
    public void ApplyDocumentRequest_Defaults()
    {
        var r = new ApplyDocumentRequest();
        Assert.Equal(string.Empty, r.LibraryId);
        Assert.NotNull(r.Document);
    }

    [Fact]
    public void SavedTemplate_CanHoldDocument()
    {
        var t = new SavedTemplate { Name = "N", Document = new CoverDocument() };
        Assert.NotNull(t.Document);
        Assert.Equal(2, t.Document!.Version);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal`
Expected: FAIL — `ApplyDocumentRequest` / `SavedTemplate.Document` missing.

- [ ] **Step 3: Write minimal implementation**

In `Models/CoverArtModels.cs`:

```csharp
public class ApplyDocumentRequest
{
    public string LibraryId { get; set; } = string.Empty;
    public CoverDocument Document { get; set; } = new();
}
```

Add to `SavedTemplate`: `public CoverDocument? Document { get; set; }`.

In `CustomCoverArtController.cs` add:

```csharp
[HttpPost("document/preview")]
public async Task<IActionResult> GeneratePreviewDocument([FromBody] CoverDocument document)
{
    if (RateLimited("preview", maxRequests: 240, TimeSpan.FromMinutes(1)))
        return StatusCode(429, new { error = "Too many requests" });
    try
    {
        var path = await _coverArtService.GenerateFromDocumentAsync(document).ConfigureAwait(false);
        var bytes = await System.IO.File.ReadAllBytesAsync(path).ConfigureAwait(false);
        var contentType = document.Canvas.Format?.ToLowerInvariant() == "gif" ? "image/gif" : "image/png";
        return File(bytes, contentType);
    }
    catch (Exception ex) { _loggingService.LogError("Failed to generate document preview", ex); return BadRequest(new { error = "Failed to generate preview." }); }
}

[HttpPost("document/apply")]
public async Task<ApiResponse<bool>> ApplyDocument([FromBody] ApplyDocumentRequest request)
{
    if (RateLimited("apply", maxRequests: 30, TimeSpan.FromMinutes(1)))
        return Fail<bool>(_localizationService.GetString("errors.too_many_uploads"));
    if (!Guid.TryParse(request.LibraryId, out _)) return Fail<bool>("Invalid library id");
    try
    {
        var path = await _coverArtService.GenerateFromDocumentAsync(request.Document).ConfigureAwait(false);
        var saved = await _coverArtService.SaveCoverArtAsync(request.LibraryId, path).ConfigureAwait(false);
        if (saved is null) return Fail<bool>("Failed to save cover art");
        await _libraryService.BackupCurrentCoverArtAsync(request.LibraryId).ConfigureAwait(false);
        var ok = await _libraryService.UpdateLibraryCoverArtAsync(request.LibraryId, saved).ConfigureAwait(false);
        return ok ? Success(true) : Fail<bool>("Failed to update library cover art");
    }
    catch (Exception ex) { _loggingService.LogError("Failed to apply document to {LibraryId}", ex, request.LibraryId); return Fail<bool>("Failed to apply cover art."); }
}
```

Update `SaveTemplate` to strip the title layer's content when a `Document` is present (add to `NormalizeTemplate`: if `template.Document != null`, set each `Layer.Content` on the layer with `Id == "title"` to empty, and clear `Document.Background.Collage.SourceId`).

- [ ] **Step 4: Run test to verify it passes**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Controllers/CustomCoverArtController.cs Models/CoverArtModels.cs tests/CustomCoverArt.Tests/ControllerDocumentTests.cs
git commit -m "feat(phase1): document preview/apply endpoints; template Document field"
```

---

## Task 7: Client canvas engine — document state + render loop

**Files:**
- Modify: `Configuration/configPage.html` (add a `<canvas id="ccaCanvas">` in the preview card; add the canvas-engine JS)

**Interfaces (JS, inside the page IIFE):**
- Produces:
  - `var doc = defaultDocument()` — the client's working `CoverDocument` (same field names/casing as the C# model; PascalCase to match the serializer, e.g. `Canvas`, `Background`, `Layers`).
  - `function renderDocument()` — draws `doc` onto the canvas 2D context (background fill/gradient/image + transform, then each visible text layer). Called by `scheduleRender` (debounced ~16ms via `requestAnimationFrame`).
  - `function normToPx(n, dim)` / `function pxToNorm(px, dim)` — coordinate helpers.
  - `function selectedLayer()` — returns the layer object with `doc._selectedId`, or null.

> **Design:** The canvas backing store is `doc.Canvas.Width × doc.Canvas.Height`; CSS caps display size (`max-width:100%; max-height:62vh`). Rendering happens client-side for instant feedback. The old server `<img>` preview (`#ccaPreview`, `runPreview`, `schedulePreview`) is REPLACED by canvas rendering; a "Server preview" affordance remains (Task 8) to show the authoritative render on demand.

- [ ] **Step 1: Add the canvas element and default document**

In the preview card (`configPage.html` ~line 281), replace the `<img id="ccaPreview">` with:

```html
<canvas id="ccaCanvas" class="ccaCanvas" width="1400" height="1400" aria-label="Cover art canvas"></canvas>
```

Add CSS mirroring the old `#ccaPreview` rules (`max-width:100%; max-height:62vh; border-radius:6px; box-shadow:...; touch-action:none;`).

Add the default-document factory in the script:

```javascript
function defaultDocument() {
    return {
        Version: 2,
        Canvas: { Width: 1280, Height: 720, Format: 'auto', DimensionPreset: 'landscape' },
        Background: {
            Source: 'upload', ImagePath: '', Fit: 'cover',
            Transform: { OffsetX: 0, OffsetY: 0, Scale: 1 },
            Blur: 0, Dim: 0.25, DimColor: '#000000',
            Gradient: { IsEnabled: true, Type: 0, Angle: 0,
                Stops: [{ Color: '#aa5cc3', Position: 0 }, { Color: '#00a4dc', Position: 1 }],
                CenterX: 0.5, CenterY: 0.5, Radius: 0.5 },
            Collage: null, Animation: null
        },
        Effects: { Preset: null },
        Layers: [{
            Id: 'title', Type: 'text', Visible: true, X: 0.5, Y: 0.5, Width: 0, Height: 0,
            Rotation: 0, Opacity: 1, Content: 'Movies', Size: 120 / 720, Weight: 400,
            Color: '#ffffff', Align: 1, FontPath: '',
            Shadow: { Enabled: false, Color: '#000000', Blur: 4, OffsetX: 2, OffsetY: 2 },
            Outline: { Enabled: false, Color: '#000000', Width: 2 }
        }],
        _selectedId: 'title'
    };
}
var doc = defaultDocument();
```

- [ ] **Step 2: Implement the canvas renderer**

Add:

```javascript
function normToPx(n, dim) { return n * dim; }
var _rafPending = false;
function scheduleRender() {
    if (_rafPending) { return; }
    _rafPending = true;
    requestAnimationFrame(function () { _rafPending = false; renderDocument(); });
}

function renderDocument() {
    var cv = el('ccaCanvas');
    if (cv.width !== doc.Canvas.Width || cv.height !== doc.Canvas.Height) {
        cv.width = doc.Canvas.Width; cv.height = doc.Canvas.Height;
    }
    var ctx = cv.getContext('2d');
    var W = cv.width, H = cv.height;
    ctx.clearRect(0, 0, W, H);

    // Background: image (if loaded) honoring fit + transform, else gradient/fill.
    if (bgImageEl && bgImageEl.complete && bgImageEl.naturalWidth) {
        drawBackgroundImage(ctx, bgImageEl, W, H);
    } else if (doc.Background.Gradient && doc.Background.Gradient.IsEnabled) {
        drawGradient(ctx, doc.Background.Gradient, W, H);
    } else {
        ctx.fillStyle = doc.Background.DimColor || '#000'; ctx.fillRect(0, 0, W, H);
    }
    if (doc.Background.Dim > 0) {
        ctx.save(); ctx.globalAlpha = doc.Background.Dim;
        ctx.fillStyle = doc.Background.DimColor || '#000'; ctx.fillRect(0, 0, W, H); ctx.restore();
    }

    doc.Layers.forEach(function (layer) {
        if (!layer.Visible) { return; }
        if (layer.Type === 'text') { drawTextLayer(ctx, layer, W, H); }
    });

    drawSelectionHandles(ctx, W, H); // Task 8
}

function drawTextLayer(ctx, layer, W, H) {
    var px = normToPx(layer.X, W), py = normToPx(layer.Y, H);
    var fontPx = Math.max(8, layer.Size * H);
    ctx.save();
    ctx.globalAlpha = layer.Opacity;
    ctx.font = layer.Weight + ' ' + fontPx + 'px "Noto Sans", sans-serif';
    ctx.textAlign = layer.Align === 0 ? 'left' : (layer.Align === 2 ? 'right' : 'center');
    ctx.textBaseline = 'middle';
    if (layer.Outline && layer.Outline.Enabled) {
        ctx.lineWidth = layer.Outline.Width * 2; ctx.strokeStyle = layer.Outline.Color;
        ctx.lineJoin = 'round'; ctx.strokeText(layer.Content, px, py);
    }
    if (layer.Shadow && layer.Shadow.Enabled) {
        ctx.shadowColor = layer.Shadow.Color; ctx.shadowBlur = layer.Shadow.Blur;
        ctx.shadowOffsetX = layer.Shadow.OffsetX; ctx.shadowOffsetY = layer.Shadow.OffsetY;
    }
    ctx.fillStyle = layer.Color; ctx.fillText(layer.Content, px, py);
    ctx.restore();
}
```

Add `drawGradient(ctx, g, W, H)` (linear via `createLinearGradient` at `g.Angle`, radial via `createRadialGradient`; stops from `g.Stops`) and `drawBackgroundImage(ctx, img, W, H)` (compute cover/contain/stretch rect, then apply `doc.Background.Transform` by scaling the source rect `1/Scale` and panning across slack — mirror `TransformedSourceRect`). Declare `var bgImageEl = null;`.

> **CRITICAL canvas-source invariant (prevents tainted-canvas `SecurityError`):** `bgImageEl` MUST always be loaded from a **`blob:` URL created with `URL.createObjectURL()`** of a client-held `File` (the upload `<input>`) or a fetched `Blob` (the poster browser already `fetch()`es the poster into a Blob before uploading — keep that Blob). A `blob:` object-URL adopts the document origin, so the canvas is never tainted and Phase 3's `getImageData` (palette) and client grain work. NEVER set `bgImageEl.src = ApiClient.getImageUrl(...)` or the server path returned by `/upload` (that path is a server filesystem path the browser can't even load, and a cross-origin image URL taints the canvas under split-origin reverse-proxy deployments). Task 8 wires the upload/poster Blob to `bgImageEl` via `createObjectURL`. A template-loaded background (server path only, no Blob) is not drawn on the canvas — only the "Show server render" reflects it (accept and note in the UI).

- [ ] **Step 3: Load Noto Sans into the canvas via an authenticated font endpoint + the FontFace API**

So canvas text metrics match the server's Noto faces, the client must load the SAME bundled Noto weights. Do NOT base64-inline them into the page (that re-bloats the embedded HTML by ~660 KB, double-ships the two faces already embedded in the DLL, and only covers 2 of 6 weights — undoing the v2.1.0 font-subset size work). Do NOT add an `[AllowAnonymous]` route (breaks the "every endpoint requires elevation" convention). Instead add an **authenticated** endpoint that streams the embedded TTF, and register it with the **FontFace API** (which, unlike CSS `@font-face`, can be fed an `ArrayBuffer` fetched WITH the auth token):

Controller (inherits the class-level `RequiresElevation`):

```csharp
[HttpGet("font/{weight:int}")]
public IActionResult GetFont(int weight)
{
    var face = weight switch
    {
        300 => "NotoSans-Light", 500 => "NotoSans-Medium", 600 => "NotoSans-SemiBold",
        700 => "NotoSans-Bold", 800 => "NotoSans-ExtraBold", _ => "NotoSans-Regular"
    };
    var res = $"CustomCoverArt.Resources.fonts.{face}.ttf";
    var stream = typeof(Plugin).Assembly.GetManifestResourceStream(res);
    if (stream is null) { return NotFound(); }
    return File(stream, "font/ttf");
}
```

Client (in the page init, before the first render):

```javascript
var FONT_WEIGHTS = [300, 400, 500, 600, 700, 800];
function loadCanvasFonts() {
    return Promise.all(FONT_WEIGHTS.map(function (w) {
        return authFetch('CustomCoverArt/font/' + w).then(function (r) { return r.arrayBuffer(); })
            .then(function (buf) {
                var ff = new FontFace('Noto Sans', buf, { weight: String(w) });
                return ff.load().then(function (loaded) { document.fonts.add(loaded); });
            }).catch(function () { /* fall back to sans-serif for this weight; server render is authoritative */ });
    }));
}
// In pageshow init: loadCanvasFonts().then(scheduleRender);
```

If a weight fails to load, canvas text falls back to the browser's sans-serif for that weight only — the applied cover is unaffected because the server render is authoritative. `Resources/fonts/*.ttf` stay embedded (unchanged); nothing is base64-inlined.

- [ ] **Step 4: Manual verification**

Build and load the page (no unit test — this is DOM). Confirm: the canvas shows the purple→blue gradient with centered white "Movies", and resizing the browser scales the canvas without distortion.

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" build -c Release`
Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
git add Configuration/configPage.html Controllers/CustomCoverArtController.cs
git commit -m "feat(phase1): client canvas engine renders the document live"
```

---

## Task 8: Wire existing controls to the document; selection + server-preview affordance

**Files:**
- Modify: `Configuration/configPage.html`

**Interfaces:**
- Consumes: `doc`, `scheduleRender`, `renderDocument`, `selectedLayer` (Task 7).
- Produces: `function collectDocument()` (returns `doc` with `_selectedId` and other `_`-prefixed UI-only fields stripped) for POSTing; `function syncControlsFromDocument()` (sets every input from `doc`, replacing `applySettingsToForm`); rewritten `collectSettings` usages.

> **Design:** Every existing control handler (Title, size, weight, color, align, dim, blur, gradient stops, fit, dims preset, format, collage, animation) now mutates the corresponding `doc` field and calls `scheduleRender()` instead of `schedulePreview()`. The Apply button posts `document/apply`. Add a "Show server render" button that POSTs `document/preview` and shows the authoritative PNG in a small overlay/`<img>` so the user sees the true output before applying.

- [ ] **Step 1: Rewrite control bindings to mutate `doc`**

Replace `collectSettings()` with `collectDocument()` and repoint each handler in `bindEvents()`:

```javascript
function collectDocument() {
    // doc already holds all state; strip UI-only fields for the wire.
    var wire = JSON.parse(JSON.stringify(doc));
    delete wire._selectedId;
    return wire;
}
// Example: title + size now edit the selected text layer.
el('ccaTitle').addEventListener('input', function () {
    var l = selectedLayer(); if (l) { l.Content = this.value; scheduleRender(); }
});
el('ccaTextSize').addEventListener('input', function () {
    el('ccaTextSizeVal').textContent = this.value;
    var l = selectedLayer(); if (l) { l.Size = parseInt(this.value, 10) / doc.Canvas.Height; scheduleRender(); }
});
el('ccaDim').addEventListener('input', function () {
    el('ccaDimVal').textContent = this.value; doc.Background.Dim = parseFloat(this.value); scheduleRender();
});
el('ccaPreset').addEventListener('change', function () {
    var p = PRESETS[this.value]; if (this.value !== 'custom') { doc.Canvas.Width = p.w; doc.Canvas.Height = p.h; }
    doc.Canvas.DimensionPreset = this.value; updateUI(); scheduleRender();
});
```

Repeat for weight/color/align/blur/dimcolor/fit/format/gradient-type/angle/stops/collage/animation, each writing to `doc.*` and calling `scheduleRender()`. Delete `runPreview`/`schedulePreview`/`previewInFlight`/`onControlChange`'s server call (keep a no-op or route it to `scheduleRender`).

**Background bitmap wiring (honor the canvas-source invariant from Task 7):** in the existing `ccaBgImage` upload handler and the `selectBrowserItem` poster handler, in addition to setting `doc.Background.ImagePath` from the `/upload` response, load the canvas bitmap from the client-held `File`/`Blob`:

```javascript
// after a successful /upload of the background File `file` (or poster Blob `blob`):
doc.Background.ImagePath = pick(res, 'Data');   // server path (used by server render + apply)
var img = new Image();
img.onload = function () { bgImageEl = img; scheduleRender(); if (el('ccaAutoPalette') && el('ccaAutoPalette').checked) { renderSwatches(extractPalette()); } };
img.src = URL.createObjectURL(file /* or blob */);   // blob: URL — never taints the canvas
```

- [ ] **Step 2: Selection + drag of the text layer**

Add pointer handlers on the canvas (convert client coords → canvas pixels via `getBoundingClientRect` scale), hit-test layers (nearest text layer within its measured bounds), set `doc._selectedId`, and drag to update `layer.X`,`layer.Y` (normalized). Implement `drawSelectionHandles(ctx,W,H)` to outline the selected layer.

```javascript
function canvasPoint(ev) {
    var cv = el('ccaCanvas'), r = cv.getBoundingClientRect();
    return { x: (ev.clientX - r.left) / r.width * cv.width, y: (ev.clientY - r.top) / r.height * cv.height };
}
```

- [ ] **Step 3: Apply + server-render affordance**

Rewrite `applyCoverArt` to POST `document/apply` with `{ LibraryId: state.libraryId, Document: collectDocument() }`. Add a "Show server render" button (id `ccaServerRender`) that POSTs `document/preview`, receives the blob, and displays it in a small `<img>` overlay labeled "Authoritative render".

```javascript
function applyCoverArt() {
    if (!state.libraryId) { return; }
    /* ...existing button-lock UI... */
    jsonFetch('CustomCoverArt/document/apply', 'POST', { LibraryId: state.libraryId, Document: collectDocument() })
        .then(function (res) { /* ...existing alert handling... */ });
}
```

- [ ] **Step 4: Manual verification**

Build; load the page. Confirm: editing Title/size/dim updates the canvas instantly; dragging the title repositions it; "Show server render" displays a PNG that matches the canvas; Apply succeeds on a test library.

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" build -c Release`
Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
git add Configuration/configPage.html
git commit -m "feat(phase1): bind controls to document; selection, drag, server-render button"
```

---

## Task 9: Background drag/zoom/pan + template migration + version bump

**Files:**
- Modify: `Configuration/configPage.html`
- Modify: `CustomCoverArt.csproj` (`<Version>3.0.0.0</Version>`)
- Modify: `README.md`, `CHANGELOG.md`

**Interfaces:**
- Consumes: `doc.Background.Transform`, `bgImageEl`, `drawBackgroundImage` (Task 7).
- Produces: background-mode pointer/wheel handlers that mutate `doc.Background.Transform`; `function migrateTemplateToDocument(settingsOrDoc)` mirroring C# `DocumentMigration.FromSettings` for templates whose `Document == null`.

- [ ] **Step 1: Background pan/zoom interactions**

Add a "Reposition background" toggle (id `ccaBgAdjust`). When on, canvas drag pans (`Transform.OffsetX/Y`) and wheel/pinch zooms (`Transform.Scale`, clamped `[1,4]`) instead of selecting layers; `drawBackgroundImage` already consumes the transform, so `scheduleRender()` reflects it live. Wheel handler:

```javascript
el('ccaCanvas').addEventListener('wheel', function (ev) {
    if (!bgAdjustMode) { return; }
    ev.preventDefault();
    var s = doc.Background.Transform.Scale * (ev.deltaY < 0 ? 1.05 : 0.95);
    doc.Background.Transform.Scale = Math.min(4, Math.max(1, s));
    scheduleRender();
}, { passive: false });
```

Add pinch via two-pointer `pointermove` (touch), updating `Scale` by the pinch ratio.

- [ ] **Step 2: Client-side template migration**

In `loadTemplates`, when an option's `Document` is null but `Settings` exists, convert:

```javascript
function migrateTemplateToDocument(s) {
    var d = defaultDocument();
    d.Canvas.Width = pick(s, 'ExportWidth') || d.Canvas.Width;
    d.Canvas.Height = pick(s, 'ExportHeight') || d.Canvas.Height;
    d.Background.Dim = pick(s, 'BackgroundDim'); d.Background.Blur = pick(s, 'BackgroundBlur');
    d.Background.DimColor = pick(s, 'DimColor'); d.Background.Fit = pick(s, 'BackgroundFit') || 'cover';
    d.Background.Gradient = pick(s, 'BackgroundGradient') || d.Background.Gradient;
    var l = d.Layers[0];
    l.Content = ''; // template title stripped
    l.Size = (pick(s, 'TextSize') || 120) / d.Canvas.Height;
    l.Weight = pick(s, 'TextWeight') || 400; l.Color = pick(s, 'TextColor') || '#ffffff';
    l.Align = pick(s, 'TextAlign') || 1;
    return d;
}
```

Load a template by assigning its document into `doc` (preserving the current target's title), then `syncControlsFromDocument()` + `scheduleRender()`. Save templates by POSTing `{ Name, Document: collectDocument() }`.

- [ ] **Step 3: Version bump + docs**

Set `<Version>3.0.0.0</Version>` in `CustomCoverArt.csproj`. Add a `CHANGELOG.md` entry for `3.0.0.0` ("Interactive canvas editor: the preview is now a live canvas you edit directly; drag the title to position it, and reposition/zoom the background image; designs are stored in a new layered format (existing templates migrate automatically)."). Update `README.md` to describe the canvas editor + background repositioning.

- [ ] **Step 4: Full build + test + manual smoke**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" build -c Release` (0 errors) and `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal` (all pass).
Manual: load page, drag background with the toggle on, wheel-zoom, "Show server render" — confirm the framed background matches. Load an old template — confirm it applies without error.

- [ ] **Step 5: Commit**

```bash
git add Configuration/configPage.html CustomCoverArt.csproj README.md CHANGELOG.md
git commit -m "feat(phase1): background pan/zoom, template migration, v3.0.0.0"
```

---

## Self-Review (run after all tasks)

- **Spec coverage:** document model (Task 1) · migration/back-compat (Tasks 2, 6, 9) · server parity refactor (Tasks 3–4) · background transform honored (Task 5) · client canvas replaces img preview (Tasks 7–8) · background drag/zoom/pan (Task 9) · server authoritative + pre-apply render (Task 8 "Show server render") · en/nl strings for new controls (Tasks 8–9) · version 3.0.0.0 (Task 9).
- **Parity guard:** all pre-existing tests (render, dim black-out, animation, collage) must stay green after Tasks 3–4, proving the migrated single-title path renders comparably.
- **Type consistency:** JS `doc` uses PascalCase field names identical to the C# `CoverDocument` so `System.Text.Json` binds without config; `TransformedSourceRect` signature identical in plan text and Task 5 code.
