# Party HUD Settings

**Date:** 2026-05-17
**Scope:** `SettingsWindow.xaml(.cs)`, `AppConfig.cs` (+ `ConfigStore.cs` sanitise), `HudWindow.xaml`, `MemberCard.xaml`, `App.xaml.cs`, `MainWindow.IController`. New `src/GamePartyHud/Settings/` folder (`BarColorPicker`, `HudColor`). New `src/GamePartyHud/Hud/HudTheme.cs`. New tests under `tests/GamePartyHud.Tests/Settings/`.

## Goals

Extend the `SettingsWindow` modal (currently one-button: "Reset HUD position") with a **Party HUD** section that lets the user customise the look of the in-game HUD:

1. **Per-bar colors.** Pick a single colour each for the HP / Stamina / Mana bars. The HUD applies it as a gradient by auto-darkening the chosen colour by 30% for the bottom stop, so the visual feel matches today's hand-tuned palette.
2. **Panel background opacity.** Adjust the alpha of the HUD overlay's dark translucent panel (currently fixed at ~40%) with a slider from 10% to 100%.
3. **Reset = everything.** The existing Reset button now restores HUD position, scale, lock state, colors, AND opacity to defaults — not just position.

Changes apply live: dragging a picker / slider in the popup updates the HUD overlay within ~16 ms (next render tick), so the user can fine-tune against the actual game.

## Non-goals

- Per-character / per-preset colors. Colors stay global per-machine.
- Color picker for the nickname overlay text, the role-glyph tile, or other HUD chrome. Only the three bar gradients + the panel background are themeable.
- Sliders / spinners for `HudScale` or `HudPosition` inside Settings. Those still come from in-place HUD interactions (drag + resize grip).
- Importing / exporting themes as portable JSON.
- Animations when colors / opacity change. The binding flip is instantaneous on the next render tick.
- "OK / Apply / Cancel" buttons. Changes commit immediately (live), and the Reset button is the only explicit revert action.
- Validator-style global error surface. Hex parsing rejects bad input by leaving the previous colour in place + a UI hint; nothing fancier.
- Any change to capture, networking, party state, preset selector, tray, Discord notifier, or the Bars section of the main window.

## Reference

User feedback summarised from chat: *"Add 'Party HUD Settings' section with the following functionality: 1) allow users to change colors of each bar with color picker. 2) allow to adjust opacity of black HUD border. 3) reset button that we have now, should reset everything to default (position, colors, size)."*

Brainstorming decisions captured:
- Color picker: inline swatch + popup with a Photoshop-style SV (saturation/value) square + vertical hue slider + hex input.
- Color depth: single colour per bar; HUD auto-derives the gradient bottom stop by darkening 30%.
- Apply timing: live updates (no explicit Apply button).
- Opacity range: 10–100% (prevents fully-invisible HUD chrome).
- Storage scope: global per-machine (same as `HudPosition`, `HudScale`).
- Popup commits on outside-click; Escape reverts to the value at popup-open.

## Design

### 1. SettingsWindow layout

```
┌─ Settings ─────────────────────────────────────┐
│                                                │
│  Party HUD                                     │
│  ─────────                                     │
│   HP color           [■  #FF821414]            │
│   Stamina color      [■  #FF977E2E]            │
│   Mana color         [■  #FF1A45A4]            │
│   Panel opacity      [────●────]   40 %        │
│                                                │
│  [ ↺  Reset to defaults ]                      │
│                                                │
└────────────────────────────────────────────────┘
```

- "Party HUD" is a section heading (`FontSize=14 SemiBold`) at the top of the body. Future settings sections (audio, network, etc.) can sit below without restructuring.
- Three colour rows: left-aligned label, right-side `BarColorPicker`. Label column and picker column align via `Grid.IsSharedSizeScope` on the section StackPanel, with `SharedSizeGroup="SettingsLabelCol"` on the label column and `SharedSizeGroup="SettingsValueCol"` on the value column.
- One opacity row: label + `Slider` (`Minimum=0.1 Maximum=1.0 LargeChange=0.05 SmallChange=0.01 TickFrequency=0.1 IsSnapToTickEnabled=False`) + a live "XX %" `TextBlock` to the right.
- Reset button at the bottom; relabelled from "Reset HUD position" → "Reset to defaults". Same `ArrowReset24` icon. Resets all five HUD-related fields (position, scale, lock state, three colors, opacity) in a single `UpdateConfig` call.

