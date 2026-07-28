# Custom Cover Art — v2.0.0.0 Feature Design

**Date:** 2026-07-27
**Target version:** `2.0.0.0` (single big release, one PR)
**Status:** Approved for planning

## Summary

Four new features for the Custom Cover Art Jellyfin plugin, shipped together as
v2.0.0.0 in a single PR. Each is buildable and reviewable independently but
shares new model plumbing, endpoints, and config-page controls.

1. **Restore original cover** — finish the existing (unexposed) backup/restore code.
2. **Poster-collage background** — a full-bleed grid mosaic built from the target's own items.
3. **Design templates + batch apply** — save a look, apply it to many targets at once, auto-titled per target.
4. **Animated GIF export** — animated-background passthrough plus an optional Ken Burns pan/zoom.

## Shared plumbing

- **New models** (`Models/CoverArtModels.cs`): `BackgroundSource` enum
  (`Upload` / `LibraryPoster` / `Collage`), `CollageSettings`, `AnimationSettings`,
  `SavedTemplate`, `BatchApplyRequest`.
- **Config persistence**: `PluginConfiguration` gains `List<SavedTemplate> Templates`
  (persists across sessions; admin-scoped).
- **Config page**: new controls added following the existing card pattern in the
  inline `configPage.html`.
- **Localization**: new strings added to `Resources/en.json`, `Resources/nl.json`,
  and the config page's inline `I18N` object (kept in sync).
- **New endpoints** on `CustomCoverArtController` (all `RequiresElevation`):
  `POST /CustomCoverArt/targets/{type}/{id}/restore`,
  `POST /CustomCoverArt/batchApply`,
  plus collage-preview support on the existing preview path.

---

## Feature 1 — Restore original cover

**Goal:** a safe undo that returns a target to its true pre-plugin primary image.

**Behavior:**
- On **any** Apply (single or batch), before the new primary image is set, the
  target's current primary image is backed up — **once**. Subsequent applies do
  **not** overwrite that first backup, so Restore always returns to the true
  pre-plugin original, never the previous plugin cover.
- A backup is considered to exist per target; the existence gates the UI button.
- Backups live under `…/customcoverart/backups/{targetId}/`.

**Implementation notes:**
- Reuse existing `LibraryDetectionService.BackupCurrentCoverArtAsync` /
  `RestoreCoverArtAsync`, adjusted to be original-preserving (no overwrite if a
  backup already exists for the target).
- Restore path: locate backup → `item.SetImage(Primary)` → `UpdateToRepositoryAsync(ImageUpdate)`,
  mirroring the existing apply path.

**UI:**
- A "Restore original cover" button on the target card, enabled only when a
  backup exists for the selected target.

**Endpoint:** `POST /CustomCoverArt/targets/{type}/{id}/restore`.

---

## Feature 2 — Poster-collage background

**Goal:** generate a background from the target's own item posters, with no manual
asset selection.

**Behavior:**
- The Background card gains a **source** choice: *Upload* / *Library poster* /
  **Poster collage from this target's items**.
- The server fetches the target's items' primary posters (library, collection, or
  playlist), shuffles them, and tiles them into a **full-bleed grid mosaic** sized
  to the export dimensions at 2:3 poster aspect.
- The collage becomes the background and flows through the existing
  blur / dim / gradient / text pipeline unchanged.

**Controls:**
- **Density**: sparse / medium / dense presets (controls grid column count).
- **Shuffle**: re-roll the poster selection/arrangement in the live preview.

**Edge cases:**
- **Live TV** (no item posters): collage option disabled with an explanatory note.
- **Zero items / zero posters**: fall back to the plain dim color background.
- Too few posters for the grid: repeat posters to fill.
- Fetched posters are cached per target for the duration of the editing session to
  keep the live preview fast.

**Implementation notes:**
- New `CollageComposer` helper keeps `CoverArtService` focused; it reuses
  `MediaItemService` for item/poster fetches.
- Grid math: given export W×H and density, compute columns/rows at 2:3 tile aspect
  to fill the frame; center-crop tiles to avoid gaps.

---

## Feature 3 — Design templates + batch apply

**Goal:** apply one design to many targets in a single action, with correct
per-target titles.

**Templates:**
- A template stores the **full design except title and target**. Reusing one
  template across libraries therefore yields correct per-library titles.
- Stored server-side in `PluginConfiguration.Templates` (persist across sessions).
- **UI**: "Save current design as template…" (prompts for a name), "Load template"
  dropdown (populates every control except title/target), "Delete template".

**Batch apply:**
- A multi-select checklist of targets (grouped by type) + a template choice
  (a saved template or "current design") → **Apply to all**.
- Each cover's title auto-fills from that target's own name.
- Each target is auto-backed-up first (Feature 1) before its new cover is applied.
- A per-target success/fail report is shown after the run.

**Endpoint:** `POST /CustomCoverArt/batchApply`
`{ template? | inlineSettings?, targetIds[] }` → per-target result list.

---

## Feature 4 — Animated GIF export

**Goal:** make the "GIF" output option produce a real animated cover, without the
jank of animating the collage.

**Behavior:**
- The Output card's format options gain **Animated GIF**. Selecting it reveals
  animation controls.
- **Two motion sources:**
  - **Background-GIF passthrough** — if the background is itself an animated GIF,
    its frames animate under the static text/dim/gradient overlays. Auto-detected.
  - **Ken Burns** — optional slow pan/zoom of the background (upload, library
    poster, or collage) behind static overlays. Controls: zoom amount, direction,
    duration / frame count, loop.
- Apply sets the animated GIF as the target's primary image; it animates only in
  the Jellyfin views that render GIFs.

**Implementation notes:**
- Per frame: transform the background (Nth source-GIF frame, or a pan/zoom crop for
  Ken Burns), then composite the static overlays; encode via ImageSharp's GIF
  encoder with per-frame delays and the loop flag.
- **Bounds:** frame count capped (≈30) and dimensions capped to keep file size
  sane; heavier rate-limiting on animated generation; a size note in the UI.

---

## Architecture & testing

**New / changed code:**
- `Models/CoverArtModels.cs` — new models/enums listed above.
- `Configuration/PluginConfiguration.cs` — `Templates` list.
- `Services/` — new `CollageComposer` helper; animation/frame builder in the render
  path; `LibraryDetectionService` restore made original-preserving.
- `Controllers/CustomCoverArtController.cs` — `restore`, `batchApply`, collage
  preview.
- `Configuration/configPage.html` — background-source choice, collage density +
  shuffle, template card, batch-apply section, animation controls; matching `I18N`.
- `Resources/en.json`, `Resources/nl.json` — new strings.

**Tests:**
- Collage grid math (columns/rows for given dimensions + density; too-few-posters fill).
- Template serialize/deserialize round-trip (title/target excluded).
- GIF frame generation (frame count honored, loop flag set, Ken Burns crop progression).
- Backup idempotency (first backup preserved across repeated applies) and restore correctness.

**Versioning:**
- Bump `<Version>` in `CustomCoverArt.csproj` to `2.0.0.0`.
- CHANGELOG entry for v2.0.0.0 covering all four features.
- Single PR.

## Out of scope

- Animated collage (posters drifting/scrolling) — explicitly excluded to avoid jank.
- Additional languages beyond en/nl.
- New target types (individual items, genres, tags) and scheduled auto-apply.
