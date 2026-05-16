# HUD Resize Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let users proportionally resize the HUD via a corner grip when unlocked, clamped to 0.5×–2.0×, with a "Reset position & size" button in the main settings window.

**Architecture:** Add `HudScale` (double, default 1.0) to `AppConfig`. Wrap the HUD's root `Border` content in a `Grid` with a `LayoutTransform`-bound `ScaleTransform`. Place a 10×10 `ResizeGrip` `Border` in the Grid's bottom-right corner, visible only when unlocked. Grip mouse handlers update `HudWindow.Scale` from screen-pixel drag delta and clamp to [0.5, 2.0]; `SizeToContent="WidthAndHeight"` automatically re-sizes the window because `LayoutTransform` participates in layout. A new `MainWindow.IController.ResetHudLayout()` method restores `HudPosition = (100, 100)` and `HudScale = 1.0`. Clamping also happens on `ConfigStore.Load` so a hand-edited config can't break the HUD.

**Tech Stack:** C# 12, .NET 8, WPF, xUnit. WPF-UI library for the FluentWindow chrome. System.Windows.Forms for `Cursor.Position`.

**Spec:** [docs/superpowers/specs/2026-05-16-hud-resize-design.md](../specs/2026-05-16-hud-resize-design.md)

---

## File Structure

**Modify:**
- `src/GamePartyHud/Config/AppConfig.cs` — add `HudScale` field.
- `src/GamePartyHud/Config/ConfigStore.cs` — sanitise `HudScale` on load (clamp + NaN/Infinity guard).
- `src/GamePartyHud/Hud/HudWindow.xaml` — wrap content in `Grid` with `LayoutTransform`; add `ResizeGrip`.
- `src/GamePartyHud/Hud/HudWindow.xaml.cs` — add `Scale` DP, `ScaleChangeCommitted` event, grip handlers, `IsWithinResizeGrip`, extend `UpdateLockVisual`.
- `src/GamePartyHud/MainWindow.xaml` — add "HUD layout" section with Reset button.
- `src/GamePartyHud/MainWindow.xaml.cs` — `OnResetHud` handler calling controller.
- `src/GamePartyHud/App.xaml.cs` — extend `IController` with `ResetHudLayout`; implement it; apply `HudScale` on startup; subscribe to `ScaleChangeCommitted` for save; extend `OnExit` save.
- `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs` — add HudScale load-clamp tests.

**Create:** None.

---

## Task 1: Add `HudScale` field to `AppConfig`

**Files:**
- Modify: `src/GamePartyHud/Config/AppConfig.cs`

- [ ] **Step 1: Add `HudScale` parameter to the positional record**

Edit `src/GamePartyHud/Config/AppConfig.cs`. Change the record signature to add `HudScale` as the last parameter with default `1.0`:

```csharp
public sealed record AppConfig(
    BarCalibration? HpCalibration,
    BarCalibration? StaminaCalibration,
    BarCalibration? ManaCalibration,
    CaptureRegion? NicknameRegion,
    string Nickname,
    Role Role,
    HudPosition HudPosition,
    bool HudLocked,
    string? LastPartyId,
    int PollIntervalMs,
    string RelayUrl,
    string RelayFallbackUrl = "",
    bool FullscreenDisclaimerDismissed = false,
    double HudScale = 1.0)
```

- [ ] **Step 2: Add `HudScale: 1.0` to `AppConfig.Defaults`**

In the same file, update the `Defaults` initializer:

```csharp
public static AppConfig Defaults { get; } = new(
    HpCalibration: null,
    StaminaCalibration: null,
    ManaCalibration: null,
    NicknameRegion: null,
    Nickname: "Player",
    Role: Role.Utility,
    HudPosition: new HudPosition(100, 100, 0),
    HudLocked: true,
    LastPartyId: null,
    PollIntervalMs: 1000,
    RelayUrl: DefaultRelayUrl,
    RelayFallbackUrl: DefaultRelayFallbackUrl,
    FullscreenDisclaimerDismissed: false,
    HudScale: 1.0);
```

