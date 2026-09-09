# Changelog

The text under each version heading is published to the plugin's manifest by the
release workflow (and shown as the version's changelog inside Jellyfin). Add a
new `## <version>` section when you bump `<Version>` in the csproj.

## 4.0.1.0
Fixes the configuration page on Jellyfin 12, where **nothing worked**. Jellyfin 12 stopped accepting the older of its two ways of proving who you are, and the page was still using it — so every request it made was rejected before it reached the plugin. Libraries did not load, saved templates did not appear, the bundled fonts did not arrive, applying a cover did nothing, and opening the poster browser produced the cryptic message *"Failed to execute 'json' on 'Response': Unexpected end of JSON input"*, which was the rejection with no explanation attached. The page now identifies itself the same way the Jellyfin web client does, which works on Jellyfin 10.11 and 12 alike. **If you installed 4.0.0.0, update** — nothing was wrong with your settings or your covers, the page simply could not reach the server.

## 4.0.0.0
**Support for Jellyfin 12.** Jellyfin 12 moved to .NET 10 and changed its plugin ABI, so a plugin built for 10.11 cannot be built for both — this release is built against Jellyfin 12 and requires it. Nothing about designing or rendering covers has changed: your saved templates, applied covers and settings all carry over untouched. If you are **still on Jellyfin 10.11**, stay on **3.5.1.0** — your server will keep being offered that version and will not see this one, so nothing breaks by leaving it alone.

## 3.5.1.0
Fixes a small but confusing moment with **Live TV** targets. Live TV has no posters to build a mosaic from, so the **Poster collage** background is unavailable there — and if you had already chosen a collage and then switched the target to Live TV, the background silently changed back to **Image** without a word. The option is now greyed out with a line explaining why, in all three languages, so the change no longer happens behind your back. Nothing else about how covers render has changed.

## 3.5.0.0
Adds **Spanish**. The whole configuration page — every step, label, button, hint and message, plus the server's upload and validation errors — is now translated, joining English and Dutch. The page follows your Jellyfin display language, and regional variants come along for the ride: `es-MX` and `es-419` get Spanish just like `es-ES`. Alongside it, the translation files themselves were **cleaned up and put under test**. Roughly a hundred strings left behind by the v3 editor rewrite were still being carried (and would have had to be translated into every new language) — those are gone, and five new build-time checks now fail if a language drifts out of sync with English, if a `{0}` placeholder goes missing, or if a string is defined but never shown. No change to how covers look or render.

## 3.4.0.0
Adds an **overlay gradient** to the Background step — a colour that fades in over your background and sits under your text. It is the missing piece behind the look where a poster **resolves into a solid band of colour** at the bottom, with the title on top of it and readable no matter what the poster shows underneath. Each colour stop has its own **opacity**, so you can go from fully transparent to fully solid, use **two colours** for a duotone fade, or wash the whole cover. Four **presets** — Bottom fade, Top fade, Full wash and Duotone — get you there in one click, and switching between them keeps the colours you have already picked so you can try all four without losing your palette. The **Auto palette** swatches now recolour the selected overlay stop, so you can tint a cover with a colour taken straight out of its own artwork. The overlay works with every background type — image, poster collage, gradient or solid — and with animated covers. Existing designs and saved templates are unaffected and render exactly as before.

## 3.3.0.0
The configuration page is now a **guided walkthrough**. Instead of every control at once, designing a cover is laid out as five numbered steps — **Target**, **Background**, **Text & logos**, **Effects**, **Output** — with one open at a time and any of them one click away. Each step shows the controls most people need and keeps the rest under **Advanced**. Choosing a background is now a single choice — **image, poster collage, gradient or solid colour** — instead of a source dropdown plus a separate gradient tick that overlapped with it. The whole page is **properly usable on a phone**: bigger touch targets throughout, the preview pinned at the top while you work, a full-screen poster browser, and canvas handles you can actually grab (they used to shrink to a few pixels on a small screen). Two more additions: **undo and redo** across the whole design with Ctrl+Z / Ctrl+Y, and an **in-context preview** showing your cover wide, square and poster-shaped before you apply it. Existing designs and saved templates are unaffected and render exactly as before.

## 3.2.0.0
Adds a new **Effects** card. A **colour wash** tints the background under your text, a **vignette** darkens the edges to draw the eye inward, **film grain** adds texture, and a **border** frames the cover — square or rounded, single or double-lined. Every effect is off until you switch it on, and turning one back down to zero leaves your cover exactly as it was. One click of **Jellyfin style** lays down the familiar dark-gradient look with clean white text, which you can then edit freely. And **Auto palette** reads the dominant colours straight out of your background and offers them as swatches — click one to recolour the selected text layer or the colour wash. Existing designs and saved templates are untouched and render exactly as before.

## 3.1.0.0
Covers are no longer one line of text. A new **Layers** card lets you stack **as many text layers as you like** plus your own **PNG logos and icons**, each independently styled and freely placed. Every layer can be **shown or hidden, reordered, duplicated, deleted** and selected from the list, and the card below it edits whichever layer you have selected. On the preview canvas, the selected layer gets **corner handles to resize it** and a **knob to rotate it** — hold **Shift** to keep a logo's proportions or snap rotation to 15°. Two new sliders, **Opacity** and **Rotation**, work on text and logos alike. Logos are uploaded the same way as backgrounds (PNG, transparency preserved) and are restored onto the canvas when you load a saved template. As always the server render stays authoritative, and rotated or faded text now renders there exactly as the preview shows it.

## 3.0.1.0
Fixes **Reposition background**, which barely worked in 3.0.0.0. Dragging the background now **follows your cursor exactly** — it used to lag badly at low zoom and overshoot at high zoom, matching the cursor only at one specific zoom level. More importantly, repositioning now **works straight away, without zooming in first**: on a **Fill** background you can slide a portrait poster up and down (or a wide still left and right) to choose which part of it the cover shows. Previously that did nothing at all until you zoomed in, because only the zoomed-in part of the image was treated as movable. The server render and the on-screen canvas were fixed together, so what you position is what gets applied. Designs you have already saved or applied are unaffected and render exactly as before.

## 3.0.0.0
The config page preview is now a **live interactive canvas**, not a static image: click the title to select it and **drag it** to position, and toggle **Reposition background** to **drag-pan** and **scroll/pinch-zoom** the background image directly on the canvas. A new **Show server render** button renders the exact same design on the server — the authoritative output, since the canvas is a fast approximation — so you can compare before applying. Designs are now stored internally in a new **layered document format** (title text is one layer, more layer types land in a later release); **existing saved templates migrate automatically** the first time you load them, so nothing is lost.

## 2.1.0.0
A code-quality and size pass. The plugin is now **much smaller** — the bundled Noto Sans fonts were subset to the glyphs cover titles use (Latin, Greek, Cyrillic, punctuation), cutting the plugin DLL from ~3.9 MB to ~1.6 MB with no visible change. **Dead code was removed** (an unused retry service, an unused exceptions module, and nine internal API endpoints the UI never called), and the render pipeline was tidied up (the image compositing is no longer needlessly async). A few **security hardening** touches: the Apply and batch-apply endpoints are now rate-limited and batch size is capped, text size is clamped, poster-collage sources get the same decompression-bomb guard as other images, and error messages no longer echo server paths back to the browser. Purely internal — no user-facing behaviour changes.

## 2.0.3.0
Small UI fixes on the Target card: a **disabled button now clearly looks disabled** (dimmed, not-allowed cursor) — the Restore button no longer looks clickable when there's no backup yet — and the Restore row has proper spacing above it. Also added an animated preview to the README.

## 2.0.2.0
The **poster browser now covers every library type** — music albums and artists, books, music videos and photos, not just movies and shows — so you can pick a cover from a music or book library too. A **loading spinner** now shows over the preview while it renders, which helps for slower operations like building a poster collage. The **Apply** button now shows an "Applying…" state and locks while it works, so a slow render (like an animated GIF) no longer looks like nothing is happening. **Animated GIF covers are more reliable**: the working size is capped so a full-size render no longer takes forever or fails to apply, and text scales with it so the look is unchanged. The default **Landscape** size is now a lighter 1280×720 (it was 1920×1080) — plenty for library tiles and much smaller files; 1920 is still available via Custom. And the Templates and collage cards were tidied up — the **Delete** and **Shuffle** buttons now sit neatly next to their dropdowns instead of as oversized blocks.

## 2.0.1.0
Polish on the v2 release. The config page now matches Jellyfin more closely — compact colour pickers (they used to stretch the full width), and action buttons that align with the selects beside them. The preview no longer starts on a black canvas: it opens with Jellyfin's **brand gradient** (purple → blue) by default. Uploading an **animated GIF** as a background now animates the preview automatically — it switches to Animated GIF output and passes the source's frames through at their own timing, instead of showing a single still frame.

## 2.0.0.0
A big release. **Poster-collage backgrounds** build a grid mosaic from a target's own item posters (with density and shuffle controls). **Design templates** let you save a look and reuse it, and **batch apply** pushes one design onto many libraries, collections or playlists at once — each cover titled with its own target's name. **Animated GIF export** is now real: it passes through an animated-GIF background or applies a Ken Burns pan/zoom (capped at 30 frames). And **Restore original cover** reverts any target to its pre-plugin image — applying a cover now backs up the previous image automatically.

## 1.3.1.0
The plugin now has its own entry in the dashboard's left navigation menu, so you no longer have to open it through the Plugins list. Added **Live TV** as a cover target (best-effort — it sets the image on Jellyfin's generated Live TV view, which may not persist on every server). Moved the custom-font picker into the **Text** card where it belongs, and added a screenshot to the README.

