using System;
using System.Threading;
using System.Threading.Tasks;
using GamePartyHud.Capture;
using GamePartyHud.Config;
using GamePartyHud.Diagnostics;
using GamePartyHud.Network;

namespace GamePartyHud.Party;

/// <summary>
/// Drives the steady-state loop after a party is joined:
///   1. Every <see cref="AppConfig.PollIntervalMs"/> ms, capture the HP region,
///      analyze it, apply self-state locally, and broadcast to peers.
///   2. Every second, tick <see cref="PartyState"/> so stale/remove transitions fire.
/// Also forwards incoming peer messages to <see cref="PartyState"/>.
/// </summary>
public sealed class PartyOrchestrator : IAsyncDisposable
{
    private readonly IScreenCapture _capture;
    private readonly HpBarAnalyzer _analyzer = new();
    private readonly HpSmoother _smoother = new(windowSize: 3);
    private readonly PartyState _state;
    private readonly RelayClient _net;
    // _cfg is mutable so that nickname / role / poll-interval / calibration
    // changes from the UI propagate into the broadcast loop without
    // recreating the orchestrator. Updated via <see cref="UpdateConfig"/>.
    private AppConfig _cfg;
    private readonly string _selfPeerId;
    private readonly long _joinedAt;
    private CancellationTokenSource? _loopCts;

    private int _tickCounter;

    public string SelfPeerId => _selfPeerId;
    public PartyState State => _state;

    public PartyOrchestrator(
        AppConfig cfg,
        IScreenCapture capture,
        PartyState state,
        RelayClient net,
        string selfPeerId)
    {
        _cfg = cfg;
        _capture = capture;
        _state = state;
        _net = net;
        _selfPeerId = selfPeerId;
        _joinedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _net.OnMessage += OnPeerMessage;
    }

    /// <summary>
    /// Replace the orchestrator's view of <see cref="AppConfig"/>. Called by
    /// <c>App.xaml.cs</c> whenever the user edits their nickname, role,
    /// HP-bar calibration, or poll interval from the UI. The next broadcast
    /// tick (≤ <see cref="AppConfig.PollIntervalMs"/> later) will use the
    /// new values, both for the locally-applied self-state on the HUD and
    /// for the <c>StateMessage</c> sent to other peers.
    /// </summary>
    public void UpdateConfig(AppConfig cfg)
    {
        _cfg = cfg;
        Log.Info($"PartyOrchestrator: config updated (nickname='{cfg.Nickname}', role={cfg.Role}, pollMs={cfg.PollIntervalMs}).");
    }

