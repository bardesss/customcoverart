# Rebuild notes (2026-07-27)

The plugin was re-scaffolded onto the correct Jellyfin 10.11 plugin API. It
previously could not build or load. Target: **Jellyfin 10.11.x / .NET 9**.

## What changed structurally

| Area | Before | After |
|------|--------|-------|
| Entry point | `CustomCoverArtPlugin : IPlugin` (fake `Jellyfin.Plugin` API) | `Plugin : BasePlugin<PluginConfiguration>, IHasWebPages` |
| DI registration | `plugin.RegisterServices()` (never called) | `PluginServiceRegistrator : IPluginServiceRegistrator` |
| Config UI | MVC Razor `Configuration.cshtml` + config controller | static embedded `Configuration/configPage.html` via `GetPages()` |
| Auth | custom `[JellyfinAdminRequired]` filter | `[Authorize(Policy = "RequiresElevation")]` |
| Packages | `Jellyfin.Plugin` (nonexistent) + `ImageSharp.Web` | `Jellyfin.Controller`/`Jellyfin.Model` 10.11.3 (ExcludeAssets=runtime) + `ImageSharp.Drawing` |
| Data paths | probed env vars / hardcoded dirs / temp fallback | `IApplicationPaths` via DI (`Common/PluginPaths.cs`) |

## Compile fixes folded in
Missing usings (`System.Globalization`, `System.Net.Sockets`, `SixLabors.Fonts`,
`SixLabors.ImageSharp.PixelFormats`); `settings.Text`→`Title`,
`settings.BackgroundColor`→`DimColor`; invalid static interface members; `Timer`
callback signature; `System.Drawing`/ImageSharp `Color` ambiguity; `TextOptions`
→ `RichTextOptions`; tuple `getImageDimensions` → `ImageDimensionsDto`.

## Security fixes folded in
- Fake "virus scan" → real content check (`Image.Identify` + executable-header reject).
- Client-supplied `BackgroundImagePath`/`CustomFontPath` now rejected unless they
  resolve inside the plugin data dir (`PluginPaths.IsInsideBase`).
- Config-page fetches now send the Jellyfin auth token (`X-Emby-Token`).

## Build & test status

**Builds clean** against `Jellyfin.Controller`/`Model` 10.11.3 on .NET 9
(`dotnet build -c Release` → 0 errors). ImageSharp pinned to **3.1.12** and
ImageSharp.Drawing **2.1.7** (the 3.1.5 originally specified had known
vulnerabilities and caused a downgrade conflict). `SortOrder` was resolved to
`Jellyfin.Database.Implementations.Enums` (10.11 moved it there).

**14 unit tests pass** (in a separate test project): a real end-to-end cover-art
render (gradient + shadow + outline → valid PNG), upload validation (accepts a
real PNG; rejects an executable and an HTML/JS polyglot renamed to `.png`), the
path-traversal sandbox, and reflection checks that the plugin implements the
required Jellyfin contracts and embeds the config page.

```
dotnet restore
dotnet build -c Release
```

Copy `bin/Release/net9.0/CustomCoverArt.dll` (and the SixLabors DLLs) into a
`CustomCoverArt` folder under the server's `plugins/` directory, then restart.

## VERIFY against a running server (could not be exercised without live Jellyfin)
Compilation is confirmed; these depend on live server/DB behaviour:

1. **`LibraryDetectionService.UpdateLibraryCoverArtAsync`** — `item.SetImage(...)`
   + `item.UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate, ...)`. The core
   "apply cover to library" call. If the image doesn't appear, a metadata refresh
   may also be needed. **Highest-risk spot.**
2. **`MediaItemService`** query execution — that `InternalItemsQuery` returns the
   expected items/counts against a real library database.
3. **`LibraryDetectionService.GetLibraryType`** — `CollectionType` values (handled
   case-insensitively, but confirm the labels come out right).
4. **Config page in the live dashboard** — response JSON casing (page reads
   case-agnostically and sends PascalCase + numeric enums) and the `ApiClient`
   token/image-URL helpers behaving as expected.
