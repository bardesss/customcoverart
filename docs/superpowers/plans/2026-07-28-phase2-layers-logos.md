# Phase 2 — Multiple Text Layers + Logos/Icons — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Depends on Phase 1** (the `CoverDocument` model, `DocumentRenderer`, client canvas engine).

**Goal:** Support an unlimited number of independently styled text layers plus upload-only PNG image (logo/icon) layers — all freely positioned, resized, opacity-controlled — managed through a layers panel (show/hide, reorder, delete, duplicate, select).

**Architecture:** Layers already live in `doc.Layers` and render in array order (Phase 1). This phase adds image-layer rendering on both sides, per-layer property editing for the *selected* layer, resize/rotate handles on the canvas, and a layers-panel UI. Image layers reuse the existing sandboxed `/upload` endpoint + `ValidateFileAsync`.

**Tech Stack:** .NET 9, SixLabors.ImageSharp, xUnit + NSubstitute, vanilla JS canvas.

## Global Constraints

- Inherits all Phase 1 constraints (coordinate contract, auth, sandbox, clamps, en/nl sync, small files).
- Version: bump `<Version>` to `3.1.0.0`.
- Image layers: PNG (transparency preserved). Reuse `POST /upload` + `ImageProcessingService.ValidateFileAsync` (already accepts png and content-validates). The server only draws image paths that pass `PluginPaths.IsInsideBase`.
- Bound layer count server-side to prevent abuse: clamp `doc.Layers` to at most 40 in `GenerateFromDocumentAsync`; log and drop the excess.
- Image layer size stored normalized: `Width`,`Height` as fraction of canvas width/height.

---

## File Structure

- Modify `Services/DocumentRenderer.cs` — add `RenderImageLayer(canvas, CoverLayer, doc, applicationPaths)` and call it from `ComposeDocumentFrame` for `Type == "image"`; add opacity/rotation compositing.
- Modify `Services/CoverArtService.cs` — clamp layer count; ensure image-layer paths are sandbox-checked (extend the existing background-path sandbox block to also filter layer `ImagePath`s).
- Modify `Configuration/configPage.html` — layers panel UI + per-layer property panel + image-layer upload + resize/rotate handles.
- Tests: `tests/CustomCoverArt.Tests/ImageLayerRenderTests.cs`, `tests/CustomCoverArt.Tests/LayerModelTests.cs`.

---

## Task 1: Render image (logo) layers server-side

**Files:**
- Modify: `Services/DocumentRenderer.cs`
- Test: `tests/CustomCoverArt.Tests/ImageLayerRenderTests.cs`

**Interfaces:**
- Consumes: `CoverLayer` with `Type == "image"`, `ImagePath`, normalized `X,Y,Width,Height`, `Opacity`, `Rotation`.
- Produces: `internal static void DocumentRenderer.RenderImageLayer(Image<Rgba32> canvas, CoverLayer layer)` — draws the PNG centered at `(X*W, Y*H)`, resized to `(Width*W, Height*H)`, at `Opacity`. Guarded by `Image.Identify` bomb-check; never throws. (The layer already carries a sandboxed absolute `ImagePath`, filtered upstream in `CoverArtService`, Task 2.)

> **Signature used by the compositor:** `RenderImageLayer(Image<Rgba32> canvas, CoverLayer layer)` — the layer already carries a sandboxed absolute `ImagePath` (filtered upstream in `CoverArtService`, Task 2). Rotation: if `layer.Rotation != 0`, rotate the resized logo via `poster.Mutate(x => x.Rotate(layer.Rotation))` before drawing (bounding box grows; recompute the top-left so the rotated image stays centered).

- [ ] **Step 1: Write the failing test**

