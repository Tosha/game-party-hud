# Character Presets Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let one install hold multiple named character profiles (nickname + role + bar regions), with a switcher in the main window and silent migration of existing `config.json` files into the new shape.

**Architecture:** Move the six per-character fields out of `AppConfig` into a new `Preset` record. `AppConfig` gains `Presets` (list, ≥1) and `ActivePresetId` (selects which preset is "live"). Read sites switch to `cfg.ActivePreset.X`; write sites use a new `AppConfigExtensions.UpdatePreset` helper. `ConfigStore.Load` detects legacy JSON (no `Presets` key) and migrates it into a single "Default" preset on first run. The main window's Profile header gets a `ComboBox` for switching/creating/renaming/deleting presets. Three never-read legacy fields (`AppConfig.NicknameRegion`, `HudPosition.Monitor`, `CaptureRegion.Monitor`) are removed in the same change so the new shape doesn't carry dead fields forward.

**Tech Stack:** C# .NET 8 / WPF, `System.Text.Json` (already in use; `JsonSerializerDefaults.Web` silently ignores unknown fields), Wpf.Ui 3.x (`FluentWindow`, `ComboBox`, `SymbolIcon`), xUnit for tests.

**Testing approach:** Pure-logic surface (`ConfigStore.Load` migration, `AppConfig.ActivePreset` accessor, `AppConfigExtensions.UpdatePreset`) is unit-tested. Per `CLAUDE.md`, UI surfaces (the new ComboBox dropdown with inline rename/delete) are manually verified by running the app — no flaky UI automation.

**Reference spec:** [`docs/superpowers/specs/2026-05-17-character-presets-design.md`](../specs/2026-05-17-character-presets-design.md)

---

## File Structure

**Created:**
- `src/GamePartyHud/Config/Preset.cs` — new record type (Task 4).
- `src/GamePartyHud/Config/AppConfigExtensions.cs` — `UpdatePreset` helper (Task 5).
- `tests/GamePartyHud.Tests/Config/AppConfigExtensionsTests.cs` — unit tests for the helper (Task 5).

**Modified:**
- `src/GamePartyHud/Capture/CaptureRegion.cs` — drop `Monitor` field (Task 1).
- `src/GamePartyHud/Calibration/RegionSelectorWindow.xaml.cs` — drop `Monitor:` ctor arg (Task 1).
- `src/GamePartyHud/Config/AppConfig.cs` — drop `HudPosition.Monitor` (Task 2); drop `NicknameRegion` (Task 3); restructure to use presets (Task 5).
- `src/GamePartyHud/App.xaml.cs` — drop `Monitor` from `HudPosition` ctor in `ResetHudLayout` (Task 2); update read sites for presets (Task 5).
- `src/GamePartyHud/MainWindow.xaml` — Profile header restructured into a 2-column grid with the preset `ComboBox` (Task 7). Dropdown templates updated for inline mgmt (Tasks 8/9/10).
- `src/GamePartyHud/MainWindow.xaml.cs` — drop `NicknameRegion = null` from `OnPickRegion` (Task 3); update read sites + write sites for presets (Task 5); preset selector handlers (Tasks 7–10).
- `src/GamePartyHud/Config/ConfigStore.cs` — migration logic (Task 6).
- `src/GamePartyHud/Party/PartyOrchestrator.cs` — update read sites for presets (Task 5).
- `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs` — drop `NicknameRegion =` from round-trip fixture (Task 3); update for new shape, skip legacy-format tests temporarily (Task 5); un-skip + add new migration tests (Task 6).
- `tests/GamePartyHud.Tests/Capture/BarAnalyzerTests.cs`, `ManaBarDiagnosticTests.cs`, `SampleImageRegressionTests.cs`, `SampleImageDiagnosticTests.cs` — drop leading `0` from `new CaptureRegion(...)` calls (Task 1).

