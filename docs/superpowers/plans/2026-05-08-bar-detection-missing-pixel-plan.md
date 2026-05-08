# Bar detection — missing-pixel redesign — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the bar analyzer's red-pixel classifier with a colour-agnostic missing-pixel classifier (so future stamina/mana detection is additive), rename HP-specific types to bar-generic, and refresh the real-image regression suite against the post-UI-update sample set.

**Architecture:** The classifier flips from "is this saturated red?" to "is this desaturated grey within a bounded value range?". Per-column aggregation and stable-run transition detection (already proven against text overlays) are kept verbatim, only the predicate changes. Callers consume the same `Analyze(bgra, w, h, cal) → float` returning fraction-filled.

**Tech Stack:** .NET 8, C# 12, xUnit. No new dependencies.

**Driving spec:** [docs/superpowers/specs/2026-05-08-bar-detection-missing-pixel-redesign.md](../specs/2026-05-08-bar-detection-missing-pixel-redesign.md)

**Phases (each produces a green-build, testable repo state):**
- **Phase A — Mechanical renames** (Tasks 1–3): non-behavioural renames of types whose new name is already final.
- **Phase B — Algorithm change** (Tasks 4–7): swap the analyzer's predicate, refresh real-image regression data, tune thresholds.
- **Phase C — Calibration record cleanup** (Task 8): slim `HpCalibration` to `BarCalibration`, drop `HsvTolerance`, simplify the wizard.
- **Phase D — Migration test** (Task 9): lock in the old-config-JSON deserialisation behaviour.
- **Phase E — Manual verification** (Task 10): live game session smoke test.

---

## File Structure

Files touched by this plan (paths are relative to repo root). The `Capture/` folder is the only source folder receiving structural changes; everything else is mechanical type-name updates.

```
src/GamePartyHud/
├── Capture/
│   ├── HpRegion.cs                    → CaptureRegion.cs            (Task 1)
│   ├── HpSmoother.cs                  → BarSmoother.cs              (Task 2)
│   ├── HpBarDetector.cs               → BarDetector.cs              (Task 3)
│   ├── HpBarAnalyzer.cs               → BarAnalyzer.cs              (Tasks 4-7)
│   ├── HpCalibration.cs               → BarCalibration.cs           (Task 8)
│   ├── HsvTolerance.cs                → DELETED                     (Task 8)
│   ├── Hsv.cs                         (unchanged)
│   ├── FillDirection.cs               (unchanged)
│   ├── IScreenCapture.cs              type-rename only              (Task 1)
│   └── WindowsScreenCapture.cs        type-rename only              (Task 1)
├── Calibration/
│   └── RegionSelectorWindow.xaml.cs   type-rename only              (Task 1)
├── Party/
│   └── PartyOrchestrator.cs           type renames + LogTick edit   (Tasks 1, 2, 5, 7, 8)
├── Config/
│   └── AppConfig.cs                   type renames                  (Tasks 1, 8)
├── App.xaml.cs                        no edit (only field-name uses are unchanged)
└── MainWindow.xaml.cs                 wizard cleanup                (Tasks 1, 8)

tests/GamePartyHud.Tests/
├── Capture/
│   ├── HpBarAnalyzerTests.cs          → BarAnalyzerTests.cs         (Tasks 4-7)
│   ├── HpBarDetectorTests.cs          → BarDetectorTests.cs         (Task 3)
│   ├── HpSmootherTests.cs             → BarSmootherTests.cs         (Task 2)
│   ├── HsvTests.cs                    drop HsvTolerance tests        (Task 8)
│   ├── SyntheticBitmap.cs             (unchanged)
│   ├── ImageLoader.cs                 doc-comment update            (Task 5)
│   ├── SampleImageRegressionTests.cs  data refresh + slim cal       (Tasks 4-7, 8)
│   ├── SampleImageDiagnosticTests.cs  data refresh + slim cal       (Tasks 4-7, 8)
│   └── HpBarExamples/                 (unchanged — already updated by user)
└── Config/
    └── ConfigStoreTests.cs            roundtrip update + new mig    (Tasks 8, 9)
```

The `HpBarExamples/` directory name stays — it's HP-specific sample data, not a generic-bar holder. Future stamina/mana sample sets will live in their own siblings.

---

## Phase A — Mechanical renames

Each rename is its own task and its own commit. None changes behaviour. After each task: build green, all tests still pass.

### Task 1: Rename `HpRegion` → `CaptureRegion`

**Files:**
- Move: `src/GamePartyHud/Capture/HpRegion.cs` → `src/GamePartyHud/Capture/CaptureRegion.cs`
- Modify: `src/GamePartyHud/Capture/CaptureRegion.cs`
- Modify: `src/GamePartyHud/Capture/IScreenCapture.cs:13`
- Modify: `src/GamePartyHud/Capture/WindowsScreenCapture.cs` (lines 15, 24, 28, 32, 35, 36, 39, 44, 45, 48, 50)
- Modify: `src/GamePartyHud/Capture/HpCalibration.cs:4` (record parameter type)
- Modify: `src/GamePartyHud/Calibration/RegionSelectorWindow.xaml.cs:11, 71-77`
- Modify: `src/GamePartyHud/Config/AppConfig.cs:10` (NicknameRegion field type)
- Modify: `src/GamePartyHud/Party/PartyOrchestrator.cs` (no direct uses — but `cal.Region` is a `HpRegion` so transitively touched; verify no source-level reference needs editing)
- Modify: `tests/GamePartyHud.Tests/Capture/HpBarDetectorTests.cs` (no direct `HpRegion` use; verify)
- Modify: `tests/GamePartyHud.Tests/Capture/HpBarAnalyzerTests.cs:9` (RedLtr fixture)
- Modify: `tests/GamePartyHud.Tests/Capture/SampleImageRegressionTests.cs:104` (`new HpRegion(...)`)
- Modify: `tests/GamePartyHud.Tests/Capture/SampleImageDiagnosticTests.cs:75` (`new HpRegion(...)`)
- Modify: `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs:37, 41` (`new HpRegion(...)`)

- [ ] **Step 1: Rename the source file**

```bash
git mv src/GamePartyHud/Capture/HpRegion.cs src/GamePartyHud/Capture/CaptureRegion.cs
```

- [ ] **Step 2: Update the record name inside the file**

Replace the contents of `src/GamePartyHud/Capture/CaptureRegion.cs` with:

```csharp
namespace GamePartyHud.Capture;

public sealed record CaptureRegion(int Monitor, int X, int Y, int W, int H);
```

