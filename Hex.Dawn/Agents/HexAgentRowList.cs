using Horizon.UI.Components;

namespace Hex.Dawn;

/// <summary>
/// The rows of the Agents view as a list the panel puts rows in and takes them out of, the way it did with a stack, shown
/// through a <see cref="VirtualStack"/> that has only the ones near what's in sight in its tree. A long chat lays out,
/// measures and paints the few dozen rows on screen, and a chat put on screen makes components for those alone, where
/// every row was measured every frame and all of them were made at once. A row keeps its height by a key of its own, and
/// a row made again in its place keeps its key, so the list doesn't move as it's swapped.
/// </summary>
internal sealed class HexAgentRowList
{
    private readonly List<UIComponent> rows = [];
    private readonly Dictionary<UIComponent, long> keys = new(ReferenceEqualityComparer.Instance);
    private long made;

    public HexAgentRowList(VirtualStack shown)
    {
        Shown = shown;
        shown.Make = index => rows[index];
        shown.KeyOf = index => keys[rows[index]];
        // A row of nothing (a call its group's row draws) takes no room before it's ever been seen either
        shown.EstimateHeight = (index, _) => rows[index].Visible ? shown.Estimate : 0.0f;
    }

    /// <summary>The list on screen, which has the rows near what's in sight among its children.</summary>
    public VirtualStack Shown { get; }

    /// <summary>Every row, in order, in sight or not.</summary>
    public IReadOnlyList<UIComponent> Children => rows;

    /// <summary>Where a row is, the count of rows for one that isn't one.</summary>
    public int IndexOf(UIComponent row)
    {
        int at = rows.IndexOf(row);
        return at < 0 ? rows.Count : at;
    }

    public T Add<T>(T row) where T : UIComponent => Insert(rows.Count, row);

    /// <summary>
    /// A row put in at a place. <paramref name="of"/> is what it shows (a chat's entry), whose height it's known by, so a
    /// chat gone back to has its rows the heights they were and lands where it was read to.
    /// </summary>
    public T Insert<T>(int index, T row, object? of = null) where T : UIComponent
    {
        index = Math.Clamp(index, 0, rows.Count);
        rows.Insert(index, row);
        // The thing's own number above every count made here, which never gets near it
        keys[row] = of is null ? ++made : (1L << 40) + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(of);
        Moved(index);
        return row;
    }

    public bool Remove(UIComponent row)
    {
        int at = rows.IndexOf(row);
        if (at < 0)
            return false;

        // In the list on screen it's taken out of its tree with the others the list asks for again
        Moved(at);
        rows.RemoveAt(at);
        keys.Remove(row);
        Shown.ItemCount = rows.Count;
        if (row.Parent == Shown)
            Shown.Remove(row);
        return true;
    }

    /// <summary>A row put in another's place, keeping its place's height until it's measured itself.</summary>
    public void Replace(UIComponent was, UIComponent now)
    {
        int at = rows.IndexOf(was);
        if (at < 0)
        {
            Add(now);
            return;
        }

        Shown.Refresh(at);
        rows[at] = now;
        keys[now] = keys[was];
        keys.Remove(was);
        if (was.Parent == Shown)
            Shown.Remove(was);
    }

    public void MoveChild(UIComponent row, int index)
    {
        if (!Remove(row))
            return;
        Insert(Math.Min(index, rows.Count), row);
    }

    /// <summary>Every row taken out but the ones kept.</summary>
    public void Clear(UIComponent? keep = null)
    {
        Shown.Rebind();
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            if (rows[i] == keep)
                continue;
            keys.Remove(rows[i]);
            rows.RemoveAt(i);
        }

        Shown.ItemCount = rows.Count;
        Shown.ItemsChanged();
    }

    // Rows numbered from here on are other rows now, the ones on screen from there are asked for again
    private void Moved(int from)
    {
        if (from <= Shown.Realized.Last)
            Shown.Rebind();
        Shown.ItemCount = rows.Count;
        Shown.ItemsChanged();
    }
}
