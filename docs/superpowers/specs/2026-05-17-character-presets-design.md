# Character presets

**Date:** 2026-05-17
**Scope:** `src/GamePartyHud/Config/*`, `src/GamePartyHud/Capture/CaptureRegion.cs`, `MainWindow.xaml(.cs)`, `App.xaml.cs`, `RegionSelectorWindow.xaml.cs`, anywhere that today reads `_config.Nickname` / `Role` / `*Calibration`. New tests under `tests/GamePartyHud.Tests/Config/`.

## Goals

Let a single install hold multiple character profiles ("presets") so a user who plays more than one character can switch nickname + role + bar-region calibrations in one click instead of recalibrating from scratch every time they change character in-game.

1. A preset bundles nickname, role, and three screen-region calibrations (HP / Stamina / Mana). Switching the active preset swaps all of those at once.
2. The main window's Profile section gains a preset selector on the right side of the "Profile" heading, with inline create / rename / delete affordances on the dropdown items.
3. Existing installs migrate transparently on first launch: the current top-level nickname/role/calibrations move into a single auto-created preset called `Default`.
4. Switching presets while in a party is allowed; teammates see the new identity within one broadcast tick. No special "republish identity" code path is needed — the existing per-tick broadcast picks up the new active preset's values automatically.
5. **Drop three unused fields** while we're already touching the config layer: `AppConfig.NicknameRegion` (vestige of the v0.1.0 calibration wizard — never read), `HudPosition.Monitor` (placeholder for multi-monitor support — never read), and `CaptureRegion.Monitor` (same — never read). Removing them keeps the new preset shape minimal and avoids carrying dead fields forward into the new format.

## Non-goals

- Per-preset HUD position / scale / lock state. HUD geometry stays a single global value per machine.
- Per-preset `LastPartyId`. The global value keeps the 1-click rejoin behaviour regardless of which preset is active.
- Per-preset fullscreen-disclaimer dismissal or other dismissed UI banners.
- Cloning / duplicating an existing preset. (Could be added later as a third action icon next to ✏ ×; not in v1.)
- Import / export presets to a portable file.
- Sharing presets across teammates.
- Per-game presets. The app remains MO2-focused per `CLAUDE.md`.

## Reference

User requirement, summarised from chat: *"there is a scenario when people switch from one character to another and they have slightly different bar positions and different nickname. we should also consider effect on how current settings are stored in settings.json"*.

Brainstorming decisions captured:
- Preset scope: Nickname + Role + 4 region calibrations.
- Switcher placement: combobox on the right side of the Profile header row.
- Management: inline in the dropdown — `+ New preset` row, per-row ✏ rename, per-row × delete.
- Storage: embed `Presets[]` + `ActivePresetId` in `config.json`; remove the corresponding top-level fields.
- Migration: auto-promote existing nickname/role/calibrations into a single preset named `Default`.
- Mid-party switching: allowed, broadcast new identity live.

## Design

### 0. Dead-field removal

Before touching the preset shape, three currently-declared-but-never-read fields are removed:

| Type | Field | Why it's safe to remove |
|---|---|---|
| `AppConfig` | `NicknameRegion` (`CaptureRegion?`) | Set to `null` in defaults and reset to `null` in `MainWindow.OnPickRegion`. No read sites anywhere in `src/`. One test fixture (`ConfigStoreTests.cs:51`) sets it during a serialization round-trip but doesn't assert behavior. |
| `HudPosition` | `Monitor` (`int`) | No read sites anywhere in `src/` or `tests/`. Originally a placeholder for multi-monitor support that hasn't materialized. |
| `CaptureRegion` | `Monitor` (`int`) | Set to `0` in `RegionSelectorWindow` with a "Single-monitor assumption for v0.1.0" comment. No read sites. |