- [ ] **Step 3: Build to confirm no callers broke**

Run: `dotnet build`
Expected: Build succeeds. (All existing constructor calls use named or positional args that don't include the new optional parameter; tests use `with` which is unaffected.)

- [ ] **Step 4: Commit**

```bash
git add src/GamePartyHud/Config/AppConfig.cs
git commit -m "feat(config): add HudScale field (default 1.0) to AppConfig"
```

---

## Task 2: Clamp `HudScale` on config load (TDD)

**Files:**
- Modify: `src/GamePartyHud/Config/ConfigStore.cs`
- Test: `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs`

- [ ] **Step 1: Add the failing tests**

Append these two tests to `tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs` inside the `ConfigStoreTests` class (before the closing brace):

```csharp
[Fact]
public void Load_HudScale_AboveMax_ClampedToTwo()
{
    // Hand-edited config with an extreme value must be clamped so the HUD
    // stays usable. The grip drag clamps too, but a curious user might edit
    // config.json directly to "stretch" the HUD.
    File.WriteAllText(_tmp, """
{
  "hpCalibration": null,
  "staminaCalibration": null,
  "manaCalibration": null,
  "nicknameRegion": null,
  "nickname": "Test",
  "role": "Tank",
  "hudPosition": { "x": 0, "y": 0, "monitor": 0 },
  "hudLocked": true,
  "lastPartyId": null,
  "pollIntervalMs": 2000,
  "relayUrl": "",
  "hudScale": 9.0
}
""");
    var loaded = new ConfigStore(_tmp).Load();
    Assert.Equal(2.0, loaded.HudScale);
}

[Fact]
public void Load_HudScale_BelowMin_ClampedToHalf()
{
    File.WriteAllText(_tmp, """
{
  "hpCalibration": null,
  "staminaCalibration": null,
  "manaCalibration": null,
  "nicknameRegion": null,
  "nickname": "Test",
  "role": "Tank",
  "hudPosition": { "x": 0, "y": 0, "monitor": 0 },
  "hudLocked": true,
  "lastPartyId": null,
  "pollIntervalMs": 2000,
  "relayUrl": "",
  "hudScale": 0.1
}
""");
    var loaded = new ConfigStore(_tmp).Load();
    Assert.Equal(0.5, loaded.HudScale);
}
```

(The NaN / Infinity guard in `SanitiseHudScale` below is defensive — `System.Text.Json` with `JsonSerializerDefaults.Web` throws on `"NaN"` / `"Infinity"` for double fields, so the guard is unreachable via JSON load. Testing it would require either changing the global JSON options or exposing the helper, both of which are more cost than the one-line guard is worth.)

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ConfigStoreTests.Load_HudScale"`
Expected: Both fail with messages like `Expected: 2 Actual: 9` and `Expected: 0.5 Actual: 0.1` — confirming clamp logic isn't yet applied.

- [ ] **Step 3: Add the sanitise helper and apply it in `Load`**

Edit `src/GamePartyHud/Config/ConfigStore.cs`. After the `using` block at the top, the file already has a single `ConfigStore` class. Inside `Load()`, after the `raw with { RelayUrl = ..., RelayFallbackUrl = ... }` block, change the return to sanitise scale:

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
            HudScale = SanitiseHudScale(raw.HudScale),
        };
    }
    catch (Exception)
    {
        try { File.Move(_path, _path + ".bad-" + DateTime.UtcNow.Ticks, overwrite: true); } catch { }
        return AppConfig.Defaults;
    }
}

private static double SanitiseHudScale(double raw)
{
    // Out-of-range, NaN, or infinite values fall back to safe bounds so the
    // HUD's LayoutTransform never receives a value that would render it
    // invisible, infinitely large, or undefined.
    if (double.IsNaN(raw) || double.IsInfinity(raw)) return 1.0;
    return Math.Clamp(raw, 0.5, 2.0);
}
```

(Leave existing comments above the `RelayUrl`/`RelayFallbackUrl` overrides in place — just append the new `HudScale = ...` line.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ConfigStoreTests.Load_HudScale"`
Expected: 2 pass.

