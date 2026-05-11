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
    // Bar changes smaller than this don't justify a network broadcast — the
    // visual delta on a 170-px HUD bar is sub-pixel. Receivers learn about
    // the new value at the next ≥ 1 % move on any bar or the next heartbeat,
    // whichever comes first.
    private const float BarChangeThreshold = 0.01f;

    // Maximum gap between broadcasts during steady state (HP/role/nickname
    // unchanged). Must stay shorter than PartyState.StaleAfterSec or
    // recipients will mark live peers stale during quiet periods.
    private static readonly TimeSpan BroadcastHeartbeat = TimeSpan.FromSeconds(15);

    private readonly IScreenCapture _capture;
    private readonly BarAnalyzer _analyzer = new();
    private readonly BarSmoother _hpSmoother = new(windowSize: 3);
    private readonly BarSmoother _staminaSmoother = new(windowSize: 3);
    private readonly BarSmoother _manaSmoother = new(windowSize: 3);
    private readonly PartyState _state;
    private readonly RelayClient _net;
    // _cfg is mutable so that nickname / role / poll-interval / calibration
    // changes from the UI propagate into the broadcast loop without
    // recreating the orchestrator. Updated via <see cref="UpdateConfig"/>.
    private AppConfig _cfg;
    private readonly long _joinedAt;
    private CancellationTokenSource? _loopCts;

    private int _tickCounter;

    // Last-broadcast snapshot, for delta detection. _lastBroadcastAtUnix
    // starts at 0 so the very first tick is always heartbeat-due, ensuring
    // peers learn we exist as soon as we join.
    private float? _lastBroadcastHp;
    private float? _lastBroadcastStamina;
    private float? _lastBroadcastMana;
    private string _lastBroadcastNick = "";
    private Role _lastBroadcastRole = default;
    private long _lastBroadcastAtUnix;

    // Self peerId is owned by RelayClient (it may regenerate on duplicate-peer
    // rejection during reconnect — see RelayClient.DuplicatePeerRegenThreshold).
    // We delegate to the live value so every outbound StateMessage / ByeMessage
    // matches the WS-level identity the relay sees, which is what peers'
    // spoofing check compares against.
    public string SelfPeerId => _net.SelfPeerId;
    public PartyState State => _state;

    public PartyOrchestrator(
        AppConfig cfg,
        IScreenCapture capture,
        PartyState state,
        RelayClient net)
    {
        _cfg = cfg;
        _capture = capture;
        _state = state;
        _net = net;
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
        // Computed once at loop start — if the peerId later regenerates, the jitter goes stale, but
        // that only affects timing of broadcasts within a 250 ms window; functional correctness is
        // unaffected.
        int jitter = Math.Abs(_net.SelfPeerId.GetHashCode()) % 250;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                float? hp = await ReadBarAsync(_cfg.HpCalibration, _hpSmoother, ct).ConfigureAwait(false);
                float? stamina = await ReadBarAsync(_cfg.StaminaCalibration, _staminaSmoother, ct).ConfigureAwait(false);
                float? mana = await ReadBarAsync(_cfg.ManaCalibration, _manaSmoother, ct).ConfigureAwait(false);

                LogTick(hp, stamina, mana);

                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

                // Apply our own state locally so our card shows up on our HUD too.
                // This always runs, even when we suppress the network broadcast —
                // local applies are free and keep the self card refreshed.
                _state.Apply(new StateMessage(_net.SelfPeerId, _cfg.Nickname, _cfg.Role, hp, stamina, mana, now), now);

                // Decide whether to actually broadcast to peers. Each WebSocket
                // message costs one relay request inbound here PLUS one per
                // recipient on the fan-out side, so suppressing no-op
                // broadcasts (all bars unchanged within threshold, role/nick same
                // as last sent) compounds with party size. A heartbeat enforces a
                // floor so receivers don't mark us stale during quiet stretches.
                bool barChanged =
                       !ApproxEqual(hp,      _lastBroadcastHp,      BarChangeThreshold)
                    || !ApproxEqual(stamina, _lastBroadcastStamina, BarChangeThreshold)
                    || !ApproxEqual(mana,    _lastBroadcastMana,    BarChangeThreshold);
                bool nickChanged = _cfg.Nickname != _lastBroadcastNick;
                bool roleChanged = _cfg.Role != _lastBroadcastRole;
                bool heartbeatDue = (now - _lastBroadcastAtUnix) >= (long)BroadcastHeartbeat.TotalSeconds;

                if (barChanged || nickChanged || roleChanged || heartbeatDue)
                {
                    var json = MessageJson.Encode(new StateMessage(_net.SelfPeerId, _cfg.Nickname, _cfg.Role, hp, stamina, mana, now));
                    await _net.BroadcastAsync(json).ConfigureAwait(false);
                    _lastBroadcastHp = hp;
                    _lastBroadcastStamina = stamina;
                    _lastBroadcastMana = mana;
                    _lastBroadcastNick = _cfg.Nickname;
                    _lastBroadcastRole = _cfg.Role;
                    _lastBroadcastAtUnix = now;
                }
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

    /// <summary>
    /// Capture, analyze, and smooth a single bar. Returns null if no calibration is
    /// set for this bar (the caller broadcasts null in that field, which receivers
    /// render as "this peer doesn't track that bar").
    /// </summary>
    private async Task<float?> ReadBarAsync(BarCalibration? cal, BarSmoother smoother, CancellationToken ct)
    {
        if (cal is null) return null;
        var bgra = await _capture.CaptureBgraAsync(cal.Region, ct).ConfigureAwait(false);
        float raw = _analyzer.Analyze(bgra, cal.Region.W, cal.Region.H, cal);
        return smoother.Push(raw);
    }

    private void LogTick(float? hp, float? stamina, float? mana)
    {
        _tickCounter++;
        Log.Info(
            $"PartyOrchestrator tick#{_tickCounter}: " +
            $"hp={FormatBar(hp)} stamina={FormatBar(stamina)} mana={FormatBar(mana)}");
    }

    private static string FormatBar(float? value) =>
        value is { } v ? v.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) : "n/a";

    private async Task StaleTickLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            _state.Tick(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            try { await Task.Delay(1000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Two-sided null-tolerant equality for the HP delta check. Both null
    /// counts as equal (no calibration → still no calibration); one null
    /// counts as a change (calibration arrived or disappeared) so we
    /// always broadcast that transition.
    /// </summary>
    private static bool ApproxEqual(float? a, float? b, float threshold)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;
        return Math.Abs(a.Value - b.Value) < threshold;
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
            var bye = MessageJson.Encode(new ByeMessage(_net.SelfPeerId));
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
