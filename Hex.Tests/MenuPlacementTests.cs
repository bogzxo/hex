using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Tests;

// Where a list goes against what opened it (y runs up, the bottom of the window is its smallest y)
public sealed class MenuPlacementTests
{
    private static readonly UIRect Window = new(Vector2.Zero, new Vector2(1600.0f, 1032.0f));

    private static bool Inside(UIRect list) =>
        list.Min.X >= Window.Min.X && list.Min.Y >= Window.Min.Y && list.Max.X <= Window.Max.X && list.Max.Y <= Window.Max.Y;

    [Theory]
    [InlineData(10.0f)]
    [InlineData(40.0f)]
    [InlineData(300.0f)]
    public void SubmenuOfAnItemNearTheBottomEdgeStaysInTheWindow(float itemY)
    {
        // The item sits a little off the bottom, its list is taller than the room under it
        var item = new UIRect(new Vector2(300.0f, itemY), new Vector2(520.0f, itemY + 24.0f));
        var parent = new UIRect(new Vector2(280.0f, itemY - 100.0f), new Vector2(540.0f, itemY + 400.0f));
        var size = new Vector2(260.0f, 320.0f);

        UIRect list = MenuPanel.Position(MenuPlacement.BesideItem, size, item, parent, Window);

        Assert.True(Inside(list), $"{list}");
        Assert.Equal(size.X, list.Width, 0.01f);
        Assert.Equal(size.Y, list.Height, 0.01f);
    }

    [Fact]
    public void SubmenuFlipsToTheLeftWhenThereIsNoRoomOnTheRight()
    {
        var item = new UIRect(new Vector2(1300.0f, 20.0f), new Vector2(1590.0f, 44.0f));
        var parent = new UIRect(new Vector2(1290.0f, 0.0f), new Vector2(1595.0f, 400.0f));

        UIRect list = Beside(item, parent, new Vector2(260.0f, 320.0f));

        Assert.True(Inside(list), $"{list}");
        Assert.True(list.Max.X <= parent.Min.X + 6.0f);
    }

    [Fact]
    public void ContextMenuOpenedAtTheBottomGoesUpFromThePoint()
    {
        var point = new UIRect(new Vector2(200.0f, 30.0f), new Vector2(200.0f, 30.0f));

        UIRect list = MenuPanel.Position(MenuPlacement.AtPoint, new Vector2(240.0f, 500.0f), point, default, Window);

        Assert.True(Inside(list), $"{list}");
        Assert.Equal(30.0f, list.Min.Y, 0.01f);
    }

    [Fact]
    public void StatusItemMenuStandsOnTheBarsTopEdge()
    {
        // A status bar item 24 high at the bottom of the window, its menu above it (or under it with no room, never over it)
        var item = new UIRect(new Vector2(1400.0f, 0.0f), new Vector2(1500.0f, 24.0f));
        var size = new Vector2(240.0f, 160.0f);

        UIRect list = MenuPanel.Position(MenuPlacement.Above, size, item, default, Window);

        Assert.True(Inside(list), $"{list}");
        Assert.Equal(24.0f, list.Min.Y, 0.01f);
    }

    [Fact]
    public void StatusItemMenuTallerThanTheRoomAboveIsKeptInTheWindow()
    {
        var item = new UIRect(new Vector2(1590.0f, 0.0f), new Vector2(1600.0f, 24.0f));

        UIRect list = MenuPanel.Position(MenuPlacement.Above, new Vector2(240.0f, 1020.0f), item, default, Window);

        Assert.True(Inside(list), $"{list}");
    }

    private static UIRect Beside(UIRect item, UIRect parent, Vector2 size) =>
        MenuPanel.Position(MenuPlacement.BesideItem, size, item, parent, Window);
}
