using System.Text;

namespace Hex.Core;

internal readonly record struct HexDocId(long Clock, int Author) : IComparable<HexDocId>
{
    public int CompareTo(HexDocId other)
    {
        int clocks = Clock.CompareTo(other.Clock);
        return clocks == 0 ? Author.CompareTo(other.Author) : clocks;
    }
}

// Offsets are UTF-16 boundaries in an immutable run. The default point is the start of the seed.
internal readonly record struct HexDocPoint(HexDocId Run, int Offset);
internal readonly record struct HexDocSpan(HexDocId Run, int Start, int Length);
internal readonly record struct HexDocPositionSpan(HexDocId Run, int Start, int End, int Offset, bool Visible);
internal abstract record HexDocOperation(HexDocId Id);
internal sealed record HexDocEdit(HexDocId Id, HexDocPoint After, string Inserted, HexDocSpan[] Removed) : HexDocOperation(Id);
internal sealed record HexDocVisibility(HexDocId Id, HexDocId Edit, bool Enabled) : HexDocOperation(Id);

internal enum HexDocApply { Accepted, Duplicate, MissingDependency }

/// <summary>
/// Replicated growable array with immutable text runs. Insertions at a boundary are visited in descending operation
/// order before the original continuation. Deletions hide only the named atoms; hidden runs retain their children.
/// Concurrent operations commute once their referenced operations are present.
/// </summary>
internal sealed class HexLinkDocumentSequence
{
    internal const int MaxStoredUnits = 8 * 1024 * 1024;
    internal const int MaxOperations = 65_536;
    internal const int MaxDeletionSpans = 262_144;

    private readonly Dictionary<HexDocId, Run> runs = [];
    private readonly Dictionary<HexDocId, HexDocOperation> operations = [];
    private readonly Dictionary<HexDocId, HexDocVisibility> visibility = [];
    private static readonly IComparer<Run> NewestFirst = Comparer<Run>.Create(static (left, right) => right.Id.CompareTo(left.Id));
    private HexDocView? view;
    private int storedUnits, deletionSpans;

    private sealed class Run(HexDocId id, string text)
    {
        public HexDocId Id { get; } = id;
        public string Text { get; } = text;
        public SortedList<int, List<Run>>? Children;
        public List<Deletion>? Deletions;
    }

    private readonly record struct Deletion(int Start, int End, HexDocId Edit);
    private readonly record struct Interval(int Start, int End);
    private readonly record struct Visit(Run Run, int Start, int End, bool Expand);

    public HexLinkDocumentSequence(string seed)
    {
        CheckText(seed);
        runs.Add(default, new Run(default, seed));
        storedUnits = seed.Length;
        view = new HexDocView(seed, seed.Length == 0 ? [] : [new HexDocSpan(default, 0, seed.Length)]);
    }

    public long Clock { get; private set; }
    public int RunCount => runs.Count;
    public int StoredUnits => storedUnits;
    public int OperationCount => operations.Count;

    public HexDocApply Apply(HexDocOperation operation)
    {
        if (operation.Id is not { Clock: > 0, Author: > 0 } || operation.Id.Clock >= long.MaxValue - 1)
            throw new ArgumentException("Invalid document operation identity.", nameof(operation));
        if (operations.TryGetValue(operation.Id, out var known))
        {
            if (!Same(known, operation))
                throw new ArgumentException("The document operation ID already has a different payload.", nameof(operation));
            return HexDocApply.Duplicate;
        }

        if (operations.Count >= MaxOperations)
            throw new InvalidOperationException("The document operation limit requires a new checkpoint.");

        switch (operation)
        {
            case HexDocEdit edit:
                if (!ApplyEdit(edit))
                    return HexDocApply.MissingDependency;
                operation = edit with { Removed = (HexDocSpan[])edit.Removed.Clone() };
                break;
            case HexDocVisibility toggle:
                if (toggle.Edit.Author != toggle.Id.Author || toggle.Edit.Clock <= 0 || toggle.Edit.Clock >= toggle.Id.Clock)
                    throw new ArgumentException("Only an author may change the visibility of an earlier edit.", nameof(operation));
                if (!operations.TryGetValue(toggle.Edit, out var target))
                    return HexDocApply.MissingDependency;
                if (target is not HexDocEdit)
                    throw new ArgumentException("Only text edits can be undone.", nameof(operation));
                if (!visibility.TryGetValue(toggle.Edit, out var previous) || previous.Id.CompareTo(toggle.Id) < 0)
                    visibility[toggle.Edit] = toggle;
                break;
            default:
                throw new ArgumentException("Unknown document operation.", nameof(operation));
        }

        operations.Add(operation.Id, operation);
        Clock = Math.Max(Clock, operation.Id.Clock);
        view = null;
        return HexDocApply.Accepted;
    }

