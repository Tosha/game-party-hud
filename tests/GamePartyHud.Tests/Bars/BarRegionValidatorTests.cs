using GamePartyHud.Bars;
using GamePartyHud.Capture;
using GamePartyHud.Tests.Capture;
using Xunit;

namespace GamePartyHud.Tests.Bars;

public class BarRegionValidatorTests
{
    // Saturated red bar matching the fixtures in BarAnalyzerTests.
    private static readonly (byte b, byte g, byte r) RedFill = (0, 0, 255);
    private static readonly (byte b, byte g, byte r) DarkEmpty = (10, 10, 10);

    [Fact]
    public void Validate_EmptyRegion_ReturnsError()
    {
        var region = new CaptureRegion(100, 100, 0, 0);
        var bgra = System.Array.Empty<byte>();

        var result = BarRegionValidator.Validate(region, bgra, isPickTime: false);

        Assert.Equal(ValidationLevel.Error, result.Level);
        Assert.Contains("empty", result.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_NoSaturatedPixels_ReturnsError()
    {
        // All-grey rectangle (no saturation anywhere).
        int w = 200, h = 22;
        var bgra = SyntheticBitmap.HorizontalBar(w, h, 0f, DarkEmpty, DarkEmpty);
        var region = new CaptureRegion(0, 0, w, h);

        var result = BarRegionValidator.Validate(region, bgra, isPickTime: false);

        Assert.Equal(ValidationLevel.Error, result.Level);
        Assert.Contains("colored", result.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RegionTooTall_ReturnsWarning()
    {
        int w = 200, h = 60; // height > 30 trips the rule
        var bgra = SyntheticBitmap.HorizontalBar(w, h, 1.0f, RedFill, DarkEmpty);
        var region = new CaptureRegion(0, 0, w, h);

        var result = BarRegionValidator.Validate(region, bgra, isPickTime: false);

        Assert.Equal(ValidationLevel.Warning, result.Level);
        Assert.Contains("tall", result.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_RegionTooNarrow_ReturnsWarning()
    {
        int w = 40, h = 22; // width < 60 trips the rule
        var bgra = SyntheticBitmap.HorizontalBar(w, h, 1.0f, RedFill, DarkEmpty);
        var region = new CaptureRegion(0, 0, w, h);

        var result = BarRegionValidator.Validate(region, bgra, isPickTime: false);

        Assert.Equal(ValidationLevel.Warning, result.Level);
        Assert.Contains("narrow", result.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_LowFillAtPickTime_ReturnsWarning()
    {
        int w = 200, h = 22;
        // 50% filled — well below the 0.85 threshold.
        var bgra = SyntheticBitmap.HorizontalBar(w, h, 0.5f, RedFill, DarkEmpty);
        var region = new CaptureRegion(0, 0, w, h);

        var result = BarRegionValidator.Validate(region, bgra, isPickTime: true);

        Assert.Equal(ValidationLevel.Warning, result.Level);
        Assert.Contains("full", result.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_LowFillNotAtPickTime_DoesNotWarn()
    {
        // Same input but isPickTime=false; the low-fill rule must suppress.
        int w = 200, h = 22;
        var bgra = SyntheticBitmap.HorizontalBar(w, h, 0.5f, RedFill, DarkEmpty);
        var region = new CaptureRegion(0, 0, w, h);

        var result = BarRegionValidator.Validate(region, bgra, isPickTime: false);

        Assert.Equal(ValidationLevel.Ok, result.Level);
    }

    [Fact]
    public void Validate_FragmentedFill_ReturnsWarning()
    {
        // Construct a buffer with two separate filled runs:
        // [filled 30][empty 40][filled 50][empty 80] — width 200, height 22
        int w = 200, h = 22;
        var bgra = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                bool inFirst  = x < 30;
                bool inSecond = x >= 70 && x < 120;
                var c = (inFirst || inSecond) ? RedFill : DarkEmpty;
                bgra[i + 0] = c.b;
                bgra[i + 1] = c.g;
                bgra[i + 2] = c.r;
                bgra[i + 3] = 255;
            }
        }
        var region = new CaptureRegion(0, 0, w, h);

        var result = BarRegionValidator.Validate(region, bgra, isPickTime: false);

        Assert.Equal(ValidationLevel.Warning, result.Level);
        Assert.Contains("continuous", result.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validate_AllChecksPass_ReturnsOk()
    {
        int w = 200, h = 22;
        var bgra = SyntheticBitmap.HorizontalBar(w, h, 1.0f, RedFill, DarkEmpty);
        var region = new CaptureRegion(827, 928, w, h);

        var result = BarRegionValidator.Validate(region, bgra, isPickTime: true);

        Assert.Equal(ValidationLevel.Ok, result.Level);
        // The OK message includes the geometry + a fill percentage.
        Assert.Contains("200", result.Message); // width
        Assert.Contains("22", result.Message);  // height
    }
}
