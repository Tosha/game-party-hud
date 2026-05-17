using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using GamePartyHud.Bars;
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

    private static readonly RoleOption[] RoleOptions =
        Enum.GetValues<Role>()
            .Select(r => new RoleOption(r, RoleGlyph.For(r), RoleDisplay.For(r)))
            .ToArray();

    /// <summary>
    /// One row in the PresetCombo dropdown. Wraps a Preset id + display name with
    /// edit-mode state for inline rename (Task 9), and a flag distinguishing the
    /// "+ New preset" command row from real preset rows (Task 8). Public so the
    /// PresetItemTemplateSelector can reflect on it.
    /// </summary>
    public sealed class PresetItemViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        private string _name = "";
        private bool _isEditing;

        public string Id { get; init; } = "";
        public bool IsCommandRow { get; init; }

        public string Name
        {
            get => _name;
            set { if (_name != value) { _name = value; OnPropertyChanged(nameof(Name)); } }
        }

        public bool IsEditing
        {
            get => _isEditing;
            set { if (_isEditing != value) { _isEditing = value; OnPropertyChanged(nameof(IsEditing)); } }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string name) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
    }

    private readonly System.Collections.ObjectModel.ObservableCollection<PresetItemViewModel> _presetItems = new();
    private readonly IScreenCapture _capture;

    public MainWindow(IController controller, IScreenCapture capture)
    {
        InitializeComponent();
        _ctl = controller;
        _capture = capture;

        RoleCombo.ItemsSource = RoleOptions;
        PresetCombo.ItemsSource = _presetItems;

        // Each BarCard owns its own preview source; AttachCapture wires the
        // screen-grabber that the card lazily uses on first Calibration set.
        HpCard.AttachCapture(_capture);
        StaminaCard.AttachCapture(_capture);
        ManaCard.AttachCapture(_capture);

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

        // Pause preview captures while the settings window is hidden (close
        // to tray) so we don't spend CPU sampling the screen for a UI no
        // one is looking at.
        IsVisibleChanged += OnMainWindowIsVisibleChanged;
    }

    private void OnMainWindowIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        bool visible = IsVisible;
        HpCard.SetWindowVisible(visible);
        StaminaCard.SetWindowVisible(visible);
        ManaCard.SetWindowVisible(visible);
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
            RebuildPresetItems();
            PresetCombo.SelectedItem = _presetItems.FirstOrDefault(i => i.Id == cfg.ActivePresetId);
            var ap = cfg.ActivePreset;
            var defaultPreset = AppConfig.Defaults.ActivePreset;
            FullscreenDisclaimer.IsOpen = !cfg.FullscreenDisclaimerDismissed;
            // Collapse the whole control (not just its inner content) when
            // dismissed, otherwise wpfui's InfoBar keeps its layout slot
            // even at IsOpen=false and leaves ~30px of empty space at the
            // top of the window.
            FullscreenDisclaimer.Visibility = cfg.FullscreenDisclaimerDismissed
                ? Visibility.Collapsed
                : Visibility.Visible;
            NickText.Text = ap.Nickname == defaultPreset.Nickname ? "" : ap.Nickname;
            RoleCombo.SelectedItem = RoleOptions.FirstOrDefault(o => o.Role == ap.Role) ?? RoleOptions[0];
            // Prepopulate the join input with the last party id the user
            // joined or created. The TextChanged handler will pick this up
            // and flip the Join button to green if it's a complete id,
            // so a returning user can rejoin with a single click.
            PartyIdInput.Text = cfg.LastPartyId ?? "";

            // The cards manage their own preview, status icon, and button
            // appearance from these two properties; just feed in the active
            // preset's values and they take care of the rest.
            HpCard.Calibration      = ap.HpCalibration;
            HpCard.IsBarEnabled     = true;
            StaminaCard.Calibration  = ap.StaminaCalibration;
            StaminaCard.IsBarEnabled = ap.StaminaEnabled;
            ManaCard.Calibration     = ap.ManaCalibration;
            ManaCard.IsBarEnabled    = ap.ManaEnabled;
        }
        finally { _populating = false; }
    }

    private void OnCtlPartyStateChanged()
    {
        // PartyStateChanged may fire off the UI thread.
        Dispatcher.Invoke(RefreshPartyState);
    }

    /// <summary>
    /// Rebuilds the PresetCombo dropdown items from the current AppConfig.Presets list.
    /// Called from PopulateFromConfig (initial load + after every config-change refresh).
    /// The selected item is set by the caller; the _populating guard prevents the
    /// SelectionChanged handler from treating that programmatic selection as a user action.
    /// </summary>
    private void RebuildPresetItems()
    {
        var cfg = _ctl.Config;
        _presetItems.Clear();
        foreach (var p in cfg.Presets)
        {
            _presetItems.Add(new PresetItemViewModel { Id = p.Id, Name = p.Name });
        }
        _presetItems.Add(new PresetItemViewModel { Id = "", Name = "+ New preset", IsCommandRow = true });
    }

    private void OnPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populating) return;
        if (PresetCombo.SelectedItem is not PresetItemViewModel item) return;
        if (item.IsCommandRow)
        {
            OnCreatePreset();
            return;
        }

        // No-op if the user clicked the already-active row.
        if (item.Id == _ctl.Config.ActivePresetId) return;

        _ctl.UpdateConfig(_ctl.Config with { ActivePresetId = item.Id });
        PopulateFromConfig();             // refreshes Nickname / Role / Bars
        UpdateJoinButtonState();          // HP-calibration of new preset may flip JoinButton state
        Log.Info($"MainWindow: active preset changed to '{item.Name}' (Id={item.Id}).");
    }

    /// <summary>
    /// Creates a new empty preset (placeholder name "New preset"/"New preset N"),
    /// activates it, refreshes the UI, and reverts PresetCombo's selection from
    /// the "+ New preset" command row to the newly-created real row. The user
    /// can then fill in nickname / role / bar regions exactly as on a fresh install.
    /// </summary>
    private void OnCreatePreset()
    {
        var cfg = _ctl.Config;
        var newId = Guid.NewGuid().ToString();
        var newName = NextAvailablePresetName(cfg);

        var newPreset = new Preset(
            Id: newId,
            Name: newName,
            Nickname: "",
            Role: Role.Utility,
            HpCalibration: null,
            StaminaCalibration: null,
            ManaCalibration: null);

        var updated = cfg with
        {
            Presets = cfg.Presets.Append(newPreset).ToList(),
            ActivePresetId = newId,
        };
        _ctl.UpdateConfig(updated);

        PopulateFromConfig();             // refreshes everything against the new preset
        UpdateJoinButtonState();          // empty calibration -> Join stays disabled

        // PopulateFromConfig set SelectedItem; the dropdown now reads as the new
        // preset row. The command row is no longer selected.
        Log.Info($"MainWindow: created new preset '{newName}' (Id={newId}).");
    }

    private static string NextAvailablePresetName(AppConfig cfg)
    {
        const string baseName = "New preset";
        var existing = cfg.Presets.Select(p => p.Name).ToHashSet();
        if (!existing.Contains(baseName)) return baseName;
        for (int n = 2; n < 100; n++)
        {
            var candidate = $"{baseName} {n}";
            if (!existing.Contains(candidate)) return candidate;
        }
        return $"{baseName} {DateTime.UtcNow.Ticks}"; // pathological fallback
    }

    /// <summary>
    /// Pencil icon click → enter inline rename mode for the clicked row.
    /// Keeps the dropdown open so the TextBox stays visible while the user types.
    /// </summary>
    private void OnPresetRenameClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
        var item = _presetItems.FirstOrDefault(i => i.Id == id);
        if (item is null) return;

        foreach (var other in _presetItems) other.IsEditing = false; // only one row in edit at a time
        item.IsEditing = true;
        PresetCombo.IsDropDownOpen = true;

        // Defer focus until after the visual swap completes so the new TextBox exists.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var textBox = FindTextBoxForPreset(id);
            if (textBox is not null)
            {
                textBox.Focus();
                textBox.SelectAll();
            }
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private System.Windows.Controls.TextBox? FindTextBoxForPreset(string id)
    {
        // Walk the dropdown's visual tree to find the TextBox whose Tag matches id.
        // Uses System.Windows.Controls.TextBox (the bare <TextBox> in XAML) rather
        // than wpfui's Wpf.Ui.Controls.TextBox.
        foreach (var obj in _presetItems)
        {
            var container = PresetCombo.ItemContainerGenerator.ContainerFromItem(obj) as ComboBoxItem;
            if (container is null) continue;
            var tb = FindChild<System.Windows.Controls.TextBox>(container, t => (t.Tag as string) == id);
            if (tb is not null) return tb;
        }
        return null;
    }

    private static T? FindChild<T>(DependencyObject parent, Func<T, bool> match) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t && match(t)) return t;
            var grand = FindChild<T>(child, match);
            if (grand is not null) return grand;
        }
        return null;
    }

    private void OnPresetRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb || tb.Tag is not string id) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitOrRevertRename(id, tb, commit: true);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CommitOrRevertRename(id, tb, commit: false);
        }
    }

    private void OnPresetRenameLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox tb || tb.Tag is not string id) return;
        CommitOrRevertRename(id, tb, commit: true);
    }

    /// <summary>
    /// ✕ icon click → confirm and delete the preset. If only one preset remains
    /// the delete is refused (we always need at least one). If the deleted preset
    /// is the currently-active one, switch to the first remaining preset.
    /// </summary>
    private void OnPresetDeleteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
        var cfg = _ctl.Config;
        var target = cfg.Presets.FirstOrDefault(p => p.Id == id);
        if (target is null) return;

        if (cfg.Presets.Count <= 1)
        {
            System.Windows.MessageBox.Show(
                "At least one preset is required.",
                "Game Party HUD",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        var confirm = System.Windows.MessageBox.Show(
            $"Delete preset '{target.Name}'?",
            "Game Party HUD",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);
        if (confirm != System.Windows.MessageBoxResult.Yes) return;

        var remaining = cfg.Presets.Where(p => p.Id != id).ToList();
        var newActive = cfg.ActivePresetId == id ? remaining[0].Id : cfg.ActivePresetId;
        var updated = cfg with { Presets = remaining, ActivePresetId = newActive };

        _ctl.UpdateConfig(updated);
        PopulateFromConfig();              // refreshes selector + Profile/Bars
        UpdateJoinButtonState();
        Log.Info($"MainWindow: deleted preset '{target.Name}' (Id={id}); active is now {newActive}.");
    }

    private void CommitOrRevertRename(string id, System.Windows.Controls.TextBox tb, bool commit)
    {
        var item = _presetItems.FirstOrDefault(i => i.Id == id);
        if (item is null) return;

        if (!commit)
        {
            var stored = _ctl.Config.Presets.FirstOrDefault(p => p.Id == id);
            if (stored is not null) item.Name = stored.Name;
            item.IsEditing = false;
            return;
        }

        var raw = (tb.Text ?? "").Trim();
        if (raw.Length == 0)
        {
            // Empty → revert to stored name.
            var stored = _ctl.Config.Presets.FirstOrDefault(p => p.Id == id);
            if (stored is not null) item.Name = stored.Name;
            item.IsEditing = false;
            return;
        }

        bool collides = _ctl.Config.Presets.Any(p => p.Id != id && p.Name == raw);
        if (collides)
        {
            System.Windows.MessageBox.Show(
                $"A preset named '{raw}' already exists.",
                "Game Party HUD",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            var stored = _ctl.Config.Presets.FirstOrDefault(p => p.Id == id);
            if (stored is not null) item.Name = stored.Name;
            item.IsEditing = false;
            return;
        }

        var cfg = _ctl.Config;
        var updated = cfg with
        {
            Presets = cfg.Presets
                .Select(p => p.Id == id ? p with { Name = raw } : p)
                .ToList(),
        };
        _ctl.UpdateConfig(updated);
        item.Name = raw;
        item.IsEditing = false;
        Log.Info($"MainWindow: renamed preset Id={id} to '{raw}'.");
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
        _ctl.UpdateConfig(_ctl.Config.UpdatePreset(p => p with { Nickname = nick }));
        Log.Info($"MainWindow: nickname changed to '{nick}'.");
    }

    private void OnPartyIdInputChanged(object sender, TextChangedEventArgs e)
    {
        UpdateJoinButtonState();
    }

    /// <summary>
    /// Drives both the enabled state and the visual Appearance of the Join
    /// button: green (Success) when a complete 6-character party id has been
    /// entered, plain Secondary otherwise. Called from input changes and
    /// from the busy-state helper.
    /// </summary>
    private void UpdateJoinButtonState(bool busy = false)
    {
        bool valid = (PartyIdInput.Text?.Trim().Length == 6);
        JoinButton.IsEnabled = !busy && valid;
        JoinButton.Appearance = (!busy && valid)
            ? ControlAppearance.Success
            : ControlAppearance.Secondary;
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
        _ctl.UpdateConfig(_ctl.Config.UpdatePreset(p => p with { Role = opt.Role }));
        Log.Info($"MainWindow: role changed to {opt.Role}.");
    }

    // Card-specific pick handlers — each forwards into the shared
    // PickRegionForBar() flow with the appropriate BarType.
    private void OnHpPickRequested(object? sender, EventArgs e) => PickRegionForBar(BarType.Hp);
    private void OnStaminaPickRequested(object? sender, EventArgs e) => PickRegionForBar(BarType.Stamina);
    private void OnManaPickRequested(object? sender, EventArgs e) => PickRegionForBar(BarType.Mana);

    private void OnStaminaEnabledChanged(object? sender, EventArgs e)
    {
        var enabled = StaminaCard.IsBarEnabled;
        _ctl.UpdateConfig(_ctl.Config.UpdatePreset(p => p with { StaminaEnabled = enabled }));
        Log.Info($"MainWindow: stamina broadcast enabled={enabled}.");
    }

    private void OnManaEnabledChanged(object? sender, EventArgs e)
    {
        var enabled = ManaCard.IsBarEnabled;
        _ctl.UpdateConfig(_ctl.Config.UpdatePreset(p => p with { ManaEnabled = enabled }));
        Log.Info($"MainWindow: mana broadcast enabled={enabled}.");
    }

    private BarCard CardFor(BarType bar) => bar switch
    {
        BarType.Hp      => HpCard,
        BarType.Stamina => StaminaCard,
        BarType.Mana    => ManaCard,
        _ => HpCard
    };

    private void PickRegionForBar(BarType bar)
    {
        Log.Info($"MainWindow: Pick-{bar}-region requested.");
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
                BarType.Hp      => _ctl.Config.UpdatePreset(p => p with { HpCalibration      = cal }),
                BarType.Stamina => _ctl.Config.UpdatePreset(p => p with { StaminaCalibration = cal }),
                BarType.Mana    => _ctl.Config.UpdatePreset(p => p with { ManaCalibration    = cal }),
                _ => _ctl.Config
            };
            _ctl.UpdateConfig(newConfig);

            // Push calibration into the card; clears its pick-time override
            // and restarts the preview at the new region.
            var card = CardFor(bar);
            card.Calibration = cal;

            // Run pick-time validation once against the just-captured region
            // so the "bar wasn't full" warning sticks until next re-pick.
            // WindowsScreenCapture is synchronous under the hood (BitBlt +
            // ValueTask.FromResult), so .GetAwaiter().GetResult() here can't
            // deadlock.
            var bgra = _capture.CaptureBgraAsync(cal.Region).AsTask().GetAwaiter().GetResult();
            var pickTimeResult = BarRegionValidator.Validate(cal.Region, bgra, isPickTime: true);
            card.SetPickTimeValidation(
                pickTimeResult.Level == ValidationLevel.Ok ? null : pickTimeResult);

            Log.Info($"MainWindow: {bar} region calibrated {region.W}x{region.H}@({region.X},{region.Y}).");
        }
        catch (Exception ex)
        {
            Log.Error($"MainWindow: PickRegionForBar ({bar}) failed.", ex);
        }
        finally
        {
            Opacity = 1;
            Activate();
        }
    }

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
        var ap = _ctl.Config.ActivePreset;
        if (ap.HpCalibration is null)
        {
            SetPartyStatus("Set your HP bar region first (see 'Pick HP bar region' above).",
                InfoBarSeverity.Warning);
            return false;
        }
        if (string.IsNullOrWhiteSpace(ap.Nickname)
            || ap.Nickname == AppConfig.Defaults.ActivePreset.Nickname)
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
        UpdateJoinButtonState(busy);
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
