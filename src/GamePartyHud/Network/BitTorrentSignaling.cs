using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GamePartyHud.Diagnostics;

namespace GamePartyHud.Network;

/// <summary>
/// Peer discovery via public WebTorrent WSS trackers. Implements the full tracker
/// protocol: each announce carries a batch of pre-generated WebRTC offers; the
/// tracker picks other peers in the same infohash room and forwards those offers
/// to them; recipients answer with the original offer_id so the originator can
/// match answers back to pending offers.
///
/// Zero hosting cost — relies on existing public trackers. Announces are repeated
/// every 60 s with fresh offers so newly-joining peers keep finding us.
/// </summary>
public sealed class BitTorrentSignaling : ISignalingProvider
{
    private static readonly string[] DefaultTrackers =
    {
        "wss://tracker.openwebtorrent.com",
        "wss://tracker.btorrent.xyz",
        "wss://tracker.webtorrent.dev"
    };

    private const int OffersPerAnnounce = 5;
    private static readonly TimeSpan ReAnnounceInterval = TimeSpan.FromSeconds(60);

    private readonly string[] _trackers;
    private readonly ClientWebSocket?[] _sockets;
    private CancellationTokenSource? _readLoopCts;
    private Timer? _reAnnounceTimer;
    private string _partyHash = "";
    private string _selfPeer = "";

    public bool IsJoined { get; private set; }

    public Func<int, Task<IReadOnlyList<PreGeneratedOffer>>>? OfferFactory { get; set; }

    public event Func<string, string, string, Task>? OnOffer;
    public event Func<string, string, string, Task>? OnAnswer;
#pragma warning disable CS0067 // WebTorrent bundles ICE candidates inside the SDP — this event is never raised for this provider.
    public event Func<string, string, Task>? OnIce;
#pragma warning restore CS0067

    public BitTorrentSignaling(string[]? trackers = null)
    {
        _trackers = trackers ?? DefaultTrackers;
        _sockets = new ClientWebSocket?[_trackers.Length];
    }

    public async Task JoinAsync(string partyId, string selfPeerId, CancellationToken ct)
    {
        _partyHash = PartyIdToInfohash(partyId);
        _selfPeer = selfPeerId;

        Log.Info($"BitTorrentSignaling: joining party '{partyId}' (infohash={_partyHash[..8]}…, self={selfPeerId[..8]}…).");

        int opened = 0;
        for (int i = 0; i < _trackers.Length; i++)
        {
            try
            {
                var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri(_trackers[i]), ct).ConfigureAwait(false);
                _sockets[i] = ws;
                opened++;
                Log.Info($"BitTorrentSignaling: ✓ connected to {_trackers[i]}");
            }
            catch (Exception ex)
            {
                Log.Warn($"BitTorrentSignaling: ✗ could not connect to {_trackers[i]} — {ex.Message}");
            }
        }
        if (opened == 0) throw new InvalidOperationException("No BitTorrent trackers reachable.");
        Log.Info($"BitTorrentSignaling: {opened}/{_trackers.Length} trackers available. Re-announce interval = {ReAnnounceInterval.TotalSeconds:F0}s.");

        _readLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        for (int i = 0; i < _sockets.Length; i++)
        {
            if (_sockets[i] is not { } s) continue;
            _ = Task.Run(() => ReadLoopAsync(s, _readLoopCts.Token));
        }

        // Initial announce with offers, and schedule periodic re-announce so we
        // stay listed and keep fresh offers available for late joiners.
        await AnnounceWithOffersAsync(_readLoopCts.Token).ConfigureAwait(false);
        _reAnnounceTimer = new Timer(_ =>
        {
            _ = Task.Run(async () =>
            {
                try { await AnnounceWithOffersAsync(_readLoopCts.Token).ConfigureAwait(false); }
                catch (Exception ex) { Log.Warn($"BitTorrentSignaling: re-announce failed — {ex.Message}"); }
            });
        }, null, ReAnnounceInterval, ReAnnounceInterval);

