using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using GamePartyHud.Calibration;
using GamePartyHud.Capture;
using GamePartyHud.Config;
using GamePartyHud.Diagnostics;
using GamePartyHud.Hud;
using GamePartyHud.Network;
using GamePartyHud.Party;
using GamePartyHud.Tray;

namespace GamePartyHud;

/// <summary>
/// Composition root. Constructs every concrete implementation and wires them together.
/// Also installs the global exception handlers that turn "silent crash" into "log +
/// messagebox" so field bugs are always diagnosable from <c>%AppData%\GamePartyHud\app.log</c>.
/// </summary>
public partial class App : Application
{
    private TrayIcon? _tray;
    private ConfigStore? _store;
    private AppConfig _config = AppConfig.Defaults;
    private WindowsScreenCapture? _capture;
    private HudWindow? _hud;
    private PartyState? _state;
    private HudViewModelSync? _sync;
    private PartyOrchestrator? _orch;
    private string? _currentPartyId;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        InstallGlobalExceptionHandlers();

        Log.Info("================ Game Party HUD starting ================");
        Log.Info($"Version:    {Assembly.GetExecutingAssembly().GetName().Version}");
        Log.Info($"OS:         {Environment.OSVersion}");
        Log.Info($"CLR:        {Environment.Version}");
        Log.Info($"Config:     {ConfigStore.DefaultPath()}");
        Log.Info($"Log:        {Log.LogPath}");

#if DEBUG
        foreach (var arg in e.Args)
        {
            if (string.Equals(arg, GamePartyHud.Hud.HudSmokeHarness.CliFlag, StringComparison.Ordinal))
            {
                Log.Info("DEBUG smoke harness flag detected; showing HUD with fake members.");
                GamePartyHud.Hud.HudSmokeHarness.Run(this);
                return;
            }
        }
#endif

        try
        {
            _store = new ConfigStore();
            _config = _store.Load();
            Log.Info($"Config loaded. Nickname='{_config.Nickname}', Role={_config.Role}, HpCalibration={(_config.HpCalibration is null ? "none" : "present")}, NicknameRegion={(_config.NicknameRegion is null ? "none" : "present")}, LastPartyId={_config.LastPartyId ?? "none"}.");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load config, using defaults.", ex);
        }

        _capture = new WindowsScreenCapture();
        Log.Info("Screen capture: WindowsScreenCapture (GDI BitBlt).");

        _state = new PartyState();
        _hud = new HudWindow();
        _sync = new HudViewModelSync(_state, _hud.MemberList);
        _hud.Left = _config.HudPosition.X;
        _hud.Top = _config.HudPosition.Y;
        _hud.Show();
        Log.Info($"HUD opened at ({_hud.Left}, {_hud.Top}).");

        _hud.KickRequested += OnKickRequested;

        _tray = new TrayIcon();
        _tray.PartySettingsRequested  += RunCalibrationSafe;
        _tray.CreatePartyRequested    += () => _ = JoinOrCreateSafeAsync(PartyIdGenerator.Generate());
        _tray.JoinPartyRequested      += PromptAndJoinSafe;
        _tray.CopyPartyIdRequested    += CopyPartyId;
        _tray.OpenLogFolderRequested  += OpenLogFolder;
        _tray.QuitRequested           += () => _ = QuitAsync();

        if (_config.LastPartyId is { Length: > 0 } last) _tray.SetPartyId(last);

        Log.Info("Tray icon installed. Starting the onboarding journey.");

        // Defer the startup journey until the message pump is running so ShowDialog works.
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            try { await RunStartupJourneyAsync(); }
            catch (Exception ex) { Log.Error("Startup journey crashed.", ex); }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Walks the user through the initial setup UX at app launch:
    /// 1. Setup (HP region + nickname + role on one screen). Cancel = bail out, stay in tray.
    /// 2. Party choice — Create / Join / Skip.
    /// 3. Create → show party ID + copy button. Join → prompt for ID and connect.
    /// </summary>
    private async Task RunStartupJourneyAsync()
    {
        // Step 1 — setup. Single-page form; always shown.
        var setup = new CalibrationWizard(_config, _capture!);
        var setupOk = setup.ShowDialog();
        if (setupOk != true || setup.Result is null)
        {
            Log.Info("Startup journey: setup cancelled. HUD + tray remain active; no party.");
            return;
        }
        _config = setup.Result;
        _store!.Save(_config);

        // Step 2 — party choice.
        var choice = new PartyChoiceDialog();
        if (choice.ShowDialog() != true)
        {
            Log.Info("Startup journey: party choice dismissed. Staying in tray with no party.");
            return;
        }

        switch (choice.Choice)
        {
            case PartyChoice.Create:
                var newId = PartyIdGenerator.Generate();
                await JoinOrCreateAsync(newId);
                if (_currentPartyId is { Length: > 0 } created)
                {
                    var dlg = new PartyCreatedDialog(created);
                    dlg.ShowDialog();
                }
                break;

            case PartyChoice.Join:
                var joinDlg = new JoinPartyDialog(_config.LastPartyId);
                if (joinDlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(joinDlg.PartyId))
                {
                    await JoinOrCreateAsync(joinDlg.PartyId!);
                }
                break;

            case PartyChoice.Skip:
                Log.Info("Startup journey: user chose to skip party setup.");
                break;
        }
    }

    // ---------- global exception handlers ----------

