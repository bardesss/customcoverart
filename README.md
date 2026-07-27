<div align="center">

# 🎨 Custom Cover Art

**A Jellyfin plugin for designing and applying custom cover art to your media libraries.**

Text, gradients, blur, custom backgrounds and fonts — with a live preview, right inside the Jellyfin dashboard.

`Jellyfin 10.11` · `.NET 9` · `SixLabors.ImageSharp`

</div>

---

> ### 🤖 AI assistance disclaimer
>
> This plugin was substantially rebuilt and refactored with the help of an AI assistant
> (Anthropic's Claude). The code **compiles cleanly and passes an automated unit-test suite**
> (image generation, upload validation, the path-traversal sandbox, and plugin wiring), and
> the runtime paths have been tested against a live Jellyfin server.

---

## ✨ Features

| | Feature | Details |
|---|---|---|
| 🅰️ | **Text overlays** | Title text with size, weight, colour and alignment control |
| 🌑 | **Text effects** | Drop shadow and outline for readability over busy backgrounds |
| 🌈 | **Gradients** | Linear and radial gradient backgrounds |
| 🖼️ | **Custom backgrounds** | Upload your own image, or pick an existing poster from any library |
| 🌫️ | **Blur & dimming** | Soften and darken backgrounds so text stands out |
| 🔠 | **Fonts** | Bundled Noto Sans (matches Jellyfin's UI, so text always renders) — or upload your own `.ttf` / `.otf` / `.woff` / `.woff2` |
| 📐 | **Presets & sizes** | Square cover, portrait poster, wide banner, or custom dimensions |
| 👁️ | **Live preview** | Render a preview before applying anything |
| 🌍 | **Localisation** | English and Dutch included; easy to add more |

## 📸 Screenshots

<div align="center">

![The Custom Cover Art configuration page inside the Jellyfin dashboard, with live preview](screenshots/1.png)

</div>

## 📋 Requirements

- **Jellyfin Server 10.11.x** (the plugin is ABI‑locked to this major version)
- The **.NET 9 SDK** — only needed if you build from source; the runtime ships with Jellyfin

## 🚀 Installation

### Via the plugin repository (recommended)

This installs the plugin from within Jellyfin and keeps it updated automatically.

1. In Jellyfin, go to **Dashboard → Plugins → Repositories → `+`**.
2. Add a repository:
   - **Name:** `Custom Cover Art`
   - **URL:**
     ```
     https://raw.githubusercontent.com/Bardesss/customcoverart/main/manifest.json
     ```
3. Go to **Dashboard → Plugins → Catalog**, find **Custom Cover Art** under *General*, and install it.
4. **Restart** the Jellyfin server when prompted.

### From a built DLL (manual)

1. Build the plugin (see below) or download a release `CustomCoverArt.dll`.
2. In your Jellyfin **data directory**, create a folder:
   `plugins/CustomCoverArt/`
   *(e.g. `/var/lib/jellyfin/plugins/CustomCoverArt/` on Linux, or
   `%ProgramData%\Jellyfin\Server\plugins\CustomCoverArt\` on Windows).*
3. Copy `CustomCoverArt.dll` **and the bundled `SixLabors.*.dll` files** into that folder.
4. Restart the Jellyfin server.
5. Open **Dashboard → Plugins** and confirm *Custom Cover Art* is listed and active.

## 🛠️ Building from source

```bash
dotnet restore
dotnet build -c Release
```

The output is in `bin/Release/net9.0/`. Copy `CustomCoverArt.dll` plus the
`SixLabors.ImageSharp*.dll` and `SixLabors.Fonts.dll` files into the plugin folder above.
*(The `Jellyfin.*` assemblies are provided by the server and are intentionally **not** shipped.)*

## 🎬 Usage

1. Go to **Dashboard → Plugins → Custom Cover Art**.
2. Pick a **library** from the dropdown.
3. Adjust the **settings** — title, colours, effects, gradient or background image, dimensions.
4. Click **Generate Preview** to see the result.
5. Click **Apply to Library** to set it as the library's cover.

### Using an existing poster as a background

Open **Browse library posters**, search/filter your media, and click any item. Its poster is
copied into the plugin's own data folder and used as the background — a safe, sandboxed copy,
never a direct reference to your media files.

## 🔒 Security & privacy

- All endpoints require an authenticated **administrator** (`RequiresElevation` policy).
- Uploads are **rate‑limited**, size‑capped, and validated by **content** (not just extension) —
  executables and non‑image payloads are rejected even if renamed to `.png`.
- Background/font paths supplied by the browser are **sandboxed** to the plugin's data
  directory, so the generator cannot be pointed at arbitrary files on the server.
- Uploaded files are stored under Jellyfin's data path using randomised names.

## 🌐 Adding a translation

1. Add `Resources/<language-code>.json` (copy `en.json` as a starting point).
2. Translate the values; keep the keys identical.
3. Use `{0}`, `{1}` for interpolated values (e.g. `"File is too large. Maximum {0}MB."`).
4. Rebuild. Detection order: `JELLYFIN_LANGUAGE` → `LANG` → system culture → English fallback.

Included: `en` (English), `nl` (Dutch).

## 🧪 Development

The core logic is covered by unit tests (image generation, upload validation, the path
sandbox, and plugin metadata/wiring). Because the plugin references the Jellyfin server
assemblies with `ExcludeAssets=runtime`, a test project should reference
`Jellyfin.Common` / `Jellyfin.Controller` / `Jellyfin.Model` directly to supply them at test time.

## 📦 Releasing

The version lives in one place: `<Version>` in `CustomCoverArt.csproj`. To ship a release,
bump it in a pull request and merge to `main`. The **Release** GitHub Action then builds the
plugin, publishes a GitHub release with the packaged zip, and appends the new version to
`manifest.json` — so servers subscribed to the repository auto-update. If the version has
already been released, the workflow is a no-op.

## 🧩 Project layout

| Path | Purpose |
|---|---|
| `Plugin.cs` | Plugin entry point (`BasePlugin` + `IHasWebPages`) |
| `PluginServiceRegistrator.cs` | DI registration (`IPluginServiceRegistrator`) |
| `Configuration/configPage.html` | The dashboard config UI (static, embedded) |
| `Controllers/` | REST API endpoints (`ControllerBase`, admin‑only) |
| `Services/` | Cover‑art generation, image processing, library/media access, etc. |
| `Common/PluginPaths.cs` | Data‑directory resolution and the path sandbox |

## 📄 License

MIT — see [`LICENSE`](LICENSE).

## 🙏 Credits

- **Jellyfin** — the media server platform this plugin extends.
- **SixLabors ImageSharp** — image rendering and text drawing.
- **Noto Sans** by Google — bundled default font, licensed under the
  [SIL Open Font License 1.1](https://openfontlicense.org/).
