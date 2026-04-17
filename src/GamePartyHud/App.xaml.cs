using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GamePartyHud.Calibration;
using GamePartyHud.Capture;
using GamePartyHud.Config;
using GamePartyHud.Hud;
using GamePartyHud.Network;
using GamePartyHud.Party;
using GamePartyHud.Tray;

namespace GamePartyHud;

/// <summary>
/// Composition root. Constructs every concrete implementation (config store, capture,
/// OCR, tray icon, HUD window, signaling, peer network, orchestrator) and wires them
/// together. Everything else in the codebase depends only on interfaces.
/// </summary>
public partial class App : Application
{
    private TrayIcon? _tray;
    private ConfigStore? _store;
    private AppConfig _config = AppConfig.Defaults;
    private WindowsScreenCapture? _capture;
    private IOcrService _ocr = new NullOcrService();
    private HudWindow? _hud;
    private PartyState? _state;
    private HudViewModelSync? _sync;
    private PartyOrchestrator? _orch;
    private string? _currentPartyId;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

#if DEBUG
        foreach (var arg in e.Args)
        {
            if (string.Equals(arg, GamePartyHud.Hud.HudSmokeHarness.CliFlag, StringComparison.Ordinal))
            {
                GamePartyHud.Hud.HudSmokeHarness.Run(this);
                return;
            }
        }
#endif

        _store = new ConfigStore();
        _config = _store.Load();
        _capture = new WindowsScreenCapture();

        try { _ocr = new OcrService(); }
        catch (Exception ex)
        {
            MessageBox.Show(
                "OCR is not available on this system. Nickname will need to be typed manually during calibration.\n\n" + ex.Message,
                "Game Party HUD", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // HUD window with local roster binding.
        _state = new PartyState();
        _hud = new HudWindow();
        _sync = new HudViewModelSync(_state, _hud.MemberList);
        _hud.Left = _config.HudPosition.X;
        _hud.Top = _config.HudPosition.Y;
        _hud.Show();

        _hud.KickRequested += async target =>
        {
            if (_orch is null || _state!.LeaderPeerId != _orch.SelfPeerId) return;
            await _orch.BroadcastLocalAsync(new KickMessage(target));
        };

        _tray = new TrayIcon();
        _tray.CalibrateRequested += RunCalibration;
        _tray.ChangeNicknameRequested += RunChangeNickname;
        _tray.ChangeRoleRequested += RunChangeRole;
        _tray.CreatePartyRequested += async () => await JoinOrCreateAsync(PartyIdGenerator.Generate());
        _tray.JoinPartyRequested += PromptAndJoin;
        _tray.CopyPartyIdRequested += () =>
        {
            if (_currentPartyId is { Length: > 0 } id)
            {
                try { Clipboard.SetText(id); } catch { }
            }
        };
        _tray.QuitRequested += async () => { await QuitAsync(); };

        // Offer one-click rejoin of the last party.
        if (_config.LastPartyId is { Length: > 0 } last)
        {
            _tray.SetPartyId(last);
        }
    }

    private async void RunCalibration()
    {
        var wiz = new CalibrationWizard(_config, _capture!, _ocr);
        if (wiz.ShowDialog() == true && wiz.Result is { } updated)
        {
            _config = updated;
            _store!.Save(_config);
        }
        await Task.CompletedTask;
    }

    private void RunChangeNickname()
    {
        var dlg = new RenameDialog(_config.Nickname);
        if (dlg.ShowDialog() == true && dlg.Value is { Length: > 0 } v)
        {
            _config = _config with { Nickname = v };
            _store!.Save(_config);
        }
    }

    private void RunChangeRole()
    {
        var dlg = new RolePickerDialog(_config.Role);
        if (dlg.ShowDialog() == true && dlg.Value is { } r)
        {
            _config = _config with { Role = r };
            _store!.Save(_config);
        }
    }

    private async void PromptAndJoin()
    {
        var dlg = new JoinPartyDialog(_config.LastPartyId);
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.PartyId))
        {
            await JoinOrCreateAsync(dlg.PartyId!);
        }
    }

    private async Task JoinOrCreateAsync(string partyId)
    {
        // Leave any existing party first.
        if (_orch is { } prev)
        {
            await prev.DisposeAsync();
            _orch = null;
        }

        var selfPeer = Guid.NewGuid().ToString("N");
        var primary = new BitTorrentSignaling();
        var fallback = new PeerJsSignaling();
        var signaling = new CompositeSignaling(primary, fallback);

        var turn = _config.CustomTurnUrl is { Length: > 0 } url
            ? new PeerNetwork.TurnCreds(url, _config.CustomTurnUsername, _config.CustomTurnCredential)
            : null;
        var net = new PeerNetwork(selfPeer, signaling, turn);

        try
        {
            await signaling.JoinAsync(partyId, selfPeer, CancellationToken.None);
        }
        catch (Exception)
        {
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
    }

    private async Task QuitAsync()
    {
        if (_orch is { } o)
        {
            await o.DisposeAsync();
            _orch = null;
        }
        Shutdown();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_orch is { } o)
        {
            try { await o.DisposeAsync(); } catch { }
        }
        if (_hud is { } h && _store is { } store)
        {
            _config = _config with { HudPosition = new HudPosition(h.Left, h.Top, 0) };
            try { store.Save(_config); } catch { }
        }
        _tray?.Dispose();
        _capture?.Dispose();
        base.OnExit(e);
    }
}
