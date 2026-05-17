# Main Window Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restructure `MainWindow` into a horizontal two-column layout (Profile + Bars on the left, Party on the right) with a header row (logo + gear icon) and a small `SettingsWindow` modal whose only action is "Reset HUD position". No behavioural changes.

**Architecture:** Two-task plan. Task 1 adds a standalone `SettingsWindow` dialog with no other side effects. Task 2 rewrites `MainWindow.xaml` (full replacement) and patches `MainWindow.xaml.cs` to swap the inline "Reset HUD" handler for an "Open Settings" handler. `MainWindow.IController` is unchanged; every existing event handler, control `x:Name`, and config/party flow is preserved.

**Tech Stack:** WPF (.NET 8 Windows, `net8.0-windows10.0.19041.0`), Wpf.Ui 3.x (`FluentWindow`, `TitleBar`, `InfoBar`, `SymbolIcon`). No new packages.

**Testing approach:** Per `CLAUDE.md`, UI code (WPF windows) is **manually tested** — "Do not invent flaky UI automation." There are no new unit tests. Verification is `dotnet build` (warnings-as-errors catches type/reference mistakes) plus the spec §9 manual checklist. Pure-logic tests in `tests/GamePartyHud.Tests` are unaffected and must continue passing.

**Reference spec:** [`docs/superpowers/specs/2026-05-17-main-window-redesign.md`](../specs/2026-05-17-main-window-redesign.md)

---

## File Structure

**Created:**
- `src/GamePartyHud/SettingsWindow.xaml` — modal dialog markup (title bar + single "Reset HUD position" button).
- `src/GamePartyHud/SettingsWindow.xaml.cs` — code-behind: takes `MainWindow.IController`, calls `ResetHudLayout()`, closes the dialog.

**Modified:**
- `src/GamePartyHud/MainWindow.xaml` — full layout rewrite (title bar / header / disclaimer / two-column content / footer).
- `src/GamePartyHud/MainWindow.xaml.cs` — delete `OnResetHud`, add `OnOpenSettings`. Nothing else changes.

**Untouched (verify by `git diff --stat` at the end):** `MainWindow.IController` interface declaration, `App.xaml.cs`, `GamePartyHud.csproj`, everything under `Capture/`, `Party/`, `Network/`, `Config/`, `Hud/`, `Calibration/`, `Tray/`, `Diagnostics/`, and all tests under `tests/`.

WPF auto-discovers `.xaml` + `.xaml.cs` pairs in the project directory and includes them as Page items — **no `.csproj` edit is required** to register `SettingsWindow`.

---

## Task 1: Add SettingsWindow modal dialog

Standalone, has no effect on the running app yet (nothing opens it). Establishes the dialog so Task 2 can wire it up.

**Files:**
- Create: `src/GamePartyHud/SettingsWindow.xaml`
- Create: `src/GamePartyHud/SettingsWindow.xaml.cs`

- [ ] **Step 1.1: Create `src/GamePartyHud/SettingsWindow.xaml`**

```xml
<ui:FluentWindow x:Class="GamePartyHud.SettingsWindow"
                 xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
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

- [ ] **Step 1.2: Create `src/GamePartyHud/SettingsWindow.xaml.cs`**

```csharp
using System.Windows;
using GamePartyHud.Diagnostics;
using Wpf.Ui.Controls;

namespace GamePartyHud;

/// <summary>
/// Small modal dialog opened from the gear icon on <see cref="MainWindow"/>.
/// Currently exposes only the Reset HUD position action. Sized via
/// SizeToContent so the dialog grows naturally as more settings are added.
/// </summary>
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

- [ ] **Step 1.3: Build**