- [ ] **Step 5: Run the full ConfigStoreTests suite to verify nothing regressed**

Run: `dotnet test --filter "FullyQualifiedName~ConfigStoreTests"`
Expected: All tests pass (including the existing round-trip test, since `HudScale = 1.0` survives clamp unchanged).

- [ ] **Step 6: Commit**

```bash
git add src/GamePartyHud/Config/ConfigStore.cs tests/GamePartyHud.Tests/Config/ConfigStoreTests.cs
git commit -m "feat(config): clamp HudScale to [0.5, 2.0] and guard NaN/Infinity on load"
```

---

## Task 3: Add `Scale` dependency property to `HudWindow`

**Files:**
- Modify: `src/GamePartyHud/Hud/HudWindow.xaml.cs`

- [ ] **Step 1: Register the `Scale` DependencyProperty**

In `src/GamePartyHud/Hud/HudWindow.xaml.cs`, just after the `ColumnCount` property block (before the `_isLocked` field), add:

```csharp
/// <summary>
/// Live scale factor applied to the entire HUD via a <c>LayoutTransform</c>.
/// 1.0 = baseline (matches the design dimensions). Bounded to [0.5, 2.0]
/// at all write sites (grip drag, config load). Bound from XAML; persisted
/// to <c>AppConfig.HudScale</c> by <c>App.xaml.cs</c> on drag-end and exit.
/// </summary>
public static readonly DependencyProperty ScaleProperty =
    DependencyProperty.Register(
        nameof(Scale), typeof(double), typeof(HudWindow),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

public double Scale
{
    get => (double)GetValue(ScaleProperty);
    set => SetValue(ScaleProperty, value);
}
```

- [ ] **Step 2: Build to confirm**

