using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

// The sides of the editor and the panels in them, how wide they were left, the grips they are dragged by and a side
// put away by a drag past its least.
internal sealed partial class DawnScene
{
    // The least and the most the panels can be dragged to, in the editor's units
    private const float MIN_SIDE = 180.0f, MAX_SIDE = 640.0f;
    private const float MIN_LAYERS = 40.0f, MAX_LAYERS = 360.0f;

    private ScrollPanel layersScroll = null!;
    private readonly List<UIComponent> leftGrips = [], rightGrips = [];

    private static bool SideIsResizing(List<UIComponent> grips)
    {
        for (int i = 0; i < grips.Count; i++)
            if (grips[i].IsResizing) return true;
        return false;
    }

    // The sizes the layout gives the panels, which is what resetting them goes back to
    private float usualLeft, usualRight, usualLayers;

    private static float PreferredSide(float saved, float usual) => Math.Clamp(saved > 0 ? saved : usual, MIN_SIDE, MAX_SIDE);

    // Fit the sides for this frame. Their saved widths are what they go back to when the window has room again.
    private (float Left, float Center, float Right) ColumnWidths(float width)
    {
        float leftAmount = left.SlideAmount, rightAmount = right.SlideAmount;
        float visible = leftAmount + rightAmount;
        float available = MathF.Max(0, width - root.Padding.Total.X - activity.Bounds.Width - center.Padding.Total.X
            - left.Padding.Total.X * leftAmount - right.Padding.Total.X * rightAmount);

        // One open side leaves more room for the document. Opening the other shares that room as it slides in.
        float oneSideCenter = Math.Clamp(available * 0.65f, MIN_CENTER, 390);
        float targetCenter = float.Lerp(oneSideCenter, MIN_CENTER, MathF.Min(leftAmount, rightAmount));
        float reserve = Math.Clamp(available - MIN_SIDE * visible, MIN_CENTER, targetCenter);
        float sideBudget = MathF.Max(0, available - reserve);
        float least = visible > 0 ? Math.Clamp(sideBudget / visible, MIN_SIDE * 0.5f, MIN_SIDE) : MIN_SIDE;

        float leftWidth = PreferredSide(settings.LeftWidth, usualLeft);
        float rightWidth = PreferredSide(settings.RightWidth, usualRight);
        float extra = (leftWidth - least) * leftAmount + (rightWidth - least) * rightAmount;
        float share = extra > 0 ? Math.Clamp((sideBudget - least * visible) / extra, 0, 1) : 1;
        leftWidth = SideIsResizing(leftGrips) ? treeScroll.Size.X : MathF.Floor(float.Lerp(least, leftWidth, share));
        rightWidth = SideIsResizing(rightGrips) ? inspectorScroll.Size.X : MathF.Floor(float.Lerp(least, rightWidth, share));
        return (leftWidth, MathF.Max(MIN_CENTER, available - leftWidth * leftAmount - rightWidth * rightAmount), rightWidth);
    }

    /// <summary>
    /// The grip of a side, between its least and its most, and a drag on past the least (to half of it) puts the side away
    /// when it is let go of there, VS Code's way. What it was dragged to is kept only while it is wide enough to keep.
    /// </summary>
    private void SideGrip(UIComponent panel, UIResize edge, bool left)
    {
        (left ? leftGrips : rightGrips).Add(panel);
        Grip(panel, edge, new Vector2(MIN_SIDE * 0.5f, 0.0f), new Vector2(MAX_SIDE, 0.0f), size =>
        {
            // Both halves of a side go with the drag (the tree and the components' list, the properties and the LastRun)
            if (left)
            {
                treeScroll.Size = treeScroll.Size with { X = size.X };
                sideTree.Size = sideTree.Size with { X = size.X };
                if (size.X >= MIN_SIDE) settings.LeftWidth = size.X;
            }
            else
            {
                inspectorScroll.Size = inspectorScroll.Size with { X = size.X };
                running.ValuesTree.Size = running.ValuesTree.Size with { X = size.X };
                if (size.X >= MIN_SIDE) settings.RightWidth = size.X;
            }
        });
        panel.OnResizeEnded = size =>
        {
            if (size.X < MIN_SIDE)
            {
                if (left) CollapseLeft();
                else CollapseRight();
            }

            SaveSettings();
        };

        // Dragged by the side's own handle on the line between the side and the middle (HexSplitter), which sizes it the
        // way a grip of its own would. With one of its own as well there were two handles to a line
        panel.Resizable = UIResize.None;
    }