## 1.3.0.0
Fixed the background dim: any non-zero dimming used to black out a picked poster entirely (JPEG posters have no alpha channel, so the overlay painted fully opaque). Dimming now composites correctly and the default was lowered to 0.25. Added an **Image fit** option — Fill (crop), Fit (show the whole image, letterboxed) or Stretch — so a portrait poster no longer gets cropped to a strip in a wide cover. New **multi-colour gradient** picker with an angle control for linear gradients and add/remove colour stops. You can now target **collections** and **playlists** in addition to libraries.

## 1.2.2.0
Poster background reliability: authenticate the image download, build the upload without the (sometimes unavailable) File constructor, and surface a clear message at each step so any failure is visible instead of silent.

## 1.2.1.0
Fixes. The config page now reliably follows your Jellyfin UI language — the detection reads Jellyfin's stored language setting, not just the browser language (Dutch now shows correctly). Picking a library poster as the background now works: the image is fetched through Jellyfin's own image endpoint and uploaded, instead of resolving a local file path that wasn't always available.

## 1.2.0.0
The config page now follows your Jellyfin UI language, with a complete Dutch (NL) translation and English fallback. Font weight now works — all six weights (Light–ExtraBold) are bundled as distinct Noto Sans faces. Background images are cover-fitted instead of stretched, so posters keep their aspect ratio. Added a Landscape 16:9 preset (now the default, best for library covers), a Download button for the generated image, and logging when a background image can't be used.

## 1.1.0.0
Redesigned the config page into a native Jellyfin two-column layout with grouped cards and a sticky **live preview** that updates automatically as you adjust settings (no more manual preview button). Styled the file pickers as native buttons showing the chosen file, and made allowed formats and size limits explicit. Fixed the empty library dropdown (libraries now come from GetVirtualFolders). Backfilled earlier version changelogs.

## 1.0.1.0
Bundled Noto Sans so titles always render (even on Docker/minimal Linux). Security & correctness hardening: fixed path-traversal and decompression-bomb risks, added rate limiting and font-signature validation, safe colour parsing, image disposal, and generated-file cleanup.

## 1.0.0.0
Initial release: custom library cover art with text, gradients, blur, custom backgrounds and fonts, a live preview, and a picker for existing library posters.
