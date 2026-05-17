# Party HUD Settings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the `SettingsWindow` modal with a Party HUD section: per-bar color picker (HP / Stamina / Mana) using an inline SV+hue palette, an opacity slider for the HUD's dark panel, and a Reset button that wipes position, scale, lock state, colors, and opacity to defaults.

**Architecture:** Four new global per-machine `AppConfig` fields (`HpBarColor`, `StaminaBarColor`, `ManaBarColor`, `HudBackgroundOpacity`) drive a new `HudTheme` `INotifyPropertyChanged` view-model. `HudWindow.RootBorder` and `MemberCard`'s three bar gradients bind into the theme so colour / opacity changes repaint the HUD on the next render tick. A new `BarColorPicker` UserControl hosts a swatch + hex label that opens a popup with an SV square, vertical hue strip, hex input, and preview gradient. The `SettingsWindow` body is rewritten to a labelled "Party HUD" section with the three pickers, an opacity slider, and the relabelled "Reset to defaults" button.

**Tech Stack:** C# .NET 8 / WPF, no new package dependencies. Pure WPF (`Popup`, `LinearGradientBrush`, `Rectangle`, `Canvas`, `Slider`, `DispatcherTimer` not needed). xUnit for the new `HudColor` pure-logic tests.

**Testing approach:** Per `CLAUDE.md`, UI surfaces (the picker popup, SettingsWindow layout, HUD live-paint) are manually verified — no flaky UI automation. The new pure-logic surface (`HudColor.TryParse` / `Format` / `RgbToHsv` / `HsvToRgb` / `Darken`) gets full TDD coverage. `ConfigStore` round-trip + two new clamp tests cover the four new `AppConfig` fields.

**Reference spec:** [`docs/superpowers/specs/2026-05-17-hud-settings-design.md`](../specs/2026-05-17-hud-settings-design.md)

---

## File Structure

**Created:**
- `src/GamePartyHud/Settings/HudColor.cs` — pure-logic hex parse/format, RGB↔HSV, Darken.
- `src/GamePartyHud/Settings/BarColorPicker.xaml` + `.xaml.cs` — swatch + popup with SV square + hue strip + hex input + preview.
- `src/GamePartyHud/Hud/HudTheme.cs` — `INotifyPropertyChanged` view-model wrapping the four new AppConfig fields as `Brush` properties.
- `tests/GamePartyHud.Tests/Settings/HudColorTests.cs` — 8 unit tests covering one rule each + happy paths.

**Modified:**
- `src/GamePartyHud/Config/AppConfig.cs` — 4 new fields with hex-string defaults.
- `src/GamePartyHud/Config/ConfigStore.cs` — new `SanitiseHudBackgroundOpacity` helper; applied in `Load`.
- `src/GamePartyHud/MainWindow.xaml.cs` — `IController` interface: rename `ResetHudLayout` → `ResetHudToDefaults`, add `HudTheme HudTheme { get; }` property.
- `src/GamePartyHud/App.xaml.cs` — construct `HudTheme`; expose via `IController`; refresh on `UpdateConfig`; set `HudWindow.DataContext`; rename + extend Reset helper.
- `src/GamePartyHud/SettingsWindow.xaml` + `.xaml.cs` — body rewrite (3 pickers + slider + relabelled reset button).
- `src/GamePartyHud/Hud/HudWindow.xaml` — `RootBorder.Background` becomes `{Binding HudTheme.PanelBackgroundBrush}`.
- `src/GamePartyHud/Hud/MemberCard.xaml` — three hardcoded `LinearGradientBrush` blocks become `{Binding}`s with `RelativeSource AncestorType=Window`.
- `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs` — round-trip fixture grows 4 fields; 2 new opacity-clamp tests.

**Untouched:** Capture pipeline, networking, party state, preset selector, tray, Discord notifier, calibration window, Bars section of MainWindow.

---

## Task 1: `HudColor` pure-logic helpers + tests (TDD)

Standalone class with five static helpers. Used by `BarColorPicker` (for hex parsing + HSV math) and `HudTheme` (for gradient darkening). Pure logic, fully testable.

**Files:**
- Create: `src/GamePartyHud/Settings/HudColor.cs`
- Create: `tests/GamePartyHud.Tests/Settings/HudColorTests.cs`

- [ ] **Step 1.1: Write the failing tests**

Create `tests/GamePartyHud.Tests/Settings/HudColorTests.cs`:

```csharp
using GamePartyHud.Settings;
using Xunit;

namespace GamePartyHud.Tests.Settings;

public class HudColorTests
{
    [Fact]
    public void TryParse_RrggbbHex_AssumesOpaqueAlpha()
    {
        var result = HudColor.TryParse("#FFAABB");
        Assert.NotNull(result);
        Assert.Equal((byte)0xFF, result!.Value.A);
        Assert.Equal((byte)0xFF, result.Value.R);
        Assert.Equal((byte)0xAA, result.Value.G);
        Assert.Equal((byte)0xBB, result.Value.B);
    }

    [Fact]
    public void TryParse_AarrggbbHex_PreservesAlpha()
    {
        var result = HudColor.TryParse("#80FFAABB");
        Assert.NotNull(result);
        Assert.Equal((byte)0x80, result!.Value.A);
        Assert.Equal((byte)0xFF, result.Value.R);
        Assert.Equal((byte)0xAA, result.Value.G);
        Assert.Equal((byte)0xBB, result.Value.B);
    }

    [Theory]
    [InlineData("#XYZ")]
    [InlineData("#FF")]
    [InlineData("nothex")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("#FFAABBCC1")]   // 9 chars total — too long
    public void TryParse_InvalidHex_ReturnsNull(string? hex)
    {
        Assert.Null(HudColor.TryParse(hex!));
    }

    [Fact]
    public void Format_RoundTripsThroughTryParse()
    {
        foreach (var input in new[] { "#FF821414", "#80FFAABB", "#FF000000", "#FFFFFFFF" })
        {
            var parsed = HudColor.TryParse(input);
            Assert.NotNull(parsed);
            var (a, r, g, b) = parsed!.Value;
            Assert.Equal(input, HudColor.Format(a, r, g, b));
        }
    }

    [Fact]
    public void RgbToHsv_ReturnsKnownValuesForPrimaryColours()
    {
        // Pure red: H=0, S=1, V=1
        var red = HudColor.RgbToHsv(255, 0, 0);
        Assert.Equal(0.0, red.H, 1);
        Assert.Equal(1.0, red.S, 3);
        Assert.Equal(1.0, red.V, 3);

        // Pure green: H=120, S=1, V=1
        var green = HudColor.RgbToHsv(0, 255, 0);
        Assert.Equal(120.0, green.H, 1);
        Assert.Equal(1.0, green.S, 3);
        Assert.Equal(1.0, green.V, 3);

        // Pure blue: H=240, S=1, V=1
        var blue = HudColor.RgbToHsv(0, 0, 255);
        Assert.Equal(240.0, blue.H, 1);
        Assert.Equal(1.0, blue.S, 3);
        Assert.Equal(1.0, blue.V, 3);

        // Mid-grey: S=0, V=0.5
        var grey = HudColor.RgbToHsv(128, 128, 128);
        Assert.Equal(0.0, grey.S, 3);
        Assert.InRange(grey.V, 0.49, 0.51);
    }

    [Fact]
    public void HsvToRgb_RoundTripsThroughRgbToHsv()
    {
        var samples = new (byte R, byte G, byte B)[]
        {
            (255, 0, 0),     (0, 255, 0),     (0, 0, 255),
            (255, 255, 0),   (0, 255, 255),   (255, 0, 255),
            (200, 100, 50),  (50, 100, 200),  (130, 20, 20),
            (0, 0, 0),       (255, 255, 255), (128, 128, 128),
        };
        foreach (var (r, g, b) in samples)
        {
            var (h, s, v) = HudColor.RgbToHsv(r, g, b);
            var (r2, g2, b2) = HudColor.HsvToRgb(h, s, v);
            // ±1 due to integer rounding in the HSV→RGB step.
            Assert.InRange(r2, (byte)Math.Max(0, r - 1), (byte)Math.Min(255, r + 1));
            Assert.InRange(g2, (byte)Math.Max(0, g - 1), (byte)Math.Min(255, g + 1));
            Assert.InRange(b2, (byte)Math.Max(0, b - 1), (byte)Math.Min(255, b + 1));
        }
    }

    [Fact]
    public void Darken_HalvesAllChannelsAtFactor05()
    {
        var darkened = HudColor.Darken((200, 100, 50), 0.5);
        Assert.Equal((byte)100, darkened.R);
        Assert.Equal((byte)50,  darkened.G);
        Assert.Equal((byte)25,  darkened.B);
    }

    [Fact]
    public void Darken_ClampsResultToByteRange()
    {
        // Factor > 1 caps at 255.
        var brightened = HudColor.Darken((200, 100, 50), 2.0);
        Assert.Equal((byte)255, brightened.R);
        Assert.Equal((byte)200, brightened.G);
        Assert.Equal((byte)100, brightened.B);

        // Factor < 0 floors at 0.
        var negative = HudColor.Darken((200, 100, 50), -0.5);
        Assert.Equal((byte)0, negative.R);
        Assert.Equal((byte)0, negative.G);
        Assert.Equal((byte)0, negative.B);
    }
}
```

