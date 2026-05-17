using GamePartyHud.Settings;
using Xunit;

namespace GamePartyHud.Tests.Settings;

public class HudColorTests
{
    [Fact]
    public void TryParse_RrggbbHex_AssumesOpaqueAlpha()
    {
        var result = HudColor.TryParse("#FFAABB");
        Assert.NotNull(result);
        Assert.Equal((byte)0xFF, result!.Value.A);
        Assert.Equal((byte)0xFF, result.Value.R);
        Assert.Equal((byte)0xAA, result.Value.G);
        Assert.Equal((byte)0xBB, result.Value.B);
    }

    [Fact]
    public void TryParse_AarrggbbHex_PreservesAlpha()
    {
        var result = HudColor.TryParse("#80FFAABB");
        Assert.NotNull(result);
        Assert.Equal((byte)0x80, result!.Value.A);
        Assert.Equal((byte)0xFF, result.Value.R);
        Assert.Equal((byte)0xAA, result.Value.G);
        Assert.Equal((byte)0xBB, result.Value.B);
    }

    [Theory]
    [InlineData("#XYZ")]
    [InlineData("#FF")]
    [InlineData("nothex")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("#FFAABBCC1")]   // 9 chars total — too long
    public void TryParse_InvalidHex_ReturnsNull(string? hex)
    {
        Assert.Null(HudColor.TryParse(hex!));
    }

    [Fact]
    public void Format_RoundTripsThroughTryParse()
    {
        foreach (var input in new[] { "#FF821414", "#80FFAABB", "#FF000000", "#FFFFFFFF" })
        {
            var parsed = HudColor.TryParse(input);
            Assert.NotNull(parsed);
            var (a, r, g, b) = parsed!.Value;
            Assert.Equal(input, HudColor.Format(a, r, g, b));
        }
    }

    [Fact]
    public void RgbToHsv_ReturnsKnownValuesForPrimaryColours()
    {
        // Pure red: H=0, S=1, V=1
        var red = HudColor.RgbToHsv(255, 0, 0);
        Assert.Equal(0.0, red.H, 1);
        Assert.Equal(1.0, red.S, 3);
        Assert.Equal(1.0, red.V, 3);

        // Pure green: H=120, S=1, V=1
        var green = HudColor.RgbToHsv(0, 255, 0);
        Assert.Equal(120.0, green.H, 1);
        Assert.Equal(1.0, green.S, 3);
        Assert.Equal(1.0, green.V, 3);

        // Pure blue: H=240, S=1, V=1
        var blue = HudColor.RgbToHsv(0, 0, 255);
        Assert.Equal(240.0, blue.H, 1);
        Assert.Equal(1.0, blue.S, 3);
        Assert.Equal(1.0, blue.V, 3);

        // Mid-grey: S=0, V=0.5
        var grey = HudColor.RgbToHsv(128, 128, 128);
        Assert.Equal(0.0, grey.S, 3);
        Assert.InRange(grey.V, 0.49, 0.51);
    }

    [Fact]
    public void HsvToRgb_RoundTripsThroughRgbToHsv()
    {
        var samples = new (byte R, byte G, byte B)[]
        {
            (255, 0, 0),     (0, 255, 0),     (0, 0, 255),
            (255, 255, 0),   (0, 255, 255),   (255, 0, 255),
            (200, 100, 50),  (50, 100, 200),  (130, 20, 20),
            (0, 0, 0),       (255, 255, 255), (128, 128, 128),
        };
        foreach (var (r, g, b) in samples)
        {
            var (h, s, v) = HudColor.RgbToHsv(r, g, b);
            var (r2, g2, b2) = HudColor.HsvToRgb(h, s, v);
            // ±1 due to integer rounding in the HSV→RGB step.
            Assert.InRange(r2, (byte)System.Math.Max(0, r - 1), (byte)System.Math.Min(255, r + 1));
            Assert.InRange(g2, (byte)System.Math.Max(0, g - 1), (byte)System.Math.Min(255, g + 1));
            Assert.InRange(b2, (byte)System.Math.Max(0, b - 1), (byte)System.Math.Min(255, b + 1));
        }
    }

    [Fact]
    public void Darken_HalvesAllChannelsAtFactor05()
    {
        var darkened = HudColor.Darken((200, 100, 50), 0.5);
        Assert.Equal((byte)100, darkened.R);
        Assert.Equal((byte)50,  darkened.G);
        Assert.Equal((byte)25,  darkened.B);
    }

    [Fact]
    public void Darken_ClampsResultToByteRange()
    {
        // Factor > 1 caps at 255.
        var brightened = HudColor.Darken((200, 100, 50), 2.0);
        Assert.Equal((byte)255, brightened.R);
        Assert.Equal((byte)200, brightened.G);
        Assert.Equal((byte)100, brightened.B);

        // Factor < 0 floors at 0.
        var negative = HudColor.Darken((200, 100, 50), -0.5);
        Assert.Equal((byte)0, negative.R);
        Assert.Equal((byte)0, negative.G);
        Assert.Equal((byte)0, negative.B);
    }
}
