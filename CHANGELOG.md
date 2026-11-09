# Changelog

The release workflow publishes the text under each version heading to the plugin's manifest,
and Jellyfin shows it as that version's changelog. Add a new `## <version>` section when you
bump `<Version>` in the csproj. Jellyfin renders the text as Markdown, so keep a short opening
sentence, a blank line, and then one bullet per change.

## 4.1.2.0
A behind-the-scenes release: the editor looks and works as it did in 4.1.1.0, apart from four small undo fixes.

- The configuration page had grown into a single file of more than five thousand lines. It is now three files: the page itself, its styles and its script.
- The script is laid out in separate parts (translations, server requests, the design, the live preview, undo, the controls and the poster browser), so future fixes are smaller and safer.
- **Undo fix:** undo, redo and loading a template now bring back the poster collage that design was made with, instead of keeping your latest Shuffle.
- **Undo fix:** Ctrl+Z and Ctrl+Y no longer change the design while a confirmation question is open.
- **Undo fix:** picking a target, which puts its name in the title, is no longer a step of its own that Ctrl+Z takes back.
- **Undo fix:** after switching to another target, Ctrl+Z no longer puts the previous target's name back in the title. A title you typed yourself still undoes as before.
- Batch apply now sends your design once instead of twice. It also used to send an older, flat copy that the server had been ignoring for several releases.
- Templates saved before the canvas editor still load, and still work with batch apply.
- **For your own scripts:** the two old server endpoints that only understood the flat format (`preview` and `apply`), which the plugin itself no longer used, have been removed, and `batchApply` no longer accepts a flat body with only `Settings`. Use `document/preview` and `document/apply` instead, and send batch apply a `Document` or a `TemplateName`.
- If the page shows up unstyled or unresponsive right after updating, reload the dashboard once (Ctrl+F5). In a Jellyfin app or on a TV, restart the app or clear its cache.

## 4.1.1.0
An animated GIF background now also moves in the live preview in Firefox.

- Before, it only moved in Chrome, Edge and other Chromium-based browsers; Firefox kept showing a single frame.
- The preview now plays the GIF in real time at its own speed, redrawn up to about 15 times a second. Very fast GIFs skip frames in the preview to keep time.
- Covers you applied were never affected: they always animated.
- Browsers that cannot decode an image frame by frame (Safari, for example) still show a still preview, but the applied cover animates as usual.

## 4.1.0.0
A polish release for the editor, based on a full review of the page. Existing designs and saved templates are unaffected and render exactly as before.

