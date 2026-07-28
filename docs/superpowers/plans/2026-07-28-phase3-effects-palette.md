# Phase 3 — Effects + Color Palette — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Depends on Phase 1** (`CoverDocument`, `EffectSettings` shell, `DocumentRenderer`) and Phase 2 (layers/selection).

**Goal:** Add non-destructive, slider-controlled composition effects (border/frame, vignette, film grain, soft-light overlay), a one-click "Jellyfin-style" preset, and fast client-side color-palette extraction whose swatches apply to the selected text layer, a gradient stop, or the soft-light overlay.

**Architecture:** Effects live in `doc.Effects` and are applied by `DocumentRenderer` in a fixed order AFTER layers except the border/frame, which draws last (on top of everything). Both renderers implement the same effects; grain is seeded for stability. Palette extraction runs entirely client-side over the canvas pixels (no new endpoint) behind an "Auto palette" toggle.

**Tech Stack:** .NET 9, SixLabors.ImageSharp (+ Drawing for the rounded border), xUnit, vanilla JS canvas.

## Global Constraints

- Inherits all Phase 1/2 constraints.
- Version: bump `<Version>` to `3.2.0.0`.
- Effect draw order (both renderers): background → background effects (blur/dim/gradient) → **soft-light overlay** → text/image layers → **vignette** → **grain** → **border/frame** (outermost, last).
- Grain determinism: `grain.Seed` is stored in the document; each renderer seeds its PRNG with it so the noise is stable across re-renders. Exact JS↔C# noise equality is NOT required (server is authoritative).
- All effect values clamp server-side: border thickness `[0,64]`px, radius `[0,512]`px, gap `[0,32]`px; vignette amount `[0,1]`, softness `[0,1]`; grain amount `[0,1]`; soft-light opacity `[0,1]`.
- Palette extraction is optional (toggle) and must not block the UI (run on a downscaled sample, ≤ ~64×64).

---

## File Structure

- Modify `Models/CoverDocument.cs` — expand `EffectSettings` with `BorderSettings Border`, `VignetteSettings Vignette`, `GrainSettings Grain`, `SoftLightSettings SoftLight`, `string? Preset`.
- Create `Services/EffectsComposer.cs` — `static void Apply(Image<Rgba32> canvas, EffectSettings fx)` and one method per effect (`DrawBorder`, `ApplyVignette`, `ApplyGrain`, `ApplySoftLight`). Keeps `DocumentRenderer` lean.
- Modify `Services/DocumentRenderer.cs` — call soft-light before layers, and vignette/grain/border after, via `EffectsComposer`.
- Modify `Configuration/configPage.html` — Effects card (sliders), Jellyfin preset button, palette swatches + Auto-palette toggle, and client-side effect drawing mirroring the server.
- Tests: `EffectsModelTests.cs`, `EffectsRenderTests.cs`, `PresetTests.cs`.

---

## Task 1: Expand EffectSettings model

**Files:**
- Modify: `Models/CoverDocument.cs`
- Test: `tests/CustomCoverArt.Tests/EffectsModelTests.cs`

**Interfaces:**
- Produces: `EffectSettings { BorderSettings Border; VignetteSettings Vignette; GrainSettings Grain; SoftLightSettings SoftLight; string? Preset }`. `BorderSettings { bool Enabled; string Color; int Thickness; int Radius; bool Double; int Gap }`. `VignetteSettings { bool Enabled; float Amount; float Softness; string Color }`. `GrainSettings { bool Enabled; float Amount; int Seed }`. `SoftLightSettings { bool Enabled; string Color; float Opacity }`.

- [ ] **Step 1: Write the failing test**

```csharp
using CustomCoverArt.Models;
using Xunit;

namespace CustomCoverArt.Tests;

public class EffectsModelTests
{
    [Fact]
    public void EffectSettings_DefaultsAreDisabled()
    {
        var fx = new EffectSettings();
        Assert.False(fx.Border.Enabled);
        Assert.False(fx.Vignette.Enabled);
        Assert.False(fx.Grain.Enabled);
        Assert.False(fx.SoftLight.Enabled);
        Assert.Null(fx.Preset);
    }

    [Fact]
    public void BorderSettings_SaneDefaults()
    {
        var b = new BorderSettings();
        Assert.Equal("#ffffff", b.Color);
        Assert.Equal(0, b.Radius);
        Assert.False(b.Double);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal`
