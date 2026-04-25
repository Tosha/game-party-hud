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
/// </summary>
public sealed class RelayClient : IAsyncDisposable
{
    private readonly string _selfPeerId;
    private readonly Uri _relayWsUri;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _readCts;
    private TaskCompletionSource? _welcomeTcs;

    public bool IsJoined { get; private set; }
    public string SelfPeerId => _selfPeerId;

    public event Action<string>? OnPeerConnected;
    public event Action<string>? OnPeerDisconnected;
    public event Action<string, string>? OnMessage;

    public RelayClient(string selfPeerId, Uri relayWsUri)
    {
        _selfPeerId = selfPeerId;
        _relayWsUri = relayWsUri;
    }

    public async Task JoinAsync(CancellationToken ct)
    {
        Log.Info($"RelayClient: connecting to {_relayWsUri} as {_selfPeerId[..Math.Min(8, _selfPeerId.Length)]}….");

        _ws = new ClientWebSocket();
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await _ws.ConnectAsync(_relayWsUri, ct).ConfigureAwait(false);

        _readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _welcomeTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(() => ReadLoopAsync(_ws, _readCts.Token));

        // Send join.
        var joinFrame = RelayProtocol.EncodeJoin(_selfPeerId);
        await SendTextAsync(joinFrame, ct).ConfigureAwait(false);

        // Await welcome (or the linked CT firing).
        using (ct.Register(() => _welcomeTcs.TrySetCanceled(ct)))
        {
            await _welcomeTcs.Task.ConfigureAwait(false);
        }

        IsJoined = true;
        Log.Info($"RelayClient: joined. Self peer id={_selfPeerId}.");
    }

    private async Task SendTextAsync(string text, CancellationToken ct)
    {
        var ws = _ws ?? throw new InvalidOperationException("WebSocket not connected.");
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                int total = 0;
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(new ArraySegment<byte>(buf, total, buf.Length - total), ct).ConfigureAwait(false);
                    total += r.Count;
                    if (total >= buf.Length) break;
                } while (!r.EndOfMessage);

                if (r.MessageType == WebSocketMessageType.Close) break;
                var text = Encoding.UTF8.GetString(buf, 0, total);
                Dispatch(text);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"RelayClient: read loop ended — {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }

    private void Dispatch(string text)
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
                _welcomeTcs?.TrySetResult();
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
                _welcomeTcs?.TrySetException(new InvalidOperationException($"relay rejected join: {e.Reason}"));
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
