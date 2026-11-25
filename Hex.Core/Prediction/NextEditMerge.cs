namespace Hex.Core;

/// <summary>
/// How what a model predicted for another place becomes a <see cref="NextEdit"/> and meets the suggestions already
/// waiting (docs/next-edit-plan.md, 2.2): a model's edit that overlaps a rename's suggestion loses to it, a newer one of
/// the model's replaces an older it overlaps, and one made for an older text is moved to the text as it is or let go.
/// </summary>
internal static class NextEditMerge
{
    /// <summary>The confidence a model's suggestion has.</summary>
    public const double CONFIDENCE = 0.6;

    /// <summary>
    /// A model's edits (offsets into <paramref name="text"/>, the document at <paramref name="version"/>) as one
    /// suggestion spanning them all, null when there are none or they are out of the text.
    /// </summary>
    public static NextEdit? FromModel(string path, string text, int version, IReadOnlyList<PredictionEdit> edits, string reason)
    {
        if (edits.Count == 0)
            return null;

        int start = int.MaxValue, end = 0;
        foreach (PredictionEdit edit in edits)
        {
            if (edit.Start < 0 || edit.End < edit.Start || edit.End > text.Length)
                return null;
            start = Math.Min(start, edit.Start);
            end = Math.Max(end, edit.End);
        }
        return new NextEdit(path, start, end, version, edits, NextEditSource.Model, reason, CONFIDENCE, text[start..end]);
    }

    /// <summary>Whether two spans overlap; an insertion counts when it is inside the other or at either end of it.</summary>
    public static bool Overlaps(int aStart, int aEnd, int bStart, int bEnd)
    {
        if (aStart == aEnd)
            return aStart >= bStart && aStart <= bEnd;
        if (bStart == bEnd)
            return bStart >= aStart && bStart <= aEnd;
        return aStart < bEnd && bStart < aEnd;
    }

    /// <summary>
    /// Puts a model's suggestion in a queue. It is let go of when it overlaps one of a rename (the deterministic edit wins);
    /// model suggestions it overlaps are replaced by it. True when it was added.
    /// </summary>
    public static bool Add(NextEditQueue queue, NextEdit model)
    {
        foreach (NextEdit other in queue.All)
        {
            if (other.Source == NextEditSource.Rename && Overlaps(model.Start, model.End, other.Start, other.End))
                return false;
        }
        foreach (NextEdit other in queue.All.ToArray())
        {
            if (other.Source == NextEditSource.Model && Overlaps(model.Start, model.End, other.Start, other.End))
                queue.Drop(other);
        }
        return queue.Add(model);
    }

    /// <summary>Puts a rename's suggestion in a queue; model suggestions it overlaps go, the rename winning.</summary>
    public static void AddRename(NextEditQueue queue, NextEdit rename)
    {
        foreach (NextEdit other in queue.All.ToArray())
        {
            if (other.Source == NextEditSource.Model && Overlaps(rename.Start, rename.End, other.Start, other.End))
                queue.Drop(other);
        }
        queue.Add(rename);
    }

    /// <summary>
    /// A suggestion made for one text, moved to another: the one change between them (everything from the first to the
    /// last difference) shifts it if it is after, and it is dropped (null) when the change touched it.
    /// </summary>
    public static NextEdit? Rebased(NextEdit edit, string made, string now)
    {
        if (Change(made, now) is not { } change)
            return edit;
        var queue = new NextEditQueue();
        queue.Add(edit);
        queue.Rebase([change]);
        return queue.Count == 0 ? null : queue.All[0];
    }

    /// <summary>What changed from one text to another as one change, null when they are the same.</summary>
    public static NextEditChange? Change(string before, string after)
    {
        if (string.Equals(before, after, StringComparison.Ordinal))
            return null;

        int limit = Math.Min(before.Length, after.Length), start = 0;
        while (start < limit && before[start] == after[start])
            start++;
        int end = 0;
        while (end < limit - start && before[before.Length - 1 - end] == after[after.Length - 1 - end])
            end++;
        return new NextEditChange(start, before.Length - start - end, after.Length - start - end);
    }
}
