using Horizon.Input;

namespace Hex.Tests;

/// <summary>The arithmetic of trackpad scrolling and pinching, apart from any window or Mac.</summary>
public class MacGesturesTests
{
    [Theory]
    [InlineData(1.0f)]
    [InlineData(-2.5f)]
    [InlineData(0.3f)]
    [InlineData(0.0f)]
    public void Off_a_mac_the_notch_is_what_it_was(float delta)
    {
        // The old result: the delta's points over a notch, whatever the layout units of a point are.
        float old = PreciseScroll.ToPoints(delta, mac: false) / PreciseScroll.NotchPoints;
        Assert.Equal(old, PreciseScroll.ToNotches(delta, mac: false, unitsPerPoint: 0.8f));
        Assert.Equal(old, PreciseScroll.ToNotches(delta, mac: false, unitsPerPoint: 1.0f));
        Assert.Equal(delta, PreciseScroll.ToNotches(delta, mac: false, unitsPerPoint: 3.0f), 5);
    }

    [Fact]
    public void On_a_mac_a_unit_is_ten_points_and_the_layout_scales_it()
    {
        Assert.Equal(10.0f / 48.0f, PreciseScroll.ToNotches(1.0f, mac: true, unitsPerPoint: 1.0f), 5);
        Assert.Equal(10.0f / 48.0f * 0.8f, PreciseScroll.ToNotches(1.0f, mac: true, unitsPerPoint: 0.8f), 5);
        Assert.Equal(-10.0f / 48.0f, PreciseScroll.ToNotches(-1.0f, mac: true, unitsPerPoint: 1.0f), 5);
    }

    [Theory]
    [InlineData(0.0f)]
    [InlineData(-1.0f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void A_bad_units_per_point_counts_as_one(float unitsPerPoint)
    {
        Assert.Equal(PreciseScroll.ToNotches(2.0f, mac: true), PreciseScroll.ToNotches(2.0f, mac: true, unitsPerPoint));
    }

    [Fact]
    public void Looks_precise_is_a_fraction()
    {
        Assert.False(PreciseScroll.LooksPrecise(0.0f, 1.0f));
        Assert.False(PreciseScroll.LooksPrecise(-3.0f, 2.0f));
        Assert.True(PreciseScroll.LooksPrecise(0.0f, 0.3f));
        Assert.True(PreciseScroll.LooksPrecise(-0.7f, 1.0f));
    }

    [Fact]
    public void A_notched_wheel_zooms_one_step_either_way_and_a_precise_one_what_it_turned()
    {
        Assert.Equal(1.0f, PreciseScroll.ZoomSteps(3.0f, precise: false));
        Assert.Equal(-1.0f, PreciseScroll.ZoomSteps(-0.2f, precise: false));
        Assert.Equal(0.0f, PreciseScroll.ZoomSteps(0.0f, precise: false));
        Assert.Equal(0.25f, PreciseScroll.ZoomSteps(0.25f, precise: true));
        Assert.Equal(-2.5f, PreciseScroll.ZoomSteps(-2.5f, precise: true));
    }

    [Fact]
    public void A_zoom_factor_is_the_step_to_the_power_of_the_steps()
    {
        Assert.Equal(1.1f, PreciseScroll.ZoomFactor(1.0f, 1.1f), 5);
        Assert.Equal(1.0f / 1.1f, PreciseScroll.ZoomFactor(-1.0f, 1.1f), 5);
        Assert.Equal(1.0f, PreciseScroll.ZoomFactor(0.0f, 1.1f), 5);
        Assert.Equal(MathF.Sqrt(1.1f), PreciseScroll.ZoomFactor(0.5f, 1.1f), 5);
    }

    [Theory]
    [InlineData(0.02f, 1.02f)]
    [InlineData(-0.02f, 0.98f)]
    [InlineData(0.0f, 1.0f)]
    [InlineData(5.0f, 1.5f)]
    [InlineData(-5.0f, 0.5f)]
    public void A_pinch_is_one_plus_its_change_held_to_half(float magnification, float expected)
    {
        Assert.Equal(expected, PreciseScroll.PinchFactor(magnification), 5);
    }

    [Fact]
    public void A_smart_zoom_goes_larger_from_the_usual_size_and_back_from_anywhere_else()
    {
        Assert.Equal(1.5f, PreciseScroll.SmartZoomTarget(1.0f, 1.0f, 1.5f));
        Assert.Equal(1.5f, PreciseScroll.SmartZoomTarget(1.01f, 1.0f, 1.5f));
        Assert.Equal(1.0f, PreciseScroll.SmartZoomTarget(1.5f, 1.0f, 1.5f));
        Assert.Equal(1.0f, PreciseScroll.SmartZoomTarget(0.7f, 1.0f, 1.5f));
    }
}
