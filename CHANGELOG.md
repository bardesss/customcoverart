# Changelog

The text under each version heading is published to the plugin's manifest by the
release workflow (and shown as the version's changelog inside Jellyfin). Add a
new `## <version>` section when you bump `<Version>` in the csproj.

## 1.1.0.0
Redesigned the config page into a native Jellyfin two-column layout with grouped cards and a sticky **live preview** that updates automatically as you adjust settings (no more manual preview button). Styled the file pickers as native buttons showing the chosen file, and made allowed formats and size limits explicit. Fixed the empty library dropdown (libraries now come from GetVirtualFolders). Backfilled earlier version changelogs.

## 1.0.1.0
Bundled Noto Sans so titles always render (even on Docker/minimal Linux). Security & correctness hardening: fixed path-traversal and decompression-bomb risks, added rate limiting and font-signature validation, safe colour parsing, image disposal, and generated-file cleanup.

## 1.0.0.0
Initial release: custom library cover art with text, gradients, blur, custom backgrounds and fonts, a live preview, and a picker for existing library posters.