```csharp
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CustomCoverArt.Tests;

public class ImageLayerRenderTests
{
    [Fact]
    public void RenderImageLayer_DrawsLogoPixelsAtCenter()
    {
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"logo_{System.Guid.NewGuid():N}.png");
        using (var logo = new Image<Rgba32>(50, 50, Color.Red)) { logo.SaveAsPng(tmp); }
        try
        {
            using var canvas = new Image<Rgba32>(200, 200); // transparent
            var layer = new CoverLayer { Type = "image", ImagePath = tmp, X = 0.5f, Y = 0.5f, Width = 0.25f, Height = 0.25f, Opacity = 1f };
            DocumentRenderer.RenderImageLayer(canvas, layer);

            var center = canvas[100, 100];
            Assert.True(center.R > 200 && center.A > 200, "Logo should be drawn opaque red at the center.");
            Assert.Equal(0, canvas[5, 5].A); // corners stay transparent
        }
        finally { System.IO.File.Delete(tmp); }
    }

    [Fact]
    public void RenderImageLayer_MissingFile_DoesNotThrow()
    {
        using var canvas = new Image<Rgba32>(100, 100);
        var layer = new CoverLayer { Type = "image", ImagePath = "/no/such/file.png", X = 0.5f, Y = 0.5f, Width = 0.5f, Height = 0.5f };
        DocumentRenderer.RenderImageLayer(canvas, layer); // must be a no-op, not throw
        Assert.Equal(0, canvas[50, 50].A);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal`
Expected: FAIL — `RenderImageLayer` not defined.

- [ ] **Step 3: Write minimal implementation**

```csharp
internal static void RenderImageLayer(Image<Rgba32> canvas, CoverLayer layer)
{
    if (string.IsNullOrEmpty(layer.ImagePath) || !System.IO.File.Exists(layer.ImagePath)) { return; }
    var w = System.Math.Max(1, (int)System.Math.Round(layer.Width * canvas.Width));
    var h = System.Math.Max(1, (int)System.Math.Round(layer.Height * canvas.Height));
    try
    {
        var info = Image.Identify(layer.ImagePath);
        if ((long)info.Width * info.Height > 8192L * 8192L) { return; }
        using var logo = Image.Load<Rgba32>(layer.ImagePath);
        logo.Mutate(x => x.Resize(w, h));
        if (layer.Rotation != 0f) { logo.Mutate(x => x.Rotate(layer.Rotation)); }
        var cx = (int)System.Math.Round(layer.X * canvas.Width);
        var cy = (int)System.Math.Round(layer.Y * canvas.Height);
        var px = cx - logo.Width / 2;
        var py = cy - logo.Height / 2;
        var opacity = System.Math.Clamp(layer.Opacity, 0f, 1f);
        canvas.Mutate(x => x.DrawImage(logo, new Point(px, py), opacity));
    }
    catch { /* unreadable logo: skip, never break the whole render */ }
}
```

Call it from `ComposeDocumentFrame`: add `else if (layer.Type == "image") { RenderImageLayer(canvas, layer); }`.

- [ ] **Step 4: Run test to verify it passes**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Services/DocumentRenderer.cs tests/CustomCoverArt.Tests/ImageLayerRenderTests.cs
git commit -m "feat(phase2): render image (logo) layers server-side"
```

---

## Task 2: Sandbox layer image paths + clamp layer count

**Files:**
- Modify: `Services/CoverArtService.cs`
- Test: `tests/CustomCoverArt.Tests/LayerModelTests.cs`

**Interfaces:**
- Consumes: `CoverDocument.Layers`.
- Produces: in `GenerateFromDocumentAsync`, before compositing: (a) drop layers beyond index 40 (log a warning); (b) for each `Type == "image"` layer, blank its `ImagePath` if `!PluginPaths.IsInsideBase(_applicationPaths, path)` (mirrors the existing background-path guard).

- [ ] **Step 1: Write the failing test**

```csharp
using CustomCoverArt.Models;
using CustomCoverArt.Services;
using Xunit;

namespace CustomCoverArt.Tests;

