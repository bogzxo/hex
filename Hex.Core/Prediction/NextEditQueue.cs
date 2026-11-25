namespace Hex.Core;

/// <summary>
/// The edits waiting at other places in one document, in the order of the text. It holds at most 32, the oldest added
/// going first. A suggestion let go of with <see cref="Dismiss"/> is remembered as a span (rebased with the rest) so that
/// the same one isn't made again. Owned by the editor's thread only.
/// </summary>
internal sealed class NextEditQueue
{
    public const int MOST = 32;

    // In the order of the text, by where each starts
    private readonly List<NextEdit> edits = [];

    // Spans let go of, moved along with the text the way the edits are
    private readonly List<(int Start, int End, NextEditSource Source)> dismissed = [];

    // The order they were added in, the first to go when there are too many
    private readonly List<NextEdit> added = [];

    public int Count => edits.Count;

    public IReadOnlyList<NextEdit> All => edits;

    /// <summary>Adds an edit in its place in the text. One for the same span and source replaces the old; one that was dismissed is ignored.</summary>
    public bool Add(NextEdit edit)
    {
        foreach (var gone in dismissed)
        {
            if (gone.Source == edit.Source && gone.Start == edit.Start && gone.End == edit.End)
                return false;
        }

        for (int i = 0; i < edits.Count; i++)
        {
            if (edits[i].Source == edit.Source && edits[i].Start == edit.Start && edits[i].End == edit.End)
            {
                added.Remove(edits[i]);
                edits.RemoveAt(i);
                break;
            }
        }

        int at = 0;
        while (at < edits.Count && edits[at].Start <= edit.Start)
            at++;
        edits.Insert(at, edit);
        added.Add(edit);

        if (edits.Count > MOST)
        {
            NextEdit oldest = added[0];
            added.RemoveAt(0);
            edits.Remove(oldest);
        }
        return true;
    }

    /// <summary>
    /// The nearest edit at or after the caret (one the caret is inside counts), the first of the text when there is none
    /// after it, so going on past the last comes back round to the top. Null when the queue is empty.
    /// </summary>
    public NextEdit? Peek(int caret)
    {
        foreach (NextEdit edit in edits)
        {
            if (edit.End >= caret)
                return edit;
        }
        return edits.Count > 0 ? edits[0] : null;
    }

    /// <summary>The first edit that starts after the caret, round to the first when it is past them all.</summary>
    public NextEdit? Next(int caret)
    {
        foreach (NextEdit edit in edits)
        {
            if (edit.Start > caret)
                return edit;
        }
        return edits.Count > 0 ? edits[0] : null;
    }

    /// <summary>The last edit that starts before the caret, round to the last when there is none before it.</summary>
    public NextEdit? Previous(int caret)
    {
        for (int i = edits.Count - 1; i >= 0; i--)
        {
            if (edits[i].Start < caret)
                return edits[i];
        }
        return edits.Count > 0 ? edits[^1] : null;
    }

    /// <summary>The nearest edit as <see cref="Peek"/> says, taken out of the queue.</summary>
    public NextEdit? Take(int caret)
    {
        NextEdit? edit = Peek(caret);
        if (edit is not null)
            Drop(edit);
        return edit;
    }

    /// <summary>Takes an edit out of the queue.</summary>
    public bool Drop(NextEdit edit)
    {
        added.Remove(edit);
        return edits.Remove(edit);
    }

    /// <summary>Takes an edit out and remembers its span as let go of.</summary>
    public void Dismiss(NextEdit edit)
    {
        Drop(edit);
        dismissed.Add((edit.Start, edit.End, edit.Source));
        if (dismissed.Count > 4 * MOST)
            dismissed.RemoveAt(0);
    }

    /// <summary>Takes every edit of a source out, those dismissed staying dismissed.</summary>
    public void DropAll(NextEditSource source)
    {
        edits.RemoveAll(edit => edit.Source == source);
        added.RemoveAll(edit => edit.Source == source);
    }

    /// <summary>Empties the queue and forgets what was dismissed.</summary>
    public void Clear()
    {
        edits.Clear();
        added.Clear();
        dismissed.Clear();
    }

    /// <summary>
    /// The text changed, in the order the changes were made, each in the text the one before left. An edit after a change
    /// moves by what it added; one the change touched (overlapping it, or put in at it or at its ends) is dropped.
    /// </summary>
    public void Rebase(IReadOnlyList<NextEditChange> changes)
    {
        foreach (NextEditChange change in changes)
        {
            for (int i = edits.Count - 1; i >= 0; i--)
            {
                NextEdit edit = edits[i];
                if (Touches(change, edit.Start, edit.End))
                {
                    added.Remove(edit);
                    edits.RemoveAt(i);
                }
                else if (change.End <= edit.Start && !(change.OldLength == 0 && change.Start >= edit.Start))
                {
                    NextEdit moved = Moved(edit, change.Delta);
                    edits[i] = moved;
                    int a = added.IndexOf(edit);
                    if (a >= 0)
                        added[a] = moved;
                }
            }

            for (int i = dismissed.Count - 1; i >= 0; i--)
            {
                var gone = dismissed[i];
                if (Touches(change, gone.Start, gone.End))
                    dismissed.RemoveAt(i);
                else if (change.End <= gone.Start)
                    dismissed[i] = (gone.Start + change.Delta, gone.End + change.Delta, gone.Source);
            }
        }
    }

    // Whether a change reaches a span: an insertion at it or at either end of it, or a replacement that overlaps it
    private static bool Touches(NextEditChange change, int start, int end) =>
        change.OldLength == 0
            ? change.Start >= start && change.Start <= end
            : change.Start < end && change.End > start;

    private static NextEdit Moved(NextEdit edit, int by)
    {
        if (by == 0)
            return edit;
        var shifted = new PredictionEdit[edit.Edits.Count];
        for (int i = 0; i < shifted.Length; i++)
            shifted[i] = edit.Edits[i] with { Start = edit.Edits[i].Start + by, End = edit.Edits[i].End + by };
        return edit with { Start = edit.Start + by, End = edit.End + by, Edits = shifted };
    }
}
