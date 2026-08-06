# Gradient overlay — design

**Date:** 2026-08-06
**Status:** approved, ready for an implementation plan
**Target release:** v3.4.0.0

## Problem

A cover today can be dimmed uniformly, tinted uniformly, or darkened at the corners. It
cannot fade a colour in from one edge. That single missing primitive is what produces the
common streaming-service look: a poster whose lower third resolves into solid colour, with
the title sitting on top of it and staying legible regardless of what the poster shows
underneath.

Three facts about the current code explain why it is missing:

1. Gradients are background-*only* and mutually exclusive with an image.
   `ComposeDocumentFrame` (`Services/DocumentRenderer.cs:23-32`) picks one or the other:
   when a background image loaded, `CreateGradientBackground` never runs. The client
   mirrors that precedence in `paintBackgroundOnly` (`Configuration/configPage.html:1488-1498`).
2. The only overlays that composite *over* an image are flat — `Dim`
   (`DocumentRenderer.cs:210-214`) and `SoftLight` (`Services/EffectsComposer.cs:35-46`).
3. The only effect that ramps from transparent to a colour is `Vignette`
   (`EffectsComposer.cs:49-84`), and it is radial, centre-out.

## Solution

A **gradient overlay**: a multi-stop linear gradient with per-stop alpha, composited over
the finished background and under the text and logo layers.

### Guiding decisions

| Decision | Choice | Why |
|---|---|---|
| Editor location | Step ② Background, below Dimming | Users reach for this as "tint my poster", not as an effect; it belongs beside the other overlay control |
| Colour model | Reuse `GradientSettings`, add per-stop alpha | Multi-stop and angle come free, and the stop-editor UI, `gradientStops()` and `BuildColorStops` are all reused rather than duplicated |
| Compositing slot | After soft-light, immediately before the layers | Applies to all four background sources; its colour is authoritative; it is the last thing under the text, which is the legibility contract |
| Geometry | Linear only | Radial would overlap the existing Vignette effect for no new capability |
| Dimming | Unchanged, kept | Folding it in would be a breaking UI change and a real migration for every document using `Dim` |

## 1. Data model

Two additive changes.

```csharp
// Models/CoverArtModels.cs — GradientStop
/// <summary>Per-stop opacity, 0..1. Defaults to 1 so every gradient written before
/// overlays existed renders byte-for-byte as it always did.</summary>
public float Alpha { get; set; } = 1f;
```

```csharp
// Models/CoverDocument.cs — BackgroundLayer
/// <summary>Optional colour gradient composited over the finished background,
/// under the layers. Null means no overlay.</summary>
public GradientSettings? Overlay { get; set; }
```

The overlay reuses `GradientSettings` rather than introducing a parallel type: it already
carries `IsEnabled`, `Angle` and `Stops`.

**Inert fields.** `Type`, `CenterX`, `CenterY` and `Radius` come along with the reused type
but are **not honoured on an overlay** — it always renders linear. The UI never writes
them. This is deliberate, not an oversight, and a test locks the contract so a future
reader does not "fix" it.

**Fallback divergence.** `BuildColorStops` (`DocumentRenderer.cs:333-348`) falls back to
`StartColor`/`EndColor` when there are fewer than two stops. The overlay must **not**
inherit that: falling back to an opaque black→white ramp would obliterate the poster.
Fewer than two stops means "off".

**Angle convention.** The existing linear brush places angle 90° as top→bottom
(`DocumentRenderer.cs:314-322`), so stop 0 is the top edge. A bottom fade is therefore
angle 90 with stop 0 at alpha 0 and the last stop opaque. Default `Angle` for a new
overlay is 90.

## 2. Server render

### Shared brush geometry

Extract the linear/radial brush construction out of `ApplyGradientBackground` into:

```csharp
internal static Brush CreateGradientBrush(GradientSettings g, int width, int height, bool forceLinear = false)
```

`ApplyGradientBackground` and `ApplyGradientOverlay` both call it, so the two paths cannot
drift. The overlay passes `forceLinear: true`.

### Stop alpha

`BuildColorStops` applies the stop alpha:

```csharp
SafeColor(s.Color, Color.Gray).WithAlpha(Math.Clamp(s.Alpha, 0f, 1f))
```

`WithAlpha` is already used in this file (`DocumentRenderer.cs:387`), so no new API
surface. With `Alpha` defaulting to `1f`, every existing background gradient is unchanged.

### The overlay itself

