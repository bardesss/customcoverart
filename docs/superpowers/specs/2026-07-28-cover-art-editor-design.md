# Custom Cover Art — Interactive Editor Overhaul Design

**Date:** 2026-07-28
**Target:** `3.0.0.0` and up, shipped as **four phased PRs/releases** under this one shared spec
**Status:** Approved for planning

## Summary

Turn Custom Cover Art from a flat, server-rendered form into an **interactive,
layered cover-art editor**, while keeping the current architecture, security
model, and coding style. Four feature groups (numbered per the user's request):

1. **Multiple text layers + logos/icons** — unlimited layers, each independently
   styled and positioned; upload-only PNG logos; a layers panel.
4. **Automatic color palette extraction** — 5–8 dominant swatches from the
   background, click to apply; fast, optional (client-side).
5. **More effects & composition** — border/frame, vignette, film grain,
   soft-light overlay, and a "Jellyfin-style" preset; all non-destructive,
   slider-driven.
7. **UI improvements + background positioning** — full undo/redo, mobile-friendly
   responsive UI, preview modes (aspect ratios + simulated client views), and
   drag/zoom/pan repositioning of the background.

Everything is built on one shared **`CoverDocument`** model and a
**dual-renderer** approach.

## Two pivotal decisions (settled)

- **Editor engine: client canvas + server parity.** An HTML5 Canvas editor in
  `configPage.html` renders the document live for instant interaction; the server
  ImageSharp pipeline renders the *same* document for the applied cover. The
  server is authoritative — Apply always shows the real server render before it
  commits. (Rejected: server-preview-only can't do live drag/zoom/pan; hybrid
  adds release flicker and doubles surface area.)
- **Delivery: four phased releases**, one shared spec. Each phase is its own PR
  and stays shippable; each gets live-server validation before the next builds on
  it. (Rejected: one big release — too much blast radius on a live plugin.)

## Additional settled decisions

- **Backward compatibility: migrate on load.** `CoverDocument` is a superset. Old
  flat `CoverArtSettings` (saved templates, the default design) auto-convert to a
  one-text-layer document; old-format apply keeps working via a shim. Nothing the
  user saved is lost.
- **Logos: upload-only.** Reuse the existing sandboxed upload endpoint +
  magic-byte validation; add PNG/transparency handling. No bundled icon library.
- **Palette: client-side.** The background is already loaded into the client
  canvas, so quantize its pixels in JS — instant, offline, no new endpoint or
  rate-limit surface. Exposed behind an "Auto palette" toggle (off by default).

---

## Architecture (the foundation)

### The document model — single source of truth

Today's flat `CoverArtSettings` is replaced (with a migration shim) by a
**`CoverDocument`**: one JSON object both renderers consume. Neither renderer
invents layout; both only draw what the document says.

```
CoverDocument {
  version: 2,
  canvas:      { width, height, format }              // export dims + png/gif
  background: {
    source:    "upload" | "poster" | "collage" | "none"
    imagePath, collage{...}, animation{...}            // existing plumbing reused
    fit:       "cover" | "contain" | "stretch"
    transform: { offsetX, offsetY, scale }             // NEW: drag/zoom/pan result
    blur, dim, dimColor, gradient{...}
  }
  effects: {                                           // NEW: non-destructive, ordered
    border{ color, thickness, radius, double, gap }
    vignette{ amount, softness }
    grain{ amount, seed }                              // seed => preview == render
    softLight{ color, opacity }
    preset: null | "jellyfin"
  }
  layers: [                                            // NEW: ordered, unlimited
    { id, type:"text",  visible, x, y, w?, rotation, opacity,
      content, font, size, weight, color, align,
      shadow{ enabled, color, blur, offsetX, offsetY },
      outline{ enabled, color, width } }
    { id, type:"image", visible, x, y, w, h, rotation, opacity,
      imagePath }                                      // logos/icons (upload-only)
  ]
}
```

**Coordinates are stored normalized (0–1)** against the canvas. A design therefore
looks identical whether the preview is 400px or the export is 1400px, and the same
document renders correctly at any aspect ratio (needed for preview modes).

### Two renderers, one contract

- **Client (HTML5 Canvas, `configPage.html`)** renders the document live for
  editing: instant drag, zoom/pan, layer selection, undo/redo. It is a WYSIWYG
  *proxy*, not the authority.
- **Server (ImageSharp, `CoverArtService`)** renders the *same* document for the
  applied cover and is **authoritative**. The Apply flow shows the server's real
  output before committing, so there is never a surprise between edit and apply.

### Keeping the two matched (the real work)

- **Shared, explicit draw order:** background → background effects (blur/dim/
  gradient) → composition effects (soft-light, vignette, grain) → layers in array
  order → border/frame last. Both renderers follow this order exactly.
- **Restrict to primitives both engines do faithfully:** solid fills, gradients,
  drop shadow, outline, gaussian blur, opacity, normalized transforms.
- **Deterministic grain:** the grain seed is stored in the document so each
  renderer reproduces the *same* noise on every render (stable preview, stable
  apply). Exact JS↔C# noise equality is not required — the server render is
  authoritative and shown before Apply; the seed just prevents the grain from
  shimmering between renders.
- **Identical fonts:** bundled Noto weights (+ optional uploaded font per text
  layer). The client loads the same Noto faces via `@font-face` from the served/
  embedded resources, so text metrics line up on both sides.
- **Truth on apply:** where a primitive can't match to the pixel (sub-pixel blur,
  font hinting), the client is "close enough" and the authoritative server render
  is shown before Apply commits.