**Untouched:** Networking (`Network/*`), HUD overlay (`Hud/*`), Discord notifier, Tray icon, capture loop (`WindowsScreenCapture.cs`'s logic is config-agnostic — it only consumes `CaptureRegion`).

---

## Task 1: Drop `CaptureRegion.Monitor` (dead field)

The `Monitor` int was set to `0` everywhere and never read. Removing it shrinks the record and cleans up the legacy "single-monitor assumption" comment in `RegionSelectorWindow`. Pure mechanical edit — affects every construction site of `CaptureRegion` (one app site, six test sites).

**Files:**
- Modify: `src/GamePartyHud/Capture/CaptureRegion.cs`
- Modify: `src/GamePartyHud/Calibration/RegionSelectorWindow.xaml.cs:71-72`
- Modify: `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs:43,46,49,51,215`
- Modify: `tests/GamePartyHud.Tests/Capture/BarAnalyzerTests.cs:9`
- Modify: `tests/GamePartyHud.Tests/Capture/ManaBarDiagnosticTests.cs:69`
- Modify: `tests/GamePartyHud.Tests/Capture/SampleImageRegressionTests.cs:44,60,95,111`
- Modify: `tests/GamePartyHud.Tests/Capture/SampleImageDiagnosticTests.cs:81`

- [ ] **Step 1.1: Update `CaptureRegion.cs`**

Replace the file contents with:

```csharp
namespace GamePartyHud.Capture;

public sealed record CaptureRegion(int X, int Y, int W, int H);
```

- [ ] **Step 1.2: Update `RegionSelectorWindow.xaml.cs`**

Open `src/GamePartyHud/Calibration/RegionSelectorWindow.xaml.cs`. Find the `Result = new CaptureRegion(...)` block (around line 71):

```csharp
Result = new CaptureRegion(
    Monitor: 0, // Single-monitor assumption for v0.1.0; the capture layer uses absolute coords.
    X: (int)Math.Round(topLeftScreen.X),
    Y: (int)Math.Round(topLeftScreen.Y),
    W: (int)Math.Round(bottomRightScreen.X - topLeftScreen.X),
    H: (int)Math.Round(bottomRightScreen.Y - topLeftScreen.Y));
```

Replace with:

```csharp
Result = new CaptureRegion(
    X: (int)Math.Round(topLeftScreen.X),
    Y: (int)Math.Round(topLeftScreen.Y),
    W: (int)Math.Round(bottomRightScreen.X - topLeftScreen.X),
    H: (int)Math.Round(bottomRightScreen.Y - topLeftScreen.Y));
```

- [ ] **Step 1.3: Update test fixtures**

For each of the test files listed in **Files**, find every `new CaptureRegion(0, ...)` call and drop the leading `0` (the `Monitor` arg). The exact replacements:

`tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs`:
- Line 43: `new CaptureRegion(0, 10, 20, 300, 18)` → `new CaptureRegion(10, 20, 300, 18)`
- Line 46: `new CaptureRegion(0, 10, 40, 300, 18)` → `new CaptureRegion(10, 40, 300, 18)`
- Line 49: `new CaptureRegion(0, 10, 60, 300, 18)` → `new CaptureRegion(10, 60, 300, 18)`
- Line 51: `new CaptureRegion(0, 10, 0, 300, 20)` → `new CaptureRegion(10, 0, 300, 20)`
- Line 215: `new CaptureRegion(0, 10, 20, 300, 18)` → `new CaptureRegion(10, 20, 300, 18)`

`tests/GamePartyHud.Tests/Capture/BarAnalyzerTests.cs`:
- Line 9: `new CaptureRegion(0, 0, 0, 200, 10)` → `new CaptureRegion(0, 0, 200, 10)`

`tests/GamePartyHud.Tests/Capture/ManaBarDiagnosticTests.cs`:
- Line 69: `new CaptureRegion(0, 0, 0, w, h)` → `new CaptureRegion(0, 0, w, h)`

`tests/GamePartyHud.Tests/Capture/SampleImageRegressionTests.cs`:
- Line 44, 60, 95, 111: each `new CaptureRegion(0, 0, 0, w, h)` → `new CaptureRegion(0, 0, w, h)`

`tests/GamePartyHud.Tests/Capture/SampleImageDiagnosticTests.cs`:
- Line 81: `new CaptureRegion(0, 0, 0, w, h)` → `new CaptureRegion(0, 0, w, h)`

The inline JSON strings in `ConfigStoreTests` that contain `"monitor": 0` keys (lines 88, 131, 157, 203, 243, 273, 299, 322 — search for `"monitor"`) are left alone for now. They'll be silently dropped on Load (System.Text.Json ignores unknown fields by default with `JsonSerializerDefaults.Web`); Task 6 may revisit them once migration tests land.

- [ ] **Step 1.4: Build**

Run:
```
dotnet build src/GamePartyHud/GamePartyHud.csproj
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

If you see `CS7036: There is no argument given that corresponds to the required parameter 'X'` etc., a `CaptureRegion(0, ...)` call was missed. Re-run the search:

```
grep -rn "new CaptureRegion(0," src tests
```
Expected after fix: no matches.

- [ ] **Step 1.5: Run tests**

Run:
```
dotnet test tests/GamePartyHud.Tests/GamePartyHud.Tests.csproj
```
Expected: all tests pass (151 today; should be 151 still — this task changes no behavior).

- [ ] **Step 1.6: Commit**

```
git add src/GamePartyHud/Capture/CaptureRegion.cs src/GamePartyHud/Calibration/RegionSelectorWindow.xaml.cs tests/GamePartyHud.Tests/
git commit -m "refactor(capture): drop unused Monitor field from CaptureRegion

The int was always 0 and never read anywhere. Removing it shrinks the
record and cleans up the 'single-monitor assumption for v0.1.0'
comment in the region picker. Pure structural cleanup; no behavior
change."
```

---

## Task 2: Drop `HudPosition.Monitor` (dead field)

Mechanical removal of the unread `Monitor` int from the `HudPosition` record.

**Files:**
- Modify: `src/GamePartyHud/Config/AppConfig.cs` (the `HudPosition` record declaration; the default value in `AppConfig.Defaults`)
- Modify: `src/GamePartyHud/App.xaml.cs:62-67` (`ResetHudLayout` reads from `AppConfig.Defaults.HudPosition`; the record-creation in `_config with` doesn't construct a HudPosition, so no edit needed there)
- Modify: `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs:54` (the round-trip test constructs `new HudPosition(500, 400, 1)`)

- [ ] **Step 2.1: Update the `HudPosition` record**

In `src/GamePartyHud/Config/AppConfig.cs`, find:

```csharp
public sealed record HudPosition(double X, double Y, int Monitor);
```

Replace with:

```csharp
public sealed record HudPosition(double X, double Y);
```

- [ ] **Step 2.2: Update `AppConfig.Defaults`**

In the same file, find the `Defaults` initializer:

```csharp
HudPosition: new HudPosition(100, 100, 0),
```

Replace with:

```csharp
HudPosition: new HudPosition(100, 100),
```

- [ ] **Step 2.3: Update `ConfigStoreTests.cs` round-trip fixture**

In `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs`, find:

```csharp
HudPosition = new HudPosition(500, 400, 1),
```

Replace with:

```csharp
HudPosition = new HudPosition(500, 400),
```

The legacy JSON strings containing `"hudPosition": { "x": 0, "y": 0, "monitor": 0 }` are left alone — the `monitor` key will be silently ignored on Load.

- [ ] **Step 2.4: Verify `App.xaml.cs` doesn't construct `HudPosition` directly**

Run:
```
grep -rn "new HudPosition" src
```
Expected: only matches in `Config/AppConfig.cs` (the `Defaults` initializer from Step 2.2). If `App.xaml.cs` has its own `new HudPosition(...)` call, update it to drop the `Monitor` arg.

- [ ] **Step 2.5: Build**

Run:
```
dotnet build src/GamePartyHud/GamePartyHud.csproj
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 2.6: Run tests**

Run:
```
dotnet test tests/GamePartyHud.Tests/GamePartyHud.Tests.csproj
```
Expected: all tests pass.

- [ ] **Step 2.7: Commit**

```
git add src/GamePartyHud/Config/AppConfig.cs tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs
git commit -m "refactor(config): drop unused Monitor field from HudPosition

The int was always 0 and never read. Legacy config.json values still
parse — System.Text.Json silently ignores unknown 'monitor' keys."
```

---

## Task 3: Drop `AppConfig.NicknameRegion` (dead field)

The vestige of the v0.1.0 calibration wizard. Set to null in defaults, reset to null in `MainWindow.OnPickRegion`, never read.

**Files:**
- Modify: `src/GamePartyHud/Config/AppConfig.cs` (field declaration line 12; `Defaults` initializer line 70)
- Modify: `src/GamePartyHud/MainWindow.xaml.cs:265` (the HP arm of `OnPickRegion`)
- Modify: `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs:51` (round-trip fixture)

- [ ] **Step 3.1: Remove the field from `AppConfig`**

In `src/GamePartyHud/Config/AppConfig.cs`, find the record positional parameter list:

```csharp
public sealed record AppConfig(
    BarCalibration? HpCalibration,
    BarCalibration? StaminaCalibration,
    BarCalibration? ManaCalibration,
    CaptureRegion? NicknameRegion,
    string Nickname,
    Role Role,
    ...
```

Drop the `CaptureRegion? NicknameRegion,` line. The list becomes:

```csharp
public sealed record AppConfig(
    BarCalibration? HpCalibration,
    BarCalibration? StaminaCalibration,
    BarCalibration? ManaCalibration,
    string Nickname,
    Role Role,
    ...
```

- [ ] **Step 3.2: Remove `NicknameRegion` from `AppConfig.Defaults`**

In the same file, find the `Defaults` initializer:

```csharp
NicknameRegion: null,
```

Delete that line entirely.

- [ ] **Step 3.3: Remove `NicknameRegion = null` from `MainWindow.OnPickRegion`**

In `src/GamePartyHud/MainWindow.xaml.cs`, find the HP arm of the switch around line 265:

```csharp
BarType.Hp      => _ctl.Config with { HpCalibration      = cal, NicknameRegion = null },
```

Replace with:

```csharp
BarType.Hp      => _ctl.Config with { HpCalibration      = cal },
```

- [ ] **Step 3.4: Remove `NicknameRegion` from the test fixture**

In `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs`, find the line:

```csharp
NicknameRegion = new CaptureRegion(10, 0, 300, 20),
```

(post-Task-1 it's already without the leading `0`.) Delete that line entirely.

- [ ] **Step 3.5: Build**

Run:
```
dotnet build src/GamePartyHud/GamePartyHud.csproj
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

If you see `CS0117: 'AppConfig' does not contain a definition for 'NicknameRegion'`, a read or write site to that field was missed. Search:

```
grep -rn "NicknameRegion" src tests
```
Expected after fix: matches only in the legacy JSON strings inside `ConfigStoreTests.cs` (which will be silently ignored by System.Text.Json on Load).

- [ ] **Step 3.6: Run tests**

Run:
```
dotnet test tests/GamePartyHud.Tests/GamePartyHud.Tests.csproj
```
Expected: all tests pass.

- [ ] **Step 3.7: Commit**

```
git add src/GamePartyHud/Config/AppConfig.cs src/GamePartyHud/MainWindow.xaml.cs tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs
git commit -m "refactor(config): drop unused NicknameRegion field

Vestige of the v0.1.0 calibration wizard, replaced by
RegionSelectorWindow. Was set to null everywhere and never read.
Legacy config.json values containing 'nicknameRegion' are silently
discarded on Load (System.Text.Json default behaviour)."
```

---

## Task 4: Add `Preset` record

Standalone new file — no usage yet. Compiles immediately. Sets up the type used by Task 5 to restructure `AppConfig`.

**Files:**
- Create: `src/GamePartyHud/Config/Preset.cs`

- [ ] **Step 4.1: Create `src/GamePartyHud/Config/Preset.cs`**

Write the file with this content:

```csharp
using GamePartyHud.Capture;
using GamePartyHud.Party;

namespace GamePartyHud.Config;

/// <summary>
/// One character profile bundle: the user-facing label plus all the per-character
/// data that today lives on <see cref="AppConfig"/>'s top level. Multiple presets
/// can coexist on the same install so a user with several alts can switch
/// nickname / role / bar-region calibrations in a single click.
/// </summary>
/// <param name="Id">
/// Stable id used by <see cref="AppConfig.ActivePresetId"/> to reference this
/// preset. GUID string for user-created presets; the literal sentinel
/// <c>"default"</c> for the auto-seeded preset that ships in
/// <see cref="AppConfig.Defaults"/> or is produced by the legacy-config
/// migration in <c>ConfigStore.Load</c>. Survives renames.
/// </param>
/// <param name="Name">User-facing label, unique within <see cref="AppConfig.Presets"/>.</param>
public sealed record Preset(
    string Id,
    string Name,
    string Nickname,
    Role Role,
    BarCalibration? HpCalibration,
    BarCalibration? StaminaCalibration,
    BarCalibration? ManaCalibration);
```

- [ ] **Step 4.2: Build**

Run:
```
dotnet build src/GamePartyHud/GamePartyHud.csproj
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 4.3: Run tests**

Run:
```
dotnet test tests/GamePartyHud.Tests/GamePartyHud.Tests.csproj
```
Expected: all tests pass (the new file isn't referenced by anything yet).

- [ ] **Step 4.4: Commit**

```
git add src/GamePartyHud/Config/Preset.cs
git commit -m "feat(config): add Preset record (unused yet)

Standalone type that Task 5 will use to restructure AppConfig. No
behavior change in this commit."
```

---

## Task 5: Restructure `AppConfig` to use presets

The big-bang change: drop the six per-character fields from `AppConfig`'s top level, add `Presets` + `ActivePresetId`, add `ActivePreset` accessor + `AppConfigExtensions.UpdatePreset`, update every read and write site, update test fixtures. The two `Load_OldShape*` tests that depend on legacy-format-tolerant parsing are temporarily skipped — Task 6 re-implements the migration and un-skips them.

This task does not introduce migration code; that's Task 6. Between Tasks 5 and 6, loading a legacy `config.json` would silently drop the user's nickname/role/calibrations and seed the Default preset with `AppConfig.Defaults` values. This is acceptable as an intermediate state because the commit only lands behind Task 6 in the PR.

**Files:**
- Modify: `src/GamePartyHud/Config/AppConfig.cs`
- Create: `src/GamePartyHud/Config/AppConfigExtensions.cs`
- Modify: `src/GamePartyHud/App.xaml.cs:125, 309`
- Modify: `src/GamePartyHud/MainWindow.xaml.cs:118, 119, 126, 136, 150, 220, 263-267, 305-308, 372-374, 435-436` (PopulateFromConfig + the write-site handlers)
- Modify: `src/GamePartyHud/Party/PartyOrchestrator.cs:113, 167-169, 178, 190-191, 196, 201-202`
- Modify: `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs` (round-trip fixture; legacy-shape tests get `[Fact(Skip = "Migration tests live in Task 6 once migration is implemented")]`)
- Create: `tests/GamePartyHud.Tests/Config/AppConfigExtensionsTests.cs`

- [ ] **Step 5.1: Restructure `AppConfig`**

Replace the entire contents of `src/GamePartyHud/Config/AppConfig.cs` with:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json.Serialization;
using GamePartyHud.Diagnostics;
using GamePartyHud.Party;

namespace GamePartyHud.Config;

public sealed record AppConfig(
    IReadOnlyList<Preset> Presets,
    string ActivePresetId,
    HudPosition HudPosition,
    bool HudLocked,
    string? LastPartyId,
    int PollIntervalMs,
    string RelayUrl,
    string RelayFallbackUrl = "",
    bool FullscreenDisclaimerDismissed = false,
    double HudScale = 1.0)
{
    /// <summary>
    /// Default relay endpoint, injected at build time via the
    /// <c>RelayUrl</c> MSBuild property (see <c>GamePartyHud.csproj</c>). Local
    /// dev builds inherit the placeholder default; release builds in CI
    /// substitute the real URL from the <c>GPH_RELAY_URL</c> GitHub Actions
    /// secret. End users override per-machine via the <c>RelayUrl</c> field
    /// in <c>%AppData%\GamePartyHud\config.json</c>.
    /// </summary>
    public static string DefaultRelayUrl { get; } = ResolveAssemblyMetadata("RelayUrl", fallback: "wss://relay.example.invalid");

    /// <summary>
    /// Optional secondary relay endpoint, tried after <see cref="DefaultRelayUrl"/>
    /// fails to connect within the client's timeout. Used to route around ISPs
    /// that block the Cloudflare CIDR ranges where the primary Worker lives;
    /// the fallback runs on a non-Cloudflare host (e.g. an Oracle Always-Free
    /// VM) and proxies into the same Worker. Empty string means no fallback,
    /// preserving the single-URL path for forks that haven't set up a bridge.
    /// </summary>
    public static string DefaultRelayFallbackUrl { get; } = ResolveAssemblyMetadata("RelayFallbackUrl", fallback: "");

    /// <summary>
    /// Discord webhook endpoint for the party-creation notification. Injected
    /// at build time via the <c>DiscordWebhookUrl</c> MSBuild property (see
    /// <c>GamePartyHud.csproj</c>). Empty string by default; release builds in
    /// CI substitute the real URL from the <c>GPH_DISCORD_WEBHOOK_URL</c>
    /// GitHub Actions secret. Empty URL = notifier is a no-op (see
    /// <c>DiscordNotifier</c>). Not a per-machine config field; never
    /// persisted in <c>config.json</c>.
    /// </summary>
    public static string DefaultDiscordWebhookUrl { get; } =
        ResolveAssemblyMetadata("DiscordWebhookUrl", fallback: "");

    private static string ResolveAssemblyMetadata(string key, string fallback)
    {
        var fromMetadata = typeof(AppConfig).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)
            ?.Value;
        if (!string.IsNullOrWhiteSpace(fromMetadata)) return fromMetadata;
        return fallback;
    }

    /// <summary>
    /// Sentinel id used by <see cref="Defaults"/> and the legacy-config migration
    /// in <c>ConfigStore.Load</c> for the auto-seeded preset, so successive runs
    /// reference a stable id rather than a fresh GUID each time.
    /// </summary>
    public const string DefaultPresetId = "default";

    public static AppConfig Defaults { get; } = new(
        Presets: new[]
        {
            new Preset(
                Id: DefaultPresetId,
                Name: "Default",
                Nickname: "Player",
                Role: Role.Utility,
                HpCalibration: null,
                StaminaCalibration: null,
                ManaCalibration: null),
        },
        ActivePresetId: DefaultPresetId,
        HudPosition: new HudPosition(100, 100),
        HudLocked: true,
        LastPartyId: null,
        PollIntervalMs: 700,
        RelayUrl: DefaultRelayUrl,
        RelayFallbackUrl: DefaultRelayFallbackUrl,
        FullscreenDisclaimerDismissed: false,
        HudScale: 1.0);

    /// <summary>
    /// The preset currently in use. Resolves <see cref="ActivePresetId"/> against
    /// <see cref="Presets"/>; falls back to the first preset (and logs a warn) if
    /// the id is stale — protects callers from an `IndexOutOfRangeException` if
    /// config.json was hand-edited or got out of sync mid-write. <c>ConfigStore.Load</c>
    /// also repairs this invariant; the fallback here is belt-and-suspenders.
    /// </summary>
    [JsonIgnore]
    public Preset ActivePreset
    {
        get
        {
            var match = Presets.FirstOrDefault(p => p.Id == ActivePresetId);
            if (match is not null) return match;
            Log.Warn($"AppConfig: ActivePresetId '{ActivePresetId}' did not match any preset; falling back to '{Presets[0].Id}'.");
            return Presets[0];
        }
    }
}

public sealed record HudPosition(double X, double Y);
```

Note: the `Log` reference requires the `using GamePartyHud.Diagnostics;` import above. Verify it's present.

- [ ] **Step 5.2: Add `AppConfigExtensions.cs`**

Create `src/GamePartyHud/Config/AppConfigExtensions.cs` with:

```csharp
using System;
using System.Linq;

namespace GamePartyHud.Config;

public static class AppConfigExtensions
{
    /// <summary>
    /// Returns a new <see cref="AppConfig"/> with <paramref name="mutate"/> applied
    /// to the currently-active preset. Other presets are passed through unchanged.
    /// Avoids the verbose
    /// <c>_config with { Presets = _config.Presets.Select(p => p.Id == _config.ActivePresetId ? p with { ... } : p).ToList() }</c>
    /// at every call site that today does <c>_config with { Nickname = ... }</c>.
    /// </summary>
    public static AppConfig UpdatePreset(this AppConfig cfg, Func<Preset, Preset> mutate)
    {
        var presets = cfg.Presets
            .Select(p => p.Id == cfg.ActivePresetId ? mutate(p) : p)
            .ToList();
        return cfg with { Presets = presets };
    }
}
```

- [ ] **Step 5.3: Update `App.xaml.cs` read sites**

In `src/GamePartyHud/App.xaml.cs`, find the startup log line around line 125:

```csharp
Log.Info($"Config loaded. Nickname='{_config.Nickname}', Role={_config.Role}, HpCalibration={(_config.HpCalibration is null ? "none" : "present")}, LastPartyId={_config.LastPartyId ?? "none"}.");
```

Replace with:

```csharp
var activePreset = _config.ActivePreset;
Log.Info($"Config loaded. ActivePreset='{activePreset.Name}' (Id={activePreset.Id}). Nickname='{activePreset.Nickname}', Role={activePreset.Role}, HpCalibration={(activePreset.HpCalibration is null ? "none" : "present")}, LastPartyId={_config.LastPartyId ?? "none"}.");
```

Find the Discord notification call around line 309:

```csharp
_ = NotifyDiscordPartyCreatedAsync(_config.Nickname, partyId);
```

Replace with:

```csharp
_ = NotifyDiscordPartyCreatedAsync(_config.ActivePreset.Nickname, partyId);
```

- [ ] **Step 5.4: Update `MainWindow.PopulateFromConfig` read sites**

In `src/GamePartyHud/MainWindow.xaml.cs`, find the `PopulateFromConfig` method (around line 104). Replace the inner reads of `cfg.Nickname` / `cfg.Role` / `cfg.HpCalibration` / `cfg.StaminaCalibration` / `cfg.ManaCalibration` with reads through `cfg.ActivePreset`. The block currently reads:

```csharp
NickText.Text = cfg.Nickname == AppConfig.Defaults.Nickname ? "" : cfg.Nickname;
RoleCombo.SelectedItem = RoleOptions.FirstOrDefault(o => o.Role == cfg.Role) ?? RoleOptions[0];
// ...
if (cfg.HpCalibration is { } cal) { ... }
// ...
if (cfg.StaminaCalibration is { } sCal) { ... }
// ...
if (cfg.ManaCalibration is { } mCal) { ... }
```

Replace with:

```csharp
var ap = cfg.ActivePreset;
var defaultPreset = AppConfig.Defaults.ActivePreset;
NickText.Text = ap.Nickname == defaultPreset.Nickname ? "" : ap.Nickname;
RoleCombo.SelectedItem = RoleOptions.FirstOrDefault(o => o.Role == ap.Role) ?? RoleOptions[0];
// ...
if (ap.HpCalibration is { } cal) { ... }
// ...
if (ap.StaminaCalibration is { } sCal) { ... }
// ...
if (ap.ManaCalibration is { } mCal) { ... }
```

(Keep the rest of the conditional bodies — `SetRegionStatus`, `IncludeStaminaCheck.IsChecked`, etc. — unchanged. Only the conditional probe and the source variable change.)

- [ ] **Step 5.5: Update `MainWindow` write sites to use `UpdatePreset`**

Still in `MainWindow.xaml.cs`, locate and update each write site:

**`OnNicknameChanged` (around line 190):**

```csharp
_ctl.UpdateConfig(_ctl.Config with { Nickname = nick });
```

becomes

```csharp
_ctl.UpdateConfig(_ctl.Config.UpdatePreset(p => p with { Nickname = nick }));
```

**`OnRoleChanged` (around line 215):**

```csharp
_ctl.UpdateConfig(_ctl.Config with { Role = opt.Role });
```

becomes

```csharp
_ctl.UpdateConfig(_ctl.Config.UpdatePreset(p => p with { Role = opt.Role }));
```

**`OnPickRegion` switch (around line 263–268):**

```csharp
var newConfig = bar switch
{
    BarType.Hp      => _ctl.Config with { HpCalibration      = cal },
    BarType.Stamina => _ctl.Config with { StaminaCalibration = cal },
    BarType.Mana    => _ctl.Config with { ManaCalibration    = cal },
    _ => _ctl.Config
};
```

becomes

```csharp
var newConfig = bar switch
{
    BarType.Hp      => _ctl.Config.UpdatePreset(p => p with { HpCalibration      = cal }),
    BarType.Stamina => _ctl.Config.UpdatePreset(p => p with { StaminaCalibration = cal }),
    BarType.Mana    => _ctl.Config.UpdatePreset(p => p with { ManaCalibration    = cal }),
    _ => _ctl.Config
};
```

**`OnIncludeStaminaUnchecked` (around line 504):**

```csharp
_ctl.UpdateConfig(_ctl.Config with { StaminaCalibration = null });
```

becomes

```csharp
_ctl.UpdateConfig(_ctl.Config.UpdatePreset(p => p with { StaminaCalibration = null }));
```

**`OnIncludeManaUnchecked` (around line 521):**

```csharp
_ctl.UpdateConfig(_ctl.Config with { ManaCalibration = null });
```

becomes

```csharp
_ctl.UpdateConfig(_ctl.Config.UpdatePreset(p => p with { ManaCalibration = null }));
```

**`ValidateBeforeJoiningParty` (around line 422):**

```csharp
if (_ctl.Config.HpCalibration is null) { ... }
// ...
if (string.IsNullOrWhiteSpace(_ctl.Config.Nickname)
    || _ctl.Config.Nickname == AppConfig.Defaults.Nickname) { ... }
```

becomes

```csharp
var ap = _ctl.Config.ActivePreset;
if (ap.HpCalibration is null) { ... }
// ...
if (string.IsNullOrWhiteSpace(ap.Nickname)
    || ap.Nickname == AppConfig.Defaults.ActivePreset.Nickname) { ... }
```

- [ ] **Step 5.6: Update `PartyOrchestrator` read sites**

In `src/GamePartyHud/Party/PartyOrchestrator.cs`, replace each `_cfg.Nickname`, `_cfg.Role`, `_cfg.HpCalibration`, `_cfg.StaminaCalibration`, `_cfg.ManaCalibration` with the corresponding `_cfg.ActivePreset.X`. The simplest pattern is to add a local at the top of each method that reads these:

**`UpdateConfig` (line 110–114):**

```csharp
public void UpdateConfig(AppConfig cfg)
{
    _cfg = cfg;
    var ap = cfg.ActivePreset;
    Log.Info($"PartyOrchestrator: config updated (preset='{ap.Name}', nickname='{ap.Nickname}', role={ap.Role}, pollMs={cfg.PollIntervalMs}).");
}
```

**`PollAndBroadcastLoopAsync` (lines 167–202):** at the top of the `try` block inside the loop, take a snapshot of the active preset:

```csharp
var ap = _cfg.ActivePreset;
float? hp = await ReadBarAsync(ap.HpCalibration, _hpSmoother, ct).ConfigureAwait(false);
float? stamina = await ReadBarAsync(ap.StaminaCalibration, _staminaSmoother, ct).ConfigureAwait(false);
float? mana = await ReadBarAsync(ap.ManaCalibration, _manaSmoother, ct).ConfigureAwait(false);
```

Then below, replace each `_cfg.Nickname` and `_cfg.Role` with `ap.Nickname` / `ap.Role` (in the `_state.Apply` call's `StateMessage` arg, the `nickChanged`/`roleChanged` checks, the `MessageJson.Encode(new StateMessage(...))` call, and the two `_lastBroadcastNick = _cfg.Nickname` / `_lastBroadcastRole = _cfg.Role` assignments). The other `_lastBroadcastX` lines for hp/stamina/mana don't reference `_cfg` and stay as-is.

The local `ap` is captured at the top of the loop body, so a preset switch that lands mid-tick uses the previous tick's snapshot — fine; next tick picks up the new preset. PollIntervalMs is read from `_cfg.PollIntervalMs` (not from `ap`) since it's not per-preset.

- [ ] **Step 5.7: Update `ConfigStoreTests.cs`**

Open `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs`. Two kinds of updates:

(a) The `RoundTrip_PreservesEverythingExceptBinaryOwnedFields` test now constructs an `AppConfig` with the new shape. Replace its body:

```csharp
[Fact]
public void RoundTrip_PreservesEverythingExceptBinaryOwnedFields()
{
    var store = new ConfigStore(_tmp);
    var cfg = AppConfig.Defaults with
    {
        Presets = new[]
        {
            new Preset(
                Id: AppConfig.DefaultPresetId,
                Name: "Default",
                Nickname: "Yiawahuye",
                Role: Role.Tank,
                HpCalibration:      new BarCalibration(new CaptureRegion(10, 20, 300, 18), FillDirection.LTR),
                StaminaCalibration: new BarCalibration(new CaptureRegion(10, 40, 300, 18), FillDirection.LTR),
                ManaCalibration:    new BarCalibration(new CaptureRegion(10, 60, 300, 18), FillDirection.LTR)),
        },
        ActivePresetId = AppConfig.DefaultPresetId,
        HudPosition = new HudPosition(500, 400),
        HudLocked = false,
        LastPartyId = "X7K2P9",
        FullscreenDisclaimerDismissed = true,
    };
    store.Save(cfg);
    Assert.Equal(cfg, store.Load());
}
```

(b) The four `Load_OldShape*` tests (`Load_OldShapeHpCalibrationJson_DropsFullColorAndTolerance`, `Load_OldShapeConfig_MissingStaminaAndManaCalibrations_ParseAsNull`, `Load_OldShapeConfig_MissingFullscreenDisclaimerDismissed_DefaultsToFalse`, and any `Load_HudScale_*` tests that use the legacy JSON shape) all parse legacy top-level fields. Without migration (Task 6 will add it), these will silently produce an `AppConfig` whose `Presets` contains only the seeded `Default` preset with no calibration. They will fail their existing assertions. Add `Skip` to each:

```csharp
[Fact(Skip = "Legacy-format migration lands in Task 6 — see plan §6")]
public void Load_OldShapeHpCalibrationJson_DropsFullColorAndTolerance() { ... }
```

(Same `[Fact(Skip = ...)]` on the other three.)

The two `Load_HudScale_*` tests use legacy JSON shape too; same `Skip` treatment.

(c) Update assertions in any test that compares against AppConfig fields that have moved: e.g. `Assert.Equal(new CaptureRegion(...), loaded.HpCalibration!.Region)` becomes `Assert.Equal(new CaptureRegion(...), loaded.ActivePreset.HpCalibration!.Region)`. (These will live on once Task 6's un-skipped tests retarget assertions.)

- [ ] **Step 5.8: Create `AppConfigExtensionsTests.cs`**

Write `tests/GamePartyHud.Tests/Config/AppConfigExtensionsTests.cs`:

```csharp
using GamePartyHud.Capture;
using GamePartyHud.Config;
using GamePartyHud.Party;
using Xunit;

namespace GamePartyHud.Tests.Config;

public class AppConfigExtensionsTests
{
    private static AppConfig WithTwoPresets()
    {
        var preset1 = new Preset(
            Id: "p1", Name: "One",
            Nickname: "Alice", Role: Role.Tank,
            HpCalibration: null, StaminaCalibration: null, ManaCalibration: null);
        var preset2 = new Preset(
            Id: "p2", Name: "Two",
            Nickname: "Bob", Role: Role.Healer,
            HpCalibration: new BarCalibration(new CaptureRegion(0, 0, 100, 10), FillDirection.LTR),
            StaminaCalibration: null, ManaCalibration: null);
        return AppConfig.Defaults with
        {
            Presets = new[] { preset1, preset2 },
            ActivePresetId = "p1",
        };
    }

    [Fact]
    public void UpdatePreset_MutatesOnlyTheActivePreset()
    {
        var cfg = WithTwoPresets();

        var updated = cfg.UpdatePreset(p => p with { Nickname = "Alice2" });

        Assert.Equal("Alice2", updated.Presets[0].Nickname);
        Assert.Equal("Bob", updated.Presets[1].Nickname); // unchanged
    }

    [Fact]
    public void UpdatePreset_LeavesNonActivePresetReferenceIntact()
    {
        var cfg = WithTwoPresets();
        var originalP2 = cfg.Presets[1];

        var updated = cfg.UpdatePreset(p => p with { Nickname = "Alice2" });

        // Non-active preset is passed through by reference (record `with` was
        // not invoked on it), so the reference equality holds.
        Assert.Same(originalP2, updated.Presets[1]);
    }

    [Fact]
    public void UpdatePreset_ReturnsNewAppConfigInstance()
    {
        var cfg = WithTwoPresets();

        var updated = cfg.UpdatePreset(p => p with { Nickname = "Alice2" });

        Assert.NotSame(cfg, updated);
        Assert.NotEqual(cfg, updated); // structurally different (new Presets list)
    }

    [Fact]
    public void ActivePreset_ReturnsMatchingPreset()
    {
        var cfg = WithTwoPresets();
        Assert.Equal("p1", cfg.ActivePreset.Id);
    }

    [Fact]
    public void ActivePreset_StaleIdFallsBackToFirstPreset()
    {
        var cfg = WithTwoPresets() with { ActivePresetId = "does-not-exist" };
        Assert.Equal("p1", cfg.ActivePreset.Id);
    }
}
```

- [ ] **Step 5.9: Build**

Run:
```
dotnet build src/GamePartyHud/GamePartyHud.csproj
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

Common failures:
- `CS0117: 'AppConfig' does not contain a definition for 'Nickname'` etc. → a read site to a moved field was missed. Search:
  ```
  grep -rn "_config\.\(Nickname\|Role\|HpCalibration\|StaminaCalibration\|ManaCalibration\)" src
  grep -rn "_cfg\.\(Nickname\|Role\|HpCalibration\|StaminaCalibration\|ManaCalibration\)" src
  grep -rn "cfg\.\(Nickname\|Role\|HpCalibration\|StaminaCalibration\|ManaCalibration\)" src
  ```
  Each remaining match needs the `.ActivePreset` prefix.

- [ ] **Step 5.10: Run tests**

Run:
```
dotnet test tests/GamePartyHud.Tests/GamePartyHud.Tests.csproj
```
Expected: all tests pass. The skipped tests show as `Skipped:` in the output; their count moves from `Passed` to `Skipped` (about 6 tests).

If the new `AppConfigExtensionsTests` fail with a compile error referencing `UpdatePreset`, the extension class wasn't created (Step 5.2) or the namespace import is missing in the test file.

- [ ] **Step 5.11: Commit**

```
git add src/GamePartyHud/Config/ src/GamePartyHud/App.xaml.cs src/GamePartyHud/MainWindow.xaml.cs src/GamePartyHud/Party/PartyOrchestrator.cs tests/GamePartyHud.Tests/Config/
git commit -m "refactor(config): restructure AppConfig to use presets

Move Nickname / Role / HpCalibration / StaminaCalibration /
ManaCalibration from AppConfig's top level into a new Preset record;
AppConfig now holds Presets (list, >=1) + ActivePresetId. Adds
AppConfig.ActivePreset accessor and AppConfigExtensions.UpdatePreset
helper. All read and write sites in MainWindow / App / PartyOrchestrator
retargeted through ActivePreset / UpdatePreset.

Legacy config.json format support and migration land in the next
commit (Task 6); the four Load_OldShape* tests are marked Skip until
then."
```

---

## Task 6: Migrate legacy `config.json` to the new shape

`ConfigStore.Load` learns to detect a legacy-shape file (no `"Presets"` key) and rebuild it into the new shape by packing the old top-level Nickname/Role/calibrations into a single preset named `Default`. Once landed, the four `[Skip]` tests from Task 5 are un-skipped and pass against the new code path.

**Files:**
- Modify: `src/GamePartyHud/Config/ConfigStore.cs`
- Modify: `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs` (un-skip + retarget assertions + add new migration tests)

- [ ] **Step 6.1: Implement migration in `ConfigStore.Load`**

Replace the body of `Load()` in `src/GamePartyHud/Config/ConfigStore.cs`. The existing implementation:

```csharp
public AppConfig Load()
{
    if (!File.Exists(_path)) return AppConfig.Defaults;
    try
    {
        var json = File.ReadAllText(_path);
        var raw = JsonSerializer.Deserialize<AppConfig>(json, _opts) ?? AppConfig.Defaults;

        return raw with
        {
            RelayUrl = AppConfig.DefaultRelayUrl,
            RelayFallbackUrl = AppConfig.DefaultRelayFallbackUrl,
            PollIntervalMs = AppConfig.Defaults.PollIntervalMs,
            HudScale = SanitiseHudScale(raw.HudScale),
        };
    }
    catch (Exception)
    {
        try { File.Move(_path, _path + ".bad-" + DateTime.UtcNow.Ticks, overwrite: true); } catch { }
        return AppConfig.Defaults;
    }
}
```

becomes:

```csharp
public AppConfig Load()
{
    if (!File.Exists(_path)) return AppConfig.Defaults;
    try
    {
        var json = File.ReadAllText(_path);
        var raw = ParseWithMigration(json);

        return RepairInvariants(raw) with
        {
            RelayUrl = AppConfig.DefaultRelayUrl,
            RelayFallbackUrl = AppConfig.DefaultRelayFallbackUrl,
            PollIntervalMs = AppConfig.Defaults.PollIntervalMs,
            HudScale = SanitiseHudScale(raw.HudScale),
        };
    }
    catch (Exception)
    {
        try { File.Move(_path, _path + ".bad-" + DateTime.UtcNow.Ticks, overwrite: true); } catch { }
        return AppConfig.Defaults;
    }
}

/// <summary>
/// Detects legacy config.json (no top-level "Presets" key) and migrates it
/// to the new shape on the fly: pack the old top-level Nickname / Role /
/// HpCalibration / StaminaCalibration / ManaCalibration into one preset
/// named "Default". New-shape files deserialise straight through.
/// Returns null for unrecoverable JSON; the catch block in Load handles that.
/// </summary>
private AppConfig ParseWithMigration(string json)
{
    var node = JsonNode.Parse(json) as JsonObject
        ?? throw new JsonException("Root is not a JSON object.");

    // Web defaults use camelCase; key-lookup is case-insensitive via TryGetPropertyValue.
    bool hasNewShape = node.ContainsKey("Presets") || node.ContainsKey("presets");
    if (hasNewShape)
    {
        return node.Deserialize<AppConfig>(_opts) ?? AppConfig.Defaults;
    }

    // Legacy shape — pull each old top-level field with a default fallback.
    var defaultPreset = AppConfig.Defaults.ActivePreset;
    var migrated = new Preset(
        Id: AppConfig.DefaultPresetId,
        Name: "Default",
        Nickname: GetString(node, "nickname")           ?? defaultPreset.Nickname,
        Role:     GetEnum<Role>(node, "role")           ?? defaultPreset.Role,
        HpCalibration:      GetObject<BarCalibration>(node, "hpCalibration"),
        StaminaCalibration: GetObject<BarCalibration>(node, "staminaCalibration"),
        ManaCalibration:    GetObject<BarCalibration>(node, "manaCalibration"));

    // Reuse the rest of the JSON for the global fields. Strip the legacy
    // keys we've now consumed plus the three dead ones being removed in
    // this PR so they don't show up as "unknown fields" surprises later.
    foreach (var legacy in new[] {
        "nickname", "role",
        "hpCalibration", "staminaCalibration", "manaCalibration",
        "nicknameRegion",
    })
    {
        node.Remove(legacy);
    }

    // Inject the new fields so we can use the normal deserialiser for everything else.
    node["presets"] = JsonSerializer.SerializeToNode(new[] { migrated }, _opts);
    node["activePresetId"] = AppConfig.DefaultPresetId;

    return node.Deserialize<AppConfig>(_opts) ?? AppConfig.Defaults;
}

private static string? GetString(JsonObject node, string key)
{
    if (!node.TryGetPropertyValue(key, out var v) || v is null) return null;
    return v.GetValue<string>();
}

private static TEnum? GetEnum<TEnum>(JsonObject node, string key) where TEnum : struct, Enum
{
    if (!node.TryGetPropertyValue(key, out var v) || v is null) return null;
    var raw = v.GetValue<string>();
    return Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed) ? parsed : null;
}

private TValue? GetObject<TValue>(JsonObject node, string key)
{
    if (!node.TryGetPropertyValue(key, out var v) || v is null) return default;
    return v.Deserialize<TValue>(_opts);
}

/// <summary>
/// Belt-and-suspenders enforcement of the AppConfig preset invariants:
///   1. Presets.Count >= 1   (else inject the Defaults preset)
///   2. ActivePresetId matches one of the presets  (else point to Presets[0])
///   3. Preset ids are unique  (else regenerate dupes' ids)
/// Repairs are logged so config drift shows up in app.log.
/// </summary>
private static AppConfig RepairInvariants(AppConfig cfg)
{
    var presets = cfg.Presets;
    if (presets is null || presets.Count == 0)
    {
        Log.Warn("AppConfig: Presets was empty on load; seeding with the Defaults preset.");
        presets = AppConfig.Defaults.Presets;
        return cfg with { Presets = presets, ActivePresetId = AppConfig.DefaultPresetId };
    }

    // Re-id duplicates (shouldn't happen via UI, but a hand-edit could).
    var seenIds = new HashSet<string>();
    var repaired = new List<Preset>(presets.Count);
    bool anyChange = false;
    foreach (var p in presets)
    {
        if (seenIds.Add(p.Id))
        {
            repaired.Add(p);
        }
        else
        {
            var newId = Guid.NewGuid().ToString();
            Log.Warn($"AppConfig: duplicate preset id '{p.Id}' on load; regenerated as '{newId}'.");
            repaired.Add(p with { Id = newId });
            anyChange = true;
            seenIds.Add(newId);
        }
    }
    if (anyChange) presets = repaired;

    string activeId = cfg.ActivePresetId;
    if (!seenIds.Contains(activeId))
    {
        var fallback = presets[0].Id;
        Log.Warn($"AppConfig: ActivePresetId '{activeId}' did not match any preset; repaired to '{fallback}'.");
        activeId = fallback;
    }

    return cfg with { Presets = presets, ActivePresetId = activeId };
}
```

Add the necessary `using`s at the top of `ConfigStore.cs`:

```csharp
using System.Collections.Generic;
using System.Text.Json.Nodes;
using GamePartyHud.Diagnostics;
using GamePartyHud.Party;
using GamePartyHud.Capture;
```

(Check existing imports; only add what's not already there.)

- [ ] **Step 6.2: Un-skip and retarget the four legacy-shape tests**

In `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs`, remove the `Skip = "..."` argument from each `[Fact(Skip = ...)]` added in Task 5. Then retarget the assertions to read through `ActivePreset`:

**`Load_OldShapeHpCalibrationJson_DropsFullColorAndTolerance`:**

```csharp
Assert.NotNull(loaded.HpCalibration);
Assert.Equal(new CaptureRegion(0, 10, 20, 300, 18), loaded.HpCalibration!.Region);
Assert.Equal(FillDirection.LTR, loaded.HpCalibration.Direction);
```

becomes

```csharp
Assert.NotNull(loaded.ActivePreset.HpCalibration);
Assert.Equal(new CaptureRegion(10, 20, 300, 18), loaded.ActivePreset.HpCalibration!.Region);
Assert.Equal(FillDirection.LTR, loaded.ActivePreset.HpCalibration.Direction);
```

(Note: post-Task 1, `CaptureRegion` no longer has the leading `Monitor` arg.)

**`Load_OldShapeConfig_MissingStaminaAndManaCalibrations_ParseAsNull`:**

```csharp
Assert.NotNull(loaded.HpCalibration);
Assert.Null(loaded.StaminaCalibration);
Assert.Null(loaded.ManaCalibration);
```

becomes

```csharp
Assert.NotNull(loaded.ActivePreset.HpCalibration);
Assert.Null(loaded.ActivePreset.StaminaCalibration);
Assert.Null(loaded.ActivePreset.ManaCalibration);
```

**`Load_OldShapeConfig_MissingFullscreenDisclaimerDismissed_DefaultsToFalse`** and **`Load_HudScale_*`:** read top-level fields that are still on `AppConfig` (`FullscreenDisclaimerDismissed`, `HudScale`) — no assertion-path change needed; only the `Skip` removal.

- [ ] **Step 6.3: Add new migration-specific tests**

Append these tests to `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs`:

```csharp
[Fact]
public void Load_LegacyConfig_MigratesIntoDefaultPreset()
{
    // A pre-presets config.json (top-level Nickname/Role/HpCalibration, no
    // Presets array) must be silently rebuilt into one preset named
    // "Default" with id "default". User keeps their existing calibration
    // and the next Save writes the new shape.
    File.WriteAllText(_tmp, """
{
  "hpCalibration": {
    "region": { "x": 10, "y": 20, "w": 300, "h": 18 },
    "direction": "LTR"
  },
  "staminaCalibration": null,
  "manaCalibration": null,
  "nicknameRegion": null,
  "nickname": "Yiawahuye",
  "role": "Tank",
  "hudPosition": { "x": 100, "y": 100, "monitor": 0 },
  "hudLocked": true,
  "lastPartyId": "ABC123",
  "pollIntervalMs": 2000,
  "relayUrl": ""
}
""");

    var loaded = new ConfigStore(_tmp).Load();

    Assert.Single(loaded.Presets);
    Assert.Equal(AppConfig.DefaultPresetId, loaded.ActivePresetId);
    var p = loaded.ActivePreset;
    Assert.Equal("Default", p.Name);
    Assert.Equal("Yiawahuye", p.Nickname);
    Assert.Equal(Role.Tank, p.Role);
    Assert.NotNull(p.HpCalibration);
    Assert.Equal(new CaptureRegion(10, 20, 300, 18), p.HpCalibration!.Region);
    Assert.Equal("ABC123", loaded.LastPartyId); // global field preserved
}

[Fact]
public void Load_LegacyConfig_SavesInNewShape()
{
    // After loading legacy JSON, the next Save writes the new format.
    // The freshly-written file must round-trip back unchanged through
    // Load (preset id and contents preserved, no top-level Nickname etc).
    File.WriteAllText(_tmp, """
{
  "nickname": "Tracker",
  "role": "Healer",
  "hpCalibration": null,
  "hudPosition": { "x": 50, "y": 50, "monitor": 0 },
  "hudLocked": true,
  "lastPartyId": null,
  "pollIntervalMs": 2000,
  "relayUrl": ""
}
""");

    var store = new ConfigStore(_tmp);
    var loaded = store.Load();
    store.Save(loaded);

    var disk = File.ReadAllText(_tmp);
    Assert.Contains("\"presets\"", disk, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("\"nickname\":", disk, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("\"nicknameRegion\"", disk, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("\"monitor\"", disk, StringComparison.OrdinalIgnoreCase);

    Assert.Equal(loaded, store.Load());
}

[Fact]
public void Load_PresetsEmpty_RepairsToDefault()
{
    File.WriteAllText(_tmp, """
{
  "presets": [],
  "activePresetId": "default",
  "hudPosition": { "x": 0, "y": 0 },
  "hudLocked": true,
  "lastPartyId": null,
  "pollIntervalMs": 700,
  "relayUrl": ""
}
""");

    var loaded = new ConfigStore(_tmp).Load();
    Assert.Single(loaded.Presets);
    Assert.Equal(AppConfig.DefaultPresetId, loaded.Presets[0].Id);
}

[Fact]
public void Load_ActivePresetIdMismatch_RepairsToFirstPreset()
{
    File.WriteAllText(_tmp, """
{
  "presets": [
    { "id": "real-one", "name": "One", "nickname": "Alice", "role": "Tank",
      "hpCalibration": null, "staminaCalibration": null, "manaCalibration": null }
  ],
  "activePresetId": "ghost-id-not-in-list",
  "hudPosition": { "x": 0, "y": 0 },
  "hudLocked": true,
  "lastPartyId": null,
  "pollIntervalMs": 700,
  "relayUrl": ""
}
""");

    var loaded = new ConfigStore(_tmp).Load();
    Assert.Equal("real-one", loaded.ActivePresetId);
}

[Fact]
public void Load_DuplicatePresetIds_Regenerates()
{
    File.WriteAllText(_tmp, """
{
  "presets": [
    { "id": "same", "name": "A", "nickname": "Alice", "role": "Tank",
      "hpCalibration": null, "staminaCalibration": null, "manaCalibration": null },
    { "id": "same", "name": "B", "nickname": "Bob", "role": "Healer",
      "hpCalibration": null, "staminaCalibration": null, "manaCalibration": null }
  ],
  "activePresetId": "same",
  "hudPosition": { "x": 0, "y": 0 },
  "hudLocked": true,
  "lastPartyId": null,
  "pollIntervalMs": 700,
  "relayUrl": ""
}
""");

    var loaded = new ConfigStore(_tmp).Load();
    Assert.Equal(2, loaded.Presets.Count);
    Assert.NotEqual(loaded.Presets[0].Id, loaded.Presets[1].Id);
}
```

- [ ] **Step 6.4: Build**

Run:
```
dotnet build src/GamePartyHud/GamePartyHud.csproj
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 6.5: Run tests**

Run:
```
dotnet test tests/GamePartyHud.Tests/GamePartyHud.Tests.csproj
```
Expected: all tests pass, including the un-skipped `Load_OldShape*` tests AND the new `Load_LegacyConfig_*` / `Load_PresetsEmpty_*` / `Load_ActivePresetIdMismatch_*` / `Load_DuplicatePresetIds_*` tests.

If a `Load_OldShape*` test fails with `Assert.NotNull(loaded.ActivePreset.HpCalibration)` → the JSON the test feeds in uses `monitor` keys inside `region`; the BarCalibration deserialiser silently ignores them so that shouldn't be the cause. More likely: the `ParseWithMigration` got the casing wrong on a key. Double-check `node.TryGetPropertyValue("hpCalibration", ...)` — `JsonObject.TryGetPropertyValue` is case-sensitive, so a JSON with `"HpCalibration"` (capital H) wouldn't match. Adjust the keys you look up to lowercase first; or change `ParseWithMigration` to first normalise via `JsonObject` index-by-name lookup, which honors the `_opts` PropertyNameCaseInsensitive setting on the eventual `Deserialize` call.

- [ ] **Step 6.6: Commit**

```
git add src/GamePartyHud/Config/ConfigStore.cs tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs
git commit -m "feat(config): migrate legacy config.json to preset shape

ConfigStore.Load detects pre-presets config.json (no top-level
'Presets' key) and packs the existing nickname/role/calibration
fields into a single 'Default' preset on the fly. The next Save
writes the new shape so the migration only runs once per install.

Also enforces the new invariants on load (Presets >= 1, ActivePresetId
must match a preset, preset ids unique); repairs are logged.

The four pre-existing Load_OldShape* tests are un-skipped and now
exercise the migration path; new tests cover the migration's edge
cases (empty Presets array, stale ActivePresetId, duplicate ids)."
```

---

## Task 7: UI — preset selector + active-preset switching

Adds the basic preset switcher to the main window. Dropdown is read-only at this point — no create/rename/delete actions, just selecting an existing preset and watching the Profile/Bars UI refresh.

**Files:**
- Modify: `src/GamePartyHud/MainWindow.xaml` (Profile header row)
- Modify: `src/GamePartyHud/MainWindow.xaml.cs` (PresetCombo binding, OnPresetChanged handler, view model class)

- [ ] **Step 7.1: Add view model class to `MainWindow.xaml.cs`**

Inside the `MainWindow` class (alongside the existing `private sealed record RoleOption(...)`), add:

```csharp
/// <summary>
/// One row in the PresetCombo dropdown. Wraps a Preset id + display name with
/// edit-mode state for inline rename, and a flag distinguishing the "+ New
/// preset" command row from real preset rows.
/// </summary>
private sealed class PresetItemViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private string _name = "";
    private bool _isEditing;

    public string Id { get; init; } = "";
    public bool IsCommandRow { get; init; }

    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; OnPropertyChanged(nameof(Name)); } }
    }

    public bool IsEditing
    {
        get => _isEditing;
        set { if (_isEditing != value) { _isEditing = value; OnPropertyChanged(nameof(IsEditing)); } }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

private readonly System.Collections.ObjectModel.ObservableCollection<PresetItemViewModel> _presetItems = new();
```

(`IsCommandRow` and `IsEditing` aren't used yet; Tasks 8/9 wire them up.)

- [ ] **Step 7.2: Update `MainWindow.xaml` — Profile header**

Find the Profile section header in `src/GamePartyHud/MainWindow.xaml`. The current single-`TextBlock` header:

```xml
<!-- Profile sub-section -->
<TextBlock Text="Profile" FontSize="18" FontWeight="SemiBold" Margin="0,0,0,10"/>
```

Replace with:

```xml
<!-- Profile sub-section. Heading shares a row with the preset selector;
     switching a preset swaps Nickname/Role/Bars content via PopulateFromConfig. -->
<Grid Margin="0,0,0,10">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*"/>
        <ColumnDefinition Width="Auto"/>
    </Grid.ColumnDefinitions>
    <TextBlock Grid.Column="0"
               Text="Profile" FontSize="18" FontWeight="SemiBold"
               VerticalAlignment="Center"/>
    <ComboBox Grid.Column="1"
              x:Name="PresetCombo"
              Width="180"
              SelectionChanged="OnPresetChanged">
        <ComboBox.ItemTemplate>
            <DataTemplate>
                <TextBlock Text="{Binding Name}" VerticalAlignment="Center"/>
            </DataTemplate>
        </ComboBox.ItemTemplate>
    </ComboBox>
</Grid>
```

- [ ] **Step 7.3: Initialise `PresetCombo` in the constructor**

In `src/GamePartyHud/MainWindow.xaml.cs`, find the constructor:

```csharp
public MainWindow(IController controller)
{
    InitializeComponent();
    _ctl = controller;

    RoleCombo.ItemsSource = RoleOptions;
    // ...
```

Add right after `RoleCombo.ItemsSource = RoleOptions;`:

```csharp
PresetCombo.ItemsSource = _presetItems;
```

- [ ] **Step 7.4: Add a `RebuildPresetItems` method**

Add this private method to `MainWindow.xaml.cs`:

```csharp
/// <summary>
/// Rebuilds the PresetCombo dropdown items from the current AppConfig.Presets list.
/// Called from PopulateFromConfig (initial load + after every config-change refresh).
/// The selected item is set to the row matching ActivePresetId, with the _populating
/// guard preventing the SelectionChanged handler from treating that programmatic
/// selection as a user action.
/// </summary>
private void RebuildPresetItems()
{
    var cfg = _ctl.Config;
    _presetItems.Clear();
    foreach (var p in cfg.Presets)
    {
        _presetItems.Add(new PresetItemViewModel { Id = p.Id, Name = p.Name });
    }
    // Selection is restored by PopulateFromConfig setting PresetCombo.SelectedItem
    // — see Step 7.5.
}
```

- [ ] **Step 7.5: Call `RebuildPresetItems` and set selection in `PopulateFromConfig`**

In `PopulateFromConfig`, inside the `try` block, add at the top (before `FullscreenDisclaimer.IsOpen = ...`):

```csharp
RebuildPresetItems();
var activeId = cfg.ActivePresetId;
PresetCombo.SelectedItem = _presetItems.FirstOrDefault(i => i.Id == activeId);
```

The rest of `PopulateFromConfig` (already updated in Task 5 Step 5.4 to read through `cfg.ActivePreset`) takes care of the Nickname/Role/Bars refresh.

- [ ] **Step 7.6: Add `OnPresetChanged` handler**

Add to `MainWindow.xaml.cs`:

```csharp
private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
{
    if (_populating) return;
    if (PresetCombo.SelectedItem is not PresetItemViewModel item) return;
    if (item.IsCommandRow) return; // Tasks 8 wire up the create flow on this row.

    // No-op if the user clicked the already-active row.
    if (item.Id == _ctl.Config.ActivePresetId) return;

    _ctl.UpdateConfig(_ctl.Config with { ActivePresetId = item.Id });
    PopulateFromConfig();             // refreshes Nickname / Role / Bars
    UpdateJoinButtonState();          // HP-calibration of new preset may flip JoinButton state
    Log.Info($"MainWindow: active preset changed to '{item.Name}' (Id={item.Id}).");
}
```

- [ ] **Step 7.7: Build**

Run:
```
dotnet build src/GamePartyHud/GamePartyHud.csproj
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 7.8: Manual verification (single-preset for now)**

Run:
```
dotnet run --project src/GamePartyHud/GamePartyHud.csproj
```

Walk through:
1. App opens. Profile section header shows `Profile` on the left, the preset `ComboBox` on the right with `[Default ▾]` (or whatever the migrated preset name is).
2. Open the dropdown — exactly one row, with the preset's name. Clicking it does nothing visible (already selected).
3. Quit the app.
4. Open `%AppData%\GamePartyHud\config.json` in a text editor. Manually add a second preset by editing the `Presets` array:
   ```json
   "Presets": [
     { "Id": "default", "Name": "Default", ... },
     { "Id": "test2", "Name": "TestAlt", "Nickname": "AltChar", "Role": "Tank",
       "HpCalibration": null, "StaminaCalibration": null, "ManaCalibration": null }
   ],
   ```
5. Relaunch. Dropdown now shows both names. Click `TestAlt` → Nickname textbox flips to `AltChar`, Role combo flips to `Tank`, HP region chip flips back to `Not set yet.`. Click `Default` → reverts.
6. Quit; reopen `config.json` — `ActivePresetId` reflects whichever you last selected.

- [ ] **Step 7.9: Commit**

```
git add src/GamePartyHud/MainWindow.xaml src/GamePartyHud/MainWindow.xaml.cs
git commit -m "feat(ui): add preset selector ComboBox in Profile header

Right side of the Profile section heading now shows a 180px ComboBox
listing all presets by name. Selecting a different preset writes
ActivePresetId via UpdateConfig and re-runs PopulateFromConfig so the
Nickname / Role / Bars UI refreshes against the new preset.

Create / rename / delete actions land in Tasks 8 / 9 / 10."
```

---

## Task 8: UI — "+ New preset" command row

Adds the dropdown footer entry that creates a new empty preset and auto-activates it.

**Files:**
- Modify: `src/GamePartyHud/MainWindow.xaml` (PresetCombo template/selector)
- Modify: `src/GamePartyHud/MainWindow.xaml.cs` (OnPresetChanged handles command row; new OnCreatePreset method; RebuildPresetItems appends the command row)

- [ ] **Step 8.1: Add a TemplateSelector and the command row template**

In `src/GamePartyHud/MainWindow.xaml`, replace the `<ComboBox.ItemTemplate>` set earlier with a template selector pattern. Find the PresetCombo block:

```xml
<ComboBox Grid.Column="1"
          x:Name="PresetCombo"
          Width="180"
          SelectionChanged="OnPresetChanged">
    <ComboBox.ItemTemplate>
        <DataTemplate>
            <TextBlock Text="{Binding Name}" VerticalAlignment="Center"/>
        </DataTemplate>
    </ComboBox.ItemTemplate>
</ComboBox>
```

Replace with:

```xml
<ComboBox Grid.Column="1"
          x:Name="PresetCombo"
          Width="180"
          SelectionChanged="OnPresetChanged">
    <ComboBox.Resources>
        <!-- Preset row template -->
        <DataTemplate x:Key="PresetRowTemplate">
            <TextBlock Text="{Binding Name}" VerticalAlignment="Center"/>
        </DataTemplate>
        <!-- "+ New preset" command-row template -->
        <DataTemplate x:Key="CommandRowTemplate">
            <StackPanel Orientation="Horizontal">
                <TextBlock Text="+" FontWeight="Bold" Margin="0,0,8,0"
                           VerticalAlignment="Center" Foreground="#FF66B2FF"/>
                <TextBlock Text="New preset" VerticalAlignment="Center"
                           Foreground="#FF66B2FF"/>
            </StackPanel>
        </DataTemplate>
    </ComboBox.Resources>
    <ComboBox.ItemTemplateSelector>
        <local:PresetItemTemplateSelector
            PresetRowTemplate="{StaticResource PresetRowTemplate}"
            CommandRowTemplate="{StaticResource CommandRowTemplate}"/>
    </ComboBox.ItemTemplateSelector>
</ComboBox>
```

If `xmlns:local="clr-namespace:GamePartyHud"` is not already on the root `<ui:FluentWindow>` element, add it.

- [ ] **Step 8.2: Implement the `PresetItemTemplateSelector` class**

Create file `src/GamePartyHud/PresetItemTemplateSelector.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;

namespace GamePartyHud;

/// <summary>
/// Picks between the regular preset-row template and the "+ New preset"
/// command-row template based on <see cref="MainWindow.PresetItemViewModel.IsCommandRow"/>.
/// </summary>
public sealed class PresetItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? PresetRowTemplate { get; set; }
    public DataTemplate? CommandRowTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is null) return base.SelectTemplate(item, container);
        // Compare by property name via reflection so we don't have to make
        // PresetItemViewModel public just for this selector. Single property
        // check — fast enough for a dropdown.
        var type = item.GetType();
        var isCmd = type.GetProperty("IsCommandRow")?.GetValue(item) as bool? ?? false;
        return isCmd ? CommandRowTemplate : PresetRowTemplate;
    }
}
```

Alternatively, make `MainWindow.PresetItemViewModel` non-nested (own file) and access it directly. Reflection is used here to keep the view model nested inside `MainWindow` as it is today, minimising the diff and keeping the type private.

- [ ] **Step 8.3: Append the command row in `RebuildPresetItems`**

Update `RebuildPresetItems` from Task 7 Step 7.4:

```csharp
private void RebuildPresetItems()
{
    var cfg = _ctl.Config;
    _presetItems.Clear();
    foreach (var p in cfg.Presets)
    {
        _presetItems.Add(new PresetItemViewModel { Id = p.Id, Name = p.Name });
    }
    _presetItems.Add(new PresetItemViewModel { Id = "", Name = "+ New preset", IsCommandRow = true });
}
```

(The command row's `Id` is `""` — it's never used because `OnPresetChanged` returns early before accessing it.)

- [ ] **Step 8.4: Handle the command-row selection in `OnPresetChanged`**

Find the early-return in `OnPresetChanged` from Task 7 Step 7.6:

```csharp
if (item.IsCommandRow) return;
```

Replace with:

```csharp
if (item.IsCommandRow)
{
    OnCreatePreset();
    return;
}
```

- [ ] **Step 8.5: Implement `OnCreatePreset`**

Add to `MainWindow.xaml.cs`:

```csharp
/// <summary>
/// Creates a new empty preset (placeholder name "New preset"/"New preset N"),
/// activates it, refreshes the UI, and reverts PresetCombo's selection from
/// the "+ New preset" command row to the newly-created real row. The user
/// can then fill in nickname / role / bar regions exactly as on a fresh install.
/// </summary>
private void OnCreatePreset()
{
    var cfg = _ctl.Config;
    var newId = System.Guid.NewGuid().ToString();
    var newName = NextAvailableName(cfg);

    var newPreset = new Preset(
        Id: newId,
        Name: newName,
        Nickname: "",
        Role: Role.Utility,
        HpCalibration: null,
        StaminaCalibration: null,
        ManaCalibration: null);

    var updated = cfg with
    {
        Presets = cfg.Presets.Append(newPreset).ToList(),
        ActivePresetId = newId,
    };
    _ctl.UpdateConfig(updated);

    PopulateFromConfig();             // refreshes everything against the new preset
    UpdateJoinButtonState();          // empty calibration → Join stays disabled

    // PopulateFromConfig set SelectedItem; the dropdown now reads as the new
    // preset row. The command row is no longer selected.
    Log.Info($"MainWindow: created new preset '{newName}' (Id={newId}).");
}

private static string NextAvailableName(AppConfig cfg)
{
    const string baseName = "New preset";
    var existing = cfg.Presets.Select(p => p.Name).ToHashSet();
    if (!existing.Contains(baseName)) return baseName;
    for (int n = 2; n < 100; n++)
    {
        var candidate = $"{baseName} {n}";
        if (!existing.Contains(candidate)) return candidate;
    }
    return $"{baseName} {System.DateTime.UtcNow.Ticks}"; // pathological fallback
}
```

- [ ] **Step 8.6: Build**

Run:
```
dotnet build src/GamePartyHud/GamePartyHud.csproj
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 8.7: Manual verification**

Run the app. Open the PresetCombo dropdown — the `+ New preset` row appears in blue at the bottom of the list, visually distinct from preset rows. Click it. Profile/Bars sections clear (empty Nickname, Utility role, all chips read "Not set yet."). Open the dropdown again — the new preset appears in the list with name `New preset` (or `New preset 2`, etc.) and is selected. Click `+ New preset` a second time — `New preset 2` is added. Quit and reopen `%AppData%\GamePartyHud\config.json` — all newly-created presets are persisted.

- [ ] **Step 8.8: Commit**

```
git add src/GamePartyHud/MainWindow.xaml src/GamePartyHud/MainWindow.xaml.cs src/GamePartyHud/PresetItemTemplateSelector.cs
git commit -m "feat(ui): add '+ New preset' command row to preset dropdown

The bottom row of the PresetCombo dropdown is now a styled '+ New
preset' entry rendered via DataTemplateSelector. Clicking it creates
a new empty preset with auto-generated name 'New preset' (or 'New
preset 2', etc.), activates it, and refreshes the Profile/Bars UI.
The user fills in nickname/role/bar regions via the existing flows."
```

---

## Task 9: UI — inline rename (pencil)

Adds the per-row pencil icon that swaps the preset-row template to an inline `TextBox` for renaming.

**Files:**
- Modify: `src/GamePartyHud/MainWindow.xaml` (PresetRowTemplate)
- Modify: `src/GamePartyHud/MainWindow.xaml.cs` (rename handlers + validation)

- [ ] **Step 9.1: Update the `PresetRowTemplate`**

In `src/GamePartyHud/MainWindow.xaml`, find the `PresetRowTemplate` from Task 8 Step 8.1:

```xml
<DataTemplate x:Key="PresetRowTemplate">
    <TextBlock Text="{Binding Name}" VerticalAlignment="Center"/>
</DataTemplate>
```

Replace with:

```xml
<DataTemplate x:Key="PresetRowTemplate">
    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="*"/>
            <ColumnDefinition Width="Auto"/>
        </Grid.ColumnDefinitions>

        <!-- Display name (default) — collapsed when IsEditing -->
        <TextBlock Grid.Column="0"
                   Text="{Binding Name}"
                   VerticalAlignment="Center"
                   TextTrimming="CharacterEllipsis">
            <TextBlock.Style>
                <Style TargetType="TextBlock">
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding IsEditing}" Value="True">
                            <Setter Property="Visibility" Value="Collapsed"/>
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </TextBlock.Style>
        </TextBlock>

        <!-- Inline-edit TextBox — visible only when IsEditing -->
        <TextBox Grid.Column="0"
                 Text="{Binding Name, UpdateSourceTrigger=PropertyChanged}"
                 VerticalAlignment="Center"
                 Tag="{Binding Id}"
                 KeyDown="OnPresetRenameKeyDown"
                 LostKeyboardFocus="OnPresetRenameLostFocus">
            <TextBox.Style>
                <Style TargetType="TextBox">
                    <Setter Property="Visibility" Value="Collapsed"/>
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding IsEditing}" Value="True">
                            <Setter Property="Visibility" Value="Visible"/>
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </TextBox.Style>
        </TextBox>

        <!-- Pencil icon: starts inline rename -->
        <Button Grid.Column="1"
                Background="Transparent" BorderThickness="0"
                Padding="4,0" Margin="4,0,0,0"
                Tag="{Binding Id}"
                Click="OnPresetRenameClick"
                ToolTip="Rename">
            <TextBlock Text="✏" FontSize="12"/>
        </Button>
    </Grid>
</DataTemplate>
```

- [ ] **Step 9.2: Implement rename handlers**

Add to `MainWindow.xaml.cs`:

```csharp
/// <summary>
/// Pencil icon click → enter inline rename mode for the clicked row.
/// Keeps the dropdown open so the TextBox stays visible while the user types.
/// </summary>
private void OnPresetRenameClick(object sender, RoutedEventArgs e)
{
    if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
    var item = _presetItems.FirstOrDefault(i => i.Id == id);
    if (item is null) return;

    foreach (var other in _presetItems) other.IsEditing = false; // only one row in edit at a time
    item.IsEditing = true;
    PresetCombo.IsDropDownOpen = true;

    // Defer focus until after the visual swap completes so the new TextBox exists.
    Dispatcher.BeginInvoke(new Action(() =>
    {
        var textBox = FindTextBoxForPreset(id);
        if (textBox is not null)
        {
            textBox.Focus();
            textBox.SelectAll();
        }
    }), System.Windows.Threading.DispatcherPriority.Input);
}

private TextBox? FindTextBoxForPreset(string id)
{
    // Walk the dropdown's visual tree to find the TextBox whose Tag matches id.
    foreach (var obj in _presetItems)
    {
        var container = PresetCombo.ItemContainerGenerator.ContainerFromItem(obj) as ComboBoxItem;
        if (container is null) continue;
        var tb = FindChild<TextBox>(container, t => (t.Tag as string) == id);
        if (tb is not null) return tb;
    }
    return null;
}

private static T? FindChild<T>(DependencyObject parent, Func<T, bool> match) where T : DependencyObject
{
    for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
    {
        var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
        if (child is T t && match(t)) return t;
        var grand = FindChild<T>(child, match);
        if (grand is not null) return grand;
    }
    return null;
}

private void OnPresetRenameKeyDown(object sender, KeyEventArgs e)
{
    if (sender is not TextBox tb || tb.Tag is not string id) return;
    if (e.Key == Key.Enter)
    {
        e.Handled = true;
        CommitOrRevertRename(id, tb, commit: true);
    }
    else if (e.Key == Key.Escape)
    {
        e.Handled = true;
        CommitOrRevertRename(id, tb, commit: false);
    }
}

private void OnPresetRenameLostFocus(object sender, RoutedEventArgs e)
{
    if (sender is not TextBox tb || tb.Tag is not string id) return;
    CommitOrRevertRename(id, tb, commit: true);
}

private void CommitOrRevertRename(string id, TextBox tb, bool commit)
{
    var item = _presetItems.FirstOrDefault(i => i.Id == id);
    if (item is null) return;

    if (!commit)
    {
        // Revert: re-read from config.
        var stored = _ctl.Config.Presets.FirstOrDefault(p => p.Id == id);
        if (stored is not null) item.Name = stored.Name;
        item.IsEditing = false;
        return;
    }

    var raw = (tb.Text ?? "").Trim();
    if (raw.Length == 0)
    {
        // Empty → revert
        var stored = _ctl.Config.Presets.FirstOrDefault(p => p.Id == id);
        if (stored is not null) item.Name = stored.Name;
        item.IsEditing = false;
        return;
    }

    bool collides = _ctl.Config.Presets.Any(p => p.Id != id && p.Name == raw);
    if (collides)
    {
        // Show a brief MessageBox; revert to stored name.
        System.Windows.MessageBox.Show(
            $"A preset named '{raw}' already exists.",
            "Game Party HUD", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        var stored = _ctl.Config.Presets.FirstOrDefault(p => p.Id == id);
        if (stored is not null) item.Name = stored.Name;
        item.IsEditing = false;
        return;
    }

    // Commit
    var cfg = _ctl.Config;
    var updated = cfg with
    {
        Presets = cfg.Presets
            .Select(p => p.Id == id ? p with { Name = raw } : p)
            .ToList(),
    };
    _ctl.UpdateConfig(updated);
    item.Name = raw;
    item.IsEditing = false;
    Log.Info($"MainWindow: renamed preset Id={id} to '{raw}'.");
}
```

- [ ] **Step 9.3: Build**

Run:
```
dotnet build src/GamePartyHud/GamePartyHud.csproj
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 9.4: Manual verification**

Run the app. Open the dropdown. Click the pencil ✏ on the active preset's row → the name swaps to a `TextBox` (autofocused, text selected). Type a new name; press Enter → dropdown row label updates, `config.json` saved with the new name. Click ✏ again, type a name that already exists, press Enter → a MessageBox warns and the rename reverts. Click ✏, type something, press Esc → reverts. Click ✏ on a different preset → only that row goes to edit mode (the previous edit-mode row exits without committing).

- [ ] **Step 9.5: Commit**

```
git add src/GamePartyHud/MainWindow.xaml src/GamePartyHud/MainWindow.xaml.cs
git commit -m "feat(ui): inline rename for preset dropdown rows

Each preset row in the PresetCombo dropdown now has a ✏ pencil icon
on the right; clicking it swaps the name TextBlock to a TextBox for
inline editing. Enter / blur commits the new name (rejecting empty
strings and collisions with other preset names via a MessageBox);
Esc reverts. The dropdown stays open during the edit."
```

---

## Task 10: UI — delete with confirmation (×)

Adds the per-row × icon. Deleting the active preset auto-switches to the first remaining; deleting the last preset is forbidden (button disabled).

**Files:**
- Modify: `src/GamePartyHud/MainWindow.xaml` (PresetRowTemplate)
- Modify: `src/GamePartyHud/MainWindow.xaml.cs` (delete handler)

- [ ] **Step 10.1: Add the × button to `PresetRowTemplate`**

In `src/GamePartyHud/MainWindow.xaml`, the `PresetRowTemplate` from Task 9 has 2 columns (`*` + `Auto` for the pencil). Add a third column for the ×. Change `ColumnDefinitions` to:

```xml
<Grid.ColumnDefinitions>
    <ColumnDefinition Width="*"/>
    <ColumnDefinition Width="Auto"/>  <!-- pencil -->
    <ColumnDefinition Width="Auto"/>  <!-- × -->
</Grid.ColumnDefinitions>
```

And append a third element after the pencil `Button`:

```xml
<!-- × delete icon -->
<Button Grid.Column="2"
        Background="Transparent" BorderThickness="0"
        Padding="4,0" Margin="2,0,0,0"
        Tag="{Binding Id}"
        Click="OnPresetDeleteClick"
        ToolTip="Delete">
    <TextBlock Text="✕" FontSize="12"/>
</Button>
```

- [ ] **Step 10.2: Implement `OnPresetDeleteClick`**

Add to `MainWindow.xaml.cs`:

```csharp
/// <summary>
/// × icon click → confirm and delete the preset. If only one preset remains
/// the delete is refused (we always need at least one). If the deleted preset
/// is the currently-active one, switch to the first remaining preset.
/// </summary>
private void OnPresetDeleteClick(object sender, RoutedEventArgs e)
{
    if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
    var cfg = _ctl.Config;
    var target = cfg.Presets.FirstOrDefault(p => p.Id == id);
    if (target is null) return;

    if (cfg.Presets.Count <= 1)
    {
        System.Windows.MessageBox.Show(
            "At least one preset is required.",
            "Game Party HUD", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        return;
    }

    var confirm = System.Windows.MessageBox.Show(
        $"Delete preset '{target.Name}'?",
        "Game Party HUD",
        System.Windows.MessageBoxButton.YesNo,
        System.Windows.MessageBoxImage.Question);
    if (confirm != System.Windows.MessageBoxResult.Yes) return;

    var remaining = cfg.Presets.Where(p => p.Id != id).ToList();
    var newActive = cfg.ActivePresetId == id ? remaining[0].Id : cfg.ActivePresetId;
    var updated = cfg with { Presets = remaining, ActivePresetId = newActive };

    _ctl.UpdateConfig(updated);
    PopulateFromConfig();              // refreshes selector + Profile/Bars
    UpdateJoinButtonState();
    Log.Info($"MainWindow: deleted preset '{target.Name}' (Id={id}); active is now {newActive}.");
}
```

(Disabling the × on the only-remaining preset row could be done via a `DataTrigger` bound to a `CanDelete` property on the view model — added complexity not worth it for v1. The MessageBox refusal in the handler is the same UX outcome.)

- [ ] **Step 10.3: Build**

Run:
```
dotnet build src/GamePartyHud/GamePartyHud.csproj
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

- [ ] **Step 10.4: Manual verification**

Run the app. Create two presets (use `+ New preset` from Task 8). Open the dropdown — both rows show pencil + ×. Click × on the inactive one → confirm dialog → click Yes → dropdown updates (one row now). Click × on the remaining one → "At least one preset is required" MessageBox; no deletion. Create another preset; switch back to `Default`; click × on `Default` (the active one) → confirm dialog → Yes → active becomes the other preset, Profile/Bars refreshes.

- [ ] **Step 10.5: Commit**

```
git add src/GamePartyHud/MainWindow.xaml src/GamePartyHud/MainWindow.xaml.cs
git commit -m "feat(ui): delete-with-confirmation for preset dropdown rows

Each preset row gains a ✕ icon on the right of the pencil. Click →
Yes/No MessageBox → on Yes the preset is removed from config.
Deleting the active preset auto-switches to the first remaining one
and refreshes the Profile/Bars UI. Attempting to delete the only
remaining preset shows an info MessageBox and refuses."
```

---

## Task 11: Final manual verification (spec §10 checklist)

Walk the spec's manual verification list end-to-end to catch anything the per-task checks missed (especially mid-party preset switching, which exercises the orchestrator wiring from Task 5 in a real party).

- [ ] **Step 11.1: Walk spec §10 checklist**

Run the app:
```
dotnet run --project src/GamePartyHud/GamePartyHud.csproj
```

Tick each item from `docs/superpowers/specs/2026-05-17-character-presets-design.md` §10:

1. Fresh install (delete `%AppData%\GamePartyHud\config.json` first) → seeded `Default` preset; dropdown shows `[Default ▾]`.
2. Existing install (a backup pre-migration `config.json` is in your test setup) → migrates silently; dropdown shows `[Default ▾]`; Nickname / Role / region chips populated from migrated values. After first save, inspect `config.json` and confirm: Presets array present, ActivePresetId present, **no `NicknameRegion`**, **no `Monitor` inside `HudPosition`**, **no `Monitor` inside any `CaptureRegion`**.
3. `+ New preset` → empty preset created and activated; Profile/Bars empty; chip says `Not set yet.`
4. Pick HP / Stamina / Mana region under the new preset → chips populate; switch back to `Default` → original chips; switch forward → new preset's chips.
5. Pencil rename → edit mode → Enter commits; Esc reverts; collision shows error.
6. × delete on non-active preset → confirm dialog → removes.
7. × delete on active preset (with ≥2) → confirms → deletes → auto-switches.
8. × delete on the only preset → info MessageBox; no deletion.
9. Join party with one preset → leave → switch presets → Join button correctly evaluates new preset's HP-calibration / Nickname for `ValidateBeforeJoiningParty`.
10. **Mid-party switch:** join party with preset A → switch to preset B → within ~1 s teammate(s) see the new nickname/role/bar values reflect preset B. (Requires a second machine or a second app instance with the same `config.json` to verify; otherwise verify locally by checking that the HUD's self-card name flips within ~1 s.)
11. Quit + relaunch → app reopens on the previously-active preset; `LastPartyId` is still prepopulated regardless of preset.
12. Manually corrupt `ActivePresetId` in `config.json` to a non-existent id → app starts, falls back to first preset, repairs the field on next save (check `config.json` after a UI change to confirm the ActivePresetId got fixed).

- [ ] **Step 11.2: Confirm `git diff` scope**

Run:
```
git diff main --stat
```

Expected file list (post-cleanup tasks too):
```
src/GamePartyHud/Capture/CaptureRegion.cs
src/GamePartyHud/Calibration/RegionSelectorWindow.xaml.cs
src/GamePartyHud/Config/AppConfig.cs
src/GamePartyHud/Config/AppConfigExtensions.cs          (new)
src/GamePartyHud/Config/ConfigStore.cs
src/GamePartyHud/Config/Preset.cs                       (new)
src/GamePartyHud/App.xaml.cs
src/GamePartyHud/MainWindow.xaml
src/GamePartyHud/MainWindow.xaml.cs
src/GamePartyHud/Party/PartyOrchestrator.cs
src/GamePartyHud/PresetItemTemplateSelector.cs          (new)
tests/GamePartyHud.Tests/Capture/BarAnalyzerTests.cs
tests/GamePartyHud.Tests/Capture/ManaBarDiagnosticTests.cs
tests/GamePartyHud.Tests/Capture/SampleImageDiagnosticTests.cs
tests/GamePartyHud.Tests/Capture/SampleImageRegressionTests.cs
tests/GamePartyHud.Tests/Config/AppConfigExtensionsTests.cs   (new)
tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs
```

If anything else shows up, investigate — likely an inadvertent edit elsewhere.

- [ ] **Step 11.3: Final test run**

Run:
```
dotnet test tests/GamePartyHud.Tests/GamePartyHud.Tests.csproj
```
Expected: all tests pass. No tests skipped (Task 6 un-skipped the four legacy-shape tests).

---

## Done criteria

After Task 11 passes:
- `dotnet build` clean, `dotnet test` green, all 16 manual-verification items confirmed by eye.
- `git log main..HEAD --oneline` shows the 10 task commits (1–10; Task 11 is verification-only and produces no commit).
- `git diff main --stat` matches the file list in Step 11.2 — nothing else.

Out of scope for this plan (do not do these here):
- Per-preset HUD position / scale / lock state.
- Per-preset `LastPartyId`.
- Per-preset fullscreen-disclaimer dismissal.
- Cloning / duplicating an existing preset.
- Import / export presets.
- Sharing presets across teammates.