    private void InstallGlobalExceptionHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainException;
        DispatcherUnhandledException += OnDispatcherException;
        TaskScheduler.UnobservedTaskException += OnTaskException;
    }

    private static void OnAppDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        var ex = e.ExceptionObject as Exception;
        Log.Error($"Unhandled AppDomain exception (terminating={e.IsTerminating}).", ex);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled dispatcher exception.", e.Exception);
        try
        {
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{e.Exception.GetType().Name}: {e.Exception.Message}\n\nA full log was saved to:\n{Log.LogPath}",
                "Game Party HUD", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* best-effort */ }
        e.Handled = true; // keep the app alive if at all possible
    }

    private static void OnTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }

    // ---------- tray handlers (each wrapped so no click ever silently crashes) ----------

    private void RunCalibrationSafe()
    {
        try { RunCalibration(); }
        catch (Exception ex) { Log.Error("Calibration flow crashed.", ex); ShowErrorDialog(ex); }
    }

    private async Task JoinOrCreateSafeAsync(string partyId)
    {
        try { await JoinOrCreateAsync(partyId); }
        catch (Exception ex) { Log.Error("Join/create party crashed.", ex); ShowErrorDialog(ex); }
    }

    private void PromptAndJoinSafe()
    {
        try { PromptAndJoin(); }
        catch (Exception ex) { Log.Error("Join-party prompt crashed.", ex); ShowErrorDialog(ex); }
    }

    private void CopyPartyId()
    {
        if (_currentPartyId is { Length: > 0 } id)
        {
            try { Clipboard.SetText(id); Log.Info($"Copied party ID '{id}' to clipboard."); }
            catch (Exception ex) { Log.Error("Copy party ID failed.", ex); }
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Log.LogDirectory,
                UseShellExecute = true
            });
            Log.Info($"Opened log folder: {Log.LogDirectory}.");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to open log folder.", ex);
            MessageBox.Show($"Log folder:\n{Log.LogDirectory}",
                "Game Party HUD", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void OnKickRequested(string target)
    {
        try
        {
            if (_orch is null || _state!.LeaderPeerId != _orch.SelfPeerId)
            {
                Log.Info($"Kick requested for {target} but ignored (not leader).");
                return;
            }
            Log.Info($"Kicking {target}.");
            await _orch.BroadcastLocalAsync(new KickMessage(target));
        }
        catch (Exception ex)
        {
            Log.Error("Kick broadcast failed.", ex);
        }
    }

    // ---------- actual flows ----------

    private void RunCalibration()
    {
        Log.Info("Opening calibration wizard.");
        var wiz = new CalibrationWizard(_config, _capture!);
        var ok = wiz.ShowDialog();
        Log.Info($"Calibration wizard closed (DialogResult={ok}).");
        if (ok == true && wiz.Result is { } updated)
        {
            _config = updated;
            _store!.Save(_config);
            Log.Info($"Config saved. HP region={_config.HpCalibration?.Region}, nickname='{_config.Nickname}', role={_config.Role}.");
        }
    }

    private void PromptAndJoin()
    {
        var dlg = new JoinPartyDialog(_config.LastPartyId);
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.PartyId))
        {
            _ = JoinOrCreateSafeAsync(dlg.PartyId!);
        }
    }

    private async Task JoinOrCreateAsync(string partyId)
    {
        Log.Info($"Joining party '{partyId}'.");

        if (_orch is { } prev)
        {
            Log.Info("Tearing down previous party before joining new one.");
            await prev.DisposeAsync();
            _orch = null;
        }

        var selfPeer = Guid.NewGuid().ToString("N");
        var signaling = new BitTorrentSignaling();

        var turn = _config.CustomTurnUrl is { Length: > 0 } url
            ? new PeerNetwork.TurnCreds(url, _config.CustomTurnUsername, _config.CustomTurnCredential)
            : null;
        if (turn is not null) Log.Info($"Using custom TURN URL: {turn.Url}");

        var net = new PeerNetwork(selfPeer, signaling, turn);
        net.OnPeerConnected    += id => Log.Info($"Peer connected: {id}");
        net.OnPeerDisconnected += id => Log.Info($"Peer disconnected: {id}");

        try
        {
            await signaling.JoinAsync(partyId, selfPeer, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error($"Signaling join failed for party '{partyId}'.", ex);
            MessageBox.Show(
                "Could not connect to party — your network may be blocking P2P connections. " +
                "See README.md / docs/requirements.md for workarounds " +
                "(UPnP / open NAT, gaming VPN, or a custom TURN URL in the config file).",
                "Game Party HUD", MessageBoxButton.OK, MessageBoxImage.Warning);
            await net.DisposeAsync();
            return;
        }

        _orch = new PartyOrchestrator(_config, _capture!, _state!, net, selfPeer);
        _orch.StartLoops();
        _currentPartyId = partyId;
        _tray!.SetPartyId(partyId);

        _config = _config with { LastPartyId = partyId };
        _store!.Save(_config);
        Log.Info($"Party '{partyId}' joined. Self peer id={selfPeer}. Capture+broadcast loop started.");
    }

    private async Task QuitAsync()
    {
        Log.Info("Quit requested.");
        if (_orch is { } o)
        {
            await o.DisposeAsync();
            _orch = null;
        }
        Shutdown();
    }

    private static void ShowErrorDialog(Exception ex)
    {
        try
        {
            MessageBox.Show(
                $"An error occurred:\n\n{ex.GetType().Name}: {ex.Message}\n\nA full log was saved to:\n{Log.LogPath}",
                "Game Party HUD", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* last resort */ }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Info("================ Game Party HUD shutting down ================");
        if (_orch is { } o)
        {
            try { await o.DisposeAsync(); }
            catch (Exception ex) { Log.Error("Orchestrator dispose failed.", ex); }
        }
        if (_hud is { } h && _store is { } store)
        {
            _config = _config with { HudPosition = new HudPosition(h.Left, h.Top, 0) };
            try { store.Save(_config); } catch (Exception ex) { Log.Error("Final config save failed.", ex); }
        }
        _tray?.Dispose();
        _capture?.Dispose();
        base.OnExit(e);
    }
}
