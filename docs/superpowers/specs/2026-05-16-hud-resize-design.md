# HUD resize (proportional, with reset)

**Date:** 2026-05-16
**Scope:** `src/GamePartyHud/Hud/HudWindow.{xaml,xaml.cs}`, `src/GamePartyHud/Config/AppConfig.cs`, `src/GamePartyHud/App.xaml.cs`, `src/GamePartyHud/MainWindow.{xaml,xaml.cs}`. No capture, party, network, or message-protocol changes.

## Goals

Three user-visible changes to the party HUD:

1. While the HUD is **unlocked**, the user can resize it by dragging a grip in the bottom-right corner. Every resize is **proportional** — both axes scale by the same factor, the HUD never stretches.
2. The scale is bounded: **0.5× to 2.0×** of the baseline size. The grip clamps; hand-edited config values outside the range are clamped on load.
3. A **"Reset HUD position & size"** button in the main app window restores the HUD to its baseline position `(100, 100)` and scale `1.0×`. No confirmation dialog.

## Non-goals

- No changes to capture, HP analysis, party state, messaging, signalling, tray, or relay.
- No new fields in `MemberState` / `PartyMessage`; no protocol changes.
- No per-monitor or per-party saved scales.
- No configurable min/max in settings (the 0.5–2.0 range is fixed).
- No automatic snap-back into visible monitor bounds — the Reset button is the explicit escape hatch when the HUD is dragged off-screen.
- No visible scale-percent indicator during drag.

## Design

### 1. Config additions

`AppConfig` gets one new field:

```csharp
public sealed record AppConfig(
    ...,
    double HudScale = 1.0);
```

- Default: `1.0` (added to `AppConfig.Defaults`).
- Clamped to `[0.5, 2.0]` on load. `NaN`, `±Infinity`, and out-of-range values fall back to `1.0`.
- Persisted in `%AppData%\GamePartyHud\config.json` alongside `HudPosition`.

No other config or type changes.

### 2. Scale model & XAML

`HudWindow` gets a new `Scale` dependency property (`double`, default `1.0`). `HudWindow.xaml` wraps the root `Border` with a `LayoutTransform` bound to that property:

```xaml
<Border x:Name="RootBorder" ...>
    <Border.LayoutTransform>
        <ScaleTransform
            ScaleX="{Binding Scale, RelativeSource={RelativeSource AncestorType=Window}}"
            ScaleY="{Binding Scale, RelativeSource={RelativeSource AncestorType=Window}}"/>
    </Border.LayoutTransform>
    <StackPanel>...</StackPanel>
</Border>
```

Why `LayoutTransform`, not `RenderTransform`: `LayoutTransform` participates in the layout pass, so `SizeToContent="WidthAndHeight"` recomputes the window's outer size against the *scaled* desired size of the content. The window grows or shrinks around the scaled HUD; the existing fixed pixel sizes (`MemberCard` 200×24, role tile 18×18, bar width 174, `HpWidthConverter` parameters, font sizes, drop-shadow blur) all scale together because they're measured in DIPs that pass through the transform.

No edits to `MemberCard.xaml`, `ColumnMajorUniformGrid`, or `HpWidthConverter`.

### 3. Corner grip

A small drag handle lives in the bottom-right of `RootBorder`, inside the scaled tree (so it scales with the rest).

`Border` only takes one child, and `RootBorder`'s current child is a `StackPanel`. To overlay a grip in the bottom-right corner without disturbing the stack, the immediate child of `RootBorder` becomes a single-cell `Grid` containing the existing `StackPanel` *plus* the new `ResizeGrip`:

```xaml
<Border x:Name="RootBorder" ...>
    <Border.LayoutTransform>... (see §2) ...</Border.LayoutTransform>
    <Grid>
        <StackPanel>
            <!-- existing content: lock-button row, ItemsControl -->
        </StackPanel>
        <Border x:Name="ResizeGrip"
                Width="10" Height="10"
                HorizontalAlignment="Right" VerticalAlignment="Bottom"
                Background="#88FFFFFF"
                CornerRadius="1"
                Cursor="SizeNWSE"
                Visibility="Collapsed"
                MouseLeftButtonDown="OnGripMouseDown"
                MouseMove="OnGripMouseMove"
                MouseLeftButtonUp="OnGripMouseUp"/>
    </Grid>
</Border>
```

The Grid sizes to its largest child (the StackPanel), and the 10×10 grip aligns to that Grid's bottom-right corner.

