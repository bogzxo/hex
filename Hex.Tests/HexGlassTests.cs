using Hex.Shell;

using Horizon.UI.Drawing;

namespace Hex.Tests;

public sealed class HexGlassTests
{
    [Theory]
    [InlineData(2.0f, 0.3f, 0.0f)]
    [InlineData(8.0f, 0.78f, 16.0f)]
    [InlineData(30.0f, 0.95f, 24.0f)]
    public void Popups_sides_and_windows_smear_as_far(float blur, float clarity, float bend)
    {
        UIGlass popup = HexGlass.Popup(blur, clarity, bend);
        UIGlass side = HexGlass.Side(blur, clarity, bend);
        UIGlass window = HexGlass.Window(popup);

        Assert.Equal(popup.Blur, side.Blur);
        Assert.Equal(popup.Blur, window.Blur);
        Assert.Equal(popup.Clarity, side.Clarity);
        Assert.True(side.Bend <= popup.Bend && window.Bend <= popup.Bend);
        Assert.Equal(side.Bend, window.Bend);
    }

    [Fact]
    public void A_window_is_never_clearer_than_the_popups_and_goes_clear_with_the_slider()
    {
        foreach (float clarity in new[] { 0.0f, 0.2f, HexGlass.CLARITY, 0.9f, 1.0f })
            Assert.True(HexGlass.WindowClarity(clarity) <= clarity + 1.0e-6f);

        Assert.Equal(HexGlass.CLARITY * 0.8f, HexGlass.WindowClarity(HexGlass.CLARITY), 4);
        Assert.Equal(1.0f, HexGlass.WindowClarity(1.0f), 4);
    }
}