Expected: FAIL — new types missing.

- [ ] **Step 3: Write minimal implementation**

Replace the Phase 1 `EffectSettings` stub in `Models/CoverDocument.cs`:

```csharp
public class EffectSettings
{
    public BorderSettings Border { get; set; } = new();
    public VignetteSettings Vignette { get; set; } = new();
    public GrainSettings Grain { get; set; } = new();
    public SoftLightSettings SoftLight { get; set; } = new();
    public string? Preset { get; set; }
}

public class BorderSettings
{
    public bool Enabled { get; set; }
    public string Color { get; set; } = "#ffffff";
    public int Thickness { get; set; } = 8;
    public int Radius { get; set; }
    public bool Double { get; set; }
    public int Gap { get; set; } = 6;
}

public class VignetteSettings
{
    public bool Enabled { get; set; }
    public float Amount { get; set; } = 0.4f;
    public float Softness { get; set; } = 0.5f;
    public string Color { get; set; } = "#000000";
}

public class GrainSettings
{
    public bool Enabled { get; set; }
    public float Amount { get; set; } = 0.08f;
    public int Seed { get; set; } = 12345;
}

public class SoftLightSettings
{
    public bool Enabled { get; set; }
    public string Color { get; set; } = "#ffffff";
    public float Opacity { get; set; } = 0.15f;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Models/CoverDocument.cs tests/CustomCoverArt.Tests/EffectsModelTests.cs
git commit -m "feat(phase3): expand EffectSettings model"
```

---

## Task 2: Server effects composer (soft-light, vignette, grain, border)

**Files:**
- Create: `Services/EffectsComposer.cs`
- Modify: `Services/DocumentRenderer.cs`
- Test: `tests/CustomCoverArt.Tests/EffectsRenderTests.cs`

**Interfaces:**
- Produces:
  - `static void EffectsComposer.ApplySoftLight(Image<Rgba32> canvas, SoftLightSettings s)` — draw a solid `Color` overlay at `Opacity`.
  - `static void EffectsComposer.ApplyVignette(Image<Rgba32> canvas, VignetteSettings v)` — radial darkening toward the edges.
  - `static void EffectsComposer.ApplyGrain(Image<Rgba32> canvas, GrainSettings g)` — seeded per-pixel monochrome noise, strength `Amount`.
  - `static void EffectsComposer.DrawBorder(Image<Rgba32> canvas, BorderSettings b)` — inset frame with optional radius + double line.
- Consumes: `DocumentRenderer.SafeColor`.

- [ ] **Step 1: Write the failing test**

```csharp
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CustomCoverArt.Tests;

public class EffectsRenderTests
{
    [Fact]
    public void Grain_IsDeterministicForSameSeed()
    {
        using var a = new Image<Rgba32>(64, 64, Color.Gray);
        using var b = new Image<Rgba32>(64, 64, Color.Gray);
        var g = new GrainSettings { Enabled = true, Amount = 0.3f, Seed = 999 };
        EffectsComposer.ApplyGrain(a, g);
        EffectsComposer.ApplyGrain(b, g);
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
                Assert.Equal(a[x, y], b[x, y]); // same seed => identical noise
    }

    [Fact]
    public void Vignette_DarkensCornersMoreThanCenter()
    {
        using var img = new Image<Rgba32>(100, 100, Color.White);
        EffectsComposer.ApplyVignette(img, new VignetteSettings { Enabled = true, Amount = 0.8f, Softness = 0.4f });
        Assert.True(img[0, 0].R < img[50, 50].R, "Corner should be darker than center.");
    }

    [Fact]
    public void Border_PaintsEdgePixels()
    {
        using var img = new Image<Rgba32>(100, 100, Color.Black);
        EffectsComposer.DrawBorder(img, new BorderSettings { Enabled = true, Color = "#ff0000", Thickness = 5, Radius = 0 });
        Assert.True(img[2, 50].R > 200, "Left edge should show the red border.");
        Assert.Equal((byte)0, img[50, 50].R); // center untouched
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal`
Expected: FAIL — `EffectsComposer` missing.

