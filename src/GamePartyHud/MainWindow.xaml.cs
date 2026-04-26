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
        IScreenCapture Capture { get; }

        /// <summary>Current party id if we're in one; null otherwise.</summary>
        string? CurrentPartyId { get; }

        /// <summary>Count of live members on our party roster (≥1 when in a party).</summary>
        int MemberCount { get; }

        /// <summary>Fires whenever CurrentPartyId or MemberCount changes.</summary>
        event Action? PartyStateChanged;

        void UpdateConfig(AppConfig cfg);

        Task CreatePartyAsync();
        Task JoinPartyAsync(string partyId);
        Task LeavePartyAsync();

        Task ShutdownAsync();
    }

    private readonly IController _ctl;
    private bool _populating;
    private bool _allowClose;

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
            NickText.Text = cfg.Nickname == AppConfig.Defaults.Nickname ? "" : cfg.Nickname;
            RoleCombo.SelectedItem = RoleOptions.FirstOrDefault(o => o.Role == cfg.Role) ?? RoleOptions[0];

            if (cfg.HpCalibration is { } cal)
            {
                SetRegionStatus(RegionStatusState.Ok,
                    $"Saved {cal.Region.W}\u00D7{cal.Region.H} at ({cal.Region.X}, {cal.Region.Y}).");
            }
            else
            {
                SetRegionStatus(RegionStatusState.NotSet, "Not set yet.");
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

    private async void OnPickRegion(object sender, RoutedEventArgs e)
    {
        Log.Info("MainWindow: Pick-region button clicked.");
        // Opacity=0 rather than Hide() — Hide() on a WPF window with a child
        // RegionSelectorWindow.ShowDialog has surprising interactions; being
        // invisible via Opacity=0 keeps the main window alive and nothing more.
        Opacity = 0;
        try
        {
            var picker = new RegionSelectorWindow(
                "Drag a tight box around your HP bar ONLY (no nickname, no other bars)");
            picker.ShowDialog();
            if (picker.Result is not { } region)
            {
                Log.Info("MainWindow: region selection cancelled.");
                return;
            }

            var bgra = await _ctl.Capture.CaptureBgraAsync(region).ConfigureAwait(true);
            var fullColor = SampleFullColor(bgra, region.W, region.H);
            var cal = new HpCalibration(region, fullColor, HsvTolerance.Default, FillDirection.LTR);

            _ctl.UpdateConfig(_ctl.Config with
            {
                HpCalibration = cal,
                NicknameRegion = null,
            });
            SetRegionStatus(RegionStatusState.Ok,
                $"Captured {region.W}\u00D7{region.H} at ({region.X}, {region.Y}).");
            Log.Info($"MainWindow: HP region calibrated {region.W}x{region.H}@({region.X},{region.Y}).");
        }
        catch (Exception ex)
        {
            Log.Error("MainWindow: OnPickRegion failed.", ex);
            SetRegionStatus(RegionStatusState.Error, "Error: " + ex.Message);
        }
        finally
        {
            Opacity = 1;
            Activate();
        }
    }

    /// <summary>Average top + bottom 20% of the captured bar, skipping text in the middle.</summary>
    private static Hsv SampleFullColor(byte[] bgra, int w, int h)
    {
        int band = Math.Max(1, h / 5);
        double sr = 0, sg = 0, sb = 0;
        int n = 0;
        void AddRow(int y)
        {
            int x0 = w / 4, x1 = w * 3 / 4;
            for (int x = x0; x < x1; x++)
            {
                int i = (y * w + x) * 4;
                sb += bgra[i]; sg += bgra[i + 1]; sr += bgra[i + 2]; n++;
            }
        }
        for (int y = 0; y < Math.Min(band, h); y++) AddRow(y);
        for (int y = Math.Max(0, h - band); y < h; y++) AddRow(y);
        if (n == 0) return new Hsv(0, 0, 0);
        return Hsv.FromBgra(
            (byte)Math.Clamp(sb / n, 0, 255),
            (byte)Math.Clamp(sg / n, 0, 255),
            (byte)Math.Clamp(sr / n, 0, 255));
    }

    // ------------------------------------------------------------------
    // Party actions
    // ------------------------------------------------------------------

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        if (_ctl.CurrentPartyId is { Length: > 0 })
        {
            SetPartyStatus("You're already in a party. Leave it first before creating a new one.",
                InfoBarSeverity.Warning);
            return;
        }
        if (!ValidateBeforeJoiningParty()) return;

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
            SetPartyActionsBusy(false, CreateProgress);
            RefreshPartyState();
        }
    }

    private async void OnJoin(object sender, RoutedEventArgs e)
    {
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

    private void SetRegionStatus(RegionStatusState state, string text)
    {
        RegionStatus.Text = text;
        (string icon, string bg, string border, string fg) = state switch
        {
            RegionStatusState.Ok    => ("\u2713", "#333E8E3E", "#664CAF50", "#FFAEE6AE"),
            RegionStatusState.Error => ("\u2717", "#33C62828", "#66EF5350", "#FFFFB4B4"),
            _                       => ("\u25CB", "#22FFFFFF", "#33FFFFFF", "#CCCCCCCC"),
        };
        RegionStatusIcon.Text = icon;
        RegionStatusIcon.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(fg)!;
        RegionStatus.Foreground = RegionStatusIcon.Foreground;
        RegionStatusChip.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(bg)!;
        RegionStatusChip.BorderBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(border)!;
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
