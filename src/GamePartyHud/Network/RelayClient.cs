using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GamePartyHud.Diagnostics;

namespace GamePartyHud.Network;

/// <summary>
/// WebSocket client for the relay server in <c>relay/</c>. Replaces the
/// earlier <c>PeerNetwork</c> + <c>BitTorrentSignaling</c> duo: one persistent
/// WebSocket to the relay, protocol frames mapped onto the same three events
/// (<see cref="OnPeerConnected"/>, <see cref="OnPeerDisconnected"/>,
/// <see cref="OnMessage"/>) that <c>PartyOrchestrator</c> already consumes.
///
/// Supports an optional fallback relay URL: on connect (and on reconnect),
/// the primary URL is tried first with a bounded timeout; if it fails, the
/// fallback is tried. This is the client-side leg of the Oracle-bridge story
/// that works around ISPs blocking the Cloudflare CIDR ranges where the
/// primary Worker lives. Reconnect always retries the primary first, so a
/// peer that switched to the bridge during an ISP blip automatically
/// migrates back to Cloudflare once it's reachable again — without a
/// restart.
/// </summary>
public sealed class RelayClient : IAsyncDisposable
{
    /// <summary>
    /// Time budget for one connect+welcome handshake before we give up and try
    /// the next URL. Tuned for the perceptible-pause case: a CIDR-blocked
    /// Cloudflare endpoint typically silently drops SYNs (no RST), so the OS
    /// would otherwise wait ~21 s on Windows before failing. 5 s keeps the
    /// "joining party…" UI freeze short while still tolerating a normal slow
    /// handshake on a flaky connection.
    /// </summary>
    private static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(5);

    private readonly string _selfPeerId;
    private readonly Uri _primaryUri;
    private readonly Uri? _fallbackUri;
    private readonly TimeSpan _connectTimeout;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _readCts;

    public bool IsJoined { get; private set; }
    public string SelfPeerId => _selfPeerId;

    public event Action<string>? OnPeerConnected;
    public event Action<string>? OnPeerDisconnected;
    public event Action<string, string>? OnMessage;

    public RelayClient(
        string selfPeerId,
        Uri primaryUri,
        Uri? fallbackUri = null,
        TimeSpan? connectTimeout = null)
    {
        _selfPeerId = selfPeerId;
        _primaryUri = primaryUri;
        _fallbackUri = fallbackUri;
        _connectTimeout = connectTimeout ?? DefaultConnectTimeout;
    }

    public async Task JoinAsync(CancellationToken ct)
    {
        if (await TryJoinViaAsync(_primaryUri, ct).ConfigureAwait(false))
        {
            IsJoined = true;
            return;
        }

        if (_fallbackUri is not null)
        {
            Log.Info($"RelayClient: primary {_primaryUri} unreachable; falling back to {_fallbackUri}.");
            if (await TryJoinViaAsync(_fallbackUri, ct).ConfigureAwait(false))
            {
                IsJoined = true;
                return;
            }
        }

        var msg = _fallbackUri is null
            ? $"Could not connect to relay at {_primaryUri}."
            : $"Could not connect to relay at primary {_primaryUri} or fallback {_fallbackUri}.";
        throw new InvalidOperationException(msg);
    }

    /// <summary>
    /// Attempts a full join handshake (TCP + WS upgrade + join frame + welcome receipt)
    /// against <paramref name="uri"/> within <see cref="_connectTimeout"/>. Returns
    /// <c>true</c> on success (with <see cref="_ws"/> and <see cref="_readCts"/> populated
    /// and the read loop running). Returns <c>false</c> on any non-cancellation failure,
    /// leaving the client in a clean state ready for the next attempt. Re-throws when
    /// <paramref name="ct"/> fires so callers can propagate caller cancellation cleanly.
    /// </summary>
    private async Task<bool> TryJoinViaAsync(Uri uri, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_connectTimeout);