        IsJoined = true;
    }

    private async Task AnnounceWithOffersAsync(CancellationToken ct)
    {
        IReadOnlyList<PreGeneratedOffer> offers = Array.Empty<PreGeneratedOffer>();
        if (OfferFactory is { } f)
        {
            try { offers = await f.Invoke(OffersPerAnnounce).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn($"BitTorrentSignaling: OfferFactory threw — {ex.Message}"); }
        }

        var msg = JsonSerializer.Serialize(new
        {
            action = "announce",
            info_hash = _partyHash,
            peer_id = _selfPeer,
            numwant = 20,
            uploaded = 0,
            downloaded = 0,
            left = 0,
            offers = offers.Select(o => new
            {
                offer_id = o.OfferId,
                offer = new { type = "offer", sdp = o.Sdp }
            }).ToArray()
        });

        Log.Info($"BitTorrentSignaling: announce with {offers.Count} pre-generated offers.");
        await BroadcastAsync(msg, ct).ConfigureAwait(false);
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
        catch (Exception ex)
        {
            Log.Warn($"BitTorrentSignaling: read loop ended — {ex.Message}");
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

            // Messages with no info_hash are tracker-level pings/errors; log lightly so we can
            // see them without being overwhelmed.
            if (!root.TryGetProperty("info_hash", out var ih))
            {
                if (root.TryGetProperty("failure reason", out var fr))
                {
                    Log.Warn($"BitTorrentSignaling: tracker failure — {fr.GetString()}");
                }
                return;
            }
            if (ih.GetString() != _partyHash) return;

            var from = root.TryGetProperty("peer_id", out var pid) ? pid.GetString() ?? "" : "";
            if (from == _selfPeer) return;

            var offerId = root.TryGetProperty("offer_id", out var oid) ? oid.GetString() ?? "" : "";

            if (root.TryGetProperty("offer", out var offer))
            {
                var sdp = offer.TryGetProperty("sdp", out var sdpEl) ? sdpEl.GetString() ?? "" : "";
                if (sdp.Length == 0) return;
                Log.Info($"BitTorrentSignaling: ← inbound offer from {ShortId(from)} (offer_id={offerId[..Math.Min(8, offerId.Length)]}…, {sdp.Length}B SDP).");
                if (OnOffer is { } h) await h.Invoke(from, offerId, sdp).ConfigureAwait(false);
            }
            else if (root.TryGetProperty("answer", out var answer))
            {
                var sdp = answer.TryGetProperty("sdp", out var sdpEl) ? sdpEl.GetString() ?? "" : "";
                if (sdp.Length == 0) return;
                Log.Info($"BitTorrentSignaling: ← inbound answer from {ShortId(from)} (offer_id={offerId[..Math.Min(8, offerId.Length)]}…, {sdp.Length}B SDP).");
                if (OnAnswer is { } h) await h.Invoke(from, offerId, sdp).ConfigureAwait(false);
            }
            else
            {
                // Tracker acknowledgment / peer-count status message — valuable to confirm we're
                // reaching the tracker even when no matches are happening.
                var complete   = root.TryGetProperty("complete", out var c)   ? c.GetInt32().ToString() : "?";
                var incomplete = root.TryGetProperty("incomplete", out var ic) ? ic.GetInt32().ToString() : "?";
                var interval   = root.TryGetProperty("interval", out var iv)  ? iv.GetInt32().ToString() : "?";
                Log.Info($"BitTorrentSignaling: tracker ack — complete={complete} incomplete={incomplete} interval={interval}s");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"BitTorrentSignaling: ignoring malformed tracker message — {ex.Message}");
        }
    }

    private static string ShortId(string id) =>
        id.Length > 8 ? id[..8] + "…" : id;

    public async Task SendAnswerAsync(string toPeerId, string offerId, string sdp, CancellationToken ct)
    {
        var msg = JsonSerializer.Serialize(new
        {
            action = "announce",
            info_hash = _partyHash,
            peer_id = _selfPeer,
            to_peer_id = toPeerId,
            answer = new { type = "answer", sdp },
            offer_id = offerId
        });
        Log.Info($"BitTorrentSignaling: sending answer to {toPeerId} (offer_id={offerId}).");
        await BroadcastAsync(msg, ct).ConfigureAwait(false);
    }

    // WebTorrent bundles ICE candidates inside the SDP, so we never need to
    // relay them separately for this provider.
    public Task SendIceAsync(string toPeerId, string candidateJson, CancellationToken ct) => Task.CompletedTask;

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
        // 20-byte infohash, hex-encoded lower case.
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes("gph:" + partyId));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async ValueTask DisposeAsync()
    {
        _reAnnounceTimer?.Dispose();
        _readLoopCts?.Cancel();
        foreach (var s in _sockets)
        {
            if (s is null) continue;
            try { await s.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false); }
            catch { }
            s.Dispose();
        }
        IsJoined = false;
    }
}