`SettingsWindow.Width` grows from 360 → 420 to fit the labelled rows + slider comfortably.

### 2. `AppConfig` fields (all global / per-machine)

| Field | Type | Default | Notes |
|---|---|---|---|
| `HpBarColor` | `string` | `"#FF821414"` | Hex `#AARRGGBB`. Always opaque alpha; the user picks RGB only. |
| `StaminaBarColor` | `string` | `"#FF977E2E"` | Same shape. |
| `ManaBarColor` | `string` | `"#FF1A45A4"` | Same shape. |
| `HudBackgroundOpacity` | `double` | `0.40` | Clamped to `[0.1, 1.0]` on load (same pattern as `SanitiseHudScale`). Drives the alpha of both `LinearGradientBrush` stops on `HudWindow.RootBorder.Background`. |

Stored as hex strings because (a) `System.Text.Json` round-trips strings trivially, (b) `config.json` stays human-readable for hand-edits, (c) ARGB hex literals already pervade the XAML.

`ConfigStore.Load` gets a new `SanitiseHudBackgroundOpacity(double)` helper following the same shape as `SanitiseHudScale`: clamps `[0.1, 1.0]`, rejects `NaN`/`Infinity` → falls back to `0.40`.

### 3. `BarColorPicker` control

New `UserControl` at `src/GamePartyHud/Settings/BarColorPicker.xaml(.cs)`.

**Closed state (the row):**

```
[■] #FF821414
 ↑     ↑
 swatch hex
```

- **Swatch:** `Border 24×24`, `CornerRadius=3`, `BorderBrush="#33FFFFFF" BorderThickness="1"`, `Background` = current colour. Cursor `Hand`; hover brightens `BorderBrush` to `#88FFFFFF`. Clicking toggles the popup.
- **Hex label:** read-only `TextBlock` (`FontFamily="Consolas, Cascadia Mono, monospace" FontSize=11`) showing the current `#AARRGGBB`. Updates live as the popup drags.

**Open state — `Popup` attached to the swatch:**

```
┌──────────────────────────────────────┐
│  ┌────────────────────┐  ┌─┐         │
│  │                    │  │ │         │
│  │   Saturation/Value │  │H│         │
│  │       square       │  │u│         │
│  │   (200×160)        │  │e│         │
│  │   click/drag       │  │ │         │
│  │   to pick          │  │ │         │
│  └────────────────────┘  └─┘         │
│                                      │
│   Hex   [#FF821414       ]           │
│                                      │
│   Preview                            │
│   ┌─────────────────────────────┐    │
│   │ ▆▆▆▆▆▆▆▆▆▆▆▆▆▆▆▆▆▆▆▆▆▆▆▆▆▆ │    │
│   └─────────────────────────────┘    │
└──────────────────────────────────────┘
```

`Popup` properties: `StaysOpen=False`, `Placement=Bottom`, `PlacementTarget=` the swatch `Border`, `AllowsTransparency=True`, `PopupAnimation=Fade`.

**SV square** (`200×160 Border`) — three layered `Rectangle`s inside a `Grid`:
1. A backing `SolidColorBrush` set to the pure hue currently picked on the strip. Updated whenever the hue strip's value changes.
2. Horizontal `LinearGradientBrush` `#FFFFFFFF` → `#00FFFFFF` (white → transparent) — saturation axis.
3. Vertical `LinearGradientBrush` `#00000000` → `#FF000000` (transparent → black) — value axis.

A small `Ellipse` cursor (`Width=10 Height=10 Stroke=White StrokeThickness=2 IsHitTestVisible=False`) tracks the current SV position via `Canvas.Left` / `Canvas.Top` updates inside an absolutely-positioned overlay Canvas. `MouseLeftButtonDown` + `MouseMove` (while LMB held) + `MouseLeftButtonUp` translate position → saturation (`x / width`) + value (`1 - y / height`).

**Hue strip** (`Rectangle 16×160` to the right of the square) — vertical `LinearGradientBrush` with 7 stops spanning the rainbow:

| Offset | Colour |
|---|---|
| 0.000 | `#FFFF0000` (red) |
| 0.167 | `#FFFFFF00` (yellow) |
| 0.333 | `#FF00FF00` (green) |
| 0.500 | `#FF00FFFF` (cyan) |
| 0.667 | `#FF0000FF` (blue) |
| 0.833 | `#FFFF00FF` (magenta) |
| 1.000 | `#FFFF0000` (red, wrap) |

