# Phase 4 — UI Polish (Undo/Redo, Mobile, Preview Modes) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **Depends on Phases 1–3** (`CoverDocument`, canvas engine, layers, effects).

**Goal:** Full undo/redo over the whole design (Ctrl+Z / Ctrl+Y + buttons), a significantly more mobile-friendly configuration page (responsive layout, larger touch targets, collapsible sections), and a preview mode showing the cover in common aspect ratios and simulated client contexts (home screen, library grid, details page).

**Architecture:** Undo/redo is a snapshot stack of the JSON-serialized `doc`, pushed on every committed edit (debounced). Mobile-friendliness is CSS + collapsible `<details>`-style cards + larger hit targets, plus the already touch-capable canvas. Preview modes render the *same* `doc` into small framed contexts at different aspect ratios — no new server work; they reuse the client renderer or the server `document/preview` for fidelity.

**Tech Stack:** vanilla JS + CSS in the embedded page. This phase is almost entirely `configPage.html`; no model or render-pipeline changes.

## Global Constraints

- Inherits all Phase 1–3 constraints (coordinate contract, auth, sandbox, en/nl sync).
- Version: bump `<Version>` to `3.3.0.0`.
- Undo/redo history is client-only and session-scoped (not persisted); cap the stack (e.g. 50 entries) to bound memory.
- Keyboard shortcuts must not hijack typing: ignore Ctrl+Z/Y when the focused element is an `<input>`/`<textarea>`/`contenteditable` (let the browser handle text undo there).
- No new endpoints. Preview modes reuse the client canvas render (fast) with an option to show the authoritative server render.

---

## File Structure

- Modify `Configuration/configPage.html` only:
  - History module (snapshot/undo/redo) + toolbar buttons + keyboard handler.
  - Responsive CSS + collapsible card behavior + larger touch targets.
  - Preview-modes strip (aspect-ratio + simulated-context frames).
  - New i18n keys (en + nl).
- No test project changes are required (all DOM/UX). Add one optional pure-JS-logic guard as a comment-documented manual check.

---

## Task 1: Undo/redo history

**Files:**
- Modify: `Configuration/configPage.html`

**Interfaces:**
- Produces: `var history = { stack: [], index: -1 }`, `function snapshot()` (push a deep JSON copy of `doc` after an edit, truncating any redo tail; cap 50), `function undo()`, `function redo()`, `function restoreSnapshot(json)` (replace `doc`, then `syncControlsFromDocument` + `renderLayersPanel` + `scheduleRender`), `function updateHistoryButtons()`.
- Consumes: every edit path (`scheduleRender` callers) should call a debounced `commit()` that snapshots.

> **Design:** Wrap edits so that a burst of slider input collapses into ONE history entry. Introduce `commit = debounce(snapshot, 400)`; call `commit()` wherever the code currently mutates `doc` then `scheduleRender()`. Discrete actions (add/delete/duplicate/reorder layer, apply preset, load template) call `snapshot()` immediately (no debounce). Push the initial document as the first snapshot on init.

- [ ] **Step 1: Add toolbar buttons**

In the preview card header (near the Apply/Download actions), add:

```html
<div class="ccaHistoryBar">
    <button is="emby-button" type="button" id="ccaUndo" class="raised" disabled title="Ctrl+Z"><span data-i18n="hist.undo">Undo</span></button>
    <button is="emby-button" type="button" id="ccaRedo" class="raised" disabled title="Ctrl+Y"><span data-i18n="hist.redo">Redo</span></button>
</div>
```

Add i18n keys `hist.undo`/`hist.redo` (en + nl).

- [ ] **Step 2: Implement the history module**