    private bool ApplyEdit(HexDocEdit edit)
    {
        CheckText(edit.Inserted);
        if (edit.Inserted.Length == 0 && edit.Removed.Length == 0)
            throw new ArgumentException("A document edit needs an insertion or deletion.", nameof(edit));
        if (edit.Inserted.Length > MaxStoredUnits - storedUnits || edit.Removed.Length > MaxDeletionSpans - deletionSpans)
            throw new InvalidOperationException("The document storage limit requires a new checkpoint.");

        if (!FindRun(edit.Id, edit.After.Run, out Run? parent))
            return false;
        if (!Boundary(parent!.Text, edit.After.Offset) || edit.After.Run != default && edit.After.Offset == 0)
            throw new ArgumentException("Invalid document insertion boundary.", nameof(edit));
        foreach (HexDocSpan span in edit.Removed)
        {
            if (!FindRun(edit.Id, span.Run, out Run? run))
                return false;
            if (span.Length <= 0 || span.Start < 0 || span.Start > run!.Text.Length
                || span.Length > run.Text.Length - span.Start || !Boundary(run.Text, span.Start) || !Boundary(run.Text, span.Start + span.Length))
                throw new ArgumentException("Invalid document deletion span.", nameof(edit));
        }

        if (edit.Inserted.Length != 0)
        {
            var inserted = new Run(edit.Id, edit.Inserted);
            runs.Add(edit.Id, inserted);
            var children = parent.Children ??= [];
            if (!children.TryGetValue(edit.After.Offset, out var siblings))
                children.Add(edit.After.Offset, siblings = []);
            int index = siblings.BinarySearch(inserted, NewestFirst);
            siblings.Insert(~index, inserted);
            storedUnits += edit.Inserted.Length;
        }

        foreach (HexDocSpan span in edit.Removed)
        {
            var deleted = runs[span.Run].Deletions ??= [];
            deleted.Add(new Deletion(span.Start, span.Start + span.Length, edit.Id));
        }
        deletionSpans += edit.Removed.Length;
        return true;
    }

    private bool FindRun(HexDocId edit, HexDocId referenced, out Run? run)
    {
        if (referenced != default && (referenced.Author <= 0 || referenced.Clock <= 0 || referenced.Clock >= edit.Clock))
            throw new ArgumentException("A document edit can only reference an earlier run.");
        return runs.TryGetValue(referenced, out run);
    }

    public HexDocView View()
    {
        if (view is { } current)
            return current;
        var text = new StringBuilder();
        var visible = new List<HexDocSpan>();
        var positions = new List<HexDocPositionSpan>();
        var visits = new Stack<Visit>();
        visits.Push(new Visit(runs[default], 0, 0, true));
        Dictionary<HexDocId, Interval[]>? deletions = null;
        while (visits.TryPop(out Visit visit))
        {
            if (visit.Expand)
            {
                Schedule(visit.Run, visits);
                continue;
            }
            if (!Enabled(visit.Run.Id))
            {
                positions.Add(new(visit.Run.Id, visit.Start, visit.End, text.Length, false));
                continue;
            }

            int at = visit.Start;
            if (visit.Run.Deletions is not null)
            {
                deletions ??= [];
                if (!deletions.TryGetValue(visit.Run.Id, out Interval[]? ranges))
                    deletions.Add(visit.Run.Id, ranges = ActiveDeletions(visit.Run.Deletions));
                for (int i = FirstEndAfter(ranges, at); i < ranges.Length && ranges[i].Start < visit.End; i++)
                {
                    Append(at, ranges[i].Start);
                    int hiddenStart = Math.Max(at, ranges[i].Start);
                    int hiddenEnd = Math.Min(visit.End, ranges[i].End);
                    if (hiddenStart < hiddenEnd)
                        positions.Add(new(visit.Run.Id, hiddenStart, hiddenEnd, text.Length, false));
                    at = Math.Max(at, ranges[i].End);
                }
            }
            Append(at, visit.End);

            void Append(int from, int to)
            {
                if (from >= to)
                    return;
                positions.Add(new(visit.Run.Id, from, to, text.Length, true));
                text.Append(visit.Run.Text.AsSpan(from, to - from));
                visible.Add(new HexDocSpan(visit.Run.Id, from, to - from));
            }
        }

        return view = new HexDocView(text.ToString(), [.. visible], [.. positions]);
    }

