# Changelog

The text under each version heading is published to the plugin's manifest by the
release workflow (and shown as the version's changelog inside Jellyfin). Add a
new `## <version>` section when you bump `<Version>` in the csproj.

## 1.0.1.0
Bundled Noto Sans so titles always render (even on Docker/minimal Linux). Security & correctness hardening: fixed path-traversal and decompression-bomb risks, added rate limiting and font-signature validation, safe colour parsing, image disposal, and generated-file cleanup.

## 1.0.0.0
Initial release: custom library cover art with text, gradients, blur, custom backgrounds and fonts, a live preview, and a picker for existing library posters.