```javascript
var history = { stack: [], index: -1 };
var HISTORY_MAX = 50;

function currentJson() { var w = collectDocument(); return JSON.stringify(w); }

function snapshot() {
    var json = currentJson();
    if (history.index >= 0 && history.stack[history.index] === json) { return; } // no-op edit
    history.stack = history.stack.slice(0, history.index + 1);
    history.stack.push(json);
    if (history.stack.length > HISTORY_MAX) { history.stack.shift(); }
    history.index = history.stack.length - 1;
    updateHistoryButtons();
}
var commit = debounce(snapshot, 400);

function restoreSnapshot(json) {
    var restored = JSON.parse(json);
    restored._selectedId = doc._selectedId; // keep selection if still present
    doc = restored;
    if (!doc.Layers.some(function (l) { return l.Id === doc._selectedId; })) {
        doc._selectedId = doc.Layers.length ? doc.Layers[doc.Layers.length - 1].Id : null;
    }
    syncControlsFromDocument(); renderLayersPanel(); scheduleRender();
}
function undo() { if (history.index > 0) { history.index--; restoreSnapshot(history.stack[history.index]); updateHistoryButtons(); } }
function redo() { if (history.index < history.stack.length - 1) { history.index++; restoreSnapshot(history.stack[history.index]); updateHistoryButtons(); } }
function updateHistoryButtons() {
    el('ccaUndo').disabled = history.index <= 0;
    el('ccaRedo').disabled = history.index >= history.stack.length - 1;
}
```

- [ ] **Step 3: Hook edits + keyboard**

- After building the initial `doc` in `pageshow`, call `snapshot()` once.
- Replace `scheduleRender()` calls that follow a `doc` mutation with `scheduleRender(); commit();` (or add `commit()` inside a small `edited()` helper you route mutations through). Discrete ops (`addTextLayer`/`deleteLayer`/`duplicateLayer`/`moveLayer`/`applyJellyfinPreset`/template load) call `snapshot()` directly.
- Keyboard:

```javascript
document.addEventListener('keydown', function (e) {
    if (!page || !document.body.contains(page)) { return; }
    var tag = (e.target && e.target.tagName) || '';
    if (/^(INPUT|TEXTAREA|SELECT)$/.test(tag) || (e.target && e.target.isContentEditable)) { return; }
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'z' && !e.shiftKey) { e.preventDefault(); undo(); }
    else if ((e.ctrlKey || e.metaKey) && (e.key.toLowerCase() === 'y' || (e.shiftKey && e.key.toLowerCase() === 'z'))) { e.preventDefault(); redo(); }
});
el('ccaUndo').addEventListener('click', undo);
el('ccaRedo').addEventListener('click', redo);
```

- [ ] **Step 4: Manual verification**