```csharp
internal static void ApplyGradientOverlay(Image<Rgba32> canvas, GradientSettings? overlay)
{
    if (overlay is null || !overlay.IsEnabled) { return; }

    // No Start/End fallback here — see the fallback divergence note in the spec.
    if (overlay.Stops is not { Count: >= 2 }) { return; }

    var brush = CreateGradientBrush(overlay, canvas.Width, canvas.Height, forceLinear: true);

    // Render into its own TRANSPARENT Rgba32 buffer, then composite. Filling the canvas
    // directly with a semi-transparent brush is the trap that once blacked out dimmed
    // backgrounds (see ApplyBackgroundLayer) — Fill ignores brush alpha on alpha-less
    // pixel formats. This buffer is explicitly Rgba32, and SrcOver onto a zero-alpha
    // destination resolves exactly to the source, so no custom GraphicsOptions are needed.
    using var scrim = new Image<Rgba32>(canvas.Width, canvas.Height);
    scrim.Mutate(x => x.Fill(brush));
    canvas.Mutate(x => x.DrawImage(scrim, Point.Empty, 1f));
}
```

Called from `ComposeDocumentFrame` immediately after `ApplySoftLight` and before the layer
loop:

```
background  (image | collage | gradient | solid)
  └ dim                     image path only, unchanged
ApplySoftLight
ApplyGradientOverlay        ← new
layers  (text, logos)
vignette → grain → border
```

Placing it in `ComposeDocumentFrame` rather than inside `ApplyBackgroundLayer` is what
makes it work for all four sources. `ApplyBackgroundLayer` runs only on the image path;
putting the overlay there would silently do nothing for gradient, solid and collage
backgrounds — repeating exactly the asymmetry the client comment at
`configPage.html:1500-1505` already documents for `Dim`.

**Animation comes free.** `CoverArtService.ComposeFrame` (`Services/CoverArtService.cs:320-323`)
delegates to `ComposeDocumentFrame`, and the animated-GIF path calls it per frame.

## 3. Client mirror

All in `Configuration/configPage.html`.

| Function | Change |
|---|---|
| `hexToRgba` (line 1521) | Widen from 3/6-digit to also accept 8-digit hex; multiply any parsed alpha with the stop's `Alpha` |
| `gradientStops` (line 1830) | Alpha-aware path returning `rgba(...)` strings |
| `drawGradientOverlay` | New. Called from `renderDocument` between `drawSoftLight` and the layer loop — the same slot as the server |
| `paintBackgroundOnly` (line 1488) | **Unchanged — deliberately excludes the overlay** |

`paintBackgroundOnly` feeds the auto-palette sampler. Including the overlay would make the
palette sample the colour the user just applied and feed itself, collapsing the swatches
toward the overlay colour on every edit.

`drawVignette` (lines 1555-1571) is the structural template for `drawGradientOverlay` —
swap `createRadialGradient` for `createLinearGradient` and take the stops from the document
instead of a single colour and amount.

### The stop editor is shared by parameterisation, not by copying

The four existing stop-editor functions — `collectGradientStops` (2931), `addGradientStop`
(2942), `rebuildGradientStopsUI` (2548) and `syncGradientStopsToDoc` (2542) — are hard-wired
to the `#ccaGradientStops` container and to `doc.Background.Gradient`. They must be
parameterised by container element and document target so the background gradient and the
overlay can each have their own instance. Copying them would guarantee the two drift.

**One trap this creates.** `collectGradientStops` reads the position slider with
`row.querySelector('input[type="range"]')` (line 2936). Adding a per-row alpha slider puts
a second range input in the row, and that selector would then silently return whichever
comes first. It must become `row.querySelector('.ccaStopPos')` — the class already exists
(line 2955) — with the alpha slider given its own `.ccaStopAlpha`. This change touches the
existing background-gradient path, so tests 6 and the manual parity check must both cover
a plain background gradient after the refactor, not just the new overlay.

Alpha rows are added to the overlay editor only. The background-gradient editor keeps two
inputs per row; its stops keep `Alpha` at the default 1.

## 4. UI — step ② Background

Sits directly below Dimming, shown for all four background sources.

| | Controls |
|---|---|
| Essentials | overlay toggle, preset dropdown, stop list (colour · position · alpha), add/remove stop |
| Advanced | angle |

### Presets

A static table of stop arrays. Selecting one replaces the stop list; hand-editing any stop
switches the dropdown to *Custom*.