- [ ] **Step 1.2: Verify tests fail to build (TDD red)**

```
dotnet build tests/GamePartyHud.Tests/GamePartyHud.Tests.csproj 2>&1 | grep "error CS"
```
Expected: `error CS0246: The type or namespace name 'HudColor' could not be found`. Confirms the tests can't pass without the implementation.

- [ ] **Step 1.3: Create `HudColor.cs` with the implementation**

```csharp
using System;
using System.Globalization;

namespace GamePartyHud.Settings;

/// <summary>
/// Pure-logic helpers for the HUD colour pipeline:
///   - hex string parse / format (#RRGGBB or #AARRGGBB)
///   - RGB ↔ HSV conversion (HSV needed by the SV+hue picker to keep
///     hue precision when value=0)
///   - Darken: linear scale of an RGB triple, used to build the
///     auto-derived gradient bottom stop both in the picker preview and
///     at runtime in HudTheme.
/// </summary>
public static class HudColor
{
    /// <summary>Parse #RRGGBB or #AARRGGBB into ARGB bytes.
    /// Returns null on bad input (null, wrong length, non-hex chars).</summary>
    public static (byte A, byte R, byte G, byte B)? TryParse(string hex)
    {
        if (string.IsNullOrEmpty(hex) || hex[0] != '#') return null;
        var body = hex.AsSpan(1);
        byte a, r, g, b;
        if (body.Length == 6)
        {
            a = 0xFF;
            if (!TryHex(body.Slice(0, 2), out r)) return null;
            if (!TryHex(body.Slice(2, 2), out g)) return null;
            if (!TryHex(body.Slice(4, 2), out b)) return null;
        }
        else if (body.Length == 8)
        {
            if (!TryHex(body.Slice(0, 2), out a)) return null;
            if (!TryHex(body.Slice(2, 2), out r)) return null;
            if (!TryHex(body.Slice(4, 2), out g)) return null;
            if (!TryHex(body.Slice(6, 2), out b)) return null;
        }
        else return null;
        return (a, r, g, b);
    }

    private static bool TryHex(ReadOnlySpan<char> s, out byte value) =>
        byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);

    /// <summary>Format ARGB bytes as #AARRGGBB (upper-case).</summary>
    public static string Format(byte a, byte r, byte g, byte b) =>
        $"#{a:X2}{r:X2}{g:X2}{b:X2}";

    /// <summary>
    /// RGB (each 0–255) → HSV (H 0–360°, S 0–1, V 0–1). Standard HSV
    /// formula; H=0 when S=0 (grey).
    /// </summary>
    public static (double H, double S, double V) RgbToHsv(byte r, byte g, byte b)
    {
        double rd = r / 255.0;
        double gd = g / 255.0;
        double bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double v = max;
        double delta = max - min;
        double s = max <= 0.0 ? 0.0 : delta / max;
        double h;
        if (delta <= 0.0)              h = 0.0;
        else if (max == rd)            h = 60.0 * (((gd - bd) / delta) % 6.0);
        else if (max == gd)            h = 60.0 * (((bd - rd) / delta) + 2.0);
        else                           h = 60.0 * (((rd - gd) / delta) + 4.0);
        if (h < 0) h += 360.0;
        return (h, s, v);
    }

    /// <summary>HSV (H 0–360°, S 0–1, V 0–1) → RGB (each 0–255).
    /// Integer rounding so RGB → HSV → RGB may differ by ±1.</summary>
    public static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360.0) + 360.0) % 360.0;
        s = Math.Clamp(s, 0.0, 1.0);
        v = Math.Clamp(v, 0.0, 1.0);
        double c = v * s;
        double x = c * (1.0 - Math.Abs(((h / 60.0) % 2.0) - 1.0));
        double m = v - c;
        double r, g, b;
        if      (h <  60.0) { r = c; g = x; b = 0; }
        else if (h < 120.0) { r = x; g = c; b = 0; }
        else if (h < 180.0) { r = 0; g = c; b = x; }
        else if (h < 240.0) { r = 0; g = x; b = c; }
        else if (h < 300.0) { r = x; g = 0; b = c; }
        else                { r = c; g = 0; b = x; }
        return (
            (byte)Math.Round((r + m) * 255.0),
            (byte)Math.Round((g + m) * 255.0),
            (byte)Math.Round((b + m) * 255.0)
        );
    }

    /// <summary>Scale an RGB triple by <paramref name="factor"/>; clamps
    /// each channel to [0, 255]. factor=0.7 gives the ~30% darkening used
    /// for the bar-gradient bottom stop.</summary>
    public static (byte R, byte G, byte B) Darken((byte R, byte G, byte B) rgb, double factor)
    {
        return (
            ClampByte(rgb.R * factor),
            ClampByte(rgb.G * factor),
            ClampByte(rgb.B * factor)
        );
    }

    private static byte ClampByte(double v) =>
        v <= 0.0 ? (byte)0 : v >= 255.0 ? (byte)255 : (byte)Math.Round(v);
}
```

- [ ] **Step 1.4: Build + run tests**

```
dotnet build src/GamePartyHud/GamePartyHud.csproj
dotnet test tests/GamePartyHud.Tests/GamePartyHud.Tests.csproj
```
Expected: build clean; all 172 existing tests pass + 8 new `HudColorTests` = 180/180.

- [ ] **Step 1.5: Commit**

```
git add src/GamePartyHud/Settings/HudColor.cs tests/GamePartyHud.Tests/Settings/HudColorTests.cs
git commit -m "feat(settings): add HudColor pure-logic helpers

Standalone class for the upcoming color picker + HudTheme. Five
static helpers:
  - TryParse / Format for #RRGGBB and #AARRGGBB hex strings
  - RgbToHsv / HsvToRgb (standard HSV formula, integer rounding)
  - Darken (scale an RGB triple, clamped to byte range)

Eight unit tests cover hex round-trip, invalid input, primary-color
HSV values, RGB->HSV->RGB stability, and Darken clamping."
```

---

## Task 2: Add the four `AppConfig` fields + opacity clamp

Add `HpBarColor`, `StaminaBarColor`, `ManaBarColor`, `HudBackgroundOpacity` to `AppConfig` with defaults matching today's hardcoded values. Extend `ConfigStore` to clamp `HudBackgroundOpacity` on load (mirroring `SanitiseHudScale`). Add two clamp tests + grow the round-trip fixture.

**Files:**
- Modify: `src/GamePartyHud/Config/AppConfig.cs`
- Modify: `src/GamePartyHud/Config/ConfigStore.cs`
- Modify: `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs`

- [ ] **Step 2.1: Add the fields to `AppConfig`**

In `src/GamePartyHud/Config/AppConfig.cs`, find the record positional parameter list and add the four new fields after `HudScale`:

```csharp
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
    double HudScale = 1.0,
    string HpBarColor = "#FF821414",
    string StaminaBarColor = "#FF977E2E",
    string ManaBarColor = "#FF1A45A4",
    double HudBackgroundOpacity = 0.40)
{
    // ... existing static members unchanged ...
}
```

(Defaults at parameter level so existing `config.json` files deserialise unchanged — `System.Text.Json` fills missing keys with the parameter default.)