### Security model — unchanged and reused

- Logo/background PNGs go through the **existing** sandboxed upload endpoint +
  magic-byte content validation into the plugin data dir.
- The server only honors image/font paths that resolve inside that dir
  (`PluginPaths.IsInsideBase`), exactly as today.
- All new endpoints keep `[Authorize(Policy = "RequiresElevation")]` and the
  existing rate limiting.
- Client-side palette extraction adds **no** new endpoint.
- Existing clamps/whitelists (output format, dimensions, decompression-bomb guard,
  effect-size clamps) are preserved and extended to the new fields.

### Backward compatibility

On load, a flat `CoverArtSettings` (old saved templates, the persisted default)
auto-migrates into a one-text-layer `CoverDocument`. The apply/template endpoints
accept both shapes during the transition via a shim, so nothing on a live server
breaks.

---

## Phase 1 — Canvas engine + document model + server parity

Foundation; **no new user-facing features**. Ships and is validated alone.

- Add `CoverDocument` models; migrate flat `CoverArtSettings` → one-text-layer
  document on load; back-compat apply shim.
- Refactor the server render path (`CoverArtService`/`ImageProcessingService`) to
  consume `CoverDocument`, honoring `background.transform` and normalized coords.
  The existing single-title output must render **pixel-comparable** to today.
- Client canvas engine in `configPage.html`: draws the document, renders the one
  migrated text layer, shows selection handles. Live preview switches from the
  server `<img>` to the client canvas; **Apply** still performs the authoritative
  server render and shows it before committing.
- **Background drag-to-reposition + wheel/pinch zoom-pan**, writing
  `background.transform`; the server honors it on render and apply. (This is part
  of feature group 7 but is foundational, so it lands here.)

**Tests:** migration round-trip (flat → document → apply); normalized→pixel
mapping; background-transform honored; parity check that the default design
renders comparably before/after the refactor.

**Version:** `3.0.0.0`.

## Phase 2 — Multiple text layers + logos/icons

- Unlimited **text layers**, each with its own content, font, size, weight, color,
  opacity, alignment, position, drop shadow, and outline. Drag to position.
- **Image (logo/icon) layers:** upload-only PNG via the existing sandboxed upload
  + magic-byte validation; freely positioned, resized via handles, opacity,
  rotation; transparency preserved.