**Presets set positions and alphas only — never colours.** Applying one keeps the colour of
each existing stop, matched by index. Stops the preset adds beyond the ones already there
take the last existing stop's colour, falling back to `Background.DimColor` when the overlay
has no stops yet. This is what makes preset-switching non-destructive: a user who has picked
their colour can try every preset without losing it.

| Preset | Stops (position / alpha) | Angle |
|---|---|---|
| Custom | — | — |
| Bottom fade | 0 / 0 · 0.45 / 0 · 1 / 0.9 | 90 |
| Top fade | 0 / 0.9 · 0.55 / 0 · 1 / 0 | 90 |
| Full wash | 0 / 0.35 · 1 / 0.85 | 90 |
| Duotone | 0 / 0.7 · 1 / 0.9 | 90 |

Duotone differs from Full wash only in that it is the preset users are expected to give two
different stop colours; the stop values themselves carry no colour.

### Units

`Alpha` and `Position` are stored `0..1` in the document, matching every other normalized
value in the model. The UI presents both as whole percentages and converts at the control
boundary, as the existing gradient stop editor already does for `Position`.

### Palette wiring

`applySwatch` (line 1803) gains a middle branch. New precedence:

1. the selected text layer, if one is selected
2. **the selected overlay stop, when the overlay is on**
3. the soft-light colour, when it is on
4. otherwise no-op, leaving the design alone (unchanged behaviour)

The stop editor tracks a selected-stop index, set on focus of any input in that row and
defaulting to the last stop — the opaque end is the one users actually recolour.

### Localisation

New control labels get keys in both `Resources/en.json` and `Resources/nl.json`.
`ConfigPageStructureTests` already asserts en/nl completeness.

## 5. Migration & back-compat

- **New documents.** `Overlay` absent or null means disabled. The render path is null-safe
  by construction, so `DocumentMigration.Normalize` (`Services/DocumentMigration.cs:72-96`)
  needs no `??=` for it — unlike the effect sub-objects, which `EffectsComposer`
  dereferences unconditionally.
- **Existing documents and saved templates.** Unchanged output, guaranteed by
  `GradientStop.Alpha` defaulting to `1f`. A regression test asserts this.
- **Forward compat.** An older plugin reading a new document ignores `Overlay` and renders
  without it. Unlike the `Source` / `Gradient.IsEnabled` consolidation there is no legacy
  field to keep in sync, so no write-both-ways rule is needed.

## 6. Testing

New `tests/CustomCoverArt.Tests/GradientOverlayTests.cs`:

1. Null or disabled overlay → canvas byte-identical to the no-overlay render.
2. Fewer than two stops → no-op. Guards the fallback divergence in §1.
3. Flat white canvas, alpha 0→1 at 90°: top row ≈ white, bottom row ≈ overlay colour, and
   **the middle row a true blend of the two**. This is the regression guard for the
   Fill-ignores-brush-alpha trap and the single most important test in the file.
4. Parameterised over all four `Source` values — the guard against the image-path-only
   asymmetry rejected in §2.
5. An overlay with `Type = Radial` still renders linear. Locks the inert-fields contract.
6. Existing background gradients unchanged when `Alpha` defaults to 1.
7. Ordering: the overlay sits over soft-light and under text — a text pixel keeps its colour.

`ConfigPageStructureTests` gains the new control ids and the en/nl key assertions.

The client canvas has no automated test; parity with the server render is verified
manually, as is already the practice in this repo.

## 7. Risks

| Risk | Mitigation |
|---|---|
| ImageSharp `Fill` mishandling brush alpha | The separate transparent `Rgba32` buffer plus `DrawImage`, which is the proven pattern here; test 3 fails loudly if the assumption is wrong |
| Client and server renders drifting | Both take stops from the same document fields and composite in the same slot |
| The stop-editor refactor regressing the existing background gradient | It is a parameterisation, not a rewrite; the `.ccaStopPos` selector change is the one behavioural edit, and test 6 plus a manual check of a plain gradient background cover it |
| Step ② becoming crowded | Only the toggle, preset and stop list are essentials; angle is behind Advanced |

## 8. Out of scope

Per-layer overlays. Blend modes beyond normal. Per-frame or animated overlay variation.
Radial overlays. Folding `Dim` into the overlay. Driving the overlay from the step ④
Effects preset — the cross-step coupling is harder to explain than it is worth.

## 9. Release

README and CHANGELOG updated in the same PR as the implementation, version bumped to
**v3.4.0.0**.