In the `Defaults` static initializer, the new fields are picked up via their parameter defaults — **no change required** to the initializer body. Verify by reading the file post-edit; if the compiler complains about missing argument names, add them explicitly.

- [ ] **Step 2.2: Add `SanitiseHudBackgroundOpacity` to `ConfigStore`**

In `src/GamePartyHud/Config/ConfigStore.cs`, find `SanitiseHudScale` (around line 215) and add the new helper directly below it:

```csharp
private static double SanitiseHudBackgroundOpacity(double raw)
{
    // Out-of-range, NaN, or infinite values fall back to the default
    // (0.40). Clamped to [0.1, 1.0] so the user can never accidentally
    // hide the HUD chrome entirely (which would also hide the lock
    // button and trap them in dragged-off-screen territory).
    if (double.IsNaN(raw) || double.IsInfinity(raw)) return 0.40;
    return Math.Clamp(raw, 0.10, 1.0);
}
```

- [ ] **Step 2.3: Apply the clamp in `Load`**

In `src/GamePartyHud/Config/ConfigStore.cs`, find the `with` block in `Load` (around line 60–69) that sets `HudScale = SanitiseHudScale(...)`. Add the opacity sanitise:

```csharp
return repaired with
{
    RelayUrl = AppConfig.DefaultRelayUrl,
    RelayFallbackUrl = AppConfig.DefaultRelayFallbackUrl,
    PollIntervalMs = AppConfig.Defaults.PollIntervalMs,
    HudScale = SanitiseHudScale(repaired.HudScale),
    HudBackgroundOpacity = SanitiseHudBackgroundOpacity(repaired.HudBackgroundOpacity),
};
```

- [ ] **Step 2.4: Extend the round-trip test fixture**