A small horizontal arrow `Polygon` cursor on the right edge marks the current hue position. Same mouse-drag handlers translate Y → hue (0–360°).

**Hex input** (`TextBox Width=110 FontFamily="Consolas"`) — accepts `#RRGGBB` (alpha defaults to `FF`) or `#AARRGGBB`. Invalid input keeps the previous colour, shows pink-tinted `BorderBrush`, and a tooltip `"Use #RRGGBB or #AARRGGBB"`. Sliders/square don't react to invalid input until it parses cleanly.

**Preview Border** (`200×22 CornerRadius=2`) — `LinearGradientBrush` with stop 0 = picked colour, stop 1 = picked colour × 0.7 (via `HudColor.Darken`). Shows exactly what the in-game bar will look like.

**Internal state model:**

The picker holds its colour as **HSV** internally so dragging in the square at value=0 doesn't lose hue precision. On any drag / hex edit:
1. Update internal HSV tuple.
2. Recompute RGB → `CurrentColor`.
3. Update SV cursor position, hue cursor position, hex `TextBox`, swatch fill, preview gradient, backing hue brush of the SV square — all guarded by a single `_suppressFeedback` flag so the hex `TextChanged` event doesn't re-trigger the same recompute.

When the popup opens (`Loaded`): parse `ColorHex` → seed HSV → position cursors. The pre-popup value is cached in `_revertSnapshot`. `Escape` while the popup is focused → revert `ColorHex` to `_revertSnapshot` and close. Outside-click → commit the current value and close. There are **no** OK / Cancel buttons.

**Public surface of `BarColorPicker`:**

```csharp
public sealed class BarColorPicker : UserControl
{
    public string Label { get; set; }
    public string ColorHex { get; set; }              // two-way; "#AARRGGBB"
    public event EventHandler? ColorChanged;          // fires on every commit
}
```

`SettingsWindow` hosts three named instances (`HpColorPicker`, `StaminaColorPicker`, `ManaColorPicker`) bound to the corresponding `AppConfig` field. `ColorChanged` handler in `SettingsWindow.xaml.cs` calls `_ctl.UpdateConfig(_ctl.Config with { HpBarColor = picker.ColorHex })`.

### 4. `HudColor` math helper (pure logic, fully testable)

```csharp
namespace GamePartyHud.Settings;

public static class HudColor
{
    /// <summary>Parse #RRGGBB or #AARRGGBB into ARGB bytes. Returns null on bad input.</summary>
    public static (byte A, byte R, byte G, byte B)? TryParse(string hex);

    /// <summary>Format ARGB bytes as "#AARRGGBB".</summary>
    public static string Format(byte a, byte r, byte g, byte b);

    /// <summary>RGB (each 0–255) → HSV (H 0–360, S 0–1, V 0–1).</summary>
    public static (double H, double S, double V) RgbToHsv(byte r, byte g, byte b);

    /// <summary>HSV → RGB (each 0–255).</summary>
    public static (byte R, byte G, byte B) HsvToRgb(double h, double s, double v);

    /// <summary>Darken an RGB triple by the given factor (0–1). Used by the
    /// picker preview AND by HudTheme when building the bottom gradient stop,
    /// so what you see in the popup is what you get in-game.</summary>
    public static (byte R, byte G, byte B) Darken((byte R, byte G, byte B) rgb, double factor);
}
```

The shared 30% darkening factor (`Darken(..., 0.70)`) is hardcoded in `HudTheme`'s brush construction so the picker preview and runtime HUD produce identical gradients.

### 5. `HudTheme` — live-update view-model

New `INotifyPropertyChanged` class at `src/GamePartyHud/Hud/HudTheme.cs`:

```csharp
public sealed class HudTheme : INotifyPropertyChanged
{
    public Brush HpBarBrush             { get; private set; }
    public Brush StaminaBarBrush        { get; private set; }
    public Brush ManaBarBrush           { get; private set; }
    public Brush PanelBackgroundBrush   { get; private set; }

    public HudTheme(AppConfig cfg);
    public void RefreshFrom(AppConfig cfg);

    public event PropertyChangedEventHandler? PropertyChanged;
}
```