Knock-on edits these removals require:
- `MainWindow.xaml.cs` `OnPickRegion` — drop the `, NicknameRegion = null` from the HP arm.
- `RegionSelectorWindow.xaml.cs` — construct `CaptureRegion` without the `Monitor: 0` argument; drop the now-irrelevant comment.
- `tests/.../ConfigStoreTests.cs:51` — drop the `NicknameRegion =` line from the test fixture.
- `AppConfig.Defaults` — drop the `NicknameRegion: null,` initializer.

The legacy `config.json` migration (§4) silently discards `"NicknameRegion"`, `"Monitor"` keys if present — `JsonSerializerDefaults.Web` ignores unknown properties by default, so this happens automatically when the old shape is parsed into the new records.

### 1. Data model

New file `src/GamePartyHud/Config/Preset.cs`:

```csharp
namespace GamePartyHud.Config;

public sealed record Preset(
    string Id,                                 // GUID string for user-created presets,
                                               // or "default" for the auto-seeded one;
                                               // stable across renames either way
    string Name,                               // user-facing label, unique within Presets
    string Nickname,
    Party.Role Role,
    Capture.BarCalibration? HpCalibration,
    Capture.BarCalibration? StaminaCalibration,
    Capture.BarCalibration? ManaCalibration);
```

`AppConfig` (revised) — `src/GamePartyHud/Config/AppConfig.cs`:

| Field                              | Before     | After                                  |
|------------------------------------|------------|----------------------------------------|
| `Nickname`                         | top-level  | moved into `Preset`                    |
| `Role`                             | top-level  | moved into `Preset`                    |
| `HpCalibration`                    | top-level  | moved into `Preset`                    |
| `StaminaCalibration`               | top-level  | moved into `Preset`                    |
| `ManaCalibration`                  | top-level  | moved into `Preset`                    |
| `NicknameRegion`                   | top-level  | **removed entirely** (see §0)          |
| `Presets`                          | —          | new `IReadOnlyList<Preset>`, always ≥1 |
| `ActivePresetId`                   | —          | new `string`, must match a `Preset.Id` |
| `HudPosition`                      | top-level  | unchanged (but `HudPosition.Monitor` removed — see §0) |
| `HudLocked`                        | top-level  | unchanged                              |
| `HudScale`                         | top-level  | unchanged                              |
| `LastPartyId`                      | top-level  | unchanged                              |
| `FullscreenDisclaimerDismissed`    | top-level  | unchanged                              |
| `PollIntervalMs`                   | top-level  | unchanged (binary-owned)               |
| `RelayUrl` / `RelayFallbackUrl`    | top-level  | unchanged (binary-owned)               |

Convenience accessor added to `AppConfig`:

```csharp
[JsonIgnore]
public Preset ActivePreset =>
    Presets.FirstOrDefault(p => p.Id == ActivePresetId) ?? Presets[0];
```

If `ActivePresetId` is stale (deleted preset id), the getter falls back to the first preset; the `Load`/`Save` paths repair the invariant so this fallback only matters during an in-flight delete + write. A one-line warn log fires when the fallback is hit so we can detect drift.

### 2. Defaults

`AppConfig.Defaults` is rebuilt with one seeded preset matching the prior per-field defaults:

```csharp
public static AppConfig Defaults { get; } = new(
    Presets: new[]
    {
        new Preset(
            Id: "default",                         // sentinel id for the bootstrap preset
            Name: "Default",
            Nickname: "Player",
            Role: Role.Utility,
            HpCalibration: null,
            StaminaCalibration: null,
            ManaCalibration: null),
    },
    ActivePresetId: "default",
    HudPosition: new HudPosition(100, 100),
    HudLocked: true,
    LastPartyId: null,
    PollIntervalMs: 700,
    RelayUrl: DefaultRelayUrl,
    RelayFallbackUrl: DefaultRelayFallbackUrl,
    FullscreenDisclaimerDismissed: false,
    HudScale: 1.0);
```

The literal string `"default"` is reserved for the bootstrap preset id. Newly created presets get `Guid.NewGuid().ToString()`.

### 3. Storage format (`config.json`)

