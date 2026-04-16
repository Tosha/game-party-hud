using GamePartyHud.Capture;
using Xunit;

namespace GamePartyHud.Tests.Capture;

public class HsvTests
{
    [Fact]
    public void FromBgra_PureRed_ReturnsHueZero()
    {
        var hsv = Hsv.FromBgra(b: 0, g: 0, r: 255);
        Assert.Equal(0f, hsv.H);
        Assert.Equal(1f, hsv.S, 3);
        Assert.Equal(1f, hsv.V, 3);
    }

    [Fact]
    public void FromBgra_PureGreen_ReturnsHue120()
    {
        var hsv = Hsv.FromBgra(b: 0, g: 255, r: 0);
        Assert.Equal(120f, hsv.H, 3);
    }

    [Fact]
    public void FromBgra_PureBlue_ReturnsHue240()
    {
        var hsv = Hsv.FromBgra(b: 255, g: 0, r: 0);
        Assert.Equal(240f, hsv.H, 3);
    }

    [Fact]
    public void FromBgra_Black_ReturnsZeroValue()
    {
        var hsv = Hsv.FromBgra(0, 0, 0);
        Assert.Equal(0f, hsv.V);
        Assert.Equal(0f, hsv.S);
    }

    [Theory]
    [InlineData(355f, 5f, true)]
    [InlineData(0f, 10f, true)]
    [InlineData(0f, 20f, false)]
    [InlineData(90f, 120f, false)]
    public void Tolerance_HueWrapAround_IsSymmetric(float reference, float sample, bool expected)
    {
        var tol = new HsvTolerance(15f, 1f, 1f);
        var a = new Hsv(reference, 0.5f, 0.5f);
        var b = new Hsv(sample, 0.5f, 0.5f);
        Assert.Equal(expected, tol.Matches(a, b));
    }

    [Fact]
    public void Tolerance_SaturationAndValueDifferencesAreRespected()
    {
        var tol = new HsvTolerance(360f, 0.1f, 0.1f);
        var reference = new Hsv(0f, 0.8f, 0.8f);
        Assert.True(tol.Matches(reference, new Hsv(0f, 0.85f, 0.82f)));
        Assert.False(tol.Matches(reference, new Hsv(0f, 0.5f, 0.8f)));
        Assert.False(tol.Matches(reference, new Hsv(0f, 0.8f, 0.5f)));
    }
}