- The three bar brushes are `LinearGradientBrush`es with two stops: top = the chosen colour, bottom = `Darken(colour, 0.7)`. Direction `(0,0)→(0,1)` — matches the existing inline brushes.
- `PanelBackgroundBrush` is the existing two-stop dark gradient (`#1B1B1E` top → `#121215` bottom) but with both stops' alpha set to `Math.Round(HudBackgroundOpacity * 255)`. (Hex stops in the XAML today read `#661B1B1E` / `#66121215` — the leading `66` is the alpha byte we're replacing.)
- `RefreshFrom(cfg)` re-parses the four config fields, rebuilds whichever brushes actually changed (cheap byte comparison on the parsed colour to short-circuit the no-op case), and raises `PropertyChanged` on the changed ones. WPF data binding picks the new brush up on the next render tick — no polling, no per-tick capture overhead.

A single instance lives on `App`, constructed at startup, refreshed inside `App.UpdateConfig`. Exposed via a new `IController.HudTheme { get; }` property so `SettingsWindow` and other windows can reach it through the existing controller.

### 6. HUD overlay binding hookup

**`HudWindow.xaml`**

The `RootBorder.Background` LinearGradientBrush becomes a binding:

```xml
<Border x:Name="RootBorder"
        Padding="6,4"
        CornerRadius="3"
        Background="{Binding HudTheme.PanelBackgroundBrush}"
        ...>
```

`HudWindow.DataContext` is set in code-behind during construction to a tiny anonymous holder that exposes `HudTheme`. The cleaner alternative — a real `HudWindowViewModel` — is overkill for a single property; we use the lightest path.

**`MemberCard.xaml`**

The three hardcoded `LinearGradientBrush` blocks inside the HP / Stamina / Mana row Borders become `{Binding}`s into the HUD-wide `HudTheme`. Because `MemberCard` is inside `ItemsControl.ItemsPanel`, its `DataContext` is the per-member `HudMember` view-model — *not* the HUD-wide `HudTheme`. We reach the theme via `RelativeSource={RelativeSource AncestorType=Window}` and a path through the Window's DataContext:

```xml
<Border.Background>
    <Binding Path="DataContext.HudTheme.HpBarBrush"
             RelativeSource="{RelativeSource AncestorType=Window}"/>
</Border.Background>
```

Same shape for the Stamina and Mana row backgrounds.

### 7. Reset to defaults

The existing button's `Click` handler is rewritten:

```csharp
private void OnResetToDefaults(object sender, RoutedEventArgs e)
{
    _ctl.ResetHudToDefaults();
    PopulateFromConfig();
    Close();
}
```

`IController.ResetHudLayout` is renamed `ResetHudToDefaults` and extended:

```csharp
public void ResetHudToDefaults()
{
    var d = AppConfig.Defaults;
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
    _hud!.Left  = _config.HudPosition.X;
    _hud.Top    = _config.HudPosition.Y;
    _hud.Scale  = _config.HudScale;
    _theme.RefreshFrom(_config);
    try { _store?.Save(_config); }
    catch (Exception ex) { Log.Error("Failed to persist config after Reset.", ex); }
    Log.Info("App: reset HUD to defaults (position, scale, colors, opacity).");
}
```

