# Main window redesign

**Date:** 2026-05-17
**Scope:** `src/GamePartyHud/MainWindow.xaml` + a new `src/GamePartyHud/SettingsWindow.xaml(.cs)`. No behavioural / non-UI changes.

## Goals

Reorganise the settings/main window so the user no longer scrolls to reach the Party section, and so the screen reads as a single horizontally-balanced layout instead of a long vertical list.

1. Two-column horizontal layout: personal setup on the left, Party on the right, separated by a vertical divider.
2. A header row at the top with a (placeholder) logo on the left and a `Settings` gear icon on the right.
3. A small modal `SettingsWindow` opened by the gear, containing only `Reset HUD position` for now.
4. Replace the long single-column `ScrollViewer` with a fixed layout sized so every existing control is visible at minimum window size with all optional bars expanded.

## Non-goals

- No behaviour changes to capture, HP/stamina/mana analysis, party state, networking, signalling, tray, or HUD overlay.
- No changes to the `MainWindow.IController` interface or the App composition root.
- No new tests. The work is XAML reorganisation plus a tiny new dialog window. Per `CLAUDE.md`, UI code is manually tested; pure-logic tests are untouched.
- No new logo asset in this change — the logo is wired as an `Image` placeholder bound to `app.ico` until the user drops in a custom PNG/SVG (one-line `Source` change later).
- No HUD-overlay (`HudWindow` / `MemberCard`) changes.

## Reference

User-supplied wireframe (committed alongside this spec is not required — see the design discussion for the original ASCII rendering). Key elements from the wireframe:

- Logo top-left, gear top-right.
- Vertical split with personal setup on the left, Party on the right.
- No bottom-bar action area was drawn, but the user explicitly chose to **keep** the existing `Close to tray` + `Quit app` footer.

## Design

### 1. Window shell

`MainWindow` remains a `ui:FluentWindow` with `WindowBackdropType="Mica"`, `ExtendsContentIntoTitleBar="True"`, `ResizeMode="CanMinimize"`, and the existing `ui:TitleBar Title="Game Party HUD"`.

| Property              | Current             | New                 |
|-----------------------|---------------------|---------------------|
| `Width` × `Height`    | 600 × 680           | **880 × 580**       |
| `MinWidth` × `MinHeight` | 480 × 520        | **720 × 520**       |
| `ResizeMode`          | `CanMinimize`       | `CanMinimize` (unchanged) |
| Outer `ScrollViewer`  | Present (wraps content) | **Removed** |

The root layout becomes a `Grid` with four rows:

```
Row 0: ui:TitleBar                              (Auto)
Row 1: Header row (logo + gear)                 (Auto, ~56px)
Row 2: FullscreenDisclaimer InfoBar             (Auto, collapses when dismissed)
Row 3: Two-column content area                  (* — fills remaining height)
Row 4: Footer (Close to tray + Quit app)        (Auto, ~50px)
```

### 2. Header row (Row 1)

A `Grid` with two columns (`* | Auto`), `Margin="24,12,16,12"`:

- **Left:** an `Image` element, `Width="28" Height="28"`, `Source="pack://application:,,,/app.ico"`, `VerticalAlignment="Center"`, `Margin="0,0,10,0"`. Named `LogoImage` so the source can be swapped to a custom asset later by updating one binding.
- **Right:** a `ui:Button` named `SettingsButton`, `Background="Transparent"`, `BorderThickness="0"`, `Padding="6,3"`, `ToolTip="Settings"`, with a `ui:SymbolIcon Symbol="Settings24" FontSize="18"` child. `Click="OnOpenSettings"`.

### 3. Fullscreen disclaimer (Row 2)

The existing `ui:InfoBar x:Name="FullscreenDisclaimer"` is preserved verbatim — same severity, message, `IsClosable`, and the `IsOpenProperty` value-changed subscription wired in `MainWindow` constructor. Margin updates to span the new layout: `Margin="24,0,24,12"`. When `IsOpen="False"` it collapses (existing behaviour) so the content rows expand to fill.

### 4. Two-column content area (Row 3)

