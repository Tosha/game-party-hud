using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GamePartyHud.Diagnostics;
using SIPSorcery.Net;

namespace GamePartyHud.Network;

/// <summary>
/// Manages WebRTC connections to all other party members.
///
/// Uses the WebTorrent discovery pattern: on every tracker announce we supply a batch
/// of pre-generated offers — each with its own <c>offer_id</c> and backed by a fresh
/// <see cref="RTCPeerConnection"/> sitting in <see cref="_pendingOffers"/>. When a
/// peer picks up one of our offers and sends back an answer (with the same offer_id),
/// we promote that pending connection to a fully-identified peer in <see cref="_peers"/>.
/// In the other direction, we accept inbound offers from unknown peers, create an
/// answer, and go straight to <see cref="_peers"/>.
/// </summary>
public sealed class PeerNetwork : IAsyncDisposable
{
    public sealed record TurnCreds(string Url, string? Username, string? Credential);

    /// <summary>How long to wait for ICE gathering before publishing an SDP.</summary>
    private static readonly TimeSpan IceGatheringTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Cap on <see cref="_pendingOffers"/>. The WebTorrent announce protocol caps a
    /// single announce at 20 offers and we emit 5 per minute, so ~2 announces of
    /// buffer is enough for late-arriving answers while bounding the number of
    /// live <see cref="RTCPeerConnection"/>s. Field logs showed the pool growing to
    /// 500+ because no offer was ever being answered — also a memory-leak symptom.
    /// </summary>
    private const int PendingOfferPoolCap = 15;

    private readonly string _selfPeerId;
    private readonly ISignalingProvider _signaling;
    private readonly IReadOnlyList<RTCIceServer> _iceServers;

    // offer_id -> the RTCPeerConnection we generated that offer on (awaiting an answer).
    // Also tracked in insertion order via _pendingOfferOrder so we can evict the oldest
    // when the pool grows past PendingOfferPoolCap.
    private readonly ConcurrentDictionary<string, PendingOffer> _pendingOffers = new();
    private readonly ConcurrentQueue<string> _pendingOfferOrder = new();
    private readonly object _poolEvictionLock = new();

    // remote peer_id -> established peer.
    private readonly ConcurrentDictionary<string, Peer> _peers = new();

    public event Action<string, string>? OnMessage;     // (fromPeerId, json)
    public event Action<string>? OnPeerConnected;        // peerId
    public event Action<string>? OnPeerDisconnected;     // peerId

    public PeerNetwork(string selfPeerId, ISignalingProvider signaling, TurnCreds? turn = null, IReadOnlyList<RTCIceServer>? iceServers = null)
    {
        _selfPeerId = selfPeerId;
        _signaling = signaling;

        if (iceServers is not null)
        {
            _iceServers = iceServers;
        }
        else
        {
            var servers = new List<RTCIceServer>
            {
                new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
            };
            if (turn is { Url.Length: > 0 })
            {
                servers.Add(new RTCIceServer
                {
                    urls = turn.Url,
                    username = turn.Username ?? string.Empty,
                    credential = turn.Credential ?? string.Empty
                });
            }
            _iceServers = servers;
        }

        _signaling.OnOffer  += HandleOfferAsync;
        _signaling.OnAnswer += HandleAnswerAsync;
        _signaling.OnIce    += HandleIceAsync;
        _signaling.OfferFactory = GenerateOffersAsync;
    }

    public Task BroadcastAsync(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        foreach (var p in _peers.Values)
        {
            if (p.Channel?.readyState == RTCDataChannelState.open)
            {
                try { p.Channel.send(bytes); } catch { }
            }
        }
        return Task.CompletedTask;
    }

    // ---------- outbound offers ----------

    /// <summary>
    /// Pre-generate <paramref name="count"/> offers to hand to the signaling layer.
    /// Offers are produced in parallel so the ICE-gathering wait is per-batch, not per-offer.
    /// </summary>
    private async Task<IReadOnlyList<PreGeneratedOffer>> GenerateOffersAsync(int count)
    {
        var tasks = Enumerable.Range(0, count).Select(_ => GenerateOneOfferAsync()).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        var valid = results.Where(r => r is not null).Cast<PreGeneratedOffer>().ToList();
        Log.Info($"PeerNetwork: generated {valid.Count}/{count} pre-offers (pending pool size now {_pendingOffers.Count}).");
        return valid;
    }