The HUD position / scale apply directly to `_hud` (same as today's `ResetHudLayout`); the colour / opacity changes propagate through `_theme.RefreshFrom`. Both happen before the disk save so a crash during persistence still leaves the in-memory app in the reset state.

### 8. Testing

Per `CLAUDE.md`, UI surfaces (the picker popup, SettingsWindow layout, HUD live-paint) are manually verified. Pure logic gets unit tests in `tests/GamePartyHud.Tests/Settings/HudColorTests.cs`:

- `TryParse_RrggbbHex_AssumesOpaqueAlpha` — `#FFAABB` → `(0xFF, 0xFF, 0xAA, 0xBB)`.
- `TryParse_AarrggbbHex_PreservesAlpha` — `#80FFAABB` → `(0x80, 0xFF, 0xAA, 0xBB)`.
- `TryParse_InvalidHex_ReturnsNull` — `"#XYZ"`, `"#FF"`, `"nothex"`.
- `Format_RoundTripsThroughTryParse` — for a dozen sample colours.
- `RgbToHsv_ReturnsKnownValuesForPrimaryColours` — pure red → `(0, 1, 1)`, pure green → `(120, 1, 1)`, etc.
- `HsvToRgb_RoundTripsThroughRgbToHsv` — for the same corpus.
- `Darken_HalvesAllChannelsAtFactor05` — input `(200, 100, 50)` × 0.5 → `(100, 50, 25)`.
- `Darken_ClampsResultToByteRange` — factor > 1 caps at 255 / factor < 0 floors at 0.

Existing `ConfigStoreTests.RoundTrip_PreservesEverythingExceptBinaryOwnedFields` automatically exercises the 4 new fields (it stuffs non-default values into all user-owned fields). One small additional test added: `Load_HudBackgroundOpacity_AboveOne_ClampedToOne` / `Load_HudBackgroundOpacity_BelowMin_ClampedToTenth`, mirroring the existing `Load_HudScale_*` tests.

### 9. Files

**Created:**
- `src/GamePartyHud/Settings/BarColorPicker.xaml` + `.xaml.cs`
- `src/GamePartyHud/Settings/HudColor.cs`
- `src/GamePartyHud/Hud/HudTheme.cs`
- `tests/GamePartyHud.Tests/Settings/HudColorTests.cs`

**Modified:**
- `src/GamePartyHud/Config/AppConfig.cs` — 4 new fields.
- `src/GamePartyHud/Config/ConfigStore.cs` — new `SanitiseHudBackgroundOpacity` helper; apply in `Load`.
- `src/GamePartyHud/SettingsWindow.xaml` + `.xaml.cs` — full body rewrite.
- `src/GamePartyHud/MainWindow.xaml.cs` — `IController` interface: rename `ResetHudLayout` → `ResetHudToDefaults`, add `HudTheme HudTheme { get; }`.
- `src/GamePartyHud/App.xaml.cs` — construct `HudTheme`; expose via `IController`; refresh on every `UpdateConfig`; rename + extend Reset helper; set `HudWindow.DataContext`.
- `src/GamePartyHud/Hud/HudWindow.xaml` — `RootBorder.Background` becomes `{Binding}`; `MemberCard` bar Borders become `{Binding}`s with `RelativeSource` ancestor lookup.
- `src/GamePartyHud/Hud/MemberCard.xaml` — three hardcoded `LinearGradientBrush` blocks become `{Binding}`s.
- `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs` — round-trip fixture grows 4 fields; 2 new clamp tests.

**Untouched:** Capture pipeline, networking, party state, preset selector, tray, Discord notifier, calibration window, Bars section of MainWindow.

### 10. Manual verification

Before claiming done, run the app and verify:

1. Fresh install — Settings popup shows a "Party HUD" section with three colour pickers (HP red, Stamina amber, Mana blue) at the default hex values, an opacity slider at 40%, and a "Reset to defaults" button.
2. Click a swatch → popup appears below it with SV square, hue slider, hex input, preview gradient.
3. Drag in the SV square → preview updates live, hex updates live, the HUD overlay's bar repaints live.
4. Drag the hue slider → SV square's backing colour changes hue, cursor stays at the same S/V position, preview + HUD repaint live.
5. Edit hex → if valid, sliders + preview + HUD update; if invalid, hex border turns pink with a tooltip and nothing else changes.
6. Press Escape inside the popup → hex/SV/hue revert to the value the popup opened with; popup closes.
7. Click outside the popup → current value commits; popup closes; saved colour appears in the swatch + hex label of the row.
8. Drag opacity slider → HUD's dark panel goes more / less transparent live; "XX %" text updates.
9. Click "Reset to defaults" → HUD snaps back to default position (100, 100), scale 1.0, default bar colours, opacity 40%; popup's swatches + slider also refresh to the defaults.
10. Quit + relaunch → all customised values persist; HUD opens with the user's chosen colours / opacity / position / scale.
11. Manually edit `config.json` to set `"HudBackgroundOpacity": 5.0` → app starts, value clamped to 1.0; next Save normalises to 1.0 on disk.
12. Manually corrupt a colour field to `"#XYZ"` → app starts; the colour falls back to the default for that bar (parsing failure in `HudTheme.RefreshFrom` keeps the previous brush, which on a corrupt-from-startup case means the default `AppConfig.Defaults.<X>BarColor`).
13. `dotnet test` — existing 172 pass + new `HudColorTests` (8 tests) + 2 new `ConfigStoreTests` clamp tests = 182/182.