In `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs`, find `RoundTrip_PreservesEverythingExceptBinaryOwnedFields` and update the `cfg = AppConfig.Defaults with { ... }` initializer to include the four new fields (with non-default values so the round-trip proves they're preserved). Add inside the `with` block:

```csharp
HpBarColor = "#FF112233",
StaminaBarColor = "#FF445566",
ManaBarColor = "#FF778899",
HudBackgroundOpacity = 0.75,
```

(Order doesn't matter for `with` expressions.)

- [ ] **Step 2.5: Add the two new clamp tests**

Append to `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs` (just before the closing `}` of the class):

```csharp
    [Fact]
    public void Load_HudBackgroundOpacity_AboveOne_ClampedToOne()
    {
        File.WriteAllText(_tmp, """
{
  "presets": [
    { "id": "default", "name": "Default", "nickname": "T", "role": "Tank",
      "hpCalibration": null, "staminaCalibration": null, "manaCalibration": null }
  ],
  "activePresetId": "default",
  "hudPosition": { "x": 0, "y": 0 },
  "hudLocked": true,
  "lastPartyId": null,
  "pollIntervalMs": 700,
  "relayUrl": "",
  "hudBackgroundOpacity": 5.0
}
""");
        var loaded = new ConfigStore(_tmp).Load();
        Assert.Equal(1.0, loaded.HudBackgroundOpacity);
    }

    [Fact]
    public void Load_HudBackgroundOpacity_BelowMin_ClampedToTenth()
    {
        File.WriteAllText(_tmp, """
{
  "presets": [
    { "id": "default", "name": "Default", "nickname": "T", "role": "Tank",
      "hpCalibration": null, "staminaCalibration": null, "manaCalibration": null }
  ],
  "activePresetId": "default",
  "hudPosition": { "x": 0, "y": 0 },
  "hudLocked": true,
  "lastPartyId": null,
  "pollIntervalMs": 700,
  "relayUrl": "",
  "hudBackgroundOpacity": 0.0
}
""");
        var loaded = new ConfigStore(_tmp).Load();
        Assert.Equal(0.10, loaded.HudBackgroundOpacity);
    }
```

- [ ] **Step 2.6: Build + run tests**

```
dotnet build src/GamePartyHud/GamePartyHud.csproj
dotnet test tests/GamePartyHud.Tests/GamePartyHud.Tests.csproj
```
Expected: build clean; 180 from Task 1 + 2 new = 182/182. The round-trip test (untouched assertion) still passes because the four new fields' string/double values round-trip natively through `System.Text.Json`.

- [ ] **Step 2.7: Commit**

```
git add src/GamePartyHud/Config/AppConfig.cs src/GamePartyHud/Config/ConfigStore.cs tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs
git commit -m "feat(config): add HUD theme fields (colors + opacity)

Four new AppConfig fields, all global per-machine, defaulting to
today's hardcoded HUD values:
  - HpBarColor       = #FF821414
  - StaminaBarColor  = #FF977E2E
  - ManaBarColor     = #FF1A45A4
  - HudBackgroundOpacity = 0.40

Hex strings round-trip natively via System.Text.Json. Opacity is
clamped to [0.10, 1.0] on load (analogous to SanitiseHudScale) so a
hand-edited 5.0 or 0.0 can't break the HUD. Two new clamp tests +
the existing RoundTrip_Preserves... test exercises the four fields."
```

---

## Task 3: `HudTheme` view-model

`INotifyPropertyChanged` class that wraps the four config fields as `Brush` properties the HUD overlay can bind to. Constructed once per `App` instance; `RefreshFrom(cfg)` rebuilds whichever brushes' source values changed.

**Files:**
- Create: `src/GamePartyHud/Hud/HudTheme.cs`

- [ ] **Step 3.1: Create `HudTheme.cs`**

```csharp
using System;
using System.ComponentModel;
using System.Windows.Media;
using GamePartyHud.Config;
using GamePartyHud.Settings;

namespace GamePartyHud.Hud;

/// <summary>
/// Live view-model exposing the four HUD theme settings as bindable
/// <see cref="Brush"/> properties. <see cref="HudWindow.RootBorder"/>
/// binds <c>Background</c> into <see cref="PanelBackgroundBrush"/>;
/// <see cref="MemberCard"/>'s three bar rows bind into the per-bar
/// brushes. Owned by <c>App</c>; refreshed in-place whenever
/// <c>AppConfig</c> changes so WPF data binding picks up new brushes
/// on the next render tick (no polling, no per-tick capture cost).
/// </summary>
public sealed class HudTheme : INotifyPropertyChanged
{
    // The shared darkening factor for the auto-derived gradient bottom
    // stop. 0.7 = 30% darker, matches the hand-tuned palette the HUD
    // shipped with before the color picker.
    public const double DarkenFactor = 0.70;

    public Brush HpBarBrush { get; private set; } = Brushes.Transparent;
    public Brush StaminaBarBrush { get; private set; } = Brushes.Transparent;
    public Brush ManaBarBrush { get; private set; } = Brushes.Transparent;
    public Brush PanelBackgroundBrush { get; private set; } = Brushes.Transparent;

    public HudTheme(AppConfig cfg) => RefreshFrom(cfg);

    public void RefreshFrom(AppConfig cfg)
    {
        var newHp      = BuildBarBrush(cfg.HpBarColor);
        var newStamina = BuildBarBrush(cfg.StaminaBarColor);
        var newMana    = BuildBarBrush(cfg.ManaBarColor);
        var newPanel   = BuildPanelBrush(cfg.HudBackgroundOpacity);

        if (!BrushEquals(newHp, HpBarBrush))             { HpBarBrush = newHp;             Raise(nameof(HpBarBrush)); }
        if (!BrushEquals(newStamina, StaminaBarBrush))   { StaminaBarBrush = newStamina;   Raise(nameof(StaminaBarBrush)); }
        if (!BrushEquals(newMana, ManaBarBrush))         { ManaBarBrush = newMana;         Raise(nameof(ManaBarBrush)); }
        if (!BrushEquals(newPanel, PanelBackgroundBrush)){ PanelBackgroundBrush = newPanel;Raise(nameof(PanelBackgroundBrush)); }
    }

    private static Brush BuildBarBrush(string hex)
    {
        var parsed = HudColor.TryParse(hex);
        if (parsed is null) return Brushes.Transparent;
        var (a, r, g, b) = parsed.Value;
        var darker = HudColor.Darken((r, g, b), DarkenFactor);
        return new LinearGradientBrush(
            Color.FromArgb(a, r, g, b),
            Color.FromArgb(a, darker.R, darker.G, darker.B),
            new System.Windows.Point(0, 0),
            new System.Windows.Point(0, 1));
    }

    private static Brush BuildPanelBrush(double opacity)
    {
        // Today's two stops, with the alpha byte swapped for the user's
        // chosen opacity (clamped at the ConfigStore.Load layer).
        byte alpha = (byte)Math.Round(Math.Clamp(opacity, 0.0, 1.0) * 255.0);
        return new LinearGradientBrush(
            Color.FromArgb(alpha, 0x1B, 0x1B, 0x1E),
            Color.FromArgb(alpha, 0x12, 0x12, 0x15),
            new System.Windows.Point(0, 0),
            new System.Windows.Point(0, 1));
    }

    // Reference equality isn't enough (every Refresh builds new
    // LinearGradientBrush instances); compare the underlying GradientStop
    // colours to avoid pointless PropertyChanged notifications.
    private static bool BrushEquals(Brush a, Brush b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is LinearGradientBrush la && b is LinearGradientBrush lb)
        {
            if (la.GradientStops.Count != lb.GradientStops.Count) return false;
            for (int i = 0; i < la.GradientStops.Count; i++)
            {
                if (la.GradientStops[i].Color != lb.GradientStops[i].Color) return false;
                if (la.GradientStops[i].Offset != lb.GradientStops[i].Offset) return false;
            }
            return true;
        }
        return false;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
```

- [ ] **Step 3.2: Build**

```
dotnet build src/GamePartyHud/GamePartyHud.csproj
```
Expected: build clean (`HudTheme` is unreferenced for now; Task 4 wires it in).

- [ ] **Step 3.3: Commit**

```
git add src/GamePartyHud/Hud/HudTheme.cs
git commit -m "feat(hud): add HudTheme INotifyPropertyChanged view-model

Wraps the four AppConfig theme fields as bindable Brush properties.
RefreshFrom(cfg) rebuilds whichever brushes' source values changed
(cheap GradientStop comparison short-circuits no-op refreshes) and
raises PropertyChanged so WPF data binding repaints on the next
render tick.

Unreferenced for now — Task 4 wires it into App + IController + HUD."
```

---

## Task 4: `IController.HudTheme` + `App` wiring + Reset rename (atomic)

The interface gets a new property and a renamed method. `App` provides the implementation, owns the single `HudTheme` instance, refreshes it on every `UpdateConfig`, sets `HudWindow.DataContext`, and renames `ResetHudLayout` → `ResetHudToDefaults` (extending it to wipe colours + opacity too). `SettingsWindow`'s existing handler is updated to call the renamed method. Three files change together; compilation breaks if any one is missed.

**Files:**
- Modify: `src/GamePartyHud/MainWindow.xaml.cs` (the `IController` interface declaration)
- Modify: `src/GamePartyHud/App.xaml.cs`
- Modify: `src/GamePartyHud/SettingsWindow.xaml.cs`

- [ ] **Step 4.1: Update `IController` in `MainWindow.xaml.cs`**

Find the `IController` interface declaration near the top of `src/GamePartyHud/MainWindow.xaml.cs`:

```csharp
public interface IController
{
    AppConfig Config { get; }

    string? CurrentPartyId { get; }
    int MemberCount { get; }
    event Action? PartyStateChanged;

    void UpdateConfig(AppConfig cfg);
    void ResetHudLayout();

    Task CreatePartyAsync();
    Task JoinPartyAsync(string partyId);
    Task LeavePartyAsync();
    Task ShutdownAsync();
}
```

Add the new `HudTheme` property and rename `ResetHudLayout` → `ResetHudToDefaults`. Also add `using GamePartyHud.Hud;` to the top of the file if not already present:

```csharp
public interface IController
{
    AppConfig Config { get; }
    Hud.HudTheme HudTheme { get; }

    string? CurrentPartyId { get; }
    int MemberCount { get; }
    event Action? PartyStateChanged;

    void UpdateConfig(AppConfig cfg);
    void ResetHudToDefaults();

    Task CreatePartyAsync();
    Task JoinPartyAsync(string partyId);
    Task LeavePartyAsync();
    Task ShutdownAsync();
}
```

(Fully-qualifying `Hud.HudTheme` avoids needing a new `using` import.)

- [ ] **Step 4.2: Construct `HudTheme` in `App.xaml.cs` startup**

In `src/GamePartyHud/App.xaml.cs`, find the field declarations near the top of the class (alongside `_tray`, `_store`, `_config`, etc.) and add:

```csharp
private HudTheme? _theme;
```

Then in `OnStartup`, after `_config = _store.Load();` (around line 124), add:

```csharp
_theme = new HudTheme(_config);
```

Add `using GamePartyHud.Hud;` to the top if not present (it already imports `Hud.HudWindow`, so likely yes).

- [ ] **Step 4.3: Expose `HudTheme` via `IController`**

Just below the existing explicit-interface properties on `App` (around line 39 `AppConfig MainWindow.IController.Config => _config;`), add:

```csharp
Hud.HudTheme MainWindow.IController.HudTheme => _theme!;
```

The `!` is honest — `_theme` is assigned before any window construction that could call this getter.

- [ ] **Step 4.4: Refresh `HudTheme` on every `UpdateConfig`**

In `src/GamePartyHud/App.xaml.cs`, find `void MainWindow.IController.UpdateConfig(AppConfig cfg)` (around line 45):

```csharp
void MainWindow.IController.UpdateConfig(AppConfig cfg)
{
    _config = cfg;
    _orch?.UpdateConfig(cfg);
    try { _store?.Save(_config); }
    catch (Exception ex) { Log.Error("Failed to persist config.", ex); }
}
```

Add a `_theme?.RefreshFrom(cfg)` call so the HUD picks up theme changes immediately:

```csharp
void MainWindow.IController.UpdateConfig(AppConfig cfg)
{
    _config = cfg;
    _theme?.RefreshFrom(cfg);     // <-- new: HUD repaints on next render tick
    _orch?.UpdateConfig(cfg);
    try { _store?.Save(_config); }
    catch (Exception ex) { Log.Error("Failed to persist config.", ex); }
}
```

- [ ] **Step 4.5: Set `HudWindow.DataContext` so the bindings (Task 5) can resolve**

In `src/GamePartyHud/App.xaml.cs`, find the HUD construction in `OnStartup` (around line 143 `_hud = new HudWindow();`). Add a `DataContext` assignment that exposes `HudTheme`:

```csharp
_hud = new HudWindow();
_hud.DataContext = new { HudTheme = _theme };
_sync = new HudViewModelSync(_state, _hud.MemberList);
```

The anonymous-object holder is the minimal shape that lets `HudWindow.xaml` say `{Binding HudTheme.PanelBackgroundBrush}` (the binding traverses through `DataContext` → property `HudTheme` → property `PanelBackgroundBrush`). A real `HudWindowViewModel` class is overkill for one property.

- [ ] **Step 4.6: Rename `ResetHudLayout` → `ResetHudToDefaults` and extend**

In `src/GamePartyHud/App.xaml.cs`, find `void MainWindow.IController.ResetHudLayout()` (around line 58). Rename and extend:

```csharp
void MainWindow.IController.ResetHudToDefaults()
{
    if (_hud is null) return;
    var d = AppConfig.Defaults;
    _hud.Left  = d.HudPosition.X;
    _hud.Top   = d.HudPosition.Y;
    _hud.Scale = d.HudScale;
    _config = _config with
    {
        HudPosition          = d.HudPosition,
        HudScale             = d.HudScale,
        HudLocked            = d.HudLocked,
        HpBarColor           = d.HpBarColor,
        StaminaBarColor      = d.StaminaBarColor,
        ManaBarColor         = d.ManaBarColor,
        HudBackgroundOpacity = d.HudBackgroundOpacity,
    };
    _theme?.RefreshFrom(_config);
    try { _store?.Save(_config); }
    catch (Exception ex) { Log.Error("Failed to persist config after HUD reset.", ex); }
    Log.Info("App: reset HUD to defaults (position, scale, lock, colors, opacity).");
}
```

- [ ] **Step 4.7: Update `SettingsWindow.xaml.cs` to call the renamed method**

In `src/GamePartyHud/SettingsWindow.xaml.cs`, find the existing `OnResetHud` handler:

```csharp
private void OnResetHud(object sender, RoutedEventArgs e)
{
    _ctl.ResetHudLayout();
    Log.Info("SettingsWindow: Reset HUD layout clicked.");
    Close();
}
```

Update the call:

```csharp
private void OnResetHud(object sender, RoutedEventArgs e)
{
    _ctl.ResetHudToDefaults();
    Log.Info("SettingsWindow: Reset to defaults clicked.");
    Close();
}
```

(The XAML button text update happens in Task 7's full SettingsWindow rewrite.)

- [ ] **Step 4.8: Build + run tests**

```
dotnet build src/GamePartyHud/GamePartyHud.csproj
dotnet test tests/GamePartyHud.Tests/GamePartyHud.Tests.csproj
```
Expected: build clean (most likely two missing-symbol errors if any rename is partial — the interface, App, or SettingsWindow), 182/182 pass. Existing UI behaves identically — the only behavioural delta is that the Reset button now resets four more fields (still defaults today since nothing has changed them).

- [ ] **Step 4.9: Commit**

```
git add src/GamePartyHud/MainWindow.xaml.cs src/GamePartyHud/App.xaml.cs src/GamePartyHud/SettingsWindow.xaml.cs
git commit -m "refactor(app): rename ResetHudLayout->ResetHudToDefaults + expose HudTheme

IController gains HudTheme property; App owns the single HudTheme
instance, refreshes it on every UpdateConfig, sets HudWindow's
DataContext to a holder exposing it (so Task 5's bindings resolve).
ResetHudLayout becomes ResetHudToDefaults and now wipes position,
scale, lock state, plus the four new color/opacity fields back to
defaults in one UpdateConfig call.

SettingsWindow's existing handler updated to call the renamed
method. UI behavior unchanged (Bars colors/opacity still default
since no picker exists yet)."
```

---

## Task 5: HUD overlay bindings

Replace the hardcoded `LinearGradientBrush` blocks in `HudWindow.xaml` and `MemberCard.xaml` with bindings into the `HudTheme` instance exposed via the HUD window's DataContext.

**Files:**
- Modify: `src/GamePartyHud/Hud/HudWindow.xaml`
- Modify: `src/GamePartyHud/Hud/MemberCard.xaml`

- [ ] **Step 5.1: Update `HudWindow.xaml` `RootBorder.Background`**

In `src/GamePartyHud/Hud/HudWindow.xaml`, find the `RootBorder` block:

```xml
<Border x:Name="RootBorder"
        Padding="6,4"
        CornerRadius="3"
        BorderBrush="#66E12A2A"
        BorderThickness="0">
    <Border.Background>
        <LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
            <GradientStop Color="#661B1B1E" Offset="0"/>
            <GradientStop Color="#66121215" Offset="1"/>
        </LinearGradientBrush>
    </Border.Background>
    ...
```

Replace the inline `Border.Background` block with a binding:

```xml
<Border x:Name="RootBorder"
        Padding="6,4"
        CornerRadius="3"
        BorderBrush="#66E12A2A"
        BorderThickness="0"
        Background="{Binding HudTheme.PanelBackgroundBrush}">
    ...
```

(Drop the entire `<Border.Background>...</Border.Background>` block.)

- [ ] **Step 5.2: Update the three bar gradients in `MemberCard.xaml`**

In `src/GamePartyHud/Hud/MemberCard.xaml`, find the **HP row** (around line 69–78):

```xml
<Border HorizontalAlignment="Left"
        Width="{Binding HpPercent, Converter={StaticResource HpWidthConverter}, ConverterParameter=174}">
    <Border.Background>
        <LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
            <GradientStop Color="#FF821414" Offset="0"/>
            <GradientStop Color="#FF591414" Offset="1"/>
        </LinearGradientBrush>
    </Border.Background>
</Border>
```

Replace the inline `Border.Background` with a `Binding` that walks up to the `HudWindow` and through its `DataContext`:

```xml
<Border HorizontalAlignment="Left"
        Width="{Binding HpPercent, Converter={StaticResource HpWidthConverter}, ConverterParameter=174}"
        Background="{Binding DataContext.HudTheme.HpBarBrush, RelativeSource={RelativeSource AncestorType=Window}}"/>
```

(Self-closing — the old `<Border.Background>` block goes away entirely.)

Do the **same swap** for the Stamina row (around line 103–112), binding to `StaminaBarBrush`:

```xml
<Border HorizontalAlignment="Left"
        Margin="0,1,0,0"
        Width="{Binding StaminaPercent, Converter={StaticResource HpWidthConverter}, ConverterParameter=174}"
        Background="{Binding DataContext.HudTheme.StaminaBarBrush, RelativeSource={RelativeSource AncestorType=Window}}"/>
```

And the **Mana row** (around line 118–127), binding to `ManaBarBrush`:

```xml
<Border HorizontalAlignment="Left"
        Margin="0,1,0,0"
        Width="{Binding ManaPercent, Converter={StaticResource HpWidthConverter}, ConverterParameter=174}"
        Background="{Binding DataContext.HudTheme.ManaBarBrush, RelativeSource={RelativeSource AncestorType=Window}}"/>
```

The white highlight stripe at the top of the HP fill (the second `<Border>` in the HP row with `Background="#33FFFFFF"`) stays as-is — it's not themed.

- [ ] **Step 5.3: Build**

```
dotnet build src/GamePartyHud/GamePartyHud.csproj
```
Expected: build clean. If XAML parser complains about a missing property path, double-check Task 4 set `HudWindow.DataContext`.

- [ ] **Step 5.4: Smoke-test the HUD live**

Run the app, get the HUD to show (debug smoke harness if a party isn't easy: `dotnet run --project src/GamePartyHud/GamePartyHud.csproj -- --hud-smoke=4`). Verify:
- HUD overlay looks identical to before this PR (default colours, default opacity).
- No `System.Windows.Data Error` lines in `app.log` complaining about failed bindings.

If a binding fails silently, the brush falls back to `Brushes.Transparent` (per `HudTheme`'s initial values) and the bar disappears — easy to spot.

- [ ] **Step 5.5: Commit**

```
git add src/GamePartyHud/Hud/HudWindow.xaml src/GamePartyHud/Hud/MemberCard.xaml
git commit -m "feat(hud): bind RootBorder.Background + bar brushes to HudTheme

Replaces four hardcoded LinearGradientBrush blocks with {Binding}
expressions that resolve through HudWindow.DataContext.HudTheme.
HUD repaints automatically on every HudTheme PropertyChanged event
(triggered by UpdateConfig / ResetHudToDefaults).

Visual unchanged at defaults; the picker UI lands in Tasks 6-7."
```

---

## Task 6: `BarColorPicker` UserControl

Inline swatch + popup with SV square, vertical hue strip, hex input, preview gradient. Self-contained, no dependencies beyond `HudColor`.

**Files:**
- Create: `src/GamePartyHud/Settings/BarColorPicker.xaml`
- Create: `src/GamePartyHud/Settings/BarColorPicker.xaml.cs`

- [ ] **Step 6.1: Create `BarColorPicker.xaml`**

```xml
<UserControl x:Class="GamePartyHud.Settings.BarColorPicker"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:local="clr-namespace:GamePartyHud.Settings">
    <UserControl.Resources>
        <!-- Converter so the closed-state swatch's Background reacts to
             ColorHex without code-behind plumbing. Declared at the top
             so StaticResource lookups below resolve at parse time. -->
        <local:HexToBrushConverter x:Key="HexToBrushConverter"/>
    </UserControl.Resources>
    <StackPanel Orientation="Horizontal" VerticalAlignment="Center">
        <!-- Clickable swatch -->
        <Border x:Name="SwatchBorder"
                Width="24" Height="24"
                CornerRadius="3"
                BorderBrush="#33FFFFFF"
                BorderThickness="1"
                Cursor="Hand"
                MouseLeftButtonUp="OnSwatchClicked"
                Background="{Binding ColorHex, RelativeSource={RelativeSource AncestorType=UserControl}, Converter={StaticResource HexToBrushConverter}}"/>
        <TextBlock x:Name="HexLabel"
                   Margin="8,0,0,0"
                   VerticalAlignment="Center"
                   FontFamily="Consolas, Cascadia Mono, monospace"
                   FontSize="11"
                   Text="{Binding ColorHex, RelativeSource={RelativeSource AncestorType=UserControl}}"/>

        <!-- Popup: SV square + hue strip + hex + preview -->
        <Popup x:Name="PickerPopup"
               StaysOpen="False"
               Placement="Bottom"
               PlacementTarget="{Binding ElementName=SwatchBorder}"
               AllowsTransparency="True"
               PopupAnimation="Fade"
               Opened="OnPopupOpened"
               Closed="OnPopupClosed"
               KeyDown="OnPopupKeyDown">
            <Border Background="#FF1F1F23"
                    BorderBrush="#44FFFFFF"
                    BorderThickness="1"
                    CornerRadius="4"
                    Padding="12">
                <StackPanel>
                    <Grid>
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="Auto"/>
                            <ColumnDefinition Width="8"/>
                            <ColumnDefinition Width="Auto"/>
                        </Grid.ColumnDefinitions>

                        <!-- SV square -->
                        <Border Grid.Column="0"
                                Width="200" Height="160"
                                BorderBrush="#44FFFFFF"
                                BorderThickness="1"
                                ClipToBounds="True"
                                MouseLeftButtonDown="OnSvSquareMouseDown"
                                MouseLeftButtonUp="OnSvSquareMouseUp"
                                MouseMove="OnSvSquareMouseMove">
                            <Grid>
                                <Rectangle x:Name="SvHueLayer">
                                    <Rectangle.Fill>
                                        <SolidColorBrush x:Name="SvHueBrush" Color="#FFFF0000"/>
                                    </Rectangle.Fill>
                                </Rectangle>
                                <Rectangle>
                                    <Rectangle.Fill>
                                        <LinearGradientBrush StartPoint="0,0" EndPoint="1,0">
                                            <GradientStop Color="#FFFFFFFF" Offset="0"/>
                                            <GradientStop Color="#00FFFFFF" Offset="1"/>
                                        </LinearGradientBrush>
                                    </Rectangle.Fill>
                                </Rectangle>
                                <Rectangle>
                                    <Rectangle.Fill>
                                        <LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
                                            <GradientStop Color="#00000000" Offset="0"/>
                                            <GradientStop Color="#FF000000" Offset="1"/>
                                        </LinearGradientBrush>
                                    </Rectangle.Fill>
                                </Rectangle>
                                <Canvas IsHitTestVisible="False">
                                    <Ellipse x:Name="SvCursor"
                                             Width="10" Height="10"
                                             Stroke="White" StrokeThickness="2"
                                             Fill="Transparent"
                                             Canvas.Left="0" Canvas.Top="0"/>
                                </Canvas>
                            </Grid>
                        </Border>

                        <!-- Hue strip -->
                        <Border Grid.Column="2"
                                Width="16" Height="160"
                                BorderBrush="#44FFFFFF"
                                BorderThickness="1"
                                ClipToBounds="True"
                                MouseLeftButtonDown="OnHueStripMouseDown"
                                MouseLeftButtonUp="OnHueStripMouseUp"
                                MouseMove="OnHueStripMouseMove">
                            <Grid>
                                <Rectangle>
                                    <Rectangle.Fill>
                                        <LinearGradientBrush StartPoint="0,0" EndPoint="0,1">
                                            <GradientStop Color="#FFFF0000" Offset="0.000"/>
                                            <GradientStop Color="#FFFFFF00" Offset="0.167"/>
                                            <GradientStop Color="#FF00FF00" Offset="0.333"/>
                                            <GradientStop Color="#FF00FFFF" Offset="0.500"/>
                                            <GradientStop Color="#FF0000FF" Offset="0.667"/>
                                            <GradientStop Color="#FFFF00FF" Offset="0.833"/>
                                            <GradientStop Color="#FFFF0000" Offset="1.000"/>
                                        </LinearGradientBrush>
                                    </Rectangle.Fill>
                                </Rectangle>
                                <Canvas IsHitTestVisible="False">
                                    <Rectangle x:Name="HueCursor"
                                               Width="20" Height="3"
                                               Fill="White"
                                               Canvas.Left="-2"
                                               Canvas.Top="0"/>
                                </Canvas>
                            </Grid>
                        </Border>
                    </Grid>

                    <!-- Hex input -->
                    <StackPanel Orientation="Horizontal" Margin="0,10,0,0" VerticalAlignment="Center">
                        <TextBlock Text="Hex" VerticalAlignment="Center" Margin="0,0,8,0"/>
                        <TextBox x:Name="HexInput"
                                 Width="110"
                                 FontFamily="Consolas, Cascadia Mono, monospace"
                                 MaxLength="9"
                                 TextChanged="OnHexInputChanged"/>
                    </StackPanel>

                    <!-- Preview -->
                    <TextBlock Text="Preview" Margin="0,10,0,4" Opacity="0.7" FontSize="11"/>
                    <Border x:Name="PreviewBorder"
                            Width="200" Height="22"
                            CornerRadius="2"
                            BorderBrush="#44FFFFFF"
                            BorderThickness="1"/>
                </StackPanel>
            </Border>
        </Popup>
    </StackPanel>
</UserControl>
```

- [ ] **Step 6.2: Create `BarColorPicker.xaml.cs`**

```csharp
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GamePartyHud.Settings;

/// <summary>
/// Inline swatch + popup color picker. Picker UI inside the popup:
/// 200x160 SV (saturation/value) square + vertical hue strip + hex
/// input + 200x22 preview gradient (top = picked color, bottom =
/// HudColor.Darken(picked, 0.7) — matches the runtime HUD render).
/// Outside-click commits, Escape reverts to the popup-open value.
/// </summary>
public partial class BarColorPicker : UserControl
{
    public static readonly DependencyProperty ColorHexProperty =
        DependencyProperty.Register(
            nameof(ColorHex), typeof(string), typeof(BarColorPicker),
            new FrameworkPropertyMetadata("#FFFFFFFF",
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnColorHexChanged));

    public string ColorHex
    {
        get => (string)GetValue(ColorHexProperty);
        set => SetValue(ColorHexProperty, value);
    }

    public event EventHandler? ColorChanged;

    private const double SvW = 200;
    private const double SvH = 160;
    private const double HueH = 160;

    // Internal HSV state (single source of truth while popup open).
    private double _h, _s, _v;
    private bool _suppressFeedback;
    private bool _draggingSv;
    private bool _draggingHue;
    private string? _revertSnapshot;

    public BarColorPicker() => InitializeComponent();

    private static void OnColorHexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (BarColorPicker)d;
        if (picker._suppressFeedback) return;
        picker.SeedFromHex((string)e.NewValue);
    }

    private void OnSwatchClicked(object sender, MouseButtonEventArgs e) =>
        PickerPopup.IsOpen = !PickerPopup.IsOpen;

    private void OnPopupOpened(object sender, EventArgs e)
    {
        _revertSnapshot = ColorHex;
        SeedFromHex(ColorHex);
        HexInput.Text = ColorHex;
    }

    private void OnPopupClosed(object sender, EventArgs e)
    {
        ColorChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPopupKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _revertSnapshot is not null)
        {
            _suppressFeedback = true;
            try
            {
                ColorHex = _revertSnapshot;
                HexInput.Text = _revertSnapshot;
                SeedFromHex(_revertSnapshot);
            }
            finally { _suppressFeedback = false; }
            PickerPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void OnSvSquareMouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggingSv = true;
        ((UIElement)sender).CaptureMouse();
        UpdateFromSv(e.GetPosition((IInputElement)sender));
    }
    private void OnSvSquareMouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingSv) return;
        UpdateFromSv(e.GetPosition((IInputElement)sender));
    }
    private void OnSvSquareMouseUp(object sender, MouseButtonEventArgs e)
    {
        _draggingSv = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void OnHueStripMouseDown(object sender, MouseButtonEventArgs e)
    {
        _draggingHue = true;
        ((UIElement)sender).CaptureMouse();
        UpdateFromHue(e.GetPosition((IInputElement)sender).Y);
    }
    private void OnHueStripMouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingHue) return;
        UpdateFromHue(e.GetPosition((IInputElement)sender).Y);
    }
    private void OnHueStripMouseUp(object sender, MouseButtonEventArgs e)
    {
        _draggingHue = false;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private void UpdateFromSv(Point p)
    {
        _s = Math.Clamp(p.X / SvW, 0.0, 1.0);
        _v = 1.0 - Math.Clamp(p.Y / SvH, 0.0, 1.0);
        ApplyHsv();
    }

    private void UpdateFromHue(double y)
    {
        _h = Math.Clamp(y / HueH, 0.0, 0.999) * 360.0;
        SvHueBrush.Color = ToColor(HudColor.HsvToRgb(_h, 1.0, 1.0));
        ApplyHsv();
    }

    private void OnHexInputChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressFeedback) return;
        var parsed = HudColor.TryParse(HexInput.Text);
        if (parsed is null)
        {
            HexInput.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
            HexInput.ToolTip = "Use #RRGGBB or #AARRGGBB";
            return;
        }
        HexInput.BorderBrush = SystemColors.ControlDarkBrush;
        HexInput.ToolTip = null;
        var (_, r, g, b) = parsed.Value;
        (_h, _s, _v) = HudColor.RgbToHsv(r, g, b);
        SvHueBrush.Color = ToColor(HudColor.HsvToRgb(_h, 1.0, 1.0));
        UpdateCursors();
        UpdatePreview();
        PushHexUp();
    }

    private void SeedFromHex(string hex)
    {
        var parsed = HudColor.TryParse(hex);
        if (parsed is null) return;
        var (_, r, g, b) = parsed.Value;
        (_h, _s, _v) = HudColor.RgbToHsv(r, g, b);
        SvHueBrush.Color = ToColor(HudColor.HsvToRgb(_h, 1.0, 1.0));
        UpdateCursors();
        UpdatePreview();
    }

    private void ApplyHsv()
    {
        UpdateCursors();
        UpdatePreview();
        var rgb = HudColor.HsvToRgb(_h, _s, _v);
        var hex = HudColor.Format(0xFF, rgb.R, rgb.G, rgb.B);
        _suppressFeedback = true;
        try
        {
            HexInput.Text = hex;
            ColorHex = hex;
        }
        finally { _suppressFeedback = false; }
    }

    private void PushHexUp()
    {
        // After hex input parses cleanly, push it back into ColorHex
        // without re-triggering the TextChanged handler.
        _suppressFeedback = true;
        try { ColorHex = HexInput.Text; }
        finally { _suppressFeedback = false; }
    }

    private void UpdateCursors()
    {
        // SV cursor — center on the picked S/V.
        Canvas.SetLeft(SvCursor, _s * SvW - 5);
        Canvas.SetTop(SvCursor, (1.0 - _v) * SvH - 5);
        // Hue cursor — vertical position along the strip.
        double y = (_h / 360.0) * HueH - 1.5;
        Canvas.SetTop(HueCursor, y);
    }

    private void UpdatePreview()
    {
        var rgb = HudColor.HsvToRgb(_h, _s, _v);
        var darker = HudColor.Darken(rgb, 0.70);
        var top = ToColor(rgb);
        var bot = Color.FromRgb(darker.R, darker.G, darker.B);
        PreviewBorder.Background = new LinearGradientBrush(top, bot,
            new Point(0, 0), new Point(0, 1));
    }

    private static Color ToColor((byte R, byte G, byte B) rgb) =>
        Color.FromRgb(rgb.R, rgb.G, rgb.B);
}

/// <summary>
/// Tiny value converter so the closed-state swatch's Background reacts
/// to the ColorHex DP without code-behind plumbing.
/// </summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string hex && HudColor.TryParse(hex) is (byte a, byte r, byte g, byte b))
            return new SolidColorBrush(Color.FromArgb(a, r, g, b));
        return Brushes.Transparent;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

- [ ] **Step 6.3: Build**

```
dotnet build src/GamePartyHud/GamePartyHud.csproj
```
Expected: build clean (the picker isn't instantiated yet; Task 7 places it in `SettingsWindow`).

- [ ] **Step 6.4: Commit**

```
git add src/GamePartyHud/Settings/BarColorPicker.xaml src/GamePartyHud/Settings/BarColorPicker.xaml.cs
git commit -m "feat(settings): add BarColorPicker UserControl

Inline swatch + popup picker with a 200x160 SV (saturation/value)
square, vertical hue strip, hex input, and 200x22 preview gradient
(top = picked color, bottom = HudColor.Darken(picked, 0.7)).
Outside-click commits; Escape reverts to the value at popup-open.

ColorHex is a two-way bindable DependencyProperty; ColorChanged
event fires on every commit. HexToBrushConverter exposes the
current ColorHex as a SolidColorBrush so the closed-state swatch
shows the live color without code-behind plumbing."
```

---

## Task 7: `SettingsWindow` body rewrite

Replace the single-button body with a "Party HUD" labelled section: three `BarColorPicker`s (HP / Stamina / Mana), an opacity slider with live "XX %" label, and the relabelled "Reset to defaults" button. Wire each control to `_ctl.UpdateConfig(...)` for live HUD repaints.

**Files:**
- Modify: `src/GamePartyHud/SettingsWindow.xaml`
- Modify: `src/GamePartyHud/SettingsWindow.xaml.cs`

- [ ] **Step 7.1: Rewrite `SettingsWindow.xaml`**

Replace the entire body of `src/GamePartyHud/SettingsWindow.xaml` with:

```xml
<ui:FluentWindow x:Class="GamePartyHud.SettingsWindow"
                 xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
                 xmlns:settings="clr-namespace:GamePartyHud.Settings"
                 Title="Settings"
                 Icon="pack://application:,,,/app.ico"
                 Width="420"
                 SizeToContent="Height"
                 WindowStartupLocation="CenterOwner"
                 WindowBackdropType="Mica"
                 ExtendsContentIntoTitleBar="True"
                 ResizeMode="NoResize">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <ui:TitleBar Grid.Row="0" Title="Settings"/>

        <StackPanel Grid.Row="1" Margin="24,12,24,20" Grid.IsSharedSizeScope="True">

            <!-- Party HUD section -->
            <TextBlock Text="Party HUD"
                       FontSize="14" FontWeight="SemiBold"
                       Margin="0,0,0,4"/>
            <Separator Margin="0,0,0,10" Opacity="0.3"/>

            <!-- HP color -->
            <Grid Margin="0,0,0,8">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto" SharedSizeGroup="SettingsLabelCol"/>
                    <ColumnDefinition Width="*"/>
                </Grid.ColumnDefinitions>
                <TextBlock Grid.Column="0" Text="HP color" VerticalAlignment="Center" Margin="0,0,16,0"/>
                <settings:BarColorPicker Grid.Column="1" x:Name="HpPicker" ColorChanged="OnHpColorChanged"/>
            </Grid>

            <!-- Stamina color -->
            <Grid Margin="0,0,0,8">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto" SharedSizeGroup="SettingsLabelCol"/>
                    <ColumnDefinition Width="*"/>
                </Grid.ColumnDefinitions>
                <TextBlock Grid.Column="0" Text="Stamina color" VerticalAlignment="Center" Margin="0,0,16,0"/>
                <settings:BarColorPicker Grid.Column="1" x:Name="StaminaPicker" ColorChanged="OnStaminaColorChanged"/>
            </Grid>

            <!-- Mana color -->
            <Grid Margin="0,0,0,12">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto" SharedSizeGroup="SettingsLabelCol"/>
                    <ColumnDefinition Width="*"/>
                </Grid.ColumnDefinitions>
                <TextBlock Grid.Column="0" Text="Mana color" VerticalAlignment="Center" Margin="0,0,16,0"/>
                <settings:BarColorPicker Grid.Column="1" x:Name="ManaPicker" ColorChanged="OnManaColorChanged"/>
            </Grid>

            <!-- Panel opacity -->
            <Grid Margin="0,0,0,18">
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="Auto" SharedSizeGroup="SettingsLabelCol"/>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>
                <TextBlock Grid.Column="0" Text="Panel opacity" VerticalAlignment="Center" Margin="0,0,16,0"/>
                <Slider Grid.Column="1"
                        x:Name="OpacitySlider"
                        Minimum="0.10" Maximum="1.0"
                        LargeChange="0.05" SmallChange="0.01"
                        TickFrequency="0.1"
                        IsSnapToTickEnabled="False"
                        VerticalAlignment="Center"
                        ValueChanged="OnOpacityChanged"/>
                <TextBlock Grid.Column="2"
                           x:Name="OpacityValueLabel"
                           Width="40"
                           Margin="8,0,0,0"
                           TextAlignment="Right"
                           VerticalAlignment="Center"
                           FontFamily="Consolas, Cascadia Mono, monospace"
                           FontSize="11"/>
            </Grid>

            <ui:Button x:Name="ResetButton"
                       Appearance="Secondary"
                       Icon="ArrowReset24"
                       Content="Reset to defaults"
                       HorizontalAlignment="Stretch"
                       Padding="12,8"
                       Click="OnResetHud"/>
        </StackPanel>
    </Grid>
</ui:FluentWindow>
```

- [ ] **Step 7.2: Rewrite `SettingsWindow.xaml.cs`**

Replace the entire body of `src/GamePartyHud/SettingsWindow.xaml.cs` with:

```csharp
using System;
using System.Windows;
using GamePartyHud.Config;
using GamePartyHud.Diagnostics;
using Wpf.Ui.Controls;

namespace GamePartyHud;

/// <summary>
/// Small modal opened from the gear icon on <see cref="MainWindow"/>.
/// Hosts the Party HUD customisation: per-bar color pickers, panel
/// opacity slider, and the master "Reset to defaults" button.
///
/// All controls write changes live (no Apply / Cancel) — each one calls
/// _ctl.UpdateConfig(...) which propagates to HudTheme and repaints the
/// HUD on the next render tick.
/// </summary>
public partial class SettingsWindow : FluentWindow
{
    private readonly MainWindow.IController _ctl;
    private bool _populating;

    public SettingsWindow(MainWindow.IController controller)
    {
        InitializeComponent();
        _ctl = controller;
        PopulateFromConfig();
    }

    private void PopulateFromConfig()
    {
        _populating = true;
        try
        {
            var cfg = _ctl.Config;
            HpPicker.ColorHex      = cfg.HpBarColor;
            StaminaPicker.ColorHex = cfg.StaminaBarColor;
            ManaPicker.ColorHex    = cfg.ManaBarColor;
            OpacitySlider.Value    = cfg.HudBackgroundOpacity;
            UpdateOpacityLabel(cfg.HudBackgroundOpacity);
        }
        finally { _populating = false; }
    }

    private void UpdateOpacityLabel(double v) =>
        OpacityValueLabel.Text = $"{(int)Math.Round(v * 100)} %";

    private void OnHpColorChanged(object? sender, EventArgs e)
    {
        if (_populating) return;
        _ctl.UpdateConfig(_ctl.Config with { HpBarColor = HpPicker.ColorHex });
    }

    private void OnStaminaColorChanged(object? sender, EventArgs e)
    {
        if (_populating) return;
        _ctl.UpdateConfig(_ctl.Config with { StaminaBarColor = StaminaPicker.ColorHex });
    }

    private void OnManaColorChanged(object? sender, EventArgs e)
    {
        if (_populating) return;
        _ctl.UpdateConfig(_ctl.Config with { ManaBarColor = ManaPicker.ColorHex });
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateOpacityLabel(e.NewValue);
        if (_populating) return;
        _ctl.UpdateConfig(_ctl.Config with { HudBackgroundOpacity = e.NewValue });
    }

    private void OnResetHud(object sender, RoutedEventArgs e)
    {
        _ctl.ResetHudToDefaults();
        PopulateFromConfig();          // refresh the popup's own swatches + slider
        Log.Info("SettingsWindow: Reset to defaults clicked.");
        Close();
    }
}
```

- [ ] **Step 7.3: Build**

```
dotnet build src/GamePartyHud/GamePartyHud.csproj
```
Expected: build clean.

- [ ] **Step 7.4: Run tests**

```
dotnet test tests/GamePartyHud.Tests/GamePartyHud.Tests.csproj
```
Expected: 182/182 pass (no new tests; UI is manually verified).

- [ ] **Step 7.5: Commit**

```
git add src/GamePartyHud/SettingsWindow.xaml src/GamePartyHud/SettingsWindow.xaml.cs
git commit -m "feat(settings): Party HUD section with color pickers + opacity slider

SettingsWindow grows from a single Reset button to a 'Party HUD'
labelled section containing three BarColorPickers (HP / Stamina /
Mana), an opacity Slider (10-100%, default 40%) with live 'XX %'
label, and the relabelled 'Reset to defaults' button.

All controls write changes live: each handler calls
_ctl.UpdateConfig(_ctl.Config with { X = ... }), which propagates
to HudTheme.RefreshFrom and the HUD repaints on the next render
tick. SizeToContent='Height' grows the popup as needed; Width
bumped from 360 to 420 to fit the rows comfortably."
```

---

## Task 8: Final manual verification (spec §10 checklist)

UI cannot be verified autonomously. The user runs the app and walks the spec's checklist.

- [ ] **Step 8.1: Walk the spec §10 checklist**

```
dotnet run --project src/GamePartyHud/GamePartyHud.csproj
```

Tick each item from `docs/superpowers/specs/2026-05-17-hud-settings-design.md` §10:

1. Fresh install — Settings popup shows the "Party HUD" section, 3 colour pickers at default hex values, opacity slider at 40%, Reset button.
2. Click a swatch → SV+hue popup appears below; preview matches the swatch colour.
3. Drag in the SV square → preview, hex, and HUD overlay's bar repaint live.
4. Drag the hue strip → SV backing colour rotates; preview + HUD repaint.
5. Edit hex with a valid value → sliders, preview, and HUD update; invalid input flips the hex border to pink with a tooltip.
6. Press Escape inside the popup → hex/SV/hue revert to the popup-open value; popup closes.
7. Click outside the popup → current value commits.
8. Drag opacity slider → HUD's dark panel goes more/less transparent live; "XX %" label updates.
9. Click "Reset to defaults" → HUD snaps to (100,100) at scale 1.0 with default colours + opacity 40%; popup's controls refresh accordingly; popup closes.
10. Quit + relaunch → all customised values persist.
11. Manually edit `config.json`, set `"hudBackgroundOpacity": 5.0` → app starts; opacity clamped to 1.0; next Save normalises on disk.
12. Manually corrupt a colour field to `"#XYZ"` → app starts; theme falls back to default for that bar (HudTheme uses `Brushes.Transparent` as a guard; verify HUD still shows the bar — if not, check `HudColor.TryParse` returns null and theme falls through).
13. `dotnet test` confirms 182/182.

- [ ] **Step 8.2: Verify the diff scope**

```
git diff main --stat
```

Expected:
```
docs/superpowers/specs/2026-05-17-hud-settings-design.md   (new)
docs/superpowers/plans/2026-05-17-hud-settings-plan.md     (new)
src/GamePartyHud/App.xaml.cs                               (5 edits)
src/GamePartyHud/Config/AppConfig.cs                       (4 new fields)
src/GamePartyHud/Config/ConfigStore.cs                     (sanitise + apply)
src/GamePartyHud/Hud/HudTheme.cs                           (new)
src/GamePartyHud/Hud/HudWindow.xaml                        (binding swap)
src/GamePartyHud/Hud/MemberCard.xaml                       (3 binding swaps)
src/GamePartyHud/MainWindow.xaml.cs                        (IController extension)
src/GamePartyHud/Settings/BarColorPicker.xaml              (new)
src/GamePartyHud/Settings/BarColorPicker.xaml.cs           (new)
src/GamePartyHud/Settings/HudColor.cs                      (new)
src/GamePartyHud/SettingsWindow.xaml                       (body rewrite)
src/GamePartyHud/SettingsWindow.xaml.cs                    (body rewrite)
tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs        (fixture grow + 2 tests)
tests/GamePartyHud.Tests/Settings/HudColorTests.cs         (new, 8 tests)
```

If anything else shows up, investigate — likely an inadvertent edit.

---

## Done criteria

After Task 8 passes:
- `dotnet build` clean, `dotnet test` green (182/182), all spec §10 manual checks confirmed.
- `git log main..HEAD --oneline` shows spec + plan + 7 implementation commits (Tasks 1–7).
- Diff scope matches Step 8.2.

Out of scope (do not do here):
- Per-character / per-preset colours.
- Color picker for nickname text or role-glyph tile.
- HudScale / HudPosition sliders inside Settings.
- Theme import/export.
- Apply/Cancel buttons (changes are live).
- Animations on color/opacity change.
