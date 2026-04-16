using GamePartyHud.Capture;
using Xunit;

namespace GamePartyHud.Tests.Capture;

public class HpBarAnalyzerTests
{
    private static readonly HpCalibration RedLtr = new(
        Region: new HpRegion(0, 0, 0, 200, 10),
        FullColor: Hsv.FromBgra(b: 0, g: 0, r: 255),
        Tolerance: HsvTolerance.Default,
        Direction: FillDirection.LTR);

    private static byte[] Bar(float ratio) => SyntheticBitmap.HorizontalBar(
        width: 200, height: 10, fillRatio: ratio,
        fillBgr: (0, 0, 255),
        emptyBgr: (40, 40, 40));

    [Fact]
    public void Analyze_FullBar_Returns1()
    {
        var buf = Bar(1.0f);
        var pct = new HpBarAnalyzer().Analyze(buf, 200, 10, RedLtr);
        Assert.InRange(pct, 0.98f, 1.0f);
    }

    [Fact]
    public void Analyze_EmptyBar_Returns0()
    {
        var buf = Bar(0.0f);
        var pct = new HpBarAnalyzer().Analyze(buf, 200, 10, RedLtr);
        Assert.InRange(pct, 0.0f, 0.02f);
    }

    [Theory]
    [InlineData(0.25f)]
    [InlineData(0.50f)]
    [InlineData(0.72f)]
    [InlineData(0.90f)]
    public void Analyze_PartialBar_WithinTwoPercent(float ratio)
    {
        var buf = Bar(ratio);
        var pct = new HpBarAnalyzer().Analyze(buf, 200, 10, RedLtr);
        Assert.InRange(pct, ratio - 0.02f, ratio + 0.02f);
    }

    [Fact]
    public void Analyze_Rtl_InvertsReading()
    {
        var buf = SyntheticBitmap.HorizontalBar(200, 10, 0.7f, (0, 0, 255), (40, 40, 40));
        var cal = RedLtr with { Direction = FillDirection.RTL };
        var pct = new HpBarAnalyzer().Analyze(buf, 200, 10, cal);
        Assert.InRange(pct, 0.28f, 0.32f);
    }

    [Fact]
    public void Analyze_NoMatchingPixels_Returns0()
    {
        var buf = SyntheticBitmap.HorizontalBar(200, 10, 0f, (40, 40, 40), (40, 40, 40));
        var pct = new HpBarAnalyzer().Analyze(buf, 200, 10, RedLtr);
        Assert.Equal(0f, pct);
    }

    [Fact]
    public void Analyze_Clamps_PercentToClosedUnit()
    {
        var buf = SyntheticBitmap.HorizontalBar(200, 10, 0.5f, (0, 0, 255), (40, 40, 40));
        buf[(5 * 200 * 4) + 150 * 4 + 0] = 0;
        buf[(5 * 200 * 4) + 150 * 4 + 1] = 0;
        buf[(5 * 200 * 4) + 150 * 4 + 2] = 255;
        var pct = new HpBarAnalyzer().Analyze(buf, 200, 10, RedLtr);
        Assert.InRange(pct, 0.48f, 0.52f);
    }
}
