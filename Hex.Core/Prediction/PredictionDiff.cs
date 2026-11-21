using Hex.Core.Git;

namespace Hex.Core;

/// <summary>
/// The fewest edits that turn a model's old text into its new one: the lines that are the same at both ends dropped,
/// the lines between compared (Myers, as git does it), and each run that changed narrowed to the characters that differ.
/// </summary>
internal static class PredictionDiff
{
    /// <summary>
    /// The edits from <paramref name="old"/> to <paramref name="updated"/>, in order, their offsets those of the old
    /// text moved along by <paramref name="offset"/> (where the old text starts in the file).
    /// </summary>
    public static IReadOnlyList<PredictionEdit> Edits(string old, string updated, int offset)
    {
        if (old == updated)
            return [];

        // A newline added to both, so that every line ends with one and a line added at the end is a line like any other
        string before = old + "\n", after = updated + "\n";
        string[] oldLines = before.Split('\n')[..^1], newLines = after.Split('\n')[..^1];
        int[] oldStarts = Starts(oldLines), newStarts = Starts(newLines);

        var edits = new List<PredictionEdit>();
        foreach (GitHunk hunk in GitLineDiff.Diff(oldLines, newLines))
        {
            int from = oldStarts[hunk.OldStart], to = oldStarts[hunk.OldStart + hunk.OldCount];
            int newFrom = newStarts[hunk.NewStart], newTo = newStarts[hunk.NewStart + hunk.NewCount];

            // What's the same at the start and the end of the run isn't part of the edit
            int same = 0;
            while (from + same < to && newFrom + same < newTo && before[from + same] == after[newFrom + same])
                same++;
            int sameEnd = 0;
            while (to - sameEnd > from + same && newTo - sameEnd > newFrom + same && before[to - sameEnd - 1] == after[newTo - sameEnd - 1])
                sameEnd++;

            int start = from + same, end = to - sameEnd;
            string text = after[(newFrom + same)..(newTo - sameEnd)];

            // The newline added at the end isn't the text's: an edit reaching it is moved back off it
            if (end > old.Length)
            {
                if (start > old.Length)
                {
                    // Lines put after the last one: the newline goes before them instead of after
                    start = old.Length;
                    text = "\n" + text[..^1];
                }
                else if (text.EndsWith('\n'))
                    text = text[..^1];
                else
                {
                    // Lines taken off the end: the newline before them goes too
                    start = Math.Max(0, start - 1);
                }
                end = old.Length;
            }

            if (start != end || text.Length > 0)
                edits.Add(new PredictionEdit(offset + start, offset + end, text));
        }
        return edits;
    }

    /// <summary>
    /// Whether edits are ghost text: one insertion at the caret, or one that replaces only what follows the caret on its
    /// line with text that starts with it.
    /// </summary>
    public static bool IsGhost(IReadOnlyList<PredictionEdit> edits, string text, int caret)
    {
        if (edits.Count != 1 || edits[0].Start != caret)
            return false;
        PredictionEdit edit = edits[0];
        if (edit.End == caret)
            return true;

        int lineEnd = text.IndexOf('\n', caret);
        if (lineEnd < 0)
            lineEnd = text.Length;
        return edit.End == lineEnd && edit.Text.AsSpan().StartsWith(text.AsSpan(caret, lineEnd - caret), StringComparison.Ordinal);
    }

    // Where each line starts, and one more for where the text ends
    private static int[] Starts(string[] lines)
    {
        int[] starts = new int[lines.Length + 1];
        for (int i = 0; i < lines.Length; i++)
            starts[i + 1] = starts[i] + lines[i].Length + 1;
        return starts;
    }
}