- **Visibility**: `Collapsed` when locked, `Visible` when unlocked. Toggled in `UpdateLockVisual()` alongside `LockGlyph.Text` and `RootBorder.BorderThickness`.
- **Hit-testing**:
  - When locked: `WndProc` already returns `HTTRANSPARENT` for everything except the lock-button rect, so the grip is invisible and clicks pass through to the game.
  - When unlocked: `WndProc` already returns `HTCLIENT` for the whole window, so the grip receives mouse events normally.
- **Interaction with existing drag handlers**: `OnMouseLeftButtonDown` currently checks `IsWithinLockButton` and bails. A parallel `IsWithinResizeGrip` check is added with the same shape (walk up the visual tree from `e.OriginalSource`, return true if `ResizeGrip` is an ancestor), so a click on the grip does not trigger window-drag or card-swap. Additionally, the grip's own `MouseLeftButtonDown` sets `e.Handled = true` to belt-and-braces against the routed event reaching the window.

### 4. Drag mechanics

The grip's mouse handlers update `Scale` live during drag:

```csharp
private double _scaleAtDragStart;
private System.Drawing.Point _dragStartScreenPx;
private double _unscaledWidthAtStart;
private double _unscaledHeightAtStart;

private void OnGripMouseDown(object sender, MouseButtonEventArgs e)
{
    _scaleAtDragStart = Scale;
    _dragStartScreenPx = System.Windows.Forms.Cursor.Position;
    _unscaledWidthAtStart  = ActualWidth  / Math.Max(Scale, 0.01);
    _unscaledHeightAtStart = ActualHeight / Math.Max(Scale, 0.01);
    ResizeGrip.CaptureMouse();
    e.Handled = true;
}

private void OnGripMouseMove(object sender, MouseEventArgs e)
{
    if (!ResizeGrip.IsMouseCaptured) return;
    var now = System.Windows.Forms.Cursor.Position;
    double dx = now.X - _dragStartScreenPx.X;
    double dy = now.Y - _dragStartScreenPx.Y;
    double delta = Math.Max(dx / _unscaledWidthAtStart, dy / _unscaledHeightAtStart);
    Scale = Math.Clamp(_scaleAtDragStart + delta, 0.5, 2.0);
}

private void OnGripMouseUp(object sender, MouseButtonEventArgs e)
{
    if (!ResizeGrip.IsMouseCaptured) return;
    ResizeGrip.ReleaseMouseCapture();
    ScaleChangeCommitted?.Invoke(Scale);
    e.Handled = true;
}
```

**Why screen coordinates via `System.Windows.Forms.Cursor.Position`:** as the window resizes mid-drag, both the window's bounds and any element-local coordinate space shift. A monotonic, screen-pixel delta from the initial cursor position is stable regardless of what the window is doing.

**Why `max(dx/W, dy/H)`:** the user may drag in any direction — straight out, straight down, or diagonally. Using the larger normalised axis means "whichever direction you pull, the HUD follows," and because the same scale applies to both axes, the result is always proportional. Negative delta on either axis shrinks (clamped at 0.5×); positive grows (clamped at 2.0×).

**`ScaleChangeCommitted`** is a new event on `HudWindow` of type `Action<double>?`. `App.xaml.cs` subscribes alongside the existing `KickRequested`. It fires once per drag, on mouse-up — not on every move tick.

### 5. Reset button (MainWindow)

A new "HUD layout" subsection is inserted in `MainWindow.xaml`, between the existing "Optional bars" block and the "Party" separator:

```xaml
<Separator Margin="0,28,0,0" Opacity="0.3"/>
<TextBlock Text="HUD layout"
           FontSize="18" FontWeight="SemiBold" Margin="0,20,0,12"/>
<TextBlock Opacity="0.75" Margin="0,0,0,10" TextWrapping="Wrap"
           Text="Unlock the HUD to drag it around or resize it from the bottom-right corner. Use this if you lose it off-screen or want to start over."/>
<ui:Button x:Name="ResetHudButton"
           Content="Reset HUD position &amp; size"
           Icon="ArrowReset24"
           Appearance="Secondary"
           Padding="14,6"
           Click="OnResetHud"/>
```

A new method is added to `MainWindow.IController`:

```csharp
void ResetHudLayout();
```

`MainWindow.OnResetHud` calls `_controller.ResetHudLayout()`.

`App.ResetHudLayout()` implementation:

```csharp
void MainWindow.IController.ResetHudLayout()
{
    if (_hud is null) return;
    _hud.Left = AppConfig.Defaults.HudPosition.X;   // 100
    _hud.Top  = AppConfig.Defaults.HudPosition.Y;   // 100
    _hud.Scale = AppConfig.Defaults.HudScale;       // 1.0
    _config = _config with
    {
        HudPosition = AppConfig.Defaults.HudPosition,
        HudScale    = AppConfig.Defaults.HudScale,
    };
    try { _store?.Save(_config); }
    catch (Exception ex) { Log.Error("Failed to persist config after HUD reset.", ex); }
}
```

