using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GamePartyHud.Network;

namespace GamePartyHud.Tests.Network;

/// <summary>
/// Process-local signaling hub: stands in for a WebTorrent tracker during tests.
/// Each <see cref="LoopbackProvider"/> attaches to the hub with a peer id; when a
/// provider announces a batch of pre-generated offers, the hub stores them and
/// forwards them to every other attached provider. Answers sent via
/// <see cref="LoopbackProvider.SendAnswerAsync"/> are routed straight to the named
/// recipient.
/// </summary>
internal sealed class LoopbackHub
{
    public ConcurrentDictionary<string, LoopbackProvider> Peers { get; } = new();
    private readonly ConcurrentDictionary<string, List<PreGeneratedOffer>> _offerPool = new();

    /// <summary>A provider just pulled offers from its OfferFactory and wants them published.</summary>
    public void Announce(string fromPeerId, IReadOnlyList<PreGeneratedOffer> offers)
    {
        _offerPool.AddOrUpdate(
            fromPeerId,
            offers.ToList(),
            (_, existing) => { existing.AddRange(offers); return existing; });

        foreach (var (otherId, peer) in Peers)
        {
            if (otherId == fromPeerId) continue;
            foreach (var o in offers)
            {
                // Fire-and-forget: we don't want the announcer's JoinAsync to block
                // on the recipient's answer flow (mirrors real tracker semantics).
                _ = peer.DeliverOfferAsync(fromPeerId, o.OfferId, o.Sdp);
            }
        }
    }

    /// <summary>Replay every offer previously announced by other peers to a newcomer.</summary>
    public void DeliverPoolTo(LoopbackProvider newcomer)
    {
        var newcomerId = newcomer.SelfId!;
        foreach (var (peerId, offers) in _offerPool)
        {
            if (peerId == newcomerId) continue;
            foreach (var o in offers)
            {
                _ = newcomer.DeliverOfferAsync(peerId, o.OfferId, o.Sdp);
            }
        }
    }
}

internal sealed class LoopbackProvider : ISignalingProvider
{
    private readonly LoopbackHub _hub;
    public bool IsJoined { get; private set; }
    public string? SelfId { get; private set; }

    public Func<int, Task<IReadOnlyList<PreGeneratedOffer>>>? OfferFactory { get; set; }

    public event Func<string, string, string, Task>? OnOffer;
    public event Func<string, string, string, Task>? OnAnswer;
    public event Func<string, string, Task>? OnIce;

    public LoopbackProvider(LoopbackHub hub) { _hub = hub; }

    public async Task JoinAsync(string partyId, string selfPeerId, CancellationToken ct)
    {
        SelfId = selfPeerId;
        _hub.Peers[selfPeerId] = this;
        IsJoined = true;

        // Simulate a tracker announce: pull offers from the owning PeerNetwork and
        // distribute them to all currently-joined peers, then replay anything that
        // was announced before we arrived.
        if (OfferFactory is { } f)
        {
            var offers = await f(5).ConfigureAwait(false);
            _hub.Announce(selfPeerId, offers);
        }
        _hub.DeliverPoolTo(this);
    }

    // The Deliver* helpers invoke the subscriber event; errors propagate back to the caller
    // so the test can observe them. Fire-and-forget callers should ignore the returned Task.
    public Task DeliverOfferAsync(string fromPeerId, string offerId, string sdp) =>
        OnOffer?.Invoke(fromPeerId, offerId, sdp) ?? Task.CompletedTask;
    public Task DeliverAnswerAsync(string fromPeerId, string offerId, string sdp) =>
        OnAnswer?.Invoke(fromPeerId, offerId, sdp) ?? Task.CompletedTask;
    public Task DeliverIceAsync(string fromPeerId, string candidateJson) =>
        OnIce?.Invoke(fromPeerId, candidateJson) ?? Task.CompletedTask;

    public Task SendAnswerAsync(string toPeerId, string offerId, string sdp, CancellationToken ct)
    {
        if (_hub.Peers.TryGetValue(toPeerId, out var p))
        {
            _ = p.DeliverAnswerAsync(SelfId!, offerId, sdp);
        }
        return Task.CompletedTask;
    }

    public Task SendIceAsync(string toPeerId, string candidateJson, CancellationToken ct)
    {
        if (_hub.Peers.TryGetValue(toPeerId, out var p))
        {
            _ = p.DeliverIceAsync(SelfId!, candidateJson);
        }
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (SelfId is not null) _hub.Peers.TryRemove(SelfId, out _);
        return ValueTask.CompletedTask;
    }
}