- [ ] **Step 3: Update every reference in src/ and tests/**

Use Grep to find all occurrences first:

```
Grep pattern: "HpRegion" — expect 11 source files including the renamed file.
```

In every match (excluding the comment line in `WindowsScreenCapture.cs:15`, which is in a doc-comment — also update), replace `HpRegion` → `CaptureRegion`. The doc-comment in `WindowsScreenCapture.cs:15` reads `<see cref="HpRegion"/>` — change to `<see cref="CaptureRegion"/>`.

- [ ] **Step 4: Build**

Run from repo root:

```bash
dotnet build
```

Expected: succeeds with 0 errors and 0 warnings.

- [ ] **Step 5: Run all tests**

```bash
dotnet test
```

Expected: every existing test still passes (this rename is non-behavioural). The real-image regression tests will fail because the example images on disk no longer match the test data table — that's a pre-existing failure introduced by the user's example-image refresh, not caused by this task. Note the failure list; Task 4 fixes it.

If any test other than `SampleImageRegressionTests` or `SampleImageDiagnosticTests` fails, stop and investigate — the rename may have missed a call site.

- [ ] **Step 6: Commit**

```bash
git add src/GamePartyHud/ tests/GamePartyHud.Tests/
git commit -m "refactor: rename HpRegion to CaptureRegion

The record is a generic screen rectangle used for both bar regions
(HpCalibration.Region) and the nickname region (AppConfig.NicknameRegion).
The Hp prefix was misleading; CaptureRegion describes its actual role.
"
```

---

### Task 2: Rename `HpSmoother` → `BarSmoother`

**Files:**
- Move: `src/GamePartyHud/Capture/HpSmoother.cs` → `src/GamePartyHud/Capture/BarSmoother.cs`
- Modify: `src/GamePartyHud/Capture/BarSmoother.cs` (class name and ctor)
- Modify: `src/GamePartyHud/Party/PartyOrchestrator.cs:33` (`HpSmoother _smoother = new(windowSize: 3);`)
- Move: `tests/GamePartyHud.Tests/Capture/HpSmootherTests.cs` → `tests/GamePartyHud.Tests/Capture/BarSmootherTests.cs`
- Modify: `tests/GamePartyHud.Tests/Capture/BarSmootherTests.cs` (class name + every `new HpSmoother(...)` site)

- [ ] **Step 1: Rename source file**

```bash
git mv src/GamePartyHud/Capture/HpSmoother.cs src/GamePartyHud/Capture/BarSmoother.cs
```

- [ ] **Step 2: Update class name and constructor inside `BarSmoother.cs`**

In `src/GamePartyHud/Capture/BarSmoother.cs`, replace `class HpSmoother` with `class BarSmoother` and `public HpSmoother(int windowSize = 3)` with `public BarSmoother(int windowSize = 3)`.

- [ ] **Step 3: Update PartyOrchestrator field**

Modify `src/GamePartyHud/Party/PartyOrchestrator.cs:33`:

```csharp
private readonly BarSmoother _smoother = new(windowSize: 3);
```

- [ ] **Step 4: Rename test file**

```bash
git mv tests/GamePartyHud.Tests/Capture/HpSmootherTests.cs tests/GamePartyHud.Tests/Capture/BarSmootherTests.cs
```

- [ ] **Step 5: Update test class and references**

In `tests/GamePartyHud.Tests/Capture/BarSmootherTests.cs`, replace `class HpSmootherTests` with `class BarSmootherTests`, and replace every occurrence of `HpSmoother` → `BarSmoother`. Verify with Grep that no `HpSmoother` references remain anywhere in the repo.

- [ ] **Step 6: Build and test**

```bash
dotnet build
dotnet test --filter "FullyQualifiedName~BarSmootherTests"
```

Expected: `BarSmootherTests` runs and passes 9/9 tests. Full `dotnet test` run still has the pre-existing real-image regression failures from Task 1.

- [ ] **Step 7: Commit**

```bash
git add src/GamePartyHud/ tests/GamePartyHud.Tests/
git commit -m "refactor: rename HpSmoother to BarSmoother

Smoothing applies to any bar's reading, not just HP. Renaming now so
future stamina/mana support reuses the same type.
"
```

---

### Task 3: Rename `HpBarDetector` → `BarDetector`

**Files:**
- Move: `src/GamePartyHud/Capture/HpBarDetector.cs` → `src/GamePartyHud/Capture/BarDetector.cs`
- Modify: `src/GamePartyHud/Capture/BarDetector.cs` (class name)
- Move: `tests/GamePartyHud.Tests/Capture/HpBarDetectorTests.cs` → `tests/GamePartyHud.Tests/Capture/BarDetectorTests.cs`
- Modify: `tests/GamePartyHud.Tests/Capture/BarDetectorTests.cs` (class name + references)

`HpBarDetector` has no callers in `src/` outside its tests — the calibration wizard is region-drag based, not auto-detect. Verify with Grep.

- [ ] **Step 1: Rename source file**

```bash
git mv src/GamePartyHud/Capture/HpBarDetector.cs src/GamePartyHud/Capture/BarDetector.cs
```

- [ ] **Step 2: Update class name**

In `src/GamePartyHud/Capture/BarDetector.cs:13`, replace `public static class HpBarDetector` with `public static class BarDetector`.

- [ ] **Step 3: Rename test file**

```bash
git mv tests/GamePartyHud.Tests/Capture/HpBarDetectorTests.cs tests/GamePartyHud.Tests/Capture/BarDetectorTests.cs
```

- [ ] **Step 4: Update test class and references**

In `tests/GamePartyHud.Tests/Capture/BarDetectorTests.cs`: replace `class HpBarDetectorTests` with `class BarDetectorTests`, and replace every `HpBarDetector.FindTopBar` with `BarDetector.FindTopBar`.

- [ ] **Step 5: Verify no stragglers**

```
Grep pattern: "HpBarDetector" — expect 0 matches.
```

- [ ] **Step 6: Build and test**

```bash
dotnet build
dotnet test --filter "FullyQualifiedName~BarDetectorTests"
```

Expected: 5/5 tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/GamePartyHud/ tests/GamePartyHud.Tests/
git commit -m "refactor: rename HpBarDetector to BarDetector

The detector finds the tallest horizontal coloured band in a region —
not HP-specific. Same future-proofing as BarSmoother.
"
```

---

## Phase B — Algorithm change

This phase rewrites the analyzer's predicate, renames `HpBarAnalyzer` → `BarAnalyzer`, refreshes the real-image regression data, and tunes thresholds against the new sample set.

### Task 4: Refresh real-image test sample table to match the new examples

**Why first:** The user replaced the example PNGs in `tests/GamePartyHud.Tests/Capture/HpBarExamples/`. The current `SampleImageRegressionTests` and `SampleImageDiagnosticTests` reference the old filenames (`HP_BAR_4_PER_CENT.png` etc.) which no longer exist on disk. Updating the tables exposes the algorithm's failure on the new images, giving us a clear TDD red signal that Task 7 makes green.

**Files:**
- Modify: `tests/GamePartyHud.Tests/Capture/SampleImageRegressionTests.cs:24-38`
- Modify: `tests/GamePartyHud.Tests/Capture/SampleImageDiagnosticTests.cs:24-38`

- [ ] **Step 1: Update `SampleImageRegressionTests.Samples`**

In `tests/GamePartyHud.Tests/Capture/SampleImageRegressionTests.cs`, replace lines 24–38 (the `Samples` declaration) with:

```csharp
public static readonly TheoryData<string, float> Samples = new()
{
    { "HP_BAR_5_PER_CENT.png",   0.05f },
    { "HP_BAR_11_PER_CENT.png",  0.11f },
    { "HP_BAR_18_PER_CENT.png",  0.18f },
    { "HP_BAR_26_PER_CENT.png",  0.26f },
    { "HP_BAR_33_PER_CENT.png",  0.33f },
    { "HP_BAR_42_PER_CENT.png",  0.42f },
    { "HP_BAR_50_PER_CENT.png",  0.50f },
    { "HP_BAR_62_PER_CENT.png",  0.62f },
    { "HP_BAR_73_PER_CENT.png",  0.73f },
    { "HP_BAR_84_PER_CENT.png",  0.84f },
    { "HP_BAR_90_PER_CENT.png",  0.90f },
    { "HP_BAR_100_PER_CENT.png", 1.00f },
};
```

- [ ] **Step 2: Update `SampleImageDiagnosticTests.Samples`**

In `tests/GamePartyHud.Tests/Capture/SampleImageDiagnosticTests.cs`, replace lines 24–38 (the `Samples` field) with:

```csharp
public static readonly (string File, float Expected)[] Samples =
{
    ("HP_BAR_5_PER_CENT.png",   0.05f),
    ("HP_BAR_11_PER_CENT.png",  0.11f),
    ("HP_BAR_18_PER_CENT.png",  0.18f),
    ("HP_BAR_26_PER_CENT.png",  0.26f),
    ("HP_BAR_33_PER_CENT.png",  0.33f),
    ("HP_BAR_42_PER_CENT.png",  0.42f),
    ("HP_BAR_50_PER_CENT.png",  0.50f),
    ("HP_BAR_62_PER_CENT.png",  0.62f),
    ("HP_BAR_73_PER_CENT.png",  0.73f),
    ("HP_BAR_84_PER_CENT.png",  0.84f),
    ("HP_BAR_90_PER_CENT.png",  0.90f),
    ("HP_BAR_100_PER_CENT.png", 1.00f),
};
```

- [ ] **Step 3: Run the regression tests**

```bash
dotnet test --filter "FullyQualifiedName~SampleImage" --logger "console;verbosity=detailed"
```

Expected: tests now load files successfully (no `FileNotFoundException`) but most assertions fail because the OLD red-pixel analyzer mis-reads the new bar UI. Note the per-sample errors — they're the baseline that Task 7 must improve.

- [ ] **Step 4: Commit**

```bash
git add tests/GamePartyHud.Tests/Capture/SampleImageRegressionTests.cs \
        tests/GamePartyHud.Tests/Capture/SampleImageDiagnosticTests.cs
git commit -m "test(capture): point regression suite at refreshed HP bar samples

The example PNGs in tests/.../HpBarExamples/ were updated for the
post-UI-update bar (commit bf117b0). Test data now references the new
filenames and percentages. The legacy red-pixel analyzer fails most
assertions against the new images — to be fixed in the missing-pixel
redesign.
"
```

---

### Task 5: Rename `HpBarAnalyzer` → `BarAnalyzer` (class only, behaviour preserved)

**Why isolate this:** The class rename touches every caller. Keeping it separate from the algorithm change makes Task 6/7 a focused diff against a familiar class name.

**Files:**
- Move: `src/GamePartyHud/Capture/HpBarAnalyzer.cs` → `src/GamePartyHud/Capture/BarAnalyzer.cs`
- Modify: `src/GamePartyHud/Capture/BarAnalyzer.cs` (class name; keep `IsFilledPixel`, constants, and `Analyze` body unchanged for now)
- Modify: `src/GamePartyHud/Party/PartyOrchestrator.cs:32, 204` (field type and `IsFilledPixel` call)
- Move: `tests/GamePartyHud.Tests/Capture/HpBarAnalyzerTests.cs` → `tests/GamePartyHud.Tests/Capture/BarAnalyzerTests.cs`
- Modify: `tests/GamePartyHud.Tests/Capture/BarAnalyzerTests.cs` (class name + every `new HpBarAnalyzer()` site)
- Modify: `tests/GamePartyHud.Tests/Capture/ImageLoader.cs:13` (doc-comment `<see cref="GamePartyHud.Capture.HpBarAnalyzer.Analyze"/>` → `BarAnalyzer.Analyze`)
- Modify: `tests/GamePartyHud.Tests/Capture/SampleImageRegressionTests.cs:48, 64` (`new HpBarAnalyzer()` sites)
- Modify: `tests/GamePartyHud.Tests/Capture/SampleImageDiagnosticTests.cs:92` (`new HpBarAnalyzer()` site)

- [ ] **Step 1: Rename source file**

```bash
git mv src/GamePartyHud/Capture/HpBarAnalyzer.cs src/GamePartyHud/Capture/BarAnalyzer.cs
```

- [ ] **Step 2: Update class name**

In `src/GamePartyHud/Capture/BarAnalyzer.cs:22`, replace `public sealed class HpBarAnalyzer` with `public sealed class BarAnalyzer`. Update the doc-comment `<see cref="HpBarAnalyzer.IsFilledPixel"/>` if present (line ~38) to `<see cref="BarAnalyzer.IsFilledPixel"/>` or remove the cref entirely.

- [ ] **Step 3: Update orchestrator references**

In `src/GamePartyHud/Party/PartyOrchestrator.cs`:
- Line 32: `private readonly HpBarAnalyzer _analyzer = new();` → `private readonly BarAnalyzer _analyzer = new();`
- Line 204: `if (HpBarAnalyzer.IsFilledPixel(hsv)) matches++;` → `if (BarAnalyzer.IsFilledPixel(hsv)) matches++;`

- [ ] **Step 4: Rename test file**

```bash
git mv tests/GamePartyHud.Tests/Capture/HpBarAnalyzerTests.cs tests/GamePartyHud.Tests/Capture/BarAnalyzerTests.cs
```

- [ ] **Step 5: Update test class and references**

In `tests/GamePartyHud.Tests/Capture/BarAnalyzerTests.cs`: replace `class HpBarAnalyzerTests` with `class BarAnalyzerTests`. Replace every `new HpBarAnalyzer()` with `new BarAnalyzer()`.

- [ ] **Step 6: Update remaining references**

- `tests/GamePartyHud.Tests/Capture/ImageLoader.cs:13` doc-comment: `<see cref="GamePartyHud.Capture.HpBarAnalyzer.Analyze"/>` → `<see cref="GamePartyHud.Capture.BarAnalyzer.Analyze"/>`
- `tests/GamePartyHud.Tests/Capture/SampleImageRegressionTests.cs:48, 64`: `new HpBarAnalyzer()` → `new BarAnalyzer()`
- `tests/GamePartyHud.Tests/Capture/SampleImageDiagnosticTests.cs:92`: `new HpBarAnalyzer()` → `new BarAnalyzer()`

- [ ] **Step 7: Verify no stragglers**

```
Grep pattern: "HpBarAnalyzer" — expect 0 matches.
```

- [ ] **Step 8: Build and run unit tests (excluding the flaky regression suite)**

```bash
dotnet build
dotnet test --filter "FullyQualifiedName!~SampleImage"
```

Expected: every test except the SampleImage* suites passes. The SampleImage suite still fails (Task 4's red signal); we exclude it here only to confirm the rename didn't break anything else.

- [ ] **Step 9: Commit**

```bash
git add src/GamePartyHud/ tests/GamePartyHud.Tests/
git commit -m "refactor: rename HpBarAnalyzer to BarAnalyzer

Class-only rename ahead of the missing-pixel algorithm rewrite.
IsFilledPixel and Analyze behaviour unchanged.
"
```

---

### Task 6: Add `BarAnalyzer.IsMissingPixel` with unit tests (alongside existing `IsFilledPixel`)

**Why incremental:** Adding the new predicate alongside the old one lets us validate it independently before swapping `Analyze` over.

**Files:**
- Modify: `src/GamePartyHud/Capture/BarAnalyzer.cs` (add new constants and method; existing members untouched)
- Modify: `tests/GamePartyHud.Tests/Capture/BarAnalyzerTests.cs` (append new test cases)

- [ ] **Step 1: Write the failing tests for `IsMissingPixel`**

Append to `tests/GamePartyHud.Tests/Capture/BarAnalyzerTests.cs`, just before the closing brace of the `BarAnalyzerTests` class:

```csharp
[Theory]
// Pure black frame border — V=0 → not missing
[InlineData((byte)0,   (byte)0,   (byte)0,   false)]
// Dark grey empty bar — S=0, V≈0.16 → missing
[InlineData((byte)40,  (byte)40,  (byte)40,  true)]
// Light grey end-cap — S=0, V≈0.63 → missing
[InlineData((byte)160, (byte)160, (byte)160, true)]
// Near-white text glyph — V≈0.96 → not missing (excluded by V upper bound)
[InlineData((byte)245, (byte)245, (byte)245, false)]
// Saturated red bar fill — S=1 → not missing (filled pixel, excluded by S upper bound)
[InlineData((byte)0,   (byte)0,   (byte)220, false)]
// Anti-alias blend pixel (red leaking into grey) — S≈0.5 → not missing
[InlineData((byte)80,  (byte)40,  (byte)40,  false)]
public void IsMissingPixel_ClassifiesBoundaryCases(byte b, byte g, byte r, bool expected)
{
    var hsv = Hsv.FromBgra(b, g, r);
    Assert.Equal(expected, BarAnalyzer.IsMissingPixel(hsv));
}
```

- [ ] **Step 2: Run the new test to verify it fails to compile**

```bash
dotnet test --filter "FullyQualifiedName~IsMissingPixel_ClassifiesBoundaryCases"
```

Expected: build error — `'BarAnalyzer' does not contain a definition for 'IsMissingPixel'`.

- [ ] **Step 3: Add the new method and constants to `BarAnalyzer`**

In `src/GamePartyHud/Capture/BarAnalyzer.cs`, **add** the following inside the class (above `IsFilledPixel`):

```csharp
/// <summary>Maximum saturation for a pixel to count as part of the bar's empty/missing region.</summary>
public const float MissingMaxSaturation = 0.20f;

/// <summary>Minimum value for a pixel to count as missing — excludes the pure-black frame border.</summary>
public const float MissingMinValue = 0.05f;

/// <summary>Maximum value for a pixel to count as missing — excludes near-white text glyphs that overlay the bar.</summary>
public const float MissingMaxValue = 0.70f;

/// <summary>
/// True if <paramref name="hsv"/> looks like a pixel from the empty/missing portion of the
/// bar — desaturated grey within a bounded value range. Excludes the bar's frame border
/// (pure black, V&lt;<see cref="MissingMinValue"/>) and any overlay text glyphs (near-white,
/// V&gt;<see cref="MissingMaxValue"/>) so they are classified as "neither filled nor missing"
/// rather than falsely counted as missing.
/// </summary>
public static bool IsMissingPixel(Hsv hsv)
{
    return hsv.S <= MissingMaxSaturation
        && hsv.V >= MissingMinValue
        && hsv.V <= MissingMaxValue;
}
```

- [ ] **Step 4: Run the test to verify it passes**

```bash
dotnet test --filter "FullyQualifiedName~IsMissingPixel_ClassifiesBoundaryCases"
```

Expected: 6/6 inline-data cases PASS.

- [ ] **Step 5: Commit**

```bash
git add src/GamePartyHud/Capture/BarAnalyzer.cs \
        tests/GamePartyHud.Tests/Capture/BarAnalyzerTests.cs
git commit -m "feat(capture): add BarAnalyzer.IsMissingPixel classifier

Color-agnostic predicate for the bar's empty/missing region: low
saturation, value bounded above by white-text exclusion and below by
black-frame exclusion. Will replace IsFilledPixel inside Analyze in the
next commit.
"
```

---

### Task 7: Replace `Analyze` body with missing-pixel logic; remove `IsFilledPixel`; update orchestrator; port synthetic tests

This is the green-phase task. After it, every regression sample reads correctly within ±3% and MAE < 1%.

**Files:**
- Modify: `src/GamePartyHud/Capture/BarAnalyzer.cs` (rewrite `Analyze`, drop `IsFilledPixel` and old constants, update class doc-comment)
- Modify: `src/GamePartyHud/Party/PartyOrchestrator.cs:186-220` (`LogTick` — switch classifier, rename bins)
- Modify: `tests/GamePartyHud.Tests/Capture/BarAnalyzerTests.cs` (delete `Analyze_IsIndependentOfCalibratedFullColor`; verify all other synthetic tests still pass)

- [ ] **Step 1: Rewrite `BarAnalyzer.Analyze`**

Replace the entire contents of `src/GamePartyHud/Capture/BarAnalyzer.cs` with:

```csharp
using System;

namespace GamePartyHud.Capture;

/// <summary>
/// Reads a bar's fill percentage from a captured BGRA bitmap by classifying each
/// column as "missing" (the bar's empty/grey background) or "not missing" (the
/// bar's filled portion or any overlay text), then finding the transition from
/// the anchor side. Current fraction = 1 - (missing columns / total columns).
///
/// The pixel classifier is colour-agnostic: it identifies any sufficiently
/// desaturated, mid-value pixel as part of the bar's empty region, regardless
/// of what colour the filled portion would be. This is robust to:
///   - text overlays in the middle of the bar (near-white, excluded by the V upper bound),
///   - the dark frame border above/below the bar (pure black, excluded by the V lower bound),
///   - users picking a region 1–2 px outside the bar,
///   - subtle vertical gradient inside both the filled and the empty regions,
/// and naturally extends to non-red bars (stamina, mana) without per-bar tuning.
/// </summary>
public sealed class BarAnalyzer
{
    private const int StableRun = 3;

    /// <summary>Maximum saturation for a pixel to count as part of the bar's empty/missing region.</summary>
    public const float MissingMaxSaturation = 0.20f;

    /// <summary>Minimum value for a pixel to count as missing — excludes the pure-black frame border.</summary>
    public const float MissingMinValue = 0.05f;

    /// <summary>Maximum value for a pixel to count as missing — excludes near-white text glyphs that overlay the bar.</summary>
    public const float MissingMaxValue = 0.70f;

    /// <summary>
    /// True if <paramref name="hsv"/> looks like a pixel from the empty/missing portion of the
    /// bar — desaturated grey within a bounded value range. Excludes the bar's frame border
    /// (pure black, V&lt;<see cref="MissingMinValue"/>) and any overlay text glyphs (near-white,
    /// V&gt;<see cref="MissingMaxValue"/>) so they are classified as "neither filled nor missing"
    /// rather than falsely counted as missing.
    /// </summary>
    public static bool IsMissingPixel(Hsv hsv)
    {
        return hsv.S <= MissingMaxSaturation
            && hsv.V >= MissingMinValue
            && hsv.V <= MissingMaxValue;
    }

    public float Analyze(ReadOnlySpan<byte> bgra, int width, int height, HpCalibration cal)
    {
        if (width <= 0 || height <= 0) return 0f;
        if (bgra.Length < width * height * 4) throw new ArgumentException("bgra too small", nameof(bgra));

        // A column is classified as "missing" if at least ~20% of its rows are
        // missing pixels. The 20% threshold tolerates white text glyphs in the
        // middle and the frame at top/bottom while still firmly rejecting stray
        // single-row noise.
        int sampleRows = height;
        int minMatches = Math.Max(2, sampleRows / 5);

        bool ltr = cal.Direction == FillDirection.LTR;

        var colMissing = new bool[width];
        bool anyMissing = false;
        for (int i = 0; i < width; i++)
        {
            int col = ltr ? i : (width - 1 - i);
            int matches = 0;
            for (int y = 0; y < height; y++)
            {
                int idx = (y * width + col) * 4;
                var hsv = Hsv.FromBgra(bgra[idx], bgra[idx + 1], bgra[idx + 2]);
                if (IsMissingPixel(hsv)) matches++;
            }
            colMissing[i] = matches >= minMatches;
            anyMissing |= colMissing[i];
        }

        // Fast path: full bar (no missing columns at all).
        if (!anyMissing) return 1f;

        // Pass 2: establish the "stable initial state" — the first run of StableRun
        // consecutive columns with the same missing/not-missing classification,
        // starting from the anchor side. Resilient to 1–2px anti-alias noise.
        int stableStart = -1;
        bool stableMissingState = false;
        for (int i = 0; i + StableRun - 1 < width; i++)
        {
            bool allSame = true;
            for (int k = 1; k < StableRun && allSame; k++)
                if (colMissing[i + k] != colMissing[i]) allSame = false;
            if (allSame)
            {
                stableStart = i;
                stableMissingState = colMissing[i];
                break;
            }
        }
        if (stableStart == -1) return 0f;

        // If the stable initial state is already "missing", the bar is empty.
        if (stableMissingState) return 0f;

        // Pass 3: from the stable initial "not missing" state, scan for StableRun
        // consecutive missing columns. The first column of that run is the
        // transition. filledFraction = transition / width (with axis flipped for RTL).
        int runMissing = 0;
        int transition = width;
        for (int i = stableStart; i < width; i++)
        {
            if (colMissing[i] != stableMissingState)
            {
                runMissing++;
                if (runMissing >= StableRun)
                {
                    transition = i - runMissing + 1;
                    break;
                }
            }
            else
            {
                runMissing = 0;
            }
        }

        return Math.Clamp((float)transition / width, 0f, 1f);
    }
}
```

Note the parameter type stays `HpCalibration` for now — Phase C renames it to `BarCalibration`.

- [ ] **Step 2: Update `PartyOrchestrator.LogTick`**

In `src/GamePartyHud/Party/PartyOrchestrator.cs`, replace lines 186–220 (the `LogTick` method) with:

```csharp
private void LogTick(HpCalibration cal, byte[] bgra, float raw, float smoothed)
{
    _tickCounter++;
    int w = cal.Region.W;
    int h = cal.Region.H;

    // Per-column missing-pixel count using the SAME classifier the analyzer uses.
    // Lets us see at-a-glance whether the capture pixels look like a real bar
    // (mostly bar columns with a tail of missing columns) or something else.
    int minMatches = Math.Max(2, h / 5);
    int barCols = 0, partial = 0, missingCols = 0;
    for (int x = 0; x < w; x++)
    {
        int matches = 0;
        for (int y = 0; y < h; y++)
        {
            int idx = (y * w + x) * 4;
            var hsv = Hsv.FromBgra(bgra[idx], bgra[idx + 1], bgra[idx + 2]);
            if (BarAnalyzer.IsMissingPixel(hsv)) matches++;
        }
        if (matches == 0) barCols++;
        else if (matches < minMatches) partial++;
        else missingCols++;
    }

    // Sample average HSV of the middle-third — sanity check that the capture
    // contains a bar (good) or something else (region-selection issue).
    var midAvg = CaptureDiagnostic.AverageHsv(bgra, w, h, w / 3, 2 * w / 3);

    Log.Info(
        $"PartyOrchestrator tick#{_tickCounter}: raw={raw:F3} smoothed={smoothed:F3} " +
        $"region={w}x{h}@({cal.Region.X},{cal.Region.Y}) " +
        $"cols {barCols}/{partial}/{missingCols} bar/partial/missing; " +
        $"mid-HSV H={midAvg.H:F0}° S={midAvg.S:F2} V={midAvg.V:F2}");
}
```

- [ ] **Step 3: Delete the no-longer-relevant synthetic test**

In `tests/GamePartyHud.Tests/Capture/BarAnalyzerTests.cs`, delete the `Analyze_IsIndependentOfCalibratedFullColor` test (lines 65–83 of the original file). The test guarded against a `FullColor`-pollution regression that can no longer happen — the analyzer no longer reads `FullColor`. (Phase C deletes the field entirely.)

- [ ] **Step 4: Run unit + synthetic tests**

```bash
dotnet test --filter "FullyQualifiedName~BarAnalyzerTests"
```

Expected: every remaining test passes, including:
- `Analyze_FullBar_Returns1` — synthetic bar with `(40,40,40)` empty colour. Empty=missing under the new criterion (S=0, V≈0.16); full bar has 0 missing columns → fast-path returns 1.0.
- `Analyze_EmptyBar_Returns0` — fully `(40,40,40)` buffer; every column is missing → stable initial state is missing → returns 0.
- `Analyze_PartialBar_WithinTwoPercent` (4 cases) — stable initial state is "not missing" (red columns); transition runs into a stable missing region; fraction = transition / width, ±2%.
- `Analyze_Rtl_InvertsReading` — same logic, anchor flipped to the right edge.
- `Analyze_NoMatchingPixels_Returns0` — entirely `(40,40,40)`, no red anywhere; all columns missing; returns 0.
- `Analyze_FullBarWithTextOverlay_ReturnsFull` — text glyphs at `(240,240,240)`, V≈0.94, excluded by `MissingMaxValue`. Text-row count toward "missing" stays at 0; non-text rows are red and not missing. Column tally has 0 missing rows → not-missing column. Bar has 0 missing columns → returns 1.0.
- `Analyze_Clamps_PercentToClosedUnit` — same as before; passes after rename.

If any of these fail, investigate before continuing — the new `Analyze` body has a bug.

- [ ] **Step 5: Run real-image regression**

```bash
dotnet test --filter "FullyQualifiedName~SampleImage" --logger "console;verbosity=detailed"
```

Expected: 12/12 per-sample assertions pass within ±3%; `AllSamples_MeanAbsoluteError_IsUnderOnePercent` passes with MAE < 1%. If any sample is outside tolerance, do not commit — proceed to Task 8 (tuning) first.

- [ ] **Step 6: Run full suite**

```bash
dotnet test
```

Expected: all green.

- [ ] **Step 7: Commit**

```bash
git add src/GamePartyHud/Capture/BarAnalyzer.cs \
        src/GamePartyHud/Party/PartyOrchestrator.cs \
        tests/GamePartyHud.Tests/Capture/BarAnalyzerTests.cs
git commit -m "feat(capture): swap BarAnalyzer to missing-pixel classifier

Replace the saturated-red predicate with the colour-agnostic
IsMissingPixel test. Per-column aggregation and stable-run transition
detection are unchanged; only the inner predicate flips. PartyOrchestrator
diagnostic log is updated to bin columns into bar/partial/missing.
The Analyze_IsIndependentOfCalibratedFullColor regression test is
removed since FullColor is no longer consulted.
"
```

---

### Task 8: Threshold tuning (only if Task 7 step 5 left any sample out of tolerance)

If Task 7's regression suite passed all assertions, **skip this task**.

If one or more samples were outside ±3% (or MAE ≥ 1%), tune `MissingMaxSaturation`, `MissingMinValue`, or `MissingMaxValue` until tolerances hold.

- [ ] **Step 1: Diagnose**

Run the diagnostic test and read its column dumps:

```bash
dotnet test --filter "FullyQualifiedName~SampleImageDiagnosticTests" --logger "console;verbosity=detailed"
```

The `PrintDimensionsAndPerRowAverageHsv` and `PrintPerColumnHsvForSeveralSamples` outputs show actual S and V values per column. Identify which sample's transition column has S/V values that don't match the criterion (e.g., S = 0.22 when threshold is 0.20).

- [ ] **Step 2: Adjust constants**

Change `MissingMaxSaturation`, `MissingMinValue`, or `MissingMaxValue` in `src/GamePartyHud/Capture/BarAnalyzer.cs` based on the observed values. Document the change as a comment.

- [ ] **Step 3: Re-run regression suite**

```bash
dotnet test --filter "FullyQualifiedName~SampleImage"
```

Repeat steps 1–3 until 12/12 pass and MAE < 1%.

- [ ] **Step 4: Update unit tests if a constant moved**

If `MissingMaxValue` (for example) had to widen to 0.75, the `IsMissingPixel_ClassifiesBoundaryCases` test data may need adjusting — particularly the `(245, 245, 245)` near-white case (V ≈ 0.96 still fails 0.75 → still "not missing"; safe). Check that the boundary cases still represent the intent.

- [ ] **Step 5: Commit**

```bash
git add src/GamePartyHud/Capture/BarAnalyzer.cs \
        tests/GamePartyHud.Tests/Capture/BarAnalyzerTests.cs
git commit -m "tune(capture): adjust missing-pixel thresholds to fit real samples

After running the diagnostic test against the new HpBarExamples set, the
[constant] threshold needed to widen from [old] to [new] to absorb
[brief description of the observation, e.g. 'the light-grey end-cap
which sits at V≈0.72 in this game's UI']. MAE across all 12 samples now
[X.X]%.
"
```

---

## Phase C — Calibration record cleanup

### Task 9: Slim `HpCalibration` → `BarCalibration`; delete `HsvTolerance`; simplify wizard

Single coherent task because the type-rename and field-removal cascade together — every caller updates in one commit.

**Files:**
- Move: `src/GamePartyHud/Capture/HpCalibration.cs` → `src/GamePartyHud/Capture/BarCalibration.cs`
- Modify: `src/GamePartyHud/Capture/BarCalibration.cs` (record signature)
- Delete: `src/GamePartyHud/Capture/HsvTolerance.cs`
- Modify: `src/GamePartyHud/Capture/BarAnalyzer.cs` (Analyze parameter type)
- Modify: `src/GamePartyHud/Config/AppConfig.cs:9` (field type only)
- Modify: `src/GamePartyHud/MainWindow.xaml.cs` (lines 99, 188-202, 217-238, 391 — wizard cleanup, drop SampleFullColor)
- Modify: `src/GamePartyHud/Party/PartyOrchestrator.cs:136, 138, 139, 142, 186` (`HpCalibration` → `BarCalibration` references)
- Modify: `tests/GamePartyHud.Tests/Capture/HsvTests.cs:39-60` (delete the two HsvTolerance tests)
- Modify: `tests/GamePartyHud.Tests/Capture/BarAnalyzerTests.cs:8-12` (RedLtr fixture)
- Modify: `tests/GamePartyHud.Tests/Capture/SampleImageRegressionTests.cs:74-108` (drop CalibrateFromFullSample, inline cal construction)
- Modify: `tests/GamePartyHud.Tests/Capture/SampleImageDiagnosticTests.cs:73-125` (drop SampleFullColor, inline cal construction)
- Modify: `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs:36-40` (HpCalibration construction)

- [ ] **Step 1: Rename `HpCalibration.cs` and slim the record**

```bash
git mv src/GamePartyHud/Capture/HpCalibration.cs src/GamePartyHud/Capture/BarCalibration.cs
```

Replace the contents of `src/GamePartyHud/Capture/BarCalibration.cs` with:

```csharp
namespace GamePartyHud.Capture;

public sealed record BarCalibration(
    CaptureRegion Region,
    FillDirection Direction);
```

- [ ] **Step 2: Delete `HsvTolerance.cs`**

```bash
git rm src/GamePartyHud/Capture/HsvTolerance.cs
```

- [ ] **Step 3: Drop the `HsvTolerance` tests from `HsvTests.cs`**

In `tests/GamePartyHud.Tests/Capture/HsvTests.cs`, delete the `Tolerance_HueWrapAround_IsSymmetric` and `Tolerance_SaturationAndValueDifferencesAreRespected` tests (lines 39–60). Keep the four `FromBgra_*` tests as-is. The final file should look like:

```csharp
using GamePartyHud.Capture;
using Xunit;

namespace GamePartyHud.Tests.Capture;

public class HsvTests
{
    [Fact]
    public void FromBgra_PureRed_ReturnsHueZero()
    {
        var hsv = Hsv.FromBgra(b: 0, g: 0, r: 255);
        Assert.Equal(0f, hsv.H);
        Assert.Equal(1f, hsv.S, 3);
        Assert.Equal(1f, hsv.V, 3);
    }

    [Fact]
    public void FromBgra_PureGreen_ReturnsHue120()
    {
        var hsv = Hsv.FromBgra(b: 0, g: 255, r: 0);
        Assert.Equal(120f, hsv.H, 3);
    }

    [Fact]
    public void FromBgra_PureBlue_ReturnsHue240()
    {
        var hsv = Hsv.FromBgra(b: 255, g: 0, r: 0);
        Assert.Equal(240f, hsv.H, 3);
    }

    [Fact]
    public void FromBgra_Black_ReturnsZeroValue()
    {
        var hsv = Hsv.FromBgra(0, 0, 0);
        Assert.Equal(0f, hsv.V);
        Assert.Equal(0f, hsv.S);
    }
}
```

- [ ] **Step 4: Update `BarAnalyzer.Analyze` parameter type**

In `src/GamePartyHud/Capture/BarAnalyzer.cs`, change the `Analyze` signature parameter `HpCalibration cal` → `BarCalibration cal`. The body reads only `cal.Direction`, which is unchanged on `BarCalibration`.

- [ ] **Step 5: Update `AppConfig.cs`**

In `src/GamePartyHud/Config/AppConfig.cs:9`, change:

```csharp
HpCalibration? HpCalibration,
```

to:

```csharp
BarCalibration? HpCalibration,
```

The field name `HpCalibration` is intentionally unchanged — it identifies which bar this calibration is for. The JSON key on disk stays `hpCalibration` (camelCase by `JsonSerializerDefaults.Web`).

- [ ] **Step 6: Update `MainWindow.xaml.cs` — wizard cleanup**

In `src/GamePartyHud/MainWindow.xaml.cs`:

**(a) Line 99 — type pattern stays the same** (the field name is unchanged, so this line needs no edit).

**(b) Lines 188–202 — replace the wizard's pixel-capture + SampleFullColor block.**

Replace lines 191–193:

```csharp
var bgra = await _ctl.Capture.CaptureBgraAsync(region).ConfigureAwait(true);
var fullColor = SampleFullColor(bgra, region.W, region.H);
var cal = new HpCalibration(region, fullColor, HsvTolerance.Default, FillDirection.LTR);
```

with:

```csharp
var cal = new BarCalibration(region, FillDirection.LTR);
```

The `await _ctl.Capture.CaptureBgraAsync(...)` call goes away — calibration is now purely geometric.

**(c) Lines 216–238 — delete the `SampleFullColor` private helper entirely.** It's no longer called.

**(d) Line 391 — no edit** (`_ctl.Config.HpCalibration is null` works the same; the field name is unchanged).

**(e) Drop `async` from the `OnPickRegion` signature** — the only `await` (the `CaptureBgraAsync` call) just got removed, and CS1998 will fire if the keyword stays:

Line 173: `private async void OnPickRegion(object sender, RoutedEventArgs e)` → `private void OnPickRegion(object sender, RoutedEventArgs e)`.

The XAML `Click="OnPickRegion"` binding works identically for `void` and `async void` handlers. Exception flow through the `try/catch/finally` is unchanged.

- [ ] **Step 7: Update `PartyOrchestrator.cs`**

In `src/GamePartyHud/Party/PartyOrchestrator.cs`:
- Line 136: `if (_cfg.HpCalibration is { } cal)` — no edit (pattern uses field name; the runtime type is now `BarCalibration` but C# infers it).
- Line 186: `private void LogTick(HpCalibration cal, byte[] bgra, float raw, float smoothed)` → `private void LogTick(BarCalibration cal, byte[] bgra, float raw, float smoothed)`.

- [ ] **Step 8: Update `BarAnalyzerTests.cs` fixtures**

In `tests/GamePartyHud.Tests/Capture/BarAnalyzerTests.cs:8-12`, replace:

```csharp
private static readonly HpCalibration RedLtr = new(
    Region: new HpRegion(0, 0, 0, 200, 10),
    FullColor: Hsv.FromBgra(b: 0, g: 0, r: 255),
    Tolerance: HsvTolerance.Default,
    Direction: FillDirection.LTR);
```

with:

```csharp
private static readonly BarCalibration RedLtr = new(
    Region: new CaptureRegion(0, 0, 0, 200, 10),
    Direction: FillDirection.LTR);
```

The `with`-expression that builds the RTL variant (around line 51) stays valid.

- [ ] **Step 9: Update `SampleImageRegressionTests.cs`**

In `tests/GamePartyHud.Tests/Capture/SampleImageRegressionTests.cs`:

- Delete the `CalibrateFromFullSample` private helper (lines 72–108).
- Replace each call site that uses `var cal = CalibrateFromFullSample();` (the two test methods) with the inline construction. Specifically, in `Analyze_MatchesFilenamePercentage_WithinTolerance`:

```csharp
[Theory]
[MemberData(nameof(Samples))]
public void Analyze_MatchesFilenamePercentage_WithinTolerance(string file, float expected)
{
    var (bgra, w, h) = ImageLoader.Load(ImageLoader.SamplePath(file));
    var cal = new BarCalibration(new CaptureRegion(0, 0, 0, w, h), FillDirection.LTR);
    var actual = new BarAnalyzer().Analyze(bgra, w, h, cal);

    Assert.InRange(actual, expected - Tolerance, expected + Tolerance);
}
```

And in `AllSamples_MeanAbsoluteError_IsUnderOnePercent`:

```csharp
[Fact]
public void AllSamples_MeanAbsoluteError_IsUnderOnePercent()
{
    float totalAbs = 0;
    int n = 0;
    foreach (var row in Samples)
    {
        var file = (string)row[0];
        var expected = (float)row[1];
        var (bgra, w, h) = ImageLoader.Load(ImageLoader.SamplePath(file));
        var cal = new BarCalibration(new CaptureRegion(0, 0, 0, w, h), FillDirection.LTR);
        var actual = new BarAnalyzer().Analyze(bgra, w, h, cal);
        totalAbs += Math.Abs(actual - expected);
        n++;
    }
    var mae = totalAbs / n;
    Assert.True(mae < 0.01f, $"Mean absolute error across {n} samples was {mae:P2}; expected <1%.");
}
```

Update the file-level doc comment (lines 7–18) to reflect the new approach: no calibration sampling, just geometric region.

- [ ] **Step 10: Update `SampleImageDiagnosticTests.cs`**

In `tests/GamePartyHud.Tests/Capture/SampleImageDiagnosticTests.cs`:

- Delete the `SampleFullColor` private helper (lines 102–125).
- Replace `CurrentAnalyzer_AgainstAllSamples_ShowsError` (lines 69–100) with:

```csharp
[Fact]
public void CurrentAnalyzer_AgainstAllSamples_ShowsError()
{
    _out.WriteLine($"Thresholds: Smax={BarAnalyzer.MissingMaxSaturation:F2}, Vmin={BarAnalyzer.MissingMinValue:F2}, Vmax={BarAnalyzer.MissingMaxValue:F2}");
    _out.WriteLine("");
    _out.WriteLine("file".PadRight(30) + "expected  actual   diff");

    float totalAbsDiff = 0;
    int n = 0;
    foreach (var (file, expected) in Samples)
    {
        var (bgra, w, h) = ImageLoader.Load(ImageLoader.SamplePath(file));
        var cal = new BarCalibration(new CaptureRegion(0, 0, 0, w, h), FillDirection.LTR);
        var actual = new BarAnalyzer().Analyze(bgra, w, h, cal);
        float diff = actual - expected;
        totalAbsDiff += Math.Abs(diff);
        n++;
        _out.WriteLine($"{file.PadRight(30)}{expected:P0}     {actual:P0}     {diff:+0.00;-0.00}");
    }
    _out.WriteLine("");
    _out.WriteLine($"Mean absolute error: {(totalAbsDiff / n):P1}");
}
```

`PrintDimensionsAndPerRowAverageHsv` and `PrintPerColumnHsvForSeveralSamples` are unchanged — they don't use calibration at all.

- [ ] **Step 11: Update `ConfigStoreTests.cs` round-trip**

In `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs:36-40`, replace:

```csharp
HpCalibration = new HpCalibration(
    new HpRegion(0, 10, 20, 300, 18),
    new Hsv(5, 0.9f, 0.7f),
    new HsvTolerance(15, 0.25f, 0.25f),
    FillDirection.LTR),
NicknameRegion = new HpRegion(0, 10, 0, 300, 20),
```

with:

```csharp
HpCalibration = new BarCalibration(
    new CaptureRegion(0, 10, 20, 300, 18),
    FillDirection.LTR),
NicknameRegion = new CaptureRegion(0, 10, 0, 300, 20),
```

- [ ] **Step 12: Verify no stragglers**

```
Grep pattern: "HpCalibration\b" — every match should be a *field name* (e.g. "_config.HpCalibration", "cfg.HpCalibration", "HpCalibration =", JSON key "hpCalibration"), not a *type name*. Any `new HpCalibration(...)` call site is a missed edit.

Grep pattern: "HsvTolerance" — expect 0 matches.

Grep pattern: "SampleFullColor" — expect 0 matches.
```

- [ ] **Step 13: Build and run all tests**

```bash
dotnet build
dotnet test
```

Expected: 0 errors, 0 warnings, all tests green.

- [ ] **Step 14: Commit**

```bash
git add src/GamePartyHud/ tests/GamePartyHud.Tests/
git commit -m "refactor: slim HpCalibration to BarCalibration

Drop the dead FullColor and HsvTolerance fields — neither has been
consulted by the analyzer since the calibration-free classifier landed,
and they are definitively unused under the missing-pixel redesign.
Delete HsvTolerance.cs and the corresponding HsvTests cases. Simplify
the calibration wizard: no more pixel sampling; the region-pick result
becomes a BarCalibration directly. AppConfig field name is preserved
('HpCalibration' identifies which bar) so on-disk JSON keys are stable.
"
```

---

## Phase D — Migration test

### Task 10: Lock in old-config-JSON deserialisation

**Files:**
- Modify: `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs` (append a new test case)

This test documents and guards the migration behaviour: an `hpCalibration` block on disk that still contains the old `fullColor` and `tolerance` keys must deserialise into a `BarCalibration` (those keys silently ignored), and the next `Save` call must produce JSON without them.

- [ ] **Step 1: Write the failing test**

Append to `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs`, before the closing brace of `class ConfigStoreTests`:

```csharp
[Fact]
public void Load_OldShapeHpCalibrationJson_DropsFullColorAndTolerance()
{
    // A config.json saved by a build before the BarCalibration redesign
    // contains hpCalibration.fullColor and hpCalibration.tolerance objects.
    // The new BarCalibration record only has Region and Direction, so those
    // two extra JSON keys must be silently ignored on load (System.Text.Json
    // default behaviour with JsonSerializerDefaults.Web). The next Save
    // re-serialises without them.
    File.WriteAllText(_tmp, """
{
  "hpCalibration": {
    "region": { "monitor": 0, "x": 10, "y": 20, "w": 300, "h": 18 },
    "fullColor": { "h": 5, "s": 0.9, "v": 0.7 },
    "tolerance": { "h": 15, "s": 0.25, "v": 0.25 },
    "direction": "LTR"
  },
  "nicknameRegion": null,
  "nickname": "Test",
  "role": "Tank",
  "hudPosition": { "x": 0, "y": 0, "monitor": 0 },
  "hudLocked": true,
  "lastPartyId": null,
  "pollIntervalMs": 2000,
  "relayUrl": ""
}
""");

    var store = new ConfigStore(_tmp);
    var loaded = store.Load();

    Assert.NotNull(loaded.HpCalibration);
    Assert.Equal(new CaptureRegion(0, 10, 20, 300, 18), loaded.HpCalibration!.Region);
    Assert.Equal(FillDirection.LTR, loaded.HpCalibration.Direction);

    // Round-trip: save and re-read; the reborn JSON must not contain the
    // legacy keys.
    store.Save(loaded);
    var reborn = File.ReadAllText(_tmp);
    Assert.DoesNotContain("\"fullColor\"", reborn);
    Assert.DoesNotContain("\"tolerance\"", reborn);
}
```

- [ ] **Step 2: Run the test**

```bash
dotnet test --filter "FullyQualifiedName~Load_OldShapeHpCalibrationJson"
```

Expected: PASS. (`System.Text.Json`'s `JsonSerializerDefaults.Web` configuration in `ConfigStore.cs:11` ignores unknown properties by default. If this test FAILS, that default has changed somewhere — investigate before proceeding.)

- [ ] **Step 3: Run full suite**

```bash
dotnet test
```

Expected: all green.

- [ ] **Step 4: Commit**

```bash
git add tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs
git commit -m "test(config): lock in BarCalibration migration from old JSON

Documents and guards the silent-ignore behaviour for the legacy
hpCalibration.fullColor and hpCalibration.tolerance keys. A future
JsonSerializerOptions tweak that flips unknown-property handling to
strict would now break this test instead of silently corrupting reads.
"
```

---

## Phase E — Manual verification

### Task 11: Live game session smoke test

Per [CLAUDE.md](../../../CLAUDE.md), UI/runtime correctness is verified manually. This task is mandatory before declaring the feature complete.

- [ ] **Step 1: Build a release-style binary**

```bash
dotnet publish src/GamePartyHud/GamePartyHud.csproj -c Release
```

- [ ] **Step 2: Launch the app and run the calibration wizard**

Open the published exe. Click "Pick HP bar region". Drag a tight box around the in-game HP bar at any HP level (full or partial). Confirm the saved-region status banner shows the dimensions you expect.

- [ ] **Step 3: Watch HP track from full to low and back**

Stay in-game for a fight or training session that drives HP from ~100% down to ~5–10% and back to 100%. On the HUD overlay, confirm:
- The bar's filled fraction visually matches the in-game HP at every step.
- No spikes, drops, or stuck readings.
- At 100%, the HUD bar reads exactly 100%.
- At 0% (or near it), the HUD bar reads 0%.

If you observe drift or spikes, capture a screenshot of the in-game HP at the moment of mismatch (with the HUD overlay visible) and re-tune in Task 8.

- [ ] **Step 4: Sanity-check the diagnostic log**

Open `%AppData%\GamePartyHud\app.log`. Look for `PartyOrchestrator tick#` entries. The `cols X/Y/Z bar/partial/missing` figures should look reasonable: at full HP, mostly `bar` columns and ~0 `missing`; at low HP, mostly `missing` columns; transition columns in `partial` are 1–3 per tick.

- [ ] **Step 5: No code change to commit (verification-only)**

If everything looks correct, the task is done — no commit needed. If you found a real issue, branch back to the appropriate earlier task and fix it.

---

## Self-review

(performed against [the spec](../specs/2026-05-08-bar-detection-missing-pixel-redesign.md))

**Spec coverage:**
- §1 (algorithm): Tasks 6, 7 ✓ — `IsMissingPixel` predicate + `Analyze` rewrite + per-column ≥20% rule + stable-run transition + edge cases.
- §2.1 (renames): Tasks 1, 2, 3, 5, 9 ✓ — every entry in the rename table is covered.
- §2.2 (deletions): Task 9 step 2 ✓ — `HsvTolerance.cs` deleted.
- §2.3 (BarCalibration shape): Task 9 step 1 ✓.
- §2.4 (AppConfig change): Task 9 step 5 ✓ — type rename only, field name preserved.
- §2.5 (config migration): Task 10 ✓ — explicit test case.
- §3 (calibration wizard): Task 9 step 6 ✓ — pixel-capture + `SampleFullColor` removed; geometric-only flow.
- §4.1–4.6 (call sites): Task 1 (`CaptureRegion`), Task 5 (`BarAnalyzer`), Task 7 (`LogTick`), Task 9 (`BarCalibration`) ✓.
- §5.1 (IsMissingPixel unit tests): Task 6 ✓ — six boundary cases.
- §5.2 (synthetic-bitmap tests): Task 7 step 4 ✓ — explicit verification each existing test still passes; `Analyze_IsIndependentOfCalibratedFullColor` deletion ✓.
- §5.3 (real-image regression): Task 4 (data refresh) + Task 9 (cal slimming) ✓.
- §5.4 (tuning gate): Task 8 ✓.
- §5.5 (`ConfigStoreTests` migration): Task 10 ✓.
- §5.6 (manual verification): Task 11 ✓.

**Type consistency:**
- `BarCalibration(CaptureRegion Region, FillDirection Direction)` is the same shape in §2.3, in Task 9 step 1, and in every Task 9 callsite update.
- `IsMissingPixel(Hsv hsv) -> bool` and constants `MissingMaxSaturation`/`MissingMinValue`/`MissingMaxValue` are the same in Task 6 step 3 and Task 7 step 1.
- `BarAnalyzer.Analyze(ReadOnlySpan<byte>, int, int, BarCalibration)` is the same after Task 9 step 4 as referenced by every test caller.

No placeholders, no TBDs, no contradictions.