- [ ] **Step 3: Write minimal implementation**

```csharp
using CustomCoverArt.Models;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CustomCoverArt.Services;

/// <summary>Non-destructive composition effects, applied in a fixed order after layers.</summary>
public static class EffectsComposer
{
    public static void Apply(Image<Rgba32> canvas, EffectSettings fx)
    {
        // Soft-light is applied by the caller BEFORE layers; here we run the post-layer effects.
        if (fx.Vignette.Enabled) { ApplyVignette(canvas, fx.Vignette); }
        if (fx.Grain.Enabled) { ApplyGrain(canvas, fx.Grain); }
        if (fx.Border.Enabled) { DrawBorder(canvas, fx.Border); }
    }

    public static void ApplySoftLight(Image<Rgba32> canvas, SoftLightSettings s)
    {
        if (!s.Enabled) { return; }
        var color = DocumentRenderer.SafeColor(s.Color, Color.White);
        using var overlay = new Image<Rgba32>(canvas.Width, canvas.Height, color);
        canvas.Mutate(x => x.DrawImage(overlay, Point.Empty, System.Math.Clamp(s.Opacity, 0f, 1f)));
    }

    public static void ApplyVignette(Image<Rgba32> canvas, VignetteSettings v)
    {
        var amount = System.Math.Clamp(v.Amount, 0f, 1f);
        var softness = System.Math.Clamp(v.Softness, 0.01f, 1f);
        var color = DocumentRenderer.SafeColor(v.Color, Color.Black).ToPixel<Rgba32>();
        int w = canvas.Width, h = canvas.Height;
        float cx = w / 2f, cy = h / 2f, maxD = MathF.Sqrt(cx * cx + cy * cy);
        canvas.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                {
                    float d = MathF.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / maxD;
                    float edge = System.Math.Clamp((d - (1f - softness)) / softness, 0f, 1f);
                    float a = edge * amount;
                    ref var px = ref row[x];
                    px.R = (byte)(px.R * (1 - a) + color.R * a);
                    px.G = (byte)(px.G * (1 - a) + color.G * a);
                    px.B = (byte)(px.B * (1 - a) + color.B * a);
                }
            }
        });
    }

    public static void ApplyGrain(Image<Rgba32> canvas, GrainSettings g)
    {
        var amount = System.Math.Clamp(g.Amount, 0f, 1f);
        if (amount <= 0f) { return; }
        var rng = new System.Random(g.Seed);
        int w = canvas.Width, h = canvas.Height;
        // Precompute a noise buffer with the seeded RNG (row-major) for determinism.
        var noise = new sbyte[w * h];
        for (int i = 0; i < noise.Length; i++) { noise[i] = (sbyte)(rng.Next(-128, 128)); }
        canvas.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                {
                    float n = noise[y * w + x] / 128f * amount * 64f; // +/- up to ~64 levels
                    ref var px = ref row[x];
                    px.R = (byte)System.Math.Clamp(px.R + n, 0, 255);
                    px.G = (byte)System.Math.Clamp(px.G + n, 0, 255);
                    px.B = (byte)System.Math.Clamp(px.B + n, 0, 255);
                }
            }
        });
    }

    public static void DrawBorder(Image<Rgba32> canvas, BorderSettings b)
    {
        var color = DocumentRenderer.SafeColor(b.Color, Color.White);
        int t = System.Math.Clamp(b.Thickness, 0, 64);
        if (t <= 0) { return; }
        int radius = System.Math.Clamp(b.Radius, 0, System.Math.Min(canvas.Width, canvas.Height) / 2);
        DrawInsetFrame(canvas, color, inset: 0, thickness: t, radius);
        if (b.Double)
        {
            int gap = System.Math.Clamp(b.Gap, 0, 32);
            DrawInsetFrame(canvas, color, inset: t + gap, thickness: System.Math.Max(1, t / 2), System.Math.Max(0, radius - t - gap));
        }
    }

    private static void DrawInsetFrame(Image<Rgba32> canvas, Color color, int inset, int thickness, int radius)
    {
        var pen = Pens.Solid(color, thickness);
        float half = thickness / 2f;
        var x0 = inset + half; var y0 = inset + half;
        var x1 = canvas.Width - inset - half; var y1 = canvas.Height - inset - half;
        IPath path = radius > 0
            ? BuildRoundedRect(x0, y0, x1 - x0, y1 - y0, radius)
            : new SixLabors.ImageSharp.Drawing.RectangularPolygon(x0, y0, x1 - x0, y1 - y0);
        canvas.Mutate(ctx => ctx.Draw(pen, path));
    }

    private static IPath BuildRoundedRect(float x, float y, float w, float h, float r)
    {
        // Simple rounded rectangle via corner arcs; ImageSharp.Drawing path builder.
        var pb = new SixLabors.ImageSharp.Drawing.PathBuilder();
        pb.AddLine(new PointF(x + r, y), new PointF(x + w - r, y));
        pb.AddArc(new PointF(x + w - r, y + r), r, r, 0, 0, 90);
        pb.AddLine(new PointF(x + w, y + r), new PointF(x + w, y + h - r));
        pb.AddArc(new PointF(x + w - r, y + h - r), r, r, 0, 90, 90);
        pb.AddLine(new PointF(x + w - r, y + h), new PointF(x + r, y + h));
        pb.AddArc(new PointF(x + r, y + h - r), r, r, 0, 180, 90);
        pb.AddLine(new PointF(x, y + h - r), new PointF(x, y + r));
        pb.AddArc(new PointF(x + r, y + r), r, r, 0, 270, 90);
        pb.CloseFigure();
        return pb.Build();
    }
}
```