A `Grid` with three columns: `* | Auto | *`.

- **Column 0 — Left content** (`Padding="24,12,12,12"`): a vertical `StackPanel` containing the `Profile` sub-section then the `Bars` sub-section.
- **Column 1 — Vertical divider:** a 1px `Border`, `Width="1"`, `Background="#22FFFFFF"`, `Margin="0,8"` (so it stops short of top/bottom).
- **Column 2 — Right content** (`Padding="12,12,24,12"`): a `Grid` with two rows (`*` | `Auto`) where the top row holds the Party UI and the bottom row holds the `PartyStatus` InfoBar pinned to the bottom.

#### 4a. Left column — Profile sub-section

```
TextBlock "Profile"          (FontSize=18, SemiBold, Margin=0,0,0,12)
TextBlock "Nickname"         (SemiBold)
TextBlock helper             ("What your teammates see on the HUD.", Opacity=0.75)
ui:TextBox NickText          (MaxLength=32, PlaceholderText="Your character name")

TextBlock "Role"             (SemiBold, Margin=0,18,0,2)
ComboBox RoleCombo           (Width=240, HorizontalAlignment=Left, existing ItemTemplate)
```

All `x:Name`s and event handlers (`OnNicknameChanged`, `OnRoleChanged`) preserved.

#### 4b. Left column — Bars sub-section

```
Separator                    (Margin=0,28,0,0, Opacity=0.3)
TextBlock "Bars"             (FontSize=16, SemiBold, Margin=0,20,0,8)
TextBlock helper             ("Track HP — plus optional stamina and mana — by dragging a tight box around each bar in your game.", Opacity=0.75, TextWrapping=Wrap, Margin=0,0,0,12)

TextBlock "HP bar region"    (SemiBold, Margin=0,0,0,2)
TextBlock helper             (existing "Be in-game with HP full..." text)
StackPanel Horizontal:
  PickRegionButton           (Primary, "Pick HP bar region", Tag="Hp")
  Border RegionStatusChip    (with RegionStatusIcon + RegionStatus TextBlock)

CheckBox IncludeStaminaCheck     (Margin=0,16,0,4, "Include stamina")
StackPanel StaminaPickRow        (Margin=20,0,0,12, Visibility=Collapsed by default)
  PickStaminaRegionButton        (Secondary)
  Border StaminaStatusChip       (...)

CheckBox IncludeManaCheck        (Margin=0,0,0,4, "Include mana")
StackPanel ManaPickRow           (Margin=20,0,0,0, Visibility=Collapsed by default)
  PickManaRegionButton           (Secondary)
  Border ManaStatusChip          (...)
```

Notes:
- The existing "Optional bars" sub-heading is **removed**; the description it carried ("Track stamina and mana too...") is rewritten into a single helper line under `Bars` covering HP + optional stamina + mana. New text: `"Track HP — plus optional stamina and mana — by dragging a tight box around each bar in your game."`.
- All `x:Name`s preserved: `PickRegionButton`, `RegionStatusChip`, `RegionStatusIcon`, `RegionStatus`, `IncludeStaminaCheck`, `StaminaPickRow`, `PickStaminaRegionButton`, `StaminaStatusChip`, `StaminaStatusIcon`, `StaminaStatus`, and the matching `Mana*` set.
- All handlers preserved: `OnPickRegion`, `OnIncludeStaminaChecked/Unchecked`, `OnIncludeManaChecked/Unchecked`. `SetRegionStatus(BarType, ...)` continues to drive the chips.

#### 4c. Right column — Party sub-section

