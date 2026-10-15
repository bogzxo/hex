using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Skinning;

namespace Horizon.Hex;

// What is selected in the canvas dragged bigger by its edges, and moves landing on the grid.
internal sealed partial class HexScene
{
    // How near an edge of what is selected in the canvas the pointer has hold of it, inside and out, in the layout's units
    private const float HANDLE_INSIDE = 7.0f, HANDLE_OUTSIDE = 5.0f;

    // What is being dragged bigger in the canvas, null for nothing
    private UIComponent? resizing;

    /// <summary>
    /// Helper method to let the one thing that is selected be dragged bigger by its edges, the tabs on them are the
    /// compositor's to draw. Returns whether the pointer is busy with that, in which case it is nobody else's.
    /// </summary>
    private bool UpdateCanvasHandles(UIPointer pointer, Vector2 inLayout, bool overCanvas, bool pressed)
    {
        if (resizing is { } held)
        {
            if (pointer.Down)
            {
                if (held.DragResize(inLayout, settings.Snap))
                    codeDirty = true;
            }
            else
            {
                held.EndResize();
                resizing = null;

                // The numbers in the inspector are from before the drag
                inspectorDirty = true;
                stage.HighlightLitGrips = UIResize.None;
            }

            return true;
        }

        UIComponent? only = settings.CanvasHandles && document.Selection.Count == 1 ? document.Selected : null;
        stage.HighlightGrips = only is null ? UIResize.None : UIResize.All;

        UIResize on = only is not null && overCanvas ? only.EdgesAt(inLayout, UIResize.All, HANDLE_INSIDE, HANDLE_OUTSIDE) : UIResize.None;
        stage.HighlightLitGrips = on;

        if (!pressed || on == UIResize.None)
            return false;

        only!.BeginResize(on, inLayout);
        stage.HighlightLitGrips = on;
        resizing = only;
        return true;
    }

    /// <summary>Helper method to have a move land on the grid, if there is one. What is left over stays with the pointer.</summary>
    private Vector2 Snapped(Vector2 moved)
    {
        if (settings.Snap <= 0.0f)
            return moved;

        return new Vector2(MathF.Truncate(moved.X / settings.Snap), MathF.Truncate(moved.Y / settings.Snap)) * settings.Snap;
    }
}
