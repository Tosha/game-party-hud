using System;
using System.Collections.Generic;
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

    [Fact(Timeout = 10_000)]
    public async Task Welcome_WithMembers_FiresOnPeerConnectedForEach()
    {
        await using var server = new FakeRelayServer();
        var client = new RelayClient(PeerA, new Uri(server.WsUrl));

        var seen = new List<string>();
        client.OnPeerConnected += id => { lock (seen) seen.Add(id); };

        var joinTask = client.JoinAsync(CancellationToken.None);
        await server.NextReceivedAsync(TimeSpan.FromSeconds(5));
        await server.SendFromServerAsync("""{"type":"welcome","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe","members":["peer-b","peer-c"]}""");
        await joinTask;

        Assert.Equal(new[] { "peer-b", "peer-c" }, seen);

        await client.DisposeAsync();
    }

    [Fact(Timeout = 10_000)]
    public async Task PeerJoined_FiresOnPeerConnected()
    {
        await using var server = new FakeRelayServer();
        var client = new RelayClient(PeerA, new Uri(server.WsUrl));

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnPeerConnected += id => { if (id != "ignore-me") tcs.TrySetResult(id); };

        var joinTask = client.JoinAsync(CancellationToken.None);
        await server.NextReceivedAsync(TimeSpan.FromSeconds(5));
        await server.SendFromServerAsync("""{"type":"welcome","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe","members":[]}""");
        await joinTask;

        await server.SendFromServerAsync("""{"type":"peer-joined","peerId":"peer-b"}""");
        var id = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("peer-b", id);

        await client.DisposeAsync();
    }

    [Fact(Timeout = 10_000)]
    public async Task PeerLeft_FiresOnPeerDisconnected()
    {
        await using var server = new FakeRelayServer();
        var client = new RelayClient(PeerA, new Uri(server.WsUrl));

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnPeerDisconnected += id => tcs.TrySetResult(id);

        var joinTask = client.JoinAsync(CancellationToken.None);
        await server.NextReceivedAsync(TimeSpan.FromSeconds(5));
        await server.SendFromServerAsync("""{"type":"welcome","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe","members":[]}""");
        await joinTask;

        await server.SendFromServerAsync("""{"type":"peer-left","peerId":"peer-b"}""");
        var id = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("peer-b", id);

        await client.DisposeAsync();
    }

    [Fact(Timeout = 10_000)]
    public async Task Message_FiresOnMessageWithFromPeerIdAndPayload()
    {
        await using var server = new FakeRelayServer();
        var client = new RelayClient(PeerA, new Uri(server.WsUrl));

        var tcs = new TaskCompletionSource<(string from, string payload)>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnMessage += (from, payload) => tcs.TrySetResult((from, payload));

        var joinTask = client.JoinAsync(CancellationToken.None);
        await server.NextReceivedAsync(TimeSpan.FromSeconds(5));
        await server.SendFromServerAsync("""{"type":"welcome","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe","members":[]}""");
        await joinTask;

        await server.SendFromServerAsync("""{"type":"message","fromPeerId":"peer-b","payload":"{\"hp\":0.42}"}""");
        var (from, payload) = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("peer-b", from);
        Assert.Equal("""{"hp":0.42}""", payload);

        await client.DisposeAsync();
    }
}
