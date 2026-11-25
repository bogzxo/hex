using System.Numerics;
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
    private const int MAX_POOLED_PLACEHOLDERS = 300;

    private readonly List<UIComponent> rows = [];
    private readonly List<long> rowKeys = [];
    private long made;

    // Pooled lightweight placeholders reused across chat switches so switching a 200-row chat does not allocate 200 new Panels.
    private readonly Stack<Panel> waitingPool = [];
    private readonly Stack<Panel> groupedPool = [];
    private readonly HashSet<UIComponent> pooledWaiting = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<UIComponent> pooledGrouped = new(ReferenceEqualityComparer.Instance);

    // The rows still to be made, by the place each holds, made as it comes into sight. Either a bespoke factory or a
    // shared callback (MakeLater) with the item tag, avoiding a closure allocation per row when switching chats.
    private readonly Dictionary<UIComponent, (object? Of, Func<UIComponent>? Make)> waiting = new(ReferenceEqualityComparer.Instance);
    private int batchDepth;
    private int batchFrom = int.MaxValue;

    /// <summary>Shared lazy row factory invoked when a row was queued with <see cref="InsertLater(int, UIComponent, object)"/>.</summary>
    public Func<object, UIComponent, UIComponent>? MakeLater { get; set; }

    public HexAgentRowList(VirtualStack shown)
    {
        Shown = shown;
        shown.Make = Make;
        shown.KeyOf = index => rowKeys[index];
        // A row of nothing (a call its group's row draws) takes no room before it's ever been seen either
        shown.EstimateHeight = (index, _) => rows[index].Visible ? shown.Estimate : 0.0f;
    }

    /// <summary>Rents a zero-size placeholder panel for a lazy row waiting to be materialized on scroll.</summary>
    public Panel RentWaitingPlace()
    {
        if (waitingPool.Count > 0)
        {
            Panel reused = waitingPool.Pop();
            reused.Visible = true;
            reused.Size = Vector2.Zero;
            reused.Color = Vector4.Zero;
            return reused;
        }
        var created = new Panel { Size = Vector2.Zero, Color = Vector4.Zero };
        pooledWaiting.Add(created);
        return created;
    }

    /// <summary>Rents a hidden zero-size placeholder panel for a tool call drawn by its group's header row.</summary>
    public Panel RentGroupedPlace()
    {
        if (groupedPool.Count > 0)
        {
            Panel reused = groupedPool.Pop();
            reused.Visible = false;
            reused.Size = Vector2.Zero;
            return reused;
        }
        var created = new Panel { Visible = false, Size = Vector2.Zero };
        pooledGrouped.Add(created);
        return created;
    }

    private void ReturnIfPooled(UIComponent component)
    {
        if (component is not Panel panel || panel.Parent is not null || panel.Children.Count > 0)
            return;
        if (pooledWaiting.Contains(panel))
        {
            if (waitingPool.Count < MAX_POOLED_PLACEHOLDERS)
                waitingPool.Push(panel);
            else
                pooledWaiting.Remove(panel);
        }
        else if (pooledGrouped.Contains(panel))
        {
            if (groupedPool.Count < MAX_POOLED_PLACEHOLDERS)
                groupedPool.Push(panel);
            else
                pooledGrouped.Remove(panel);
        }
    }

    /// <summary>Defers <see cref="VirtualStack.ItemsChanged"/> notifications until <see cref="EndBatch"/> so bulk chat switches notify once.</summary>
    public void BeginBatch()
    {
        batchDepth++;
    }

    /// <summary>Flushes any deferred notifications from <see cref="BeginBatch"/>.</summary>
    public void EndBatch()
    {
        if (batchDepth <= 0 || --batchDepth > 0)
            return;
        if (batchFrom == int.MaxValue)
            return;
        int from = batchFrom;
        batchFrom = int.MaxValue;
        Moved(from);
    }

    // A row as the list on screen asks for it, made now if it was waiting to be
    private UIComponent Make(int index)
    {
        UIComponent place = rows[index];
        if (!waiting.Remove(place, out var pending))
            return place;

        UIComponent made = pending.Make is not null
            ? pending.Make()
            : MakeLater!(pending.Of!, place);
        rows[index] = made;
        ReturnIfPooled(place);
        return made;
    }

    /// <summary>
    /// A row put in at a place to be made only once it comes into sight, <paramref name="place"/> holding its place until
    /// then. Uses <see cref="MakeLater"/> when <paramref name="make"/> is omitted to avoid per-row closure allocations.
    /// </summary>
    public UIComponent InsertLater(int index, UIComponent place, object of, Func<UIComponent>? make = null)
    {
        waiting[place] = (of, make);
        return Insert(index, place, of);
    }

    /// <summary>Whether a row is still a place waiting to be made, which is made from what its entry is when it is.</summary>
    public bool IsWaiting(UIComponent row) => waiting.ContainsKey(row);

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

    /// <summary>Where the row showing a thing (a chat's entry) is, -1 when none does.</summary>
    public int IndexOfThing(object of)
    {
        long key = (1L << 40) + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(of);
        for (int i = 0; i < rowKeys.Count; i++)
        {
            if (rowKeys[i] == key)
                return i;
        }

        return -1;
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
        rowKeys.Insert(index, of is null ? ++made : (1L << 40) + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(of));
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
        rowKeys.RemoveAt(at);
        waiting.Remove(row);
        Shown.ItemCount = rows.Count;
        if (row.Parent == Shown)
            Shown.Remove(row);
        ReturnIfPooled(row);
        return true;
    }

    /// <summary>Removes a contiguous range of rows (e.g. spilled prefix) in one shot without clearing the rest.</summary>
    public void RemoveRange(int index, int count)
    {
        if (count <= 0 || index < 0 || index >= rows.Count)
            return;
        int take = Math.Min(count, rows.Count - index);
        for (int i = index; i < index + take; i++)
        {
            UIComponent row = rows[i];
            waiting.Remove(row);
            if (row.Parent == Shown)
                Shown.Remove(row);
            ReturnIfPooled(row);
        }
        rows.RemoveRange(index, take);
        rowKeys.RemoveRange(index, take);
        Moved(index);
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
        waiting.Remove(was);
        if (was.Parent == Shown)
            Shown.Remove(was);
        ReturnIfPooled(was);
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
            UIComponent row = rows[i];
            if (row == keep)
                continue;
            waiting.Remove(row);
            rows.RemoveAt(i);
            rowKeys.RemoveAt(i);
            ReturnIfPooled(row);
        }

        Shown.ItemCount = rows.Count;
        Shown.ItemsChanged();
    }

    // Rows numbered from here on are other rows now, the ones on screen from there are asked for again
    private void Moved(int from)
    {
        if (batchDepth > 0)
        {
            if (from < batchFrom)
                batchFrom = from;
            return;
        }
        int last = Shown.Realized.Last;
        if (from <= last)
        {
            // Appending a streaming row just before 1-2 tail indicators (typing dots / waiting banner) only shifts those
            // tail indices; refreshing just that tail avoids detaching and re-measuring the entire visible viewport.
            if (last - from <= 2 && from >= Shown.Realized.First)
            {
                for (int i = from; i <= last; i++)
                    Shown.Refresh(i);
            }
            else
                Shown.Rebind();
        }
        Shown.ItemCount = rows.Count;
        Shown.ItemsChanged();
    }
}
