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

    private readonly string _selfPeerId;
    private readonly ISignalingProvider _signaling;
    private readonly IReadOnlyList<RTCIceServer> _iceServers;

    // offer_id -> the RTCPeerConnection we generated that offer on (awaiting an answer).
    private readonly ConcurrentDictionary<string, PendingOffer> _pendingOffers = new();

    // remote peer_id -> established peer.
    private readonly ConcurrentDictionary<string, Peer> _peers = new();

    public event Action<string, string>? OnMessage;     // (fromPeerId, json)
    public event Action<string>? OnPeerConnected;        // peerId
    public event Action<string>? OnPeerDisconnected;     // peerId

    public PeerNetwork(string selfPeerId, ISignalingProvider signaling, TurnCreds? turn = null)
    {
        _selfPeerId = selfPeerId;
        _signaling = signaling;

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
    /// Each offer has its own RTCPeerConnection that parks in <see cref="_pendingOffers"/>
    /// waiting for an answer from whatever peer the tracker eventually matches us with.
    /// </summary>
    private async Task<IReadOnlyList<PreGeneratedOffer>> GenerateOffersAsync(int count)
    {
        var result = new List<PreGeneratedOffer>(count);
        for (int i = 0; i < count; i++)
        {
            try
            {
                var offerId = Guid.NewGuid().ToString("N")[..20];
                var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = new List<RTCIceServer>(_iceServers) });

                // Instrument pre-identification: we don't know the peer yet, so log
                // everything under the offer_id. Once an answer promotes this PC,
                // the same handlers keep working and log under the peer id instead.
                string label = offerId;
                pc.onicecandidate += c =>
                {
                    if (c is null) Log.Info($"PeerNetwork[{label}]: ICE gathering produced all candidates.");
                    else Log.Info($"PeerNetwork[{label}]: ICE candidate — {SummarizeCandidate(c.candidate)}");
                };
                pc.oniceconnectionstatechange += s => Log.Info($"PeerNetwork[{label}]: ICE connection state -> {s}");
                pc.onicegatheringstatechange += s => Log.Info($"PeerNetwork[{label}]: ICE gathering state -> {s}");
                pc.onsignalingstatechange += () => Log.Info($"PeerNetwork[{label}]: signaling state -> {pc.signalingState}");

                // Initiator creates the data channel.
                var channel = await pc.createDataChannel("party").ConfigureAwait(false);
                Log.Info($"PeerNetwork[{offerId}]: opened local data channel 'party' for pre-offer.");

                // Prepare the offer.
                var offer = pc.createOffer();
                await pc.setLocalDescription(offer).ConfigureAwait(false);
                Log.Info($"PeerNetwork[{offerId}]: pre-generated offer ready ({offer.sdp.Length} bytes of SDP).");

                _pendingOffers[offerId] = new PendingOffer(offerId, pc, channel);
                result.Add(new PreGeneratedOffer(offerId, offer.sdp));
            }
            catch (Exception ex)
            {
                Log.Error("PeerNetwork: failed to pre-generate an offer; skipping.", ex);
            }
        }
        Log.Info($"PeerNetwork: generated {result.Count}/{count} pre-offers (pending pool size now {_pendingOffers.Count}).");
        return result;
    }

    private static string SummarizeCandidate(string? candidate)
    {
        // Candidate string looks like: "candidate:1 1 UDP 2113937151 192.168.1.50 50000 typ host"
        // Extract the type + whether it's local/public for terseness.
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
            // Race: someone else promoted first. Drop this one.
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
            Log.Info($"PeerNetwork: accepted answer, promoted pending offer_id={offerId} to peer {fromPeerId}.");
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
        Log.Info($"PeerNetwork[{fromPeerId}]: accepting inbound offer (offer_id={offerId}, {sdp.Length} bytes of SDP).");

        var pc = new RTCPeerConnection(new RTCConfiguration { iceServers = new List<RTCIceServer>(_iceServers) });
        var peer = new Peer(fromPeerId, pc);

        // Instrument the new PC immediately so we can see ICE progress from here on.
        pc.onicecandidate += c =>
        {
            if (c is null) Log.Info($"PeerNetwork[{fromPeerId}]: ICE gathering produced all candidates.");
            else Log.Info($"PeerNetwork[{fromPeerId}]: ICE candidate — {SummarizeCandidate(c.candidate)}");
        };
        pc.oniceconnectionstatechange += s => Log.Info($"PeerNetwork[{fromPeerId}]: ICE connection state -> {s}");
        pc.onicegatheringstatechange += s => Log.Info($"PeerNetwork[{fromPeerId}]: ICE gathering state -> {s}");
        pc.onsignalingstatechange += () => Log.Info($"PeerNetwork[{fromPeerId}]: signaling state -> {pc.signalingState}");

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
            await _signaling.SendAnswerAsync(fromPeerId, offerId, answer.sdp, CancellationToken.None).ConfigureAwait(false);
            Log.Info($"PeerNetwork: answered inbound offer from {fromPeerId} (offer_id={offerId}).");
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

    // ---------- wiring helpers ----------

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
