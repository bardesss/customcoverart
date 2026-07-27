# Custom Cover Art v2.0.0.0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add four features to the Custom Cover Art Jellyfin plugin — restore-original-cover, poster-collage backgrounds, design templates + batch apply, and animated-GIF export — shipped together as v2.0.0.0 in one PR.

**Architecture:** Server-side ImageSharp rendering stays the integration model (generate an image, `SetImage` + `UpdateToRepositoryAsync(ImageUpdate)`). New logic lives in focused units: a `CollageComposer` helper (grid mosaic), an original-preserving backup registry on disk, template persistence in `PluginConfiguration`, and a frame-builder refactor of `CoverArtService` for animated GIF. The config page (`configPage.html`) gains new cards/controls following its existing `ccaCard` + `data-i18n` pattern.

**Tech Stack:** .NET 9, Jellyfin.Controller/Model 10.11.3 (ABI-locked to 10.11.x), SixLabors.ImageSharp 3.1.12 + ImageSharp.Drawing 2.1.7, xUnit 2.9.2 + NSubstitute 5.1.0 for tests.

## Global Constraints

- **Version:** four-part; bump `<Version>` in `CustomCoverArt.csproj` to exactly `2.0.0.0` (currently `1.3.1.0`).
- **Jellyfin ABI:** do not change the `10.11.3` package versions or the `<ExcludeAssets>runtime</ExcludeAssets>` on Jellyfin refs. Test project supplies them at runtime.
- **Admin-only:** every controller endpoint inherits `[Authorize(Policy = "RequiresElevation")]` from the class; do NOT add per-method `[Authorize]`.
- **Id handling:** every id string is validated with `Guid.TryParse` before use; resolve items with `_libraryManager.GetItemById<BaseItem>(Guid id)`.
- **ApiResponse:** JSON endpoints return `Task<ApiResponse<T>>` via the `Success(data)` / `Fail<T>(message)` helpers. Binary endpoints return `IActionResult` via `File(bytes, contentType)`.
- **Config page JSON casing:** the client sends PascalCase and reads responses with the case-tolerant `pick(obj, 'Key')` accessor. Keep both patterns.
- **Localization:** every new user-facing string gets a `data-i18n` key added to BOTH `I18N.en` and `I18N.nl` in `configPage.html`, and any server-side string added to both `Resources/en.json` and `Resources/nl.json`.
- **Fonts/text:** unchanged; bundled Noto Sans stays the fallback.

---

## File Structure

**New files**
- `Services/CollageComposer.cs` — builds a grid-mosaic `Image<Rgba32>` from poster file paths. One responsibility: layout + tiling. No Jellyfin API calls.
- `tests/CustomCoverArt.Tests/CollageComposerTests.cs`
- `tests/CustomCoverArt.Tests/BackupRestoreTests.cs`
- `tests/CustomCoverArt.Tests/TemplateTests.cs`
- `tests/CustomCoverArt.Tests/AnimationTests.cs`
- `tests/CustomCoverArt.Tests/ModelTests.cs`

**Modified files**
- `Models/CoverArtModels.cs` — new models/enums + new `CoverArtSettings` props.
- `Configuration/PluginConfiguration.cs` — `Templates` list.
- `Common/PluginPaths.cs` — `Backups(...)` folder helper.
- `Plugin.cs` — ensure `static Plugin? Instance` exists (template persistence).
- `Services/IServices.cs` — new interface methods.
- `Services/LibraryDetectionService.cs` — original-preserving backup + restore.
- `Services/MediaItemService.cs` — `GetPosterPathsAsync`.
- `Services/CoverArtService.cs` — collage source + frame-builder refactor + animated GIF.
- `Controllers/CustomCoverArtController.cs` — restore, templates CRUD, batchApply, auto-backup on apply.
- `PluginServiceRegistrator.cs` — register `CollageComposer`.
- `Configuration/configPage.html` — restore button, background-source + collage controls, template card, batch-apply section, animation controls, `I18N` keys.
- `Resources/en.json`, `Resources/nl.json` — new server strings (if any).
- `CustomCoverArt.csproj` — version bump.
- `CHANGELOG.md`, `README.md` — v2.0.0.0 entry + feature docs.

---

## Phase 0 — Foundations

### Task 1: New models, enums, and `CoverArtSettings` properties

**Files:**
- Modify: `Models/CoverArtModels.cs`
- Test: `tests/CustomCoverArt.Tests/ModelTests.cs` (create)

**Interfaces:**
- Produces: `BackgroundSources` constants (`Upload`/`LibraryPoster`/`Collage`); `CollageSettings { SourceId, SourceType, Density, Seed }`; `AnimationSettings { Enabled, KenBurns, ZoomAmount, Direction, FrameCount, DelayMs, Loop }`; `SavedTemplate { Name, Settings }`; `BatchTargetRef { Id, Type }`; `BatchApplyRequest { TemplateName, Settings, Targets }`; `BatchApplyResult { Id, Name, Success, Error }`; new `CoverArtSettings.BackgroundSource` (string, default `"upload"`), `CoverArtSettings.Collage` (`CollageSettings?`), `CoverArtSettings.Animation` (`AnimationSettings?`).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/CustomCoverArt.Tests/ModelTests.cs
using CustomCoverArt.Models;
using Xunit;

namespace CustomCoverArt.Tests;

public class ModelTests
{
    [Fact]
    public void CoverArtSettings_HasCollageAndAnimationDefaults()
    {
        var s = new CoverArtSettings();
        Assert.Equal("upload", s.BackgroundSource);
        Assert.Null(s.Collage);
        Assert.Null(s.Animation);
    }

    [Fact]
    public void CollageSettings_DefaultsAreSafe()
    {
        var c = new CollageSettings();
        Assert.Equal("medium", c.Density);
        Assert.Equal("library", c.SourceType);
        Assert.Equal(string.Empty, c.SourceId);
    }

    [Fact]
    public void AnimationSettings_DefaultsAreBounded()
    {
        var a = new AnimationSettings();
        Assert.False(a.Enabled);
        Assert.Equal(20, a.FrameCount);
        Assert.True(a.Loop);
    }

