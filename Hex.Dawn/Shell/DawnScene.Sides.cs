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

    private void Grip(UIComponent panel, UIResize edge, Vector2 least, Vector2 most, Action<Vector2> keep)
    {
        panel.Resizable = edge;
        panel.MinSize = least;
        panel.MaxSize = most;
        panel.GripSize = 2.0f;
        panel.GripReach = 8.0f;
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