- **Spacing:** since the guided-steps redesign, some controls sat flush against each other (the Restore button touched the Type dropdown, *In context* touched Dimensions) while other gaps were twice as big. Every step now has one even rhythm, and dropdowns line up with the buttons beside them.
- **Error messages:** when the server refuses a request you now see why in your own language (your session expired, the file is too large, too many requests, or the server's own reason for refusing a design) instead of *"Unexpected end of JSON input"*. Nothing is left stuck on "Applying…".
- **Confirmations:** Restore, deleting a template and batch apply now ask before they act, as Reset backup already did.
- Batch apply says how many covers it will replace and can no longer be started twice.
- Loading a template asks before replacing changes you have not applied.
- The browser warns before you close the tab with unapplied work, but only when the design really differs from what you last applied, saved or loaded.
- Undo and loading a template keep your background image on the preview instead of dropping it, and an animated GIF background keeps moving.
- Choosing a **Custom** size or **Animated GIF** now shows their settings straight away.
- **Save this design** and **Apply to several targets** are no longer tucked away under Advanced.
- Custom sizes stay between 100 and 2048 pixels, which is what the server accepts.
- **Keyboard and screen reader:** select layers with Tab and Enter, and move the selected layer with the arrow keys (Shift for bigger steps; Alt, Ctrl and Meta combinations are left to the browser).
- The poster browser is a proper dialog: Esc closes it, and it keeps keyboard focus inside while you page through posters.
- Every icon button has a name.
- The poster browser follows light themes and the Jellyfin theme colours.
- On narrow screens the layer ⋯ button works again.
- The title starts as the name of the target you pick, and the Apply button says what it applies to (library, collection, playlist or Live TV).
- Switching the display language back to English no longer leaves Dutch or Spanish labels behind.
- Ctrl+Z now also undoes a slider or colour change, and is no longer intercepted on other dashboard pages.

## 4.0.4.0
The live preview and the cover that actually gets applied now agree on text effects. Covers you have already applied are not changed until you apply them again. Fully opaque, unrotated text without a blurred shadow or an outline looks the same as before.

- **Shadow softness** came out twice as wide on the applied cover as in the preview. It now matches, so a cover with a blurred text shadow looks slightly sharper than before once you apply it again.
- **An outline on semi-transparent text** used to stay fully solid on the cover however far you turned the opacity down. It now fades with the text, as the preview always showed, and outline corners are a little rounder.
- **Rotated text:** the cover turned the shadow along with the letters, while the preview kept it falling the same way on screen. The cover now does what the preview shows.
- In the preview, semi-transparent text with a shadow was drawn slightly too dense.
- The in-context previews (wide, square, poster) drew text, shadow and outline at full size, so they spilled out of the little thumbnails.
- An uploaded font looked artificially bold in the preview when the weight was set to bold (the cover never was).
- A gradient from an old template with fewer than two colour stops showed different colours in the editor, the preview and the cover. All three now show the same colours.

## 4.0.3.0
A fix-up release for backups and the live preview.

- **Applying a cover a second time could be refused** with *"Could not back up the current cover"* on libraries that had no backup before 4.0.2.0. The backup was saved under one spelling of the library's id and looked for under another. Backups made by earlier versions are found again, so they keep working and the refusals stop.
- **Covers made by this plugin are no longer saved as the "original".** The plugin now remembers the cover it applied last and never backs that up, nor anything in its own data folder.
- If you applied covers with an earlier version before a target's first backup was taken (while trying the plugin out, for example), that backup may hold one of those test covers, and **Restore original cover** brings the test cover back instead of the real original.
- Covers applied by 4.0.2.0 are not recognised as plugin covers until you have applied once with this version.
- **New Reset backup button** next to Restore. It deletes a target's backup and saves nothing itself: the next apply stores the cover the target shows at that moment as the new original, unless it is a cover the plugin applied.
- To fix a wrong backup, first put the cover you want to keep back on the library: upload it in Jellyfin (**Edit images** in the dashboard), or delete the poster and refresh metadata with *Replace existing images*. Then press **Reset backup**, then apply. This matters most if your old backup holds an early test cover, because your true original was overwritten before backups existed and has to come from you.
- Restores, backups and skipped backups are now written to the Jellyfin log.
- A poster collage background now shows in the live preview. It used to stay black until you pressed Show server render.
- An animated GIF background now moves in the preview instead of standing still, unless your system is set to reduce motion.

## 4.0.2.0
A hardening and housekeeping release. Apart from the fixes below, nothing about how you design covers changes.

- **Applied covers now go through Jellyfin's own image handling**, the same path as uploading an image in the dashboard, so they are stored alongside the rest of your library artwork.
- Covers applied or restored from this version on keep working even if the plugin's data folder is cleared. Covers applied by 4.0.1.0 and never re-applied still live in that folder. **Restore original cover** works exactly as before.
- **If the current cover cannot be backed up first, applying is refused** with a clear message and the cover is left untouched, so the cover you had before you first used the plugin can always be restored. A cover you set in the dashboard after an earlier apply is replaced by the next apply, as with Jellyfin's own upload.
- A logo or icon layer scaled larger than 1.5× the canvas now renders at most 1.5×, so old templates with huge logos render smaller.
- **Animated images:** the server only decodes the frames it needs (one for a still cover, at most 60 for an animated one).
- A long or high-resolution animated GIF is no longer turned away; the plugin uses as many of its frames as fit safely in memory. Only an image whose single frame is larger than 8192×8192 pixels is refused at upload, with a clear message.
- Ken Burns covers no longer stretch a background whose shape differs from the cover, animated covers render faster, and a shrunk animated GIF keeps its blur and border in proportion.
- A single faulty text layer can no longer blank out the whole cover. It is skipped and everything else still renders.
- Size errors such as *"Dimensions too large"* are now shown as they are instead of a generic failure.
- The applied-cover folder no longer grows with every apply.
- Applying to the same library or collection now happens one at a time, so two applies can no longer overwrite each other.
- Rapid repeated requests can no longer slip past the rate limits, and rejected uploads now count toward the upload limit too.
- Saving a template while a batch apply runs is now safe.
- **TIFF uploads are no longer accepted.** Browsers cannot draw TIFF in the editor, so they never previewed properly; convert to PNG or JPG.
- The background upload limit can now be changed with `MaxUploadSizeMb` in the plugin's configuration file (1 to 50 MB, default 5). Values above about 28 MB may still be blocked earlier by the web server's own request-size limit.

## 4.0.1.0
Fixes the configuration page on Jellyfin 12, where nothing worked. **If you installed 4.0.0.0, update.** Nothing was wrong with your settings or your covers; the page simply could not reach the server.

- Jellyfin 12 stopped accepting the older of its two ways of proving who you are, and the page was still using it, so every request it made was rejected before it reached the plugin.
- As a result, libraries did not load, saved templates did not appear, the bundled fonts did not arrive, and applying a cover did nothing.
- Opening the poster browser produced the cryptic message *"Failed to execute 'json' on 'Response': Unexpected end of JSON input"*, which was that rejection with no explanation attached.
- The page now identifies itself the same way the Jellyfin web client does, which works on Jellyfin 10.11 and 12 alike.

## 4.0.0.0
Support for Jellyfin 12. This release is built against Jellyfin 12 and requires it.

- Jellyfin 12 moved to .NET 10 and changed its plugin ABI, so one plugin build cannot serve both 10.11 and 12.
- Nothing about designing or rendering covers has changed: your saved templates, applied covers and settings all carry over untouched.
- **Still on Jellyfin 10.11?** Stay on 3.5.1.0. Your server will keep being offered that version and will not see this one, so nothing breaks if you leave it alone.

## 3.5.1.0
Fixes a confusing moment with Live TV targets. Nothing else about how covers render has changed.

- Live TV has no posters to build a mosaic from, so the **Poster collage** background is unavailable there.
- If you had already chosen a collage and then switched the target to Live TV, the background silently changed back to **Image**.
- The option is now greyed out with a line explaining why, in all three languages, so the change no longer happens behind your back.

## 3.5.0.0
Adds Spanish. No change to how covers look or render.

- **Spanish translation:** the whole configuration page (every step, label, button, hint and message, plus the server's upload and validation errors) is now translated, joining English and Dutch.
- The page follows your Jellyfin display language, and regional variants are covered too: `es-MX` and `es-419` get Spanish just like `es-ES`.
- The translation files were cleaned up: roughly a hundred strings left behind by the v3 editor rewrite were still being carried (and would have had to be translated into every new language). They are gone.
- Five new build-time checks fail if a language drifts out of sync with English, if a `{0}` placeholder goes missing, or if a string is defined but never shown.

## 3.4.0.0
Adds an overlay gradient to the Background step: a colour that fades in over your background and sits under your text. Existing designs and saved templates are unaffected and render exactly as before.

- It gives you the look where a poster resolves into a solid band of colour at the bottom, with the title on top of it and readable whatever the poster shows underneath.
- Each colour stop has its own **opacity**, so you can go from fully transparent to fully solid, use two colours for a duotone fade, or wash the whole cover.
- Four **presets** (Bottom fade, Top fade, Full wash and Duotone) get you there in one click. Switching between them keeps the colours you have already picked, so you can try all four without losing your palette.
- The **Auto palette** swatches now recolour the selected overlay stop, so you can tint a cover with a colour taken from its own artwork.
- The overlay works with every background type (image, poster collage, gradient or solid) and with animated covers.

## 3.3.0.0
The configuration page is now a guided walkthrough. Existing designs and saved templates are unaffected and render exactly as before.

- Designing a cover is laid out as five numbered steps (**Target**, **Background**, **Text & logos**, **Effects**, **Output**) with one open at a time and any of them one click away, instead of every control at once.
- Each step shows the controls most people need and keeps the rest under **Advanced**.
- Choosing a background is now a single choice (image, poster collage, gradient or solid colour) instead of a source dropdown plus a separate gradient tick that overlapped with it.
- **Phones:** bigger touch targets throughout, the preview pinned at the top while you work, a full-screen poster browser, and canvas handles you can actually grab (they used to shrink to a few pixels on a small screen).
- **Undo and redo** across the whole design with Ctrl+Z / Ctrl+Y.
- **In-context preview** showing your cover wide, square and poster-shaped before you apply it.

## 3.2.0.0
Adds a new Effects card. Existing designs and saved templates are untouched and render exactly as before.

- A **colour wash** tints the background under your text.
- A **vignette** darkens the edges to draw the eye inward.
- **Film grain** adds texture.
- A **border** frames the cover: square or rounded, single or double-lined.
- Every effect is off until you switch it on, and turning one back down to zero leaves your cover exactly as it was.
- **Jellyfin style** lays down the familiar dark-gradient look with clean white text in one click, which you can then edit freely.
- **Auto palette** reads the dominant colours from your background and offers them as swatches. Click one to recolour the selected text layer or the colour wash.

## 3.1.0.0
Covers are no longer one line of text: a new Layers card lets you stack text layers and logos.

- Add as many text layers as you like, plus your own PNG logos and icons, each styled separately and freely placed.
- Every layer can be shown or hidden, reordered, duplicated, deleted and selected from the list. The card below it edits whichever layer you have selected.
- On the preview canvas, the selected layer gets corner handles to resize it and a knob to rotate it. Hold **Shift** to keep a logo's proportions or snap rotation to 15°.
- Two new sliders, **Opacity** and **Rotation**, work on text and logos alike.
- Logos are uploaded the same way as backgrounds (PNG, transparency preserved) and are restored onto the canvas when you load a saved template.
- The server render still has the final say, and rotated or faded text now renders there exactly as the preview shows it.

## 3.0.1.0
Fixes Reposition background, which barely worked in 3.0.0.0. Designs you have already saved or applied are unaffected and render exactly as before.

- Dragging the background now follows your cursor exactly. It used to lag badly at low zoom and overshoot at high zoom, matching the cursor at only one zoom level.
- Repositioning now works straight away, without zooming in first. On a **Fill** background you can slide a portrait poster up and down (or a wide still left and right) to choose which part of it the cover shows.
- Before, that did nothing until you zoomed in, because only the zoomed-in part of the image was treated as movable.
- The server render and the on-screen canvas were fixed together, so what you position is what gets applied.

## 3.0.0.0
The config page preview is now a live interactive canvas instead of a static image.

- Click the title to select it and drag it into position.
- Turn on **Reposition background** to drag-pan and scroll/pinch-zoom the background image directly on the canvas.
- A new **Show server render** button renders the same design on the server so you can compare before applying. The canvas is a fast approximation; the server render is the real output.
- Designs are now stored in a new layered document format. The title text is one layer, and more layer types follow in a later release.
- Existing saved templates migrate automatically the first time you load them, so nothing is lost.

## 2.1.0.0
A code-quality and size pass. There are no user-facing behaviour changes.

- **Much smaller plugin:** the bundled Noto Sans fonts were subset to the glyphs cover titles use (Latin, Greek, Cyrillic, punctuation), cutting the plugin DLL from ~3.9 MB to ~1.6 MB with no visible change.
- Dead code was removed: an unused retry service, an unused exceptions module, and nine internal API endpoints the UI never called.
- The render pipeline was tidied up; the image compositing is no longer needlessly async.
- The Apply and batch-apply endpoints are now rate-limited, and batch size is capped.
- Text size is clamped.
- Poster-collage sources get the same decompression-bomb guard as other images.
- Error messages no longer echo server paths back to the browser.

## 2.0.3.0
Small UI fixes on the Target card.

- A disabled button now clearly looks disabled (dimmed, not-allowed cursor), so the Restore button no longer looks clickable when there is no backup yet.
- The Restore row has proper spacing above it.
- Added an animated preview to the README.

## 2.0.2.0
The poster browser now covers every library type, plus several smaller fixes.

- Music albums and artists, books, music videos and photos now show in the poster browser as well as movies and shows, so you can pick a cover from a music or book library too.
- A loading spinner shows over the preview while it renders, which helps for slower operations like building a poster collage.
- The **Apply** button shows an "Applying…" state and locks while it works, so a slow render (like an animated GIF) no longer looks like nothing is happening.
- **Animated GIF covers are more reliable:** the working size is capped so a full-size render no longer takes forever or fails to apply, and text scales with it so the look is unchanged.
- The default **Landscape** size is now a lighter 1280×720 (it was 1920×1080). That is plenty for library tiles and gives much smaller files; 1920 is still available via Custom.
- The **Delete** and **Shuffle** buttons on the Templates and collage cards now sit next to their dropdowns instead of as oversized blocks.

## 2.0.1.0
Polish on the v2 release.

- The config page matches Jellyfin more closely: compact colour pickers (they used to stretch the full width) and action buttons that line up with the selects beside them.
- The preview no longer starts on a black canvas. It opens with Jellyfin's brand gradient (purple to blue) by default.
- Uploading an animated GIF as a background now animates the preview automatically. It switches to Animated GIF output and passes the source's frames through at their own timing, instead of showing a single still frame.

## 2.0.0.0
A big release with poster collages, templates, batch apply, animated GIF export and restore.

- **Poster-collage backgrounds** build a grid mosaic from a target's own item posters, with density and shuffle controls.
- **Design templates** let you save a look and reuse it.
- **Batch apply** puts one design on many libraries, collections or playlists at once, each cover titled with its own target's name.
- **Animated GIF export** now works: it passes through an animated GIF background or applies a Ken Burns pan/zoom (capped at 30 frames).
- **Restore original cover** reverts any target to its pre-plugin image. Applying a cover now backs up the previous image automatically.

## 1.3.1.0
A dashboard menu entry and Live TV as a target.

- The plugin now has its own entry in the dashboard's left navigation menu, so you no longer have to open it through the Plugins list.
- Added **Live TV** as a cover target. This is best-effort: it sets the image on Jellyfin's generated Live TV view, which may not persist on every server.
- Moved the custom-font picker into the **Text** card.
- Added a screenshot to the README.

## 1.3.0.0
A background dim fix, image fit options, multi-colour gradients and more targets.

- Fixed the background dim: any non-zero dimming used to black out a picked poster entirely (JPEG posters have no alpha channel, so the overlay painted fully opaque). Dimming now composites correctly, and the default was lowered to 0.25.
- Added an **Image fit** option: Fill (crop), Fit (show the whole image, letterboxed) or Stretch. A portrait poster no longer gets cropped to a strip in a wide cover.
- New **multi-colour gradient** picker with an angle control for linear gradients and add/remove colour stops.
- You can now target **collections** and **playlists** as well as libraries.

## 1.2.2.0
Makes poster backgrounds more reliable.

- The image download is now authenticated.
- The upload is built without the (sometimes unavailable) File constructor.
- A clear message is shown at each step, so any failure is visible instead of silent.

## 1.2.1.0
Fixes for the display language and poster backgrounds.

- The config page now reliably follows your Jellyfin UI language. It reads Jellyfin's stored language setting as well as the browser language, so Dutch now shows correctly.
- Picking a library poster as the background now works. The image is fetched through Jellyfin's own image endpoint and uploaded, instead of resolving a local file path that was not always available.

## 1.2.0.0
Dutch translation, working font weights and better background fitting.

- The config page follows your Jellyfin UI language, with a complete Dutch (NL) translation and English fallback.
- Font weight now works: all six weights (Light to ExtraBold) are bundled as distinct Noto Sans faces.
- Background images are cover-fitted instead of stretched, so posters keep their aspect ratio.
- Added a Landscape 16:9 preset, now the default and best for library covers.
- Added a Download button for the generated image.
- A background image that can't be used is now logged.

## 1.1.0.0
A redesigned config page.

- The config page now uses a native Jellyfin two-column layout with grouped cards.
- A sticky live preview updates automatically as you adjust settings (no more manual preview button).
- The file pickers are styled as native buttons that show the chosen file, and allowed formats and size limits are stated.
- Fixed the empty library dropdown: libraries now come from GetVirtualFolders.
- Backfilled earlier version changelogs.

## 1.0.1.0
Reliable title fonts and security fixes.

- Bundled Noto Sans so titles always render, even on Docker or minimal Linux.
- Fixed path-traversal and decompression-bomb risks.
- Added rate limiting and font-signature validation.
- Safe colour parsing, image disposal, and cleanup of generated files.

## 1.0.0.0
Initial release: custom library cover art with text, gradients, blur, custom backgrounds and fonts, a live preview, and a picker for existing library posters.