        ClientWebSocket? ws = null;
        CancellationTokenSource? readCts = null;

        try
        {
            Log.Info($"RelayClient: connecting to {uri} as {_selfPeerId[..Math.Min(8, _selfPeerId.Length)]}…");

            ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            await ws.ConnectAsync(uri, timeoutCts.Token).ConfigureAwait(false);

            readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var welcomeTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Publish to instance fields BEFORE starting the read loop so SendTextAsync
            // (which reads _ws) and BroadcastAsync work. The local `welcomeTcs` is passed
            // explicitly into the read loop so a stale frame from a previously-failed
            // attempt's read loop can never accidentally complete the new attempt's TCS.
            _ws = ws;
            _readCts = readCts;

            var capturedWs = ws;
            var capturedReadCts = readCts;
            _ = Task.Run(() => ReadLoopAsync(capturedWs, welcomeTcs, capturedReadCts.Token));

            var joinFrame = RelayProtocol.EncodeJoin(_selfPeerId);
            await SendTextAsync(joinFrame, timeoutCts.Token).ConfigureAwait(false);

            using (timeoutCts.Token.Register(() => welcomeTcs.TrySetCanceled(timeoutCts.Token)))
            {
                await welcomeTcs.Task.ConfigureAwait(false);
            }

            Log.Info($"RelayClient: connected to {uri}.");
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller cancelled — clean up and propagate.
            await CleanupAttemptAsync(ws, readCts).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn($"RelayClient: connect to {uri} failed — {ex.GetType().Name}: {ex.Message}.");
            await CleanupAttemptAsync(ws, readCts).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// Tear down a half-finished connect attempt so the next URL gets a fresh
    /// slate. Cancelling readCts kicks the read loop out of any pending
    /// ReceiveAsync; closing+disposing the socket releases the kernel handle.
    /// We null out the instance fields only if they still point at <paramref
    /// name="ws"/> / <paramref name="readCts"/>, so a successful subsequent
    /// attempt that already overwrote them isn't accidentally cleared.
    /// </summary>
    private async Task CleanupAttemptAsync(ClientWebSocket? ws, CancellationTokenSource? readCts)
    {
        try { readCts?.Cancel(); } catch { }
        if (ws is { State: WebSocketState.Open })
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "abandoning attempt", CancellationToken.None).ConfigureAwait(false); } catch { }
        }
        try { ws?.Dispose(); } catch { }
        if (ReferenceEquals(_ws, ws)) _ws = null;
        if (ReferenceEquals(_readCts, readCts)) _readCts = null;
    }

    private async Task SendTextAsync(string text, CancellationToken ct)
    {
        var ws = _ws ?? throw new InvalidOperationException("WebSocket not connected.");
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(ClientWebSocket ws, TaskCompletionSource? welcomeTcs, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                int total = 0;
                WebSocketReceiveResult r;
                try
                {
                    do
                    {
                        r = await ws.ReceiveAsync(new ArraySegment<byte>(buf, total, buf.Length - total), ct).ConfigureAwait(false);
                        total += r.Count;
                        if (total >= buf.Length) break;
                    } while (!r.EndOfMessage);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    // If we never finished the join handshake, fail fast so the caller
                    // (TryJoinViaAsync) can fall through to the next URL without waiting
                    // for the full connect timeout to elapse.
                    if (welcomeTcs is { Task.IsCompleted: false })
                    {
                        Log.Warn($"RelayClient: receive error before welcome — {ex.Message}.");
                        welcomeTcs.TrySetException(ex);
                        return;
                    }
                    Log.Warn($"RelayClient: receive error — {ex.Message}; reconnecting.");
                    _ = Task.Run(() => ReconnectLoopAsync(ct));
                    return;
                }

                if (r.MessageType == WebSocketMessageType.Close)
                {
                    try { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "ack", CancellationToken.None).ConfigureAwait(false); } catch { }
                    if (welcomeTcs is { Task.IsCompleted: false })
                    {
                        Log.Info("RelayClient: server closed the socket before welcome.");
                        welcomeTcs.TrySetException(new InvalidOperationException("Server closed the socket before sending welcome."));
                        return;
                    }
                    Log.Info("RelayClient: server closed the socket; reconnecting.");
                    _ = Task.Run(() => ReconnectLoopAsync(ct));
                    return;
                }
                var text = Encoding.UTF8.GetString(buf, 0, total);
                Dispatch(text, welcomeTcs);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    /// <summary>
    /// Reconnect after the server (or network) drops the active connection.
    /// Always tries the primary first so users automatically migrate back to
    /// the Cloudflare path once their ISP unblocks the CIDR — without having
    /// to restart the app. Falls through to the fallback if primary still
    /// fails on this round. Backoff is per round-trip (primary + fallback),
    /// not per URL, so a known-bad primary doesn't stretch the recovery
    /// window.
    /// </summary>
    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        // Monotonic backoff: 500 ms, 1 s, 2 s, 4 s, then cap at 8 s. Resets on success.
        var delays = new[] { 500, 1_000, 2_000, 4_000, 8_000 };
        int attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (await TryJoinViaAsync(_primaryUri, ct).ConfigureAwait(false))
                {
                    Log.Info($"RelayClient: reconnected to primary on attempt #{attempt + 1}.");
                    return;
                }

                if (_fallbackUri is not null && await TryJoinViaAsync(_fallbackUri, ct).ConfigureAwait(false))
                {
                    Log.Info($"RelayClient: reconnected via fallback on attempt #{attempt + 1}.");
                    return;
                }
            }
            catch (OperationCanceledException) { return; }

            var delay = delays[Math.Min(attempt, delays.Length - 1)];
            Log.Warn($"RelayClient: reconnect attempt #{attempt + 1} failed; retrying in {delay} ms.");
            try { await Task.Delay(delay, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            attempt++;
        }
    }

    private void Dispatch(string text, TaskCompletionSource? welcomeTcs)
    {
        var msg = RelayProtocol.DecodeServerMessage(text);
        if (msg is null)
        {
            Log.Warn($"RelayClient: dropping unparseable frame ({text.Length}B).");
            return;
        }

        switch (msg)
        {
            case RelayProtocol.Welcome w:
                Log.Info($"RelayClient: welcome received with {w.Members.Count} existing member(s).");
                foreach (var id in w.Members) OnPeerConnected?.Invoke(id);
                welcomeTcs?.TrySetResult();
                break;
            case RelayProtocol.PeerJoined j:
                Log.Info($"RelayClient: peer-joined {j.PeerId[..Math.Min(8, j.PeerId.Length)]}….");
                OnPeerConnected?.Invoke(j.PeerId);
                break;
            case RelayProtocol.PeerLeft l:
                Log.Info($"RelayClient: peer-left {l.PeerId[..Math.Min(8, l.PeerId.Length)]}….");
                OnPeerDisconnected?.Invoke(l.PeerId);
                break;
            case RelayProtocol.Message m:
                OnMessage?.Invoke(m.FromPeerId, m.Payload);
                break;
            case RelayProtocol.ErrorMessage e:
                Log.Warn($"RelayClient: relay error — {e.Reason}");
                welcomeTcs?.TrySetException(new InvalidOperationException($"relay rejected join: {e.Reason}"));
                break;
        }
    }

    public Task BroadcastAsync(string json)
    {
        if (_ws is not { State: WebSocketState.Open }) return Task.CompletedTask;
        var frame = RelayProtocol.EncodeBroadcast(json);
        return SendTextAsync(frame, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        _readCts?.Cancel();
        if (_ws is { State: WebSocketState.Open } ws)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false); } catch { }
        }
        _ws?.Dispose();
        IsJoined = false;
    }
}