public class LayerModelTests
{
    [Fact]
    public async System.Threading.Tasks.Task OutsideSandboxImagePath_IsIgnored_NoThrow()
    {
        var svc = AnimationTestHost.NewCoverArtService();
        var doc = new CoverDocument { Canvas = new CanvasSettings { Width = 120, Height = 120, Format = "png" } };
        doc.Background.DimColor = "#000000";
        doc.Layers.Add(new CoverLayer { Type = "image", ImagePath = "/etc/passwd", X = 0.5f, Y = 0.5f, Width = 0.5f, Height = 0.5f });

        var path = await svc.GenerateFromDocumentAsync(doc); // must succeed; unsafe path ignored
        Assert.True(System.IO.File.Exists(path));
        try { System.IO.File.Delete(path); } catch { }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal`
Expected: PASS-or-FAIL — if it throws/leaks today it fails; make the guard explicit regardless.

- [ ] **Step 3: Write minimal implementation**

In `GenerateFromDocumentAsync`, right after the existing background-path sandbox block:

```csharp
if (document.Layers.Count > 40)
{
    _loggingService.LogWarning("Dropping {Count} excess layers (max 40)", document.Layers.Count - 40);
    document.Layers = document.Layers.Take(40).ToList();
}
foreach (var layer in document.Layers)
{
    if (layer.Type == "image" && !PluginPaths.IsInsideBase(_applicationPaths, layer.ImagePath))
    {
        if (!string.IsNullOrEmpty(layer.ImagePath))
            _loggingService.LogWarning("Layer image rejected (outside plugin data dir)");
        layer.ImagePath = string.Empty;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" test tests/CustomCoverArt.Tests -v minimal`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Services/CoverArtService.cs tests/CustomCoverArt.Tests/LayerModelTests.cs
git commit -m "feat(phase2): sandbox layer image paths, clamp layer count"
```

---

## Task 3: Layers panel UI (list, select, show/hide, reorder, delete, duplicate)

**Files:**
- Modify: `Configuration/configPage.html`

**Interfaces:**
- Consumes: `doc.Layers`, `doc._selectedId`, `scheduleRender`, `syncControlsFromDocument`.
- Produces: `function renderLayersPanel()` (rebuilds the list DOM from `doc.Layers`, newest on top = last drawn on top), `function addTextLayer()`, `function addImageLayer(path, name)`, `function duplicateLayer(id)`, `function deleteLayer(id)`, `function moveLayer(id, dir)`, `function selectLayerById(id)`, `function newLayerId()`.

> **Design:** A new "Layers" card above the Text card. Each row: visibility toggle (👁), name (text = its content or "Text"; image = file name or "Logo"), up/down reorder, duplicate, delete. Clicking a row selects it (`doc._selectedId`) and the Text/property card edits that layer. Draw order: `doc.Layers[0]` is bottom; the panel lists top-most first (reverse) for intuitive stacking.

- [ ] **Step 1: Add the Layers card HTML**

Insert before the Text card (`configPage.html` ~line 48):

```html
<section class="ccaCard">
    <div class="ccaCardHead" data-i18n="card.layers">Layers</div>
    <div id="ccaLayerList" class="ccaLayerList"></div>
    <div class="ccaFileRow">
        <button is="emby-button" type="button" id="ccaAddText" class="raised"><span data-i18n="layer.addText">Add text</span></button>
        <button is="emby-button" type="button" id="ccaAddImage" class="raised"><span data-i18n="layer.addImage">Add logo/icon</span></button>
        <input type="file" id="ccaLayerImage" accept=".png" class="ccaHiddenFile" />
    </div>
</section>
```

Add CSS for `.ccaLayerList`/`.ccaLayerRow` (flex rows, selected highlight using `var(--theme-primary-color)`), and add all new i18n keys (`card.layers`, `layer.addText`, `layer.addImage`, `layer.show`, `layer.hide`, `layer.up`, `layer.down`, `layer.dup`, `layer.del`, `layer.text`, `layer.logo`) to BOTH `en` and `nl` in `I18N`.

- [ ] **Step 2: Implement the panel logic**

```javascript
function newLayerId() { return 'l' + Date.now().toString(36) + Math.floor(Math.random() * 1e4).toString(36); }
function selectLayerById(id) { doc._selectedId = id; renderLayersPanel(); syncControlsFromDocument(); scheduleRender(); }

function renderLayersPanel() {
    var box = el('ccaLayerList'); box.innerHTML = '';
    for (var i = doc.Layers.length - 1; i >= 0; i--) {
        (function (layer) {
            var row = document.createElement('div');
            row.className = 'ccaLayerRow' + (layer.Id === doc._selectedId ? ' ccaLayerSel' : '');
            var vis = document.createElement('button'); vis.type = 'button'; vis.className = 'ccaLayerBtn';
            vis.textContent = layer.Visible ? '👁' : '⌀';
            vis.addEventListener('click', function (e) { e.stopPropagation(); layer.Visible = !layer.Visible; renderLayersPanel(); scheduleRender(); });
            var name = document.createElement('span'); name.className = 'ccaLayerName';
            name.textContent = layer.Type === 'text' ? (layer.Content || t('layer.text')) : (layer._name || t('layer.logo'));
            row.appendChild(vis); row.appendChild(name);
            ['up','down','dup','del'].forEach(function (act) {
                var b = document.createElement('button'); b.type = 'button'; b.className = 'ccaLayerBtn';
                b.textContent = act === 'up' ? '▲' : act === 'down' ? '▼' : act === 'dup' ? '⧉' : '✕';
                b.title = t('layer.' + act);
                b.addEventListener('click', function (e) {
                    e.stopPropagation();
                    if (act === 'up') { moveLayer(layer.Id, 1); }
                    else if (act === 'down') { moveLayer(layer.Id, -1); }
                    else if (act === 'dup') { duplicateLayer(layer.Id); }
                    else { deleteLayer(layer.Id); }
                });
                row.appendChild(b);
            });
            row.addEventListener('click', function () { selectLayerById(layer.Id); });
            box.appendChild(row);
        })(doc.Layers[i]);
    }
}

function addTextLayer() {
    var l = JSON.parse(JSON.stringify(defaultDocument().Layers[0]));
    l.Id = newLayerId(); l.Content = 'Text'; doc.Layers.push(l); selectLayerById(l.Id);
}
function duplicateLayer(id) {
    var i = doc.Layers.findIndex(function (l) { return l.Id === id; });
    if (i < 0) { return; }
    var copy = JSON.parse(JSON.stringify(doc.Layers[i])); copy.Id = newLayerId();
    copy.X = Math.min(1, copy.X + 0.03); copy.Y = Math.min(1, copy.Y + 0.03);
    doc.Layers.splice(i + 1, 0, copy); selectLayerById(copy.Id);
}
function deleteLayer(id) {
    doc.Layers = doc.Layers.filter(function (l) { return l.Id !== id; });
    if (doc._selectedId === id) { doc._selectedId = doc.Layers.length ? doc.Layers[doc.Layers.length - 1].Id : null; }
    renderLayersPanel(); syncControlsFromDocument(); scheduleRender();
}
function moveLayer(id, dir) {
    var i = doc.Layers.findIndex(function (l) { return l.Id === id; });
    var j = i + dir;
    if (i < 0 || j < 0 || j >= doc.Layers.length) { return; }
    var tmp = doc.Layers[i]; doc.Layers[i] = doc.Layers[j]; doc.Layers[j] = tmp;
    renderLayersPanel(); scheduleRender();
}
```

Wire `ccaAddText` → `addTextLayer`, `ccaAddImage` → `el('ccaLayerImage').click()`. Call `renderLayersPanel()` in the init (`pageshow`) after building `doc`.

- [ ] **Step 3: Image-layer upload**

```javascript
el('ccaLayerImage').addEventListener('change', function () {
    if (!this.files || !this.files[0]) { return; }
    var file = this.files[0];
    Dashboard.showLoadingMsg();
    uploadFile('upload', file).then(function (res) {
        Dashboard.hideLoadingMsg();
        if (pick(res, 'Success')) {
            var l = { Id: newLayerId(), Type: 'image', Visible: true, X: 0.5, Y: 0.5,
                Width: 0.25, Height: 0.25, Rotation: 0, Opacity: 1,
                ImagePath: pick(res, 'Data'), _name: file.name,
                Content: '', Size: 0, Weight: 400, Color: '#ffffff', Align: 1, FontPath: '',
                Shadow: { Enabled: false }, Outline: { Enabled: false } };
            // Keep the displayed aspect ratio: load client-side to set Height from natural ratio.
            var img = new Image();
            img.onload = function () {
                var ar = img.naturalHeight / img.naturalWidth;
                l.Height = l.Width * ar * (doc.Canvas.Width / doc.Canvas.Height);
                doc.Layers.push(l); l._img = img; selectLayerById(l.Id);
            };
            img.src = URL.createObjectURL(file);
        } else { Dashboard.alert(t('dyn.uploadFail', pick(res, 'ErrorMessage') || t('dyn.unknown'))); }
    }).catch(function (e) { Dashboard.hideLoadingMsg(); Dashboard.alert(e.message); });
});
```

Extend `drawTextLayer`'s dispatcher in `renderDocument` so `layer.Type === 'image'` draws `layer._img` (client-cached) via `ctx.drawImage` at the same normalized rect/opacity/rotation the server uses; for layers loaded from a template without `_img`, lazily create an `Image()` from `ApiClient`-independent path is not possible (server path), so fetch is skipped client-side and only the server render shows it — acceptable, note it in the UI hint.

- [ ] **Step 4: Manual verification**

Build; load. Confirm: Add text creates a second editable layer; Add logo uploads a PNG and shows it on the canvas; visibility/reorder/duplicate/delete all update canvas + panel; selecting a row focuses its properties.

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" build -c Release`
Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
git add Configuration/configPage.html
git commit -m "feat(phase2): layers panel (add/select/hide/reorder/duplicate/delete) + logo upload"
```

---

## Task 4: Per-layer property panel + resize/rotate/opacity handles

**Files:**
- Modify: `Configuration/configPage.html`

**Interfaces:**
- Consumes: `selectedLayer()`, canvas pointer handlers (Phase 1 Task 8), `renderLayersPanel`.
- Produces: `syncControlsFromDocument()` extended to populate controls from the selected layer (text vs image); an Opacity slider (id `ccaLayerOpacity`) and Rotation slider (id `ccaLayerRotation`) shown for the selected layer; canvas corner handles that resize an image layer (`Width`/`Height`) and a rotate handle.

> **Design:** The existing Text card becomes the "selected layer" editor. When the selected layer is text, show text controls (Title→Content, size, weight, color, align, shadow, outline, font). When it's an image, hide text-only controls and show only Opacity + Rotation + size (drag handles). Add Opacity/Rotation sliders applicable to both types.

- [ ] **Step 1: Add Opacity + Rotation controls to the Text card**

```html
<div class="ccaGrid">
    <div class="inputContainer">
        <label for="ccaLayerOpacity"><span data-i18n="layer.opacity">Opacity</span> <span class="ccaVal" id="ccaLayerOpacityVal">100</span></label>
        <input type="range" class="ccaRange" id="ccaLayerOpacity" min="0" max="100" step="1" value="100" />
    </div>
    <div class="inputContainer">
        <label for="ccaLayerRotation"><span data-i18n="layer.rotation">Rotation</span> <span class="ccaVal" id="ccaLayerRotationVal">0°</span></label>
        <input type="range" class="ccaRange" id="ccaLayerRotation" min="-180" max="180" step="1" value="0" />
    </div>
</div>
```

Add i18n keys `layer.opacity`, `layer.rotation` (en + nl). Handlers write `selectedLayer().Opacity`/`.Rotation` and `scheduleRender()`.

- [ ] **Step 2: `syncControlsFromDocument` for the selected layer**

```javascript
function syncControlsFromDocument() {
    var l = selectedLayer();
    var isText = l && l.Type === 'text';
    el('ccaSettings').querySelectorAll('.ccaTextOnly').forEach(function (n) { n.style.display = isText ? '' : 'none'; });
    if (!l) { return; }
    el('ccaLayerOpacity').value = Math.round(l.Opacity * 100); el('ccaLayerOpacityVal').textContent = Math.round(l.Opacity * 100);
    el('ccaLayerRotation').value = l.Rotation; el('ccaLayerRotationVal').textContent = l.Rotation + '°';
    if (isText) {
        el('ccaTitle').value = l.Content;
        el('ccaTextSize').value = Math.round(l.Size * doc.Canvas.Height); el('ccaTextSizeVal').textContent = el('ccaTextSize').value;
        el('ccaTextWeight').value = l.Weight; el('ccaTextColor').value = l.Color; el('ccaTextAlign').value = l.Align;
        el('ccaShadow').checked = !!(l.Shadow && l.Shadow.Enabled); el('ccaOutline').checked = !!(l.Outline && l.Outline.Enabled);
    }
    // also sync background/output/gradient controls from doc (unchanged from Phase 1)
}
```

Mark text-only inputContainers in the Text card with class `ccaTextOnly` so they hide for image layers.

- [ ] **Step 3: Canvas resize + rotate handles**

Extend `drawSelectionHandles` to draw 4 corner squares + a rotate knob for the selected layer's bounds (text bounds from `ctx.measureText`; image bounds from `Width*W × Height*H`). In the canvas pointer handlers, hit-test handles first: dragging a corner sets image `Width`/`Height` (keep aspect with Shift); dragging the knob sets `Rotation`. Text layers resize by mapping the drag to `Size`.

- [ ] **Step 4: Manual verification**

Build; load. Confirm: selecting a text layer shows text controls; selecting a logo shows only opacity/rotation; corner-drag resizes the logo; rotate knob rotates it; opacity slider fades it — all live, and the server render matches.

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" build -c Release`
Expected: 0 errors.

- [ ] **Step 5: Commit + version + docs**

Set `<Version>3.1.0.0</Version>`; add CHANGELOG + README notes ("Add any number of text layers, plus your own PNG logos/icons — each freely positioned, resized, rotated, and faded, managed in a Layers panel.").

```bash
git add Configuration/configPage.html CustomCoverArt.csproj CHANGELOG.md README.md
git commit -m "feat(phase2): per-layer properties, resize/rotate/opacity handles, v3.1.0.0"
```

---

## Self-Review (run after all tasks)

- **Spec coverage:** unlimited text layers (Task 3 `addTextLayer`) · per-layer content/font/size/weight/color/opacity/align/position/shadow/outline (Tasks 3–4 + Phase 1 text render) · logo upload + placement/resize/opacity (Tasks 1–4) · layers panel show/hide/reorder/delete/duplicate/select (Task 3) · server draws layers in order (Task 1) · sandbox + count clamp (Task 2).
- **Type consistency:** JS image-layer objects carry the full `CoverLayer` field set (PascalCase) so they serialize into the C# model; `_img`/`_name` are UI-only and stripped by `collectDocument()` (extend it to strip `_`-prefixed keys recursively).
- **Placeholder scan:** none — resize/rotate handle math is described concretely; if a step feels large, split corner-resize and rotate into two commits.
