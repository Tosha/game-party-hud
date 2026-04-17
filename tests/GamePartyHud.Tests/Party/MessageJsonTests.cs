using GamePartyHud.Party;
using Xunit;

namespace GamePartyHud.Tests.Party;

public class MessageJsonTests
{
    [Fact]
    public void RoundTrip_State()
    {
        var msg = new StateMessage("peer-1", "Yia", Role.Tank, 0.72f, 1713200000);
        var json = MessageJson.Encode(msg);
        var decoded = MessageJson.Decode(json);
        Assert.Equal(msg, decoded);
    }

    [Fact]
    public void RoundTrip_Bye()
    {
        var msg = new ByeMessage("peer-2");
        Assert.Equal(msg, MessageJson.Decode(MessageJson.Encode(msg)));
    }

    [Fact]
    public void RoundTrip_Kick()
    {
        var msg = new KickMessage("peer-3");
        Assert.Equal(msg, MessageJson.Decode(MessageJson.Encode(msg)));
    }

    [Fact]
    public void Decode_UnknownType_ReturnsNull()
    {
        Assert.Null(MessageJson.Decode("""{"type":"nope"}"""));
    }

    [Fact]
    public void Decode_MalformedJson_ReturnsNull()
    {
        Assert.Null(MessageJson.Decode("{not-json"));
    }

    [Fact]
    public void State_NullHp_RoundTripsAsNull()
    {
        var msg = new StateMessage("p1", "n", Role.Healer, null, 42);
        var json = MessageJson.Encode(msg);
        Assert.Contains("\"hp\":null", json);
        var decoded = (StateMessage?)MessageJson.Decode(json);
        Assert.NotNull(decoded);
        Assert.Null(decoded!.Hp);
    }
}