Run: `dotnet build`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/GamePartyHud/Hud/HudWindow.xaml.cs
git commit -m "feat(hud): add Scale dependency property to HudWindow"
```

---

## Task 4: Wrap HUD content in a Grid + LayoutTransform + add ResizeGrip

**Files:**
- Modify: `src/GamePartyHud/Hud/HudWindow.xaml`

- [ ] **Step 1: Restructure the XAML**

Replace the entire contents of `src/GamePartyHud/Hud/HudWindow.xaml` with:

```xaml
<Window x:Class="GamePartyHud.Hud.HudWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:hud="clr-namespace:GamePartyHud.Hud"
        WindowStyle="None" AllowsTransparency="True"
        Background="Transparent" Topmost="True"
        ShowInTaskbar="False" ResizeMode="NoResize"
        SizeToContent="WidthAndHeight"
        Title="GamePartyHUD"
        FontFamily="Segoe UI Variable, Segoe UI">
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
        <Border.LayoutTransform>
            <ScaleTransform
                ScaleX="{Binding Scale, RelativeSource={RelativeSource AncestorType=Window}}"
                ScaleY="{Binding Scale, RelativeSource={RelativeSource AncestorType=Window}}"/>
        </Border.LayoutTransform>
        <Grid>
            <StackPanel>
                <Grid HorizontalAlignment="Right" Margin="0,0,0,4">
                    <Button x:Name="LockButton" Width="20" Height="20"
                            Background="Transparent" BorderThickness="0"
                            Click="OnLockButtonClick"
                            Padding="0"
                            Focusable="False"
                            Cursor="Hand">
                        <TextBlock x:Name="LockGlyph" Text="🔒" FontSize="11"
                                   Foreground="#CCFFFFFF"/>
                    </Button>
                </Grid>
                <ItemsControl x:Name="Members">
                    <ItemsControl.ItemsPanel>
                        <ItemsPanelTemplate>
                            <hud:ColumnMajorUniformGrid Rows="10"
                                                        Columns="{Binding ColumnCount,
                                                                  RelativeSource={RelativeSource AncestorType=Window}}"/>
                        </ItemsPanelTemplate>
                    </ItemsControl.ItemsPanel>
                    <ItemsControl.ItemTemplate>
                        <DataTemplate>
                            <hud:MemberCard Margin="2,1">
                                <hud:MemberCard.ContextMenu>
                                    <ContextMenu>
                                        <MenuItem Header="Kick from party" Click="OnKickClick"/>
                                    </ContextMenu>
                                </hud:MemberCard.ContextMenu>
                            </hud:MemberCard>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>
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
</Window>
```

Changes from the previous version:
- `RootBorder` gains a `LayoutTransform` bound to the new `Scale` DP.
- Its child was a `StackPanel`; it is now a single-cell `Grid` containing the old `StackPanel` and a new `ResizeGrip` `Border` that aligns to the Grid's bottom-right corner.
- The `ResizeGrip` declares its three mouse event handlers (implemented in the next task).

- [ ] **Step 2: Build to confirm XAML compiles**

Run: `dotnet build`
Expected: Build fails — the three handlers (`OnGripMouseDown`, `OnGripMouseMove`, `OnGripMouseUp`) referenced from XAML don't exist yet. This is expected; the next task adds them.

- [ ] **Step 3: Do NOT commit yet**

The XAML references handlers that don't exist; commit happens at the end of Task 5 when the file compiles.

---

## Task 5: Implement grip handlers + visibility + hit-test exclusion

**Files:**
- Modify: `src/GamePartyHud/Hud/HudWindow.xaml.cs`

- [ ] **Step 1: Add the `ScaleChangeCommitted` event near other public events**

In `src/GamePartyHud/Hud/HudWindow.xaml.cs`, just below the existing `KickRequested` event declaration:

```csharp
/// <summary>Raised once per drag, on grip mouse-up, with the new (clamped) scale.</summary>
public event Action<double>? ScaleChangeCommitted;
```

- [ ] **Step 2: Add the drag-state fields next to `_dragSource`**

Just below the `_dragStart` / `DragThreshold` field block in `HudWindow.xaml.cs`:

```csharp
// Resize-grip drag state.
private double _scaleAtDragStart;
private System.Drawing.Point _dragStartScreenPx;
private double _unscaledWidthAtStart;
private double _unscaledHeightAtStart;
```

- [ ] **Step 3: Add the three grip handlers**

Append these methods to the `HudWindow` class (before its closing brace):

```csharp
private void OnGripMouseDown(object sender, MouseButtonEventArgs e)
{
    _scaleAtDragStart      = Scale;
    _dragStartScreenPx     = System.Windows.Forms.Cursor.Position;
    // Recover the "unscaled" content size by undoing the current scale, so
    // the drag-delta math is independent of where the user starts.
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
    // Whichever axis the user pulls hardest wins; both axes always scale
    // together (ScaleTransform.ScaleX == ScaleY) so the result is
    // proportional by construction.
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

- [ ] **Step 4: Add `IsWithinResizeGrip` and call it from `OnMouseLeftButtonDown`**

Append this method to `HudWindow` (alongside the existing `IsWithinLockButton`):

```csharp
private bool IsWithinResizeGrip(object source)
{
    var d = source as DependencyObject;
    while (d is not null)
    {
        if (ReferenceEquals(d, ResizeGrip)) return true;
        d = VisualTreeHelper.GetParent(d);
    }
    return false;
}
```

Then update `OnMouseLeftButtonDown` to bail when the click is on the grip. The current method body starts with:

```csharp
protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
{
    base.OnMouseLeftButtonDown(e);
    if (_isLocked) return;
    if (IsWithinLockButton(e.OriginalSource)) return;
    ...
}
```

Change it to:

```csharp
protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
{
    base.OnMouseLeftButtonDown(e);
    if (_isLocked) return;
    if (IsWithinLockButton(e.OriginalSource)) return;
    if (IsWithinResizeGrip(e.OriginalSource)) return;
    ...
}
```

- [ ] **Step 5: Toggle grip visibility from `UpdateLockVisual`**

Replace the existing `UpdateLockVisual` method:

```csharp
private void UpdateLockVisual()
{
    LockGlyph.Text = _isLocked ? "🔒" : "🔓";
    RootBorder.BorderThickness = _isLocked ? new Thickness(0) : new Thickness(1);
    ResizeGrip.Visibility = _isLocked ? Visibility.Collapsed : Visibility.Visible;
}
```

- [ ] **Step 6: Build to confirm**

Run: `dotnet build`
Expected: Build succeeds — all three XAML-referenced handlers now exist.

- [ ] **Step 7: Run the full test suite to verify nothing regressed**

Run: `dotnet test`
Expected: All tests pass. (No new tests, but `ColumnMajorUniformGridTests` and `HudMemberTests` may exercise paths near the changed XAML.)

- [ ] **Step 8: Commit**

```bash
git add src/GamePartyHud/Hud/HudWindow.xaml src/GamePartyHud/Hud/HudWindow.xaml.cs
git commit -m "feat(hud): add corner resize grip with proportional scale drag"
```

---

## Task 6: Apply `HudScale` on startup, save on drag-end and exit

**Files:**
- Modify: `src/GamePartyHud/App.xaml.cs`

- [ ] **Step 1: Apply saved scale before showing the HUD**

In `src/GamePartyHud/App.xaml.cs`, locate this block in `OnStartup`:

```csharp
_hud = new HudWindow();
_sync = new HudViewModelSync(_state, _hud.MemberList);
_hud.Left = _config.HudPosition.X;
_hud.Top = _config.HudPosition.Y;
_hud.Show();
```

Insert one line before `_hud.Show()` (Math.Clamp is belt-and-braces — `ConfigStore.Load` already sanitises, but a direct construction via `AppConfig.Defaults with { ... }` from anywhere in code could bypass that):

```csharp
_hud = new HudWindow();
_sync = new HudViewModelSync(_state, _hud.MemberList);
_hud.Left = _config.HudPosition.X;
_hud.Top = _config.HudPosition.Y;
_hud.Scale = Math.Clamp(_config.HudScale, 0.5, 2.0);
_hud.Show();
```

- [ ] **Step 2: Subscribe to `ScaleChangeCommitted` next to `KickRequested`**

Just below the existing line `_hud.KickRequested += OnKickRequested;` in `OnStartup`, add:

```csharp
_hud.ScaleChangeCommitted += OnHudScaleChanged;
```

- [ ] **Step 3: Implement the handler**

Add this method to `App` (place it near `OnKickRequested`):

```csharp
private void OnHudScaleChanged(double newScale)
{
    _config = _config with { HudScale = newScale };
    try { _store?.Save(_config); }
    catch (Exception ex) { Log.Error("Failed to persist HudScale after drag.", ex); }
}
```

- [ ] **Step 4: Save scale in `OnExit` alongside position**

Find this block in `OnExit`:

```csharp
if (_hud is { } h && _store is { } store)
{
    _config = _config with { HudPosition = new HudPosition(h.Left, h.Top, 0) };
    try { store.Save(_config); } catch (Exception ex) { Log.Error("Final config save failed.", ex); }
}
```

Extend the `with` expression:

```csharp
if (_hud is { } h && _store is { } store)
{
    _config = _config with
    {
        HudPosition = new HudPosition(h.Left, h.Top, 0),
        HudScale = h.Scale,
    };
    try { store.Save(_config); } catch (Exception ex) { Log.Error("Final config save failed.", ex); }
}
```

- [ ] **Step 5: Build to confirm**

Run: `dotnet build`
Expected: Build succeeds.

- [ ] **Step 6: Commit**

```bash
git add src/GamePartyHud/App.xaml.cs
git commit -m "feat(app): persist and restore HudScale on drag-commit and exit"
```

---

## Task 7: Add `ResetHudLayout` to `IController` and implement it

**Files:**
- Modify: `src/GamePartyHud/MainWindow.xaml.cs` (interface only)
- Modify: `src/GamePartyHud/App.xaml.cs` (implementation)

- [ ] **Step 1: Add the method to `MainWindow.IController`**

In `src/GamePartyHud/MainWindow.xaml.cs`, inside the `IController` interface, add:

```csharp
/// <summary>Restores the HUD to its baseline position (100, 100) and scale 1.0.
/// Called from the Reset button in the MainWindow's "HUD layout" section.</summary>
void ResetHudLayout();
```

Place it just after `void UpdateConfig(AppConfig cfg);` for grouping with the other write methods.

- [ ] **Step 2: Implement it on `App`**

In `src/GamePartyHud/App.xaml.cs`, add the implementation alongside the other `MainWindow.IController` members (near `UpdateConfig`):

```csharp
void MainWindow.IController.ResetHudLayout()
{
    if (_hud is null) return;
    _hud.Left  = AppConfig.Defaults.HudPosition.X;
    _hud.Top   = AppConfig.Defaults.HudPosition.Y;
    _hud.Scale = AppConfig.Defaults.HudScale;
    _config = _config with
    {
        HudPosition = AppConfig.Defaults.HudPosition,
        HudScale    = AppConfig.Defaults.HudScale,
    };
    try { _store?.Save(_config); }
    catch (Exception ex) { Log.Error("Failed to persist config after HUD reset.", ex); }
    Log.Info("HUD layout reset to defaults.");
}
```

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/GamePartyHud/MainWindow.xaml.cs src/GamePartyHud/App.xaml.cs
git commit -m "feat(app): add IController.ResetHudLayout to restore HUD defaults"
```

---

## Task 8: Add "HUD layout" section and Reset button to MainWindow

**Files:**
- Modify: `src/GamePartyHud/MainWindow.xaml`
- Modify: `src/GamePartyHud/MainWindow.xaml.cs`

- [ ] **Step 1: Insert the section in `MainWindow.xaml`**

Find the existing line `<!-- Party ───────────────────────────────────────────────── -->` (around line 166). Insert the following block *immediately before* it:

```xaml
                <!-- HUD layout ─────────────────────────────────────────── -->
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

- [ ] **Step 2: Add the `OnResetHud` handler in `MainWindow.xaml.cs`**

Append this method to the `MainWindow` class in `src/GamePartyHud/MainWindow.xaml.cs` (place it near `OnQuitApp` / `OnCloseToTray` as a "single-line forward to controller"):

```csharp
private void OnResetHud(object sender, RoutedEventArgs e)
{
    _ctl.ResetHudLayout();
    Log.Info("MainWindow: Reset HUD layout clicked.");
}
```

- [ ] **Step 3: Build to confirm the icon name and button compile**

Run: `dotnet build`
Expected: Build succeeds. If the `Icon="ArrowReset24"` value isn't recognised by the Wpf.Ui version pinned in this project, the build will fail with a XAML error — in that case substitute a different available glyph from the project (search for `Icon=` in `MainWindow.xaml` to see what's in use today, e.g. `ChevronDown24`, `Power24`, `SignOut24`) and continue.