Run:
```
dotnet build src/GamePartyHud/GamePartyHud.csproj
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

If you see `CS0246: The type or namespace name 'FluentWindow' could not be found`, the `using Wpf.Ui.Controls;` line is missing from the `.xaml.cs`. If you see `Cannot find member 'OnResetHud'`, the `Click="OnResetHud"` attribute in the XAML doesn't match the handler name in the `.xaml.cs`.

- [ ] **Step 1.4: Commit**

```
git add src/GamePartyHud/SettingsWindow.xaml src/GamePartyHud/SettingsWindow.xaml.cs
git commit -m "feat(ui): add SettingsWindow modal with Reset HUD position"
```

---

## Task 2: Restructure MainWindow into horizontal two-column layout

Replaces `MainWindow.xaml` wholesale with the new layout, and patches `MainWindow.xaml.cs` to swap the old `OnResetHud` for a new `OnOpenSettings` that opens the dialog from Task 1. All other handlers and members of `MainWindow.xaml.cs` stay exactly as they are.

**Files:**
- Modify (full rewrite): `src/GamePartyHud/MainWindow.xaml`
- Modify (single method swap): `src/GamePartyHud/MainWindow.xaml.cs:553-557`

- [ ] **Step 2.1: Replace the entire contents of `src/GamePartyHud/MainWindow.xaml`**

Write the file with this content (every `x:Name` and `Click=` attribute below matches an existing member of `MainWindow.xaml.cs`, except `LogoImage`, `SettingsButton`, and `Click="OnOpenSettings"` which Task 2.2 introduces):

```xml
<ui:FluentWindow x:Class="GamePartyHud.MainWindow"
                 xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                 xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
                 Title="Game Party HUD"
                 Icon="pack://application:,,,/app.ico"
                 Width="880" Height="580"
                 MinWidth="720" MinHeight="520"
                 WindowStartupLocation="CenterScreen"
                 WindowBackdropType="Mica"
                 ExtendsContentIntoTitleBar="True"
                 ResizeMode="CanMinimize">
    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>   <!-- Title bar -->
            <RowDefinition Height="Auto"/>   <!-- Header (logo + gear) -->
            <RowDefinition Height="Auto"/>   <!-- Fullscreen disclaimer -->
            <RowDefinition Height="*"/>      <!-- Two-column content -->
            <RowDefinition Height="Auto"/>   <!-- Footer -->
        </Grid.RowDefinitions>

        <ui:TitleBar Grid.Row="0" Title="Game Party HUD"/>

        <!-- Header row: placeholder logo (left) + Settings gear (right) -->
        <Grid Grid.Row="1" Margin="24,12,16,12">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>
            <!-- Placeholder logo: bound to app.ico for now. Swap Source to a
                 custom PNG/SVG when one is added. -->
            <Image x:Name="LogoImage" Grid.Column="0"
                   Width="28" Height="28"
                   Source="pack://application:,,,/app.ico"
                   VerticalAlignment="Center"
                   HorizontalAlignment="Left"/>
            <ui:Button x:Name="SettingsButton" Grid.Column="1"
                       Background="Transparent"
                       BorderThickness="0"
                       Padding="6,3"
                       ToolTip="Settings"
                       Click="OnOpenSettings">
                <ui:SymbolIcon Symbol="Settings24" FontSize="18"/>
            </ui:Button>
        </Grid>

        <!-- Fullscreen disclaimer: one-time persistent notice (existing
             behavior — IsOpen seeded from AppConfig.FullscreenDisclaimerDismissed,
             user dismissal persisted via OnFullscreenDisclaimerIsOpenChanged). -->
        <ui:InfoBar Grid.Row="2"
                    x:Name="FullscreenDisclaimer"
                    Severity="Warning"
                    IsClosable="True"
                    Margin="24,0,24,12"
                    Title="If your game runs in fullscreen, the HUD may not be visible."
                    Message="Workaround: use borderless / windowed-fullscreen mode, or drag the HUD onto a second monitor."/>

        <!-- Two-column content area -->
        <Grid Grid.Row="3">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>

            <!-- LEFT COLUMN: Profile + Bars -->
            <StackPanel Grid.Column="0" Margin="24,12,12,12">
                <!-- Profile sub-section -->
                <TextBlock Text="Profile" FontSize="18" FontWeight="SemiBold" Margin="0,0,0,12"/>

                <TextBlock Text="Nickname" FontWeight="SemiBold" Margin="0,0,0,2"/>
                <TextBlock Opacity="0.75" Margin="0,0,0,6" TextWrapping="Wrap"
                           Text="What your teammates see on the HUD."/>
                <ui:TextBox x:Name="NickText" MaxLength="32"
                            PlaceholderText="Your character name"
                            TextChanged="OnNicknameChanged"/>

                <TextBlock Text="Role" FontWeight="SemiBold" Margin="0,18,0,2"/>
                <ComboBox x:Name="RoleCombo"
                          Width="240" HorizontalAlignment="Left"
                          SelectionChanged="OnRoleChanged">
                    <ComboBox.ItemTemplate>
                        <DataTemplate>
                            <StackPanel Orientation="Horizontal">
                                <TextBlock Text="{Binding Glyph}" FontSize="13"
                                           Width="18" TextAlignment="Center"
                                           VerticalAlignment="Center"/>
                                <TextBlock Text="{Binding Label}" Margin="6,0,0,0"
                                           VerticalAlignment="Center"/>
                            </StackPanel>
                        </DataTemplate>
                    </ComboBox.ItemTemplate>
                </ComboBox>

                <!-- Bars sub-section -->
                <Separator Margin="0,28,0,0" Opacity="0.3"/>
                <TextBlock Text="Bars" FontSize="16" FontWeight="SemiBold" Margin="0,20,0,8"/>
                <TextBlock Opacity="0.75" Margin="0,0,0,12" TextWrapping="Wrap"
                           Text="Track HP — plus optional stamina and mana — by dragging a tight box around each bar in your game."/>

                <TextBlock Text="HP bar region" FontWeight="SemiBold" Margin="0,0,0,2"/>
                <TextBlock Opacity="0.75" Margin="0,0,0,6" TextWrapping="Wrap">
                    Be in-game with HP full, then drag a tight box around just the red HP bar —
                    no nickname, no other bars, no frame.
                </TextBlock>
                <StackPanel Orientation="Horizontal">
                    <ui:Button x:Name="PickRegionButton"
                               Appearance="Primary"
                               Icon="Target24"
                               Content="Pick HP bar region"
                               Tag="Hp"
                               Click="OnPickRegion"/>
                    <Border x:Name="RegionStatusChip"
                            VerticalAlignment="Center" Margin="12,0,0,0"
                            Padding="10,4"
                            CornerRadius="10"
                            Background="#22FFFFFF"
                            BorderBrush="#33FFFFFF" BorderThickness="1">
                        <StackPanel Orientation="Horizontal">
                            <TextBlock x:Name="RegionStatusIcon"
                                       Text="○" FontSize="12" FontWeight="Bold"
                                       VerticalAlignment="Center"/>
                            <TextBlock x:Name="RegionStatus"
                                       VerticalAlignment="Center" Margin="6,0,0,0"
                                       TextWrapping="Wrap" MaxWidth="320"/>
                        </StackPanel>
                    </Border>
                </StackPanel>

                <CheckBox x:Name="IncludeStaminaCheck"
                          Content="Include stamina"
                          Margin="0,16,0,4"
                          Checked="OnIncludeStaminaChecked"
                          Unchecked="OnIncludeStaminaUnchecked"/>
                <StackPanel x:Name="StaminaPickRow"
                            Orientation="Horizontal"
                            Margin="20,0,0,12"
                            Visibility="Collapsed">
                    <ui:Button x:Name="PickStaminaRegionButton"
                               Appearance="Secondary"
                               Icon="Target24"
                               Content="Pick stamina bar region"
                               Tag="Stamina"
                               Click="OnPickRegion"/>
                    <Border x:Name="StaminaStatusChip"
                            VerticalAlignment="Center" Margin="12,0,0,0"
                            Padding="10,4"
                            CornerRadius="10"
                            Background="#22FFFFFF"
                            BorderBrush="#33FFFFFF" BorderThickness="1">
                        <StackPanel Orientation="Horizontal">
                            <TextBlock x:Name="StaminaStatusIcon"
                                       Text="○" FontSize="12" FontWeight="Bold"
                                       VerticalAlignment="Center"/>
                            <TextBlock x:Name="StaminaStatus"
                                       VerticalAlignment="Center" Margin="6,0,0,0"
                                       TextWrapping="Wrap" MaxWidth="320"/>
                        </StackPanel>
                    </Border>
                </StackPanel>

                <CheckBox x:Name="IncludeManaCheck"
                          Content="Include mana"
                          Margin="0,0,0,4"
                          Checked="OnIncludeManaChecked"
                          Unchecked="OnIncludeManaUnchecked"/>
                <StackPanel x:Name="ManaPickRow"
                            Orientation="Horizontal"
                            Margin="20,0,0,0"
                            Visibility="Collapsed">
                    <ui:Button x:Name="PickManaRegionButton"
                               Appearance="Secondary"
                               Icon="Target24"
                               Content="Pick mana bar region"
                               Tag="Mana"
                               Click="OnPickRegion"/>
                    <Border x:Name="ManaStatusChip"
                            VerticalAlignment="Center" Margin="12,0,0,0"
                            Padding="10,4"
                            CornerRadius="10"
                            Background="#22FFFFFF"
                            BorderBrush="#33FFFFFF" BorderThickness="1">
                        <StackPanel Orientation="Horizontal">
                            <TextBlock x:Name="ManaStatusIcon"
                                       Text="○" FontSize="12" FontWeight="Bold"
                                       VerticalAlignment="Center"/>
                            <TextBlock x:Name="ManaStatus"
                                       VerticalAlignment="Center" Margin="6,0,0,0"
                                       TextWrapping="Wrap" MaxWidth="320"/>
                        </StackPanel>
                    </Border>
                </StackPanel>
            </StackPanel>

            <!-- Vertical divider between columns -->
            <Border Grid.Column="1"
                    Width="1"
                    Background="#22FFFFFF"
                    Margin="0,8"/>

            <!-- RIGHT COLUMN: Party + Status InfoBar (pinned bottom) -->
            <Grid Grid.Column="2" Margin="12,12,24,12">
                <Grid.RowDefinitions>
                    <RowDefinition Height="*"/>
                    <RowDefinition Height="Auto"/>
                </Grid.RowDefinitions>

                <StackPanel Grid.Row="0" VerticalAlignment="Top">
                    <TextBlock Text="Party" FontSize="18" FontWeight="SemiBold" Margin="0,0,0,12"/>

                    <!-- Not in a party yet -->
                    <StackPanel x:Name="NotInPartySection">
                        <TextBlock Text="You're not in a party yet."
                                   Margin="0,0,0,14" Opacity="0.85"/>

                        <StackPanel Orientation="Horizontal">
                            <ui:Button x:Name="CreateButton"
                                       Content="Create new party"
                                       Icon="AddCircle24"
                                       Appearance="Primary"
                                       Padding="16,8"
                                       Click="OnCreate"/>
                            <ui:ProgressRing x:Name="CreateProgress"
                                             Width="20" Height="20"
                                             IsIndeterminate="True"
                                             Visibility="Collapsed"
                                             Margin="12,0,0,0"
                                             VerticalAlignment="Center"/>
                        </StackPanel>
                        <TextBlock Opacity="0.6" FontSize="11" Margin="2,6,0,0" TextWrapping="Wrap"
                                   Text="Generates a 6-character code you share with your teammates."/>

                        <TextBlock Text="or join an existing party" Opacity="0.75" Margin="0,20,0,6"/>
                        <StackPanel Orientation="Horizontal">
                            <ui:TextBox x:Name="PartyIdInput"
                                        MaxLength="6"
                                        CharacterCasing="Upper"
                                        PlaceholderText="ABCDEF"
                                        Width="180"
                                        FontSize="14"
                                        TextChanged="OnPartyIdInputChanged"
                                        KeyDown="OnPartyIdInputKeyDown"/>
                            <ui:Button x:Name="JoinButton"
                                       Content="Join"
                                       Icon="PlugConnected24"
                                       Appearance="Secondary"
                                       IsEnabled="False"
                                       Margin="8,0,0,0"
                                       Padding="16,0"
                                       Click="OnJoin"/>
                            <ui:ProgressRing x:Name="JoinProgress"
                                             Width="20" Height="20"
                                             IsIndeterminate="True"
                                             Visibility="Collapsed"
                                             Margin="12,0,0,0"
                                             VerticalAlignment="Center"/>
                        </StackPanel>
                    </StackPanel>

                    <!-- In a party -->
                    <StackPanel x:Name="InPartySection" Visibility="Collapsed">
                        <StackPanel Orientation="Horizontal">
                            <TextBlock Text="You're in party:"
                                       VerticalAlignment="Center" Opacity="0.85"/>
                            <!-- Click anywhere on this badge to copy the party ID. -->
                            <Border CornerRadius="4"
                                    Padding="10,4" Margin="10,0,0,0"
                                    BorderBrush="#33FFFFFF" BorderThickness="1"
                                    Cursor="Hand"
                                    MouseLeftButtonUp="OnPartyIdClick"
                                    ToolTip="Click to copy">
                                <Border.Style>
                                    <Style TargetType="Border">
                                        <Setter Property="Background" Value="#22FFFFFF"/>
                                        <Style.Triggers>
                                            <Trigger Property="IsMouseOver" Value="True">
                                                <Setter Property="Background" Value="#33FFFFFF"/>
                                            </Trigger>
                                        </Style.Triggers>
                                    </Style>
                                </Border.Style>
                                <TextBlock x:Name="PartyIdDisplay"
                                           FontFamily="Consolas, Cascadia Mono, monospace"
                                           FontSize="18" FontWeight="Bold"
                                           Text="XXXXXX"/>
                            </Border>
                            <TextBlock x:Name="CopyFeedback"
                                       Text="✓ Copied"
                                       VerticalAlignment="Center"
                                       Margin="12,0,0,0"
                                       FontWeight="SemiBold"
                                       Foreground="#FFAEE6AE"
                                       Opacity="0"
                                       IsHitTestVisible="False"/>
                        </StackPanel>
                        <TextBlock x:Name="MemberCountDisplay"
                                   Margin="0,10,0,14" Opacity="0.85"
                                   Text="1 member (you)"/>

                        <StackPanel Orientation="Horizontal">
                            <ui:Button Content="Leave party"
                                       Icon="SignOut24"
                                       Appearance="Danger"
                                       Padding="14,6"
                                       Click="OnLeave"/>
                        </StackPanel>
                    </StackPanel>
                </StackPanel>

                <!-- Party status InfoBar pinned to bottom of right column -->
                <ui:InfoBar Grid.Row="1"
                            x:Name="PartyStatus"
                            VerticalAlignment="Bottom"
                            Margin="0,12,0,0"
                            IsOpen="False"
                            IsClosable="True"/>
            </Grid>
        </Grid>

        <!-- Footer: Close to tray + Quit app -->
        <Grid Grid.Row="4" Margin="24,12,24,16">
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                <ui:Button Content="Close to tray"
                           Appearance="Secondary"
                           Icon="ChevronDown24"
                           Padding="14,6"
                           Click="OnCloseToTray"/>
                <ui:Button Content="Quit app"
                           Icon="Power24"
                           Appearance="Danger"
                           Margin="8,0,0,0"
                           Padding="14,6"
                           Click="OnQuitApp"/>
            </StackPanel>
        </Grid>
    </Grid>
