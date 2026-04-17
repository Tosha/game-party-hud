using GamePartyHud.Party;
using Xunit;

namespace GamePartyHud.Tests.Party;

public class PartyStateTests
{
    [Fact]
    public void Apply_State_AddsMember()
    {
        var s = new PartyState();
        s.Apply(new StateMessage("p1", "n", Role.Tank, 0.9f, 100), 100);
        Assert.True(s.Members.ContainsKey("p1"));
        Assert.Equal(0.9f, s.Members["p1"].HpPercent);
        Assert.Equal(100, s.Members["p1"].JoinedAtUnix);
    }

    [Fact]
    public void Apply_StateAgain_UpdatesHpButKeepsJoinTime()
    {
        var s = new PartyState();
        s.Apply(new StateMessage("p1", "n", Role.Tank, 0.9f, 100), 100);
        s.Apply(new StateMessage("p1", "n", Role.Tank, 0.4f, 200), 200);
        Assert.Equal(0.4f, s.Members["p1"].HpPercent);
        Assert.Equal(100, s.Members["p1"].JoinedAtUnix);
        Assert.Equal(200, s.Members["p1"].LastUpdateUnix);
    }

    [Fact]
    public void Apply_Bye_RemovesMember()
    {
        var s = new PartyState();
        s.Apply(new StateMessage("p1", "n", Role.Tank, 0.9f, 100), 100);
        s.Apply(new ByeMessage("p1"), 150);
        Assert.False(s.Members.ContainsKey("p1"));
    }

    [Fact]
    public void Apply_Kick_FlagsPeer_AndIgnoresSubsequentState()
    {
        var s = new PartyState();
        s.Apply(new StateMessage("p1", "n", Role.Tank, 0.9f, 100), 100);
        s.Apply(new KickMessage("p1"), 150);
        Assert.True(s.IsKicked("p1"));
        Assert.False(s.Members.ContainsKey("p1"));

        s.Apply(new StateMessage("p1", "n", Role.Tank, 0.9f, 200), 200);
        Assert.False(s.Members.ContainsKey("p1"));
    }

    [Fact]
    public void Tick_MarksStaleAfter6s_RemovesAfter60s()
    {
        var s = new PartyState();
        s.Apply(new StateMessage("p1", "n", Role.Tank, 1f, 100), 100);

        s.Tick(105);
        Assert.False(IsStale(s, "p1"));

        s.Tick(107);
        Assert.True(IsStale(s, "p1"));

        s.Tick(170);
        Assert.False(s.Members.ContainsKey("p1"));
    }

    private static bool IsStale(PartyState s, string id) =>
        s.Members.TryGetValue(id, out var m) && s.IsStale(m, s.LastTickUnix);

    [Fact]
    public void Changed_FiresOnApplyAndStaleTransition()
    {
        var s = new PartyState();
        int count = 0;
        s.Changed += () => count++;
        s.Apply(new StateMessage("p1", "n", Role.Tank, 1f, 100), 100);
        Assert.Equal(1, count);
        s.Tick(107);
        Assert.Equal(2, count);
        // Another tick without transition must not fire.
        s.Tick(108);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Changed_FiresOnFreshStateClearsStale()
    {
        var s = new PartyState();
        int count = 0;
        s.Apply(new StateMessage("p1", "n", Role.Tank, 1f, 100), 100);
        s.Tick(107); // stale
        s.Changed += () => count++;
        s.Apply(new StateMessage("p1", "n", Role.Tank, 0.5f, 110), 110);
        Assert.Equal(1, count);
        Assert.False(IsStale(s, "p1"));
    }
}
