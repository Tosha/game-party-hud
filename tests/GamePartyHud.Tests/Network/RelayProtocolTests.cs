using System.Text.Json;
using GamePartyHud.Network;
using Xunit;

namespace GamePartyHud.Tests.Network;

/// <summary>
/// Wire-format parity with relay/test/fixtures.ts. Any change here must be
/// mirrored on the TypeScript side — the tests on both sides pin the same
/// canonical JSON strings so the server and client can't drift apart silently.
/// </summary>
public class RelayProtocolTests
{
    // Exact copies of relay/test/fixtures.ts.
    private const string FxJoin       = """{"type":"join","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe"}""";
    private const string FxBroadcast  = """{"type":"broadcast","payload":"{\"type\":\"state\",\"hp\":0.5}"}""";
    private const string FxWelcome    = """{"type":"welcome","peerId":"a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe","members":["peer-b","peer-c"]}""";
    private const string FxPeerJoined = """{"type":"peer-joined","peerId":"peer-b"}""";
    private const string FxPeerLeft   = """{"type":"peer-left","peerId":"peer-b"}""";
    private const string FxMessage    = """{"type":"message","fromPeerId":"peer-b","payload":"{\"type\":\"state\",\"hp\":0.5}"}""";

    [Fact]
    public void EncodeJoin_MatchesTsFixture()
    {
        Assert.Equal(FxJoin, RelayProtocol.EncodeJoin("a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe"));
    }

    [Fact]
    public void EncodeBroadcast_MatchesTsFixture()
    {
        Assert.Equal(FxBroadcast, RelayProtocol.EncodeBroadcast("""{"type":"state","hp":0.5}"""));
    }

    [Fact]
    public void DecodeWelcome_ParsesAllFields()
    {
        var msg = RelayProtocol.DecodeServerMessage(FxWelcome);
        var welcome = Assert.IsType<RelayProtocol.Welcome>(msg);
        Assert.Equal("a5bdd9f976fe4da6a5dc11035522d1ddbeefcafe", welcome.PeerId);
        Assert.Equal(new[] { "peer-b", "peer-c" }, welcome.Members);
    }

    [Fact]
    public void DecodePeerJoined_Parses()
    {
        var msg = RelayProtocol.DecodeServerMessage(FxPeerJoined);
        var joined = Assert.IsType<RelayProtocol.PeerJoined>(msg);
        Assert.Equal("peer-b", joined.PeerId);
    }

    [Fact]
    public void DecodePeerLeft_Parses()
    {
        var msg = RelayProtocol.DecodeServerMessage(FxPeerLeft);
        var left = Assert.IsType<RelayProtocol.PeerLeft>(msg);
        Assert.Equal("peer-b", left.PeerId);
    }

    [Fact]
    public void DecodeMessage_Parses()
    {
        var msg = RelayProtocol.DecodeServerMessage(FxMessage);
        var m = Assert.IsType<RelayProtocol.Message>(msg);
        Assert.Equal("peer-b", m.FromPeerId);
        Assert.Equal("""{"type":"state","hp":0.5}""", m.Payload);
    }

    [Fact]
    public void DecodeError_Parses()
    {
        var msg = RelayProtocol.DecodeServerMessage("""{"type":"error","reason":"party-full"}""");
        var err = Assert.IsType<RelayProtocol.ErrorMessage>(msg);
        Assert.Equal("party-full", err.Reason);
    }

    [Fact]
    public void DecodeMalformedJson_ReturnsNull()
    {
        Assert.Null(RelayProtocol.DecodeServerMessage("not json"));
    }

    [Fact]
    public void DecodeUnknownType_ReturnsNull()
    {
        Assert.Null(RelayProtocol.DecodeServerMessage("""{"type":"hello"}"""));
    }
}
