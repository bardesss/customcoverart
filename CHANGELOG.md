# Changelog

The text under each version heading is published to the plugin's manifest by the
release workflow (and shown as the version's changelog inside Jellyfin). Add a
new `## <version>` section when you bump `<Version>` in the csproj.

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