    private void OnPeerMessage(string fromPeerId, string text)
    {
        var msg = MessageJson.Decode(text);
        if (msg is null)
        {
            Log.Warn($"PartyOrchestrator: dropped undecodable message from {fromPeerId[..Math.Min(8, fromPeerId.Length)]}… ({text.Length} bytes).");
            return;
        }

        // Trust-but-verify: a peer can only announce its own state.
        if (msg is StateMessage s && s.PeerId != fromPeerId)
        {
            Log.Warn($"PartyOrchestrator: spoofing attempt — message claimed peer_id={s.PeerId} but came from {fromPeerId}. Dropped.");
            return;
        }

        switch (msg)
        {
            case StateMessage st:
                Log.Info($"PartyOrchestrator: ← state from {fromPeerId[..Math.Min(8, fromPeerId.Length)]}… (nick='{st.Nick}', hp={st.Hp:F3}).");
                break;
            case ByeMessage:
                Log.Info($"PartyOrchestrator: ← bye from {fromPeerId[..Math.Min(8, fromPeerId.Length)]}….");
                break;
            case KickMessage km:
                Log.Info($"PartyOrchestrator: ← kick from {fromPeerId[..Math.Min(8, fromPeerId.Length)]}… targeting {km.Target[..Math.Min(8, km.Target.Length)]}….");
                break;
        }

        _state.Apply(msg, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    public void StartLoops()
    {
        _loopCts = new CancellationTokenSource();
        _ = Task.Run(() => PollAndBroadcastLoopAsync(_loopCts.Token));
        _ = Task.Run(() => StaleTickLoopAsync(_loopCts.Token));
    }

    private async Task PollAndBroadcastLoopAsync(CancellationToken ct)
    {
        // Deterministic per-peer jitter (0–250 ms) so 20 peers don't all broadcast on the same boundary.
        int jitter = Math.Abs(_selfPeerId.GetHashCode()) % 250;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                float? hp = null;
                if (_cfg.HpCalibration is { } cal)
                {
                    var bgra = await _capture.CaptureBgraAsync(cal.Region, ct).ConfigureAwait(false);
                    float raw = _analyzer.Analyze(bgra, cal.Region.W, cal.Region.H, cal);
                    hp = _smoother.Push(raw);

                    LogTick(cal, bgra, raw, hp.Value);
                }

                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // Apply our own state locally so our card shows up on our HUD too.
                _state.Apply(new StateMessage(_selfPeerId, _cfg.Nickname, _cfg.Role, hp, now), now);

                // Broadcast to peers.
                var json = MessageJson.Encode(new StateMessage(_selfPeerId, _cfg.Nickname, _cfg.Role, hp, now));
                await _net.BroadcastAsync(json).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Keep looping — transient errors during capture/broadcast shouldn't kill the party,
                // but they should show up in the log so we can diagnose.
                Log.Error("PartyOrchestrator: capture/broadcast tick failed; continuing.", ex);
            }

            try { await Task.Delay(_cfg.PollIntervalMs + jitter, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void LogTick(HpCalibration cal, byte[] bgra, float raw, float smoothed)
    {
        _tickCounter++;
        int w = cal.Region.W;
        int h = cal.Region.H;

        // Per-column match count using the SAME classifier the analyzer uses
        // (saturated red, calibration-free). This lets us see at-a-glance whether the
        // capture pixels look like a red bar at all.
        int minMatches = Math.Max(2, h / 5);
        int pass = 0, partial = 0, empty = 0;
        for (int x = 0; x < w; x++)
        {
            int matches = 0;
            for (int y = 0; y < h; y++)
            {
                int idx = (y * w + x) * 4;
                var hsv = Hsv.FromBgra(bgra[idx], bgra[idx + 1], bgra[idx + 2]);
                if (HpBarAnalyzer.IsFilledPixel(hsv)) matches++;
            }
            if (matches == 0) empty++;
            else if (matches < minMatches) partial++;
            else pass++;
        }

        // Sample average HSV of the middle-third so we can see if the capture actually
        // contains a red bar (good) or something else (sign of a region-selection issue).
        var midAvg = CaptureDiagnostic.AverageHsv(bgra, w, h, w / 3, 2 * w / 3);

        Log.Info(
            $"PartyOrchestrator tick#{_tickCounter}: raw={raw:F3} smoothed={smoothed:F3} " +
            $"region={w}x{h}@({cal.Region.X},{cal.Region.Y}) " +
            $"cols {pass}/{partial}/{empty} pass/partial/empty; " +
            $"mid-HSV H={midAvg.H:F0}° S={midAvg.S:F2} V={midAvg.V:F2}");
    }

    private async Task StaleTickLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            _state.Tick(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            try { await Task.Delay(1000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Apply a message locally AND broadcast it to all peers in one call.</summary>
    public Task BroadcastLocalAsync(PartyMessage msg)
    {
        _state.Apply(msg, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        return _net.BroadcastAsync(MessageJson.Encode(msg));
    }

    public async Task LeaveAsync()
    {
        try
        {
            var bye = MessageJson.Encode(new ByeMessage(_selfPeerId));
            await _net.BroadcastAsync(bye).ConfigureAwait(false);
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        _loopCts?.Cancel();
        await LeaveAsync().ConfigureAwait(false);
        await _net.DisposeAsync().ConfigureAwait(false);
    }
}