- **Layers panel:** list with show/hide, reorder (drag or up/down), delete,
  duplicate, select. Selecting a layer focuses its property controls.
- Server render walks the layer array in draw order for both text and image
  layers.

**Tests:** multi-layer draw order; image-layer placement/opacity; layer op
(reorder/duplicate/delete) document integrity.

## Phase 3 — Effects + palette extraction

- **Effects** (non-destructive, slider-driven, implemented in both renderers):
  - Border/frame: color, thickness, corner radius, optional double border + gap.
  - Vignette: amount, softness.
  - Film grain: amount, seeded (deterministic).
  - Soft-light / overlay color: color, opacity.
- **Jellyfin preset:** one click populates the document with the dark-gradient +
  clean-white-text look mimicking the default library cover; fully editable after.
- **Auto palette:** client-side quantization of the current background → 5–8
  dominant swatches shown as clickable chips. Clicking applies the color to the
  **currently selected** text layer / gradient stop / soft-light overlay. An
  "Auto palette" toggle enables it (off by default = fast, optional).

**Tests:** per-effect render (border, vignette, grain-seed determinism,
soft-light); preset populates expected document; palette quantization is pure JS
(light unit / manual QA).

## Phase 4 — UI polish

> **Revised 2026-08-01.** Phase 4 grew into a guided-editor restructure after the user
> found the nine-card page overwhelming. See
> `docs/superpowers/specs/2026-08-01-guided-editor-ux-design.md`, which supersedes this
> section and the Phase 4 plan: the page becomes five numbered accordion steps, mobile
> becomes a cross-cutting requirement rather than a bullet, and `Background.Source`
> absorbs the separate gradient checkbox. Undo/redo and preview modes below are unchanged
> and fold into that work.

- **Undo/redo** over the whole document: snapshot history, `Ctrl+Z` / `Ctrl+Y`
  plus on-screen buttons.
- **Mobile-friendly:** responsive layout, larger touch targets, collapsible
  sections; canvas touch interactions (drag/pinch from Phase 1) confirmed on
  mobile.
- **Preview modes:** render the current cover at common aspect ratios and in
  simulated client contexts — **home screen**, **library grid**, **details page**
  — so the design can be judged in context before applying.

**Tests:** undo/redo history correctness (snapshot round-trip); responsive layout
smoke; preview-mode aspect rendering from the same document.

---

## Cross-cutting: code organization, docs, versioning

- **New/changed code:**
  - `Models/CoverArtModels.cs` — `CoverDocument` + nested models/enums; migration
    from `CoverArtSettings`.
  - `Services/` — render path refactored to consume `CoverDocument`; effects
    helpers kept in focused units (e.g. an effects composer separate from the
    frame composer) so no single file balloons.
  - `Controllers/CustomCoverArtController.cs` — endpoints accept the document
    shape (with the back-compat shim); reuse the existing upload endpoint for
    logos; all `RequiresElevation` + rate-limited.
  - `Configuration/configPage.html` — the client canvas engine, layers panel,
    effects controls, palette chips, undo/redo, responsive/collapsible layout,
    preview modes; inline `I18N` kept in sync.
  - `Configuration/PluginConfiguration.cs` — `Templates` now store documents
    (migrated on load).
- **Docs + localization:** README updated per phase; new strings added to
  `Resources/en.json`, `Resources/nl.json`, and the config page's inline `I18N`
  (en + nl kept in sync), matching the existing pattern.
- **Versioning:** Phase 1 = `3.0.0.0` (render pipeline changes shape); Phases 2–4
  increment from there. One CHANGELOG entry per phase. Auto-release on merge to
  main, as today. CI (`dotnet test`) stays green per PR.

## Out of scope (YAGNI)

- Bundled icon library (logos are upload-only).
- Server-side palette endpoint (client-side only).
- Languages beyond en/nl.
- Animated/video layers and per-layer animation (Ken Burns stays background-only,
  as today).
- New target types beyond those that already exist.
