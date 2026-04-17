using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GamePartyHud.Network;

/// <summary>
/// Signaling via the free PeerJS public cloud (<c>0.peerjs.com</c>). Each peer connects
/// with id <c>"{partyId}-{selfPeerId}"</c>; peers address each other using the same
/// prefix so they only find party mates.
/// </summary>
public sealed class PeerJsSignaling : ISignalingProvider
{
    private const string Endpoint = "wss://0.peerjs.com/peerjs?key=peerjs&id={0}&token=t&version=1.5.0";
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private string _party = "";
    private string _selfFullId = "";
    public bool IsJoined { get; private set; }

    public event Func<string, string, Task>? OnOffer;
    public event Func<string, string, Task>? OnAnswer;
    public event Func<string, string, Task>? OnIce;

    public async Task JoinAsync(string partyId, string selfPeerId, CancellationToken ct)
    {
        _party = partyId;
        _selfFullId = $"{partyId}-{selfPeerId}";
        _ws = new ClientWebSocket();
        var uri = new Uri(string.Format(Endpoint, _selfFullId));
        await _ws.ConnectAsync(uri, ct).ConfigureAwait(false);
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ = Task.Run(() => ReadLoopAsync(_cts.Token));
        IsJoined = true;
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        while (!ct.IsCancellationRequested && _ws!.State == WebSocketState.Open)
        {
            int total = 0;
            ValueWebSocketReceiveResult r;
            do
            {
                r = await _ws.ReceiveAsync(buf.AsMemory(total), ct).ConfigureAwait(false);
                total += r.Count;
                if (total >= buf.Length) break;
            } while (!r.EndOfMessage);

            if (r.MessageType == WebSocketMessageType.Close) break;

            var text = Encoding.UTF8.GetString(buf, 0, total);
            try
            {
                using var doc = JsonDocument.Parse(text);
                var type = doc.RootElement.GetProperty("type").GetString();
                var src  = doc.RootElement.TryGetProperty("src",  out var s) ? s.GetString() ?? "" : "";
                var payload = doc.RootElement.TryGetProperty("payload", out var p) ? p : default;

                string from = src.StartsWith(_party + "-") ? src[(_party.Length + 1)..] : src;

                switch (type)
                {
                    case "OFFER":
                        if (OnOffer is { } ho)  await ho.Invoke(from, payload.GetProperty("sdp").GetString() ?? "").ConfigureAwait(false);
                        break;
                    case "ANSWER":
                        if (OnAnswer is { } ha) await ha.Invoke(from, payload.GetProperty("sdp").GetString() ?? "").ConfigureAwait(false);
                        break;
                    case "CANDIDATE":
                        if (OnIce is { } hi)    await hi.Invoke(from, payload.GetRawText()).ConfigureAwait(false);
                        break;
                }
            }
            catch
            {
                // Ignore malformed signaling messages.
            }
        }
    }

    private Task SendSignalAsync(string type, string toPeerId, object payload, CancellationToken ct)
    {
        var msg = JsonSerializer.Serialize(new
        {
            type,
            src = _selfFullId,
            dst = $"{_party}-{toPeerId}",
            payload
        });
        return _ws!.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, ct);
    }

    public Task SendOfferAsync(string to, string sdp, CancellationToken ct) =>
        SendSignalAsync("OFFER", to, new { type = "offer", sdp }, ct);
    public Task SendAnswerAsync(string to, string sdp, CancellationToken ct) =>
        SendSignalAsync("ANSWER", to, new { type = "answer", sdp }, ct);
    public Task SendIceAsync(string to, string iceJson, CancellationToken ct) =>
        SendSignalAsync("CANDIDATE", to, JsonDocument.Parse(iceJson).RootElement, ct);

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_ws is { State: WebSocketState.Open })
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false); }
            catch { }
        }
        _ws?.Dispose();
    }
}