    private static void Schedule(Run run, Stack<Visit> visits)
    {
        int end = run.Text.Length;
        if (run.Children is { } children)
        {
            for (int i = children.Count - 1; i >= 0; i--)
            {
                int anchor = children.Keys[i];
                if (anchor < end)
                    visits.Push(new Visit(run, anchor, end, false));
                List<Run> siblings = children.Values[i];
                for (int child = siblings.Count - 1; child >= 0; child--)
                    visits.Push(new Visit(siblings[child], 0, 0, true));
                end = anchor;
            }
        }
        if (end > 0)
            visits.Push(new Visit(run, 0, end, false));
    }

    private Interval[] ActiveDeletions(List<Deletion> marks)
    {
        var active = new List<Interval>();
        foreach (Deletion mark in marks)
            if (Enabled(mark.Edit))
                active.Add(new Interval(mark.Start, mark.End));
        if (active.Count < 2)
            return [.. active];

        active.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        int written = 0;
        for (int i = 0; i < active.Count; i++)
        {
            Interval interval = active[i];
            if (written > 0 && interval.Start <= active[written - 1].End)
                active[written - 1] = active[written - 1] with { End = Math.Max(active[written - 1].End, interval.End) };
            else
                active[written++] = interval;
        }
        active.RemoveRange(written, active.Count - written);
        return [.. active];
    }

    private static int FirstEndAfter(Interval[] ranges, int offset)
    {
        int lower = 0, upper = ranges.Length;
        while (lower < upper)
        {
            int middle = (lower + upper) / 2;
            if (ranges[middle].End <= offset)
                lower = middle + 1;
            else
                upper = middle;
        }
        return lower;
    }

    private bool Enabled(HexDocId edit) => !visibility.TryGetValue(edit, out var toggle) || toggle.Enabled;

    private static bool Same(HexDocOperation left, HexDocOperation right) => (left, right) switch
    {
        (HexDocEdit a, HexDocEdit b) => a.After == b.After && a.Inserted == b.Inserted && a.Removed.AsSpan().SequenceEqual(b.Removed),
        (HexDocVisibility a, HexDocVisibility b) => a == b,
        _ => false
    };

    internal static bool Boundary(string text, int offset) =>
        (uint)offset <= (uint)text.Length && (offset == text.Length || !char.IsLowSurrogate(text[offset]));

    private static void CheckText(string text)
    {
        if (text.Length > MaxStoredUnits)
            throw new ArgumentException("The document exceeds the text limit.", nameof(text));
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                i++;
            else if (char.IsSurrogate(text[i]))
                throw new ArgumentException("The document contains invalid UTF-16.", nameof(text));
        }
    }
}

/// <summary>Immutable text and the stable ranges which produced it.</summary>
internal sealed class HexDocView(string text, HexDocSpan[] spans, HexDocPositionSpan[]? positions = null)
{
    private int[]? lineStarts;
    public string Text { get; } = text;
    public IReadOnlyList<HexDocSpan> Spans => spans;

    public int OffsetAt(int line, int column)
    {
        int[] starts = Lines();
        if ((uint)line >= (uint)starts.Length)
            return Text.Length;
        int start = starts[line];
        int end = line + 1 == starts.Length ? Text.Length : starts[line + 1] - 1;
        int offset = start + Math.Clamp(column, 0, end - start);
        return HexLinkDocumentSequence.Boundary(Text, offset) ? offset : offset - 1;
    }

    public (int Line, int Column) PositionAt(int offset)
    {
        int[] starts = Lines();
        offset = Math.Clamp(offset, 0, Text.Length);
        int line = Array.BinarySearch(starts, offset);
        if (line < 0)
            line = ~line - 1;
        return (line, offset - starts[line]);
    }

    private int[] Lines()
    {
        if (lineStarts is { } known)
            return known;
        var starts = new List<int> { 0 };
        for (int i = Text.IndexOf('\n'); i >= 0; i = Text.IndexOf('\n', i + 1))
            starts.Add(i + 1);
        return lineStarts = [.. starts];
    }

    public HexDocEdit Edit(HexDocId id, int start, int length, string inserted)
    {
        if (length < 0 || start < 0 || start > Text.Length || length > Text.Length - start
            || !HexLinkDocumentSequence.Boundary(Text, start) || !HexLinkDocumentSequence.Boundary(Text, start + length))
            throw new ArgumentOutOfRangeException(nameof(start), "Document edits must align with UTF-16 character boundaries.");
        return new HexDocEdit(id, PointAt(start), inserted, length == 0 ? [] : Removal(start, length));
    }