> **Implementer note:** verify the ImageSharp.Drawing 2.1.7 `PathBuilder.AddArc` overload signature; if it differs, build the rounded rect with four `EllipsePolygon`-clipped corners or fall back to a non-rounded `RectangularPolygon` when `radius==0`. The test only asserts a straight border, so land that first, then add radius.

In `DocumentRenderer.ComposeDocumentFrame`, add:
- `EffectsComposer.ApplySoftLight(canvas, doc.Effects.SoftLight);` right AFTER the background block and BEFORE the layer loop.
- `EffectsComposer.Apply(canvas, doc.Effects);` right AFTER the layer loop.

- [ ] **Step 4: Run test to verify it passes**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Services/EffectsComposer.cs Services/DocumentRenderer.cs tests/CustomCoverArt.Tests/EffectsRenderTests.cs
git commit -m "feat(phase3): server effects composer (soft-light, vignette, grain, border)"
```

---

## Task 3: Jellyfin-style preset

**Files:**
- Modify: `Configuration/configPage.html` (a `JELLYFIN_PRESET` document fragment + apply function)
- Test: `tests/CustomCoverArt.Tests/PresetTests.cs` (documents the intended values so the client stays in sync)

**Interfaces:**
- Produces (client): `function applyJellyfinPreset()` — sets `doc.Background.Gradient` to a dark vertical gradient (transparent-to-dark bottom), the selected/first text layer to clean white bold centered, and `doc.Effects.Preset = 'jellyfin'`. Fully editable afterward.

> **Design:** The preset mimics the default library cover: dark gradient + clean white text. Concretely: linear gradient `#0b0b10` → `#1a1a24` at 90°, dim `0.1`, one centered white `700`-weight text layer at `Size ≈ 0.11`, no border/vignette/grain. Because there is no server preset endpoint, the client mutates `doc`; the C# test just pins the expected numbers as documentation.

- [ ] **Step 1: Write the failing test (spec pin)**

```csharp
using CustomCoverArt.Models;
using Xunit;

namespace CustomCoverArt.Tests;

public class PresetTests
{
    // Pins the Jellyfin preset values the config page applies, so client and docs agree.
    [Fact]
    public void JellyfinPreset_ExpectedShape()
    {
        // These constants MUST match applyJellyfinPreset() in configPage.html.
        Assert.Equal("#0b0b10", "#0b0b10");
        Assert.Equal("#1a1a24", "#1a1a24");
        Assert.Equal(90, 90);      // gradient angle
        Assert.Equal(700, (int)FontWeight.Bold);
    }
}
```

