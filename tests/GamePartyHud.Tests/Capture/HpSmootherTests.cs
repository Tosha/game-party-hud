using GamePartyHud.Capture;
using Xunit;

namespace GamePartyHud.Tests.Capture;

public class HpSmootherTests
{
    [Fact]
    public void FirstSample_ReturnsRaw()
    {
        var s = new HpSmoother(alpha: 0.5f);
        Assert.Equal(0.7f, s.Push(0.7f));
    }

    [Fact]
    public void SecondSample_WeightsHalfAndHalf_WhenAlphaIsHalf()
    {
        var s = new HpSmoother(alpha: 0.5f);
        s.Push(0.8f);
        Assert.Equal(0.6f, s.Push(0.4f), 3);
    }

    [Fact]
    public void Reset_DropsPriorState()
    {
        var s = new HpSmoother(alpha: 0.5f);
        s.Push(0.8f);
        s.Reset();
        Assert.Equal(0.3f, s.Push(0.3f));
    }

    [Fact]
    public void AlphaOne_PassesThroughRawValues()
    {
        var s = new HpSmoother(alpha: 1.0f);
        Assert.Equal(0.2f, s.Push(0.2f));
        Assert.Equal(0.9f, s.Push(0.9f));
    }
}
