using Horizon.UI.Components;

namespace Hex.Tests;

/// <summary>How much room a Mac's traffic lights and window corner take of the title bar, apart from any Mac.</summary>
public class MacWindowChromeTests
{
    [Theory]
    [InlineData(156.0f, 2.0f, 78.0f)]
    [InlineData(78.0f, 1.0f, 78.0f)]
    [InlineData(0.0f, 2.0f, 0.0f)]
    [InlineData(-4.0f, 2.0f, 0.0f)]
    [InlineData(78.0f, 0.0f, 0.0f)]
    public void The_room_of_the_lights_is_their_pixels_in_the_units_of_the_ui(float pixels, float uiScale, float units) =>
        Assert.Equal(units, TitleBar.LeadingRoomFor(pixels, uiScale), 4);
}
