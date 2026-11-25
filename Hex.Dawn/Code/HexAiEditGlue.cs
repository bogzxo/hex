using System.Globalization;

using Hex.Core;

namespace Hex.Dawn;

/// <summary>Which line the progress of an AI edit says: the model loading, the request waiting, the model thinking, or the answer being written.</summary>
internal enum AiEditProgress
{
    Loading,
    Waiting,
    Thinking,
    Writing,
}

/// <summary>
/// The parts of AI edits' glue (docs/ai-edit-plan.md, 2) that need no editor: what the quick actions ask, the recent
/// instructions, how many lines a change adds and removes, and what the progress line says.
/// </summary>
internal static partial class HexAiEditGlue
{
    /// <summary>The most instructions kept to be run again.</summary>
    public const int MOST_RECENT = 20;

    /// <summary>How many of them the menu lists.</summary>
    public const int MENU_RECENT = 5;

    /// <summary>The quick actions, in the order the menu and the chips list them. "fix" is only offered with problems on the lines.</summary>
    public static readonly string[] QuickActions = ["fix", "document", "simplify", "errors", "faster"];

    /// <summary>
    /// What a quick action asks the model, in words. The key of one that doesn't exist gives an empty text. "fix" points at
    /// the problems the prompt lists above the code.
    /// </summary>
    public static string Instruction(string action) => action switch
    {
        "fix" => "Fix the problems listed above, and change nothing else.",
        "document" => "Add documentation comments to the declarations in this code, in the style the language and this file use, and change nothing else.",
        "simplify" => "Simplify this code without changing what it does: remove what's redundant, shorten what's long-winded.",
        "errors" => "Add error handling to this code: check what can fail, handle it the way the rest of the file does, and keep what it does when nothing fails.",
        "faster" => "Make this code faster without changing what it does.",
        _ => string.Empty,
    };

    /// <summary>
    /// The instructions with a new one put first: trimmed, an earlier copy of the same (whatever its case) removed, and no
    /// more than <see cref="MOST_RECENT"/> kept. An empty instruction changes nothing.
    /// </summary>
    public static List<string> Remember(IReadOnlyList<string> recent, string instruction)
    {
        var made = new List<string>(recent);
        string trimmed = instruction.Trim();
        if (trimmed.Length == 0)
            return made;
        made.RemoveAll(each => string.Equals(each, trimmed, StringComparison.OrdinalIgnoreCase));
        made.Insert(0, trimmed);
        if (made.Count > MOST_RECENT)
            made.RemoveRange(MOST_RECENT, made.Count - MOST_RECENT);
        return made;
    }

    /// <summary>
    /// Where Up (-1) and Down (+1) take the recent instructions from where they are: -1 is what the person was writing,
    /// 0 the newest instruction, up to the last. Up goes back in time, Down returns toward the draft and stops at it.
    /// </summary>
    public static int Walk(int at, int key, int count)
    {
        if (count == 0)
            return -1;
        int next = key < 0 ? at + 1 : at - 1;
        return Math.Clamp(next, -1, count - 1);
    }

    /// <summary>One problem as the prompt lists it: its line (1-based) and what the language server says, on one line.</summary>
    public static string ProblemLine(int line, string message)
    {
        string one = string.Join(' ', message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return $"line {(line + 1).ToString(CultureInfo.InvariantCulture)}: {one}";
    }

    /// <summary>
    /// Where the target is in a text that was typed in since the request was made: the target's own text, exactly, found
    /// where it was or else the nearest place to there. Null when it's gone or changed, then the edit can't go on. The
    /// difference to its old start is how far the request's offsets (and the answer's edits) move.
    /// </summary>
    public static int? Rebase(string before, int start, int end, string after)
    {
        if (start < 0 || end > before.Length || end <= start)
            return null;
        string target = before[start..end];
        if (start + target.Length <= after.Length && string.CompareOrdinal(after, start, target, 0, target.Length) == 0)
            return start;

        int forward = after.IndexOf(target, Math.Min(start, after.Length), StringComparison.Ordinal);
        int back = after.Length == 0 ? -1 : after.LastIndexOf(target, Math.Min(after.Length - 1, start + target.Length - 1), StringComparison.Ordinal);
        if (forward < 0)
            return back < 0 ? null : back;
        if (back < 0)
            return forward;
        return forward - start <= start - back ? forward : back;
    }

    /// <summary>
    /// Whether the card of an AI edit may be shown: its editor is the document on screen, it and everything above it is
    /// visible (the right tab, layout and face, not collapsed), and its code area has some size that meets the area it can
    /// be seen in. Otherwise the card is hidden and the request goes on.
    /// </summary>
    public static bool CardVisible(bool editorOnScreen, bool visibleInHierarchy, (float X0, float Y0, float X1, float Y1) code, (float X0, float Y0, float X1, float Y1) area)
    {
        if (!editorOnScreen || !visibleInHierarchy)
            return false;
        float w = Math.Min(code.X1, area.X1) - Math.Max(code.X0, area.X0), h = Math.Min(code.Y1, area.Y1) - Math.Max(code.Y0, area.Y0);
        return code.X1 > code.X0 && code.Y1 > code.Y0 && w > 0.5f && h > 0.5f;
    }

    /// <summary>
    /// The targets of several selections (each as its start, end and caret offsets in the text), each grown by
    /// <see cref="InstructTarget.Around"/>, in the order of the text, those that touch or overlap made one. Also which of them
    /// holds the primary selection (the one at <paramref name="primary"/>), where the box hangs.
    /// </summary>
    public static (List<(int Start, int End)> Targets, int Primary) Targets(string text, IReadOnlyList<(int From, int To, int Caret)> selections, int primary, bool lineOnly = false)
    {
        var all = new List<(int Start, int End, bool Primary)>();
        for (int i = 0; i < selections.Count; i++)
        {
            (int start, int end) = InstructTarget.Around(text, selections[i].From, selections[i].To, selections[i].Caret);

            // Where to put a line, not what to rewrite: with no selection, the caret's own line even when it is in a block
            if (lineOnly && selections[i].From == selections[i].To && text.Length > 0)
            {
                int caret = Math.Clamp(selections[i].Caret, 0, text.Length);
                int lineStart = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1, lineEnd = text.IndexOf('\n', caret);
                if (lineEnd < 0)
                    lineEnd = text.Length;
                if (text.AsSpan(lineStart, lineEnd - lineStart).Trim().Length > 0)
                    (start, end) = (lineStart, lineEnd);
            }
            if (end > start)
                all.Add((start, end, i == primary));
        }
        all.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));

