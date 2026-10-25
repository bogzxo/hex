using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The pointer on the canvas. A click selects what's under it, a drag moves what's selected, and where it is in the layout
// is in the status bar. The edges of what's selected are Handles, zooming and panning Canvas, a double click Texts.
internal sealed partial class HexDesigner
{
    // Dragging a component around the canvas
    private bool pointerWasDown;
    internal bool dragging, dragMoved;
    private Vector2 dragLast;

    // Something that was selected already and pressed on, it's all that's selected if it isn't dragged
    private UIComponent? clickedSelected;

    /// <summary>
    /// Clicking in the canvas selects what is there (on top of what already is with ctrl or shift), dragging moves all
    /// of it. The wheel zooms, the middle button (or space) pans, and a click on a tab opens its page.
    /// </summary>
    internal void UpdateCanvasPointer()
    {
        UIPointer pointer = compositor.Pointer;

        // The code has no canvas to point at
        if (!LayoutTools)
        {
            pointerWasDown = pointer.Down;
            dragging = false;
            ShowCoords(false, default);
            return;
        }

        bool pressed = pointer.Down && !pointerWasDown;
        bool released = !pointer.Down && pointerWasDown;
        pointerWasDown = pointer.Down;

        // Where the pointer is in the editor, and in the layout that is being edited however big that is drawn
        Vector2 inChrome = chrome.ToLocal(pointer.Position);
        Vector2 inLayout = document.Module.ToLocal(pointer.Position);

        // Not over the canvas if a menu of the editor is in the way of it
        // Nor if a question or the menu of art is up over it, a click on those is not a click on the layout
        // Nor on the bottom edge of the canvas itself, which is dragged to make it taller
        bool overCanvas = PointerOnCanvas(pointer.Position) && chrome.Popup is null && chrome.Dialog is null && artPicker is not { IsOpen: true }
            && canvas.ActiveGrip == UIResize.None && !canvas.IsResizing;

        // Where the pointer is in the layout, in its own units, for lining things up by number
        ShowCoords(overCanvas, inLayout);

        // A double click edits the text of what's under it, worked out alongside everything below (see Texts)
        TrackClicks(pointer, pressed, released, overCanvas, inLayout);

        // The hover of what is pointed at, so it can be seen while it is set up. Not while it is being dragged about
        HoldHover(overCanvas && !dragging ? document.Module.FindAt(pointer.Position) : null);

        if (UpdateCanvasView(inChrome, overCanvas, pressed))
        {
            dragging = false;
            return;
        }

        // An edge of what is selected, dragged to make it bigger, before the press is taken for anything else
        if (!dragging && UpdateCanvasHandles(pointer, inLayout, overCanvas, pressed))
            return;

        if (pressed && overCanvas)
        {
            UIComponent? under = document.Module.FindAt(pointer.Position);

            // A tab in the canvas opens its page, the way it would in a game, and selects the tabs
            if (TabUnder(under, inLayout) is var (tabs, index))
            {
                tabs.Selected = index;
                Select(document.OwnerOf(tabs) ?? tabs);
                return;
            }

            // What stands in for the items of a container is the container as far as selecting goes
            UIComponent? hit = ResolveClick(document.OwnerOf(under));

            // Pressing on something that's selected already drags all of the selection, a click without a drag
            // selects only it (see below)
            clickedSelected = hit is not null && document.IsSelected(hit) && !Modifier() ? hit : null;
            if (clickedSelected is null)
                Pick(hit);

            dragging = document.Selection.Count > 0 && (hit is null || document.IsSelected(hit));
            dragMoved = false;
            dragLast = inLayout;
        }
        else if (dragging && pointer.Down)
        {
            // On the grid if there is one, what the pointer moved past the last line of it waits for the next
            Vector2 moved = Snapped(inLayout - dragLast);
            if (moved != Vector2.Zero)
            {
                foreach (var selected in document.TopSelection())
                    selected.Position += moved;

                dragLast += moved;
                dragMoved = true;
                codeDirty = true;
            }
        }

        if (released && dragging)
        {
            dragging = false;

            if (!dragMoved && clickedSelected is { } only)
                Select(only);
            clickedSelected = null;

            // The numbers in the inspector are from before the drag
            inspectorDirty = true;
        }
    }

    // Where the pointer was last said to be, in whole units of the layout, null for off the canvas
    private (int X, int Y)? coordsShown;

    /// <summary>Says where the pointer is in the layout, made into text only when it has moved a whole unit.</summary>
    private void ShowCoords(bool over, Vector2 at)
    {
        (int X, int Y)? now = over ? ((int)MathF.Round(at.X), (int)MathF.Round(at.Y)) : null;
        if (now == coordsShown)
            return;

        coordsShown = now;
        coordsItem.Text = now is var (x, y) ? $"$(location) {x}, {y}" : string.Empty;
    }

    /// <summary>
    /// Whether a point is on the canvas with nothing of the editor's own over it. The canvas is a hole in the editor its
    /// layouts are seen through, the innermost thing of the editor there is the canvas itself, and anything else found
    /// there (a little window like the settings, what is in it) is in front of it and has the pointer, not the layout under it.
    /// </summary>
    private bool PointerOnCanvas(Vector2 world)
    {
        if (!canvas.Bounds.Contains(chrome.ToLocal(world)))
            return false;

        UIComponent? over = chrome.FindAt(world);
        return over is null || over == canvas;
    }
}
