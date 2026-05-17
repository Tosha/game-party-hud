using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GamePartyHud.Calibration;
using GamePartyHud.Capture;
using GamePartyHud.Config;
using GamePartyHud.Diagnostics;
using GamePartyHud.Party;
using Wpf.Ui.Controls;

namespace GamePartyHud;

/// <summary>
/// Everything on one screen: player settings (nickname, role, HP region) plus
/// party controls (create / join when out; copy-id / leave when in).
/// Shown at startup and re-openable via the tray. Closing the window (X or
/// "Close to tray") hides it; the app keeps running in the tray + HUD.
/// </summary>
public partial class MainWindow : FluentWindow
{
    /// <summary>
    /// Thin controller surface the window uses to talk to <see cref="App"/>.
    /// Kept narrow so the window isn't tied to the App's internals.
    /// </summary>
    public interface IController
    {
        AppConfig Config { get; }

        /// <summary>Current party id if we're in one; null otherwise.</summary>
        string? CurrentPartyId { get; }

        /// <summary>Count of live members on our party roster (≥1 when in a party).</summary>
        int MemberCount { get; }

        /// <summary>Fires whenever CurrentPartyId or MemberCount changes.</summary>
        event Action? PartyStateChanged;

        void UpdateConfig(AppConfig cfg);

        /// <summary>Restores the HUD to its baseline position (100, 100) and scale 1.0.
        /// Called from the Reset button in the MainWindow's "HUD layout" section.</summary>
        void ResetHudLayout();

        Task CreatePartyAsync();
        Task JoinPartyAsync(string partyId);
        Task LeavePartyAsync();

        Task ShutdownAsync();
    }

    private readonly IController _ctl;
    private bool _populating;
    private bool _allowClose;
    // Re-entrancy guard for the Create / Join party flows. Today the
    // SetPartyActionsBusy(true) call before each await disables both
    // buttons and WPF won't deliver Click to a disabled Button, so the
    // user can't double-trigger from the UI. This flag is belt-and-
    // suspenders for any future code path (tray shortcut, automation)
    // that calls into these handlers without going through the button.
    private bool _partyActionInFlight;

    private sealed record RoleOption(Role Role, string Glyph, string Label);

    private enum RegionStatusState { NotSet, Ok, Error }

    private static readonly RoleOption[] RoleOptions =
        Enum.GetValues<Role>()
            .Select(r => new RoleOption(r, RoleGlyph.For(r), RoleDisplay.For(r)))
            .ToArray();

    public MainWindow(IController controller)
    {
        InitializeComponent();
        _ctl = controller;

        RoleCombo.ItemsSource = RoleOptions;

        // Wpf.Ui's InfoBar doesn't expose a public Closed routed event; the
        // built-in close button just flips IsOpen to false. We watch the
        // property to detect a user-initiated dismissal. The _populating
        // guard inside the handler suppresses the initial set from
        // PopulateFromConfig (which would otherwise look like an
        // instant-dismiss).
        System.ComponentModel.DependencyPropertyDescriptor
            .FromProperty(InfoBar.IsOpenProperty, typeof(InfoBar))
            .AddValueChanged(FullscreenDisclaimer, OnFullscreenDisclaimerIsOpenChanged);

        PopulateFromConfig();
        RefreshPartyState();

        _ctl.PartyStateChanged += OnCtlPartyStateChanged;
    }

    // ------------------------------------------------------------------
    // Populate UI from config / party state
    // ------------------------------------------------------------------