    [Fact]
    public void SavedTemplate_HoldsNameAndSettings()
    {
        var t = new SavedTemplate { Name = "Neon", Settings = new CoverArtSettings { TextSize = 200 } };
        Assert.Equal("Neon", t.Name);
        Assert.Equal(200, t.Settings.TextSize);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CustomCoverArt.Tests --filter FullyQualifiedName~ModelTests`
Expected: FAIL — `CollageSettings` / `AnimationSettings` / `SavedTemplate` / `BackgroundSource` do not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

Add to `Models/CoverArtModels.cs`. Add the three properties inside `CoverArtSettings` (near the Export Settings block):

```csharp
    // Background source: "upload" (default), "libraryPoster", or "collage".
    public string BackgroundSource { get; set; } = "upload";
    public CollageSettings? Collage { get; set; }
    public AnimationSettings? Animation { get; set; }
```

Add these new types at the end of the file:

```csharp
/// <summary>String constants for CoverArtSettings.BackgroundSource.</summary>
public static class BackgroundSources
{
    public const string Upload = "upload";
    public const string LibraryPoster = "libraryPoster";
    public const string Collage = "collage";
}

/// <summary>Auto poster-collage background settings.</summary>
public class CollageSettings
{
    /// <summary>The target whose child items supply the posters (a library/collection/playlist id).</summary>
    public string SourceId { get; set; } = string.Empty;
    public string SourceType { get; set; } = "library";
    /// <summary>Grid density preset: "sparse", "medium" or "dense".</summary>
    public string Density { get; set; } = "medium";
    /// <summary>Deterministic shuffle seed so preview and apply match; the Shuffle button changes it.</summary>
    public int Seed { get; set; } = 0;
}

/// <summary>Animated-GIF export settings.</summary>
public class AnimationSettings
{
    public bool Enabled { get; set; } = false;
    /// <summary>Ken Burns pan/zoom on the (static) background. Ignored when the background is itself an animated GIF.</summary>
    public bool KenBurns { get; set; } = false;
    /// <summary>Fractional zoom over the whole animation (0.15 = 15%).</summary>
    public float ZoomAmount { get; set; } = 0.15f;
    /// <summary>"in" or "out".</summary>
    public string Direction { get; set; } = "in";
    public int FrameCount { get; set; } = 20;
    /// <summary>Per-frame delay in milliseconds.</summary>
    public int DelayMs { get; set; } = 80;
    public bool Loop { get; set; } = true;
}

/// <summary>A saved design template. Title and target are intentionally excluded from the design.</summary>
public class SavedTemplate
{
    public string Name { get; set; } = string.Empty;
    public CoverArtSettings Settings { get; set; } = new();
}

/// <summary>A single batch-apply target reference.</summary>
public class BatchTargetRef
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = "library";
}

/// <summary>Request to apply one design to many targets at once.</summary>
public class BatchApplyRequest
{
    /// <summary>Name of a saved template to use; if null, <see cref="Settings"/> is used.</summary>
    public string? TemplateName { get; set; }
    public CoverArtSettings? Settings { get; set; }
    public List<BatchTargetRef> Targets { get; set; } = new();
}

/// <summary>Per-target outcome from a batch apply.</summary>
public class BatchApplyResult
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Error { get; set; }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CustomCoverArt.Tests --filter FullyQualifiedName~ModelTests`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add Models/CoverArtModels.cs tests/CustomCoverArt.Tests/ModelTests.cs
git commit -m "feat(models): add collage, animation, template and batch-apply models"
```

---

### Task 2: Backups path + templates persistence prerequisites

**Files:**
- Modify: `Common/PluginPaths.cs`
- Modify: `Configuration/PluginConfiguration.cs`
- Modify: `Plugin.cs` (only if `Instance` is missing)
- Test: `tests/CustomCoverArt.Tests/Tests.cs` (add one fact to existing `PathSandboxTests`)

**Interfaces:**
- Produces: `PluginPaths.Backups(IApplicationPaths)` returning `<DataPath>/customcoverart/backups`; `PluginConfiguration.Templates` (`List<SavedTemplate>`); `Plugin.Instance` (`static Plugin?`).

- [ ] **Step 1: Confirm `Plugin.Instance` exists**

Read `Plugin.cs`. If it does NOT already have a static `Instance`, add it. The constructor must assign it:

```csharp
    public static Plugin? Instance { get; private set; }

    // inside the existing constructor body, first line:
    Instance = this;
```

(If `Instance` already exists, skip this — no change.)

- [ ] **Step 2: Write the failing test**

Add to the existing `PathSandboxTests` class in `tests/CustomCoverArt.Tests/Tests.cs`:

```csharp
    [Fact]
    public void BackupsPathIsInsideDataDir()
    {
        var paths = PathsWith(Path.Combine(Path.GetTempPath(), "jfdata"));
        var backup = Path.Combine(PluginPaths.Backups(paths), "abc", "original.png");
        Assert.True(PluginPaths.IsInsideBase(paths, backup));
    }
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test tests/CustomCoverArt.Tests --filter FullyQualifiedName~PathSandboxTests`
Expected: FAIL — `PluginPaths.Backups` does not exist.

- [ ] **Step 4: Implement**

In `Common/PluginPaths.cs`, add a `Backups` helper mirroring the existing `Uploads`/`Generated` helpers (same base folder `customcoverart`):

```csharp
    public static string Backups(IApplicationPaths paths) =>
        Path.Combine(Base(paths), "backups");
```

(Use whatever the existing private base helper is named — match `Uploads(...)`'s implementation exactly, substituting `"backups"`.)

In `Configuration/PluginConfiguration.cs`, add:

```csharp
    /// <summary>
    /// Gets or sets the saved design templates (title/target excluded from each).
    /// </summary>
    public List<SavedTemplate> Templates { get; set; } = new();
```

Add `using CustomCoverArt.Models;` and `using System.Collections.Generic;` to the top of `PluginConfiguration.cs` if not present.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test tests/CustomCoverArt.Tests --filter FullyQualifiedName~PathSandboxTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Common/PluginPaths.cs Configuration/PluginConfiguration.cs Plugin.cs tests/CustomCoverArt.Tests/Tests.cs
git commit -m "feat: add backups path helper, templates config store, plugin Instance"
```

---

## Phase 1 — Feature 1: Restore original cover

### Task 3: Original-preserving backup + restore in `LibraryDetectionService`

**Files:**
- Modify: `Services/IServices.cs`
- Modify: `Services/LibraryDetectionService.cs`
- Test: `tests/CustomCoverArt.Tests/BackupRestoreTests.cs` (create)

**Interfaces:**
- Consumes: existing `_libraryManager.GetItemById<BaseItem>(id)`, `item.HasImage(ImageType.Primary)`, `item.GetImagePath(ImageType.Primary, 0)`, `item.SetImage`, `item.UpdateToRepositoryAsync`.
- Produces on `ILibraryDetectionService`: `bool HasBackup(string libraryId)`; `Task<bool> RestoreOriginalCoverArtAsync(string libraryId)`. Changed semantics of `Task<string?> BackupCurrentCoverArtAsync(string libraryId)` — now writes to a deterministic per-target path and never overwrites an existing backup.

**Design:** backups live at `<Backups>/<targetId>/original<ext>`. First backup for a target is preserved forever, so restore always returns the true pre-plugin image. `LibraryDetectionService` gains an `IApplicationPaths` constructor dependency to resolve the backups folder.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/CustomCoverArt.Tests/BackupRestoreTests.cs
using System;
using System.IO;
using System.Threading.Tasks;
using CustomCoverArt.Services;
using MediaBrowser.Common.Configuration;
using NSubstitute;
using Xunit;

namespace CustomCoverArt.Tests;

public class BackupRestoreTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "cca-backup-" + Guid.NewGuid().ToString("N"));

    private IApplicationPaths Paths()
    {
        var p = Substitute.For<IApplicationPaths>();
        p.DataPath.Returns(_dataDir);
        return p;
    }

    [Fact]
    public void HasBackup_FalseWhenNoBackupFileExists()
    {
        var svc = new LibraryDetectionService(
            Substitute.For<MediaBrowser.Controller.Library.ILibraryManager>(),
            Substitute.For<ILoggingService>(),
            Paths());

        Assert.False(svc.HasBackup(Guid.NewGuid().ToString()));
    }

    [Fact]
    public void HasBackup_TrueAfterBackupFilePlaced()
    {
        var paths = Paths();
        var id = Guid.NewGuid().ToString();
        var dir = Path.Combine(CustomCoverArt.Common.PluginPaths.Backups(paths), id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "original.png"), "x");

        var svc = new LibraryDetectionService(
            Substitute.For<MediaBrowser.Controller.Library.ILibraryManager>(),
            Substitute.For<ILoggingService>(), paths);

        Assert.True(svc.HasBackup(id));
    }

    [Fact]
    public async Task RestoreOriginal_ReturnsFalseWhenNoBackup()
    {
        var svc = new LibraryDetectionService(
            Substitute.For<MediaBrowser.Controller.Library.ILibraryManager>(),
            Substitute.For<ILoggingService>(), Paths());

        Assert.False(await svc.RestoreOriginalCoverArtAsync(Guid.NewGuid().ToString()));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, true); } catch { }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CustomCoverArt.Tests --filter FullyQualifiedName~BackupRestoreTests`
Expected: FAIL — the 3-arg constructor, `HasBackup`, and `RestoreOriginalCoverArtAsync` do not exist.

- [ ] **Step 3: Implement**

In `Services/IServices.cs`, extend `ILibraryDetectionService`:

```csharp
    bool HasBackup(string libraryId);
    Task<bool> RestoreOriginalCoverArtAsync(string libraryId);
```

In `Services/LibraryDetectionService.cs`:

Add `using CustomCoverArt.Common;` and `using MediaBrowser.Common.Configuration;`. Add the field and extend the constructor:

```csharp
    private readonly IApplicationPaths _applicationPaths;

    public LibraryDetectionService(
        ILibraryManager libraryManager,
        ILoggingService loggingService,
        IApplicationPaths applicationPaths)
    {
        _libraryManager = libraryManager;
        _loggingService = loggingService;
        _applicationPaths = applicationPaths;
    }
```

Add a private path resolver and `HasBackup`:

```csharp
    private string BackupPathFor(string targetId, string extension)
        => Path.Combine(PluginPaths.Backups(_applicationPaths), targetId, "original" + extension);

    private string? ExistingBackupPath(string targetId)
    {
        var dir = Path.Combine(PluginPaths.Backups(_applicationPaths), targetId);
        if (!Directory.Exists(dir)) return null;
        var files = Directory.GetFiles(dir, "original.*");
        return files.Length > 0 ? files[0] : null;
    }

    public bool HasBackup(string libraryId) => ExistingBackupPath(libraryId) is not null;
```

Replace the body of `BackupCurrentCoverArtAsync` so it is deterministic and original-preserving:

```csharp
    public Task<string?> BackupCurrentCoverArtAsync(string libraryId)
    {
        try
        {
            if (!Guid.TryParse(libraryId, out var id))
            {
                return Task.FromResult<string?>(null);
            }

            // Never overwrite an existing backup: the first one is the true original.
            var existing = ExistingBackupPath(libraryId);
            if (existing is not null)
            {
                return Task.FromResult<string?>(existing);
            }

            var item = _libraryManager.GetItemById<BaseItem>(id);
            if (item is null || !item.HasImage(ImageType.Primary))
            {
                return Task.FromResult<string?>(null);
            }

            var currentPath = item.GetImagePath(ImageType.Primary, 0);
            if (string.IsNullOrEmpty(currentPath) || !File.Exists(currentPath))
            {
                return Task.FromResult<string?>(null);
            }

            var backupPath = BackupPathFor(libraryId, Path.GetExtension(currentPath));
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Copy(currentPath, backupPath, overwrite: false);
            return Task.FromResult<string?>(backupPath);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to back up cover art for {LibraryId}", ex, libraryId);
            return Task.FromResult<string?>(null);
        }
    }
```

Add `RestoreOriginalCoverArtAsync` (reusing the existing lower-level `RestoreCoverArtAsync(id, path)`):

```csharp
    public async Task<bool> RestoreOriginalCoverArtAsync(string libraryId)
    {
        var backup = ExistingBackupPath(libraryId);
        if (backup is null)
        {
            return false;
        }

        return await RestoreCoverArtAsync(libraryId, backup).ConfigureAwait(false);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CustomCoverArt.Tests --filter FullyQualifiedName~BackupRestoreTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Build the whole solution** (the constructor change breaks DI callers until Task 4/registrator; verify only the plugin project compiles what it can)

Run: `dotnet build CustomCoverArt.csproj`
Expected: succeeds (DI is resolved by reflection at runtime, not compile time; no other code news up `LibraryDetectionService` directly).

- [ ] **Step 6: Commit**

```bash
git add Services/IServices.cs Services/LibraryDetectionService.cs tests/CustomCoverArt.Tests/BackupRestoreTests.cs
git commit -m "feat(restore): original-preserving backup registry and restore"
```

---

### Task 4: Restore + hasBackup endpoints; auto-backup on apply

**Files:**
- Modify: `Controllers/CustomCoverArtController.cs`

**Interfaces:**
- Consumes: `ILibraryDetectionService.HasBackup`, `RestoreOriginalCoverArtAsync`, `BackupCurrentCoverArtAsync` (Task 3).
- Produces: `GET /CustomCoverArt/targets/{id}/backup` → `ApiResponse<bool>`; `POST /CustomCoverArt/targets/{type}/{id}/restore` → `ApiResponse<bool>`. Auto-backup call inserted into `ApplyInternal` before `UpdateLibraryCoverArtAsync`.

- [ ] **Step 1: Add the auto-backup line to `ApplyInternal`**

In `ApplyInternal`, immediately BEFORE the `UpdateLibraryCoverArtAsync` call, add:

```csharp
        // Preserve the target's current image once, so Restore can undo later.
        await _libraryService.BackupCurrentCoverArtAsync(libraryId).ConfigureAwait(false);
```

- [ ] **Step 2: Add the two endpoints**

Add near the other target endpoints:

```csharp
    /// <summary>Whether a restore point (original cover backup) exists for a target.</summary>
    [HttpGet("targets/{id}/backup")]
    public ApiResponse<bool> HasBackup(string id)
    {
        if (!Guid.TryParse(id, out _))
        {
            return Fail<bool>("Invalid target id");
        }

        return Success(_libraryService.HasBackup(id));
    }

    /// <summary>Restore a target's original (pre-plugin) primary image.</summary>
    [HttpPost("targets/{type}/{id}/restore")]
    public async Task<ApiResponse<bool>> RestoreOriginal(string type, string id)
    {
        if (!Guid.TryParse(id, out _))
        {
            return Fail<bool>("Invalid target id");
        }

        try
        {
            var ok = await _libraryService.RestoreOriginalCoverArtAsync(id).ConfigureAwait(false);
            return ok ? Success(true) : Fail<bool>("No original cover backup found for this target.");
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to restore original cover for {Id}", ex, id);
            return Fail<bool>("Failed to restore original cover.");
        }
    }
```

(The `{type}` segment is accepted for URL symmetry with other target routes but is not needed to resolve the item.)

- [ ] **Step 3: Build**

Run: `dotnet build CustomCoverArt.csproj`
Expected: succeeds.

- [ ] **Step 4: Commit**

```bash
git add Controllers/CustomCoverArtController.cs
git commit -m "feat(restore): backup-check + restore endpoints, auto-backup on apply"
```

---

### Task 5: Config page — Restore button

**Files:**
- Modify: `Configuration/configPage.html`

**Manual verification** (no JS test harness exists; verify by build + load).

- [ ] **Step 1: Add the button to the Target card**

Inside the Target card (the `ccaCard` whose head is `data-i18n="card.target"`), after the target `<select>` row, add:

```html
    <div class="ccaFileRow">
        <button is="emby-button" type="button" id="ccaRestoreBtn" class="raised" disabled>
            <span data-i18n="restore.button">Restore original cover</span>
        </button>
        <span class="ccaFileName" id="ccaRestoreHint" data-i18n="restore.none">No backup yet</span>
    </div>
```

- [ ] **Step 2: Add i18n keys**

In `I18N.en` add: `'restore.button': 'Restore original cover', 'restore.none': 'No backup yet', 'restore.available': 'A backup exists', 'restore.done': 'Original cover restored.', 'restore.fail': 'No backup found to restore.'`
In `I18N.nl` add the Dutch equivalents: `'restore.button': 'Originele cover herstellen', 'restore.none': 'Nog geen back-up', 'restore.available': 'Er is een back-up', 'restore.done': 'Originele cover hersteld.', 'restore.fail': 'Geen back-up gevonden om te herstellen.'`

- [ ] **Step 3: Wire the button**

In the target-select change handler (and at the end of `loadLibraries()` after a target is chosen), refresh the button state; add a helper and a click handler:

```javascript
function refreshRestoreState() {
    var btn = el('ccaRestoreBtn');
    var hint = el('ccaRestoreHint');
    if (!btn) return;
    if (!state.libraryId) { btn.disabled = true; hint.textContent = t('restore.none'); return; }
    jsonFetch('CustomCoverArt/targets/' + encodeURIComponent(state.libraryId) + '/backup').then(function (res) {
        var has = pick(res, 'Success') && pick(res, 'Data');
        btn.disabled = !has;
        hint.textContent = has ? t('restore.available') : t('restore.none');
    });
}

el('ccaRestoreBtn').addEventListener('click', function () {
    if (!state.libraryId) return;
    var type = el('ccaTargetType') ? el('ccaTargetType').value : 'library';
    jsonFetch('CustomCoverArt/targets/' + type + '/' + encodeURIComponent(state.libraryId) + '/restore', 'POST')
        .then(function (res) {
            Dashboard.alert(pick(res, 'Success') ? t('restore.done') : t('restore.fail'));
        });
});
```

Call `refreshRestoreState()` at the end of the existing library `<select>` change handler and after `loadLibraries()` resolves.

- [ ] **Step 4: Build + manual check**

Run: `dotnet build CustomCoverArt.csproj` (embeds the HTML; must succeed).
Manual: load the plugin page, pick a target that has had a cover applied → the button enables; click → alert confirms restore; the library's image reverts on next dashboard refresh.

- [ ] **Step 5: Commit**

```bash
git add Configuration/configPage.html
git commit -m "feat(restore): config-page restore button + i18n"
```

---

## Phase 2 — Feature 2: Poster-collage background

### Task 6: `MediaItemService.GetPosterPathsAsync`

**Files:**
- Modify: `Services/IServices.cs`
- Modify: `Services/MediaItemService.cs`

**Interfaces:**
- Produces on `IMediaItemService`: `Task<IReadOnlyList<string>> GetPosterPathsAsync(string parentId, int max)` — returns primary-image file paths of a target's child items (empty list if none / invalid id / no posters). Works for library, collection and playlist ids via `ParentId` query.

- [ ] **Step 1: Add to the interface**

In `Services/IServices.cs`, add to `IMediaItemService`:

```csharp
    Task<IReadOnlyList<string>> GetPosterPathsAsync(string parentId, int max);
```

- [ ] **Step 2: Implement** (mirror the existing `GetLibraryItemsAsync` query, but collect image paths)

In `Services/MediaItemService.cs`:

```csharp
    public Task<IReadOnlyList<string>> GetPosterPathsAsync(string parentId, int max)
    {
        try
        {
            if (!Guid.TryParse(parentId, out var id))
            {
                return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            }

            var query = new InternalItemsQuery
            {
                ParentId = id,
                ImageTypes = new[] { ImageType.Primary },
                Recursive = true,
                Limit = max
            };

            var paths = new List<string>();
            foreach (var item in _libraryManager.GetItemList(query))
            {
                if (item.HasImage(ImageType.Primary))
                {
                    var p = item.GetImagePath(ImageType.Primary, 0);
                    if (!string.IsNullOrEmpty(p) && File.Exists(p))
                    {
                        paths.Add(p);
                    }
                }
            }

            return Task.FromResult<IReadOnlyList<string>>(paths);
        }
        catch (Exception ex)
        {
            _loggingService.LogError("Failed to get poster paths for {ParentId}", ex, parentId);
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
    }
```

(Match the existing file's `using`s, logger field name, and `InternalItemsQuery` usage from `GetLibraryItemsAsync`. Add `using System.IO;` / `using System.Linq;` if missing.)

- [ ] **Step 3: Build**

Run: `dotnet build CustomCoverArt.csproj`
Expected: succeeds. (No unit test — this is a thin adapter over `ILibraryManager.GetItemList`; it is exercised via the collage integration in Task 8 and manual verification.)

- [ ] **Step 4: Commit**

```bash
git add Services/IServices.cs Services/MediaItemService.cs
git commit -m "feat(collage): fetch child poster paths for a target"
```

---

### Task 7: `CollageComposer` grid mosaic

**Files:**
- Create: `Services/CollageComposer.cs`
- Test: `tests/CustomCoverArt.Tests/CollageComposerTests.cs`

**Interfaces:**
- Produces: `CollageComposer.ColumnsFor(string density)` → `int` (sparse=4, medium=6, dense=8); `CollageComposer.BuildCollage(IReadOnlyList<string> posterPaths, int width, int height, string density, int seed)` → `Image<Rgba32>`. On empty `posterPaths`, returns a solid dark canvas (never throws). Fewer posters than tiles → posters repeat to fill.

- [ ] **Step 1: Write the failing tests** (grid math + empty + fill are the testable core; image tiling is verified by asserting output size and non-null)

```csharp
// tests/CustomCoverArt.Tests/CollageComposerTests.cs
using System;
using System.Collections.Generic;
using System.IO;
using CustomCoverArt.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace CustomCoverArt.Tests;

public class CollageComposerTests
{
    [Theory]
    [InlineData("sparse", 4)]
    [InlineData("medium", 6)]
    [InlineData("dense", 8)]
    [InlineData("unknown", 6)]
    public void ColumnsFor_MapsDensity(string density, int expected)
    {
        Assert.Equal(expected, CollageComposer.ColumnsFor(density));
    }

    [Fact]
    public void BuildCollage_EmptyPosters_ReturnsCanvasOfRequestedSize()
    {
        using var img = CollageComposer.BuildCollage(new List<string>(), 800, 600, "medium", 0);
        Assert.Equal(800, img.Width);
        Assert.Equal(600, img.Height);
    }

    [Fact]
    public void BuildCollage_FewerPostersThanTiles_StillFillsWithoutThrowing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cca-collage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var paths = new List<string>();
        for (int i = 0; i < 2; i++)
        {
            var p = Path.Combine(dir, $"p{i}.png");
            using (var poster = new Image<Rgba32>(100, 150, Color.Blue)) { poster.Save(p); }
            paths.Add(p);
        }

        using var img = CollageComposer.BuildCollage(paths, 800, 600, "sparse", 42);
        Assert.Equal(800, img.Width);
        Assert.Equal(600, img.Height);

        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public void BuildCollage_IsDeterministicForSameSeed()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cca-collage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var paths = new List<string>();
        for (int i = 0; i < 6; i++)
        {
            var p = Path.Combine(dir, $"p{i}.png");
            using (var poster = new Image<Rgba32>(100, 150, i % 2 == 0 ? Color.Red : Color.Green)) { poster.Save(p); }
            paths.Add(p);
        }

        using var a = CollageComposer.BuildCollage(paths, 400, 400, "medium", 7);
        using var b = CollageComposer.BuildCollage(paths, 400, 400, "medium", 7);
        Assert.Equal(a[0, 0], b[0, 0]);
        Assert.Equal(a[200, 200], b[200, 200]);

        try { Directory.Delete(dir, true); } catch { }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CustomCoverArt.Tests --filter FullyQualifiedName~CollageComposerTests`
Expected: FAIL — `CollageComposer` does not exist.

- [ ] **Step 3: Implement**

```csharp
// Services/CollageComposer.cs
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace CustomCoverArt.Services;

/// <summary>
/// Builds a full-bleed grid mosaic from a set of poster image files.
/// Pure image work — no Jellyfin API calls. Never throws on bad/missing files.
/// </summary>
public static class CollageComposer
{
    public static int ColumnsFor(string density) => (density ?? "medium").ToLowerInvariant() switch
    {
        "sparse" => 4,
        "dense" => 8,
        _ => 6,
    };

    public static Image<Rgba32> BuildCollage(
        IReadOnlyList<string> posterPaths, int width, int height, string density, int seed)
    {
        var canvas = new Image<Rgba32>(width, height, Color.FromRgb(18, 18, 18));
        if (posterPaths is null || posterPaths.Count == 0)
        {
            return canvas;
        }

        var cols = ColumnsFor(density);
        var tileW = (int)System.Math.Ceiling(width / (double)cols);
        var tileH = (int)System.Math.Ceiling(tileW * 3.0 / 2.0); // 2:3 poster aspect
        var rows = (int)System.Math.Ceiling(height / (double)tileH);

        // Deterministic shuffle by seed.
        var order = new List<int>();
        for (int i = 0; i < posterPaths.Count; i++) order.Add(i);
        var rng = new System.Random(seed);
        for (int i = order.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }

        int tileIndex = 0;
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                // Repeat posters to fill the grid.
                var path = posterPaths[order[tileIndex % order.Count]];
                tileIndex++;
                try
                {
                    using var poster = Image.Load<Rgba32>(path);
                    poster.Mutate(x => x.Resize(new ResizeOptions
                    {
                        Size = new Size(tileW, tileH),
                        Mode = ResizeMode.Crop, // cover-crop into the tile
                    }));
                    canvas.Mutate(x => x.DrawImage(poster, new Point(c * tileW, r * tileH), 1f));
                }
                catch
                {
                    // Skip unreadable poster; leave the dark canvas showing through.
                }
            }
        }

        return canvas;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/CustomCoverArt.Tests --filter FullyQualifiedName~CollageComposerTests`
Expected: PASS (7 cases).

- [ ] **Step 5: Commit**

```bash
git add Services/CollageComposer.cs tests/CustomCoverArt.Tests/CollageComposerTests.cs
git commit -m "feat(collage): grid-mosaic composer with deterministic shuffle"
```

---

### Task 8: Wire collage into `CoverArtService` + DI

**Files:**
- Modify: `Services/CoverArtService.cs`
- Modify: `PluginServiceRegistrator.cs`

**Interfaces:**
- Consumes: `IMediaItemService.GetPosterPathsAsync` (Task 6), `CollageComposer.BuildCollage` (Task 7).
- Produces: when `settings.BackgroundSource == "collage"`, `GenerateCoverArtAsync` uses a collage as the background instead of `BackgroundImagePath`.

- [ ] **Step 1: Register `CollageComposer` DI (no-op if static)**

`CollageComposer` is static, so nothing to register. Confirm `IMediaItemService` is registered as Scoped in `PluginServiceRegistrator.cs` (it already is per the existing registrations). No change needed unless `CoverArtService`'s constructor gains a new dependency in Step 2 — then ensure `IMediaItemService` remains resolvable in `CoverArtService`'s scope (both are Scoped — OK).

- [ ] **Step 2: Add `IMediaItemService` to `CoverArtService`**

Read the current `CoverArtService` constructor. Add `IMediaItemService mediaItemService` as a parameter, store it in a field `_mediaItemService`. Add `using CustomCoverArt.Models;` if not present.

- [ ] **Step 3: Build the collage background before canvas composition**

In `GenerateCoverArtAsync`, where the background image is currently loaded from `settings.BackgroundImagePath` (around the `Image.Identify` / `Image.LoadAsync` block, lines ~108–140), add a branch BEFORE that block:

```csharp
        Image? backgroundImage = null;

        if (settings.BackgroundSource == "collage" && settings.Collage is not null
            && !string.IsNullOrEmpty(settings.Collage.SourceId))
        {
            var posters = await _mediaItemService
                .GetPosterPathsAsync(settings.Collage.SourceId, 60)
                .ConfigureAwait(false);

            backgroundImage = CollageComposer.BuildCollage(
                posters, settings.ExportWidth, settings.ExportHeight,
                settings.Collage.Density, settings.Collage.Seed);
        }
        else if (!string.IsNullOrEmpty(settings.BackgroundImagePath))
        {
            // ... existing Image.Identify + Image.LoadAsync block stays here ...
        }
```

Ensure the existing `backgroundImage` variable declaration is not duplicated (reuse the one above). The collage image is already export-sized, so the existing `ApplyBackgroundAsync(...)` path (with `BackgroundFit`) composites it correctly; no other change needed.

- [ ] **Step 4: Build**

Run: `dotnet build CustomCoverArt.csproj`
Expected: succeeds.

- [ ] **Step 5: Run the full test suite** (nothing should regress)

Run: `dotnet test tests/CustomCoverArt.Tests`
Expected: PASS (all).

- [ ] **Step 6: Commit**

```bash
git add Services/CoverArtService.cs PluginServiceRegistrator.cs
git commit -m "feat(collage): use poster collage as background source in render"
```

---

### Task 9: Config page — background source + collage controls

**Files:**
- Modify: `Configuration/configPage.html`

- [ ] **Step 1: Add a background-source selector + collage controls to the Background card**

At the top of the Background card, before the dimming row, add:

```html
    <div class="inputContainer">
        <label for="ccaBgSource" data-i18n="bg.source">Background source</label>
        <select is="emby-select" id="ccaBgSource" class="emby-select">
            <option value="upload" selected data-i18n="bg.source.upload">Upload / poster</option>
            <option value="collage" data-i18n="bg.source.collage">Poster collage from this target</option>
        </select>
    </div>
    <div class="ccaGrid" id="ccaCollageRow" style="display:none">
        <div class="inputContainer">
            <label for="ccaCollageDensity" data-i18n="bg.collage.density">Collage density</label>
            <select is="emby-select" id="ccaCollageDensity" class="emby-select">
                <option value="sparse" data-i18n="bg.collage.sparse">Sparse</option>
                <option value="medium" selected data-i18n="bg.collage.medium">Medium</option>
                <option value="dense" data-i18n="bg.collage.dense">Dense</option>
            </select>
        </div>
        <div class="inputContainer">
            <label>&nbsp;</label>
            <button is="emby-button" type="button" id="ccaCollageShuffle" class="raised">
                <span data-i18n="bg.collage.shuffle">Shuffle</span>
            </button>
        </div>
    </div>
```

- [ ] **Step 2: Add i18n keys**

`I18N.en`: `'bg.source': 'Background source', 'bg.source.upload': 'Upload / poster', 'bg.source.collage': 'Poster collage from this target', 'bg.collage.density': 'Collage density', 'bg.collage.sparse': 'Sparse', 'bg.collage.medium': 'Medium', 'bg.collage.dense': 'Dense', 'bg.collage.shuffle': 'Shuffle', 'bg.collage.livetv': 'Live TV has no posters to collage.'`
`I18N.nl`: `'bg.source': 'Achtergrondbron', 'bg.source.upload': 'Upload / poster', 'bg.source.collage': 'Postercollage van dit doel', 'bg.collage.density': 'Collagedichtheid', 'bg.collage.sparse': 'Dun', 'bg.collage.medium': 'Gemiddeld', 'bg.collage.dense': 'Dicht', 'bg.collage.shuffle': 'Schudden', 'bg.collage.livetv': 'Live TV heeft geen posters voor een collage.'`

- [ ] **Step 3: State + toggle + Live-TV guard**

Add to the `state` object: `collageSeed: 1`. Add handlers:

```javascript
function updateCollageVisibility() {
    var isCollage = el('ccaBgSource').value === 'collage';
    var type = el('ccaTargetType') ? el('ccaTargetType').value : 'library';
    // Live TV has no child posters; disable collage for it.
    var opt = el('ccaBgSource').querySelector('option[value="collage"]');
    if (opt) opt.disabled = (type === 'livetv');
    if (type === 'livetv' && isCollage) { el('ccaBgSource').value = 'upload'; isCollage = false; }
    el('ccaCollageRow').style.display = isCollage ? '' : 'none';
    updateUI();
}

el('ccaBgSource').addEventListener('change', function () { updateCollageVisibility(); runPreview(); });
el('ccaCollageDensity').addEventListener('change', runPreview);
el('ccaCollageShuffle').addEventListener('click', function () { state.collageSeed = (state.collageSeed + 1) & 0x7fffffff; runPreview(); });
```

Call `updateCollageVisibility()` at the end of the target-type change handler and after `loadLibraries()` resolves.

- [ ] **Step 4: Extend `collectSettings()`**

In the returned object, add:

```javascript
        BackgroundSource: el('ccaBgSource').value,
        Collage: {
            SourceId: state.libraryId || '',
            SourceType: (el('ccaTargetType') ? el('ccaTargetType').value : 'library'),
            Density: el('ccaCollageDensity').value,
            Seed: state.collageSeed
        },
```

- [ ] **Step 5: Build + manual check**

Run: `dotnet build CustomCoverArt.csproj`
Manual: pick a library, choose "Poster collage" → preview shows a dimmed grid of that library's posters; Shuffle re-rolls it; switching target to Live TV disables the collage option.

- [ ] **Step 6: Commit**

```bash
git add Configuration/configPage.html
git commit -m "feat(collage): config-page background source + density + shuffle"
```

---

## Phase 3 — Feature 3: Design templates + batch apply

### Task 10: Template CRUD endpoints

**Files:**
- Modify: `Controllers/CustomCoverArtController.cs`
- Test: `tests/CustomCoverArt.Tests/TemplateTests.cs` (create)

**Interfaces:**
- Produces: `GET /CustomCoverArt/templates` → `ApiResponse<List<SavedTemplate>>`; `POST /CustomCoverArt/templates` `[FromBody] SavedTemplate` → `ApiResponse<bool>` (upsert by name, Title blanked); `DELETE /CustomCoverArt/templates/{name}` → `ApiResponse<bool>`. Persist via `Plugin.Instance.Configuration.Templates` + `Plugin.Instance.SaveConfiguration()`. A pure static helper `CustomCoverArtController.NormalizeTemplate(SavedTemplate)` (title/target stripped) is unit-tested.

- [ ] **Step 1: Write the failing test** (the persistence path needs a running plugin host, so unit-test the pure normalization helper)

```csharp
// tests/CustomCoverArt.Tests/TemplateTests.cs
using CustomCoverArt.Controllers;
using CustomCoverArt.Models;
using Xunit;

namespace CustomCoverArt.Tests;

public class TemplateTests
{
    [Fact]
    public void NormalizeTemplate_StripsTitleAndTargetSpecificFields()
    {
        var t = new SavedTemplate
        {
            Name = "  Neon  ",
            Settings = new CoverArtSettings
            {
                Title = "Movies",
                TextSize = 180,
                BackgroundSource = "collage",
                Collage = new CollageSettings { SourceId = "abc-123", Density = "dense" }
            }
        };

        var n = CustomCoverArtController.NormalizeTemplate(t);

        Assert.Equal("Neon", n.Name);                 // trimmed
        Assert.Equal(string.Empty, n.Settings.Title); // title stripped
        Assert.Equal(180, n.Settings.TextSize);       // design kept
        Assert.Equal("collage", n.Settings.BackgroundSource);
        // Collage SourceId is target-specific → cleared; density (a design choice) kept.
        Assert.Equal(string.Empty, n.Settings.Collage!.SourceId);
        Assert.Equal("dense", n.Settings.Collage!.Density);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CustomCoverArt.Tests --filter FullyQualifiedName~TemplateTests`
Expected: FAIL — `NormalizeTemplate` does not exist.

- [ ] **Step 3: Implement the helper + endpoints**

Add the static helper and endpoints to `CustomCoverArtController`:

```csharp
    /// <summary>Strip title and target-specific fields so a template is reusable across targets.</summary>
    public static SavedTemplate NormalizeTemplate(SavedTemplate template)
    {
        template.Name = (template.Name ?? string.Empty).Trim();
        template.Settings.Title = string.Empty;
        if (template.Settings.Collage is not null)
        {
            template.Settings.Collage.SourceId = string.Empty;
        }
        return template;
    }

    /// <summary>List saved design templates.</summary>
    [HttpGet("templates")]
    public ApiResponse<List<SavedTemplate>> GetTemplates()
    {
        var list = Plugin.Instance?.Configuration.Templates ?? new List<SavedTemplate>();
        return Success(list);
    }

    /// <summary>Save (upsert by name) a design template.</summary>
    [HttpPost("templates")]
    public ApiResponse<bool> SaveTemplate([FromBody] SavedTemplate template)
    {
        if (template is null || string.IsNullOrWhiteSpace(template.Name))
        {
            return Fail<bool>("Template name is required.");
        }

        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null)
        {
            return Fail<bool>("Plugin not initialized.");
        }

        var normalized = NormalizeTemplate(template);
        cfg.Templates.RemoveAll(t => string.Equals(t.Name, normalized.Name, StringComparison.OrdinalIgnoreCase));
        cfg.Templates.Add(normalized);
        Plugin.Instance!.SaveConfiguration();
        return Success(true);
    }

    /// <summary>Delete a design template by name.</summary>
    [HttpDelete("templates/{name}")]
    public ApiResponse<bool> DeleteTemplate(string name)
    {
        var cfg = Plugin.Instance?.Configuration;
        if (cfg is null)
        {
            return Fail<bool>("Plugin not initialized.");
        }

        cfg.Templates.RemoveAll(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        Plugin.Instance!.SaveConfiguration();
        return Success(true);
    }
```

Add `using CustomCoverArt.Configuration;` / `using System;` / `using System.Collections.Generic;` if not present. If `Plugin.Instance.SaveConfiguration()` is not accessible, use `Plugin.Instance.UpdateConfiguration(cfg)` instead (both persist; pick whichever the Jellyfin `BasePlugin<T>` in use exposes as public).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CustomCoverArt.Tests --filter FullyQualifiedName~TemplateTests`
Expected: PASS.

- [ ] **Step 5: Build**

Run: `dotnet build CustomCoverArt.csproj`
Expected: succeeds.

- [ ] **Step 6: Commit**

```bash
git add Controllers/CustomCoverArtController.cs tests/CustomCoverArt.Tests/TemplateTests.cs
git commit -m "feat(templates): save/list/delete design template endpoints"
```

---

### Task 11: `batchApply` endpoint

**Files:**
- Modify: `Controllers/CustomCoverArtController.cs`
- Test: `tests/CustomCoverArt.Tests/TemplateTests.cs` (add cases)

**Interfaces:**
- Consumes: `ApplyInternal(libraryId, settings)` (existing, now auto-backing-up), `Plugin.Instance.Configuration.Templates`, `_libraryManager`? No — resolve target name via `_libraryService`. Add a pure helper `BuildBatchSettings(baseSettings, targetName, targetId)` that clones settings, sets `Title = targetName`, and if `BackgroundSource == "collage"` sets `Collage.SourceId = targetId`. This helper is unit-tested.
- Produces: `POST /CustomCoverArt/batchApply` `[FromBody] BatchApplyRequest` → `ApiResponse<List<BatchApplyResult>>`.

- [ ] **Step 1: Write the failing test for the pure helper**

Add to `TemplateTests`:

```csharp
    [Fact]
    public void BuildBatchSettings_SetsTitleAndCollageSource()
    {
        var baseSettings = new CoverArtSettings
        {
            Title = "",
            BackgroundSource = "collage",
            Collage = new CollageSettings { SourceId = "", Density = "medium" }
        };

        var built = CustomCoverArtController.BuildBatchSettings(baseSettings, "Kids", "target-9");

        Assert.Equal("Kids", built.Title);
        Assert.Equal("target-9", built.Collage!.SourceId);
        // Original is not mutated (clone).
        Assert.Equal("", baseSettings.Title);
    }

    [Fact]
    public void BuildBatchSettings_NonCollageLeavesCollageNull()
    {
        var baseSettings = new CoverArtSettings { BackgroundSource = "upload" };
        var built = CustomCoverArtController.BuildBatchSettings(baseSettings, "Movies", "id-1");
        Assert.Equal("Movies", built.Title);
        Assert.Null(built.Collage);
    }
```

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test tests/CustomCoverArt.Tests --filter FullyQualifiedName~TemplateTests`
Expected: FAIL — `BuildBatchSettings` does not exist.

- [ ] **Step 3: Implement helper + endpoint**

```csharp
    /// <summary>Clone base settings for one batch target: title = target name, collage source = target id.</summary>
    public static CoverArtSettings BuildBatchSettings(CoverArtSettings baseSettings, string targetName, string targetId)
    {
        // Shallow JSON clone to avoid mutating the shared base settings.
        var json = System.Text.Json.JsonSerializer.Serialize(baseSettings);
        var clone = System.Text.Json.JsonSerializer.Deserialize<CoverArtSettings>(json) ?? new CoverArtSettings();
        clone.Title = targetName;
        if (clone.BackgroundSource == BackgroundSources.Collage && clone.Collage is not null)
        {
            clone.Collage.SourceId = targetId;
        }
        return clone;
    }

    /// <summary>Apply one design to many targets, auto-titling each from the target's name.</summary>
    [HttpPost("batchApply")]
    public async Task<ApiResponse<List<BatchApplyResult>>> BatchApply([FromBody] BatchApplyRequest request)
    {
        if (request is null || request.Targets.Count == 0)
        {
            return Fail<List<BatchApplyResult>>("No targets selected.");
        }

        // Resolve the base design: a named template, or inline settings.
        CoverArtSettings? baseSettings = request.Settings;
        if (!string.IsNullOrWhiteSpace(request.TemplateName))
        {
            var tpl = Plugin.Instance?.Configuration.Templates
                .FirstOrDefault(t => string.Equals(t.Name, request.TemplateName, StringComparison.OrdinalIgnoreCase));
            if (tpl is null)
            {
                return Fail<List<BatchApplyResult>>("Template not found: " + request.TemplateName);
            }
            baseSettings = tpl.Settings;
        }

        if (baseSettings is null)
        {
            return Fail<List<BatchApplyResult>>("No template or settings provided.");
        }

        var results = new List<BatchApplyResult>();
        foreach (var target in request.Targets)
        {
            var result = new BatchApplyResult { Id = target.Id };
            if (!Guid.TryParse(target.Id, out _))
            {
                result.Success = false;
                result.Error = "Invalid id";
                results.Add(result);
                continue;
            }

            var info = await _libraryService.GetLibraryByIdAsync(target.Id).ConfigureAwait(false);
            var name = info?.Name ?? "Cover";
            result.Name = name;

            var settings = BuildBatchSettings(baseSettings, name, target.Id);
            var applied = await ApplyInternal(target.Id, settings).ConfigureAwait(false);
            result.Success = applied.Success;
            result.Error = applied.ErrorMessage;
            results.Add(result);
        }

        return Success(results);
    }
```

Add `using System.Linq;` if not present.

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/CustomCoverArt.Tests --filter FullyQualifiedName~TemplateTests`
Expected: PASS (all TemplateTests).

- [ ] **Step 5: Build**

Run: `dotnet build CustomCoverArt.csproj`
Expected: succeeds.

- [ ] **Step 6: Commit**

```bash
git add Controllers/CustomCoverArtController.cs tests/CustomCoverArt.Tests/TemplateTests.cs
git commit -m "feat(batch): apply one design to many targets, auto-titled"
```

---

### Task 12: Config page — template card + batch-apply section

**Files:**
- Modify: `Configuration/configPage.html`

- [ ] **Step 1: Add a Templates card** (after the Text card, before Output)

```html
<section class="ccaCard">
    <div class="ccaCardHead" data-i18n="card.templates">Templates</div>
    <div class="ccaGrid">
        <div class="inputContainer">
            <label for="ccaTemplateSelect" data-i18n="tpl.load">Load template</label>
            <select is="emby-select" id="ccaTemplateSelect" class="emby-select"></select>
        </div>
        <div class="inputContainer">
            <label>&nbsp;</label>
            <button is="emby-button" type="button" id="ccaTemplateDelete" class="raised">
                <span data-i18n="tpl.delete">Delete</span>
            </button>
        </div>
    </div>
    <div class="ccaFileRow">
        <input type="text" id="ccaTemplateName" class="emby-input" placeholder="Template name" />
        <button is="emby-button" type="button" id="ccaTemplateSave" class="raised">
            <span data-i18n="tpl.save">Save current design</span>
        </button>
    </div>
    <div class="fieldDescription" data-i18n="tpl.hint">Templates store the design but not the title — each target keeps its own name.</div>
</section>

<section class="ccaCard">
    <div class="ccaCardHead" data-i18n="card.batch">Batch apply</div>
    <div class="fieldDescription" data-i18n="batch.hint">Apply the current design (or loaded template) to several targets at once. Each cover is titled with its target's name.</div>
    <div id="ccaBatchList" class="ccaBatchList"></div>
    <div class="ccaFileRow">
        <button is="emby-button" type="button" id="ccaBatchApply" class="raised">
            <span data-i18n="batch.apply">Apply to selected</span>
        </button>
        <span class="ccaFileName" id="ccaBatchStatus"></span>
    </div>
</section>
```

- [ ] **Step 2: Add i18n keys**

`I18N.en`: `'card.templates': 'Templates', 'tpl.load': 'Load template', 'tpl.delete': 'Delete', 'tpl.save': 'Save current design', 'tpl.hint': 'Templates store the design but not the title — each target keeps its own name.', 'tpl.saved': 'Template saved.', 'tpl.named': 'Enter a template name first.', 'card.batch': 'Batch apply', 'batch.hint': 'Apply the current design (or loaded template) to several targets at once. Each cover is titled with its target\'s name.', 'batch.apply': 'Apply to selected', 'batch.none': 'Select at least one target.', 'batch.running': 'Applying…', 'batch.done': 'Done: {0} ok, {1} failed.'`
`I18N.nl`: Dutch equivalents (`'card.templates': 'Sjablonen', 'tpl.load': 'Sjabloon laden', 'tpl.delete': 'Verwijderen', 'tpl.save': 'Huidig ontwerp opslaan', 'tpl.hint': 'Sjablonen bewaren het ontwerp maar niet de titel — elk doel houdt zijn eigen naam.', 'tpl.saved': 'Sjabloon opgeslagen.', 'tpl.named': 'Voer eerst een sjabloonnaam in.', 'card.batch': 'Bulk toepassen', 'batch.hint': 'Pas het huidige ontwerp (of geladen sjabloon) in één keer toe op meerdere doelen. Elke cover krijgt de naam van zijn doel als titel.', 'batch.apply': 'Toepassen op selectie', 'batch.none': 'Selecteer minstens één doel.', 'batch.running': 'Bezig met toepassen…', 'batch.done': 'Klaar: {0} gelukt, {1} mislukt.'`).

- [ ] **Step 3: Template load/save/delete JS**

```javascript
function loadTemplates() {
    return jsonFetch('CustomCoverArt/templates').then(function (res) {
        var sel = el('ccaTemplateSelect');
        sel.innerHTML = '';
        var none = document.createElement('option'); none.value = ''; none.textContent = '—'; sel.appendChild(none);
        (pick(res, 'Data') || []).forEach(function (tpl) {
            var o = document.createElement('option');
            o.value = pick(tpl, 'Name'); o.textContent = pick(tpl, 'Name');
            o._settings = pick(tpl, 'Settings');
            sel.appendChild(o);
        });
    });
}

el('ccaTemplateSelect').addEventListener('change', function () {
    var opt = this.options[this.selectedIndex];
    if (opt && opt._settings) { applySettingsToForm(opt._settings); runPreview(); }
});

el('ccaTemplateSave').addEventListener('click', function () {
    var name = el('ccaTemplateName').value.trim();
    if (!name) { Dashboard.alert(t('tpl.named')); return; }
    jsonFetch('CustomCoverArt/templates', 'POST', { Name: name, Settings: collectSettings() })
        .then(function () { Dashboard.alert(t('tpl.saved')); loadTemplates(); });
});

el('ccaTemplateDelete').addEventListener('click', function () {
    var name = el('ccaTemplateSelect').value;
    if (!name) return;
    jsonFetch('CustomCoverArt/templates/' + encodeURIComponent(name), 'DELETE').then(loadTemplates);
});
```

`applySettingsToForm(settings)` must set each control from a settings object (the inverse of `collectSettings()`), EXCEPT `Title`. Implement it to set: text size/weight/color/align, dim/blur/dim-color, shadow/outline checks, gradient fields, background source + collage density, format + animation controls. (Set only the fields present; ignore `Title` and `Collage.SourceId`.)

- [ ] **Step 4: Batch list + apply JS**

```javascript
function loadBatchList() {
    var type = el('ccaTargetType') ? el('ccaTargetType').value : 'library';
    jsonFetch('CustomCoverArt/targets/' + type).then(function (res) {
        var box = el('ccaBatchList'); box.innerHTML = '';
        (pick(res, 'Data') || []).forEach(function (lib) {
            var id = pick(lib, 'Id'), name = pick(lib, 'Name');
            var row = document.createElement('label'); row.className = 'ccaCheck ccaCheckBlock';
            var cb = document.createElement('input'); cb.type = 'checkbox'; cb.setAttribute('is', 'emby-checkbox');
            cb.value = id; cb.dataset.type = type;
            var span = document.createElement('span'); span.textContent = name;
            row.appendChild(cb); row.appendChild(span); box.appendChild(row);
        });
    });
}

el('ccaBatchApply').addEventListener('click', function () {
    var checks = el('ccaBatchList').querySelectorAll('input[type=checkbox]:checked');
    if (!checks.length) { Dashboard.alert(t('batch.none')); return; }
    var targets = Array.prototype.map.call(checks, function (c) { return { Id: c.value, Type: c.dataset.type }; });
    el('ccaBatchStatus').textContent = t('batch.running');
    jsonFetch('CustomCoverArt/batchApply', 'POST', { Settings: collectSettings(), Targets: targets })
        .then(function (res) {
            var data = pick(res, 'Data') || [];
            var ok = data.filter(function (r) { return pick(r, 'Success'); }).length;
            var fail = data.length - ok;
            el('ccaBatchStatus').textContent = t('batch.done').replace('{0}', ok).replace('{1}', fail);
        });
});
```

Call `loadTemplates()` once on page init and `loadBatchList()` on init + whenever the target type changes.

- [ ] **Step 5: Build + manual check**

Run: `dotnet build CustomCoverArt.csproj`
Manual: save a design as a template; reload page → it appears in the dropdown and loading it restores the controls (title untouched); check several libraries in Batch apply → Apply → status shows N ok; each library gets a cover titled with its own name.

- [ ] **Step 6: Commit**

```bash
git add Configuration/configPage.html
git commit -m "feat(templates): config-page template card + batch-apply section"
```

---

## Phase 4 — Feature 4: Animated GIF export

### Task 13: Refactor `CoverArtService` to a single-frame builder

**Files:**
- Modify: `Services/CoverArtService.cs`

**Interfaces:**
- Produces: a private `Task ComposeFrameAsync(Image<Rgba32> canvas, Image? background, CoverArtSettings settings)` that does today's background+text compositing onto a supplied canvas. The existing static-image path calls it once; this is a pure refactor with no behavior change.

**Design:** Today `GenerateCoverArtAsync` creates one canvas and composites background+text inline. Extract the compositing so animation (Task 15) can call it per frame. No functional change here — the guard is the existing test suite (`BackgroundDimTests`) still passing.

- [ ] **Step 1: Extract the frame compositor**

Wrap the existing "create canvas → ApplyBackgroundAsync/gradient → ApplyTextOverlayWithFallbackAsync" sequence into:

```csharp
    private async Task ComposeFrameAsync(Image<Rgba32> canvas, Image? background, CoverArtSettings settings)
    {
        if (background is not null)
        {
            await ApplyBackgroundAsync(canvas, background, settings).ConfigureAwait(false);
        }
        else
        {
            await CreateGradientBackgroundAsync(canvas, settings).ConfigureAwait(false);
        }

        await ApplyTextOverlayWithFallbackAsync(canvas, settings).ConfigureAwait(false);
    }
```

(Match the exact method names/signatures already in the file — `ApplyBackgroundAsync`, `CreateGradientBackgroundAsync`, `ApplyTextOverlayWithFallbackAsync`. If any is not `static`, keep the call form the file already uses.)

In `GenerateCoverArtAsync`, replace the inline compositing with a single `await ComposeFrameAsync(image, backgroundImage, settings);` before `SaveImageWithRetryAsync`.

- [ ] **Step 2: Run the existing suite to prove no regression**

Run: `dotnet test tests/CustomCoverArt.Tests`
Expected: PASS (all, including `BackgroundDimTests`).

- [ ] **Step 3: Build + commit**

Run: `dotnet build CustomCoverArt.csproj`

```bash
git add Services/CoverArtService.cs
git commit -m "refactor(render): extract single-frame compositor"
```

---

### Task 14: Ken Burns crop math

**Files:**
- Modify: `Services/CoverArtService.cs` (add a static pure helper)
- Test: `tests/CustomCoverArt.Tests/AnimationTests.cs` (create)

**Interfaces:**
- Produces: `public static Rectangle CoverArtService.KenBurnsCrop(int srcW, int srcH, float t, float zoomAmount, string direction)` — the source-rectangle to crop for frame progress `t` in `[0,1]`. At the "wide" end it returns the full frame; at the "zoomed" end it returns a centered sub-rect scaled by `1/(1+zoomAmount)`. `direction == "in"` zooms from wide→tight as t goes 0→1; `"out"` reverses.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/CustomCoverArt.Tests/AnimationTests.cs
using CustomCoverArt.Services;
using SixLabors.ImageSharp;
using Xunit;

namespace CustomCoverArt.Tests;

public class AnimationTests
{
    [Fact]
    public void KenBurnsCrop_ZoomIn_StartsFullFrame()
    {
        var r = CoverArtService.KenBurnsCrop(1000, 1000, 0f, 0.2f, "in");
        Assert.Equal(0, r.X);
        Assert.Equal(0, r.Y);
        Assert.Equal(1000, r.Width);
        Assert.Equal(1000, r.Height);
    }

    [Fact]
    public void KenBurnsCrop_ZoomIn_EndsTighterAndCentered()
    {
        var r = CoverArtService.KenBurnsCrop(1000, 1000, 1f, 0.2f, "in");
        // 1/1.2 ≈ 0.8333 → ~833px, centered.
        Assert.InRange(r.Width, 820, 840);
        Assert.InRange(r.Height, 820, 840);
        Assert.True(r.X > 0 && r.Y > 0);
        Assert.Equal(r.X, (1000 - r.Width) / 2);
    }

    [Fact]
    public void KenBurnsCrop_ZoomOut_IsReverseOfZoomIn()
    {
        var inStart = CoverArtService.KenBurnsCrop(1000, 1000, 0f, 0.2f, "out");
        // "out" at t=0 is the tight end.
        Assert.InRange(inStart.Width, 820, 840);
    }
}
```

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test tests/CustomCoverArt.Tests --filter FullyQualifiedName~AnimationTests`
Expected: FAIL — `KenBurnsCrop` does not exist.

- [ ] **Step 3: Implement**

```csharp
    /// <summary>Source crop rectangle for a Ken Burns frame at progress t (0..1).</summary>
    public static Rectangle KenBurnsCrop(int srcW, int srcH, float t, float zoomAmount, string direction)
    {
        var z = System.Math.Clamp(zoomAmount, 0f, 1f);
        // progress from wide (0) to tight (1)
        var p = (direction ?? "in").ToLowerInvariant() == "out" ? 1f - t : t;
        p = System.Math.Clamp(p, 0f, 1f);

        // scale goes 1.0 (full) → 1/(1+z) (tight)
        var scale = 1f - p * (1f - 1f / (1f + z));
        var w = (int)System.Math.Round(srcW * scale);
        var h = (int)System.Math.Round(srcH * scale);
        var x = (srcW - w) / 2;
        var y = (srcH - h) / 2;
        return new Rectangle(x, y, w, h);
    }
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/CustomCoverArt.Tests --filter FullyQualifiedName~AnimationTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Services/CoverArtService.cs tests/CustomCoverArt.Tests/AnimationTests.cs
git commit -m "feat(gif): Ken Burns crop math"
```

---

### Task 15: Animated GIF encode + passthrough

**Files:**
- Modify: `Services/CoverArtService.cs`
- Test: `tests/CustomCoverArt.Tests/AnimationTests.cs` (add an integration fact)

**Interfaces:**
- Consumes: `ComposeFrameAsync` (Task 13), `KenBurnsCrop` (Task 14).
- Produces: `GenerateCoverArtAsync` branches to a multi-frame GIF when `settings.Animation?.Enabled == true`, via a private `Task<string> GenerateAnimatedAsync(CoverArtSettings settings, Image? background)`. Frame count is clamped to `[2, 30]`. Loop via GIF `RepeatCount` (0 = infinite).

**Design:** two motion sources — if `background` has >1 frame (animated source GIF), iterate its frames (passthrough); otherwise, if `KenBurns`, crop-zoom the static background per frame. Compose each frame with `ComposeFrameAsync`, add to an output `Image<Rgba32>`, set per-frame delay and repeat count, save with `GifEncoder`.

- [ ] **Step 1: Write the failing integration test**

```csharp
    [Fact]
    public async System.Threading.Tasks.Task GeneratesMultiFrameGif_WithKenBurns()
    {
        // Build settings with a gradient background (no file needed) + Ken Burns.
        var settings = new CustomCoverArt.Models.CoverArtSettings
        {
            Title = "Test",
            ExportWidth = 200,
            ExportHeight = 200,
            OutputFormat = "gif",
            BackgroundSource = "upload",
            Animation = new CustomCoverArt.Models.AnimationSettings
            {
                Enabled = true, KenBurns = true, FrameCount = 6, DelayMs = 80, Loop = true, ZoomAmount = 0.2f
            }
        };

        var svc = AnimationTestHost.NewCoverArtService();
        var path = await svc.GenerateCoverArtAsync(settings);

        Assert.True(System.IO.File.Exists(path));
        using var img = SixLabors.ImageSharp.Image.Load(path);
        Assert.True(img.Frames.Count >= 2);

        try { System.IO.File.Delete(path); } catch { }
    }
```

Add a small `AnimationTestHost` helper in the test file that constructs a `CoverArtService` with NSubstitute mocks for its dependencies (logger, paths pointing at a temp dir, media-item service). Model it on how `BackgroundDimTests` constructs the service — reuse that exact construction pattern so the dependency list stays correct.

> If `BackgroundDimTests` already has a factory for `CoverArtService`, reuse it instead of adding `AnimationTestHost`.

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test tests/CustomCoverArt.Tests --filter FullyQualifiedName~AnimationTests`
Expected: FAIL — animation branch not implemented (single-frame output → `Frames.Count == 1`).

- [ ] **Step 3: Implement the animated path**

In `GenerateCoverArtAsync`, after the background is resolved and BEFORE the single-frame compose/save, add:

```csharp
        if (settings.Animation?.Enabled == true)
        {
            return await GenerateAnimatedAsync(settings, backgroundImage).ConfigureAwait(false);
        }
```

Add the method:

```csharp
    private async Task<string> GenerateAnimatedAsync(CoverArtSettings settings, Image? background)
    {
        var frameCount = System.Math.Clamp(settings.Animation!.FrameCount, 2, 30);
        var delayCentis = System.Math.Max(2, settings.Animation.DelayMs / 10); // GIF delay is 1/100s
        var w = settings.ExportWidth;
        var h = settings.ExportHeight;

        // Passthrough when the source background is itself animated.
        var animatedSource = background is not null && background.Frames.Count > 1;

        Image<Rgba32>? output = null;
        try
        {
            for (int i = 0; i < frameCount; i++)
            {
                var t = frameCount == 1 ? 0f : i / (float)(frameCount - 1);

                using var frameCanvas = new Image<Rgba32>(w, h);
                Image? frameBg = null;
                Image<Rgba32>? tempBg = null;
                try
                {
                    if (animatedSource)
                    {
                        var srcIndex = i % background!.Frames.Count;
                        tempBg = background.Frames.CloneFrame(srcIndex).CloneAs<Rgba32>();
                        frameBg = tempBg;
                    }
                    else if (settings.Animation.KenBurns && background is not null)
                    {
                        var crop = KenBurnsCrop(background.Width, background.Height, t,
                            settings.Animation.ZoomAmount, settings.Animation.Direction);
                        tempBg = background.CloneAs<Rgba32>();
                        tempBg.Mutate(x => x.Crop(crop).Resize(w, h));
                        frameBg = tempBg;
                    }
                    else if (background is not null)
                    {
                        tempBg = background.CloneAs<Rgba32>();
                        frameBg = tempBg;
                    }

                    await ComposeFrameAsync(frameCanvas, frameBg, settings).ConfigureAwait(false);

                    if (output is null)
                    {
                        output = frameCanvas.Clone();
                        var gm = output.Metadata.GetGifMetadata();
                        gm.RepeatCount = (ushort)(settings.Animation.Loop ? 0 : 1);
                        var fm = output.Frames.RootFrame.Metadata.GetGifMetadata();
                        fm.FrameDelay = delayCentis;
                    }
                    else
                    {
                        var added = frameCanvas.Frames.RootFrame;
                        added.Metadata.GetGifMetadata().FrameDelay = delayCentis;
                        output.Frames.AddFrame(added);
                    }
                }
                finally
                {
                    tempBg?.Dispose();
                }
            }

            var outputPath = BuildGeneratedPath(settings, ".gif"); // match existing path-building in the file
            await output!.SaveAsync(outputPath, new SixLabors.ImageSharp.Formats.Gif.GifEncoder()).ConfigureAwait(false);
            return outputPath;
        }
        finally
        {
            output?.Dispose();
        }
    }
```

Notes for the implementer:
- Replace `BuildGeneratedPath(settings, ".gif")` with the file's existing output-path construction (the same one `GenerateCoverArtAsync` already uses for the generated file, forcing the `.gif` extension).
- Add `using SixLabors.ImageSharp.Formats.Gif;` and the metadata extension namespace if needed. `GetGifMetadata()` lives in `SixLabors.ImageSharp.Formats.Gif`.
- If `background` is null (pure gradient) and neither passthrough nor Ken Burns applies, every frame is identical — that is acceptable (a valid, if static, animated GIF). The UI (Task 16) only enables animation when a moving source makes sense, but the server stays robust.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test tests/CustomCoverArt.Tests --filter FullyQualifiedName~AnimationTests`
Expected: PASS (crop math + multi-frame integration).

- [ ] **Step 5: Full suite + build**

Run: `dotnet test tests/CustomCoverArt.Tests` then `dotnet build CustomCoverArt.csproj`
Expected: PASS / succeeds.

- [ ] **Step 6: Commit**

```bash
git add Services/CoverArtService.cs tests/CustomCoverArt.Tests/AnimationTests.cs
git commit -m "feat(gif): animated export with Ken Burns and animated-source passthrough"
```

---

### Task 16: Config page — animated GIF controls

**Files:**
- Modify: `Configuration/configPage.html`

- [ ] **Step 1: Add an "Animated GIF" option + controls to the Output card**

In the Output card's format `<select id="ccaFormat">`, add:

```html
        <option value="animatedgif" data-i18n="fmt.animated">Animated GIF</option>
```

After the format row, add an animation controls block:

```html
    <div class="ccaGrid" id="ccaAnimRow" style="display:none">
        <label class="ccaCheck ccaCheckBlock"><input is="emby-checkbox" type="checkbox" id="ccaKenBurns" checked /><span data-i18n="anim.kenburns">Ken Burns pan/zoom</span></label>
        <div class="inputContainer">
            <label for="ccaAnimFrames"><span data-i18n="anim.frames">Frames</span> <span class="ccaVal" id="ccaAnimFramesVal">20</span></label>
            <input type="range" class="ccaRange" id="ccaAnimFrames" min="2" max="30" step="1" value="20" />
        </div>
        <div class="inputContainer">
            <label for="ccaAnimDelay"><span data-i18n="anim.delay">Frame delay (ms)</span> <span class="ccaVal" id="ccaAnimDelayVal">80</span></label>
            <input type="range" class="ccaRange" id="ccaAnimDelay" min="20" max="300" step="10" value="80" />
        </div>
        <div class="inputContainer">
            <label for="ccaAnimDir" data-i18n="anim.dir">Zoom direction</label>
            <select is="emby-select" id="ccaAnimDir" class="emby-select">
                <option value="in" data-i18n="anim.in">Zoom in</option>
                <option value="out" data-i18n="anim.out">Zoom out</option>
            </select>
        </div>
    </div>
    <div class="fieldDescription" data-i18n="anim.hint">Animated GIFs are larger and only animate in views that render GIFs. Capped at 30 frames.</div>
```

- [ ] **Step 2: Add i18n keys**

`I18N.en`: `'fmt.animated': 'Animated GIF', 'anim.kenburns': 'Ken Burns pan/zoom', 'anim.frames': 'Frames', 'anim.delay': 'Frame delay (ms)', 'anim.dir': 'Zoom direction', 'anim.in': 'Zoom in', 'anim.out': 'Zoom out', 'anim.hint': 'Animated GIFs are larger and only animate in views that render GIFs. Capped at 30 frames.'`
`I18N.nl`: `'fmt.animated': 'Geanimeerde GIF', 'anim.kenburns': 'Ken Burns pan/zoom', 'anim.frames': 'Frames', 'anim.delay': 'Framevertraging (ms)', 'anim.dir': 'Zoomrichting', 'anim.in': 'Inzoomen', 'anim.out': 'Uitzoomen', 'anim.hint': 'Geanimeerde GIFs zijn groter en bewegen alleen in weergaven die GIFs tonen. Max. 30 frames.'`

- [ ] **Step 3: JS toggle + range labels + collectSettings**

```javascript
function updateAnimVisibility() {
    el('ccaAnimRow').style.display = el('ccaFormat').value === 'animatedgif' ? '' : 'none';
}
el('ccaFormat').addEventListener('change', function () { updateAnimVisibility(); runPreview(); });
el('ccaAnimFrames').addEventListener('input', function () { el('ccaAnimFramesVal').textContent = this.value; });
el('ccaAnimDelay').addEventListener('input', function () { el('ccaAnimDelayVal').textContent = this.value; });
```

In `collectSettings()`, map the animated format to `OutputFormat: 'gif'` plus an `Animation` object:

```javascript
        OutputFormat: el('ccaFormat').value === 'animatedgif' ? 'gif' : el('ccaFormat').value,
        Animation: el('ccaFormat').value === 'animatedgif' ? {
            Enabled: true,
            KenBurns: el('ccaKenBurns').checked,
            ZoomAmount: 0.15,
            Direction: el('ccaAnimDir').value,
            FrameCount: parseInt(el('ccaAnimFrames').value, 10),
            DelayMs: parseInt(el('ccaAnimDelay').value, 10),
            Loop: true
        } : null,
```

(Replace the existing single `OutputFormat:` line with the conditional version above; keep only one `OutputFormat` key.)

Call `updateAnimVisibility()` on page init.

- [ ] **Step 4: Build + manual check**

Run: `dotnet build CustomCoverArt.csproj`
Manual: choose Animated GIF → controls appear; Download produces a multi-frame GIF that visibly pans; Apply sets it (animation shows where Jellyfin renders GIFs).

- [ ] **Step 5: Commit**

```bash
git add Configuration/configPage.html
git commit -m "feat(gif): config-page animated GIF format + controls"
```

---

## Phase 5 — Docs, version, final verification

### Task 17: Server-side localization strings (if any)

**Files:**
- Modify: `Resources/en.json`, `Resources/nl.json`

- [ ] **Step 1:** Review the new controller error messages added in Tasks 4/10/11 (e.g. "No original cover backup found…", "Template name is required."). If the codebase routes controller strings through `ILocalizationService` (check how existing endpoints phrase user-facing errors), add matching keys to both `Resources/en.json` and `Resources/nl.json`. If controller errors are currently plain inline English (they are, per existing `Fail<T>("...")` usage), leave them inline and make NO change here — the config page's `I18N` already covers all UI text.

- [ ] **Step 2:** If no change was needed, skip the commit. Otherwise:

```bash
git add Resources/en.json Resources/nl.json
git commit -m "chore(i18n): server strings for v2 features"
```

---

### Task 18: README feature docs

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Extend the Features table**

Add these rows to the `## ✨ Features` table (matching the existing `| emoji | **Feature** | Details |` format):

```markdown
| 🧩 | **Poster collage** | Auto-build a grid-mosaic background from a target's own item posters |
| 💾 | **Design templates** | Save a look and reuse it; each target keeps its own name as the title |
| 📚 | **Batch apply** | Apply one design to many libraries/collections/playlists at once |
| ↩️ | **Restore original** | One-click revert to a target's pre-plugin cover |
| 🎞️ | **Animated GIF** | Export animated covers (animated-source passthrough or Ken Burns pan/zoom) |
```

- [ ] **Step 2: Add usage notes** under `## 🎬 Usage` (a short subsection after "Using an existing poster as a background"):

```markdown
### Templates and batch apply

Design a cover, then **Save current design** in the Templates card. In **Batch apply**, tick several targets and apply your design (or a saved template) to all of them at once — each cover is titled with its target's own name.

### Poster-collage backgrounds

In the Background card, set **Background source** to *Poster collage from this target* to build a dimmed grid of that library's own posters. Use **Shuffle** to re-roll the arrangement. (Live TV has no posters, so collage is unavailable there.)

### Animated covers

Set the output format to **Animated GIF** to export a moving cover — either passing through an animated-GIF background, or applying a gentle **Ken Burns** pan/zoom. GIFs are larger and only animate in the Jellyfin views that render GIFs.

### Restoring the original

Applying a cover automatically backs up the target's previous image once. Use **Restore original cover** on the Target card to revert.
```

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "docs(readme): document v2 features"
```

---

### Task 19: CHANGELOG + version bump + final verification

**Files:**
- Modify: `CHANGELOG.md`, `CustomCoverArt.csproj`

- [ ] **Step 1: Bump version**

In `CustomCoverArt.csproj`, change `<Version>1.3.1.0</Version>` to `<Version>2.0.0.0</Version>`.

- [ ] **Step 2: Add CHANGELOG entry** at the top (below the intro paragraph, above `## 1.3.1.0`), matching the existing single-paragraph, `**bold**`-emphasis format:

```markdown
## 2.0.0.0
A big release. **Poster-collage backgrounds** build a grid mosaic from a target's own item posters (with density and shuffle controls). **Design templates** let you save a look and reuse it, and **batch apply** pushes one design onto many libraries, collections or playlists at once — each cover titled with its own target's name. **Animated GIF export** is now real: it passes through an animated-GIF background or applies a Ken Burns pan/zoom (capped at 30 frames). And **Restore original cover** reverts any target to its pre-plugin image — applying a cover now backs up the previous image automatically.
```

- [ ] **Step 3: Full verification**

Run: `dotnet build CustomCoverArt.csproj`
Expected: succeeds, and the built assembly reports version 2.0.0.0.

Run: `dotnet test tests/CustomCoverArt.Tests`
Expected: ALL tests pass (ModelTests, PathSandboxTests, BackupRestoreTests, CollageComposerTests, TemplateTests, AnimationTests, BackgroundDimTests, PathSandbox existing).

- [ ] **Step 4: Commit**

```bash
git add CHANGELOG.md CustomCoverArt.csproj
git commit -m "chore(release): v2.0.0.0 — collage, templates+batch, restore, animated GIF"
```

- [ ] **Step 5: Manual smoke test on a running server** (highest-value manual check, since the apply path is the historic risk area)

1. Build and deploy the DLL to a Jellyfin 10.11 test server.
2. Apply a plain cover to a library → confirm it appears; confirm **Restore original** reverts it.
3. Apply a **poster collage** cover to a library with items → confirm the mosaic renders.
4. Save a **template**, then **batch apply** it to 2–3 libraries → confirm each gets its own titled cover.
5. Export an **animated GIF** with Ken Burns → download and confirm it animates.

---

## Self-Review (completed by plan author)

**Spec coverage:**
- Restore original cover → Tasks 3–5 (backup registry, endpoints + auto-backup, UI). ✓
- Poster-collage background → Tasks 6–9 (poster paths, composer, render integration, UI). ✓
- Templates + batch apply → Tasks 10–12 (CRUD, batchApply, UI). ✓
- Animated GIF (passthrough + Ken Burns) → Tasks 13–16 (refactor, crop math, encode, UI). ✓
- Shared plumbing (models, config, paths, DI) → Tasks 1–2, 8. ✓
- Localization → each UI task adds `I18N.en` + `I18N.nl`; Task 17 covers server strings. ✓
- README + CHANGELOG + version → Tasks 18–19. ✓
- Out-of-scope items (animated collage, extra languages, new target types) → not planned. ✓

**Type consistency:** `BackgroundSources.Collage` used consistently (Tasks 1, 11, 15); `CollageSettings.SourceId/Density/Seed` consistent (Tasks 1, 8, 9, 11); `AnimationSettings.{Enabled,KenBurns,ZoomAmount,Direction,FrameCount,DelayMs,Loop}` consistent (Tasks 1, 15, 16); `KenBurnsCrop` signature identical in Tasks 14 and 15; `BuildBatchSettings`/`NormalizeTemplate` static helpers match their tests; `HasBackup`/`RestoreOriginalCoverArtAsync`/`GetPosterPathsAsync` interface additions match their call sites.

**Known implementer judgement calls (flagged inline, not placeholders):** exact `CoverArtService` constructor arg list (Task 8/13/15 — reuse the file's existing pattern); exact generated-output-path builder name (Task 15); whether `SaveConfiguration()` vs `UpdateConfiguration()` is public on the Jellyfin `BasePlugin<T>` in use (Task 10); exact ImageSharp GIF metadata calls (Task 15, guarded by the frame-count assertion test).
