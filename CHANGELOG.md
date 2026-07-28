# Changelog

The text under each version heading is published to the plugin's manifest by the
release workflow (and shown as the version's changelog inside Jellyfin). Add a
new `## <version>` section when you bump `<Version>` in the csproj.

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
