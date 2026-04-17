using System;
using System.Threading;
using System.Threading.Tasks;
using GamePartyHud.Capture;
using GamePartyHud.Config;
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
    private readonly HpSmoother _smoother = new(alpha: 0.5f);
    private readonly PartyState _state;
    private readonly PeerNetwork _net;
    private readonly AppConfig _cfg;
    private readonly string _selfPeerId;
    private readonly long _joinedAt;
    private CancellationTokenSource? _loopCts;

    public string SelfPeerId => _selfPeerId;
    public PartyState State => _state;

    public PartyOrchestrator(
        AppConfig cfg,
        IScreenCapture capture,
        PartyState state,
        PeerNetwork net,
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

    private void OnPeerMessage(string fromPeerId, string text)
    {
        var msg = MessageJson.Decode(text);
        if (msg is null) return;

        // Trust-but-verify: a peer can only announce its own state.
        if (msg is StateMessage s && s.PeerId != fromPeerId) return;

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
                }

                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // Apply our own state locally so our card shows up on our HUD too.
                _state.Apply(new StateMessage(_selfPeerId, _cfg.Nickname, _cfg.Role, hp, now), now);

                // Broadcast to peers.
                var json = MessageJson.Encode(new StateMessage(_selfPeerId, _cfg.Nickname, _cfg.Role, hp, now));
                await _net.BroadcastAsync(json).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch
            {
                // Keep looping — transient errors during capture/broadcast shouldn't kill the party.
            }

            try { await Task.Delay(_cfg.PollIntervalMs + jitter, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
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
