using GamePartyHud.Party;
using Xunit;

namespace GamePartyHud.Tests.Party;

public class LeaderElectionTests
{
    [Fact]
    public void EarliestJoiner_IsLeader()
    {
        var s = new PartyState();
        s.Apply(new StateMessage("p3", "n", Role.Tank, 1f, 300), 300);
        s.Apply(new StateMessage("p1", "n", Role.Tank, 1f, 100), 300);
        s.Apply(new StateMessage("p2", "n", Role.Tank, 1f, 200), 300);
        Assert.Equal("p1", s.LeaderPeerId);
    }

    [Fact]
    public void TieBreaker_IsLexicographicPeerId()
    {
        var s = new PartyState();
        s.Apply(new StateMessage("zeta", "n", Role.Tank, 1f, 100), 300);
        s.Apply(new StateMessage("alpha", "n", Role.Tank, 1f, 100), 300);
        s.Apply(new StateMessage("mike", "n", Role.Tank, 1f, 100), 300);
        Assert.Equal("alpha", s.LeaderPeerId);
    }

    [Fact]
    public void LeaderLeaves_NextEarliestBecomesLeader()
    {
        var s = new PartyState();
        s.Apply(new StateMessage("p1", "n", Role.Tank, 1f, 100), 100);
        s.Apply(new StateMessage("p2", "n", Role.Tank, 1f, 200), 200);
        Assert.Equal("p1", s.LeaderPeerId);
        s.Apply(new ByeMessage("p1"), 210);
        Assert.Equal("p2", s.LeaderPeerId);
    }

    [Fact]
    public void EmptyParty_LeaderIsNull()
    {
        Assert.Null(new PartyState().LeaderPeerId);
    }
}
