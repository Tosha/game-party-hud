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
}