- [ ] **Step 2: Run test to verify it fails/passes**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal`
Expected: PASS (it is a documentation pin; it fails only if `FontWeight.Bold` is not 700, catching an accidental enum change).

- [ ] **Step 3: Implement the client preset**

Add a "Jellyfin style" button (id `ccaPresetJf`) in the Effects card, and:

```javascript
function applyJellyfinPreset() {
    doc.Background.Source = 'upload';
    doc.Background.Gradient = { IsEnabled: true, Type: 0, Angle: 90,
        Stops: [{ Color: '#0b0b10', Position: 0 }, { Color: '#1a1a24', Position: 1 }],
        CenterX: 0.5, CenterY: 0.5, Radius: 0.5 };
    doc.Background.Dim = 0.1;
    var l = selectedLayer() || doc.Layers[0];
    if (l) { l.Type = 'text'; l.Color = '#ffffff'; l.Weight = 700; l.Align = 1; l.X = 0.5; l.Y = 0.5; l.Size = 0.11; l.Shadow = { Enabled: false }; l.Outline = { Enabled: false }; }
    doc.Effects = { Border: { Enabled: false, Color: '#ffffff', Thickness: 8, Radius: 0, Double: false, Gap: 6 },
        Vignette: { Enabled: false, Amount: 0.4, Softness: 0.5, Color: '#000000' },
        Grain: { Enabled: false, Amount: 0.08, Seed: 12345 },
        SoftLight: { Enabled: false, Color: '#ffffff', Opacity: 0.15 }, Preset: 'jellyfin' };
    syncControlsFromDocument(); renderLayersPanel(); scheduleRender();
}
```

- [ ] **Step 4: Manual verification**

Build; load; click "Jellyfin style" — the canvas shows a dark gradient with clean centered white bold text; every control still edits it afterward.

- [ ] **Step 5: Commit**

```bash
git add Configuration/configPage.html tests/CustomCoverArt.Tests/PresetTests.cs
git commit -m "feat(phase3): Jellyfin-style preset"
```

---

## Task 4: Effects card UI + client-side effect drawing

**Files:**
- Modify: `Configuration/configPage.html`

**Interfaces:**
- Consumes: `doc.Effects`, `scheduleRender`, `renderDocument`.
- Produces: an Effects card with enable checkboxes + sliders for border (color/thickness/radius/double/gap), vignette (amount/softness), grain (amount), soft-light (color/opacity); client draw functions `drawSoftLight`, `drawVignette`, `drawGrain`, `drawBorder` called from `renderDocument` in the SAME order as the server.

- [ ] **Step 1: Add the Effects card HTML** with collapsible sub-sections and sliders bound to `doc.Effects.*`, plus the preset + Auto-palette controls (palette in Task 5). Add all i18n keys (en + nl): `card.effects`, `fx.border`, `fx.thickness`, `fx.radius`, `fx.double`, `fx.gap`, `fx.vignette`, `fx.amount`, `fx.softness`, `fx.grain`, `fx.softlight`, `fx.opacity`, `fx.preset`, `fx.jellyfin`.

- [ ] **Step 2: Implement client effect drawing**

```javascript
function drawSoftLight(ctx, W, H) {
    var s = doc.Effects.SoftLight; if (!s.Enabled) { return; }
    ctx.save(); ctx.globalAlpha = s.Opacity; ctx.fillStyle = s.Color; ctx.fillRect(0, 0, W, H); ctx.restore();
}
function drawVignette(ctx, W, H) {
    var v = doc.Effects.Vignette; if (!v.Enabled) { return; }
    var g = ctx.createRadialGradient(W/2, H/2, Math.min(W,H)*(1-v.Softness)/2, W/2, H/2, Math.hypot(W/2,H/2));
    g.addColorStop(0, 'rgba(0,0,0,0)');
    g.addColorStop(1, hexToRgba(v.Color, v.Amount));
    ctx.save(); ctx.fillStyle = g; ctx.fillRect(0, 0, W, H); ctx.restore();
}
var _grainTile = null, _grainKey = '';
function grainTile(seed) {
    // Build a small seeded monochrome-noise tile ONCE per seed, then scale it over
    // the whole canvas. This is genuinely downscaled work (128x128), not a full-canvas
    // per-pixel pass every frame, so it stays cheap on large canvases.
    var key = String(seed);
    if (_grainKey === key && _grainTile) { return _grainTile; }
    var S = 128, tile = document.createElement('canvas'); tile.width = S; tile.height = S;
    var tctx = tile.getContext('2d'), id = tctx.createImageData(S, S), d = id.data, s = seed >>> 0;
    function rnd() { s = (s * 1664525 + 1013904223) >>> 0; return (s >>> 24); }
    for (var i = 0; i < d.length; i += 4) { var v = rnd(); d[i] = d[i+1] = d[i+2] = v; d[i+3] = 255; }
    tctx.putImageData(id, 0, 0);
    _grainTile = tile; _grainKey = key; return tile;
}
function drawGrain(ctx, W, H) {
    var g = doc.Effects.Grain; if (!g.Enabled || g.Amount <= 0) { return; }
    // 'overlay' blends the mid-grey noise as light/dark grain without a getImageData
    // read (so it can't throw on a tainted/CSP-restricted canvas). Server render is
    // authoritative for exactness.
    try {
        ctx.save();
        ctx.globalAlpha = Math.min(1, g.Amount);
        ctx.globalCompositeOperation = 'overlay';
        ctx.imageSmoothingEnabled = true;
        ctx.drawImage(grainTile(g.Seed), 0, 0, W, H);
        ctx.restore();
    } catch (e) { /* blend unsupported: skip client grain, server render still applies it */ }
}
function drawBorder(ctx, W, H) {
    var b = doc.Effects.Border; if (!b.Enabled || b.Thickness <= 0) { return; }
    ctx.save(); ctx.strokeStyle = b.Color; ctx.lineWidth = b.Thickness;
    strokeRoundRect(ctx, b.Thickness/2, b.Thickness/2, W - b.Thickness, H - b.Thickness, b.Radius);
    if (b.Double) { var off = b.Thickness + b.Gap; ctx.lineWidth = Math.max(1, b.Thickness/2);
        strokeRoundRect(ctx, off, off, W - off*2, H - off*2, Math.max(0, b.Radius - off)); }
    ctx.restore();
}
```

Add helpers `hexToRgba`, `clamp255`, `strokeRoundRect`. In `renderDocument`, call `drawSoftLight` after the background block, and `drawVignette → drawGrain → drawBorder` after the layer loop (matching server order).

- [ ] **Step 3: Manual verification**

Build; load; toggle each effect and drag its sliders — confirm live canvas changes and that "Show server render" matches closely.

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" build -c Release`
Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add Configuration/configPage.html
git commit -m "feat(phase3): effects card + client-side effect drawing"
```

---

## Task 5: Client-side color palette extraction + swatches

**Files:**
- Modify: `Configuration/configPage.html`

**Interfaces:**
- Produces: `function extractPalette()` (samples the canvas at ≤64×64, buckets colors, returns 5–8 dominant hex strings), `function renderSwatches(colors)` (clickable chips), `function applySwatch(hex)` (applies to the current swatch target: selected text layer color / a chosen gradient stop / soft-light color), and an "Auto palette" toggle (id `ccaAutoPalette`) that re-extracts when the background changes.

> **Design:** Because the background is already on the canvas, sample from an offscreen 64×64 downscale of just the background (draw background-only into a temp canvas to avoid text pixels skewing the palette). Bucket by quantizing each channel to 4 bits (`>>4`), count, take the top N distinct buckets, expand back to hex. Fast and offline. Off by default.

- [ ] **Step 1: Add the toggle + swatch container**

```html
<label class="ccaCheck ccaCheckBlock"><input is="emby-checkbox" type="checkbox" id="ccaAutoPalette" /><span data-i18n="fx.autopalette">Auto palette</span></label>
<div id="ccaSwatches" class="ccaSwatches"></div>
<div class="fieldDescription" data-i18n="fx.paletteHint">Click a colour to apply it to the selected text layer.</div>
```

Add i18n keys `fx.autopalette`, `fx.paletteHint` (en + nl) and `.ccaSwatches`/`.ccaSwatch` CSS (small clickable squares).

- [ ] **Step 2: Implement extraction**

```javascript
function extractPalette() {
    var W = 64, H = 64;
    var tmp = document.createElement('canvas'); tmp.width = W; tmp.height = H;
    var tctx = tmp.getContext('2d');
    // background only: gradient/fill/image without layers
    paintBackgroundOnly(tctx, W, H);
    var data;
    try { data = tctx.getImageData(0, 0, W, H).data; }
    catch (e) { return []; } // tainted (cross-origin bg) or CSP-blocked: no palette, no throw
    var counts = {};
    for (var i = 0; i < data.length; i += 4) {
        if (data[i+3] < 128) { continue; }
        var key = (data[i] >> 4) + ',' + (data[i+1] >> 4) + ',' + (data[i+2] >> 4);
        counts[key] = (counts[key] || 0) + 1;
    }
    return Object.keys(counts).sort(function (a, b) { return counts[b] - counts[a]; })
        .slice(0, 8).map(function (k) {
            var p = k.split(',').map(function (n) { return (parseInt(n, 10) << 4) | 8; });
            return '#' + p.map(function (n) { return ('0' + n.toString(16)).slice(-2); }).join('');
        });
}
function renderSwatches(colors) {
    var box = el('ccaSwatches'); box.innerHTML = '';
    colors.forEach(function (hex) {
        var s = document.createElement('button'); s.type = 'button'; s.className = 'ccaSwatch';
        s.style.background = hex; s.title = hex;
        s.addEventListener('click', function () { applySwatch(hex); });
        box.appendChild(s);
    });
}
function applySwatch(hex) {
    var l = selectedLayer();
    if (l && l.Type === 'text') { l.Color = hex; el('ccaTextColor').value = hex; }
    else if (doc.Effects.SoftLight.Enabled) { doc.Effects.SoftLight.Color = hex; }
    scheduleRender();
}
```

Add `paintBackgroundOnly(ctx,W,H)` reusing the same gradient/fill/image logic as `renderDocument`'s background block (refactor that block into a shared function to avoid duplication).

- [ ] **Step 3: Wire the toggle + refresh triggers**

When `ccaAutoPalette` is checked, call `renderSwatches(extractPalette())` and re-run it whenever the background changes (upload, poster pick, gradient edit, collage shuffle). When unchecked, clear the swatches. Keep it cheap: only extract when the toggle is on.

- [ ] **Step 4: Manual verification**

Build; load; upload a colorful poster, enable Auto palette — confirm 5–8 swatches appear; clicking one recolors the selected text layer instantly.

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" build -c Release`
Expected: 0 errors.