    private void PopulateFromConfig()
    {
        _populating = true;
        try
        {
            var cfg = _ctl.Config;
            FullscreenDisclaimer.IsOpen = !cfg.FullscreenDisclaimerDismissed;
            // Collapse the whole control (not just its inner content) when
            // dismissed, otherwise wpfui's InfoBar keeps its layout slot
            // even at IsOpen=false and leaves ~30px of empty space at the
            // top of the window.
            FullscreenDisclaimer.Visibility = cfg.FullscreenDisclaimerDismissed
                ? Visibility.Collapsed
                : Visibility.Visible;
            NickText.Text = cfg.Nickname == AppConfig.Defaults.Nickname ? "" : cfg.Nickname;
            RoleCombo.SelectedItem = RoleOptions.FirstOrDefault(o => o.Role == cfg.Role) ?? RoleOptions[0];

            if (cfg.HpCalibration is { } cal)
            {
                SetRegionStatus(BarType.Hp, RegionStatusState.Ok,
                    $"Saved {cal.Region.W}\u00D7{cal.Region.H} at ({cal.Region.X}, {cal.Region.Y}).");
            }
            else
            {
                SetRegionStatus(BarType.Hp, RegionStatusState.NotSet, "Not set yet.");
            }

            if (cfg.StaminaCalibration is { } sCal)
            {
                IncludeStaminaCheck.IsChecked = true;
                StaminaPickRow.Visibility = Visibility.Visible;
                SetRegionStatus(BarType.Stamina, RegionStatusState.Ok,
                    $"Saved {sCal.Region.W}\u00D7{sCal.Region.H} at ({sCal.Region.X}, {sCal.Region.Y}).");
            }
            else
            {
                IncludeStaminaCheck.IsChecked = false;
                StaminaPickRow.Visibility = Visibility.Collapsed;
                SetRegionStatus(BarType.Stamina, RegionStatusState.NotSet, "Not set yet.");
            }

            if (cfg.ManaCalibration is { } mCal)
            {
                IncludeManaCheck.IsChecked = true;
                ManaPickRow.Visibility = Visibility.Visible;
                SetRegionStatus(BarType.Mana, RegionStatusState.Ok,
                    $"Saved {mCal.Region.W}\u00D7{mCal.Region.H} at ({mCal.Region.X}, {mCal.Region.Y}).");
            }
            else
            {
                IncludeManaCheck.IsChecked = false;
                ManaPickRow.Visibility = Visibility.Collapsed;
                SetRegionStatus(BarType.Mana, RegionStatusState.NotSet, "Not set yet.");
            }
        }
        finally { _populating = false; }
    }

    private void OnCtlPartyStateChanged()
    {
        // PartyStateChanged may fire off the UI thread.
        Dispatcher.Invoke(RefreshPartyState);
    }

    private void RefreshPartyState()
    {
        var id = _ctl.CurrentPartyId;
        if (id is { Length: > 0 })
        {
            NotInPartySection.Visibility = Visibility.Collapsed;
            InPartySection.Visibility    = Visibility.Visible;
            PartyIdDisplay.Text          = id;
            int n = _ctl.MemberCount;
            MemberCountDisplay.Text      = n <= 1
                ? "You're the only one here right now."
                : $"{n} members connected.";
        }
        else
        {
            NotInPartySection.Visibility = Visibility.Visible;
            InPartySection.Visibility    = Visibility.Collapsed;
        }
    }

    // ------------------------------------------------------------------
    // Settings editors — auto-save on change
    // ------------------------------------------------------------------

    private void OnNicknameChanged(object sender, TextChangedEventArgs e)
    {
        if (_populating) return;
        var nick = NickText.Text?.Trim() ?? "";
        if (nick.Length == 0) return;
        _ctl.UpdateConfig(_ctl.Config with { Nickname = nick });
        Log.Info($"MainWindow: nickname changed to '{nick}'.");
    }

    private void OnPartyIdInputChanged(object sender, TextChangedEventArgs e)
    {
        JoinButton.IsEnabled = (PartyIdInput.Text?.Trim().Length == 6);
    }

