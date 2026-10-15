using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Horizon.Hex;

// What the editor is set to (HexSettings), the dialog it is set in, and the edges of its panels that can be dragged.
// The sides of the editor and the panels in them, how wide they were left, the grips they are dragged by and a side
// put away by a drag past its least.
internal sealed partial class HexScene
{
    // The least and the most the panels can be dragged to, in the editor's units
    private const float MIN_SIDE = 180.0f, MAX_SIDE = 640.0f;
    private const float MIN_LAYERS = 40.0f, MAX_LAYERS = 360.0f;

    private ScrollPanel layersScroll = null!;

    // The sizes the layout gives the panels, which is what resetting them goes back to
    private float usualLeft, usualRight, usualLayers;

    /// <summary>
    /// The grip of a side, between its least and its most, and a drag on past the least (to half of it) puts the side away
    /// when it is let go of there, VS Code's way. What it was dragged to is kept only while it is wide enough to keep.
    /// </summary>
    private void SideGrip(UIComponent panel, UIResize edge, bool left)
    {
        Grip(panel, edge, new Vector2(MIN_SIDE * 0.5f, 0.0f), new Vector2(MAX_SIDE, 0.0f), size =>
        {
            // Both halves of a side go with the drag (the tree and the components' list, the properties and the values)
            if (left)
            {
                treeScroll.Size = treeScroll.Size with { X = size.X };
                sideTree.Size = sideTree.Size with { X = size.X };
                if (size.X >= MIN_SIDE) settings.LeftWidth = size.X;
            }
            else
            {
                inspectorScroll.Size = inspectorScroll.Size with { X = size.X };
                valuesTree.Size = valuesTree.Size with { X = size.X };
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
        float width = Math.Clamp(settings.LeftWidth > 0.0f ? settings.LeftWidth : usualLeft, MIN_SIDE, MAX_SIDE);
        treeScroll.Size = treeScroll.Size with { X = width };
        sideTree.Size = sideTree.Size with { X = width };
        if (left.Visible)
            ToggleSideBar();
    }

    /// <summary>The same for the right side, the properties or the values.</summary>
    private void CollapseRight()
    {
        float width = Math.Clamp(settings.RightWidth > 0.0f ? settings.RightWidth : usualRight, MIN_SIDE, MAX_SIDE);
        inspectorScroll.Size = inspectorScroll.Size with { X = width };
        valuesTree.Size = valuesTree.Size with { X = width };
        if (right.Visible)
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

    /// <summary>Helper method to give the panels the sizes the settings have for them, the layout's own where they have none.</summary>
    private void ApplyPanelSizes()
    {
        treeScroll.Size = treeScroll.Size with { X = settings.LeftWidth > 0.0f ? Math.Clamp(settings.LeftWidth, MIN_SIDE, MAX_SIDE) : usualLeft };
        inspectorScroll.Size = inspectorScroll.Size with { X = settings.RightWidth > 0.0f ? Math.Clamp(settings.RightWidth, MIN_SIDE, MAX_SIDE) : usualRight };
        layersScroll.Size = layersScroll.Size with { Y = settings.LayersHeight > 0.0f ? Math.Clamp(settings.LayersHeight, MIN_LAYERS, MAX_LAYERS) : usualLayers };

        left.Visible = !settings.SideBarHidden;
        right.Visible = !settings.PropertiesHidden;
    }

    private void ResetPanels()
    {
        settings.LeftWidth = settings.RightWidth = settings.LayersHeight = settings.PanelHeight = 0.0f;
        bottom.Size = bottom.Size with { Y = 180.0f };
        ApplyPanelSizes();
        SaveSettings();
        Say("the panels are the size they come at");
    }
}