```
Grid (RowDefinitions: * | Auto):
  Row 0: StackPanel (VerticalAlignment=Top):
    TextBlock "Party"            (FontSize=18, SemiBold, Margin=0,0,0,12)

    StackPanel NotInPartySection (existing, unchanged):
      TextBlock "You're not in a party yet." (Margin=0,0,0,14, Opacity=0.85)
      StackPanel Horizontal:
        ui:Button CreateButton   (Primary, "Create new party", Icon=AddCircle24)
        ui:ProgressRing CreateProgress
      TextBlock helper           ("Generates a 6-character code...")
      TextBlock "or join an existing party"
      StackPanel Horizontal:
        ui:TextBox PartyIdInput  (MaxLength=6, Upper, Width=180)
        ui:Button JoinButton     (Secondary, "Join", Icon=PlugConnected24)
        ui:ProgressRing JoinProgress

    StackPanel InPartySection    (existing, unchanged):
      StackPanel Horizontal: party-id badge (clickable, copy-to-clipboard) + CopyFeedback
      TextBlock MemberCountDisplay
      ui:Button Leave            (Danger, Icon=SignOut24)

  Row 1: ui:InfoBar PartyStatus  (VerticalAlignment=Bottom, IsOpen=False, IsClosable=True)
```

All `x:Name`s preserved: `NotInPartySection`, `InPartySection`, `CreateButton`, `CreateProgress`, `PartyIdInput`, `JoinButton`, `JoinProgress`, `PartyIdDisplay`, `CopyFeedback`, `MemberCountDisplay`, `PartyStatus`. All handlers preserved: `OnCreate`, `OnJoin`, `OnLeave`, `OnPartyIdClick`, `OnPartyIdInputChanged`, `OnPartyIdInputKeyDown`. `SetPartyStatus` and `SetPartyActionsBusy` continue to drive the InfoBar and spinners.

The `Row 1` placement of `PartyStatus` (vs. being inline at the bottom of the StackPanel) ensures status messages always sit pinned to the bottom of the column, regardless of whether the not-in-party or in-party sub-stack is showing.

### 5. Footer (Row 4)

Unchanged controls, repositioned:

```
Grid (Margin=24,12,24,16):
  StackPanel Horizontal HorizontalAlignment=Right:
    ui:Button Close to tray   (Secondary, Icon=ChevronDown24, Click=OnCloseToTray)
    ui:Button Quit app        (Danger, Icon=Power24, Click=OnQuitApp)
```

### 6. `SettingsWindow` — new modal dialog

New files:
- `src/GamePartyHud/SettingsWindow.xaml`
- `src/GamePartyHud/SettingsWindow.xaml.cs`

```xml
<ui:FluentWindow x:Class="GamePartyHud.SettingsWindow"
                 Title="Settings"
                 Icon="pack://application:,,,/app.ico"
                 Width="360"
                 SizeToContent="Height"
                 WindowStartupLocation="CenterOwner"
                 WindowBackdropType="Mica"
                 ExtendsContentIntoTitleBar="True"
                 ResizeMode="NoResize">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>  <!-- TitleBar -->
            <RowDefinition Height="Auto"/>  <!-- Body -->
        </Grid.RowDefinitions>
        <ui:TitleBar Grid.Row="0" Title="Settings"/>
        <StackPanel Grid.Row="1" Margin="24,12,24,20">
            <ui:Button x:Name="ResetHudButton"
                       Appearance="Secondary"
                       Icon="ArrowReset24"
                       Content="Reset HUD position"
                       HorizontalAlignment="Stretch"
                       Padding="12,8"
                       Click="OnResetHud"/>
        </StackPanel>
    </Grid>
</ui:FluentWindow>
```

Code-behind:

```csharp
public partial class SettingsWindow : FluentWindow
{
    private readonly MainWindow.IController _ctl;

    public SettingsWindow(MainWindow.IController controller)
    {
        InitializeComponent();
        _ctl = controller;
    }

    private void OnResetHud(object sender, RoutedEventArgs e)
    {
        _ctl.ResetHudLayout();
        Log.Info("SettingsWindow: Reset HUD layout clicked.");
        Close();
    }
}
```

Opened from `MainWindow.xaml.cs`:

```csharp
private void OnOpenSettings(object sender, RoutedEventArgs e)
{
    var dlg = new SettingsWindow(_ctl) { Owner = this };
    dlg.ShowDialog();
}
```