- [ ] **Step 4: Run the full test suite**

Run: `dotnet test`
Expected: All tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/GamePartyHud/MainWindow.xaml src/GamePartyHud/MainWindow.xaml.cs
git commit -m "feat(ui): add Reset HUD position & size button to MainWindow"
```

---

## Task 9: Manual smoke test

**Files:** None (manual verification per `CLAUDE.md` UI-testing policy).

- [ ] **Step 1: Run the app with the smoke harness at 5 members**

Run: `dotnet run --project src/GamePartyHud -- --smoke-hud=5`

(The HUD opens with 5 fake members; the main window does not — the smoke harness is HUD-only.)

- [ ] **Step 2: Verify locked-mode behaviour**

- HUD shows 5 cards in a single column.
- Lock glyph is "🔒".
- Resize grip is **not** visible.
- Clicks on the HUD pass through (move cursor over a card; the cursor doesn't change to indicate the HUD is interactive).

- [ ] **Step 3: Click the lock to unlock**

- Lock glyph switches to "🔓".
- A 1px red border appears around the HUD.
- A small light square (the resize grip) appears in the bottom-right.

- [ ] **Step 4: Resize tests (5 members)**

  - Drag the grip diagonally outward → HUD grows proportionally. Continue past the visible limit → growth stops at ~2× original size (clamped).
  - Drag the grip diagonally inward → HUD shrinks proportionally. Continue → shrinking stops at ~0.5× (clamped).
  - Drag only right (no Y movement) → HUD scales up.
  - Drag only down (no X movement) → HUD scales up.
  - Drag at 45° → HUD scales smoothly.
  - At every size, the proportions of role tile : bar : margins look identical to the 1× baseline.

- [ ] **Step 5: Drag-to-move still works**

While unlocked, click an empty area of the HUD (e.g. just outside any card but inside the border) and drag → the window moves. Release.

- [ ] **Step 6: Drag-to-swap still works**

Press and hold on one card, drag onto another → the two members swap positions in the column.

- [ ] **Step 7: Lock again and verify grip hides**

Click 🔓 → glyph returns to 🔒, the red border disappears, the resize grip disappears.

- [ ] **Step 8: Close the app and re-open to verify persistence**

Resize to ~1.4×, drag the HUD to a new screen position, close the app (Alt+F4 or close the main window if it's open), then `dotnet run --project src/GamePartyHud -- --smoke-hud=5` again. The HUD should appear at the same position and scale.

- [ ] **Step 9: Repeat resize tests with 11 and 20 members**

Close the app, re-launch with `--smoke-hud=11` then `--smoke-hud=20`. For each:
- Members render in 2 columns (10 + 1, then 10 + 10).
- Resize grip still works.
- Proportions remain correct at every scale.
- Window resizes around the wider 2-column content correctly.

- [ ] **Step 10: Test the Reset button**

Close all instances. Launch the full app (no `--smoke-hud` flag): `dotnet run --project src/GamePartyHud`.

- The HUD opens at the saved (resized, repositioned) state from earlier steps.
- The main window opens.
- Scroll to the new "HUD layout" section.
- Click **Reset HUD position & size**.
- The HUD jumps to `(100, 100)` at `1.0×` scale.
- Close the main window and re-open the app → HUD is still at `(100, 100)` and `1.0×` (reset persisted).

- [ ] **Step 11: Test reset when HUD is off-screen**

Unlock the HUD, drag it to the bottom-right corner of the screen until most of it is invisible (or off-screen entirely on a multi-monitor setup), close the app, reopen — HUD is still off-screen. Click **Reset HUD position & size**. HUD reappears at `(100, 100)`, fully visible.

- [ ] **Step 12: Quick perf eyeball (no commit if passing)**

Open Task Manager → Processes. With the HUD at default scale and 5 fake members, GamePartyHud should sit at <1% CPU, <100 MB RAM. Resize to 2×; CPU briefly spikes during drag (re-layout per frame), then returns to <1% steady. This satisfies the perf checklist item from the spec.

- [ ] **Step 13: No commit needed**

This task is verification-only. If any of the steps above failed, file an issue or fix-forward in a new commit per the failure; otherwise the implementation is complete.

---

## Verification Summary

After all tasks complete:

- `dotnet build` succeeds with no warnings (`CLAUDE.md`: warnings are errors).
- `dotnet test` reports all green.
- Manual checklist in Task 9 passes end-to-end.

No new dependencies were added. No protocol or message changes. Capture, party state, networking, and tray remain untouched.
