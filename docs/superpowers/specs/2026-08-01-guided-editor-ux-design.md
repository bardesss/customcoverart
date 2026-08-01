# Guided Editor UX + Phase 4 Polish — Design

**Status:** approved (2026-08-01), not yet planned or implemented
**Target release:** v3.3.0.0
**Supersedes:** `docs/superpowers/plans/2026-07-28-phase4-ui-polish.md` (its three tasks are folded in; see [Relationship to Phase 4](#relationship-to-phase-4))

## Problem

After Phases 1–3 the configuration page is nine cards and roughly 117 controls, all
rendered at once in a single scrolling column. Every control is visible whether or not it
is relevant to what the user is doing, and there is no indication of where to start. The
user's words: *"it's very overwhelming."*

The page also grew organically — Gradient is a checkbox inside the Background card while
"Background source" is a separate dropdown, so "what is my background?" has two
overlapping answers.

## Goals

1. Give the page an obvious order: choose a target, then a background, then text, then
   effects, then output — without locking anyone into that order.
2. Reduce what is on screen at any moment, so a step shows the handful of controls most
   people want and hides the rest one level down.
3. Work properly on a phone. Not "reflows without breaking" — genuinely usable, including
   the canvas, the layers panel and the poster browser.
4. Absorb the remaining Phase 4 work (undo/redo, preview modes) rather than building
   collapsible cards twice.

## Non-goals

- No rewrite of the config page. The markup is regrouped in place; element IDs, handlers
  and the canvas engine are preserved.
- No change to the render pipeline, the `CoverDocument` layer model, or any endpoint.
  The one model change is the background-source consolidation below.
- No new server-side work beyond that migration.

## Decisions

| Decision | Choice | Rationale |
|---|---|---|
| Layout | **Accordion**, one step open at a time | Closest to the current page, so the smallest change; steps are jumpable, so tweaking costs one click |
| Ordering | Numbered but **never locked** | The user designs from scratch and tweaks existing covers about equally; a gated wizard taxes the second case |
| Density | **Essentials visible, Advanced collapsed** within each step | Five steps alone still leaves ~20 controls in Background |
| Templates | **Load in step ①, save in step ⑤** | A template is where a design comes *from*; saving is where it goes |
| Restructure method | **Regroup existing markup in place** | Preserves 117 IDs and their bound handlers; keeps the blast radius on markup and CSS |
| Mobile | **Cross-cutting requirement**, not a task | Per-component work is needed in the canvas, layers panel and modals that a CSS-only pass would miss |

## Step structure

The canvas stays pinned in the right column (top on mobile) across every step — it is the
object being edited, not a step.

### ① Target & start

| | Controls |
|---|---|
| Essentials | target type, target select, start-from-template, restore original |
| Advanced | — |

### ② Background

Essentials are **source-dependent**: the source selector is always shown, followed by that
source's own controls. A control that belongs to an unchosen source is not shown at all —
it is not merely "advanced".

| Source | Essentials shown with it |
|---|---|
| Upload | choose-image button, file name, image fit, dimming |
| Library poster | browse-posters button, selected name, image fit, dimming |
| Poster collage | collage density, shuffle, dimming |
| Gradient | gradient type, angle, colour stops, add-stop |
| Solid colour | base colour |

| | Controls |
|---|---|
| Advanced (all sources) | blur, base colour when not already essential, gradient centre/radius |

`ccaBgAdjust` ("Reposition background") stays beside the canvas. It is a canvas mode, not
a background property, and belongs where the dragging happens.

### ③ Text & logos

| | Controls |
|---|---|
| Essentials | layers list, add text, add logo, content, size, colour, alignment |
| Advanced | font weight, shadow, outline, custom font, opacity, rotation |

### ④ Effects

| | Controls |
|---|---|
| Essentials | Jellyfin preset, the four effect toggles and each one's strength, auto palette |
| Advanced | vignette softness, border corner radius, double line and gap, effect colours |

### ⑤ Output & apply

| | Controls |
|---|---|
| Essentials | dimensions preset, format, preview-in-context, Apply, Download |
| Advanced | custom width/height, animation settings, save as template, batch apply |

## Background source consolidation

Today `Background.Source` is `upload | poster | collage | none` while `Gradient.IsEnabled`
is a separate boolean. Two controls answer one question.

**New:** `Source` becomes a single choice of `upload | poster | collage | gradient | solid`,
and each source reveals only its own controls.

### Migration

Applied in `DocumentMigration.Normalize` (server) and a new `normalizeBackgroundSource(d)`
on the client, called from the same places `normalizeEffects(d)` already is (template load
and document init), so saved templates keep working:

| Existing document | New `Source` |
|---|---|
| `Source == "collage"` | `collage` |
| `ImagePath` is non-empty | `upload` |
| `Gradient.IsEnabled == true` | `gradient` |
| anything else (incl. `"none"`) | `solid` |

`upload` and `poster` both resolve to `upload`: a browsed poster is copied into the
uploads directory, so after selection the two are indistinguishable in the document. The
UI keeps both buttons as separate ways to *pick* an image.

**Back-compat both ways:** whenever the client writes `Source`, it also writes
`Gradient.IsEnabled = (Source === 'gradient')`. An older server (or a rolled-back plugin)
reading the document still renders correctly.

Renderers switch on `Source`: `gradient` draws the gradient, `solid` fills `DimColor`, and
the image paths are unchanged. `CreateGradientBackground`'s current
"gradient-if-enabled-else-fill" becomes that switch.

## Step controller

**DOM contract.** Each step is:

```html
<section class="ccaStep" data-step="2">
  <button class="ccaStepHead" aria-expanded="false" aria-controls="ccaStepBody2">…</button>
  <div class="ccaStepBody" id="ccaStepBody2" hidden>…</div>
</section>
```

`.ccaStepBody` is touched **only** by the step controller. This matters: several existing
functions (`updateEffectsVisibility`, `updateCollageVisibility`, `updateAnimVisibility`,
the `.ccaTextOnly`/`.ccaImageOnly` toggles) already write `style.display` on elements
*inside* a step. Collapsing a step must not write to anything those functions also own, or
the two layers of hiding will fight and a control will come back visible inside a
collapsed step.

**Behaviour.**

- One step open at a time; opening one closes the others.
- Clicking an open header collapses it — all-collapsed is a valid state.
- On load: step ① when no target is selected, otherwise the last step used, read from
  `localStorage`. Falls back to ① if `localStorage` is unavailable or holds a step id that
  no longer exists.
- Expanding or collapsing is navigation, not an edit: it must not push undo history and
  must not re-render the canvas.
- Advanced disclosures use the same pattern (`<button aria-expanded>` plus a body), nested
  one level inside a step body, with their own open/closed state per step (not persisted).

**Accessibility.** Headers are real `<button>` elements, reachable by keyboard and
toggled by Enter/Space, with `aria-expanded` and `aria-controls`. The collapse animation
is suppressed under `prefers-reduced-motion`.

## Mobile

A first-class requirement across every component, verified at **360 × 640** (the narrow
end of common phones). Acceptance criteria:

- No horizontal page scroll at 360px. Wide content (the layers list, the preview-context
  strip) scrolls inside its own container.
- Every interactive target is at least **44 × 44 CSS px**.
- The canvas is usable by touch for select, drag, resize, rotate and background pan/zoom.

Per-component work:

| Component | Mobile work |
|---|---|
| Layout | Canvas moves above the accordion and becomes sticky at reduced height; steps go full width |
| Canvas handles | **Handle size and hit tolerance must be computed from the canvas's CSS display size, not its backing store.** Today `handleSize(H)` derives from the backing-store height, so on a phone — where a 1400px canvas displays at ~340px — the handles render proportionally and end up a few screen pixels across. They need a screen-space minimum, converted back into canvas units for drawing. |
| Canvas hit-testing | Touch needs a larger tolerance than a mouse pointer; derive it from `PointerEvent.pointerType` |
| Layers panel | Each row currently packs five ~30px buttons (👁 ▲ ▼ ⧉ ✕) plus the name. On narrow screens the row becomes tap-to-select with the actions behind a single overflow control, so the primary action (select) gets the whole row |
| Poster browser | Full-screen on mobile, two-column grid, sticky search and close; dismissible without hover |
| Sliders / colour inputs | Minimum height and thumb size for touch |
| Apply | Sticky bottom bar, so it is reachable without scrolling to the end |
| Preview-in-context | Horizontal scroll container rather than wrapping |
| Swatches | Meet the 44px minimum (currently 2em) |

## Undo/redo

Folded in from Phase 4 Task 1, unchanged in substance:

- Snapshot stack of the JSON-serialised `doc`, capped at 50 entries.
- Bursts of slider input collapse into one entry via a debounced `commit()` (400 ms);
  discrete actions (add/delete/duplicate/reorder a layer, apply preset, load template)
  snapshot immediately.
- Ctrl+Z / Ctrl+Y, ignored when focus is in an `<input>`, `<textarea>` or
  `contenteditable` so browser text-undo still works there.
- **Toolbar lives beside the canvas**, not inside a step, so it is reachable from every
  step — a change from the Phase 4 plan, which placed it in the preview card header before
  steps existed.
- Step navigation does not push history.

## Preview modes

Folded in from Phase 4 Task 3, unchanged: the same `doc` is drawn into offscreen canvases
at each context's aspect ratio and composed into simple mock chrome (home row, library
grid, details hero). Presentation-only, no server change, with a "use server render"
checkbox to swap in the authoritative PNG. Lands in step ⑤ essentials.

## Testing

The page is DOM and CSS, so most of this is manual. Three automated guards are worth
having, all built on the pattern established in Phase 3 — reading the embedded
`configPage.html` out of the assembly manifest and asserting against it:

1. **No control lost in the move.** Pin the current set of `cca*` element IDs and assert
   every one still exists after the restructure. This is the main risk of regrouping
   markup in place, and it is cheap to guard.
2. **Step structure is well-formed.** Every `.ccaStep` has exactly one `.ccaStepHead` and
   one `.ccaStepBody`, with `aria-controls` matching the body's id.
3. **en/nl completeness.** Already in place from Phase 3 — it will cover the new step
   labels automatically, with no extra work.

Server-side, the background-source migration gets normal xUnit coverage: each row of the
migration table above, plus a round-trip asserting `Gradient.IsEnabled` stays in sync with
`Source`.

Manual verification: at 360 × 640 and on a desktop width, walk all five steps, confirm
every advanced disclosure opens, and confirm the canvas still selects, drags, resizes and
rotates by touch.

## Relationship to Phase 4

The existing Phase 4 plan is superseded by this design:

| Phase 4 task | Fate |
|---|---|
| Task 1 — Undo/redo | **Folded in**, toolbar relocated beside the canvas |
| Task 2 — Mobile + collapsible cards + touch targets | **Collapsible cards superseded** by the accordion; mobile and touch-target work absorbed and expanded into the cross-cutting requirement above |
| Task 3 — Preview modes | **Folded in** unchanged, lands in step ⑤ |

Building Phase 4's collapsible cards first and then replacing them with the accordion
would be wasted work, which is why the two are merged.

## Risks

| Risk | Mitigation |
|---|---|
| A control is silently lost while regrouping markup | The element-ID pin test (Testing #1) |
| Step collapse fights the existing per-control visibility logic | The `.ccaStepBody` ownership rule; nothing else writes to it |
| Something used often ends up buried under Advanced | The split is specified per step above and is cheap to adjust after the user tries it |
| Background-source migration changes how an existing saved design renders | Migration table covered by tests; `Gradient.IsEnabled` kept in sync for two-way compatibility |
| Mobile canvas handles remain unusable | Called out explicitly as a screen-space conversion, not a CSS tweak |

## Out of scope

- Rewriting the config page.
- Any change to layer rendering, effects or the document schema beyond `Background.Source`.
- New endpoints.
- Removing the dead `#ccaPreviewSpinner` is *in* scope as incidental cleanup (unused since
  Phase 1); the other deferred Phase 1 minors (unused `_imageProcessingService` field,
  hard-coded `'Noto Sans'` in hit-testing, client/server text-effect draw order) are not.
