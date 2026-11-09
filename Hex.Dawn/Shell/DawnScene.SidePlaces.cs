using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Where a view of the side bar was left, its selected row and how far down it was scrolled, put back as its rows are made
// again and as the bar comes back to it. The Test Explorer's rows were made again after every run, the selection went
// and the list jumped about, finding a test among a thousand again each time. Only a view whose rows have ids (SideTag)
// is kept this way, its rows say which is which. A view can have something of its own over its rows as well, the Test
// Explorer's search box, up only while the bar is on it.
internal sealed partial class DawnScene
{
    // Where each view was left, by the view, and the view the tree has the rows of now
    private readonly Dictionary<HexSide, (float Scroll, string? Id)> sidePlaces = [];
    private HexSide? placeOf;

    // What views have over their rows
    private readonly List<(HexSide Side, UIComponent Head)> sideHeads = [];

    /// <summary>Called before the side bar's rows are made again, where the rows it has now were left kept.</summary>
    private void KeepSidePlace()
    {
        if (placeOf is { } side)
            sidePlaces[side] = (sideTree.ScrollOffset, (sideTree.Selected?.Tag as SideTag)?.Id);
        placeOf = null;
    }

    /// <summary>Called after a view's rows are made, the row it had selected selected again and the list where it was.</summary>
    private void PutBackSidePlace()
    {
        if (sideTree.Items.Count == 0 || (sideTree.Items[0].Tag as SideTag)?.Id is null)
            return;

        placeOf = Side;
        if (!sidePlaces.TryGetValue(Side, out var place))
            return;

        if (place.Id is { } id)
        {
            for (int i = 0; i < sideTree.VisibleCount; i++)
            {
                UITreeItem row = sideTree.VisibleAt(i);
                if ((row.Tag as SideTag)?.Id == id)
                {
                    sideTree.Selected = row;
                    break;
                }
            }
        }

        // After the selection, which would scroll to it otherwise
        sideTree.ScrollOffset = place.Scroll;
    }

    /// <summary>Something a view has over its rows put there, hidden until the bar is on that view.</summary>
    private void AddSideHead(HexSide side, UIComponent head)
    {
        head.Visible = false;
        left.Add(head);
        left.MoveChild(head, IndexIn(left, sideTree));
        sideHeads.Add((side, head));
    }

    /// <summary>The heads of the views up for the view the bar is on, away for the rest.</summary>
    private void PlaceSideHeads()
    {
        foreach (var (side, head) in sideHeads)
            head.Visible = Side == side;
    }
}