</ui:FluentWindow>
```

- [ ] **Step 2.2: Patch `src/GamePartyHud/MainWindow.xaml.cs` — swap `OnResetHud` for `OnOpenSettings`**

Find this block in `src/GamePartyHud/MainWindow.xaml.cs` (currently at lines 553–557):

```csharp
    private void OnResetHud(object sender, RoutedEventArgs e)
    {
        _ctl.ResetHudLayout();
        Log.Info("MainWindow: Reset HUD layout clicked.");
    }
```

Replace it with:

```csharp
    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_ctl) { Owner = this };
        dlg.ShowDialog();
        Log.Info("MainWindow: Settings dialog closed.");
    }
```

Nothing else in `MainWindow.xaml.cs` changes. Do **not** modify the constructor, `PopulateFromConfig`, `RefreshPartyState`, any of the `OnXxx` handlers, `SetRegionStatus`, `SetPartyStatus`, `ShowCopyFeedback`, `SetPartyActionsBusy`, `ValidateBeforeJoiningParty`, `ParseBarType`, `PromptFor`, `OnCloseToTray`, `OnQuitApp`, `OnClosing`, or `ShowAndActivate`.

- [ ] **Step 2.3: Build**

Run:
```
dotnet build src/GamePartyHud/GamePartyHud.csproj
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`.

Common failure modes:
- `Cannot find member 'OnResetHud' in type 'MainWindow'` — old XAML reference slipped through. Re-check Step 2.1; the new XAML must not have any `Click="OnResetHud"`.
- `Cannot find member 'OnOpenSettings' in type 'MainWindow'` — Step 2.2 wasn't done.
- `The name 'SettingsWindow' does not exist in the current context` — Task 1 wasn't committed; re-run Task 1.
- Any `CS0xxx` warning treated as error (per `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` in the csproj) — fix the underlying issue, do not suppress.

- [ ] **Step 2.4: Run unit tests**

Run:
```
dotnet test tests/GamePartyHud.Tests/GamePartyHud.Tests.csproj
```
Expected: all existing tests still pass. No tests are added or modified by this change.

- [ ] **Step 2.5: Manual verification (spec §9 checklist)**

Run the app:
```
dotnet run --project src/GamePartyHud/GamePartyHud.csproj
```

Walk through each item; check off only after you've seen the behaviour with your own eyes. If anything fails, stop and fix before committing — **never** mark the task done based on "should work".

1. Window opens at 880 × 580. Title bar, header row (logo + gear), both columns, and footer are all visible. **No vertical scrollbar** appears anywhere.
2. Grab the bottom-right corner and resize the window to its minimum (720 × 520). With `Include stamina` and `Include mana` both checked AND in the in-party state, every control is still visible — no clipping, no scroll.
3. **First-run disclaimer:** delete `%APPDATA%\GamePartyHud\appconfig.json` (or set `FullscreenDisclaimerDismissed: false` in it), relaunch — the yellow fullscreen-disclaimer InfoBar shows full-width under the header. Click its X. Relaunch — banner stays hidden. `appconfig.json` shows `"FullscreenDisclaimerDismissed": true`.
4. **Nickname:** type in the Nickname box, look in `appconfig.json` after a moment — `Nickname` is updated.
5. **Role:** pick a different role from the dropdown — `Role` updates in `appconfig.json`.
6. **Pick HP bar region:** click the button. Main window goes invisible. A red selection overlay covers the screen. Drag a box. Release. The window returns; the green ✓ chip appears showing `Saved WxH at (X, Y).`.
7. **Include stamina:** tick the checkbox — the secondary "Pick stamina bar region" row appears below it. Untick — the row collapses and `appconfig.json`'s `StaminaCalibration` becomes `null`.
8. **Include mana:** same behaviour as stamina.
9. **Create party:** click "Create new party". The Create button greys out, the spinner shows next to it. On success, the right column flips to in-party state with the 6-char ID badge, and a green InfoBar at the bottom of the right column says "Party created…" and auto-dismisses after ~5 s.
10. **Join party (separate machine or instance):** type a valid 6-char ID, click Join. Same busy → success → auto-dismiss flow.
11. **Leave party:** click "Leave party". The right column flips back to not-in-party. Confirmation InfoBar shows briefly.
12. **Copy party ID:** while in a party, click the party-id badge. The "✓ Copied" text flashes next to it; paste into Notepad to confirm the clipboard.
13. **Settings dialog:** click the gear icon top-right. A small modal "Settings" dialog opens centered on the main window. The only content is a "Reset HUD position" button. Click it — HUD snaps to (100, 100) at scale 1.0 (verify by moving the HUD first, then resetting), and the dialog closes. Re-open via gear → close via the title-bar X — dialog dismisses without changes.
14. **Close to tray:** click the footer button — main window hides; tray icon still active. Double-click the tray icon — main window reappears.
15. **Quit app:** click the footer Quit button — process exits cleanly (no zombie process in Task Manager, no "exited with code 1" in the console).
16. **No leftover state after Settings dialog:** open the Settings dialog, then close it (either via Reset HUD or the X). Confirm focus returns to the main window, the gear icon is still rendered correctly in its place, and clicking the gear again opens a fresh dialog (no double-open, no zombie window).

- [ ] **Step 2.6: Verify the diff scope**

Run:
```
git status
git diff --stat HEAD
```
Expected: exactly two modified files (`MainWindow.xaml`, `MainWindow.xaml.cs`) and no other changes. If anything else shows up, you've drifted from the plan — investigate before committing.

- [ ] **Step 2.7: Commit**

```
git add src/GamePartyHud/MainWindow.xaml src/GamePartyHud/MainWindow.xaml.cs
git commit -m "refactor(ui): horizontal two-column main window layout"
```

---

## Done criteria

After both tasks are committed:
- `git log --oneline -3` shows the two new commits on top of `89b999d docs(spec): main window redesign…`.
- `dotnet build` clean, `dotnet test` green.
- Manually walking through spec §9 checklist passes every item.
- `git diff HEAD~2 --stat` shows exactly: `SettingsWindow.xaml` (new), `SettingsWindow.xaml.cs` (new), `MainWindow.xaml` (modified), `MainWindow.xaml.cs` (modified). Nothing else.

Out of scope for this plan (do not do these here):
- Adding a real logo asset — the `LogoImage.Source` swap happens later in a separate, one-line change.
- Adding more items to `SettingsWindow` beyond "Reset HUD position".
- Any change to HUD overlay, capture, party, networking, or config logic.