    private async Task<PreGeneratedOffer?> GenerateOneOfferAsync()
    {
        try
        {
            var offerId = Guid.NewGuid().ToString("N")[..20];
            var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = new List<RTCIceServer>(_iceServers) });
            InstrumentPeerConnection(pc, offerId);

            // Initiator creates the data channel.
            var channel = await pc.createDataChannel("party").ConfigureAwait(false);
            Log.Info($"PeerNetwork[{offerId}]: opened local data channel 'party' for pre-offer.");

            var offer = pc.createOffer();
            await pc.setLocalDescription(offer).ConfigureAwait(false);

            // Wait for ICE gathering so the published SDP carries all local candidates.
            // Without this the remote peer can't find our network address and ICE
            // connectivity checks never have anything to match against.
            await WaitForIceGatheringAsync(pc, IceGatheringTimeout).ConfigureAwait(false);

            var sdp = pc.localDescription?.sdp?.ToString() ?? offer.sdp;
            Log.Info($"PeerNetwork[{offerId}]: pre-generated offer ready ({sdp.Length}B SDP, gathering={pc.iceGatheringState}).");

            _pendingOffers[offerId] = new PendingOffer(offerId, pc, channel);
            _pendingOfferOrder.Enqueue(offerId);
            EvictOldestPendingOffers();
            return new PreGeneratedOffer(offerId, sdp);
        }
        catch (Exception ex)
        {
            Log.Error("PeerNetwork: failed to pre-generate an offer.", ex);
            return null;
        }
    }

    /// <summary>
    /// Close and remove pending offers from the front of the insertion-order queue
    /// until the live pool size is within <see cref="PendingOfferPoolCap"/>.
    /// </summary>
    private void EvictOldestPendingOffers()
    {
        lock (_poolEvictionLock)
        {
            while (_pendingOffers.Count > PendingOfferPoolCap
                && _pendingOfferOrder.TryDequeue(out var oldestId))
            {
                if (_pendingOffers.TryRemove(oldestId, out var stale))
                {
                    Log.Info($"PeerNetwork: evicting stale pending offer_id={oldestId} (pool was {_pendingOffers.Count + 1}, cap={PendingOfferPoolCap}).");
                    try { stale.Connection.Close("evicted"); } catch { }
                }
            }
        }
    }

    private async Task HandleAnswerAsync(string fromPeerId, string offerId, string sdp)
    {
        if (!_pendingOffers.TryRemove(offerId, out var pending))
        {
            Log.Info($"PeerNetwork: received answer for unknown offer_id={offerId} from {fromPeerId}; dropping.");
            return;
        }

        if (_peers.ContainsKey(fromPeerId))
        {
            Log.Info($"PeerNetwork: already connected to {fromPeerId}, closing duplicate PC from offer_id={offerId}.");
            try { pending.Connection.Close("duplicate"); } catch { }
            return;
        }

        var peer = new Peer(fromPeerId, pending.Connection) { Channel = pending.Channel };
        if (!_peers.TryAdd(fromPeerId, peer))
        {
            try { pending.Connection.Close("duplicate"); } catch { }
            return;
        }

        WireConnection(peer);

        try
        {
            pending.Connection.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.answer,
                sdp = sdp
            });
            Log.Info($"PeerNetwork[{fromPeerId}]: accepted answer, promoted pending offer_id={offerId} to peer.");
        }
        catch (Exception ex)
        {
            Log.Error($"PeerNetwork: failed to set remote description from {fromPeerId}.", ex);
            _peers.TryRemove(fromPeerId, out _);
            try { pending.Connection.Close("failed"); } catch { }
        }

        await Task.CompletedTask;
    }

    // ---------- inbound offers ----------

    private async Task HandleOfferAsync(string fromPeerId, string offerId, string sdp)
    {
        if (_peers.ContainsKey(fromPeerId))
        {
            Log.Info($"PeerNetwork: ignoring offer from {fromPeerId} — already connected.");
            return;
        }
        Log.Info($"PeerNetwork[{fromPeerId}]: accepting inbound offer (offer_id={offerId}, {sdp.Length}B SDP).");

        var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = new List<RTCIceServer>(_iceServers) });
        InstrumentPeerConnection(pc, fromPeerId);
        var peer = new Peer(fromPeerId, pc);

        // As the responder we receive the data channel from the remote side.
        pc.ondatachannel += ch =>
        {
            Log.Info($"PeerNetwork[{fromPeerId}]: remote opened data channel '{ch.label}' (state={ch.readyState}).");
            peer.Channel = ch;
            WirePeerChannel(peer);
        };

        if (!_peers.TryAdd(fromPeerId, peer))
        {
            try { pc.Close("duplicate"); } catch { }
            return;
        }
        WireConnection(peer);

        try
        {
            pc.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.offer,
                sdp = sdp
            });
            var answer = pc.createAnswer();
            await pc.setLocalDescription(answer).ConfigureAwait(false);

            // Same ICE-gathering wait as on the initiator side — the remote peer needs
            // our candidates in the SDP to complete the handshake.
            await WaitForIceGatheringAsync(pc, IceGatheringTimeout).ConfigureAwait(false);

            var answerSdp = pc.localDescription?.sdp?.ToString() ?? answer.sdp;
            await _signaling.SendAnswerAsync(fromPeerId, offerId, answerSdp, CancellationToken.None).ConfigureAwait(false);
            Log.Info($"PeerNetwork[{fromPeerId}]: answered inbound offer (offer_id={offerId}, {answerSdp.Length}B SDP).");
        }
        catch (Exception ex)
        {
            Log.Error($"PeerNetwork: failed to answer offer from {fromPeerId}.", ex);
            _peers.TryRemove(fromPeerId, out _);
            try { pc.Close("failed"); } catch { }
        }
    }

    private Task HandleIceAsync(string fromPeerId, string iceJson)
    {
        if (!_peers.TryGetValue(fromPeerId, out var p)) return Task.CompletedTask;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(iceJson);
            var e = doc.RootElement;
            var init = new RTCIceCandidateInit
            {
                candidate = e.TryGetProperty("candidate", out var c) ? c.GetString() : null,
                sdpMid = e.TryGetProperty("sdpMid", out var m) ? m.GetString() : null,
                sdpMLineIndex = e.TryGetProperty("sdpMLineIndex", out var i) && i.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? (ushort)i.GetInt32()
                    : (ushort)0
            };
            p.Connection.addIceCandidate(init);
        }
        catch (Exception ex)
        {
            Log.Warn($"PeerNetwork: ignoring malformed ICE candidate from {fromPeerId} — {ex.Message}");
        }
        return Task.CompletedTask;
    }

    // ---------- wiring / instrumentation ----------

    private static void InstrumentPeerConnection(RTCPeerConnection pc, string label)
    {
        pc.onicecandidate += c =>
        {
            if (c is null) Log.Info($"PeerNetwork[{label}]: ICE gathering produced all candidates.");
            else Log.Info($"PeerNetwork[{label}]: ICE candidate — {SummarizeCandidate(c.candidate)}");
        };
        pc.oniceconnectionstatechange += s => Log.Info($"PeerNetwork[{label}]: ICE connection state -> {s}");
        pc.onicegatheringstatechange += s => Log.Info($"PeerNetwork[{label}]: ICE gathering state -> {s}");
        pc.onsignalingstatechange += () => Log.Info($"PeerNetwork[{label}]: signaling state -> {pc.signalingState}");
    }

    private static string SummarizeCandidate(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate)) return "<empty>";
        var typIdx = candidate.IndexOf(" typ ", StringComparison.Ordinal);
        if (typIdx >= 0)
        {
            var tail = candidate[(typIdx + 5)..];
            var spaceIdx = tail.IndexOf(' ');
            var typ = spaceIdx > 0 ? tail[..spaceIdx] : tail;
            return $"typ={typ}";
        }
        return candidate.Length > 80 ? candidate[..80] + "…" : candidate;
    }

    /// <summary>
    /// Wait until the peer connection's ICE gathering reaches <see cref="RTCIceGatheringState.complete"/>,
    /// or the timeout elapses — whichever comes first. We publish the SDP either way; a partial set of
    /// candidates is better than none.
    /// </summary>
    private static Task WaitForIceGatheringAsync(RTCPeerConnection pc, TimeSpan timeout)
    {
        if (pc.iceGatheringState == RTCIceGatheringState.complete) return Task.CompletedTask;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStateChange(RTCIceGatheringState state)
        {
            if (state == RTCIceGatheringState.complete) tcs.TrySetResult();
        }
        pc.onicegatheringstatechange += OnStateChange;
        // Re-check in case gathering finished between the initial check and subscription.
        if (pc.iceGatheringState == RTCIceGatheringState.complete) tcs.TrySetResult();

        var timeoutTask = Task.Delay(timeout).ContinueWith(_ => tcs.TrySetResult(), TaskScheduler.Default);
        return tcs.Task.ContinueWith(_ =>
        {
            pc.onicegatheringstatechange -= OnStateChange;
        }, TaskScheduler.Default);
    }

    private void WireConnection(Peer peer)
    {
        peer.Connection.onconnectionstatechange += state =>
        {
            Log.Info($"PeerNetwork[{peer.PeerId}]: connection state -> {state}");
            if (state == RTCPeerConnectionState.connected)
            {
                Log.Info($"PeerNetwork[{peer.PeerId}]: 🟢 peer CONNECTED. Data channel state: {peer.Channel?.readyState}");
                OnPeerConnected?.Invoke(peer.PeerId);
            }
            if (state == RTCPeerConnectionState.disconnected
             || state == RTCPeerConnectionState.failed
             || state == RTCPeerConnectionState.closed)
            {
                Log.Info($"PeerNetwork[{peer.PeerId}]: 🔴 peer terminated (state={state}).");
                OnPeerDisconnected?.Invoke(peer.PeerId);
                _peers.TryRemove(peer.PeerId, out _);
                try { peer.Connection.Close("bye"); } catch { }
            }
        };

        if (peer.Channel is not null) WirePeerChannel(peer);
    }

    private void WirePeerChannel(Peer peer)
    {
        if (peer.Channel is null) return;
        var channel = peer.Channel;
        channel.onopen += () =>
            Log.Info($"PeerNetwork[{peer.PeerId}]: 📡 data channel '{channel.label}' OPEN. Can now send/receive party messages.");
        channel.onclose += () =>
            Log.Info($"PeerNetwork[{peer.PeerId}]: data channel '{channel.label}' closed.");
        channel.onerror += err =>
            Log.Warn($"PeerNetwork[{peer.PeerId}]: data channel error — {err}");
        channel.onmessage += (_, _, data) =>
        {
            var text = Encoding.UTF8.GetString(data);
            OnMessage?.Invoke(peer.PeerId, text);
        };
    }

    // ---------- lifecycle ----------

    public async ValueTask DisposeAsync()
    {
        foreach (var po in _pendingOffers.Values)
        {
            try { po.Connection.Close("dispose"); } catch { }
        }
        _pendingOffers.Clear();
        while (_pendingOfferOrder.TryDequeue(out _)) { }

        foreach (var p in _peers.Values)
        {
            try { p.Connection.Close("dispose"); } catch { }
        }
        _peers.Clear();

        await _signaling.DisposeAsync().ConfigureAwait(false);
    }

    // ---------- types ----------

    private sealed record PendingOffer(string OfferId, RTCPeerConnection Connection, RTCDataChannel Channel);

    private sealed class Peer
    {
        public string PeerId { get; }
        public RTCPeerConnection Connection { get; }
        public RTCDataChannel? Channel { get; set; }
        public Peer(string id, RTCPeerConnection conn)
        {
            PeerId = id;
            Connection = conn;
        }
    }
}
