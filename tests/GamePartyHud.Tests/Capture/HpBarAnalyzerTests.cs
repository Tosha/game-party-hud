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
    public void Analyze_IsIndependentOfCalibratedFullColor()
    {
        // Regression test for the field bug where the calibration wizard sampled the
        // dark frame rows above/below the bar (user over-selected the region by 2 px
        // on each side) and stored a near-black fullColor. Before the fix, the analyzer
        // matched the frame and reported ~3% for a 100%-filled bar. After, the
        // classifier looks only at "is this a saturated red pixel?" and ignores the
        // calibrated reference entirely, so a bogus fullColor can't break readings.
        var buf = Bar(1.0f); // fully red bar
        var bogusDarkCalibration = new HpCalibration(
            Region: new HpRegion(0, 0, 0, 200, 10),
            FullColor: new Hsv(15f, 0.098f, 0.161f),   // near-black, exactly like the bug report
            Tolerance: HsvTolerance.Default,
            Direction: FillDirection.LTR);

        var pct = new HpBarAnalyzer().Analyze(buf, 200, 10, bogusDarkCalibration);

        Assert.InRange(pct, 0.98f, 1.0f);
    }

    [Fact]
    public void Analyze_FullBarWithTextOverlay_ReturnsFull()
    {
        // Reproduces the "Throne and Liberty HP bar with '246/246' text in the
        // middle" bug: a fully-filled red bar with the middle 60% of rows
        // obscured by light-coloured text. An earlier version of the analyzer
        // sampled only the middle 3 rows and misread this as ~3% HP.
        int width = 200, height = 10;
        var buf = SyntheticBitmap.HorizontalBar(width, height, 1.0f, (0, 0, 255), (40, 40, 40));
        // Overlay white text on rows 3..7 (inclusive) across columns 40..160 —
        // 120 text columns = 60% of the bar.
        for (int y = 3; y <= 7; y++)
        {
            for (int x = 40; x <= 160; x++)
            {
                int i = (y * width + x) * 4;
                buf[i]     = 240; // B
                buf[i + 1] = 240; // G
                buf[i + 2] = 240; // R — nearly-white text
                buf[i + 3] = 255;
            }
        }
        var pct = new HpBarAnalyzer().Analyze(buf, width, height, RedLtr);
        Assert.InRange(pct, 0.95f, 1.0f);
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