    /// <summary>Puts the left side away after a drag past its least, as wide again as it was kept when it comes back.</summary>
    private void CollapseLeft()
    {
        float width = PreferredSide(settings.LeftWidth, usualLeft);
        treeScroll.Size = treeScroll.Size with { X = width };
        sideTree.Size = sideTree.Size with { X = width };
        if (left.Shown)
            ToggleSideBar();
    }

    /// <summary>The same for the right side, the properties or the LastRun.</summary>
    private void CollapseRight()
    {
        float width = PreferredSide(settings.RightWidth, usualRight);
        inspectorScroll.Size = inspectorScroll.Size with { X = width };
        running.ValuesTree.Size = running.ValuesTree.Size with { X = width };
        if (right.Shown)
            ToggleProperties();
    }

    // How wide the strip is that the pointer takes hold of an edge in, half of it to each side of the line
    private const float GRIP_HOLD = 10.0f;

    // The handles of the two sides, each the only one on its line (HexSplitter)
    private Hex.Shell.HexSplitter? leftSplitter, rightSplitter;

    /// <summary>The handles of the sides for the tests, the left's and the right's.</summary>
    internal (Hex.Shell.HexSplitter? Left, Hex.Shell.HexSplitter? Right) Splitters => (leftSplitter, rightSplitter);

    /// <summary>
    /// Every tick, the handle of each side laid exactly on the line between the side and the middle, for whichever list
    /// of that side is showing. One handle a line: the lists have no grips of their own, several of them at one line was
    /// handles lighting over each other as the pointer went across.
    /// </summary>
    private void CentreGrips()
    {
        leftSplitter ??= chrome.Root.Add(new Hex.Shell.HexSplitter());
        rightSplitter ??= chrome.Root.Add(new Hex.Shell.HexSplitter());

        UIRect window = chrome.Root.Bounds;
        Vector4 accent = compositor.Skin?.AccentColor ?? Vector4.One;
        bool ready = compositor.Skin is not null && !window.IsEmpty;

        leftSplitter.Place(ready && left.Shown ? ShownGrip(leftGrips) : null, UIResize.Right,
            new Vector2(left.Bounds.Max.X - window.Min.X, window.Max.Y - left.Bounds.Max.Y), GRIP_HOLD, left.Bounds.Height, accent);
        rightSplitter.Place(ready && right.Shown ? ShownGrip(rightGrips) : null, UIResize.Left,
            new Vector2(right.Bounds.Min.X - window.Min.X, window.Max.Y - right.Bounds.Max.Y), GRIP_HOLD, right.Bounds.Height, accent);
    }

    // The list of a side that is showing, which is the one a drag of the side's edge sizes: the widest of them when
    // several are (the properties and a page inside them), a drag is counted from how wide what's held was. Null with none
    private static UIComponent? ShownGrip(List<UIComponent> grips)
    {
        UIComponent? widest = null;
        for (int i = 0; i < grips.Count; i++)
        {
            if (grips[i].IsShownInHierarchy && !grips[i].Bounds.IsEmpty && (widest is null || grips[i].Bounds.Width > widest.Bounds.Width))
                widest = grips[i];
        }

        return widest;
    }

    private void Grip(UIComponent panel, UIResize edge, Vector2 least, Vector2 most, Action<Vector2> keep)
    {
        panel.Resizable = edge;
        panel.MinSize = least;
        panel.MaxSize = most;

        // Half the strip to each side of the edge, so it's held on the line between this and what's next to it
        panel.GripSize = GRIP_HOLD * 0.5f;
        panel.GripReach = GRIP_HOLD * 0.5f;
        panel.OnResized = keep;
        panel.OnResizeEnded = _ => SaveSettings();
    }

    /// <summary>
    /// Helper method to give the panels the sizes the settings have for them, the layout's own where they have none, and
    /// the sides shown or not. At once as Hex starts, sliding after.
    /// </summary>
    private void ApplyPanelSizes(bool instantly = false)
    {
        treeScroll.Size = treeScroll.Size with { X = PreferredSide(settings.LeftWidth, usualLeft) };
        inspectorScroll.Size = inspectorScroll.Size with { X = PreferredSide(settings.RightWidth, usualRight) };
        layersScroll.Size = layersScroll.Size with { Y = settings.LayersHeight > 0.0f ? Math.Clamp(settings.LayersHeight, MIN_LAYERS, MAX_LAYERS) : usualLayers };

        left.Show(!settings.SideBarHidden, instantly);
        right.Show(!settings.PropertiesHidden, instantly);
    }

    private void ResetPanels()
    {
        settings.LeftWidth = settings.RightWidth = settings.LayersHeight = settings.PanelHeight = 0.0f;
        bottom.Size = bottom.Size with { Y = 180.0f };
        ApplyPanelSizes();
        SaveSettings();
        Say(Said("sides.panels_reset"));
    }
}
