# Bar detection — missing-pixel redesign

**Status:** Approved (brainstorming complete)
**Date:** 2026-05-08
**Author:** Anton Zemskov

---

## TL;DR

Replace the bar analyzer's "saturated red pixel" classifier with a colour-agnostic "desaturated grey pixel" classifier. The bar's *missing* portion (the dark grey background plus a slightly lighter grey end-cap, both visible after the game UI update) becomes the load-bearing signal; current HP is computed as `1 - missingFraction`.

The same algorithm also works for stamina and mana bars (which leave the same grey behind when they decrease). This change is HP-only at runtime — Stamina/Mana detection is a separate future change — but the renamed and color-agnostic types (`BarAnalyzer`, `BarCalibration`, etc.) make adding them later a small, additive change.

`HpCalibration.FullColor` and `HsvTolerance` are deleted: dead code today, definitively unused after the redesign.

---

## Why now

The most recent in-game UI update altered the rendered shape of the HP bar. The existing real-image regression samples (`HP_BAR_4_PER_CENT.png` … `HP_BAR_89_PER_CENT.png`) no longer match what the game produces; they have been replaced in `tests/GamePartyHud.Tests/Capture/HpBarExamples/` with a new set at percentages `5, 11, 18, 26, 33, 42, 50, 62, 73, 84, 90, 100`. The current `SampleImageRegressionTests` references the old filenames and will fail until updated regardless.

Independent of the UI change, the user wants stamina and mana bars in a future release. The current per-pixel classifier is hard-coded to a red hue window and would need three parallel hue tests to support all three bars. Inverting the question to "is this pixel grey?" gives one classifier that works for all three.

---

## Non-goals

- Stamina or mana bar detection at runtime (config schema, capture, UI, message protocol). The algorithm is generalised; runtime wiring is a separate future change.
- Calibration auto-detection (finding the bar without the user dragging a region).
- Multi-resolution scaling logic. The analyzer remains agnostic to bar dimensions; nothing here changes that.
- Smoothing/filtering changes. `HpSmoother` is renamed to `BarSmoother` but its behaviour is unchanged.
- Capture backend changes. The WGC capture engine introduced in the 2026-05-01 design is unchanged.

---

## 1. Algorithm

### 1.1 Per-pixel classifier

```text
IsMissingPixel(hsv) :=
       S ≤ MissingMaxSaturation   (default 0.20)
    && V ≥ MissingMinValue        (default 0.05)
    && V ≤ MissingMaxValue        (default 0.70)
```

Why each bound:

- **`S ≤ 0.20`** — both the dark-grey "empty bar" body and the light-grey end-cap are nearly desaturated. Saturated bar fill (red, blue, green) fails this test.
- **`V ≥ 0.05`** — pure black (the frame border around the bar) is excluded. Without this bound, frame rows near the top and bottom would be misclassified as missing.
- **`V ≤ 0.70`** — bright text glyphs (the "246/246" overlay rendered near-pure-white, V ≈ 0.95) are excluded. Without this bound, every column intersected by text would tip toward "missing".

The starting threshold values are educated guesses. They will be tuned against the real-image diagnostic test (Section 5.3) until per-sample error stays within ±3% and mean absolute error stays under 1%.

### 1.2 Per-column aggregation

For each column `x` in `[0, width)`:

1. Count rows `y` for which `IsMissingPixel(hsv[x, y])` is true.
2. Classify the column as "missing" if the count is at least `max(2, height / 5)` — the same ≥20% rule used by the previous classifier, mirrored. The ≥20% threshold (rather than ≥50%) is intentional: a column whose middle rows are obscured by white text glyphs still reads correctly because the unobscured top and bottom rows alone clear the bar.

### 1.3 Transition detection

Identical in shape to the existing `HpBarAnalyzer.Analyze` pass logic, with inverted state names:

1. Start at the **anchor** side — left for `FillDirection.LTR`, right for `FillDirection.RTL`.
2. Find the first run of `StableRun = 3` consecutive columns with the same classification. That establishes the *stable initial state* — typically `not missing` if any HP is left, `missing` only when the bar is entirely drained.
3. From the end of the stable run, scan toward the opposite side for the first run of 3 consecutive opposite-state columns. The first column of that run is the *transition*.
4. Compute:
   - `filledFraction = transition / width` for LTR.
   - `filledFraction = 1 - (transition / width)` for RTL.
5. **Edge cases:**
   - No column is `missing` anywhere → bar is full → return `1.0`.
   - Every column is `missing` → bar is empty → return `0.0`.
   - No stable initial state can be established (width too small or pixels too noisy) → return `0.0` (matches existing behaviour for the analogous failure mode).

### 1.4 Why per-column, not per-pixel

The bar is rendered with a vertical gradient inside the fill (top and bottom rows are slightly darker than the middle), and overlay text occupies roughly the middle 40–60% of rows. Per-pixel classification of the boundary is brittle. The existing analyzer's per-column tally + stable-run logic has been validated against the previous game's image set and absorbs both the gradient and the text overlay correctly. The redesign keeps that structure and only flips the inner predicate.

---

## 2. Types and config schema

### 2.1 Renames

| Before | After |
|---|---|
| `Capture/HpBarAnalyzer.cs` (class `HpBarAnalyzer`) | `Capture/BarAnalyzer.cs` (class `BarAnalyzer`) |
| `Capture/HpBarDetector.cs` (class `HpBarDetector`) | `Capture/BarDetector.cs` (class `BarDetector`) |
| `Capture/HpCalibration.cs` (record `HpCalibration`) | `Capture/BarCalibration.cs` (record `BarCalibration`) |
| `Capture/HpRegion.cs` (record `HpRegion`) | `Capture/CaptureRegion.cs` (record `CaptureRegion`) |
| `Capture/HpSmoother.cs` (class `HpSmoother`) | `Capture/BarSmoother.cs` (class `BarSmoother`) |
| `HpBarAnalyzer.IsFilledPixel` | `BarAnalyzer.IsMissingPixel` |
| `FilledMinSaturation` / `FilledMinValue` / `FilledHueHalfWindow` | `MissingMaxSaturation` / `MissingMinValue` / `MissingMaxValue` |

Test files mirror the source renames:

| Before | After |
|---|---|
| `tests/.../Capture/HpBarAnalyzerTests.cs` | `BarAnalyzerTests.cs` |
| `tests/.../Capture/HpBarDetectorTests.cs` | `BarDetectorTests.cs` |
| `tests/.../Capture/HpSmootherTests.cs` | `BarSmootherTests.cs` |

The `HpBarExamples/` folder name is unchanged — it specifically holds HP samples; future stamina/mana sample folders will be named per bar type.

### 2.2 Deletions

- `Capture/HsvTolerance.cs` is deleted. It has no callers after `BarCalibration` drops the `Tolerance` field.

### 2.3 New `BarCalibration` shape

```csharp
public sealed record BarCalibration(
    CaptureRegion Region,
    FillDirection Direction);
```

`FullColor` and `Tolerance` are removed. The capture region and the fill direction are sufficient input for the colour-agnostic analyzer.

### 2.4 `AppConfig` change

Field types change; field names and JSON keys do not:

```csharp
public sealed record AppConfig(
    BarCalibration? HpCalibration,
    CaptureRegion? NicknameRegion,
    string Nickname,
    Role Role,
    /* ...remaining fields unchanged... */
);
```

The field name `HpCalibration` stays — it identifies *which* bar this calibration is for. Once stamina/mana arrive, sibling fields `StaminaCalibration` and `ManaCalibration` will be added of the same `BarCalibration` type.

### 2.5 Config migration

No migration code. `System.Text.Json`'s default behaviour is to ignore unknown JSON properties on read, so:

- An old config containing `"HpCalibration": { "Region": ..., "FullColor": ..., "Tolerance": ..., "Direction": ... }` deserializes into the new `BarCalibration` record. The `FullColor` and `Tolerance` keys are silently dropped.
- The first save after upgrade re-serializes without those keys.

