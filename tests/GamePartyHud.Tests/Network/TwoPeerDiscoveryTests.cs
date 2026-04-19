using System;
using System.Threading;
using System.Threading.Tasks;
using GamePartyHud.Network;
using GamePartyHud.Party;
using SIPSorcery.Net;
using Xunit;

namespace GamePartyHud.Tests.Network;

/// <summary>
/// End-to-end integration test that stands up two <see cref="PeerNetwork"/> instances
/// and drives them through the full WebTorrent-style discovery flow: announce pre-generated
/// offers via a loopback tracker, exchange answers, complete ICE negotiation on localhost,
/// open the data channel, and round-trip a <see cref="StateMessage"/>.
///
/// Regression guard for the field bug where two players joined a party and never saw each
/// other because the signaling layer subscribed them but nobody sent offers.
/// </summary>
public class TwoPeerDiscoveryTests
{
    /// <summary>
    /// Host-only ICE configuration: no STUN, no TURN. Keeps the test fully offline and
    /// avoids ~1 s of STUN roundtrip for srflx candidates that aren't useful on loopback.
    /// </summary>
    private static readonly System.Collections.Generic.IReadOnlyList<RTCIceServer> LocalhostOnlyIce =
        Array.Empty<RTCIceServer>();

    [Fact(Timeout = 60_000)]
    public async Task TwoPeers_DiscoverAndExchangeStateMessage()
    {
        var hub = new LoopbackHub();
        var sigA = new LoopbackProvider(hub);
        var sigB = new LoopbackProvider(hub);

        var netA = new PeerNetwork("alice-peer-id-abc", sigA, iceServers: LocalhostOnlyIce);
        var netB = new PeerNetwork("bob-peer-id-xyz",   sigB, iceServers: LocalhostOnlyIce);

        // Arrange: wait for "peer is fully connected" from each side's perspective.
        var aSeesB = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bSeesA = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        netA.OnPeerConnected += id => aSeesB.TrySetResult(id);
        netB.OnPeerConnected += id => bSeesA.TrySetResult(id);

        var bReceived = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        netB.OnMessage += (_, text) => bReceived.TrySetResult(text);

        // Act 1: both peers join the party. LoopbackHub's Announce semantics forward each
        // peer's pre-generated offers to the other; pending RTCPeerConnections negotiate
        // answers and open a data channel.
        await sigA.JoinAsync("test-party", "alice-peer-id-abc", default);
        await sigB.JoinAsync("test-party", "bob-peer-id-xyz",   default);

        // Assert 1: both sides observe a connected peer.
        var connectedOnA = await aSeesB.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var connectedOnB = await bSeesA.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("bob-peer-id-xyz",   connectedOnA);
        Assert.Equal("alice-peer-id-abc", connectedOnB);

        // Act 2: A broadcasts a StateMessage over the open data channel.
        // The channel may be in 'connecting' for one tick after OnPeerConnected fires;
        // BroadcastAsync silently drops sends on an unopened channel, so retry until B
        // actually observes the payload — or the timeout elapses.
        var outgoing = MessageJson.Encode(new StateMessage(
            PeerId: "alice-peer-id-abc",
            Nick:   "Alice",
            Role:   Role.Tank,
            Hp:     0.73f,
            T:      1_713_200_000));

        var incoming = await RetryBroadcastUntilReceivedAsync(
            netA, outgoing, bReceived.Task, TimeSpan.FromSeconds(10));

        // Assert 2: B received the exact bytes A sent and can decode them back.
        Assert.Equal(outgoing, incoming);
        var decoded = MessageJson.Decode(incoming) as StateMessage;
        Assert.NotNull(decoded);
        Assert.Equal("alice-peer-id-abc", decoded!.PeerId);
        Assert.Equal("Alice",             decoded.Nick);
        Assert.Equal(Role.Tank,           decoded.Role);
        Assert.Equal(0.73f,               decoded.Hp);

        await netA.DisposeAsync();
        await netB.DisposeAsync();
    }

    /// <summary>
    /// Keep broadcasting <paramref name="msg"/> on <paramref name="net"/> every 250 ms
    /// until <paramref name="received"/> completes or the overall <paramref name="timeout"/>
    /// elapses. Covers the case where OnPeerConnected fires a tick before the data
    /// channel's onopen, so the first broadcast would otherwise be silently dropped.
    /// </summary>
    private static async Task<string> RetryBroadcastUntilReceivedAsync(
        PeerNetwork net, string msg, Task<string> received, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            await net.BroadcastAsync(msg);
            var delay = Task.Delay(250);
            var completed = await Task.WhenAny(received, delay);
            if (completed == received) return await received;
        }
        throw new TimeoutException("peer never received the broadcast within the timeout.");
    }
}
