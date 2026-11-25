using Hex.Core;

using Horizon.UI.Components;

namespace Hex.Dawn;

/// <summary>
/// The parts of next-edit suggestions' glue (docs/next-edit-plan.md, 2.3) that need no editor: the hint written after the
/// caret's line, whether a suggestion still sits where it was made, the edits a rename leaves, and taking every one of a
/// source in a single undo step.
/// </summary>
internal static class HexNextEditGlue
{
    /// <summary>
    /// What is written after the caret's line: the key, which way the suggestion is and its line from 1 ("Tab ↓ 42"), how
    /// many more wait after it ("Tab ↓ 57 (3 more)"). Just "Tab" for one on the caret's own line.
    /// </summary>
    public static string JumpHint(int caretLine, int targetLine, int more = 0)
    {
        string where = targetLine == caretLine ? string.Empty
            : (targetLine > caretLine ? " ↓ " : " ↑ ") + (targetLine + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string rest = more > 0 ? " (" + more.ToString(System.Globalization.CultureInfo.InvariantCulture) + " more)" : string.Empty;
        return "Tab" + where + rest;
    }

    /// <summary>The line of a suggestion, from 0, in the text it was made for.</summary>
    public static int LineOf(string text, int offset)
    {
        int line = 0;
        int end = Math.Min(offset, text.Length);
        for (int i = 0; i < end; i++)
        {
            if (text[i] == '\n')
                line++;
        }
        return line;
    }

    /// <summary>An offset into the text (lines ended with '\n') of a place in the document, from the document's lines.</summary>
    public static int Offset(UITextDocument document, UITextPosition at)
    {
        int offset = 0;
        for (int i = 0; i < at.Line && i < document.LineCount; i++)
            offset += document.LineAt(i).Length + 1;
        return offset + Math.Min(at.Column, at.Line < document.LineCount ? document.LineAt(at.Line).Length : 0);
    }

    /// <summary>
    /// What changed from one text to another as one change: everything between the text they share at the start and at the
    /// end. Null when they're the same. Edits at several places come out as the whole stretch between the first and last.
    /// </summary>
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

    /// <summary>The document's line strings (shared, not copied) into <paramref name="into"/>, grown only when too small. Returns the count.</summary>
    public static int Snapshot(UITextDocument document, ref string[] into)
    {
        int count = document.LineCount;
        if (into.Length < count)
            into = new string[Math.Max(count, into.Length * 2)];
        for (int i = 0; i < count; i++)
            into[i] = document.LineAt(i);
        return count;
    }

    /// <summary>
    /// <see cref="Change(string, string)"/> from a snapshot of the document's lines to the document now, without joining the
    /// whole text: lines are compared by reference first, and only the lines that differ are joined to find the characters.
    /// </summary>
    public static NextEditChange? Change(string[] before, int beforeCount, UITextDocument after)
    {
        int n = after.LineCount, m = beforeCount, limit = Math.Min(n, m), first = 0;
        while (first < limit && string.Equals(before[first], after.LineAt(first), StringComparison.Ordinal))
            first++;
        if (first == n && first == m)
            return null;

        int tail = 0;
        while (tail < limit - first && string.Equals(before[m - 1 - tail], after.LineAt(n - 1 - tail), StringComparison.Ordinal))
            tail++;

        // Each side keeps at least one line, so a line put in or taken out has its newline in the compared text
        while ((first + tail >= m || first + tail >= n) && (first > 0 || tail > 0))
        {
            // The line after is taken in first, so a newline put in or taken out is placed where the whole text's diff places it
            if (tail > 0)
                tail--;
            else
                first--;
        }

        long baseOffset = first;
        for (int i = 0; i < first; i++)
            baseOffset += before[i].Length;

        var old = new System.Text.StringBuilder();
        for (int i = first; i < m - tail; i++)
            old.Append(i > first ? "\n" : "").Append(before[i]);
        var now = new System.Text.StringBuilder();
        for (int i = first; i < n - tail; i++)
            now.Append(i > first ? "\n" : "").Append(after.LineAt(i));

        NextEditChange? inner = Change(old.ToString(), now.ToString());
        return inner is { } c ? new NextEditChange((int)baseOffset + c.Start, c.OldLength, c.NewLength) : null;
    }

    /// <summary>The span a suggestion covers, null when the text there isn't what it was made for.</summary>
    public static (int Start, int End)? Anchored(NextEdit edit, string text) =>
        edit.Start >= 0 && edit.End <= text.Length && edit.Start <= edit.End
            && string.CompareOrdinal(text, edit.Start, edit.Expected, 0, edit.Expected.Length) == 0
            && edit.End - edit.Start == edit.Expected.Length
            ? (edit.Start, edit.End)
            : null;

    /// <summary>The suggestions for a rename: every whole word left of the old name made the new one, or only the one nearest the caret while the rename isn't confirmed.</summary>
    public static List<NextEdit> ForRename(Rename rename, string text, int version, int caret)
    {
        IReadOnlyList<(int Start, int End)> places = RenameTracker.Remaining(text, rename);
        var made = new List<NextEdit>(places.Count);
        string reason = "rename " + rename.OldName + " → " + rename.NewName;
        foreach ((int start, int end) in places)
        {
            made.Add(new NextEdit(rename.Path, start, end, version, [new PredictionEdit(start, end, rename.NewName)],
                NextEditSource.Rename, reason, 0.9, rename.OldName));
        }

        if (!rename.Confirmed && made.Count > 1)
        {
            NextEdit nearest = made.FirstOrDefault(edit => edit.End >= caret) ?? made[0];
            return [nearest];
        }
        return made;
    }

    /// <summary>Where each suggestion is, as the editor marks it: the line, and the columns it spans on that line.</summary>
    public static UIJumpMarker[] Markers(IReadOnlyList<NextEdit> edits, IReadOnlyList<string> lines)
    {
        var markers = new List<UIJumpMarker>(edits.Count);
        foreach (NextEdit edit in edits)
        {
            UITextPosition start = HexPredictionGlue.Position(lines, edit.Start), end = HexPredictionGlue.Position(lines, edit.End);
            markers.Add(new UIJumpMarker(start.Line, start.Column, end.Line == start.Line ? end.Column : lines[start.Line].Length, edit.Reason));
        }
        return [.. markers];
    }

    /// <summary>
    /// The editor's edits for several suggestions at once, in the order of the text, null when one of them no longer sits
    /// where it was made or they overlap.
    /// </summary>
    public static UITextEdit[]? Edits(IReadOnlyList<NextEdit> suggestions, string text)
    {
        var flat = new List<PredictionEdit>();
        foreach (NextEdit suggestion in suggestions.OrderBy(edit => edit.Start))
        {
            if (Anchored(suggestion, text) is null)
                return null;
            flat.AddRange(suggestion.Edits);
        }
        return HexPredictionGlue.Edits(flat, text.Split('\n'));
    }

    /// <summary>
    /// Takes every suggestion given that is still where it was made, together as one undo step. Returns how many were taken,
    /// none when the document can't be edited.
    /// </summary>
    public static int AcceptAll(UITextDocument document, IReadOnlyList<NextEdit> suggestions)
    {
        string text = document.Text;
        List<NextEdit> fitting = [.. suggestions.Where(edit => Anchored(edit, text) is not null).OrderBy(edit => edit.Start)];
        if (fitting.Count == 0 || Edits(fitting, text) is not { } edits || !document.ApplyEdits(edits))
            return 0;
        return fitting.Count;
    }
}
