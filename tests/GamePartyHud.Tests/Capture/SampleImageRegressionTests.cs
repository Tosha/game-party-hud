using System;
using GamePartyHud.Capture;
using Xunit;

namespace GamePartyHud.Tests.Capture;

/// <summary>
/// Regression guard for <see cref="HpBarAnalyzer"/> against real captures of the
/// actual HP bar in a supported game (Throne &amp; Liberty), at 12 HP percentages
/// from 4% to 100%. The 100% capture is used as the calibration sample (matching
/// what the in-app wizard does when the user picks their HP region with full HP),
/// then every other sample is analyzed and asserted within ±3% of its filename-
/// advertised value.
///
/// If this test ever fails, the analyzer's accuracy on the reference game has
/// regressed — check what changed in the analyzer or in the calibration wizard's
/// <c>SampleFullColor</c> logic.
/// </summary>
public class SampleImageRegressionTests
{
    /// <summary>Per-sample tolerance: 3% of bar width. Absorbs 1–2 column rounding.</summary>
    private const float Tolerance = 0.03f;

    public static readonly TheoryData<string, float> Samples = new()
    {
        { "HP_BAR_4_PER_CENT.png",   0.04f },
        { "HP_BAR_6_PER_CENT.png",   0.06f },
        { "HP_BAR_13_PER_CENT.png",  0.13f },
        { "HP_BAR_22_PER_CENT.png",  0.22f },
        { "HP_BAR_28_PER_CENT.png",  0.28f },
        { "HP_BAR_34_PER_CENT.png",  0.34f },
        { "HP_BAR_43_PER_CENT.png",  0.43f },
        { "HP_BAR_53_PER_CENT.png",  0.53f },
        { "HP_BAR_63_PER_CENT.png",  0.63f },
        { "HP_BAR_75_PER_CENT.png",  0.75f },
        { "HP_BAR_89_PER_CENT.png",  0.89f },
        { "HP_BAR_100_PER_CENT.png", 1.00f },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Analyze_MatchesFilenamePercentage_WithinTolerance(string file, float expected)
    {
        var cal = CalibrateFromFullSample();

        var (bgra, w, h) = ImageLoader.Load(ImageLoader.SamplePath(file));
        var actual = new HpBarAnalyzer().Analyze(bgra, w, h, cal);

        Assert.InRange(actual, expected - Tolerance, expected + Tolerance);
    }

    [Fact]
    public void AllSamples_MeanAbsoluteError_IsUnderOnePercent()
    {
        var cal = CalibrateFromFullSample();

        float totalAbs = 0;
        int n = 0;
        foreach (var row in Samples)
        {
            var file = (string)row[0];
            var expected = (float)row[1];
            var (bgra, w, h) = ImageLoader.Load(ImageLoader.SamplePath(file));
            var actual = new HpBarAnalyzer().Analyze(bgra, w, h, cal);
            totalAbs += Math.Abs(actual - expected);
            n++;
        }
        var mae = totalAbs / n;
        Assert.True(mae < 0.01f, $"Mean absolute error across {n} samples was {mae:P2}; expected <1%.");
    }

    /// <summary>
    /// Mirrors the calibration wizard's <c>SampleFullColor</c> logic: average pixels
    /// from the top and bottom 20% of the bar and from the middle 50% of columns,
    /// skipping the overlaid text that lives in the middle rows.
    /// </summary>
    private static HpCalibration CalibrateFromFullSample()
    {
        var (bgra, w, h) = ImageLoader.Load(ImageLoader.SamplePath("HP_BAR_100_PER_CENT.png"));

        int band = Math.Max(1, h / 5);
        double sr = 0, sg = 0, sb = 0;
        int pixels = 0;
        void AddRow(int y)
        {
            int x0 = w / 4;
            int x1 = w * 3 / 4;
            for (int x = x0; x < x1; x++)
            {
                int i = (y * w + x) * 4;
                sb += bgra[i]; sg += bgra[i + 1]; sr += bgra[i + 2];
                pixels++;
            }
        }
        for (int y = 0; y < Math.Min(band, h); y++) AddRow(y);
        for (int y = Math.Max(0, h - band); y < h; y++) AddRow(y);

        var fullColor = Hsv.FromBgra(
            (byte)Math.Clamp(sb / pixels, 0, 255),
            (byte)Math.Clamp(sg / pixels, 0, 255),
            (byte)Math.Clamp(sr / pixels, 0, 255));

        return new HpCalibration(
            new HpRegion(0, 0, 0, w, h),
            fullColor,
            HsvTolerance.Default,
            FillDirection.LTR);
    }
}