After migration, the file looks like:

```json
{
  "Presets": [
    {
      "Id": "default",
      "Name": "Default",
      "Nickname": "BananaBrain",
      "Role": "Utility",
      "HpCalibration": { "Region": { "X": 827, "Y": 928, "W": 290, "H": 22 }, "Direction": "LTR" },
      "StaminaCalibration": null,
      "ManaCalibration": null
    }
  ],
  "ActivePresetId": "default",
  "HudPosition": { "X": 100, "Y": 100 },
  "HudLocked": true,
  "HudScale": 1.0,
  "LastPartyId": null,
  "FullscreenDisclaimerDismissed": false
}
```

`ConfigStore.Save` continues to write atomically via tmp-file + move; it just serialises the new shape. As today, `RelayUrl` / `RelayFallbackUrl` are still blanked before serialisation (the build-time values always win on load).

### 4. Migration in `ConfigStore.Load`

Migration is detected at the JSON layer, not the deserialised-record layer, because the new `AppConfig` no longer has `Nickname` / `Role` / `*Calibration` properties to receive the legacy values.

Steps:

1. `File.ReadAllText(_path)` → JSON string.
2. `JsonNode.Parse(json)` → `JsonObject root`.
3. If `root["Presets"]` is non-null → standard deserialisation path:
   `JsonSerializer.Deserialize<AppConfig>(root, _opts)`.
4. Else (legacy file) → manual extraction:
   - Pull `Nickname`, `Role`, `HpCalibration`, `StaminaCalibration`, `ManaCalibration` from `root` (each may be missing → use the value from `AppConfig.Defaults.Presets[0]`).
   - Build a single `Preset` with `Id = "default"`, `Name = "Default"`, and those values.
   - Legacy `NicknameRegion` / `HudPosition.Monitor` / `CaptureRegion.Monitor` keys in the JSON are silently discarded — `JsonSerializerDefaults.Web` ignores unknown fields, so once the new shape is the target, those properties simply don't bind.
   - Inject `Presets = [defaultPreset]` and `ActivePresetId = "default"` into `root`.
   - Deserialise the patched `root` as `AppConfig`.
