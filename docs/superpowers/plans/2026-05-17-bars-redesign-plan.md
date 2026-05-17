# Bars Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the messy / misaligned Bars section with three uniform stacked cards (HP / Stamina / Mana), each showing a live preview of the captured region and a green/yellow/red status icon whose tooltip explains validation issues.

**Architecture:** New `Bars/` folder hosts a pure-logic `BarRegionValidator`, a `BarPreviewSource` capture-timer service, and a `BarCard` UserControl. `MainWindow` swaps the current Bars block for three `<BarCard>` instances and drives their preview lifecycle on `IsVisibleChanged`. Two new bool flags (`StaminaEnabled`, `ManaEnabled`) on the `Preset` record back the toggle switches; the orchestrator reads them through new extension methods so disabling a bar suppresses broadcast without clearing calibration.

**Tech Stack:** C# .NET 8 / WPF, Wpf.Ui 3.x (`ToggleSwitch`), `System.Windows.Media.Imaging.WriteableBitmap`, `System.Windows.Threading.DispatcherTimer`, xUnit for the validator tests.

**Testing approach:** Per `CLAUDE.md`, UI surfaces (cards, preview Image, ToggleSwitch) are manually verified — no flaky UI automation. The new validator is pure logic and gets full TDD coverage (one test per rule + happy path). `BarPreviewSource` is exercised live through the cards; no automated test (it's a thin DispatcherTimer wrapper). The `BarAnalyzer` refactor (extracting `ClassifyColumns`) is non-behavioural and re-uses the existing `BarAnalyzerTests` + `SampleImageRegressionTests` for coverage.

**Reference spec:** [`docs/superpowers/specs/2026-05-17-bars-redesign-design.md`](../specs/2026-05-17-bars-redesign-design.md)

---

## File Structure

**Created:**
- `src/GamePartyHud/Bars/ValidationLevel.cs` — `enum ValidationLevel { Ok, Warning, Error }`.
- `src/GamePartyHud/Bars/ValidationResult.cs` — `record ValidationResult(ValidationLevel Level, string Message)`.
- `src/GamePartyHud/Bars/BarRegionValidator.cs` — pure-logic `static Validate(...)`.
- `src/GamePartyHud/Bars/BarPreviewSource.cs` — capture timer + bitmap reuse, lifecycle (`Start`/`Stop`/`Dispose`).
- `src/GamePartyHud/Bars/BarCard.xaml` + `.xaml.cs` — per-bar `UserControl`.
- `tests/GamePartyHud.Tests/Bars/BarRegionValidatorTests.cs` — one test per rule + happy path.

**Modified:**
- `src/GamePartyHud/Capture/BarAnalyzer.cs` — extract column classification into a public `ClassifyColumns` helper; `Analyze` calls it internally (behaviour unchanged).
- `src/GamePartyHud/Config/Preset.cs` — add `bool StaminaEnabled = true, bool ManaEnabled = true`.
- `src/GamePartyHud/Config/AppConfigExtensions.cs` — add `EffectiveStaminaCalibration` + `EffectiveManaCalibration` extension methods.
- `src/GamePartyHud/Party/PartyOrchestrator.cs` — read sites switch from `ap.StaminaCalibration` / `ap.ManaCalibration` to `ap.EffectiveStaminaCalibration()` / `ap.EffectiveManaCalibration()`.
- `src/GamePartyHud/MainWindow.xaml` — replace the Bars block (separator + three indented pick rows) with three `<local:BarCard>` instances.
- `src/GamePartyHud/MainWindow.xaml.cs` — drop the four `OnIncludeXChecked/Unchecked` handlers, drop `SetRegionStatus` + `RegionStatusState` + chip field references. Add card event handlers that route to `UpdatePreset`. Add `IsVisibleChanged` wiring to drive card preview lifecycles. `PopulateFromConfig` populates each card's `Calibration` + `IsBarEnabled`.
- `tests/GamePartyHud.Tests/Config/AppConfigExtensionsTests.cs` — `Preset` constructions get `StaminaEnabled`/`ManaEnabled` named args (defaults `true`).
- `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs` — same mechanical update.
- `src/GamePartyHud/Config/ConfigStore.cs` — the legacy-migration's `new Preset(...)` constructor needs the two new fields (defaults `true`).

**Untouched (verify by `git diff --stat` at the end):** `RegionSelectorWindow`, `WindowsScreenCapture`, `BarCalibration`, `BarSmoother`, `Hsv`, networking, HUD overlay, preset selector UI, tray, Discord notifier.

---

## Task 1: Extract `BarAnalyzer.ClassifyColumns` helper

Refactor only — pull the column-saturation classification out of `Analyze` into a separate public method so the validator can reuse it without duplicating the saturation/run-length logic. `Analyze` calls the new helper internally; existing `BarAnalyzerTests` and `SampleImageRegressionTests` provide the regression net (no behaviour change expected).

**Files:**
- Modify: `src/GamePartyHud/Capture/BarAnalyzer.cs`

- [ ] **Step 1.1: Add the `ClassifyColumns` helper and route `Analyze` through it**

Open `src/GamePartyHud/Capture/BarAnalyzer.cs`. Add the new public method below `Analyze` (or above `Analyze`, either works), and replace the inline classification loop inside `Analyze` with a call to the new helper:

```csharp
/// <summary>
/// Classify each column of the bar region as "filled" (sufficient saturated
/// vertical run to be part of the bar's coloured fill) or "missing" (the
/// column is empty/grey/text-overlay/frame). Returned array is indexed by
/// column from the bar's anchor side (i.e. axis already flipped for RTL).
/// </summary>
public static bool[] ClassifyColumns(ReadOnlySpan<byte> bgra, int width, int height, BarCalibration cal)
{
    if (width <= 0 || height <= 0) return Array.Empty<bool>();
    if (bgra.Length < width * height * 4) throw new ArgumentException("bgra too small", nameof(bgra));

    int sampleRows = height;
    int minFilledRun = Math.Max(StableRun, sampleRows / 5);

    bool ltr = cal.Direction == FillDirection.LTR;

    var colFilled = new bool[width];
    for (int i = 0; i < width; i++)
    {
        int col = ltr ? i : (width - 1 - i);
        int currentRun = 0;
        int longestRun = 0;
        for (int y = 0; y < height; y++)
        {
            int idx = (y * width + col) * 4;
            var hsv = Hsv.FromBgra(bgra[idx], bgra[idx + 1], bgra[idx + 2]);
            if (IsFilledPixel(hsv))
            {
                currentRun++;
                if (currentRun > longestRun) longestRun = currentRun;
            }
            else
            {
                currentRun = 0;
            }
        }
        colFilled[i] = longestRun >= minFilledRun;
    }
    return colFilled;
}
```

Then rewrite the body of `Analyze` to start with:

```csharp
public float Analyze(ReadOnlySpan<byte> bgra, int width, int height, BarCalibration cal)
{
    if (width <= 0 || height <= 0) return 0f;
    if (bgra.Length < width * height * 4) throw new ArgumentException("bgra too small", nameof(bgra));

    var colFilled = ClassifyColumns(bgra, width, height, cal);

    // Convert to "missing" semantics that the rest of the algorithm uses
    // (filled=false in the original code → missing=true).
    var colMissing = new bool[colFilled.Length];
    int missingCount = 0;
    for (int i = 0; i < colFilled.Length; i++)
    {
        colMissing[i] = !colFilled[i];
        if (colMissing[i]) missingCount++;
    }

    if (missingCount == 0) return 1f;
    if (missingCount == width) return 0f;

    // ... (Pass 2 + Pass 3 from the existing code, unchanged) ...
```

Keep Pass 2 and Pass 3 (the stable-initial-state detection and the transition scan) exactly as they are today. Only the per-column classification loop is moved out.

- [ ] **Step 1.2: Build + run all tests**

```
dotnet build src/GamePartyHud/GamePartyHud.csproj
dotnet test tests/GamePartyHud.Tests/GamePartyHud.Tests.csproj
```
Expected: build clean, 161/161 pass. The existing `BarAnalyzerTests` and `SampleImageRegressionTests` are the regression net.

- [ ] **Step 1.3: Commit**

```
git add src/GamePartyHud/Capture/BarAnalyzer.cs
git commit -m "refactor(capture): extract BarAnalyzer.ClassifyColumns helper

Pulls the per-column saturation/run-length classification out of
Analyze() so the new BarRegionValidator (next commit) can reuse it
without duplicating the IsFilledPixel + minFilledRun threshold logic.
Analyze still drives the transition-scan; behavior unchanged
(BarAnalyzerTests + SampleImageRegressionTests cover the regression)."
```

---

## Task 2: Add `StaminaEnabled` / `ManaEnabled` to `Preset` + extension methods

Two new bool fields on `Preset` (default `true`), two new extension methods so the orchestrator can read the calibration "as broadcast" (null when disabled), one read-site change in `PartyOrchestrator`. All existing `Preset(...)` construction sites need the new named args. Migration is free — `System.Text.Json` fills missing JSON keys with the default values.

**Files:**
- Modify: `src/GamePartyHud/Config/Preset.cs`
- Modify: `src/GamePartyHud/Config/AppConfigExtensions.cs`
- Modify: `src/GamePartyHud/Config/AppConfig.cs` (Defaults preset construction)
- Modify: `src/GamePartyHud/Config/ConfigStore.cs` (migration-path preset construction)
- Modify: `src/GamePartyHud/MainWindow.xaml.cs` (new-preset construction in `OnCreatePreset`)
- Modify: `src/GamePartyHud/Party/PartyOrchestrator.cs` (read sites)
- Modify: `tests/GamePartyHud.Tests/Config/AppConfigExtensionsTests.cs` (Preset constructions)
- Modify: `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs` (Preset constructions)

- [ ] **Step 2.1: Add the two flags to `Preset`**

In `src/GamePartyHud/Config/Preset.cs` replace the record with:

```csharp
public sealed record Preset(
    string Id,
    string Name,
    string Nickname,
    Role Role,
    BarCalibration? HpCalibration,
    BarCalibration? StaminaCalibration,
    BarCalibration? ManaCalibration,
    bool StaminaEnabled = true,
    bool ManaEnabled = true);
```

(Keep the existing XML doc comments at the top of the file — only the parameter list changes.)

- [ ] **Step 2.2: Add the extension methods**

Open `src/GamePartyHud/Config/AppConfigExtensions.cs`. Append to the existing `AppConfigExtensions` static class:

```csharp
    /// <summary>Returns the Stamina calibration if it's enabled, else null.
    /// The toggle controls whether the bar is broadcast / tracked at runtime
    /// without clearing the saved calibration, so re-enabling restores it.</summary>
    public static BarCalibration? EffectiveStaminaCalibration(this Preset p) =>
        p.StaminaEnabled ? p.StaminaCalibration : null;

    /// <summary>Returns the Mana calibration if it's enabled, else null.</summary>
    public static BarCalibration? EffectiveManaCalibration(this Preset p) =>
        p.ManaEnabled ? p.ManaCalibration : null;
```

- [ ] **Step 2.3: Update read sites in `PartyOrchestrator`**

In `src/GamePartyHud/Party/PartyOrchestrator.cs`, find the snapshot block at the top of the `try` in `PollAndBroadcastLoopAsync`:

```csharp
var ap = _cfg.ActivePreset;
float? hp = await ReadBarAsync(ap.HpCalibration, _hpSmoother, ct).ConfigureAwait(false);
float? stamina = await ReadBarAsync(ap.StaminaCalibration, _staminaSmoother, ct).ConfigureAwait(false);
float? mana = await ReadBarAsync(ap.ManaCalibration, _manaSmoother, ct).ConfigureAwait(false);
```

Replace with:

```csharp
var ap = _cfg.ActivePreset;
float? hp = await ReadBarAsync(ap.HpCalibration, _hpSmoother, ct).ConfigureAwait(false);
float? stamina = await ReadBarAsync(ap.EffectiveStaminaCalibration(), _staminaSmoother, ct).ConfigureAwait(false);
float? mana = await ReadBarAsync(ap.EffectiveManaCalibration(), _manaSmoother, ct).ConfigureAwait(false);
```

HP has no toggle, so its read is unchanged.

- [ ] **Step 2.4: Update Defaults preset construction**

In `src/GamePartyHud/Config/AppConfig.cs`, the `Defaults` initializer constructs a `Preset`. No change is required at the call site (the two new flags have `true` defaults), but verify the build will accept the existing call. If the compiler complains about unrecognized parameters, the issue is the previous step wasn't saved — re-read `Preset.cs` and confirm.

- [ ] **Step 2.5: Update legacy-config migration preset construction**

In `src/GamePartyHud/Config/ConfigStore.cs`, find `ParseWithMigration` which builds the migrated `Preset`:

```csharp
var migrated = new Preset(
    Id: AppConfig.DefaultPresetId,
    Name: "Default",
    Nickname:           GetString(node, "nickname")       ?? defaultPreset.Nickname,
    Role:               GetEnum<Role>(node, "role")       ?? defaultPreset.Role,
    HpCalibration:      GetObject<BarCalibration>(node, "hpCalibration"),
    StaminaCalibration: GetObject<BarCalibration>(node, "staminaCalibration"),
    ManaCalibration:    GetObject<BarCalibration>(node, "manaCalibration"));
```

No change required — both new params have defaults of `true` so the existing positional-named call still compiles. Legacy installs migrate with both bars marked enabled (matches their pre-toggle expectation that any saved calibration was being broadcast).

- [ ] **Step 2.6: Update `OnCreatePreset` in MainWindow**

In `src/GamePartyHud/MainWindow.xaml.cs`, find `OnCreatePreset`:

```csharp
var newPreset = new Preset(
    Id: newId,
    Name: newName,
    Nickname: "",
    Role: Role.Utility,
    HpCalibration: null,
    StaminaCalibration: null,
    ManaCalibration: null);
```

No change required — defaults apply. (If you want, you can explicitly write `StaminaEnabled: true, ManaEnabled: true` for clarity, but it's redundant.)

- [ ] **Step 2.7: Update test fixtures**

In `tests/GamePartyHud.Tests/Config/AppConfigExtensionsTests.cs`, find the two `new Preset(...)` calls inside `WithTwoPresets`. No change required — defaults apply.

In `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs`, find the round-trip fixture's `new Preset(...)`:

```csharp
new Preset(
    Id: AppConfig.DefaultPresetId,
    Name: "Default",
    Nickname: "Yiawahuye",
    Role: Role.Tank,
    HpCalibration:      new BarCalibration(new CaptureRegion(10, 20, 300, 18), FillDirection.LTR),
    StaminaCalibration: new BarCalibration(new CaptureRegion(10, 40, 300, 18), FillDirection.LTR),
    ManaCalibration:    new BarCalibration(new CaptureRegion(10, 60, 300, 18), FillDirection.LTR)),
```

To prove the round-trip preserves the new flags too, change one to `false`:

```csharp
new Preset(
    Id: AppConfig.DefaultPresetId,
    Name: "Default",
    Nickname: "Yiawahuye",
    Role: Role.Tank,
    HpCalibration:      new BarCalibration(new CaptureRegion(10, 20, 300, 18), FillDirection.LTR),
    StaminaCalibration: new BarCalibration(new CaptureRegion(10, 40, 300, 18), FillDirection.LTR),
    ManaCalibration:    new BarCalibration(new CaptureRegion(10, 60, 300, 18), FillDirection.LTR),
    StaminaEnabled: false,
    ManaEnabled: true),
```

- [ ] **Step 2.8: Add a unit test for the extension methods**

Append to `tests/GamePartyHud.Tests/Config/AppConfigExtensionsTests.cs`:

```csharp
    [Fact]
    public void EffectiveStaminaCalibration_ReturnsNullWhenDisabled()
    {
        var preset = new Preset(
            Id: "p", Name: "P", Nickname: "N", Role: Role.Utility,
            HpCalibration: null,
            StaminaCalibration: new BarCalibration(new CaptureRegion(0, 0, 100, 20), FillDirection.LTR),
            ManaCalibration: null,
            StaminaEnabled: false);

        Assert.Null(preset.EffectiveStaminaCalibration());
    }

    [Fact]
    public void EffectiveStaminaCalibration_ReturnsCalibrationWhenEnabled()
    {
        var cal = new BarCalibration(new CaptureRegion(0, 0, 100, 20), FillDirection.LTR);
        var preset = new Preset(
            Id: "p", Name: "P", Nickname: "N", Role: Role.Utility,
            HpCalibration: null,
            StaminaCalibration: cal,
            ManaCalibration: null,
            StaminaEnabled: true);

        Assert.Same(cal, preset.EffectiveStaminaCalibration());
    }

    [Fact]
    public void EffectiveManaCalibration_ReturnsNullWhenDisabled()
    {
        var preset = new Preset(
            Id: "p", Name: "P", Nickname: "N", Role: Role.Utility,
            HpCalibration: null,
            StaminaCalibration: null,
            ManaCalibration: new BarCalibration(new CaptureRegion(0, 0, 100, 20), FillDirection.LTR),
            ManaEnabled: false);

        Assert.Null(preset.EffectiveManaCalibration());
    }
```

- [ ] **Step 2.9: Build + run tests**

```
dotnet build src/GamePartyHud/GamePartyHud.csproj
dotnet test tests/GamePartyHud.Tests/GamePartyHud.Tests.csproj
```
Expected: build clean, 164/164 pass (161 existing + 3 new).

- [ ] **Step 2.10: Commit**

```
git add src/GamePartyHud/Config/Preset.cs src/GamePartyHud/Config/AppConfigExtensions.cs src/GamePartyHud/Party/PartyOrchestrator.cs tests/GamePartyHud.Tests/Config/
git commit -m "feat(config): add StaminaEnabled/ManaEnabled flags to Preset

Toggle for the upcoming Bars-redesign card headers. The flag controls
whether the bar is broadcast/tracked at runtime; disabling does NOT
clear the saved calibration, so re-enabling restores everything.

PartyOrchestrator reads through new EffectiveStaminaCalibration() /
EffectiveManaCalibration() extension methods that return null when the
corresponding flag is false. Existing presets deserialise with both
flags defaulting to true (matches pre-toggle behaviour).

Round-trip test exercises a Preset with StaminaEnabled=false to prove
the JSON layer preserves the flag."
```

---

## Task 3: `BarRegionValidator` + tests (TDD, one test per rule)

Pure-logic class with one public method. Each rule gets one test; the seventh test covers the happy path. Reuses `SyntheticBitmap.HorizontalBar(...)` for fixture pixels and `BarAnalyzer.ClassifyColumns` (from Task 1) for the saturation profile.

**Files:**
- Create: `src/GamePartyHud/Bars/ValidationLevel.cs`
- Create: `src/GamePartyHud/Bars/ValidationResult.cs`
- Create: `src/GamePartyHud/Bars/BarRegionValidator.cs`
- Create: `tests/GamePartyHud.Tests/Bars/BarRegionValidatorTests.cs`

- [ ] **Step 3.1: Create `ValidationLevel.cs`**

```csharp
namespace GamePartyHud.Bars;

public enum ValidationLevel
{
    Ok,
    Warning,
    Error,
}
```

- [ ] **Step 3.2: Create `ValidationResult.cs`**

```csharp
namespace GamePartyHud.Bars;

public sealed record ValidationResult(ValidationLevel Level, string Message);
```

- [ ] **Step 3.3: Write the failing test file**

Create `tests/GamePartyHud.Tests/Bars/BarRegionValidatorTests.cs`:

```csharp
using GamePartyHud.Bars;
using GamePartyHud.Capture;
using GamePartyHud.Tests.Capture;
using Xunit;

namespace GamePartyHud.Tests.Bars;

public class BarRegionValidatorTests
{
    // Saturated red bar matching the fixtures in BarAnalyzerTests.
    private static readonly (byte b, byte g, byte r) RedFill = (0, 0, 255);
    private static readonly (byte b, byte g, byte r) DarkEmpty = (10, 10, 10);

    [Fact]
    public void Validate_EmptyRegion_ReturnsError()
    {
        var region = new CaptureRegion(100, 100, 0, 0);
        var bgra = System.Array.Empty<byte>();

        var result = BarRegionValidator.Validate(region, bgra, isPickTime: false);

        Assert.Equal(ValidationLevel.Error, result.Level);
        Assert.Contains("empty", result.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_NoSaturatedPixels_ReturnsError()
    {
        // All-grey rectangle (no saturation anywhere).
        int w = 200, h = 22;
        var bgra = SyntheticBitmap.HorizontalBar(w, h, 0f, DarkEmpty, DarkEmpty);
        var region = new CaptureRegion(0, 0, w, h);

        var result = BarRegionValidator.Validate(region, bgra, isPickTime: false);

        Assert.Equal(ValidationLevel.Error, result.Level);
        Assert.Contains("colored", result.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RegionTooTall_ReturnsWarning()
    {
        int w = 200, h = 60; // height > 30 trips the rule
        var bgra = SyntheticBitmap.HorizontalBar(w, h, 1.0f, RedFill, DarkEmpty);
        var region = new CaptureRegion(0, 0, w, h);

        var result = BarRegionValidator.Validate(region, bgra, isPickTime: false);

        Assert.Equal(ValidationLevel.Warning, result.Level);
        Assert.Contains("tall", result.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RegionTooNarrow_ReturnsWarning()
    {
        int w = 40, h = 22; // width < 60 trips the rule
        var bgra = SyntheticBitmap.HorizontalBar(w, h, 1.0f, RedFill, DarkEmpty);
        var region = new CaptureRegion(0, 0, w, h);

        var result = BarRegionValidator.Validate(region, bgra, isPickTime: false);

        Assert.Equal(ValidationLevel.Warning, result.Level);
        Assert.Contains("narrow", result.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_LowFillAtPickTime_ReturnsWarning()
    {
        int w = 200, h = 22;
        // 50% filled — well below the 0.85 threshold.
        var bgra = SyntheticBitmap.HorizontalBar(w, h, 0.5f, RedFill, DarkEmpty);
        var region = new CaptureRegion(0, 0, w, h);

        var result = BarRegionValidator.Validate(region, bgra, isPickTime: true);

        Assert.Equal(ValidationLevel.Warning, result.Level);
        Assert.Contains("full", result.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_LowFillNotAtPickTime_DoesNotWarn()
    {
        // Same input but isPickTime=false; the low-fill rule must suppress.
        int w = 200, h = 22;
        var bgra = SyntheticBitmap.HorizontalBar(w, h, 0.5f, RedFill, DarkEmpty);
        var region = new CaptureRegion(0, 0, w, h);

        var result = BarRegionValidator.Validate(region, bgra, isPickTime: false);

        Assert.Equal(ValidationLevel.Ok, result.Level);
    }

    [Fact]
    public void Validate_FragmentedFill_ReturnsWarning()
    {
        // Construct a buffer with two separate filled runs:
        // [filled 30][empty 40][filled 50][empty 80] — width 200, height 22
        int w = 200, h = 22;
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool inFirst  = x < 30;
                bool inSecond = x >= 70 && x < 120;
                var c = (inFirst || inSecond) ? RedFill : DarkEmpty;
                bgra[i + 0] = c.b;
                bgra[i + 1] = c.g;
                bgra[i + 2] = c.r;
                bgra[i + 3] = 255;
            }
        }
        var region = new CaptureRegion(0, 0, w, h);

        var result = BarRegionValidator.Validate(region, bgra, isPickTime: false);

        Assert.Equal(ValidationLevel.Warning, result.Level);
        Assert.Contains("continuous", result.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllChecksPass_ReturnsOk()
    {
        int w = 200, h = 22;
        var bgra = SyntheticBitmap.HorizontalBar(w, h, 1.0f, RedFill, DarkEmpty);
        var region = new CaptureRegion(827, 928, w, h);

        var result = BarRegionValidator.Validate(region, bgra, isPickTime: true);

        Assert.Equal(ValidationLevel.Ok, result.Level);
        // The OK message includes the geometry + a fill percentage.
        Assert.Contains("200", result.Message); // width
        Assert.Contains("22", result.Message);  // height
    }
}
```

- [ ] **Step 3.4: Verify the test fails with a missing-type error**

```
dotnet test tests/GamePartyHud.Tests/GamePartyHud.Tests.csproj
```
Expected: build fails with `CS0246: BarRegionValidator could not be found`. This proves the tests can't pass without the implementation.

- [ ] **Step 3.5: Implement `BarRegionValidator`**

Create `src/GamePartyHud/Bars/BarRegionValidator.cs`:

```csharp
using System;
using GamePartyHud.Capture;

namespace GamePartyHud.Bars;

/// <summary>
/// Soft-guidance validator for a user-selected bar region. Returns a single
/// <see cref="ValidationResult"/> — the most actionable issue, or
/// <see cref="ValidationLevel.Ok"/> if nothing is amiss. Never blocks save;
/// the caller surfaces the result as a coloured status icon + tooltip.
///
/// Rules, applied in order:
///   1. Empty region              → Error
///   2. No saturated columns      → Error
///   3. Region too tall           → Warning
///   4. Region too narrow         → Warning
///   5. Low fill at pick time     → Warning  (isPickTime only)
///   6. Fragmented fill           → Warning
///   7. All checks pass           → Ok
/// </summary>
public static class BarRegionValidator
{
    // Geometry thresholds. Tuned to the empirically-observed bar sizes
    // (~250–300 wide × 18–24 tall in typical MMO HUDs). Numbers outside
    // these envelopes almost always mean the user grabbed too much or
    // too little.
    public const int MaxReasonableHeight = 30;
    public const int MinReasonableWidth = 60;
    public const float MinFillAtPickTime = 0.85f;

    public static ValidationResult Validate(
        CaptureRegion region,
        ReadOnlySpan<byte> bgra,
        bool isPickTime)
    {
        // Rule 1: empty region.
        if (region.W <= 0 || region.H <= 0)
        {
            return new ValidationResult(
                ValidationLevel.Error,
                "Region is empty. Click Re-pick and drag a box around the bar.");
        }

        // We assume LTR for the validator (the only direction the picker emits today).
        var cal = new BarCalibration(region, FillDirection.LTR);

        // Build the column-filled array using the same saturation/run-length
        // signal the production analyzer uses. Empty span happens only if
        // bgra is shorter than expected — treat as "no saturated pixels".
        bool[] colFilled;
        try
        {
            colFilled = BarAnalyzer.ClassifyColumns(bgra, region.W, region.H, cal);
        }
        catch (ArgumentException)
        {
            return new ValidationResult(
                ValidationLevel.Error,
                "Region is empty. Click Re-pick and drag a box around the bar.");
        }

        int filledCount = 0;
        for (int i = 0; i < colFilled.Length; i++) if (colFilled[i]) filledCount++;

        // Rule 2: nothing saturated at all → not a bar.
        if (filledCount == 0)
        {
            return new ValidationResult(
                ValidationLevel.Error,
                "No colored bar pixels detected. Try dragging a tight box around the colored bar itself — not the background or frame.");
        }

        // Rule 3: region too tall.
        if (region.H > MaxReasonableHeight || region.H * 5 > region.W)
        {
            return new ValidationResult(
                ValidationLevel.Warning,
                $"Box looks too tall ({region.H}px tall vs {region.W}px wide). Try dragging just the bar's height — no frame above or below.");
        }

        // Rule 4: region too narrow.
        if (region.W < MinReasonableWidth)
        {
            return new ValidationResult(
                ValidationLevel.Warning,
                $"Box looks narrow ({region.W}px). For best accuracy, drag across the full visible width of the bar.");
        }

        // Compute fill fraction for both the pick-time rule and the OK message.
        // We count the leading run of filled columns from the anchor side
        // (the same direction Analyze uses) divided by total width.
        int leadingFilledRun = 0;
        for (int i = 0; i < colFilled.Length && colFilled[i]; i++) leadingFilledRun++;
        float fillFraction = (float)leadingFilledRun / region.W;

        // Rule 5: bar wasn't full when picked (only at pick time).
        if (isPickTime && fillFraction < MinFillAtPickTime)
        {
            int pct = (int)Math.Round(fillFraction * 100f);
            return new ValidationResult(
                ValidationLevel.Warning,
                $"Bar was only {pct}% full when picked. For best calibration, have HP / Stamina / Mana at maximum before clicking Pick.");
        }

        // Rule 6: fragmented fill — more than one contiguous run of filled
        // columns separated by empties. A clean bar has at most one filled
        // run followed by one empty run.
        int filledRuns = 0;
        bool inRun = false;
        for (int i = 0; i < colFilled.Length; i++)
        {
            if (colFilled[i] && !inRun) { filledRuns++; inRun = true; }
            else if (!colFilled[i]) inRun = false;
            if (filledRuns >= 2) break;
        }
        if (filledRuns >= 2)
        {
            return new ValidationResult(
                ValidationLevel.Warning,
                "The captured pixels don't look like one continuous bar. Did the box catch part of an adjacent bar or icon?");
        }

        // Rule 7: all clear.
        int okPct = (int)Math.Round(fillFraction * 100f);
        return new ValidationResult(
            ValidationLevel.Ok,
            $"Looks good. Detected {region.W}×{region.H} at ({region.X}, {region.Y}), {okPct}% fill.");
    }
}
```

- [ ] **Step 3.6: Run tests**

```
dotnet test tests/GamePartyHud.Tests/GamePartyHud.Tests.csproj
```
Expected: all 8 new tests pass; existing 164 still pass; total 172/172.

- [ ] **Step 3.7: Commit**

```
git add src/GamePartyHud/Bars/ tests/GamePartyHud.Tests/Bars/
git commit -m "feat(bars): add BarRegionValidator with 7 soft-guidance rules

Pure-logic validator that returns one of Ok / Warning / Error plus a
human-readable message for any captured bar region. Rules are applied
in priority order and the first match wins — the user sees the most
actionable issue, not a wall of warnings:

  1. Empty region            → Error
  2. No saturated columns    → Error
  3. Region too tall         → Warning
  4. Region too narrow       → Warning
  5. Low fill at pick time   → Warning (isPickTime only)
  6. Fragmented fill         → Warning
  7. All pass                → Ok

The validator reuses BarAnalyzer.ClassifyColumns so the saturation
threshold stays central. Eight unit tests cover one rule each plus
the happy path."
```

---

## Task 4: `BarPreviewSource` capture-timer service

Thin wrapper around `DispatcherTimer` + `IScreenCapture` + reused `WriteableBitmap`. No tests (it's exercised live through the BarCard manual verification). Public surface is the constructor + `Start` / `Stop` / `Dispose`.

**Files:**
- Create: `src/GamePartyHud/Bars/BarPreviewSource.cs`

- [ ] **Step 4.1: Create `BarPreviewSource.cs`**

```csharp
using System;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GamePartyHud.Capture;
using GamePartyHud.Diagnostics;

namespace GamePartyHud.Bars;

/// <summary>
/// Per-bar capture timer. Every ~333 ms (3 Hz) while running, grabs the
/// region returned by <c>getCalibration()</c>, validates it, and invokes
/// <c>onUpdate</c> on the UI thread with a refreshed <see cref="WriteableBitmap"/>
/// plus the <see cref="ValidationResult"/>.
///
/// The bitmap is reused across frames to avoid per-tick allocations; when
/// the captured region's width or height changes (typical case: user
/// re-picks the bar) the old bitmap is discarded and a new one is created
/// at the new dimensions.
///
/// Start/Stop are idempotent. Dispose stops the timer and releases the bitmap.
/// </summary>
public sealed class BarPreviewSource : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(333);

    private readonly IScreenCapture _capture;
    private readonly Func<BarCalibration?> _getCalibration;
    private readonly Action<WriteableBitmap?, ValidationResult> _onUpdate;
    private readonly DispatcherTimer _timer;

    private WriteableBitmap? _bitmap;
    private int _bitmapW;
    private int _bitmapH;
    private CancellationTokenSource? _inFlightCts;

    public BarPreviewSource(
        IScreenCapture capture,
        Func<BarCalibration?> getCalibration,
        Action<WriteableBitmap?, ValidationResult> onUpdate)
    {
        _capture = capture;
        _getCalibration = getCalibration;
        _onUpdate = onUpdate;
        _timer = new DispatcherTimer { Interval = Interval };
        _timer.Tick += OnTick;
    }

    public void Start()
    {
        if (_timer.IsEnabled) return;
        _timer.Start();
    }

    public void Stop()
    {
        if (!_timer.IsEnabled) return;
        _timer.Stop();
        _inFlightCts?.Cancel();
        _inFlightCts = null;
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        var cal = _getCalibration();
        if (cal is null) return;

        // Cancel any in-flight capture from a previous tick (shouldn't happen
        // at 333 ms with a fast BitBlt, but defensive).
        _inFlightCts?.Cancel();
        var cts = _inFlightCts = new CancellationTokenSource();

        byte[] bgra;
        try
        {
            bgra = (await _capture.CaptureBgraAsync(cal.Region, cts.Token).ConfigureAwait(true)).ToArray();
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Log.Warn("BarPreviewSource: capture failed: " + ex.Message);
            return;
        }

        if (cts.IsCancellationRequested) return;

        int w = cal.Region.W;
        int h = cal.Region.H;
        if (w <= 0 || h <= 0 || bgra.Length < w * h * 4) return;

        // Reuse the WriteableBitmap if the dimensions match; otherwise re-create.
        if (_bitmap is null || _bitmapW != w || _bitmapH != h)
        {
            _bitmap = new WriteableBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            _bitmapW = w;
            _bitmapH = h;
        }

        var rect = new Int32Rect(0, 0, w, h);
        _bitmap.WritePixels(rect, bgra, w * 4, 0);

        var result = BarRegionValidator.Validate(cal.Region, bgra, isPickTime: false);
        _onUpdate(_bitmap, result);
    }

    public void Dispose()
    {
        Stop();
        _bitmap = null;
    }
}
```

- [ ] **Step 4.2: Build**

```
dotnet build src/GamePartyHud/GamePartyHud.csproj
```
Expected: build clean.

- [ ] **Step 4.3: Commit**

```
git add src/GamePartyHud/Bars/BarPreviewSource.cs
git commit -m "feat(bars): add BarPreviewSource capture-timer service

Per-bar DispatcherTimer at 3 Hz that captures the calibrated region
via IScreenCapture, copies the BGRA bytes into a reused
WriteableBitmap (re-allocated only when W/H changes), runs
BarRegionValidator (isPickTime=false for live ticks), and invokes
the supplied onUpdate callback on the UI thread.

Used by the new BarCard UserControl to drive the live preview Image
and the validation status indicator. Start/Stop idempotent; Dispose
releases the bitmap. Idle when getCalibration() returns null."
```

---

## Task 5: `BarCard` UserControl

The per-bar UI piece. Header (name + optional ToggleSwitch) over a content row (preview Image + status Ellipse + Pick button). Owns its `BarPreviewSource` and lifecycle. Raises events that `MainWindow` forwards into the config layer.

**Files:**
- Create: `src/GamePartyHud/Bars/BarCard.xaml`
- Create: `src/GamePartyHud/Bars/BarCard.xaml.cs`

- [ ] **Step 5.1: Create `BarCard.xaml`**

```xml
<UserControl x:Class="GamePartyHud.Bars.BarCard"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml">
    <Border Padding="10,8"
            CornerRadius="4"
            Background="#0CFFFFFF"
            BorderBrush="#22FFFFFF"
            BorderThickness="1">
        <Grid>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>   <!-- header -->
                <RowDefinition Height="Auto"/>   <!-- content -->
            </Grid.RowDefinitions>

            <!-- Header: bar name (left) + ToggleSwitch (right, only for Stamina/Mana) -->
            <Grid Grid.Row="0" Margin="0,0,0,6">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>
                <TextBlock Grid.Column="0"
                           x:Name="HeaderText"
                           FontSize="13" FontWeight="SemiBold"
                           VerticalAlignment="Center"/>
                <ui:ToggleSwitch Grid.Column="1"
                                 x:Name="EnableToggle"
                                 Visibility="Collapsed"
                                 VerticalAlignment="Center"
                                 Checked="OnEnableToggleChanged"
                                 Unchecked="OnEnableToggleChanged"/>
            </Grid>

            <!-- Content: preview thumbnail (left, flex) + status icon + pick button (right) -->
            <Grid Grid.Row="1">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>

                <!-- Preview area -->
                <Border Grid.Column="0"
                        x:Name="PreviewBorder"
                        Height="28"
                        Margin="0,0,12,0"
                        CornerRadius="2"
                        Background="#22000000"
                        BorderBrush="#33FFFFFF"
                        BorderThickness="1">
                    <Grid>
                        <Image x:Name="PreviewImage"
                               Stretch="Fill"
                               Visibility="Collapsed"/>
                        <TextBlock x:Name="PreviewPlaceholder"
                                   Text="Pick a region to see a live preview"
                                   FontSize="11"
                                   Opacity="0.5"
                                   HorizontalAlignment="Center"
                                   VerticalAlignment="Center"/>
                    </Grid>
                </Border>

                <!-- Status + button -->
                <StackPanel Grid.Column="1" Orientation="Horizontal" VerticalAlignment="Center">
                    <Ellipse x:Name="StatusIcon"
                             Width="12" Height="12"
                             Margin="0,0,10,0"
                             Fill="#88FFFFFF"
                             ToolTip="No region picked yet."/>
                    <ui:Button x:Name="PickButton"
                               Appearance="Primary"
                               Icon="Target24"
                               Padding="10,4"
                               Click="OnPickClicked"/>
                </StackPanel>
            </Grid>
        </Grid>
    </Border>
</UserControl>
```

- [ ] **Step 5.2: Create `BarCard.xaml.cs`**

```csharp
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GamePartyHud.Capture;

namespace GamePartyHud.Bars;

/// <summary>
/// One bar's setup card: header (name + optional ToggleSwitch) over a
/// content row (live preview + status icon + pick button). HP cards have
/// no toggle (always-on); Stamina/Mana cards have the toggle.
///
/// Owns a BarPreviewSource. Caller is responsible for:
///   - setting BarName + IsToggleable at XAML construction time
///   - assigning Calibration + IsBarEnabled (typically from
///     MainWindow.PopulateFromConfig after a preset switch)
///   - calling AttachPreview(capture) once the screen capture is available
///   - calling SetPickTimeValidation(result) right after a fresh pick so the
///     low-fill warning gets cached as the displayed result until next pick
///   - handling PickRequested / EnabledChanged
/// </summary>
public partial class BarCard : UserControl
{
    private BarPreviewSource? _previewSource;
    private IScreenCapture? _capture;
    private BarCalibration? _calibration;
    private bool _isBarEnabled = true;
    private bool _isWindowVisible = true;
    private ValidationResult? _pickTimeOverride;

    public BarCard() => InitializeComponent();

    /// <summary>"HP" / "Stamina" / "Mana"</summary>
    public string BarName
    {
        get => HeaderText.Text;
        set
        {
            HeaderText.Text = value;
            PickButton.Content = $"Pick {value.ToLowerInvariant()} bar region";
        }
    }

    /// <summary>If true, the header shows a ToggleSwitch. HP cards set false.</summary>
    public bool IsToggleable
    {
        get => EnableToggle.Visibility == Visibility.Visible;
        set => EnableToggle.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Current bar calibration. Setting this triggers preview lifecycle
    /// updates (Start if non-null + enabled + window visible; Stop otherwise).
    /// Clears the pick-time override so the next live result wins.
    /// </summary>
    public BarCalibration? Calibration
    {
        get => _calibration;
        set
        {
            _calibration = value;
            _pickTimeOverride = null;
            UpdateButtonAppearance();
            UpdatePlaceholderVisibility();
            UpdatePreviewLifecycle();
        }
    }

    /// <summary>
    /// Two-way mirror of the ToggleSwitch's IsChecked. Setting this updates
    /// the switch without firing EnabledChanged (programmatic vs user-driven).
    /// </summary>
    public bool IsBarEnabled
    {
        get => _isBarEnabled;
        set
        {
            if (_isBarEnabled == value) return;
            _isBarEnabled = value;
            // Update the switch silently — the Checked/Unchecked handler guards
            // on _suppressToggleEvent to avoid re-raising.
            _suppressToggleEvent = true;
            EnableToggle.IsChecked = value;
            _suppressToggleEvent = false;
            UpdateBodyOpacity();
            UpdatePreviewLifecycle();
        }
    }

    public event EventHandler? PickRequested;
    public event EventHandler? EnabledChanged;

    /// <summary>
    /// Wire up the screen-capture source. Called once by MainWindow during
    /// construction; the card lazily creates a BarPreviewSource on first
    /// Calibration assignment.
    /// </summary>
    public void AttachCapture(IScreenCapture capture)
    {
        _capture = capture;
    }

    /// <summary>
    /// Called by MainWindow whenever its own IsVisibleChanged fires — the
    /// preview captures should pause while the settings window is hidden.
    /// </summary>
    public void SetWindowVisible(bool visible)
    {
        _isWindowVisible = visible;
        UpdatePreviewLifecycle();
    }

    /// <summary>
    /// Apply a pick-time validation result (typically from a low-fill
    /// warning). The card uses this as the displayed result until either
    /// (a) Calibration is reassigned (re-pick clears the override) or (b)
    /// the caller calls this with a new result.
    /// </summary>
    public void SetPickTimeValidation(ValidationResult? result)
    {
        _pickTimeOverride = result;
        if (result is not null) ApplyValidation(result);
    }

    private bool _suppressToggleEvent;

    private void OnEnableToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleEvent) return;
        _isBarEnabled = EnableToggle.IsChecked == true;
        UpdateBodyOpacity();
        UpdatePreviewLifecycle();
        EnabledChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPickClicked(object sender, RoutedEventArgs e)
    {
        PickRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateButtonAppearance()
    {
        if (_calibration is null)
        {
            PickButton.Appearance = Wpf.Ui.Controls.ControlAppearance.Primary;
            PickButton.Content = $"Pick {HeaderText.Text.ToLowerInvariant()} bar region";
        }
        else
        {
            PickButton.Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary;
            PickButton.Content = "Re-pick";
        }
    }

    private void UpdatePlaceholderVisibility()
    {
        bool showImage = _calibration is not null && _isBarEnabled;
        PreviewImage.Visibility   = showImage ? Visibility.Visible : Visibility.Collapsed;
        PreviewPlaceholder.Visibility = showImage ? Visibility.Collapsed : Visibility.Visible;
        if (_calibration is null)
        {
            PreviewPlaceholder.Text = "Pick a region to see a live preview";
        }
        else if (!_isBarEnabled)
        {
            PreviewPlaceholder.Text = "Not enabled. Toggle on to broadcast.";
        }
    }

    private void UpdateBodyOpacity()
    {
        // Greys out the preview row when disabled, keeping the toggle bright.
        PreviewBorder.Opacity = _isBarEnabled ? 1.0 : 0.4;
        PickButton.IsEnabled = _isBarEnabled;
        StatusIcon.Opacity = _isBarEnabled ? 1.0 : 0.4;
        UpdatePlaceholderVisibility();
    }

    private void UpdatePreviewLifecycle()
    {
        bool shouldRun =
            _capture is not null &&
            _calibration is not null &&
            _isBarEnabled &&
            _isWindowVisible;

        if (shouldRun)
        {
            _previewSource ??= new BarPreviewSource(
                _capture!,
                () => _calibration,
                OnPreviewUpdated);
            _previewSource.Start();
        }
        else
        {
            _previewSource?.Stop();
        }
    }

    private void OnPreviewUpdated(WriteableBitmap? bitmap, ValidationResult result)
    {
        if (bitmap is not null)
        {
            PreviewImage.Source = bitmap;
            PreviewImage.Visibility = Visibility.Visible;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
        }

        // A cached pick-time warning wins over the live result; the cached
        // result is cleared whenever Calibration is reassigned (i.e. re-pick).
        var displayed = _pickTimeOverride ?? result;
        ApplyValidation(displayed);
    }

    private void ApplyValidation(ValidationResult result)
    {
        StatusIcon.Fill = result.Level switch
        {
            ValidationLevel.Ok      => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
            ValidationLevel.Warning => new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07)),
            ValidationLevel.Error   => new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)),
            _                       => new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
        };
        StatusIcon.ToolTip = result.Message;
    }
}
```

- [ ] **Step 5.3: Build**

```
dotnet build src/GamePartyHud/GamePartyHud.csproj
```
Expected: build clean.

- [ ] **Step 5.4: Commit**

```
git add src/GamePartyHud/Bars/BarCard.xaml src/GamePartyHud/Bars/BarCard.xaml.cs
git commit -m "feat(bars): add BarCard UserControl

One-bar setup card: header row (BarName + optional ToggleSwitch for
Stamina/Mana) over a content row (live preview Image + Ellipse status
icon + Pick button). Owns its BarPreviewSource and Start/Stops it
based on Calibration / IsBarEnabled / SetWindowVisible().

The card consumes capture via AttachCapture() once at startup, and
forwards user actions back through PickRequested / EnabledChanged
events for MainWindow to wire into the config layer.

A pick-time validation override (set via SetPickTimeValidation) takes
precedence over live preview validation results until the next
re-pick — that's how the 'bar wasn't full when picked' warning stays
visible after the user starts playing and their HP drops below 85%."
```

---

## Task 6: Replace MainWindow Bars block

Drop the current Bars section from `MainWindow.xaml` and the matching handlers from `MainWindow.xaml.cs`. Replace with three `<local:BarCard>` instances and forward each card's events to the existing `UpdatePreset` flow + the existing `OnPickRegion` flow.

**Files:**
- Modify: `src/GamePartyHud/MainWindow.xaml`
- Modify: `src/GamePartyHud/MainWindow.xaml.cs`

- [ ] **Step 6.1: Replace the Bars block in `MainWindow.xaml`**

Open `src/GamePartyHud/MainWindow.xaml`. Find the Bars block — it spans from the `Separator` after the Profile section ("`<Separator Margin="0,10,0,0" Opacity="0.3"/>`" right after the Profile sub-grid closes) down through the `ManaPickRow` `StackPanel`'s closing tag.

Replace that entire range (the Separator, the `"Bars"` `TextBlock`, the HP picker block, the `IncludeStaminaCheck` checkbox + `StaminaPickRow`, and the `IncludeManaCheck` checkbox + `ManaPickRow`) with:

```xml
<!-- Bars sub-section. Three uniform cards; each card carries its own
     preview/validation. Toggle on Stamina/Mana preserves calibration
     when disabled. -->
<Separator Margin="0,10,0,0" Opacity="0.3"/>
<TextBlock Text="Bars" FontSize="16" FontWeight="SemiBold" Margin="0,8,0,10"
           ToolTip="Track HP — plus optional stamina and mana — by dragging a tight box around each bar in your game."/>

<StackPanel>
    <local:BarCard x:Name="HpCard"
                   BarName="HP"
                   IsToggleable="False"
                   IsBarEnabled="True"
                   Margin="0,0,0,8"
                   PickRequested="OnHpPickRequested"/>
    <local:BarCard x:Name="StaminaCard"
                   BarName="Stamina"
                   IsToggleable="True"
                   Margin="0,0,0,8"
                   PickRequested="OnStaminaPickRequested"
                   EnabledChanged="OnStaminaEnabledChanged"/>
    <local:BarCard x:Name="ManaCard"
                   BarName="Mana"
                   IsToggleable="True"
                   PickRequested="OnManaPickRequested"
                   EnabledChanged="OnManaEnabledChanged"/>
</StackPanel>
```

Verify the `xmlns:local="clr-namespace:GamePartyHud"` attribute is already on the root `<ui:FluentWindow>` element (added during the character-presets PR). If not, add `xmlns:local="clr-namespace:GamePartyHud"` to the root element. Then add `xmlns:bars="clr-namespace:GamePartyHud.Bars"` (or change `<local:BarCard>` to `<bars:BarCard>` — the cleaner choice).

If you want the explicit `bars:` prefix (recommended), add `xmlns:bars="clr-namespace:GamePartyHud.Bars"` to the root element and use `<bars:BarCard>` everywhere in the snippet above.

- [ ] **Step 6.2: Update `MainWindow.xaml.cs`**

Open `src/GamePartyHud/MainWindow.xaml.cs`.

**(a) Remove deleted handlers and state.** Delete:
- `OnIncludeStaminaChecked`, `OnIncludeStaminaUnchecked`, `OnIncludeManaChecked`, `OnIncludeManaUnchecked` methods.
- The `SetRegionStatus` method.
- The `RegionStatusState` enum.
- Any field references to `RegionStatusChip`, `RegionStatusIcon`, `RegionStatus`, `StaminaPickRow`, `StaminaStatusChip`, `StaminaStatusIcon`, `StaminaStatus`, `ManaPickRow`, `ManaStatusChip`, `ManaStatusIcon`, `ManaStatus` — those XAML names no longer exist after Step 6.1.
- The old `PickRegionButton`, `PickStaminaRegionButton`, `PickManaRegionButton` field references (the cards' own buttons replace them).

**(b) Replace `OnPickRegion` with three card-specific handlers.** Find the existing `OnPickRegion` method. Delete it (the per-card handlers replace it) and add:

```csharp
// Card-specific pick handlers. Each forwards into the shared
// PickRegionForBar() flow with the appropriate BarType.
private void OnHpPickRequested(object? sender, EventArgs e) => PickRegionForBar(BarType.Hp);
private void OnStaminaPickRequested(object? sender, EventArgs e) => PickRegionForBar(BarType.Stamina);
private void OnManaPickRequested(object? sender, EventArgs e) => PickRegionForBar(BarType.Mana);

private void PickRegionForBar(BarType bar)
{
    Log.Info($"MainWindow: Pick-{bar}-region requested.");
    Opacity = 0;
    try
    {
        var picker = new RegionSelectorWindow(PromptFor(bar));
        picker.ShowDialog();
        if (picker.Result is not { } region)
        {
            Log.Info($"MainWindow: {bar} region selection cancelled.");
            return;
        }

        var cal = new BarCalibration(region, FillDirection.LTR);
        var newConfig = bar switch
        {
            BarType.Hp      => _ctl.Config.UpdatePreset(p => p with { HpCalibration      = cal }),
            BarType.Stamina => _ctl.Config.UpdatePreset(p => p with { StaminaCalibration = cal }),
            BarType.Mana    => _ctl.Config.UpdatePreset(p => p with { ManaCalibration    = cal }),
            _ => _ctl.Config
        };
        _ctl.UpdateConfig(newConfig);

        // Refresh the card's calibration; this clears its cached pick-time
        // override and starts the preview at the new region.
        var card = CardFor(bar);
        card.Calibration = cal;

        // Run pick-time validation once against the just-captured region so
        // the "bar wasn't full" warning sticks until next re-pick.
        var bgra = ((GamePartyHud.Capture.IScreenCapture)_capture)
            .CaptureBgraAsync(cal.Region).AsTask().GetAwaiter().GetResult();
        var pickTimeResult = BarRegionValidator.Validate(cal.Region, bgra, isPickTime: true);
        card.SetPickTimeValidation(pickTimeResult.Level == ValidationLevel.Ok ? null : pickTimeResult);

        Log.Info($"MainWindow: {bar} region calibrated {region.W}x{region.H}@({region.X},{region.Y}).");
    }
    catch (Exception ex)
    {
        Log.Error($"MainWindow: PickRegionForBar ({bar}) failed.", ex);
    }
    finally
    {
        Opacity = 1;
        Activate();
    }
}

private BarCard CardFor(BarType bar) => bar switch
{
    BarType.Hp      => HpCard,
    BarType.Stamina => StaminaCard,
    BarType.Mana    => ManaCard,
    _ => HpCard
};
```

Note `_capture` doesn't yet exist on MainWindow — see Step 6.4 for how it gets injected. The `((GamePartyHud.Capture.IScreenCapture)_capture)` cast prepares for that.

Also add `using GamePartyHud.Bars;` at the top of the file.

**(c) Add the enable-toggle handlers:**

```csharp
private void OnStaminaEnabledChanged(object? sender, EventArgs e)
{
    var enabled = StaminaCard.IsBarEnabled;
    _ctl.UpdateConfig(_ctl.Config.UpdatePreset(p => p with { StaminaEnabled = enabled }));
    Log.Info($"MainWindow: stamina broadcast enabled={enabled}.");
}

private void OnManaEnabledChanged(object? sender, EventArgs e)
{
    var enabled = ManaCard.IsBarEnabled;
    _ctl.UpdateConfig(_ctl.Config.UpdatePreset(p => p with { ManaEnabled = enabled }));
    Log.Info($"MainWindow: mana broadcast enabled={enabled}.");
}
```

**(d) Update `PopulateFromConfig`.** Find the existing chip/checkbox population block (the `if (ap.HpCalibration is { } cal) { SetRegionStatus(...) ... }` chain) and replace with:

```csharp
HpCard.Calibration      = ap.HpCalibration;
HpCard.IsBarEnabled     = true;

StaminaCard.Calibration   = ap.StaminaCalibration;
StaminaCard.IsBarEnabled  = ap.StaminaEnabled;

ManaCard.Calibration      = ap.ManaCalibration;
ManaCard.IsBarEnabled     = ap.ManaEnabled;
```

(The card itself updates its own button label, placeholder text, and preview lifecycle based on these assignments.)

- [ ] **Step 6.3: Build**

```
dotnet build src/GamePartyHud/GamePartyHud.csproj
```
Expected: build clean. If errors mention "missing XAML name", a deleted field is still referenced — search and remove. If errors mention "BarType not found", add `using GamePartyHud.Capture;`.

- [ ] **Step 6.4: Wire capture injection (for the pick-time validator) and visibility-driven preview lifecycle**

The MainWindow constructor must receive an `IScreenCapture` so the card's `PickTimeValidation` step can re-read the captured region. Today MainWindow only takes an `IController`. Add a second constructor parameter.

In `src/GamePartyHud/MainWindow.xaml.cs`, change the constructor signature:

```csharp
private readonly GamePartyHud.Capture.IScreenCapture _capture;

public MainWindow(IController controller, GamePartyHud.Capture.IScreenCapture capture)
{
    InitializeComponent();
    _ctl = controller;
    _capture = capture;

    RoleCombo.ItemsSource = RoleOptions;
    PresetCombo.ItemsSource = _presetItems;

    HpCard.AttachCapture(_capture);
    StaminaCard.AttachCapture(_capture);
    ManaCard.AttachCapture(_capture);

    // ... existing constructor body (FullscreenDisclaimer wiring, PopulateFromConfig, RefreshPartyState, PartyStateChanged subscription) ...

    IsVisibleChanged += OnIsVisibleChanged;
}

private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
{
    bool visible = IsVisible;
    HpCard.SetWindowVisible(visible);
    StaminaCard.SetWindowVisible(visible);
    ManaCard.SetWindowVisible(visible);
}
```

Then update `App.xaml.cs` where MainWindow is constructed. Find the `new MainWindow(this)` call (search for it; should be in the OnStartup region):

```csharp
_main = new MainWindow(this, _capture!);
```

`_capture` is already constructed earlier in `OnStartup` as a `WindowsScreenCapture` instance, so the `!` null-forgive is honest there (the field is set before this line).

- [ ] **Step 6.5: Build + tests**

```
dotnet build src/GamePartyHud/GamePartyHud.csproj
dotnet test tests/GamePartyHud.Tests/GamePartyHud.Tests.csproj
```
Expected: build clean, 172/172 pass.

- [ ] **Step 6.6: Commit**

```
git add src/GamePartyHud/MainWindow.xaml src/GamePartyHud/MainWindow.xaml.cs src/GamePartyHud/App.xaml.cs
git commit -m "feat(ui): replace Bars block with three uniform BarCards

Removes the misaligned chip + indented checkbox layout in favor of
three identical <bars:BarCard> instances driven by the new
BarCard / BarPreviewSource / BarRegionValidator types.

MainWindow injects IScreenCapture into each card (so PickRegion can
run pick-time validation), wires PickRequested / EnabledChanged
events to UpdatePreset, and forwards IsVisibleChanged so the preview
captures pause when the settings window closes to tray.

Drops the four OnIncludeXChecked/Unchecked handlers, SetRegionStatus,
RegionStatusState, and the chip XAML field references — all
superseded by the card's own validation + preview surface.

The old destructive 'Include stamina/mana' checkbox is replaced with
the card-header ToggleSwitch which preserves saved calibration when
disabled."
```

---

## Task 7: Final manual verification (spec §9 checklist)

UI cannot be verified autonomously. The user runs the app and walks the spec's checklist.

- [ ] **Step 7.1: Walk the spec §9 checklist**

```
dotnet run --project src/GamePartyHud/GamePartyHud.csproj
```

Tick each item from `docs/superpowers/specs/2026-05-17-bars-redesign-design.md` §9:

1. Fresh install — three uniform Bars cards, Stamina/Mana toggles default to Disabled position (or Enabled if you migrated).
2. Pick HP region at full → green status icon, tooltip shows W×H + ~100% fill. Live preview updates at ~3 fps.
3. Move game window so the bar leaves the captured area → preview shows new pixels → status flips to red ("No colored bar pixels detected..."). Move window back → green again.
4. Pick a too-tall region → yellow status icon, tooltip says "Box looks too tall...".
5. Pick at ~50% HP → yellow icon, tooltip says "Bar was only 50% full when picked...". Play and let HP drop further → tooltip stays at the cached pick-time message.
6. Toggle Stamina off → preview greys + pauses, calibration **preserved**. Toggle back on → preview restarts at the same region; no re-pick.
7. Close to tray → preview captures stop (verify in app.log: no further "BarPreviewSource: capture failed" or tick logs). Re-open → resumes.
8. Switch to a preset with different calibrations → all three cards refresh; previews retarget.
9. In-party: toggle Stamina off → teammate's HUD stops showing the stamina stripe within ~1 broadcast tick. Toggle back on → it returns.
10. `dotnet test` confirms 172/172 (161 from before this PR + 3 new extension tests from Task 2 + 8 new validator tests from Task 3 = 172).

- [ ] **Step 7.2: Verify the diff scope**

```
git diff main --stat
```

Expected file list:
```
docs/superpowers/specs/2026-05-17-bars-redesign-design.md   (new)
docs/superpowers/plans/2026-05-17-bars-redesign-plan.md     (new)
src/GamePartyHud/App.xaml.cs                                (1-line change)
src/GamePartyHud/Bars/BarCard.xaml                          (new)
src/GamePartyHud/Bars/BarCard.xaml.cs                       (new)
src/GamePartyHud/Bars/BarPreviewSource.cs                   (new)
src/GamePartyHud/Bars/BarRegionValidator.cs                 (new)
src/GamePartyHud/Bars/ValidationLevel.cs                    (new)
src/GamePartyHud/Bars/ValidationResult.cs                   (new)
src/GamePartyHud/Capture/BarAnalyzer.cs                     (refactor)
src/GamePartyHud/Config/AppConfigExtensions.cs              (+2 methods)
src/GamePartyHud/Config/Preset.cs                           (+2 fields)
src/GamePartyHud/MainWindow.xaml                            (Bars block rewrite)
src/GamePartyHud/MainWindow.xaml.cs                         (handlers + ctor)
src/GamePartyHud/Party/PartyOrchestrator.cs                 (2 read sites)
tests/GamePartyHud.Tests/Bars/BarRegionValidatorTests.cs    (new, 8 tests)
tests/GamePartyHud.Tests/Config/AppConfigExtensionsTests.cs (3 new tests)
tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs         (round-trip fixture)
```

If anything else shows up, investigate — likely an inadvertent edit.

---

## Done criteria

After Task 7 passes:
- `dotnet build` clean, `dotnet test` green, all spec §9 manual checks confirmed.
- `git log main..HEAD --oneline` shows the spec + plan + 6 implementation commits (Tasks 1-6).
- Diff scope matches Step 7.2 — nothing else.

Out of scope (do not do here):
- Vision-based auto-detect of bar location.
- Numeric fill-% overlay on the live preview.
- Per-card preview pause toggle.
- Additional validator rules beyond the seven in the spec.
- Any change to capture, networking, party state, HUD overlay, preset selector, Discord notifier, tray.
