using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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

    public MainWindow(IController controller)
    {
        InitializeComponent();
        _ctl = controller;

        RoleCombo.ItemsSource = Enum.GetValues<Role>();
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
            RoleCombo.SelectedItem = cfg.Role;

            if (cfg.HpCalibration is { } cal)
            {
                RegionStatus.Text =
                    $"Saved: {cal.Region.W}\u00D7{cal.Region.H} at ({cal.Region.X}, {cal.Region.Y}).";
            }
            else
            {
                RegionStatus.Text = "Not set yet.";
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

    private void OnRoleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populating) return;
        if (RoleCombo.SelectedItem is not Role r) return;
        _ctl.UpdateConfig(_ctl.Config with { Role = r });
        Log.Info($"MainWindow: role changed to {r}.");
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
            RegionStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
            RegionStatus.Text =
                $"Captured {region.W}\u00D7{region.H} at ({region.X}, {region.Y}).";
            Log.Info($"MainWindow: HP region calibrated {region.W}x{region.H}@({region.X},{region.Y}).");
        }
        catch (Exception ex)
        {
            Log.Error("MainWindow: OnPickRegion failed.", ex);
            RegionStatus.Foreground = System.Windows.Media.Brushes.Salmon;
            RegionStatus.Text = "Error: " + ex.Message;
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
            SetPartyStatus("You're already in a party. Leave it first before creating a new one.");
            return;
        }
        if (!ValidateBeforeJoiningParty()) return;

        SetPartyStatus("Creating party…");
        CreateButton.IsEnabled = JoinButton.IsEnabled = false;
        try
        {
            await _ctl.CreatePartyAsync();
            SetPartyStatus(_ctl.CurrentPartyId is { Length: > 0 }
                ? "Party created. Share the ID above with your teammates."
                : "Party creation didn't finish — check app.log.");
        }
        catch (Exception ex)
        {
            Log.Error("MainWindow: CreatePartyAsync failed.", ex);
            SetPartyStatus("Party creation failed: " + ex.Message);
        }
        finally
        {
            CreateButton.IsEnabled = JoinButton.IsEnabled = true;
            RefreshPartyState();
        }
    }

    private async void OnJoin(object sender, RoutedEventArgs e)
    {
        if (_ctl.CurrentPartyId is { Length: > 0 })
        {
            SetPartyStatus("You're already in a party. Leave it first before joining another.");
            return;
        }
        var id = (PartyIdInput.Text ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(id))
        {
            SetPartyStatus("Enter a party ID first.");
            PartyIdInput.Focus();
            return;
        }
        if (!ValidateBeforeJoiningParty()) return;

        SetPartyStatus("Joining party " + id + "…");
        CreateButton.IsEnabled = JoinButton.IsEnabled = false;
        try
        {
            await _ctl.JoinPartyAsync(id);
            SetPartyStatus(_ctl.CurrentPartyId is { Length: > 0 }
                ? "Joined. Waiting for teammates' state to arrive."
                : "Couldn't join. The tracker may be unreachable or your network is blocking P2P.");
        }
        catch (Exception ex)
        {
            Log.Error("MainWindow: JoinPartyAsync failed.", ex);
            SetPartyStatus("Join failed: " + ex.Message);
        }
        finally
        {
            CreateButton.IsEnabled = JoinButton.IsEnabled = true;
            RefreshPartyState();
        }
    }

    private async void OnLeave(object sender, RoutedEventArgs e)
    {
        if (_ctl.CurrentPartyId is not { Length: > 0 }) return;
        SetPartyStatus("Leaving party…");
        try
        {
            await _ctl.LeavePartyAsync();
            SetPartyStatus("You've left the party.");
        }
        catch (Exception ex)
        {
            Log.Error("MainWindow: LeavePartyAsync failed.", ex);
            SetPartyStatus("Leave failed: " + ex.Message);
        }
        finally { RefreshPartyState(); }
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_ctl.CurrentPartyId is { Length: > 0 } id)
        {
            try
            {
                Clipboard.SetText(id);
                SetPartyStatus("Copied '" + id + "' to clipboard.");
            }
            catch (Exception ex)
            {
                Log.Error("MainWindow: copy ID failed.", ex);
                SetPartyStatus("Copy failed: " + ex.Message);
            }
        }
    }

    private bool ValidateBeforeJoiningParty()
    {
        if (_ctl.Config.HpCalibration is null)
        {
            SetPartyStatus("Set your HP bar region first (see 'Pick HP bar region' above).");
            return false;
        }
        if (string.IsNullOrWhiteSpace(_ctl.Config.Nickname)
            || _ctl.Config.Nickname == AppConfig.Defaults.Nickname)
        {
            SetPartyStatus("Enter your nickname first.");
            NickText.Focus();
            return false;
        }
        return true;
    }

    private void SetPartyStatus(string message)
    {
        PartyStatus.Text = message;
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
