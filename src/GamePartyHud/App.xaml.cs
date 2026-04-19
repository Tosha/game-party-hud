using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
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
public partial class App : Application, MainWindow.IController
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
    private MainWindow? _main;

    // -- MainWindow.IController surface --------------------------------------

    AppConfig MainWindow.IController.Config => _config;
    IScreenCapture MainWindow.IController.Capture => _capture!;
    string? MainWindow.IController.CurrentPartyId => _currentPartyId;
    int MainWindow.IController.MemberCount => _state?.Members.Count ?? 0;

    public event Action? PartyStateChanged;

    void MainWindow.IController.UpdateConfig(AppConfig cfg)
    {
        _config = cfg;
        try { _store?.Save(_config); }
        catch (Exception ex) { Log.Error("Failed to persist config.", ex); }
    }

    Task MainWindow.IController.CreatePartyAsync() =>
        JoinOrCreateAsync(PartyIdGenerator.Generate());

    Task MainWindow.IController.JoinPartyAsync(string partyId) =>
        JoinOrCreateAsync(partyId);

    Task MainWindow.IController.LeavePartyAsync() => LeavePartyAsync();

    Task MainWindow.IController.ShutdownAsync() => QuitAsync();

    // -- startup -------------------------------------------------------------

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
            Log.Info($"Config loaded. Nickname='{_config.Nickname}', Role={_config.Role}, HpCalibration={(_config.HpCalibration is null ? "none" : "present")}, LastPartyId={_config.LastPartyId ?? "none"}.");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load config, using defaults.", ex);
        }

        _capture = new WindowsScreenCapture();
        Log.Info("Screen capture: WindowsScreenCapture (GDI BitBlt).");

        _state = new PartyState();
        _state.Changed += () => PartyStateChanged?.Invoke();

        _hud = new HudWindow();
        _sync = new HudViewModelSync(_state, _hud.MemberList);
        _hud.Left = _config.HudPosition.X;
        _hud.Top = _config.HudPosition.Y;
        _hud.Show();
        Log.Info($"HUD opened at ({_hud.Left}, {_hud.Top}).");

        _hud.KickRequested += OnKickRequested;

        _tray = new TrayIcon();
        _tray.ShowMainWindowRequested += () => _main?.ShowAndActivate();
        _tray.OpenLogFolderRequested  += OpenLogFolder;
        _tray.SaveTestCaptureRequested += SaveTestCaptureSafe;
        _tray.QuitRequested           += () => _ = QuitAsync();

        _main = new MainWindow(this);
        _main.Show();
        Log.Info("Main window shown.");
    }

    // -- global exception handlers -------------------------------------------

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
        e.Handled = true;
    }

    private static void OnTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error("Unobserved task exception.", e.Exception);
        e.SetObserved();
    }

    // -- tray handlers -------------------------------------------------------

    private void OpenLogFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = Log.LogDirectory, UseShellExecute = true });
            Log.Info($"Opened log folder: {Log.LogDirectory}.");
        }
        catch (Exception ex)
        {
            Log.Error("Failed to open log folder.", ex);
            MessageBox.Show($"Log folder:\n{Log.LogDirectory}",
                "Game Party HUD", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void SaveTestCaptureSafe()
    {
        try
        {
            if (_capture is null)
            {
                MessageBox.Show("Screen capture isn't ready yet — try again in a moment.",
                    "Game Party HUD", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var pngPath = await CaptureDiagnostic.RunAsync(_config, _capture, reason: "manual");
            var body = pngPath is null
                ? "Couldn't save a test capture. Set your HP bar region first (in the main window) and check app.log."
                : $"Saved:\n  {pngPath}\n  {pngPath}.txt\n\nAttach both files to your bug report.";
            MessageBox.Show(body, "Game Party HUD \u2014 Test Capture",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error("SaveTestCapture failed.", ex);
            ShowErrorDialog(ex);
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

    // -- party lifecycle -----------------------------------------------------

    private async Task JoinOrCreateAsync(string partyId)
    {
        // One-party rule: refuse to start a new one without explicit Leave.
        if (_orch is not null || _currentPartyId is not null)
        {
            Log.Warn($"JoinOrCreateAsync('{partyId}') refused: already in party '{_currentPartyId}'. Leave first.");
            return;
        }

        Log.Info($"Joining party '{partyId}'.");

        var selfPeer = Guid.NewGuid().ToString("N");
        var signaling = new BitTorrentSignaling();
        var turn = _config.CustomTurnUrl is { Length: > 0 } url
            ? new PeerNetwork.TurnCreds(url, _config.CustomTurnUsername, _config.CustomTurnCredential)
            : null;
        if (turn is not null) Log.Info($"Using custom TURN URL: {turn.Url}");

        var net = new PeerNetwork(selfPeer, signaling, turn);
        net.OnPeerConnected    += id => { Log.Info($"Peer connected: {id}"); PartyStateChanged?.Invoke(); };
        net.OnPeerDisconnected += id => { Log.Info($"Peer disconnected: {id}"); PartyStateChanged?.Invoke(); };

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

        PartyStateChanged?.Invoke();
    }

    private async Task LeavePartyAsync()
    {
        if (_orch is not { } orch)
        {
            Log.Info("LeavePartyAsync: no active party; nothing to do.");
            return;
        }

        Log.Info($"Leaving party '{_currentPartyId}'.");
        _orch = null;
        _currentPartyId = null;
        _tray?.SetPartyId(null);

        try
        {
            await orch.DisposeAsync();
            Log.Info("Party left and orchestrator disposed.");
        }
        catch (Exception ex)
        {
            Log.Error("Error while leaving party.", ex);
        }

        // Clear the local roster so the HUD drops stale peers immediately.
        if (_state is not null)
        {
            // Tick with 'far future' to remove anyone we have cached.
            _state.Tick(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3600);
        }

        PartyStateChanged?.Invoke();
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
