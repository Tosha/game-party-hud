using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using GamePartyHud.Network;
using Xunit;

namespace GamePartyHud.Tests.Network;

/// <summary>
/// Coverage for the primary/fallback URL handling added to <see cref="RelayClient"/>
/// to route around ISPs that block the Cloudflare CIDR ranges where the primary
/// relay Worker lives. The contract under test:
///   • If primary connects, fallback is never touched.
///   • If primary refuses or times out, the fallback URL is tried within the same
///     <c>JoinAsync</c> call.
///   • If both fail (or no fallback is configured and the only URL fails),
///     <c>JoinAsync</c> throws.
/// </summary>
public class RelayClientFallbackTests
{
    private const string PeerA = "a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe";
    private static readonly string WelcomeFrame =
        $$"""{"type":"welcome","peerId":"{{PeerA}}","members":[]}""";
    private static readonly string ExpectedJoinFrame =
        $$"""{"type":"join","peerId":"{{PeerA}}"}""";

    [Fact(Timeout = 10_000)]
    public async Task JoinAsync_PrimaryReachable_NeverContactsFallback()
    {
        await using var primary = new FakeRelayServer();
        await using var fallback = new FakeRelayServer();

        var client = new RelayClient(
            PeerA,
            new Uri(primary.WsUrl),
            new Uri(fallback.WsUrl),
            connectTimeout: TimeSpan.FromSeconds(3));

        var joinTask = client.JoinAsync(CancellationToken.None);
        Assert.Equal(ExpectedJoinFrame, await primary.NextReceivedAsync(TimeSpan.FromSeconds(5)));
        await primary.SendFromServerAsync(WelcomeFrame);
        await joinTask;

        Assert.True(client.IsJoined);

        // Fallback should never have received a frame. We can't directly observe
        // "no connection" but we can assert no frames were enqueued — since
        // RelayClient sends `join` immediately after the WS upgrade, absence of a
        // frame proves we never opened the WS to the fallback.
        await Assert.ThrowsAsync<TimeoutException>(
            () => fallback.NextReceivedAsync(TimeSpan.FromMilliseconds(500)));

        await client.DisposeAsync();
    }

    [Fact(Timeout = 10_000)]
    public async Task JoinAsync_PrimaryRefused_FallsBackToSecondary()
    {
        // Closed port: TcpListener bound, then stopped — connect attempts get
        // ConnectionRefused immediately, the cheapest possible primary failure.
        int closedPort = FindFreePort();
        await using var fallback = new FakeRelayServer();

        var client = new RelayClient(
            PeerA,
            new Uri($"ws://localhost:{closedPort}/party/TEST"),
            new Uri(fallback.WsUrl),
            connectTimeout: TimeSpan.FromSeconds(2));

        var joinTask = client.JoinAsync(CancellationToken.None);
        Assert.Equal(ExpectedJoinFrame, await fallback.NextReceivedAsync(TimeSpan.FromSeconds(5)));
        await fallback.SendFromServerAsync(WelcomeFrame);
        await joinTask;

        Assert.True(client.IsJoined);

        await client.DisposeAsync();
    }

    [Fact(Timeout = 10_000)]
    public async Task JoinAsync_PrimarySilent_TimesOutAndFallsBack()
    {
        // Silent server: accepts the WS upgrade but never replies to `join`.
        // Simulates a Cloudflare endpoint that's reachable at the TCP layer but
        // whose Worker is misconfigured / hung — the welcome never arrives, so
        // we must time out instead of hanging forever.
        await using var silentPrimary = new FakeRelayServer();
        await using var fallback = new FakeRelayServer();

        var client = new RelayClient(
            PeerA,
            new Uri(silentPrimary.WsUrl),
            new Uri(fallback.WsUrl),
            connectTimeout: TimeSpan.FromMilliseconds(500));

        var joinTask = client.JoinAsync(CancellationToken.None);

        // Primary receives the join frame but never sends welcome.
        Assert.Equal(ExpectedJoinFrame, await silentPrimary.NextReceivedAsync(TimeSpan.FromSeconds(2)));

        // After ~500 ms the client should have given up on primary and switched
        // to fallback. Use a generous receive window to absorb scheduler jitter.
        Assert.Equal(ExpectedJoinFrame, await fallback.NextReceivedAsync(TimeSpan.FromSeconds(5)));
        await fallback.SendFromServerAsync(WelcomeFrame);
        await joinTask;

        Assert.True(client.IsJoined);

        await client.DisposeAsync();
    }

    [Fact(Timeout = 10_000)]
    public async Task JoinAsync_BothFail_Throws()
    {
        int closedPort1 = FindFreePort();
        int closedPort2 = FindFreePort();

        var client = new RelayClient(
            PeerA,
            new Uri($"ws://localhost:{closedPort1}/party/TEST"),
            new Uri($"ws://localhost:{closedPort2}/party/TEST"),
            connectTimeout: TimeSpan.FromSeconds(2));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.JoinAsync(CancellationToken.None));
        Assert.Contains("primary", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fallback", ex.Message, StringComparison.OrdinalIgnoreCase);

        await client.DisposeAsync();
    }

    [Fact(Timeout = 10_000)]
    public async Task JoinAsync_NoFallbackPrimaryFails_Throws()
    {
        int closedPort = FindFreePort();

        var client = new RelayClient(
            PeerA,
            new Uri($"ws://localhost:{closedPort}/party/TEST"),
            fallbackUri: null,
            connectTimeout: TimeSpan.FromSeconds(2));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.JoinAsync(CancellationToken.None));
        Assert.DoesNotContain("fallback", ex.Message, StringComparison.OrdinalIgnoreCase);

        await client.DisposeAsync();
    }

    /// <summary>
    /// Allocates a TCP port, then immediately releases it. A connect to that
    /// port (assuming nobody else races us in the same millisecond) gets
    /// ConnectionRefused — the fastest primary-failure mode we can simulate.
    /// </summary>
    private static int FindFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
