using System;
using System.Threading;
using System.Threading.Tasks;
using GamePartyHud.Network;
using Xunit;

namespace GamePartyHud.Tests.Network;

public class RelayClientTests
{
    private const string PeerA = "a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe";

    [Fact(Timeout = 10_000)]
    public async Task JoinAsync_SendsJoinFrameAndAwaitsWelcome()
    {
        await using var server = new FakeRelayServer();
        var client = new RelayClient(PeerA, new Uri(server.WsUrl));

        var joinTask = client.JoinAsync(CancellationToken.None);

        // Server sees the join frame.
        var joinFrame = await server.NextReceivedAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("""{"type":"join","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe"}""", joinFrame);

        // Server replies with welcome.
        await server.SendFromServerAsync("""{"type":"welcome","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe","members":[]}""");

        // Client's JoinAsync completes.
        await joinTask;
        Assert.True(client.IsJoined);

        await client.DisposeAsync();
    }
}
