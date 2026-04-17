using System;
using System.Threading;
using System.Threading.Tasks;
using GamePartyHud.Network;
using Xunit;

namespace GamePartyHud.Tests.Network;

public class CompositeSignalingTests
{
    private sealed class FakeProvider : ISignalingProvider
    {
        public bool IsJoined { get; private set; }
        public bool ShouldFail { get; set; }

        public event Func<string, string, Task>? OnOffer;
#pragma warning disable CS0067 // Interface requires these events; tests only exercise OnOffer.
        public event Func<string, string, Task>? OnAnswer;
        public event Func<string, string, Task>? OnIce;
#pragma warning restore CS0067

        public Task JoinAsync(string partyId, string selfPeerId, CancellationToken ct)
        {
            if (ShouldFail) throw new InvalidOperationException("boom");
            IsJoined = true;
            return Task.CompletedTask;
        }

        public Task SendOfferAsync(string t, string s, CancellationToken c) => Task.CompletedTask;
        public Task SendAnswerAsync(string t, string s, CancellationToken c) => Task.CompletedTask;
        public Task SendIceAsync(string t, string s, CancellationToken c) => Task.CompletedTask;

        public Task RaiseOfferAsync(string from, string sdp) =>
            OnOffer?.Invoke(from, sdp) ?? Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Join_UsesPrimary_WhenPrimarySucceeds()
    {
        var a = new FakeProvider();
        var b = new FakeProvider();
        var c = new CompositeSignaling(a, b);
        await c.JoinAsync("X7K2P9", "me", CancellationToken.None);
        Assert.True(a.IsJoined);
        Assert.False(b.IsJoined);
    }

    [Fact]
    public async Task Join_FallsBackToSecondary_WhenPrimaryFails()
    {
        var a = new FakeProvider { ShouldFail = true };
        var b = new FakeProvider();
        var c = new CompositeSignaling(a, b);
        await c.JoinAsync("X7K2P9", "me", CancellationToken.None);
        Assert.False(a.IsJoined);
        Assert.True(b.IsJoined);
    }

    [Fact]
    public async Task Join_Throws_WhenBothFail()
    {
        var a = new FakeProvider { ShouldFail = true };
        var b = new FakeProvider { ShouldFail = true };
        var c = new CompositeSignaling(a, b);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => c.JoinAsync("X", "me", CancellationToken.None));
    }

    [Fact]
    public async Task OnOffer_FromEitherProvider_IsForwarded()
    {
        var a = new FakeProvider();
        var b = new FakeProvider();
        var c = new CompositeSignaling(a, b);
        string? seenFrom = null;
        c.OnOffer += (f, _) => { seenFrom = f; return Task.CompletedTask; };
        await a.RaiseOfferAsync("peerX", "sdp");
        Assert.Equal("peerX", seenFrom);
        await b.RaiseOfferAsync("peerY", "sdp");
        Assert.Equal("peerY", seenFrom);
    }
}
