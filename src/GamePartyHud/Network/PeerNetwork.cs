using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;

namespace GamePartyHud.Network;

/// <summary>
/// Owns WebRTC connections to every other peer in the party. Uses the injected
/// <see cref="ISignalingProvider"/> only during connection setup; all steady-state
/// traffic is direct peer-to-peer via a single "party" data channel per connection.
/// </summary>
public sealed class PeerNetwork : IAsyncDisposable
{
    public sealed record TurnCreds(string Url, string? Username, string? Credential);

    private readonly string _selfPeerId;
    private readonly ISignalingProvider _signaling;
    private readonly IReadOnlyList<RTCIceServer> _iceServers;
    private readonly ConcurrentDictionary<string, Peer> _peers = new();

    public event Action<string, string>? OnMessage;      // (fromPeerId, json)
    public event Action<string>? OnPeerConnected;         // peerId
    public event Action<string>? OnPeerDisconnected;      // peerId

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
    }

    public async Task ConnectToAsync(string peerId, CancellationToken ct)
    {
        if (_peers.ContainsKey(peerId)) return;
        var peer = await CreatePeerAsync(peerId, isInitiator: true).ConfigureAwait(false);
        var offer = peer.Connection.createOffer();
        await peer.Connection.setLocalDescription(offer).ConfigureAwait(false);
        await _signaling.SendOfferAsync(peerId, offer.sdp, ct).ConfigureAwait(false);
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

    private Task<Peer> CreatePeerAsync(string peerId, bool isInitiator)
    {
        var config = new RTCConfiguration { iceServers = new List<RTCIceServer>(_iceServers) };
        var pc = new RTCPeerConnection(config);
        var peer = new Peer(peerId, pc);
        _peers[peerId] = peer;

        pc.onicecandidate += async c =>
        {
            if (c is null) return;
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                candidate = c.candidate,
                sdpMid = c.sdpMid,
                sdpMLineIndex = c.sdpMLineIndex
            });
            try { await _signaling.SendIceAsync(peerId, json, CancellationToken.None).ConfigureAwait(false); } catch { }
        };
        pc.onconnectionstatechange += state =>
        {
            if (state == RTCPeerConnectionState.connected) OnPeerConnected?.Invoke(peerId);
            if (state == RTCPeerConnectionState.disconnected
             || state == RTCPeerConnectionState.failed
             || state == RTCPeerConnectionState.closed)
            {
                OnPeerDisconnected?.Invoke(peerId);
                _peers.TryRemove(peerId, out _);
                try { pc.Close("bye"); } catch { }
            }
        };

        if (isInitiator)
        {
            return InitiatorAsync(peer);
        }
        pc.ondatachannel += ch => { peer.Channel = ch; WireChannel(peer, peerId); };
        return Task.FromResult(peer);
    }

    private async Task<Peer> InitiatorAsync(Peer peer)
    {
        peer.Channel = await peer.Connection.createDataChannel("party").ConfigureAwait(false);
        WireChannel(peer, peer.PeerId);
        return peer;
    }

    private void WireChannel(Peer peer, string peerId)
    {
        if (peer.Channel is null) return;
        peer.Channel.onmessage += (_, _, data) =>
        {
            var text = Encoding.UTF8.GetString(data);
            OnMessage?.Invoke(peerId, text);
        };
    }

    private async Task HandleOfferAsync(string fromPeerId, string sdp)
    {
        Peer peer;
        if (_peers.TryGetValue(fromPeerId, out var existing))
            peer = existing;
        else
            peer = await CreatePeerAsync(fromPeerId, isInitiator: false).ConfigureAwait(false);

        peer.Connection.setRemoteDescription(new RTCSessionDescriptionInit
        {
            type = RTCSdpType.offer,
            sdp = sdp
        });
        var answer = peer.Connection.createAnswer();
        await peer.Connection.setLocalDescription(answer).ConfigureAwait(false);
        await _signaling.SendAnswerAsync(fromPeerId, answer.sdp, CancellationToken.None).ConfigureAwait(false);
    }

    private Task HandleAnswerAsync(string fromPeerId, string sdp)
    {
        if (_peers.TryGetValue(fromPeerId, out var p))
        {
            p.Connection.setRemoteDescription(new RTCSessionDescriptionInit
            {
                type = RTCSdpType.answer,
                sdp = sdp
            });
        }
        return Task.CompletedTask;
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
                candidate = e.GetProperty("candidate").GetString(),
                sdpMid = e.TryGetProperty("sdpMid", out var m) ? m.GetString() : null,
                sdpMLineIndex = e.TryGetProperty("sdpMLineIndex", out var i) && i.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? (ushort)i.GetInt32()
                    : (ushort)0
            };
            p.Connection.addIceCandidate(init);
        }
        catch
        {
            // Ignore malformed candidate payloads.
        }
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _peers.Values)
        {
            try { p.Connection.Close("dispose"); } catch { }
        }
        _peers.Clear();
        await _signaling.DisposeAsync().ConfigureAwait(false);
    }

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