    public HexDocPoint PointAt(int offset)
    {
        if (!HexLinkDocumentSequence.Boundary(Text, offset))
            throw new ArgumentOutOfRangeException(nameof(offset));
        if (offset == 0)
            return default;
        foreach (HexDocSpan span in spans)
        {
            if (offset <= span.Length)
                return new HexDocPoint(span.Run, span.Start + offset);
            offset -= span.Length;
        }
        throw new InvalidOperationException("Missing visible document span.");
    }

    // The boundary after a deleted atom stays at its old place in sequence order.
    public int? OffsetOf(HexDocPoint point)
    {
        if (point == default)
            return 0;
        if (positions is not null)
        {
            foreach (HexDocPositionSpan range in positions)
                if (range.Run == point.Run && point.Offset > range.Start && point.Offset <= range.End)
                    return range.Offset + (range.Visible ? point.Offset - range.Start : 0);
            return null;
        }

        int offset = 0;
        foreach (HexDocSpan range in spans)
        {
            if (range.Run == point.Run && point.Offset > range.Start && point.Offset <= range.Start + range.Length)
                return offset + point.Offset - range.Start;
            offset += range.Length;
        }
        return null;
    }

    // Only local atoms belong to this view. Keep it when another key was typed before a network result could paint.
    internal HexDocView WithEdit(HexDocSplice change, HexDocId id, string replaced)
    {
        var result = new List<HexDocSpan>();
        Copy(0, change.Start);
        if (change.Inserted.Length != 0)
            result.Add(new HexDocSpan(id, 0, change.Inserted.Length));
        Copy(change.Start + change.Length, Text.Length);
        return new HexDocView(replaced, [.. result]);

        void Copy(int start, int end)
        {
            int offset = 0;
            foreach (HexDocSpan span in spans)
            {
                if (offset >= end)
                    break;
                int from = Math.Max(start, offset), to = Math.Min(end, offset + span.Length);
                if (from < to)
                    result.Add(new HexDocSpan(span.Run, span.Start + from - offset, to - from));
                offset += span.Length;
            }
        }
    }

    // Shared atom IDs stay ordered. Preserve their common ranges and patch only the gaps between them.
    public HexDocSplice[] PatchesTo(HexDocView next)
    {
        if (Text == next.Text)
            return [];
        var lookup = new Dictionary<HexDocId, List<(HexDocSpan Span, int Offset)>>();
        int offset = 0;
        foreach (HexDocSpan span in next.Spans)
        {
            if (!lookup.TryGetValue(span.Run, out var mapped))
                lookup.Add(span.Run, mapped = []);
            mapped.Add((span, offset));
            offset += span.Length;
        }

        var patches = new List<HexDocSplice>();
        var consumed = new Dictionary<HexDocId, int>();
        int oldAt = 0, newAt = 0, start = 0;
        foreach (HexDocSpan span in spans)
        {
            if (lookup.TryGetValue(span.Run, out var mapped))
            {
                int at = consumed.GetValueOrDefault(span.Run);
                while (at < mapped.Count && mapped[at].Span.Start + mapped[at].Span.Length <= span.Start)
                    at++;
                consumed[span.Run] = at;
                for (; at < mapped.Count && mapped[at].Span.Start < span.Start + span.Length; at++)
                {
                    var (common, nextOffset) = mapped[at];
                    int first = Math.Max(span.Start, common.Start);
                    int end = Math.Min(span.Start + span.Length, common.Start + common.Length);
                    if (first >= end)
                        continue;

                    int oldStart = start + first - span.Start;
                    int newStart = nextOffset + first - common.Start;
                    if (oldStart < oldAt || newStart < newAt)
                        throw new InvalidOperationException("Shared document atoms appeared out of order.");
                    AddGap(oldStart, newStart);
                    oldAt = oldStart + end - first;
                    newAt = newStart + end - first;
                }
            }

            start += span.Length;
        }
        AddGap(Text.Length, next.Text.Length);
        return [.. patches];

        void AddGap(int oldEnd, int newEnd)
        {
            if (!Text.AsSpan(oldAt, oldEnd - oldAt).SequenceEqual(next.Text.AsSpan(newAt, newEnd - newAt)))
                patches.Add(new HexDocSplice(oldAt, oldEnd - oldAt, next.Text[newAt..newEnd]));
        }
    }

    private HexDocSpan[] Removal(int start, int length)
    {
        int end = start + length, at = 0;
        var removed = new List<HexDocSpan>();
        foreach (HexDocSpan span in spans)
        {
            if (at >= end)
                break;
            int from = Math.Max(start, at), to = Math.Min(end, at + span.Length);
            if (from < to)
                removed.Add(new HexDocSpan(span.Run, span.Start + from - at, to - from));
            at += span.Length;
        }
        return [.. removed];
    }
}