No confirmation dialog — both axes are trivially recoverable (drag and resize back).

### 6. Persistence wiring

Two paths save `HudScale`:

- **Drag-end (primary path)** — `HudWindow.ScaleChangeCommitted` fires from `OnGripMouseUp`. `App.xaml.cs` writes `_config = _config with { HudScale = newScale };  _store.Save(_config);`.
- **App exit (belt-and-braces)** — `App.OnExit` already saves `HudPosition`. Extend the `with` expression to include `HudScale = _hud.Scale`.

**Load on startup:** `App.OnStartup` already applies `_config.HudPosition` to `_hud.Left/Top`. Add one line *before* `_hud.Show()`:

```csharp
_hud.Scale = Math.Clamp(_config.HudScale, 0.5, 2.0);
```

The HUD does **not** auto-snap back into a visible monitor on load. That is existing behaviour and the Reset button is the explicit fix for an off-screen HUD.

## Testing

Per `CLAUDE.md`: pure logic gets unit tests; UI is manually verified.

### New unit tests

In `tests/GamePartyHud.Tests/Config`:

- `AppConfigTests.HudScale_OutOfRange_ClampedToBounds` — load with `HudScale = 0.1` → clamped to `0.5`; load with `HudScale = 9.0` → clamped to `2.0`.
- `AppConfigTests.HudScale_NaNOrInfinity_FallsBackToOne` — load with `HudScale = double.NaN` or `±Infinity` → falls back to `1.0`.

(The clamp lives in `ConfigStore.Load` or an `AppConfig.WithSanitisedScale()` helper — implementation choice, but the behaviour must be observable from the test.)

### Manual checklist

Added to `HudSmokeHarness` runs:

1. Unlocked → grip visible bottom-right. Locked → grip hidden.
2. Drag grip diagonally outward → HUD grows proportionally; at the extreme, scale clamps at 2.0×.
3. Drag inward past minimum → clamps at 0.5×; HUD still readable.
4. Drag only horizontally / only vertically → both axes still scale together (proportional).
5. Resize at 5, 10, 11, and 20 members (both 1-col and 2-col layouts) → proportions stay correct, no clipping, second-column overflow still works.
6. Restart the app → HUD opens at the last saved scale and position.
7. Click "Reset HUD position & size" → HUD jumps back to `(100, 100)` at scale `1.0×`; persists across restart.
8. Reset works when the HUD has been dragged off-screen (drag onto a non-existent monitor area, then click Reset).
9. After a resize: lock toggle, drag-to-move, drag-to-swap, and the "Kick from party" context menu all still work.
10. CPU / GPU / RAM 8-hour spot-check stays within the perf budget from `CLAUDE.md`. `LayoutTransform` only re-measures when `Scale` changes, so steady-state cost should be unchanged.

## Risks & mitigations

- **`HpWidthConverter` parameter under scale** — the converter does `width * (percent/100)` with a fixed `ConverterParameter`. Because `LayoutTransform` scales the resulting `Border.Width`, the fill is the right *fraction* of the bar, just visually larger. Verified by manual checklist item 5.
- **`ColumnMajorUniformGrid.MeasureOverride` under scale** — it measures children with `availableSize / cols`. With a `LayoutTransform` ancestor, child measurements happen in unscaled DIPs and the transform multiplies the final visual. Should "just work"; checklist item 5 confirms.
- **Grip click area at 0.5×** — at minimum scale the 10-px grip is ~5 actual screen pixels. Acceptable for an unlocked-mode-only handle. If it feels finicky in practice, widen the base grip to 12–14 px — one-line change.
- **Reset jumps across monitors** — defaults are screen-pixel `(100, 100)` of the primary monitor. If the user's last position was on a high-DPI second monitor, reset jumps it to the primary. That is the intended behaviour of "reset to default".
- **Mouse-capture lost mid-drag (Alt-tab, focus steal)** — `OnGripMouseUp` is the only path that writes config. If capture is lost without a mouse-up, the live `Scale` is still applied to the HUD but not persisted until app exit (the belt-and-braces save in `OnExit` catches it).

## Out of scope / future ideas

- Configurable min/max in settings — YAGNI; the 0.5–2.0 range is generous.
- Auto-snap into visible monitor bounds on startup.
- Per-monitor or per-party saved scales.
- A visible scale-percent indicator while dragging.
- Mouse-wheel resize as an alternative trigger.