`MainWindow.IController` is unchanged. `ResetHudLayout()` already exists. The existing `OnResetHud` handler on `MainWindow` is deleted; an equivalent handler (same one-line body plus `Close()`) is added to `SettingsWindow` per the code-behind above. The new `OnOpenSettings` handler replaces it on `MainWindow`.

### 7. What gets removed from `MainWindow.xaml`

- The inline `"Reset HUD position"` icon button currently embedded in the `"Your settings"` heading row (the entire `Grid` at lines ~38–50 of the current `MainWindow.xaml`).
- The `"Your settings"` sub-heading (replaced by the new `Profile` header).
- The `"Optional bars"` sub-heading (folded into the unified `Bars` block).
- The wrapping `ScrollViewer` around the content (lines ~22 and ~288 of current XAML).
- The two horizontal `<Separator>`s that today divided "Your settings → Optional bars → Party" inside the scrolled content (the vertical column divider replaces them).
- The `OnResetHud` event handler in `MainWindow.xaml.cs` (its only caller, the inline icon button, is gone). The equivalent handler lives in `SettingsWindow.xaml.cs` per §6.

### 8. What stays untouched

- All event handlers in `MainWindow.xaml.cs` (`OnNicknameChanged`, `OnRoleChanged`, `OnPickRegion`, `OnIncludeStaminaChecked/Unchecked`, `OnIncludeManaChecked/Unchecked`, `OnCreate`, `OnJoin`, `OnLeave`, `OnPartyIdClick`, `OnPartyIdInputChanged`, `OnPartyIdInputKeyDown`, `OnCloseToTray`, `OnQuitApp`, `OnFullscreenDisclaimerIsOpenChanged`).
- The body of the old `OnResetHud` lives on as the `SettingsWindow.OnResetHud` handler (same `_ctl.ResetHudLayout()` call + a `Close()`). `_ctl` is the same `MainWindow.IController` reference passed into the dialog.
- `PopulateFromConfig`, `RefreshPartyState`, `SetRegionStatus`, `SetPartyStatus`, `ShowCopyFeedback`, `SetPartyActionsBusy`, `ValidateBeforeJoiningParty`, `ParseBarType`, `PromptFor` — unchanged.
- `MainWindow.IController` interface — unchanged.
- `App.xaml.cs` composition root — unchanged.
- Tray menu, HUD overlay, capture, party, networking, config, signalling — all completely untouched.

### 9. Manual verification

Before claiming done, run the app and verify all of the following:

1. Window opens at 880 × 580 with title bar, header (logo + gear), both columns, and footer all visible with no scroll.
2. Resize to 720 × 520 (minimum). With both `Include stamina` and `Include mana` checked AND in the in-party state, everything is still visible without scrolling.
3. Fullscreen disclaimer: on first run it shows full-width below the header. Dismissing it persists `FullscreenDisclaimerDismissed = true` in config, and the next launch starts without the banner.
4. Profile → Nickname: typing updates config (verify `appconfig.json`).
5. Profile → Role: selecting a role updates config.
6. Bars → Pick HP bar region: main window goes invisible, region selector opens, drag-select saves region. Chip turns green with the saved size/position.
7. Bars → Include stamina: ticking shows the Pick row; unticking clears `StaminaCalibration` in config and re-collapses the row.
8. Bars → Include mana: same as stamina.
9. Party → Create: button busy state during the await, party-id badge populates on success, `PartyStatus` InfoBar shows success message in the bottom-right and auto-dismisses after 5 s.
10. Party → Join: same as create.
11. Party → Leave: returns to the not-in-party UI, status InfoBar confirms.
12. Click the party-id badge while in a party: clipboard receives the id, "✓ Copied" flashes.
13. Gear icon → Settings dialog opens centered on `MainWindow`, only "Reset HUD position" is shown. Clicking it calls `ResetHudLayout()` (HUD snaps to (100, 100) at scale 1.0) and the dialog closes. The X in the dialog's title bar also closes it.
14. Footer → Close to tray hides the window; tray double-click reopens it.
15. Footer → Quit app exits the process cleanly.
16. Re-open the main window after a Reset HUD — the gear icon is still where you'd expect, no leftover focus / window state oddities.
