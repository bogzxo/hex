using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Shell;

/// <summary>
/// The one handle on the line between two parts of a window, a side and the middle. What is dragged is a list inside the
/// side (so the list keeps its width), and that list's own edge is short of the side's by the side's padding and its
/// scroll bar. Held by its own edge the handle sat inside the side and not between the two. Moved out to the line, half
/// of it lay in the neighbour's column, which the pointer finds first when the neighbour is painted later, and a strip
/// laid over that half made two handles on one line that lit one after the other as the pointer crossed it.
/// <para>
/// So the line has this and nothing else: a strip centred on it, laid over the window above both parts, that takes the
/// press, drags the list's edge the way the list's own grip would, and lights a line on the boundary while the pointer
/// is on it or has hold of it. The list has no grip of its own any more.
/// </para>
/// </summary>
internal sealed class HexSplitter : Panel
{
    // How wide the line is that lights on the boundary
    private const float LINE = 2.0f;

    private readonly Panel line;
    private UIComponent? target, held;
    private UIResize edge;
    private bool lit;

    public HexSplitter()
    {
        Anchor = Origin.TopLeft;
        Pivot = Origin.TopLeft;
        Visible = false;
        line = Add(new Panel { Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Visible = false });
    }

    // There to be pressed whenever there's an edge under it, with nothing drawn of its own
    protected override bool HitTestVisible => target is not null;

    /// <summary>Whether it has hold of an edge right now.</summary>
    public bool Dragging => held is not null;

    /// <summary>Whether its line is lit, the pointer on it or holding it.</summary>
    public bool Lit => lit;

    /// <summary>What it drags, null while there's nothing to.</summary>
    public UIComponent? Target => target;

    /// <summary>
    /// Laid over the boundary for a panel's edge, or put away for no panel.
    /// </summary>
    /// <param name="boundary">Where the line is, right from the window's left edge, and its top down from the window's top.</param>
    /// <param name="hold">How wide the strip is that takes the press, half of it to each side of the line.</param>
    public void Place(UIComponent? panel, UIResize which, Vector2 boundary, float hold, float height, Vector4 accent)
    {
        // What's held is held until it's let go of, whatever the layout does under it
        if (held is null)
        {
            target = panel;
            edge = which;
        }

        bool wanted = target is not null && hold > 0.0f && height > 0.0f;
        if (Visible != wanted)
            Visible = wanted;
        if (!wanted)
        {
            lit = false;
            return;
        }

        var position = new Vector2(boundary.X - hold * 0.5f, -boundary.Y);
        var size = new Vector2(hold, height);
        if (Position != position)
            Position = position;
        if (Size != size)
        {
            Size = size;
            line.Size = new Vector2(LINE, height);
            line.Position = new Vector2((hold - LINE) * 0.5f, 0.0f);
        }

        bool light = IsHovered || held is not null;
        if (light != lit)
        {
            lit = light;
            line.Visible = light;
            line.Color = accent with { W = 0.85f };
        }
    }

    protected override void OnPointerDown(Vector2 point)
    {
        if (target is not { } panel)
            return;

        held = panel;
        panel.BeginResize(edge, point);
    }

    protected override void OnPointerDrag(Vector2 point) => held?.DragResize(point);

    protected override void OnPointerUp(Vector2 point)
    {
        held?.EndResize();
        held = null;
    }
}
