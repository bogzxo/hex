using Horizon.Input;
using Xunit;

namespace Hex.Tests;

public class PreciseScrollTests
{
    [Theory]
    [InlineData(1.0f, 0.0f, false)]
    [InlineData(-3.0f, 2.0f, false)]
    [InlineData(0.0f, 0.0f, false)]
    [InlineData(0.0f, 0.3f, true)]
    [InlineData(-0.1f, 0.0f, true)]
    [InlineData(1.0f, 0.5f, true)]
    public void FractionalDeltasLookPrecise(float x, float y, bool expected) =>
        Assert.Equal(expected, PreciseScroll.LooksPrecise(x, y));

    [Fact]
    public void MacUnitIsTenPoints()
    {
        // SDL scales NSEvent.scrollingDeltaY by 0.1, so 0.5 is 5 points and 48 points are one notch
        Assert.Equal(5.0f, PreciseScroll.ToPoints(0.5f, mac: true), 4);
        Assert.Equal(4.8f, PreciseScroll.ToNotches(0.48f, mac: true) * PreciseScroll.NotchPoints, 3);
        Assert.Equal(10.0f / 48.0f, PreciseScroll.ToNotches(1.0f, mac: true), 5);
    }

    [Fact]
    public void OtherPlatformsUnitIsANotch()
    {
        Assert.Equal(24.0f, PreciseScroll.ToPoints(0.5f, mac: false), 4);
        Assert.Equal(0.5f, PreciseScroll.ToNotches(0.5f, mac: false), 5);
    }

    [Fact]
    public void SignIsKept()
    {
        Assert.True(PreciseScroll.ToNotches(-0.3f, mac: true) < 0.0f);
        Assert.True(PreciseScroll.ToNotches(0.3f, mac: false) > 0.0f);
    }

    [Fact]
    public void MomentumTailSumsToTheFingerDistance()
    {
        // A flick of events adds up to the same distance whatever it is cut into
        float total = 0.0f;
        foreach (float step in new[] { 0.8f, 0.5f, 0.3f, 0.2f, 0.1f })
            total += PreciseScroll.ToPoints(step, mac: true);
        Assert.Equal(19.0f, total, 3);
    }
}
