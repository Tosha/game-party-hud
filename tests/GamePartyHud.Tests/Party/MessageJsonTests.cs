using GamePartyHud.Party;
using Xunit;

namespace GamePartyHud.Tests.Party;

public class MessageJsonTests
{
    [Fact]
    public void RoundTrip_State_AllBars()
    {
        var msg = new StateMessage("peer-1", "Yia", Role.Tank, 0.72f, 0.55f, 0.41f, 1713200000);
        var json = MessageJson.Encode(msg);
        var decoded = MessageJson.Decode(json);
        Assert.Equal(msg, decoded);
    }

    [Fact]
    public void RoundTrip_State_HpOnly_StaminaAndManaNull()
    {
        var msg = new StateMessage("peer-1", "Yia", Role.Tank, 0.72f, null, null, 1713200000);
        var json = MessageJson.Encode(msg);
        Assert.Contains("\"stamina\":null", json);
        Assert.Contains("\"mana\":null", json);
        var decoded = (StateMessage?)MessageJson.Decode(json);
        Assert.NotNull(decoded);
        Assert.Equal(0.72f, decoded!.Hp);
        Assert.Null(decoded.Stamina);
        Assert.Null(decoded.Mana);
    }

    [Fact]
    public void Decode_OldShapeJson_MissingStaminaAndMana_ParsesAsNulls()
    {
        // Wire-back-compat: a peer running a pre-Stam/Mana build emits JSON
        // without "stamina" and "mana" keys. Decoder must default them to null
        // rather than throw.
        var json = """
        {
          "type": "state",
          "peerId": "old-peer",
          "nick": "Old",
          "role": "Tank",
          "hp": 0.5,
          "t": 1234
        }
        """;
        var decoded = (StateMessage?)MessageJson.Decode(json);
        Assert.NotNull(decoded);
        Assert.Equal(0.5f, decoded!.Hp);
        Assert.Null(decoded.Stamina);
        Assert.Null(decoded.Mana);
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
        var msg = new StateMessage("p1", "n", Role.Healer, null, null, null, 42);
        var json = MessageJson.Encode(msg);
        Assert.Contains("\"hp\":null", json);
        var decoded = (StateMessage?)MessageJson.Decode(json);
        Assert.NotNull(decoded);
        Assert.Null(decoded!.Hp);
        Assert.Null(decoded.Stamina);
        Assert.Null(decoded.Mana);
    }
}