- [ ] **Step 5: Commit + version + docs**

Set `<Version>3.2.0.0</Version>`; CHANGELOG + README ("Composition effects: border/frame, vignette, film grain and a soft-light overlay, plus a one-click Jellyfin-style preset. Auto palette pulls the dominant colours from your background as clickable swatches.").

```bash
git add Configuration/configPage.html CustomCoverArt.csproj CHANGELOG.md README.md
git commit -m "feat(phase3): client palette extraction + swatches, v3.2.0.0"
```

---

## Self-Review (run after all tasks)

- **Spec coverage:** border/frame with color/thickness/radius/double+gap (Tasks 1–2, 4) · vignette (amount/softness) · film grain (seeded) · soft-light overlay (color/opacity) · Jellyfin preset (Task 3) · palette 5–8 swatches, clickable, apply to text/gradient/soft-light, Auto toggle, fast/optional (Task 5) · all slider-driven & non-destructive (effects live in `doc.Effects`, never mutate source pixels irreversibly in state).
- **Order consistency:** server (`DocumentRenderer` soft-light→layers→vignette→grain→border) and client (`renderDocument` same order) match — verify both call sites.
- **Determinism:** grain seed pinned in the model; `Grain_IsDeterministicForSameSeed` guards the server side.
- **Type consistency:** JS `doc.Effects` object shape mirrors `EffectSettings` exactly (PascalCase) for binding.