Build; load. Confirm: editing text/sliders then Ctrl+Z reverts as one step; adding/deleting a layer is one undo step; Ctrl+Y / Redo re-applies; buttons enable/disable correctly; Ctrl+Z inside a text input still does normal text undo (not doc undo).

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" build -c Release`
Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
git add Configuration/configPage.html
git commit -m "feat(phase4): full undo/redo history with Ctrl+Z/Y and buttons"
```

---

## Task 2: Mobile-friendly responsive layout + collapsible cards + touch targets

**Files:**
- Modify: `Configuration/configPage.html`

**Interfaces:**
- Produces: collapsible cards via a clickable `.ccaCardHead` that toggles a `.ccaCollapsed` class on its `.ccaCard`; responsive breakpoints; larger touch targets on small screens.

> **Design:** Keep the existing two-column grid on wide screens (already collapses to one column at 900px). On narrow screens: make each card header a tap-to-collapse control (chevron), increase control heights/tap areas, make the preview canvas full-width and the history/apply buttons sticky at the bottom. Respect `prefers-reduced-motion` for the collapse animation.

- [ ] **Step 1: Collapsible cards**

Add a chevron to each `.ccaCardHead` and JS:

```javascript
function makeCardsCollapsible() {
    page.querySelectorAll('.ccaCard .ccaCardHead').forEach(function (head) {
        head.setAttribute('role', 'button'); head.setAttribute('tabindex', '0');
        head.classList.add('ccaCollapsible');
        function toggle() { head.parentElement.classList.toggle('ccaCollapsed'); }
        head.addEventListener('click', toggle);
        head.addEventListener('keydown', function (e) { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(); } });
    });
}
```

CSS:

```css
#CustomCoverArtConfigPage .ccaCollapsible { cursor: pointer; display: flex; align-items: center; justify-content: space-between; }
#CustomCoverArtConfigPage .ccaCollapsible::after { content: '▾'; opacity: .5; transition: transform .2s ease; }
#CustomCoverArtConfigPage .ccaCollapsed .ccaCollapsible::after { transform: rotate(-90deg); }
#CustomCoverArtConfigPage .ccaCollapsed > *:not(.ccaCardHead) { display: none !important; }
@media (prefers-reduced-motion: reduce) { #CustomCoverArtConfigPage .ccaCollapsible::after { transition: none; } }
```

Call `makeCardsCollapsible()` once in init (guarded by `page.dataset.ccaInit`).

- [ ] **Step 2: Touch targets + narrow layout**

```css
@media (max-width: 640px) {
    #CustomCoverArtConfigPage .emby-button { min-height: 44px; }
    #CustomCoverArtConfigPage .ccaRange { height: 28px; }
    #CustomCoverArtConfigPage input[type="color"] { width: 3em; height: 2.6em; }
    #CustomCoverArtConfigPage .ccaLayerBtn { min-width: 38px; min-height: 38px; }
    #CustomCoverArtConfigPage .ccaActions { position: sticky; bottom: 0; background: var(--theme-body-background, #101010); padding-top: .5em; }
}
```

Ensure the canvas has `touch-action: none` (set in Phase 1) so drag/pinch don't scroll the page, and that pinch-zoom of the background (Phase 1 Task 9) works on touch.

- [ ] **Step 3: Manual verification**

Build; load; narrow the browser to a phone width (or use device emulation). Confirm: cards collapse/expand on tap; buttons are comfortably tappable; the canvas is draggable without scrolling the page; apply/undo stay reachable (sticky).

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" build -c Release`
Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
git add Configuration/configPage.html
git commit -m "feat(phase4): mobile-friendly responsive layout, collapsible cards, touch targets"
```

---

## Task 3: Preview modes (aspect ratios + simulated client contexts)

**Files:**
- Modify: `Configuration/configPage.html`

**Interfaces:**
- Produces: a "Preview modes" toggle/section that renders the current `doc` into small framed thumbnails: (a) aspect-ratio variants (square, portrait poster, landscape, banner) and (b) simulated contexts — **home screen** row, **library grid** tile, **details page** hero. `function renderPreviewModes()` draws each from the client canvas (or fetches `document/preview` for fidelity).

> **Design:** Reuse the client renderer by drawing `doc` into offscreen canvases at each context's aspect (normalized coords mean the design re-flows correctly). Compose those thumbnails into simple mock chrome (a home row of tiles with the cover among placeholders; a grid of tiles; a details hero with a dark gradient and placeholder text). This is presentation-only and needs no server change. Provide a "Use server render" checkbox that swaps the offscreen client draw for a fetched authoritative PNG when the user wants exactness.

- [ ] **Step 1: Add the Preview-modes UI**

```html
<section class="ccaCard">
    <div class="ccaCardHead" data-i18n="card.previewModes">Preview modes</div>
    <label class="ccaCheck ccaCheckBlock"><input is="emby-checkbox" type="checkbox" id="ccaPreviewModes" /><span data-i18n="pm.show">Show in client contexts</span></label>
    <div id="ccaPreviewModesBody" style="display:none">
        <div class="ccaPmRow" id="ccaPmAspects"></div>
        <div class="ccaPmContext" id="ccaPmHome"><div class="ccaPmLabel" data-i18n="pm.home">Home screen</div><div class="ccaPmStage"></div></div>
        <div class="ccaPmContext" id="ccaPmGrid"><div class="ccaPmLabel" data-i18n="pm.grid">Library grid</div><div class="ccaPmStage"></div></div>
        <div class="ccaPmContext" id="ccaPmDetails"><div class="ccaPmLabel" data-i18n="pm.details">Details page</div><div class="ccaPmStage"></div></div>
    </div>
</section>
```

Add i18n keys `card.previewModes`, `pm.show`, `pm.home`, `pm.grid`, `pm.details` (en + nl), and `.ccaPmRow`/`.ccaPmContext`/`.ccaPmStage`/`.ccaPmLabel` CSS for the mock chrome.

- [ ] **Step 2: Render the modes from the document**

```javascript
function renderDocToCanvas(targetW, targetH) {
    // Draw doc into an offscreen canvas at the given aspect (normalized coords reflow).
    var oc = document.createElement('canvas'); oc.width = targetW; oc.height = targetH;
    var saveCanvas = doc.Canvas; // temporarily retarget the renderer at this size
    doc.Canvas = Object.assign({}, doc.Canvas, { Width: targetW, Height: targetH });
    renderDocumentInto(oc.getContext('2d'), targetW, targetH); // extract renderDocument's body to accept ctx+W+H
    doc.Canvas = saveCanvas;
    return oc;
}
function renderPreviewModes() {
    if (!el('ccaPreviewModes').checked) { return; }
    var aspects = [[300,300],[220,330],[400,225],[480,135]];
    var row = el('ccaPmAspects'); row.innerHTML = '';
    aspects.forEach(function (a) { row.appendChild(renderDocToCanvas(a[0], a[1])); });
    // Home: cover tile among 4 placeholder tiles
    fillContext('#ccaPmHome', renderDocToCanvas(300,169), 'home');
    fillContext('#ccaPmGrid', renderDocToCanvas(200,300), 'grid');
    fillContext('#ccaPmDetails', renderDocToCanvas(640,360), 'details');
}
```

Refactor `renderDocument` to delegate its drawing to `renderDocumentInto(ctx, W, H)` so preview modes can reuse it at arbitrary sizes. `fillContext(sel, coverCanvas, kind)` builds the mock chrome (placeholder tiles / hero text) around the cover.

- [ ] **Step 3: Wire the toggle + refresh**

Toggling `ccaPreviewModes` shows the body and calls `renderPreviewModes()`. Re-run it (debounced) when `doc` changes (hook into `commit`/`scheduleRender`) but only while the section is open, to keep it cheap.

- [ ] **Step 4: Manual verification**

Build; load; enable Preview modes. Confirm the current design appears correctly framed at each aspect ratio and inside the home/grid/details mocks, and updates as you edit.

Run: `& "$env:USERPROFILE\.dotnet\dotnet.exe" build -c Release`
Expected: 0 errors.

- [ ] **Step 5: Commit + version + docs**

Set `<Version>3.3.0.0</Version>`; CHANGELOG + README ("Undo/redo (Ctrl+Z / Ctrl+Y), a much more mobile-friendly editor with collapsible sections and larger touch targets, and preview modes that show your cover at common sizes and inside simulated home / grid / details views.").

```bash
git add Configuration/configPage.html CustomCoverArt.csproj CHANGELOG.md README.md
git commit -m "feat(phase4): preview modes, v3.3.0.0"
```

---

## Self-Review (run after all tasks)

- **Spec coverage:** full undo/redo over the whole design + Ctrl+Z/Y + buttons (Task 1) · mobile-friendly responsive layout, larger touch targets, collapsible sections (Task 2) · preview modes at common aspect ratios AND simulated client views home/grid/details (Task 3) · background drag/zoom/pan already delivered in Phase 1 (noted, no dup).
- **Regression guard:** undo/redo restores must not desync the layers panel or selection (covered by `restoreSnapshot` re-selecting a valid layer).
- **Keyboard safety:** shortcuts are ignored while typing in inputs (Task 1 Step 3) so native text undo is preserved.
- **Placeholder scan:** the `renderDocumentInto` refactor is called out explicitly; land it as the first sub-step of Task 3 so preview modes and the main render share one code path.
