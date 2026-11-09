using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Something in a list that scrolls and out of the part of it that shows, the Problems panel's rows scrolled away. They
// were listed where they sat with the list scrolled, over the code, and a click on one by its id went to the code. They
// say they're scrolled away now, and a click (or a hover) on one scrolls the list to it first, the way a tree's row is
// revealed.
internal sealed partial class DawnScene
{
    /// <summary>The list a component is scrolled out of view of, its nearest scrolling panel with its middle outside what that shows, null when it shows.</summary>
    private static ScrollPanel? McpScrolledAway(UIComponent component)
    {
        for (UIComponent? above = component.Parent; above is not null; above = above.Parent)
        {
            if (above is not ScrollPanel scroll)
                continue;

            UIRect shows = scroll.Bounds.Shrink(scroll.Padding);
            float middle = component.Bounds.Center.Y;
            return !component.Bounds.IsEmpty && (middle < shows.Min.Y || middle > shows.Max.Y) ? scroll : null;
        }

        return null;
    }

    /// <summary>
    /// Where a component will be once it's scrolled into view, its list scrolled to it first when it's out of it. The
    /// list is laid out again on the next tick, so it's where it is now moved by however far that scrolled.
    /// </summary>
    private static UIRect McpBroughtIntoView(UIComponent component)
    {
        UIRect bounds = component.Bounds;
        if (McpScrolledAway(component) is not { } scroll)
            return bounds;

        float before = scroll.Offset;
        scroll.ScrollIntoView(component);
        var moved = new System.Numerics.Vector2(0.0f, scroll.Offset - before);
        return new UIRect(bounds.Min + moved, bounds.Max + moved);
    }
}