        var merged = new List<(int Start, int End)>();
        int at = 0;
        foreach ((int start, int end, bool isPrimary) in all)
        {
            if (merged.Count > 0 && start <= merged[^1].End)
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            else
                merged.Add((start, end));
            if (isPrimary)
                at = merged.Count - 1;
        }
        return (merged, at);
    }

    /// <summary>Whether an instruction says where to put something ("add a comment above", "insert a blank line below") rather than what to change.</summary>
    public static bool IsPlacement(string instruction) => PlaceWord().IsMatch(instruction) && PutWord().IsMatch(instruction);

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(above|below|before|after|over|under)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex PlaceWord();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(add|insert|put|write|place)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex PutWord();

    /// <summary>
    /// The edits of several targets' answers as one list in the order of the text, for one prediction to show and so one
    /// undo step to take. Null when two of them overlap.
    /// </summary>
    public static List<PredictionEdit>? Combine(IEnumerable<IReadOnlyList<PredictionEdit>> parts)
    {
        List<PredictionEdit> all = [.. parts.SelectMany(p => p).OrderBy(e => e.Start).ThenBy(e => e.End)];
        for (int i = 1; i < all.Count; i++)
        {
            if (all[i].Start < all[i - 1].End)
                return null;
        }
        return all;
    }

    /// <summary>The edits moved by a number of characters, for a target that moved.</summary>
    public static List<PredictionEdit> Shift(IReadOnlyList<PredictionEdit> edits, int by) =>
        [.. edits.Select(edit => edit with { Start = edit.Start + by, End = edit.End + by })];

    /// <summary>The text with the edits made, as offsets into it in order. Null when one is out of the text or they overlap.</summary>
    public static string? Apply(string text, IReadOnlyList<PredictionEdit> edits)
    {
        var made = new System.Text.StringBuilder(text.Length);
        int at = 0;
        foreach (PredictionEdit edit in edits)
        {
            if (edit.Start < at || edit.End < edit.Start || edit.End > text.Length)
                return null;
            made.Append(text, at, edit.Start - at).Append(edit.Text);
            at = edit.End;
        }
        return made.Append(text, at, text.Length - at).ToString();
    }

    /// <summary>
    /// How many lines a change adds and removes: the two texts compared line by line (a line changed counts as one removed
    /// and one added), so edits within the same line don't count it twice. Text too long to compare line by line is
    /// counted by its lines alone.
    /// </summary>
    public static (int Added, int Removed) Lines(string before, string after)
    {
        string[] old = Split(before), now = Split(after);
        int head = 0;
        while (head < old.Length && head < now.Length && old[head] == now[head])
            head++;
        int tail = 0;
        while (tail < old.Length - head && tail < now.Length - head && old[^(tail + 1)] == now[^(tail + 1)])
            tail++;

        int n = old.Length - head - tail, m = now.Length - head - tail;
        if (n == 0 || m == 0)
            return (m, n);
        if ((long)n * m > 2_000_000)
            return (m, n);

        // The longest run of lines both have, in order; the rest of the old went and the rest of the new came
        var table = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                table[i, j] = old[head + i] == now[head + j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }
        return (m - table[0, 0], n - table[0, 0]);
    }

    /// <summary>The key of what the progress line says (ai_edit.progress_*), from what has come of the answer so far.</summary>
    public static AiEditProgress ProgressOf(bool swaps, int thinkingPieces, int answerPieces)
    {
        if (answerPieces > 0)
            return AiEditProgress.Writing;
        if (thinkingPieces > 0)
            return AiEditProgress.Thinking;
        return swaps ? AiEditProgress.Loading : AiEditProgress.Waiting;
    }

    /// <summary>Seconds as the progress line writes them: one decimal, the invariant culture's point, "3.1".</summary>
    public static string Seconds(double seconds) => Math.Max(0, seconds).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>How many lines an answer has so far, a last line still being written counted.</summary>
    public static int LinesWritten(string answer)
    {
        if (answer.Length == 0)
            return 0;
        int count = 1;
        foreach (char c in answer)
        {
            if (c == '\n')
                count++;
        }
        return count;
    }

    // A text's lines, the one an ending newline leaves after it not counted
    private static string[] Split(string text)
    {
        if (text.Length == 0)
            return [];
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }
}
