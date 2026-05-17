using System;
using GamePartyHud.Capture;
using Xunit;

namespace GamePartyHud.Tests.Capture;

/// <summary>
/// Regression guard for <see cref="BarAnalyzer"/> against real captures of the
/// actual HP bar in a supported game (Throne &amp; Liberty), at 12 HP percentages
/// from 5% to 100%. The analyzer is run against each capture using a purely
/// geometric calibration (just the region bounds + fill direction — no pixel
/// sampling), and every sample is asserted within ±3% of its filename-advertised
/// value.
///
/// If this test ever fails, the analyzer's accuracy on the reference game has
/// regressed — check what changed in <see cref="BarAnalyzer"/>.
/// </summary>
public class SampleImageRegressionTests
{
    /// <summary>Per-sample tolerance: 3% of bar width. Absorbs 1–2 column rounding.</summary>
    private const float Tolerance = 0.03f;

    public static readonly TheoryData<string, float> Samples = new()
    {
        { "HP_BAR_5_PER_CENT.png",   0.05f },
        { "HP_BAR_11_PER_CENT.png",  0.11f },
        { "HP_BAR_18_PER_CENT.png",  0.18f },
        { "HP_BAR_26_PER_CENT.png",  0.26f },
        { "HP_BAR_33_PER_CENT.png",  0.33f },
        { "HP_BAR_42_PER_CENT.png",  0.42f },
        { "HP_BAR_50_PER_CENT.png",  0.50f },
        { "HP_BAR_62_PER_CENT.png",  0.62f },
        { "HP_BAR_73_PER_CENT.png",  0.73f },
        { "HP_BAR_84_PER_CENT.png",  0.84f },
        { "HP_BAR_90_PER_CENT.png",  0.90f },
        { "HP_BAR_100_PER_CENT.png", 1.00f },
    };

    [Theory]
    [MemberData(nameof(Samples))]
    public void Analyze_MatchesFilenamePercentage_WithinTolerance(string file, float expected)
    {
        var (bgra, w, h) = ImageLoader.Load(ImageLoader.SamplePath(file));
        var cal = new BarCalibration(new CaptureRegion(0, 0, w, h), FillDirection.LTR);
        var actual = new BarAnalyzer().Analyze(bgra, w, h, cal);

        Assert.InRange(actual, expected - Tolerance, expected + Tolerance);
    }

    [Fact]
    public void AllSamples_MeanAbsoluteError_IsUnderOnePercent()
    {
        float totalAbs = 0;
        int n = 0;
        foreach (var row in Samples)
        {
            var file = (string)row[0];
            var expected = (float)row[1];
            var (bgra, w, h) = ImageLoader.Load(ImageLoader.SamplePath(file));
            var cal = new BarCalibration(new CaptureRegion(0, 0, w, h), FillDirection.LTR);
            var actual = new BarAnalyzer().Analyze(bgra, w, h, cal);
            totalAbs += Math.Abs(actual - expected);
            n++;
        }
        var mae = totalAbs / n;
        Assert.True(mae < 0.01f, $"Mean absolute error across {n} samples was {mae:P2}; expected <1%.");
    }

    /// <summary>Per-sample tolerance for mana bars — 4% to absorb the larger anti-alias
    /// noise band around the dark text-label area that sits inside the bar.</summary>
    private const float ManaTolerance = 0.04f;

    public static readonly TheoryData<string, float> ManaSamples = new()
    {
        { "MANA_BAR_7_PER_CENT.png",   0.07f },
        { "MANA_BAR_15_PER_CENT.png",  0.15f },
        { "MANA_BAR_23_PER_CENT.png",  0.23f },
        { "MANA_BAR_29_PER_CENT.png",  0.29f },
        { "MANA_BAR_39_PER_CENT.png",  0.39f },
        { "MANA_BAR_45_PER_CENT.png",  0.45f },
        { "MANA_BAR_53_PER_CENT.png",  0.53f },
        { "MANA_BAR_58_PER_CENT.png",  0.58f },
        { "MANA_BAR_71_PER_CENT.png",  0.71f },
        { "MANA_BAR_79_PER_CENT.png",  0.79f },
        { "MANA_BAR_88_PER_CENT.png",  0.88f },
        { "MANA_BAR_94_PER_CENT.png",  0.94f },
        { "MANA_BAR_100_PER_CENT.png", 1.00f },
    };

    [Theory]
    [MemberData(nameof(ManaSamples))]
    public void AnalyzeMana_MatchesFilenamePercentage_WithinTolerance(string file, float expected)
    {
        var (bgra, w, h) = ImageLoader.Load(ImageLoader.ManaSamplePath(file));
        var cal = new BarCalibration(new CaptureRegion(0, 0, w, h), FillDirection.LTR);
        var actual = new BarAnalyzer().Analyze(bgra, w, h, cal);

        Assert.InRange(actual, expected - ManaTolerance, expected + ManaTolerance);
    }

    [Fact]
    public void AllManaSamples_MeanAbsoluteError_IsUnderTwoPercent()
    {
        float totalAbs = 0;
        int n = 0;
        foreach (var row in ManaSamples)
        {
            var file = (string)row[0];
            var expected = (float)row[1];
            var (bgra, w, h) = ImageLoader.Load(ImageLoader.ManaSamplePath(file));
            var cal = new BarCalibration(new CaptureRegion(0, 0, w, h), FillDirection.LTR);
            var actual = new BarAnalyzer().Analyze(bgra, w, h, cal);
            totalAbs += Math.Abs(actual - expected);
            n++;
        }
        var mae = totalAbs / n;
        Assert.True(mae < 0.02f, $"Mean absolute error across {n} mana samples was {mae:P2}; expected <2%.");
    }
}
