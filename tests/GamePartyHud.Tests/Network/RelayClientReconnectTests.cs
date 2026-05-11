using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using GamePartyHud.Network;
using Xunit;

namespace GamePartyHud.Tests.Network;

/// <summary>
/// Coverage for reconnect resilience in <see cref="RelayClient"/>, specifically
/// the recovery path for the <c>duplicate-peer</c> reconnect loop seen in the
/// field (relay drops the WS, client reconnects with the same peerId, relay
/// hasn't released the old slot yet so it rejects with <c>duplicate-peer</c>,
/// server closes, client reconnects, …). The contract under test:
///   • After <c>DuplicatePeerRegenThreshold</c> consecutive <c>duplicate-peer</c>
///     errors during reconnect, the client regenerates its peerId and uses the
///     new value for the next attempt — so the same client can rejoin even
///     when the relay's previous-peer GC hasn't fired.
///   • A successful welcome resets the counter, so a transient blip doesn't
///     accumulate over a long session.
/// </summary>
public class RelayClientReconnectTests
{
    private const string PeerA = "a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe";
    private static readonly string WelcomeForPeerA =
        $$"""{"type":"welcome","peerId":"{{PeerA}}","members":[]}""";
    private const string DuplicatePeerError =
        """{"type":"error","reason":"duplicate-peer"}""";

    [Fact(Timeout = 30_000)]
    public async Task ReconnectLoop_AfterThreeDuplicatePeerErrors_RegeneratesPeerId()
    {
        await using var server = new FakeRelayServer();
        var client = new RelayClient(
            PeerA,
            new Uri(server.WsUrl),
            connectTimeout: TimeSpan.FromSeconds(3));

        // 1. Clean initial join.
        var joinTask = client.JoinAsync(CancellationToken.None);
        Assert.Contains(PeerA, await server.NextReceivedAsync(TimeSpan.FromSeconds(5)));
        await server.SendFromServerAsync(WelcomeForPeerA);
        await joinTask;
        Assert.Equal(PeerA, client.SelfPeerId);

        // 2. Force a steady-state drop so the client enters its reconnect loop.
        await server.CloseFromServerAsync();

        // 3. The first three reconnect attempts: each sends `join` with PeerA,
        //    we reply `duplicate-peer`. Threshold is hardcoded at 3; the fourth
        //    attempt should use a freshly-generated peerId.
        for (int i = 0; i < 3; i++)
        {
            var join = await server.NextReceivedAsync(TimeSpan.FromSeconds(20));
            Assert.Contains(PeerA, join);
            await server.SendFromServerAsync(DuplicatePeerError);
        }

        // 4. Fourth attempt: new peerId.
        var freshJoin = await server.NextReceivedAsync(TimeSpan.FromSeconds(20));
        Assert.DoesNotContain(PeerA, freshJoin);

        // Validate the new peerId looks like a valid 40-hex-lower id and the
        // public property reflects the change.
        Assert.NotEqual(PeerA, client.SelfPeerId);
        Assert.Matches(new Regex("^[0-9a-f]{40}$"), client.SelfPeerId);

        await client.DisposeAsync();
    }

    [Fact(Timeout = 30_000)]
    public async Task ReconnectLoop_SuccessfulWelcome_ResetsDuplicatePeerCounter()
    {
        await using var server = new FakeRelayServer();
        var client = new RelayClient(
            PeerA,
            new Uri(server.WsUrl),
            connectTimeout: TimeSpan.FromSeconds(3));

        // 1. Initial join.
        var joinTask = client.JoinAsync(CancellationToken.None);
        Assert.Contains(PeerA, await server.NextReceivedAsync(TimeSpan.FromSeconds(5)));
        await server.SendFromServerAsync(WelcomeForPeerA);
        await joinTask;

        // 2. First disconnect — start a reconnect cycle and hit two `duplicate-peer`
        //    errors (below threshold).
        await server.CloseFromServerAsync();
        for (int i = 0; i < 2; i++)
        {
            Assert.Contains(PeerA, await server.NextReceivedAsync(TimeSpan.FromSeconds(20)));
            await server.SendFromServerAsync(DuplicatePeerError);
        }

        // 3. Next attempt succeeds (welcome). This should reset the counter.
        Assert.Contains(PeerA, await server.NextReceivedAsync(TimeSpan.FromSeconds(20)));
        await server.SendFromServerAsync(WelcomeForPeerA);

        // 4. Second disconnect — another two `duplicate-peer` errors. With the
        //    counter reset, total stays below threshold and peerId must NOT
        //    have regenerated yet.
        await server.CloseFromServerAsync();
        for (int i = 0; i < 2; i++)
        {
            Assert.Contains(PeerA, await server.NextReceivedAsync(TimeSpan.FromSeconds(20)));
            await server.SendFromServerAsync(DuplicatePeerError);
        }

        // 5. Sanity: the very next reconnect attempt still uses PeerA (we've
        //    accumulated 2 + 2 = 4 errors total but the welcome in step 3
        //    reset the run to 0). If the counter weren't resetting, this would
        //    already be a fresh peerId.
        var stillPeerA = await server.NextReceivedAsync(TimeSpan.FromSeconds(20));
        Assert.Contains(PeerA, stillPeerA);
        Assert.Equal(PeerA, client.SelfPeerId);

        await client.DisposeAsync();
    }
}
