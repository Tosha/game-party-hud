using System;
using System.Collections.Concurrent;
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

    public event Func<string, string, Task>? OnOffer;
    public event Func<string, string, Task>? OnAnswer;
    public event Func<string, string, Task>? OnIce;

    public string? SelfId { get; private set; }

    public LoopbackProvider(LoopbackHub hub) { _hub = hub; }

    public Task JoinAsync(string partyId, string selfPeerId, CancellationToken ct)
    {
        SelfId = selfPeerId;
        _hub.Peers[selfPeerId] = this;
        IsJoined = true;
        return Task.CompletedTask;
    }

    public Task SendOfferAsync(string to, string sdp, CancellationToken ct) =>
        _hub.Peers.TryGetValue(to, out var p)
            ? (p.OnOffer?.Invoke(SelfId!, sdp) ?? Task.CompletedTask)
            : Task.CompletedTask;

    public Task SendAnswerAsync(string to, string sdp, CancellationToken ct) =>
        _hub.Peers.TryGetValue(to, out var p)
            ? (p.OnAnswer?.Invoke(SelfId!, sdp) ?? Task.CompletedTask)
            : Task.CompletedTask;

    public Task SendIceAsync(string to, string iceJson, CancellationToken ct) =>
        _hub.Peers.TryGetValue(to, out var p)
            ? (p.OnIce?.Invoke(SelfId!, iceJson) ?? Task.CompletedTask)
            : Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        if (SelfId is not null) _hub.Peers.TryRemove(SelfId, out _);
        return ValueTask.CompletedTask;
    }
}