    private void OnPartyIdInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && JoinButton.IsEnabled)
        {
            e.Handled = true;
            OnJoin(JoinButton, new RoutedEventArgs());
        }
    }

    private void OnRoleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populating) return;
        if (RoleCombo.SelectedItem is not RoleOption opt) return;
        _ctl.UpdateConfig(_ctl.Config with { Role = opt.Role });
        Log.Info($"MainWindow: role changed to {opt.Role}.");
    }

    private void OnPickRegion(object sender, RoutedEventArgs e)
    {
        var bar = ParseBarType(sender);
        Log.Info($"MainWindow: Pick-{bar}-region button clicked.");
        // Opacity=0 rather than Hide() — Hide() on a WPF window with a child
        // RegionSelectorWindow.ShowDialog has surprising interactions; being
        // invisible via Opacity=0 keeps the main window alive and nothing more.
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
                BarType.Hp      => _ctl.Config with { HpCalibration      = cal, NicknameRegion = null },
                BarType.Stamina => _ctl.Config with { StaminaCalibration = cal },
                BarType.Mana    => _ctl.Config with { ManaCalibration    = cal },
                _ => _ctl.Config
            };
            _ctl.UpdateConfig(newConfig);

            SetRegionStatus(bar, RegionStatusState.Ok,
                $"Captured {region.W}\u00D7{region.H} at ({region.X}, {region.Y}).");
            Log.Info($"MainWindow: {bar} region calibrated {region.W}x{region.H}@({region.X},{region.Y}).");
        }
        catch (Exception ex)
        {
            var failedBar = ParseBarType(sender);
            Log.Error($"MainWindow: OnPickRegion ({failedBar}) failed.", ex);
            SetRegionStatus(failedBar, RegionStatusState.Error, "Error: " + ex.Message);
        }
        finally
        {
            Opacity = 1;
            Activate();
        }
    }

    private static BarType ParseBarType(object sender) =>
        sender is FrameworkElement fe && Enum.TryParse<BarType>(fe.Tag as string, out var t) ? t : BarType.Hp;

    private static string PromptFor(BarType bar) => bar switch
    {
        BarType.Hp      => "Drag a tight box around your HP bar ONLY (no nickname, no other bars)",
        BarType.Stamina => "Drag a tight box around your STAMINA bar ONLY (no nickname, no other bars)",
        BarType.Mana    => "Drag a tight box around your MANA bar ONLY (no nickname, no other bars)",
        _ => ""
    };

    // ------------------------------------------------------------------
    // Party actions
    // ------------------------------------------------------------------

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        if (_partyActionInFlight)
        {
            Log.Info("MainWindow: Create click ignored — party action already in flight.");
            return;
        }
        if (_ctl.CurrentPartyId is { Length: > 0 })
        {
            SetPartyStatus("You're already in a party. Leave it first before creating a new one.",
                InfoBarSeverity.Warning);
            return;
        }
        if (!ValidateBeforeJoiningParty()) return;

        _partyActionInFlight = true;
        SetPartyStatus("Creating party…", InfoBarSeverity.Informational);
        SetPartyActionsBusy(true, CreateProgress);
        try
        {
            await _ctl.CreatePartyAsync();
            if (_ctl.CurrentPartyId is { Length: > 0 })
            {
                SetPartyStatus("Party created. Share the ID above with your teammates.",
                    InfoBarSeverity.Success, autoDismissMs: 5000);
            }
            else
            {
                SetPartyStatus("Party creation didn't finish — check app.log.", InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            Log.Error("MainWindow: CreatePartyAsync failed.", ex);
            SetPartyStatus("Party creation failed: " + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _partyActionInFlight = false;
            SetPartyActionsBusy(false, CreateProgress);
            RefreshPartyState();
        }
    }

    private async void OnJoin(object sender, RoutedEventArgs e)
    {
        if (_partyActionInFlight)
        {
            Log.Info("MainWindow: Join click ignored — party action already in flight.");
            return;
        }
        if (_ctl.CurrentPartyId is { Length: > 0 })
        {
            SetPartyStatus("You're already in a party. Leave it first before joining another.",
                InfoBarSeverity.Warning);
            return;
        }
        var id = (PartyIdInput.Text ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(id))
        {
            SetPartyStatus("Enter a party ID first.", InfoBarSeverity.Warning);
            PartyIdInput.Focus();
            return;
        }
        if (!ValidateBeforeJoiningParty()) return;

        _partyActionInFlight = true;
        SetPartyStatus("Joining party " + id + "…", InfoBarSeverity.Informational);
        SetPartyActionsBusy(true, JoinProgress);
        try
        {
            await _ctl.JoinPartyAsync(id);
            if (_ctl.CurrentPartyId is { Length: > 0 })
            {
                SetPartyStatus("Joined. Waiting for teammates' state to arrive.",
                    InfoBarSeverity.Success, autoDismissMs: 5000);
            }
            else
            {
                SetPartyStatus("Couldn't join. The tracker may be unreachable or your network is blocking P2P.",
                    InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            Log.Error("MainWindow: JoinPartyAsync failed.", ex);
            SetPartyStatus("Join failed: " + ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _partyActionInFlight = false;
            SetPartyActionsBusy(false, JoinProgress);
            RefreshPartyState();
        }
    }

    private async void OnLeave(object sender, RoutedEventArgs e)
    {
        if (_ctl.CurrentPartyId is not { Length: > 0 }) return;
        SetPartyStatus("Leaving party…", InfoBarSeverity.Informational);
        try
        {
            await _ctl.LeavePartyAsync();
            SetPartyStatus("You've left the party.", InfoBarSeverity.Success, autoDismissMs: 3000);
        }
        catch (Exception ex)
        {
            Log.Error("MainWindow: LeavePartyAsync failed.", ex);
            SetPartyStatus("Leave failed: " + ex.Message, InfoBarSeverity.Error);
        }
        finally { RefreshPartyState(); }
    }

    private void OnPartyIdClick(object sender, MouseButtonEventArgs e)
    {
        if (_ctl.CurrentPartyId is { Length: > 0 } id)
        {
            try
            {
                Clipboard.SetText(id);
                ShowCopyFeedback();
            }
            catch (Exception ex)
            {
                Log.Error("MainWindow: copy ID failed.", ex);
                SetPartyStatus("Copy failed: " + ex.Message, InfoBarSeverity.Error);
            }
        }
    }

    private void ShowCopyFeedback()
    {
        CopyFeedback.BeginAnimation(UIElement.OpacityProperty, null);
        CopyFeedback.Opacity = 1.0;
        var fade = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            BeginTime = TimeSpan.FromMilliseconds(800),
            Duration = TimeSpan.FromMilliseconds(700),
            FillBehavior = FillBehavior.HoldEnd,
        };
        CopyFeedback.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private bool ValidateBeforeJoiningParty()
    {
        if (_ctl.Config.HpCalibration is null)
        {
            SetPartyStatus("Set your HP bar region first (see 'Pick HP bar region' above).",
                InfoBarSeverity.Warning);
            return false;
        }
        if (string.IsNullOrWhiteSpace(_ctl.Config.Nickname)
            || _ctl.Config.Nickname == AppConfig.Defaults.Nickname)
        {
            SetPartyStatus("Enter your nickname first.", InfoBarSeverity.Warning);
            NickText.Focus();
            return false;
        }
        return true;
    }

    private DispatcherTimer? _partyStatusAutoDismiss;

    private void SetPartyStatus(string message, InfoBarSeverity severity = InfoBarSeverity.Informational,
                                int autoDismissMs = 0)
    {
        _partyStatusAutoDismiss?.Stop();
        _partyStatusAutoDismiss = null;

        PartyStatus.Message = message;
        PartyStatus.Severity = severity;
        PartyStatus.IsOpen = true;

        if (autoDismissMs > 0)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(autoDismissMs) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (ReferenceEquals(_partyStatusAutoDismiss, timer))
                {
                    PartyStatus.IsOpen = false;
                    _partyStatusAutoDismiss = null;
                }
            };
            _partyStatusAutoDismiss = timer;
            timer.Start();
        }
    }

    private void SetRegionStatus(BarType bar, RegionStatusState state, string text)
    {
        var (textBlock, iconBlock, chip) = bar switch
        {
            BarType.Hp      => (RegionStatus,        RegionStatusIcon,        RegionStatusChip),
            BarType.Stamina => (StaminaStatus,       StaminaStatusIcon,       StaminaStatusChip),
            BarType.Mana    => (ManaStatus,          ManaStatusIcon,          ManaStatusChip),
            _ => (RegionStatus, RegionStatusIcon, RegionStatusChip)
        };

        textBlock.Text = text;
        (string icon, string bg, string border, string fg) = state switch
        {
            RegionStatusState.Ok    => ("\u2713", "#333E8E3E", "#664CAF50", "#FFAEE6AE"),
            RegionStatusState.Error => ("\u2717", "#33C62828", "#66EF5350", "#FFFFB4B4"),
            _                       => ("\u25CB", "#22FFFFFF", "#33FFFFFF", "#CCCCCCCC"),
        };
        iconBlock.Text = icon;
        iconBlock.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(fg)!;
        textBlock.Foreground = iconBlock.Foreground;
        chip.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(bg)!;
        chip.BorderBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(border)!;
    }

    private void OnIncludeStaminaChecked(object sender, RoutedEventArgs e)
    {
        if (_populating) return;
        StaminaPickRow.Visibility = Visibility.Visible;
        Log.Info("MainWindow: Include-stamina checkbox ticked.");
        // No config write here; user must explicitly pick a region.
    }

    private void OnIncludeStaminaUnchecked(object sender, RoutedEventArgs e)
    {
        if (_populating) return;
        StaminaPickRow.Visibility = Visibility.Collapsed;
        _ctl.UpdateConfig(_ctl.Config with { StaminaCalibration = null });
        SetRegionStatus(BarType.Stamina, RegionStatusState.NotSet, "Not set yet.");
        Log.Info("MainWindow: Include-stamina checkbox unticked; calibration cleared.");
    }

    private void OnIncludeManaChecked(object sender, RoutedEventArgs e)
    {
        if (_populating) return;
        ManaPickRow.Visibility = Visibility.Visible;
        Log.Info("MainWindow: Include-mana checkbox ticked.");
    }

    private void OnIncludeManaUnchecked(object sender, RoutedEventArgs e)
    {
        if (_populating) return;
        ManaPickRow.Visibility = Visibility.Collapsed;
        _ctl.UpdateConfig(_ctl.Config with { ManaCalibration = null });
        SetRegionStatus(BarType.Mana, RegionStatusState.NotSet, "Not set yet.");
        Log.Info("MainWindow: Include-mana checkbox unticked; calibration cleared.");
    }

    private void OnFullscreenDisclaimerIsOpenChanged(object? sender, EventArgs e)
    {
        // Triggered by the InfoBar's built-in close button (IsOpen → false) and
        // by PopulateFromConfig setting IsOpen from config (suppressed via
        // _populating). Only the user-initiated dismiss path persists state.
        if (_populating) return;
        if (FullscreenDisclaimer.IsOpen) return;
        FullscreenDisclaimer.Visibility = Visibility.Collapsed;
        _ctl.UpdateConfig(_ctl.Config with { FullscreenDisclaimerDismissed = true });
        Log.Info("MainWindow: fullscreen disclaimer dismissed.");
    }

    private void SetPartyActionsBusy(bool busy, ProgressRing activeSpinner)
    {
        CreateButton.IsEnabled = !busy;
        JoinButton.IsEnabled = !busy && (PartyIdInput.Text?.Trim().Length == 6);
        activeSpinner.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------
    // Close / quit
    // ------------------------------------------------------------------

    private void OnCloseToTray(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(_ctl) { Owner = this };
        dlg.ShowDialog();
        Log.Info("MainWindow: Settings dialog closed.");
    }

    private async void OnQuitApp(object sender, RoutedEventArgs e)
    {
        _allowClose = true;
        try { await _ctl.ShutdownAsync(); }
        catch (Exception ex) { Log.Error("MainWindow: Shutdown failed.", ex); }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    /// <summary>Called by App when the user picks 'Open Game Party HUD' from tray.</summary>
    public void ShowAndActivate()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Focus();
    }
}
