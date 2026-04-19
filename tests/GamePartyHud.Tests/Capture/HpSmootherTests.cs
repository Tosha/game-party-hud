using System.Linq;
using GamePartyHud.Capture;
using Xunit;

namespace GamePartyHud.Tests.Capture;

public class HpSmootherTests
{
    [Fact]
    public void FirstSample_ReturnsRaw()
    {
        var s = new HpSmoother();
        Assert.Equal(0.7f, s.Push(0.7f));
    }

    [Fact]
    public void TwoSamples_ReturnMeanOfBoth()
    {
        var s = new HpSmoother();
        s.Push(0.2f);
        Assert.Equal(0.5f, s.Push(0.8f), 3);
    }

    [Fact]
    public void ThreeSamples_ReturnMiddleValue()
    {
        var s = new HpSmoother();
        s.Push(0.2f);
        s.Push(0.8f);
        Assert.Equal(0.5f, s.Push(0.5f), 3);
    }

    [Fact]
    public void SingleOutlier_IsRejectedAtSteadyState()
    {
        // Simulate five frames where HP is steady at 70% but one frame shows a spurious 10%
        // due to shimmer / capture racing / whatever.
        var s = new HpSmoother();
        s.Push(0.70f);
        s.Push(0.70f);
        float afterOutlier = s.Push(0.10f);   // window = [0.70, 0.70, 0.10] -> median 0.70
        Assert.Equal(0.70f, afterOutlier, 3);

        float next = s.Push(0.70f);           // window = [0.70, 0.10, 0.70] -> median 0.70
        Assert.Equal(0.70f, next, 3);

        float after = s.Push(0.70f);          // window = [0.10, 0.70, 0.70] -> median 0.70
        Assert.Equal(0.70f, after, 3);
    }

    [Fact]
    public void SustainedChange_PropagatesAfterMajoritySamples()
    {
        // HP was 70% for several ticks, now it's truly dropped to 20% and stays there.
        // Median-3 should lag by at most ⌈3/2⌉ - 1 = 1 tick after the change is the majority.
        var s = new HpSmoother();
        s.Push(0.70f);
        s.Push(0.70f);
        s.Push(0.70f);

        Assert.Equal(0.70f, s.Push(0.20f), 3); // [0.70, 0.70, 0.20] -> 0.70
        Assert.Equal(0.20f, s.Push(0.20f), 3); // [0.70, 0.20, 0.20] -> 0.20  <-- catches up here
        Assert.Equal(0.20f, s.Push(0.20f), 3); // [0.20, 0.20, 0.20] -> 0.20
    }

    [Fact]
    public void NoisyButStableSeries_ProducesTightOutput()
    {
        // Simulate noisy readings around a true 0.50 value (±0.05 jitter).
        var s = new HpSmoother();
        var rawSeries = new[] { 0.50f, 0.55f, 0.45f, 0.51f, 0.48f, 0.52f, 0.49f, 0.53f };
        var smoothed = rawSeries.Select(r => s.Push(r)).ToArray();

        // Every smoothed output should stay within a reasonably tight band.
        foreach (var v in smoothed.Skip(2))
        {
            Assert.InRange(v, 0.48f, 0.52f);
        }
    }

    [Fact]
    public void Reset_DropsPriorState()
    {
        var s = new HpSmoother();
        s.Push(0.8f);
        s.Push(0.9f);
        s.Reset();
        Assert.Equal(0.3f, s.Push(0.3f));
    }

    [Fact]
    public void WindowSizeOne_PassesThroughRaw()
    {
        var s = new HpSmoother(windowSize: 1);
        Assert.Equal(0.2f, s.Push(0.2f));
        Assert.Equal(0.9f, s.Push(0.9f));
    }

    [Fact]
    public void WindowSize_MustBePositive()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() => new HpSmoother(windowSize: 0));
        Assert.Throws<System.ArgumentOutOfRangeException>(() => new HpSmoother(windowSize: -1));
    }
}
