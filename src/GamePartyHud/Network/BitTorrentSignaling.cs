using System;
using System.Buffers;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GamePartyHud.Network;

/// <summary>
/// Signaling via public BitTorrent WSS trackers (WebTorrent-compatible). The party ID is
/// hashed into a 20-byte infohash that peers announce; the tracker forwards handshake
/// blobs between matching peers. Zero hosting cost.
/// </summary>
/// <remarks>
/// Trackers are best-effort: we open every configured tracker concurrently and use the
/// first one that responds. If a tracker closes, the other open ones keep working.
/// Protocol is a simplified WebTorrent announce — may need tuning once exercised against
/// real trackers during M6 three-machine verification.
/// </remarks>
public sealed class BitTorrentSignaling : ISignalingProvider
{
    private static readonly string[] DefaultTrackers =
    {
        "wss://tracker.openwebtorrent.com",
        "wss://tracker.btorrent.xyz",
        "wss://tracker.webtorrent.io"
    };

    private readonly string[] _trackers;
    private readonly ClientWebSocket[] _sockets;
    private CancellationTokenSource? _readLoopCts;
    private string _partyHash = "";
    private string _selfPeer = "";

    public bool IsJoined { get; private set; }

    public event Func<string, string, Task>? OnOffer;
    public event Func<string, string, Task>? OnAnswer;
#pragma warning disable CS0067 // WebTorrent tracker protocol bundles ICE inside SDP; event declared for interface compliance.
    public event Func<string, string, Task>? OnIce;
#pragma warning restore CS0067

    public BitTorrentSignaling(string[]? trackers = null)
    {
        _trackers = trackers ?? DefaultTrackers;
        _sockets = new ClientWebSocket[_trackers.Length];
    }

    public async Task JoinAsync(string partyId, string selfPeerId, CancellationToken ct)
    {
        _partyHash = PartyIdToInfohash(partyId);
        _selfPeer = selfPeerId;

        int opened = 0;
        for (int i = 0; i < _trackers.Length; i++)
        {
            try
            {
                var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(_trackers[i]), ct).ConfigureAwait(false);
                _sockets[i] = ws;
                opened++;
            }
            catch
            {
                // Move on to the next tracker; we only need one.
            }
        }
        if (opened == 0) throw new InvalidOperationException("No trackers reachable.");

        _readLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        for (int i = 0; i < _sockets.Length; i++)
        {
            if (_sockets[i] is not { } s) continue;
            _ = Task.Run(() => ReadLoopAsync(s, _readLoopCts.Token));
            await AnnounceAsync(s, _readLoopCts.Token).ConfigureAwait(false);
        }
        IsJoined = true;
    }

    private async Task AnnounceAsync(ClientWebSocket s, CancellationToken ct)
    {
        var msg = JsonSerializer.Serialize(new
        {
            action = "announce",
            info_hash = _partyHash,
            peer_id = _selfPeer,
            numwant = 20,
            uploaded = 0,
            downloaded = 0,
            left = 0,
            offers = Array.Empty<object>()
        });
        await s.SendAsync(Encoding.UTF8.GetBytes(msg), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(ClientWebSocket s, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (!ct.IsCancellationRequested && s.State == WebSocketState.Open)
            {
                int total = 0;
                ValueWebSocketReceiveResult r;
                do
                {
                    r = await s.ReceiveAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
                    total += r.Count;
                    if (total >= buffer.Length) break;
                } while (!r.EndOfMessage);

                if (r.MessageType == WebSocketMessageType.Close) break;
                var text = Encoding.UTF8.GetString(buffer, 0, total);
                await HandleMessageAsync(text).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            // One tracker dropping is fine — others may still be serving.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task HandleMessageAsync(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("info_hash", out var ih) || ih.GetString() != _partyHash) return;

            var from = root.TryGetProperty("peer_id", out var pid) ? pid.GetString() ?? "" : "";
            if (from == _selfPeer || from.Length == 0) return;

            if (root.TryGetProperty("offer", out var offer))
            {
                var sdp = offer.GetProperty("sdp").GetString() ?? "";
                if (OnOffer is { } h) await h.Invoke(from, sdp).ConfigureAwait(false);
            }
            else if (root.TryGetProperty("answer", out var answer))
            {
                var sdp = answer.GetProperty("sdp").GetString() ?? "";
                if (OnAnswer is { } h) await h.Invoke(from, sdp).ConfigureAwait(false);
            }
        }
        catch
        {
            // Ignore malformed tracker messages.
        }
    }

    public async Task SendOfferAsync(string toPeerId, string sdp, CancellationToken ct)
    {
        var msg = JsonSerializer.Serialize(new
        {
            action = "announce",
            info_hash = _partyHash,
            peer_id = _selfPeer,
            to_peer_id = toPeerId,
            offer = new { type = "offer", sdp },
            offer_id = Guid.NewGuid().ToString("N")[..20]
        });
        await BroadcastAsync(msg, ct).ConfigureAwait(false);
    }

    public async Task SendAnswerAsync(string toPeerId, string sdp, CancellationToken ct)
    {
        var msg = JsonSerializer.Serialize(new
        {
            action = "announce",
            info_hash = _partyHash,
            peer_id = _selfPeer,
            to_peer_id = toPeerId,
            answer = new { type = "answer", sdp }
        });
        await BroadcastAsync(msg, ct).ConfigureAwait(false);
    }

    public Task SendIceAsync(string toPeerId, string candidateJson, CancellationToken ct) =>
        Task.CompletedTask; // WebTorrent tracker protocol bundles ICE inside SDP.

    private async Task BroadcastAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        foreach (var s in _sockets)
        {
            if (s is null || s.State != WebSocketState.Open) continue;
            try { await s.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
            catch { /* one-tracker failure is fine */ }
        }
    }

    private static string PartyIdToInfohash(string partyId)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes("gph:" + partyId));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async ValueTask DisposeAsync()
    {
        _readLoopCts?.Cancel();
        foreach (var s in _sockets)
        {
            if (s is null) continue;
            try { await s.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false); }
            catch { }
            s.Dispose();
        }
    }
}