5. After deserialisation, **repair invariants**:
   - If `Presets` is null/empty → replace with `AppConfig.Defaults.Presets`.
   - If `ActivePresetId` doesn't match any preset's Id → set it to `Presets[0].Id` and log a warn.
   - If two presets share an Id (shouldn't happen via UI) → regenerate the duplicates' Ids; log a warn.
6. Apply the existing build-time overrides (`RelayUrl`, `RelayFallbackUrl`, `PollIntervalMs`) and `SanitiseHudScale` — unchanged from today.
7. Return the repaired `AppConfig`. The next `Save` writes the new shape, completing the migration.

Failure mode (parse error, IOException, etc.) falls through to the existing "move file aside as `.bad-<ticks>`, return Defaults" branch. Note that `AppConfig.Defaults` now contains the seeded "Default" preset, so a corrupted file recovers to a usable state.

### 5. Helper: `AppConfigExtensions.UpdatePreset`

Most call sites today are `_config with { Nickname = "X" }`. Under the new shape that becomes `_config with { Presets = _config.Presets.Select(p => p.Id == _config.ActivePresetId ? p with { Nickname = "X" } : p).ToList() }` — verbose and error-prone. Introduce one helper:

```csharp
namespace GamePartyHud.Config;

public static class AppConfigExtensions
{
    /// <summary>Returns a new <see cref="AppConfig"/> with <paramref name="mutate"/>
    /// applied to the active preset. Other presets are passed through unchanged.</summary>
    public static AppConfig UpdatePreset(this AppConfig cfg, Func<Preset, Preset> mutate)
    {
        var presets = cfg.Presets
            .Select(p => p.Id == cfg.ActivePresetId ? mutate(p) : p)
            .ToList();
        return cfg with { Presets = presets };
    }
}
```

All `_ctl.UpdateConfig(_ctl.Config with { Nickname = nick })` style sites become `_ctl.UpdateConfig(_ctl.Config.UpdatePreset(p => p with { Nickname = nick }))`.

### 6. UI — Profile header row

Today's MainWindow profile header is a single `TextBlock`. Replace it with a two-column `Grid`:

```xml
<Grid Margin="0,0,0,10">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="*"/>
        <ColumnDefinition Width="Auto"/>
    </Grid.ColumnDefinitions>
    <TextBlock Grid.Column="0"
               Text="Profile" FontSize="18" FontWeight="SemiBold"
               VerticalAlignment="Center"/>
    <ComboBox  Grid.Column="1"
               x:Name="PresetCombo"
               Width="180"
               SelectionChanged="OnPresetChanged"/>
</Grid>
```

`PresetCombo.ItemsSource` is bound to a `ObservableCollection<PresetItemViewModel>` maintained by `MainWindow`. The view model has:
- `Id` (string) — matches `Preset.Id`.
- `Name` (string) — for display + inline edit.
- `IsEditing` (bool) — drives the template swap.
- `IsCommandRow` (bool) — `true` for the synthetic "+ New preset" item.

`PresetCombo.ItemTemplateSelector` returns one of two templates:

- **Preset row template** — 3-column grid: name `TextBlock` (or `TextBox` when `IsEditing`), pencil icon button, × icon button. Icons are hover-revealed via a `Style` on the row container (default `Opacity=0`, `IsMouseOver=True` → `Opacity=1`).
- **Command row template** — single row with `+ New preset` label and a `+` icon, full-width, no per-row actions.

#### 6a. Switching active preset

`OnPresetChanged(object sender, SelectionChangedEventArgs e)`:
1. If the new selection is the command row (`IsCommandRow == true`) → call into the "create" flow (see §6d) and return early without flipping `ActivePresetId`.
2. Otherwise, update `_ctl.UpdateConfig(_ctl.Config with { ActivePresetId = newItem.Id })`.
3. Call `PopulateFromConfig()` — the same routine that already populates Nickname / Role / region chips on startup. It just reads through `cfg.ActivePreset.X` now, so it transparently redraws against the new active preset.
4. Re-evaluate `JoinButton` state (`UpdateJoinButtonState()`), since `ValidateBeforeJoiningParty()` may now refuse if the new preset has no HP calibration.

#### 6b. Renaming (pencil)

Click ✏ → `IsEditing = true` on that row → template swap renders a `TextBox` (autofocused, text selected). Enter or lose-focus → validate (trimmed non-empty, no collision with another preset's Name) → on success, `UpdatePreset(p => p with { Name = newName })` and `Save`; on failure, show a pink border + tooltip "Name already in use" and stay in edit mode. Esc → revert and exit edit mode.

The `IsDropDownOpen=true` flag is held during the edit so the dropdown doesn't collapse on TextBox focus.

#### 6c. Deleting (×)

Click × → `MessageBox.Show("Delete preset '<Name>'?", "Game Party HUD", YesNo, Question)`. On Yes:
1. Remove the preset from `Presets`.
2. If the deleted preset was the active one, set `ActivePresetId` to `Presets[0].Id` (and run the same `PopulateFromConfig()` refresh as a normal switch).
3. `Save`.

The × icon is `IsEnabled=False` when `Presets.Count == 1` (can't delete the last preset). Tooltip on disabled state: "At least one preset is required."

#### 6d. Creating (+ New preset)

Selecting the command row triggers `OnCreatePreset`:
1. Compute next free name: `"New preset"`, `"New preset 2"`, `"New preset 3"`, ...
2. Build a new `Preset` with `Id = Guid.NewGuid().ToString()`, `Name = freeName`, `Nickname = ""`, `Role = Role.Utility`, all calibrations `null`.
3. Append to `Presets`, set `ActivePresetId` to the new id, `Save`.
4. Refresh `PresetCombo.ItemsSource`, select the new row, set its `IsEditing = true` so the user can name it immediately.
5. `PopulateFromConfig()` redraws Profile/Bars with empty values; the existing region-picker flow lets the user calibrate.

If the user clears the inline-edit and commits an empty name → validation rejects, the row stays in edit mode showing the placeholder name from step 1.

### 7. Wiring read sites to `ActivePreset`

A `Grep` for `cfg.Nickname`, `cfg.Role`, `cfg.HpCalibration`, `cfg.StaminaCalibration`, `cfg.ManaCalibration`, and `_config.<same>` enumerates every site that needs to change. Known call-sites (verify exhaustively in the plan):

- `MainWindow.xaml.cs`:
  - `PopulateFromConfig` (reads the five remaining per-character fields).
  - `OnNicknameChanged` → `UpdatePreset(p => p with { Nickname = nick })`.
  - `OnRoleChanged` → `UpdatePreset(p => p with { Role = opt.Role })`.
  - `OnPickRegion` switch-arm building the new config — instead of `_ctl.Config with { HpCalibration = cal, NicknameRegion = null }`, becomes `_ctl.Config.UpdatePreset(p => p with { HpCalibration = cal })`. The `NicknameRegion = null` reset disappears with the field itself (see §0). Stamina/Mana arms similarly switch to `UpdatePreset`.
  - `OnIncludeStaminaUnchecked` / `OnIncludeManaUnchecked` — `UpdatePreset(p => p with { StaminaCalibration = null })`.
  - `ValidateBeforeJoiningParty` — read `_ctl.Config.ActivePreset.HpCalibration` and `.Nickname`.
- `App.xaml.cs`:
  - Startup log line — read through `ActivePreset`.
  - `JoinOrCreateAsync` and anywhere the orchestrator / capture / state are constructed — pass `AppConfig` as today; the consumer reads `cfg.ActivePreset.X` per tick.
- `PartyOrchestrator`, `WindowsScreenCapture`, `HudViewModelSync`, `PartyState`: each consumer that today reads nickname/role/calibrations from `_config` is retargeted to `_config.ActivePreset`. Per-tick reads (not cached at construction) are required for live-switching to work — the plan must verify each consumer re-reads.

**Config propagation note (plan-time concern):** Today `_config` is a field on `App` that gets reassigned via `_config = _config with { ... }`. Any service constructed earlier with a snapshot of the prior `AppConfig` reference (e.g. `new PartyOrchestrator(_config, ...)`) holds stale data after a reassignment. For preset switching to take effect live, the plan must verify how each consumer obtains the current config. The two viable mechanisms are (a) pass a `Func<AppConfig>` getter into the constructor instead of an `AppConfig` snapshot, or (b) introduce a `ConfigChanged` event on `App` that consumers subscribe to and use to refresh their cached reference. The plan should pick one based on which pattern is least invasive given the current code shape and apply it consistently across `PartyOrchestrator`, the capture loop, and any other long-lived consumer. If today's code already relies on `App` holding the field and passing it through fresh on each invocation (rather than caching), no change is needed for that consumer.

### 8. Mid-party switching behaviour

Because the broadcast loop reads nickname / role / HP-percent / etc. on every tick, switching the active preset takes effect automatically on the next tick (≤ `PollIntervalMs` ≈ 700 ms by default). Teammates see the new nickname/role glyph; the next bar values come from the new HP-region calibration.

No special "republish identity" code path is added. The plan must confirm by inspection that:
- `PartyOrchestrator`'s broadcast tick reads through `_config.ActivePreset` (not a captured local).
- `WindowsScreenCapture` reads `HpCalibration` / `StaminaCalibration` / `ManaCalibration` from `_config.ActivePreset` on each capture cycle.

If any consumer does cache at construction, the plan will refactor it to re-read per tick. The capture loop today already pulls calibrations per frame (a calibration change while running takes effect immediately), so the change is mostly a search-and-replace plus a re-target of the indirection.

### 9. Testing

Per `CLAUDE.md`, UI surfaces (the new combobox dropdown + inline rename/delete) are manually verified. Pure-logic surface gets unit tests:

- `ConfigStore.Load`:
  - Legacy format (no `Presets` key) → produces one preset named `Default` with `Id = "default"`, populated from the legacy top-level fields. `ActivePresetId == "default"`.
  - New format (`Presets` present, valid) → round-trips unchanged.
  - Corrupted: `Presets` present but empty → repaired to `AppConfig.Defaults.Presets`. `ActivePresetId` repaired to match.
  - Corrupted: `ActivePresetId` doesn't match any preset → repaired to first preset's id; warning logged.
  - Corrupted: two presets share an Id → duplicates re-Id'd; warning logged.
- `AppConfigExtensions.UpdatePreset`:
  - Mutates only the active preset; non-active presets pass through with referential equality.
  - Returned `AppConfig` is a new instance (record `with` semantics).
- `AppConfig.ActivePreset`:
  - Returns matching preset for valid `ActivePresetId`.
  - Returns `Presets[0]` and logs warn when `ActivePresetId` is stale (test the warn via the existing Log capture pattern, if present; else just assert the fallback preset).

Tests that today construct an `AppConfig` directly (with `Nickname:`, `Role:`, `HpCalibration:` named args) need updating to use the new shape (one preset embedded in `Presets`). The `ConfigStoreTests.cs:51` fixture that sets `NicknameRegion` drops that line entirely (the field no longer exists per §0). Any test constructing `CaptureRegion` with the `Monitor:` named arg drops it likewise. This is mechanical; plan should enumerate the test files touched.

A specific migration test asserts the legacy keys are tolerated and discarded: feed a JSON string containing the pre-cleanup shape (with top-level `Nickname`, `Role`, `HpCalibration`, `NicknameRegion`, and `HudPosition.Monitor`) into `ConfigStore.Load`, then assert the loaded `AppConfig` has the values migrated into a `Default` preset and no exception was raised. This guards against accidental regression where strict deserialisation might throw on unknown keys.

### 10. Manual verification

Before claiming done, run the app and verify:

1. Fresh install (`config.json` does not exist) → app starts on the seeded `Default` preset; Profile heading shows `[Default ▾]` on the right.
2. Existing install (pre-migration `config.json` present) → app starts; the dropdown shows `[Default ▾]` and the Nickname / Role / region chips are populated from the migrated values. Inspecting `config.json` after first save shows the new shape: Presets array + ActivePresetId, no top-level Nickname / Role / calibrations, **no `NicknameRegion` anywhere**, **no `Monitor` field in `HudPosition`**, **no `Monitor` field on any `CaptureRegion`** under a calibration.
3. `+ New preset` → creates an empty preset, switches to it, opens inline rename. Profile/Bars now show empty values; `Pick HP bar region` chip is "Not set yet."
4. Pick HP/Stamina/Mana region under the new preset → chips populate; switching back to `Default` shows the original chips; switching forward again shows the new preset's chips.
5. Pencil rename → enters edit mode; commit on Enter / blur; revert on Esc; collision shows error tooltip.
6. × delete on a non-active preset → confirm dialog; removes it; dropdown updates.
7. × delete on the active preset (with ≥2 presets) → confirms; deletes; auto-switches to first remaining; UI refreshes.
8. × delete when only one preset remains → button is disabled; tooltip explains why.
9. Join party with one preset → leave → switch to a different preset → Join button correctly evaluates the new preset's HP-calibration / Nickname state for `ValidateBeforeJoiningParty`.
10. Mid-party switch: join party with preset A → switch to preset B → within ~1 s the teammate(s) see the new nickname/role/bar values reflect preset B.
11. Quit and relaunch → app reopens on the previously-active preset; LastPartyId is still prepopulated regardless of preset.
12. Manually corrupt `ActivePresetId` in `config.json` to a non-existent id → app starts, falls back to first preset, repairs the field on next save.
