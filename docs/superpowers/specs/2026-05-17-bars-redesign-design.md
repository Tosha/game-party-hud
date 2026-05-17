# Bars section redesign

**Date:** 2026-05-17
**Scope:** `src/GamePartyHud/MainWindow.xaml(.cs)` Bars block, plus a new `Bars/` folder (BarCard control, BarPreviewSource service, BarRegionValidator pure-logic surface). One Preset-record additive change (two bool flags). One read-site update in PartyOrchestrator.

## Goals

Replace the current Bars section — three differently-indented rows of "Pick" buttons and wordy "Saved 290×22 at (827, 928)." chips — with a uniform stack of three cards (HP, Stamina, Mana), each carrying a **live preview** of the captured region and a **validation status icon with a guidance tooltip**. Specifically:

1. **Uniform alignment.** All three cards have the same left edge, same width, same internal structure. The current HP-vs-Stamina/Mana indent mismatch goes away.
2. **Less verbose status.** Remove the wide chip that today reads "✓ Saved 290×22 at (827, 928)." The dimensions move to the status icon's tooltip; the chip itself is gone.
3. **Live preview.** Each card shows a thumbnail of the captured region, refreshed at ~3 Hz while the settings window is visible. Lets the user verify their pick visually and catch UI moves immediately.
4. **Validation with guidance.** A green / yellow / red status icon next to each preview, with a tooltip explaining the issue and how to fix it (too tall, no colored pixels, narrow box, bar wasn't full at pick time, fragmented fill). Soft guidance only — never blocks save.
5. **Non-destructive enable toggle.** Stamina and Mana cards toggle via a header switch that preserves the saved calibration when disabled (vs. today's `Include` checkbox which clears the calibration on uncheck).

## Non-goals

- Vision-based "auto-detect bar region" — user still drags a box.
- Numeric fill-% overlay drawn on top of the preview thumbnail. The tooltip carries the number; on-image text adds visual noise and complicates rendering.
- Per-card preview pause toggle. The window-visibility check (preview pauses when settings is hidden) covers the only realistic need.
- Animated transitions on enable / disable toggling. Static visibility flip.
- New validator rules beyond the seven listed below. Thresholds tunable later if they prove noisy.
- Any change to capture pipeline, networking, party state, HUD overlay, or the preset selector.
- Per-bar HUD position / scale customisation.
- Touching `RegionSelectorWindow` (the drag picker overlay).

## Reference

User feedback summarised from chat: *"rework Bars section. Currently it is quite messy and misaligned. we also have too big messages saying what was saved. design a better approach that would also show live view what player has selected and validation mechanism with tooltips to guide players to select region more precisely"*.

Brainstorming decisions:
- Layout: three uniform stacked cards (HP / Stamina / Mana), same width.
- Preview cadence: auto-update at ~3 Hz while the settings window is visible; pauses entirely when hidden.
- Validation policy: soft guidance only — icon + tooltip, never blocks save.
- Disable behavior: card stays visible; toggle off preserves the saved calibration.

## Design

### 1. Card layout (uniform across the three bars)

Each card is a `Border` (1px rounded, subtle inset background) hosting a 2-row Grid:

```
┌─ HP ────────────────────────────────────────────────────────────────┐
│ ┌──────────────────────────────────────────┐    ●  [ Re-pick ]      │
│ │  [live preview thumbnail of saved region]│                        │
│ └──────────────────────────────────────────┘                        │
└─────────────────────────────────────────────────────────────────────┘

┌─ Stamina                              [✓ Enabled  (toggle switch) ] ┐
│ ┌──────────────────────────────────────────┐    ●  [ Re-pick ]      │
│ │  [live preview thumbnail]                │                        │
│ └──────────────────────────────────────────┘                        │
└─────────────────────────────────────────────────────────────────────┘

┌─ Mana                                 [○ Disabled ────────────────] ┐
│ ┌──── (greyed) ────────────────────────────┐    ●                   │
│ │  Not enabled. Toggle on to broadcast.    │ [Pick mana bar region] │
│ └──────────────────────────────────────────┘                        │
└─────────────────────────────────────────────────────────────────────┘
```

**Card structure:**

- **Header row** (`Auto` height, ~24px): bar name on the left (`FontSize=13`, `SemiBold`), `Wpf.Ui.Controls.ToggleSwitch` on the right. HP card has **no** toggle (always-on; required). Stamina / Mana cards render the toggle.
- **Content row** (`Grid`, columns: `*` + `Auto`):
  - **Left column** — `Border` framing the live-preview `Image`. Fixed `Height=28` (close to a real bar's pixel height; matches the visual scale users see in-game). Width fills the remaining card width. Two visual states:
    - **No region yet:** centered hint text `"Pick a region to see a live preview"` (Opacity 0.5).
    - **Disabled (Stamina/Mana toggle off):** Opacity 0.4 + centered text `"Not enabled. Toggle on to broadcast."`.
    - **Live:** the `Image` element shows the captured `WriteableBitmap`, stretched to fit the 28px height (so a 290×22 capture renders as a wide thin strip just like in-game).
  - **Right column** — `Ellipse` status indicator (`Width=12 Height=12`, colored per validation level) + `ui:Button`. Status icon's `ToolTip` carries the full validation message + measured dimensions/fill.

**Button label & appearance:**
- No saved region → primary appearance, text `"Pick HP bar region"` / `"Pick stamina bar region"` / `"Pick mana bar region"`.
- Saved region present → secondary appearance, text `"Re-pick"`.

**Card width:** stretches to fill the left column. Same left edge as the Profile section above. Right edge aligned via the existing `Grid.IsSharedSizeScope` flow from the preset PR — but since the cards aren't sharing column widths with the Profile grid, this is just "card width = StackPanel content width". 8px gap between cards.

**HP card specifics:** no `ToggleSwitch` rendered. Same Border / header / content structure otherwise, so the cards visually align top-to-bottom.

### 2. Live preview

A new service `Bars/BarPreviewSource` owns one capture timer per bar.

```csharp
public sealed class BarPreviewSource : IDisposable
{
    public BarPreviewSource(
        IScreenCapture capture,
        Func<BarCalibration?> getCalibration,    // lets the source see preset switches live
        Action<WriteableBitmap, ValidationResult> onUpdate);

    public void Start();          // idempotent
    public void Stop();           // idempotent
    public void Dispose();        // stops + releases the bitmap
}
```

Internally:
- `DispatcherTimer` at 333 ms (~3 Hz).
- Each tick: call `getCalibration()`. If null, do nothing. Otherwise, `await IScreenCapture.CaptureBgraAsync(cal.Region)`, copy the bytes into a reused `WriteableBitmap` (allocated lazily on first non-null tick, sized to the region), call `BarRegionValidator.Validate(...)`, fire `onUpdate(bitmap, result)`.
- Reusing the same `WriteableBitmap` avoids per-tick allocations. If the region's W/H changes (e.g. after a re-pick), the bitmap is freed and re-allocated at the new size.

Lifecycle (driven by `MainWindow`):
- `Start()` when the card's saved calibration is non-null AND the card's `IsEnabled` is true AND the settings window is visible.
- `Stop()` when any of those becomes false.
- `Dispose()` when `MainWindow` closes.

Window-visibility wiring: `MainWindow` subscribes to its own `IsVisibleChanged`. On hide, stop all three sources; on show, restart any that should be running.

CPU/RAM cost: each region is small (a typical 300×22 BGRA is ~26 KB), captured 3×/s while settings is open. Negligible. The settings window isn't open during the user's actual gaming session, so this never competes with the `<1% CPU during gameplay` budget in `CLAUDE.md`.

### 3. Validation

Pure-logic, fully testable:

```csharp
namespace GamePartyHud.Bars;

public enum ValidationLevel { Ok, Warning, Error }

public sealed record ValidationResult(ValidationLevel Level, string Message);

public static class BarRegionValidator
{
    public static ValidationResult Validate(
        CaptureRegion region,
        ReadOnlySpan<byte> bgra,
        bool isPickTime);
}
```

Rules, applied in order — **first match wins** so the most actionable issue is what the user sees:

| # | Level | Trigger | Tooltip |
|---|---|---|---|
| 1 | **Error** | `region.W <= 0` or `region.H <= 0` | "Region is empty. Click Re-pick and drag a box around the bar." |
| 2 | **Error** | Zero columns classified as "filled" by the existing `BarAnalyzer` saturation/run-length test | "No colored bar pixels detected. Try dragging a tight box around the colored bar itself — not the background or frame." |
| 3 | **Warning** | `region.H > 30` or `region.H > region.W / 5` | "Box looks too tall ({H}px tall vs {W}px wide). Try dragging just the bar's height — no frame above or below." |
| 4 | **Warning** | `region.W < 60` | "Box looks narrow ({W}px). For best accuracy, drag across the full visible width of the bar." |
| 5 | **Warning** | `isPickTime && detectedFill < 0.85` | "Bar was only {fill}% full when picked. For best calibration, have HP / Stamina / Mana at maximum before clicking Pick." |
| 6 | **Warning** | Filled-column array contains more than one contiguous run of `filled` (a normal bar shows ONE run of filled followed by ONE run of empty; multiple filled runs separated by empty gaps means we caught a discontinuity — adjacent bar or icon overlay) | "The captured pixels don't look like one continuous bar. Did the box catch part of an adjacent bar or icon?" |
| 7 | **Ok** | All checks pass | "Looks good. Detected {W}×{H} at ({X}, {Y}), {fill}% fill." |

Notes:
- The "fill < 0.85" check (rule 5) fires only when `isPickTime == true` (set by `MainWindow.OnPickRegion` after a fresh user pick). Live preview ticks pass `isPickTime: false`, so a user playing at 60% HP doesn't get repeatedly nagged. The "low fill at pick" warning is **cached** on a side field per card (in-memory, not persisted), and the card uses it as the displayed result whenever it's set; otherwise it uses the live result. Re-pick clears the cache and re-evaluates.
- Rules 3 and 4 are based on `region` alone and so produce the same result every tick — fine to recompute each time.
- Rule 6 (fragmentation) reuses the column-classification pass from `BarAnalyzer` but inspects the run pattern instead of just transition position. To avoid duplicating the saturation/run-length logic, the validator either (a) calls a new helper `BarAnalyzer.ClassifyColumns(bgra, w, h)` that returns the `bool[] columnFilled` array, or (b) inlines a small re-implementation. Implementation chooses (a) to keep the saturation threshold central.

Status icon color per level:
- `Ok` → green (`#FF4CAF50`)
- `Warning` → amber (`#FFFFC107`)
- `Error` → red (`#FFE53935`)

### 4. Card control (`BarCard`)

New `UserControl` at `src/GamePartyHud/Bars/BarCard.xaml(.cs)`.

Dependency properties (back the bindings the parent sets):

```csharp
public string BarName { get; set; }              // "HP" | "Stamina" | "Mana"
public bool IsToggleable { get; set; }           // true for Stamina/Mana, false for HP
public bool IsEnabled { get; set; }              // two-way; bound to the ToggleSwitch
public BarCalibration? Calibration { get; set; } // updates trigger preview lifecycle
```

Routed events:

```csharp
public event EventHandler? PickRequested;        // user clicked Pick / Re-pick
public event EventHandler? EnabledChanged;       // user flipped the toggle
```

Internal:
- Holds a `BarPreviewSource` instance, wired to `getCalibration: () => Calibration`.
- `IsEnabled` change → `Start()` / `Stop()` the source.
- `Calibration` change (e.g. preset switch reaching us via DP update) → `Stop()` + `Start()` if new calibration is non-null, else `Stop()`.
- Receives the `(bitmap, validationResult)` callback; updates the `Image.Source` and the `Ellipse` brush + tooltip.

`MainWindow` hosts three named cards: `<local:BarCard x:Name="HpCard" BarName="HP" IsToggleable="False"/>`, similarly for Stamina and Mana with `IsToggleable="True"`.

### 5. `Preset` flag additions for the enable toggle

The toggle controls a behavior that must reach the orchestrator's broadcast layer. Two options considered:

**A.** Add `bool StaminaEnabled` and `bool ManaEnabled` to the `Preset` record (default `true`). The toggle writes them via `UpdatePreset(p => p with { StaminaEnabled = false })`. The orchestrator reads `ap.GetEffectiveStaminaCalibration()` (extension method that returns `cal` if enabled, else `null`).

**B.** Have the toggle clear/restore the calibration field directly. Disable → cache the calibration in MainWindow's memory, set `StaminaCalibration = null`. Enable → restore. Doesn't change `Preset` shape, but the "memory" of the cached calibration is volatile (lost on app restart) — defeats the user-stated requirement that toggle off preserves calibration.

**Picking A.** Two new bool fields with default `true` (so existing presets deserialize correctly without migration). `JsonSerializerDefaults.Web` handles missing keys via default values. Round-tripping is automatic.

New extension on `Preset` (in `AppConfigExtensions.cs` to keep all preset helpers together):

```csharp
public static BarCalibration? EffectiveStaminaCalibration(this Preset p) =>
    p.StaminaEnabled ? p.StaminaCalibration : null;

public static BarCalibration? EffectiveManaCalibration(this Preset p) =>
    p.ManaEnabled ? p.ManaCalibration : null;
```

`PartyOrchestrator`'s broadcast tick (currently reads `ap.StaminaCalibration` and `ap.ManaCalibration`) switches to `ap.EffectiveStaminaCalibration()` and `ap.EffectiveManaCalibration()`. HP has no toggle, so its read stays `ap.HpCalibration`.

### 6. Removed surface

From `MainWindow.xaml.cs`:
- `OnIncludeStaminaChecked`, `OnIncludeStaminaUnchecked`, `OnIncludeManaChecked`, `OnIncludeManaUnchecked` — gone. The new `BarCard` raises `EnabledChanged` which the window forwards into `UpdatePreset(p => p with { StaminaEnabled = newValue })`.
- `SetRegionStatus` method and the `RegionStatusState` enum — gone. Status display moves into `BarCard` / `ValidationResult`.
- `RegionStatusChip`, `RegionStatusIcon`, `RegionStatus` and the `StaminaStatusChip` / `ManaStatusChip` field references — gone.

From `MainWindow.xaml`:
- The entire current Bars block (from the `Separator` after the Profile section down to the bottom of `ManaPickRow`'s `StackPanel`) — replaced with three `<local:BarCard>` instances.

`PopulateFromConfig` reduces to: for each bar, set the corresponding `BarCard.Calibration` from `ap.XCalibration` and the `BarCard.IsEnabled` from `ap.XEnabled` (always true for HP).

### 7. Testing

Per `CLAUDE.md`, UI surfaces (cards, preview Image, ToggleSwitch) are manually verified. The new pure-logic surface gets unit tests.

- `tests/GamePartyHud.Tests/Bars/BarRegionValidatorTests.cs`:
  - One test per rule (`Validate_EmptyRegion_ReturnsError`, `Validate_NoSaturatedPixels_ReturnsError`, `Validate_TooTall_ReturnsWarning`, `Validate_TooNarrow_ReturnsWarning`, `Validate_LowFillAtPickTime_ReturnsWarning`, `Validate_LowFillNotAtPickTime_NoWarning`, `Validate_FragmentedFill_ReturnsWarning`, `Validate_AllPass_ReturnsOk`).
  - Fixtures use the existing `SyntheticBitmap.HorizontalBar(...)` helper in `tests/GamePartyHud.Tests/Capture/`.
- Existing tests that construct `Preset` records need the new `StaminaEnabled`/`ManaEnabled` parameters with defaults `true`. Mechanical update to 4–5 sites in `AppConfigExtensionsTests.cs` and `ConfigStoreTests.cs`.

### 8. Files

**Created:**
- `src/GamePartyHud/Bars/BarPreviewSource.cs` — capture timer + bitmap reuse.
- `src/GamePartyHud/Bars/BarRegionValidator.cs` — pure-logic validation (+ `ValidationLevel`, `ValidationResult`).
- `src/GamePartyHud/Bars/BarCard.xaml` + `.xaml.cs` — the per-bar UserControl.
- `tests/GamePartyHud.Tests/Bars/BarRegionValidatorTests.cs`.

**Modified:**
- `src/GamePartyHud/MainWindow.xaml` — replace Bars block with three `BarCard`s.
- `src/GamePartyHud/MainWindow.xaml.cs` — drop old handlers/state; wire up cards' events; manage preview lifecycle on `IsVisibleChanged`.
- `src/GamePartyHud/Config/Preset.cs` — add `bool StaminaEnabled = true, bool ManaEnabled = true`.
- `src/GamePartyHud/Config/AppConfigExtensions.cs` — add the two effective-calibration extension methods.
- `src/GamePartyHud/Party/PartyOrchestrator.cs` — switch the two reads to the effective-calibration extensions.
- `src/GamePartyHud/Capture/BarAnalyzer.cs` — extract the column-classification pass into a public helper `ClassifyColumns(bgra, w, h, cal) -> bool[]` so the validator can call it without duplicating saturation logic. The existing `Analyze` method is rewritten to call the helper internally; behavior unchanged (covered by existing `BarAnalyzerTests` and `SampleImageRegressionTests`).
- `tests/GamePartyHud.Tests/Config/AppConfigExtensionsTests.cs` and `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs` — update `Preset` constructions for new fields.

### 9. Manual verification

Before claiming done, run the app and verify:

1. Fresh install — Bars section shows three cards (HP, Stamina, Mana), all same width / left edge / structure. Stamina and Mana cards show a "Disabled" toggle and the greyed-out body message.
2. Pick HP region with a tight box around a full HP bar → green status icon, hover tooltip says "Looks good. Detected …×…, ~100% fill." Live preview shows the actual screen pixels at ~3 fps.
3. Drag the in-game window so the bar moves out from under the picked region → preview now shows whatever's underneath (probably background) → status icon flips to red ("No colored bar pixels detected…"). Move the game window back → icon flips green again.
4. Pick a region that's much too tall (capture HP + some frame below) → yellow status icon, tooltip says "Box looks too tall…".
5. Pick a region while HP is at ~50% → yellow status icon, tooltip says "Bar was only 50% full when picked…". Play and let HP drop further; tooltip stays at the cached pick-time message until you re-pick.
6. Toggle Stamina on → preview starts; toggle off → preview pauses (visible greyed-out body), calibration **preserved** (toggle back on → preview restarts at the same region; no re-pick needed).
7. Close the settings window to tray → CPU briefly drops (preview captures stop). Re-open → preview captures resume.
8. Switch to a different preset that has different calibrations → all three cards refresh to the new calibrations, previews retarget to the new regions.
9. In-party with a teammate: toggle Stamina off in the active preset → teammate's HUD stops showing the stamina stripe within ~1 broadcast tick (the orchestrator now broadcasts `null` for stamina). Toggle back on → it returns.
10. `dotnet test` — 161 existing tests pass plus the new `BarRegionValidatorTests` (one per rule + happy path = ~8 tests).
