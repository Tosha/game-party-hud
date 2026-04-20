using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
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
/// Wire format follows the WebTorrent standard as implemented by
/// <see href="https://github.com/webtorrent/bittorrent-tracker">webtorrent/bittorrent-tracker</see>
/// — <c>info_hash</c>, <c>peer_id</c>, <c>to_peer_id</c> travel as 20 raw bytes
/// each, packed into a JSON string as 20 Latin-1 code points. Sending these as
/// 40-char hex (as an earlier revision did) is silently rejected by node-based
/// trackers' length validation; the C++ <c>openwebtorrent-tracker</c> accepts hex
/// as an opaque key but some code paths (we suspect offer forwarding) still
/// misbehave. Internally the rest of the app stays in hex — conversion happens at
/// this wire boundary only.
///
/// Zero hosting cost — relies on existing public trackers. Announces are repeated
/// every 60 s with fresh offers so newly-joining peers keep finding us; sockets
/// that go non-Open (e.g., idle-timed-out by the tracker) are transparently
/// reconnected on the next announce.
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

    // JSON options that keep Latin-1 bytes (0x80..0xFF) escaped as \u00XX rather
    // than emitting multi-byte UTF-8 for them. Trackers read the JSON and care
    // about code-point count (== 20), not byte count.
    private static readonly JsonSerializerOptions WireJson = new()
    {
        Encoder = JavaScriptEncoder.Default
    };

    private readonly string[] _trackers;
    private readonly ClientWebSocket?[] _sockets;
    private CancellationTokenSource? _readLoopCts;
    private Timer? _reAnnounceTimer;

    // Hex identities — what the rest of the app sees.
    private string _partyHashHex = "";
    private string _selfPeerHex = "";

    // Wire identities — 20 Latin-1 code points each (for info_hash and peer_id).
    private string _partyHashBin = "";
    private string _selfPeerBin = "";

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

    public async Task JoinAsync(string partyId, string selfPeerIdHex, CancellationToken ct)
    {
        _partyHashHex = PartyIdToInfohashHex(partyId);
        _partyHashBin = HexToBinary(_partyHashHex);
        _selfPeerHex = selfPeerIdHex;
        _selfPeerBin = HexToBinary(selfPeerIdHex);

        Log.Info($"BitTorrentSignaling: joining party '{partyId}' (infohash={_partyHashHex[..8]}…, self={_selfPeerHex[..8]}…).");

        int opened = 0;
        for (int i = 0; i < _trackers.Length; i++)
        {
            if (await TryOpenSocketAsync(i, ct).ConfigureAwait(false)) opened++;
        }
        if (opened == 0) throw new InvalidOperationException("No BitTorrent trackers reachable.");
        Log.Info($"BitTorrentSignaling: {opened}/{_trackers.Length} trackers available. Re-announce interval = {ReAnnounceInterval.TotalSeconds:F0}s.");

        _readLoopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        for (int i = 0; i < _sockets.Length; i++) StartReadLoopForSlot(i);

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

    /// <summary>Open socket <paramref name="i"/> if it isn't already open. Returns true on success.</summary>
    private async Task<bool> TryOpenSocketAsync(int i, CancellationToken ct)
    {
        var existing = _sockets[i];
        if (existing is { State: WebSocketState.Open }) return true;

        try { existing?.Dispose(); } catch { }
        _sockets[i] = null;

        try
        {
            var ws = new ClientWebSocket();
            // Let the tracker see us idle-pinging the TCP stack; doesn't push WS
            // pings itself but keeps the socket from silently half-dying.
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
            await ws.ConnectAsync(new Uri(_trackers[i]), ct).ConfigureAwait(false);
            _sockets[i] = ws;
            Log.Info($"BitTorrentSignaling: ✓ connected to {_trackers[i]}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"BitTorrentSignaling: ✗ could not connect to {_trackers[i]} — {ex.Message}");
            return false;
        }
    }

    private void StartReadLoopForSlot(int i)
    {
        if (_sockets[i] is not { } s) return;
        var ctsToken = _readLoopCts!.Token;
        _ = Task.Run(() => ReadLoopAsync(s, _trackers[i], i, ctsToken));
    }

    private async Task AnnounceWithOffersAsync(CancellationToken ct)
    {
        // Reopen any socket that dropped since the last announce.
        for (int i = 0; i < _sockets.Length; i++)
        {
            var s = _sockets[i];
            if (s is { State: WebSocketState.Open }) continue;
            Log.Info($"BitTorrentSignaling: {_trackers[i]} is {s?.State.ToString() ?? "null"}; attempting reconnect.");
            if (await TryOpenSocketAsync(i, ct).ConfigureAwait(false)) StartReadLoopForSlot(i);
        }

        IReadOnlyList<PreGeneratedOffer> offers = Array.Empty<PreGeneratedOffer>();
        if (OfferFactory is { } f)
        {
            try { offers = await f.Invoke(OffersPerAnnounce).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn($"BitTorrentSignaling: OfferFactory threw — {ex.Message}"); }
        }

        var msg = JsonSerializer.Serialize(new
        {
            action = "announce",
            info_hash = _partyHashBin,
            peer_id = _selfPeerBin,
            numwant = 20,
            uploaded = 0,
            downloaded = 0,
            left = 0,
            offers = offers.Select(o => new
            {
                offer_id = o.OfferId,
                offer = new { type = "offer", sdp = o.Sdp }
            }).ToArray()
        }, WireJson);

        Log.Info($"BitTorrentSignaling: announce with {offers.Count} pre-generated offers.");
        await BroadcastAsync(msg, ct).ConfigureAwait(false);
    }

    private async Task ReadLoopAsync(ClientWebSocket s, string trackerUri, int slot, CancellationToken ct)
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
                await HandleMessageAsync(text, trackerUri).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warn($"BitTorrentSignaling: read loop for {trackerUri} ended — {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (ReferenceEquals(_sockets[slot], s))
            {
                Log.Info($"BitTorrentSignaling: socket for {trackerUri} is closed (state={s.State}); next announce will reconnect.");
            }
        }
    }

    private async Task HandleMessageAsync(string json, string trackerUri)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Log EVERY tracker message we don't immediately recognize so a future
            // "peers never discover each other" session has a trail to follow.
            if (!root.TryGetProperty("info_hash", out var ih))
            {
                var reason = root.TryGetProperty("failure reason", out var fr) ? fr.GetString() : null;
                if (reason is not null) Log.Warn($"BitTorrentSignaling[{trackerUri}]: tracker failure — {reason}");
                else Log.Info($"BitTorrentSignaling[{trackerUri}]: unhandled tracker message — {Preview(json)}");
                return;
            }
            var inboundHashBin = ih.GetString() ?? "";
            if (inboundHashBin != _partyHashBin)
            {
                Log.Info($"BitTorrentSignaling[{trackerUri}]: dropping message for different info_hash ({BinaryToHex(inboundHashBin)[..Math.Min(8, inboundHashBin.Length * 2)]}…).");
                return;
            }

            // peer_id may be absent (ack) or binary (offer/answer). Convert to hex
            // for every callback consumer — they match against our hex identity.
            var fromBin = root.TryGetProperty("peer_id", out var pid) ? pid.GetString() ?? "" : "";
            if (fromBin == _selfPeerBin) return;
            var fromHex = fromBin.Length > 0 ? BinaryToHex(fromBin) : "";

            var offerIdStr = root.TryGetProperty("offer_id", out var oid) ? oid.GetString() ?? "" : "";

            if (root.TryGetProperty("offer", out var offer))
            {
                var sdp = offer.TryGetProperty("sdp", out var sdpEl) ? sdpEl.GetString() ?? "" : "";
                if (sdp.Length == 0) return;
                Log.Info($"BitTorrentSignaling[{trackerUri}]: ← inbound offer from {ShortHex(fromHex)} (offer_id={ShortId(offerIdStr)}, {sdp.Length}B SDP).");
                if (OnOffer is { } h) await h.Invoke(fromHex, offerIdStr, sdp).ConfigureAwait(false);
            }
            else if (root.TryGetProperty("answer", out var answer))
            {
                var sdp = answer.TryGetProperty("sdp", out var sdpEl) ? sdpEl.GetString() ?? "" : "";
                if (sdp.Length == 0) return;
                Log.Info($"BitTorrentSignaling[{trackerUri}]: ← inbound answer from {ShortHex(fromHex)} (offer_id={ShortId(offerIdStr)}, {sdp.Length}B SDP).");
                if (OnAnswer is { } h) await h.Invoke(fromHex, offerIdStr, sdp).ConfigureAwait(false);
            }
            else
            {
                var complete   = root.TryGetProperty("complete", out var c)   ? c.GetInt32().ToString() : "?";
                var incomplete = root.TryGetProperty("incomplete", out var ic) ? ic.GetInt32().ToString() : "?";
                var interval   = root.TryGetProperty("interval", out var iv)  ? iv.GetInt32().ToString() : "?";
                Log.Info($"BitTorrentSignaling[{trackerUri}]: tracker ack — complete={complete} incomplete={incomplete} interval={interval}s");
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"BitTorrentSignaling[{trackerUri}]: ignoring malformed tracker message — {ex.Message}; raw={Preview(json)}");
        }
    }

    private static string Preview(string s) =>
        s.Length > 160 ? s[..160] + "…" : s;

    private static string ShortHex(string hex) =>
        hex.Length >= 8 ? hex[..8] + "…" : hex;

    private static string ShortId(string id) =>
        id.Length > 8 ? id[..8] + "…" : id;

    public async Task SendAnswerAsync(string toPeerIdHex, string offerId, string sdp, CancellationToken ct)
    {
        var toPeerBin = HexToBinary(toPeerIdHex);
        var msg = JsonSerializer.Serialize(new
        {
            action = "announce",
            info_hash = _partyHashBin,
            peer_id = _selfPeerBin,
            to_peer_id = toPeerBin,
            answer = new { type = "answer", sdp },
            offer_id = offerId
        }, WireJson);
        Log.Info($"BitTorrentSignaling: → answer to {ShortHex(toPeerIdHex)} (offer_id={ShortId(offerId)}).");
        await BroadcastAsync(msg, ct).ConfigureAwait(false);
    }

    // WebTorrent bundles ICE candidates inside the SDP, so we never need to
    // relay them separately for this provider.
    public Task SendIceAsync(string toPeerId, string candidateJson, CancellationToken ct) => Task.CompletedTask;

    private async Task BroadcastAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        for (int i = 0; i < _sockets.Length; i++)
        {
            var s = _sockets[i];
            if (s is null || s.State != WebSocketState.Open) continue;
            try
            {
                await s.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Mark socket as dead; next announce will retry the connect.
                Log.Warn($"BitTorrentSignaling: send to {_trackers[i]} failed — {ex.Message}; dropping socket.");
                try { s.Dispose(); } catch { }
                _sockets[i] = null;
            }
        }
    }

    private static string PartyIdToInfohashHex(string partyId)
    {
        // 20-byte infohash, hex-encoded lower case (callers hold the hex form;
        // the wire-binary form is derived via HexToBinary).
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes("gph:" + partyId));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Convert an even-length hex string to a string of length <c>hex.Length / 2</c>
    /// where each char is the byte value (0x00–0xFF) cast to a char. This matches
    /// Node's <c>Buffer.from(hex, 'hex').toString('binary')</c> — the encoding
    /// WebTorrent trackers expect for <c>info_hash</c> / <c>peer_id</c> /
    /// <c>to_peer_id</c>.
    /// </summary>
    internal static string HexToBinary(string hex)
    {
        if (hex.Length % 2 != 0) throw new ArgumentException("hex string must have even length", nameof(hex));
        var buf = new char[hex.Length / 2];
        for (int i = 0, j = 0; i < hex.Length; i += 2, j++)
        {
            buf[j] = (char)((HexDigit(hex[i]) << 4) | HexDigit(hex[i + 1]));
        }
        return new string(buf);
    }

    /// <summary>Inverse of <see cref="HexToBinary"/>.</summary>
    internal static string BinaryToHex(string binary)
    {
        var sb = new StringBuilder(binary.Length * 2);
        const string digits = "0123456789abcdef";
        foreach (var c in binary)
        {
            int b = c & 0xFF;
            sb.Append(digits[b >> 4]);
            sb.Append(digits[b & 0xF]);
        }
        return sb.ToString();
    }

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => throw new ArgumentException($"not a hex digit: '{c}'")
    };

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