The `ConfigStoreTests` round-trip test gains a case asserting that a stored old-shape JSON deserializes successfully and re-serializes in the new shape.

---

## 3. Calibration wizard

### 3.1 Removed step

`MainWindow.xaml.cs` (around lines 188–199) currently captures pixels and calls a private `SampleFullColor` helper to compute the bar's average colour. That step is removed. The wizard's new flow is:

1. User drags a rectangle around the HP bar in `RegionSelectorWindow`.
2. The selection is converted to screen pixels (existing logic in `RegionSelectorWindow.OnUp`).
3. `BarCalibration(region, FillDirection.LTR)` is constructed and saved to config.

### 3.2 No pixel read at calibration time

The `await _ctl.Capture.CaptureBgraAsync(region)` call in the wizard is removed. Calibration becomes purely geometric — there is no longer a "the bar must be at full HP at calibration time" implicit requirement, because no pixels are sampled. (The user still sees the bar while dragging, so they will naturally calibrate at whatever HP level they happen to have.)

### 3.3 Removed helper

The private `SampleFullColor` helper inside `MainWindow.xaml.cs` is deleted.

### 3.4 Direction stays hardcoded

`FillDirection.LTR` is the only direction the wizard produces. All known supported games drain HP rightward. The analyzer keeps RTL support so a config-side tweak is enough if a future game inverts; no UI for direction selection is added in this change.

---

## 4. Other call sites

### 4.1 `Party/PartyOrchestrator.cs`

- `HpBarAnalyzer _analyzer` → `BarAnalyzer _analyzer` (field type rename).
- `LogTick` (lines ~186–207) — its inner column-scan calls `HpBarAnalyzer.IsFilledPixel` to bin columns into `empty`/`partial`/`full`. The bins are renamed `missing`/`partial`/`bar` and the call switches to `BarAnalyzer.IsMissingPixel`. Diagnostic intent is unchanged: per-tick "is the bar reading sane?" log output.

### 4.2 `App.xaml.cs`

The `Log.Info` line at 110 references `_config.HpCalibration` for null-checking. The field name is unchanged, so no edit is required.

### 4.3 `MainWindow.xaml.cs` (other touch points)

- Line 99–101: nullable check on `cfg.HpCalibration` for the saved-region status banner — field name unchanged, no edit.
- Line 391: `if (_ctl.Config.HpCalibration is null)` validation gate before joining a party — field name unchanged, no edit.

### 4.4 `Capture/IScreenCapture.cs` and `Capture/WindowsScreenCapture.cs`

Pure type-name update: any `HpRegion` parameter or field becomes `CaptureRegion`. No behavioural change.

### 4.5 `Calibration/RegionSelectorWindow.xaml.cs`

`HpRegion? Result` becomes `CaptureRegion? Result`. The construction at line 71 updates accordingly.

### 4.6 `Config/AppConfig.cs`

Type-name updates only. The `using GamePartyHud.Capture;` import stays.

---

## 5. Testing

### 5.1 New unit tests for `IsMissingPixel`

Added either to `BarAnalyzerTests.cs` or as a separate file. Asserts the S/V boundary cases at corners of the criterion:

| Pixel BGR | HSV (approx) | Expected |
|---|---|---|
| `(0, 0, 0)` (black frame) | S=0, V=0 | not missing |
| `(40, 40, 40)` (dark grey empty) | S=0, V≈0.16 | missing |
| `(160, 160, 160)` (light grey end-cap) | S=0, V≈0.63 | missing |
| `(245, 245, 245)` (white text) | S≈0.04, V≈0.96 | not missing |
| `(0, 0, 220)` (saturated red bar) | S=1, V≈0.86 | not missing |
| `(80, 40, 40)` (anti-alias blend) | S≈0.5, V≈0.31 | not missing |

### 5.2 Synthetic-bitmap tests in `BarAnalyzerTests`

The existing `HpBarAnalyzerTests.cs` cases port over with the renamed type and inverted semantics:

- `Analyze_FullBar_Returns1`, `Analyze_EmptyBar_Returns0`, `Analyze_PartialBar_WithinTwoPercent` — fixture uses red `(0, 0, 255)` fill on `(40, 40, 40)` empty. The empty colour is missing under the new criterion (S=0, V≈0.16). Tests pass without altering the fixture.
- `Analyze_Rtl_InvertsReading` — same logic, inverted direction. Passes after rename.
- `Analyze_NoMatchingPixels_Returns0` — uses an all-`(40, 40, 40)` buffer. Under the new semantics, every column is missing → return 0. Test passes; assertion unchanged.
- `Analyze_FullBarWithTextOverlay_ReturnsFull` — the text overlay is `(240, 240, 240)`, V≈0.94, excluded by the V upper bound. Passes.
- `Analyze_Clamps_PercentToClosedUnit` — passes after rename.
- `Analyze_IsIndependentOfCalibratedFullColor` — **deleted**. Its purpose was to guard against a regression where a bogus `FullColor` polluted the analyzer; with `FullColor` removed, the failure mode no longer exists.

### 5.3 Real-image regression tests

`SampleImageRegressionTests.cs` and `SampleImageDiagnosticTests.cs` are updated:

- The sample table replaces filenames and expected percentages: `5, 11, 18, 26, 33, 42, 50, 62, 73, 84, 90, 100`.
- The `CalibrateFromFullSample` helper is removed. Tests construct `new BarCalibration(new CaptureRegion(0, 0, 0, w, h), FillDirection.LTR)` directly.
- Per-sample tolerance stays at ±3%. MAE assertion stays at < 1%.
- The diagnostic test's `Calibrated fullColor` log lines and `SampleFullColor` helper are removed.

### 5.4 Tuning gate

After the algorithm is implemented but before tests are asserted as passing, the diagnostic test (`CurrentAnalyzer_AgainstAllSamples_ShowsError`, renamed) prints per-sample observed-vs-expected. Threshold values are tuned by adjusting `MissingMaxSaturation` and `MissingMaxValue` until both the per-sample ±3% and the MAE < 1% targets are met. The default starting values (0.20 / 0.05 / 0.70) are not load-bearing in this spec.

### 5.5 `ConfigStoreTests`

A new test case asserts that a JSON document containing the old-shape `HpCalibration` (with `FullColor` and `Tolerance` keys) deserializes successfully into the new `BarCalibration` record, and that a re-save drops the old keys. This documents the migration behaviour and prevents a silent regression if a future serializer-options change made unknown-property handling stricter.

### 5.6 Manual verification

Before merging, run the app once against a live game session, watch HP track from 100% down to ~5% and back, and confirm no glitches or drift. Required because per [CLAUDE.md](../../../CLAUDE.md), UI/runtime correctness is manual.

---

## 6. Risks and mitigations

| Risk | Mitigation |
|---|---|
| The 0.70 V upper bound clips legitimate light-grey end-cap pixels in some lighting. | Tunable via constant. Diagnostic test surfaces miscount immediately; threshold can be widened to e.g. 0.75 if the end-cap V is higher than estimated. |
| Existing user configs containing `FullColor`/`Tolerance` JSON fail to deserialize. | Verified: `System.Text.Json` ignores unknown fields by default. `ConfigStoreTests` adds a case to lock this in. |
| Stamina/mana hue overlaps with the "missing" S/V box at extreme settings (e.g. fully drained mana might be a very low-saturation blue). | Out of scope — Stamina/Mana wiring is a separate change. The S/V thresholds will be re-validated against stamina/mana sample images at that time. |
| Per-column ≥20% threshold is too low if a future bar uses a chunkier vertical gradient. | Same threshold has been validated against the previous game's UI. If a future game breaks it, the test infrastructure will catch it on first regression. |

---

## 7. Out-of-scope follow-ups

- Stamina/mana detection runtime: new `BarCalibration` fields in `AppConfig`, calibration UI for two more regions, capture pipeline reading three regions per tick, message protocol carrying three values, party HUD UI rendering three bars.
- Auto-detection of bar regions (no user dragging required).
- A "bar type" enum on `BarCalibration` if any future bar needs per-type S/V threshold overrides.
