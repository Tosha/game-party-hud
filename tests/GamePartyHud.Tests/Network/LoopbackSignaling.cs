using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GamePartyHud.Network;

namespace GamePartyHud.Tests.Network;

/// <summary>Process-local signaling hub: routes messages directly between peers by id.</summary>
internal sealed class LoopbackHub
{
    public ConcurrentDictionary<string, LoopbackProvider> Peers { get; } = new();
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

    public Task JoinAsync(string partyId, string selfPeerId, CancellationToken ct)
    {
        SelfId = selfPeerId;
        _hub.Peers[selfPeerId] = this;
        IsJoined = true;
        return Task.CompletedTask;
    }

    /// <summary>Inject an offer as if it had come in from <paramref name="fromPeerId"/>.</summary>
    public Task DeliverOfferAsync(string fromPeerId, string offerId, string sdp) =>
        OnOffer?.Invoke(fromPeerId, offerId, sdp) ?? Task.CompletedTask;

    /// <summary>Inject an answer as if it had come in from <paramref name="fromPeerId"/>.</summary>
    public Task DeliverAnswerAsync(string fromPeerId, string offerId, string sdp) =>
        OnAnswer?.Invoke(fromPeerId, offerId, sdp) ?? Task.CompletedTask;

    /// <summary>Inject an ICE candidate as if it had come in from <paramref name="fromPeerId"/>.</summary>
    public Task DeliverIceAsync(string fromPeerId, string candidateJson) =>
        OnIce?.Invoke(fromPeerId, candidateJson) ?? Task.CompletedTask;

    public Task SendAnswerAsync(string toPeerId, string offerId, string sdp, CancellationToken ct) =>
        _hub.Peers.TryGetValue(toPeerId, out var p)
            ? p.DeliverAnswerAsync(SelfId!, offerId, sdp)
            : Task.CompletedTask;

    public Task SendIceAsync(string toPeerId, string candidateJson, CancellationToken ct) =>
        _hub.Peers.TryGetValue(toPeerId, out var p)
            ? p.DeliverIceAsync(SelfId!, candidateJson)
            : Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        if (SelfId is not null) _hub.Peers.TryRemove(SelfId, out _);
        return ValueTask.CompletedTask;
    }
}
