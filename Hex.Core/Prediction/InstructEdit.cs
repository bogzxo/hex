namespace Hex.Core;

/// <summary>
/// An edit asked for in words (AI ▸ Edit…, Ctrl+I): the file's path inside its folder and its language, its whole text
/// with lines ended by '\n', the target as offsets into it (whole lines, TargetEnd at the end of the last line's text or
/// at the start of the line after it), the instruction, the recent edits (oldest first), the problems the language
/// server says on the target's lines, and the conversation so far when the person refines an earlier answer.
/// </summary>
internal sealed record InstructRequest(
    string Path,
    string Language,
    string Text,
    int TargetStart,
    int TargetEnd,
    string Instruction,
    IReadOnlyList<PredictionEvent> Events,
    IReadOnlyList<string> Problems,
    IReadOnlyList<ChatTurn> Earlier,
    IReadOnlyList<InstructContext>? Context = null)
{
    /// <summary>The target's text.</summary>
    public string Target => Text[TargetStart..TargetEnd];
}

/// <summary>
/// What an answer comes to so far: the target's new text (null until the code block has ended, or when there is none),
/// the edits that make it, against the request's text (empty when nothing changes), the problem when there's one
/// (<see cref="InstructProblem"/>'s words), and whether it's settled.
/// </summary>
internal sealed record InstructParsed(string? Replacement, IReadOnlyList<PredictionEdit> Edits, string? Problem, bool Done)
{
    /// <summary>An answer still coming that says nothing yet.</summary>
    public static readonly InstructParsed Waiting = new(null, [], null, false);
}

/// <summary>Why an answer is no edit, the keys the editor says them by (ai_edit.problem_*).</summary>
internal static class InstructProblem
{
    public const string NO_CODE = "no_code", CUT_SHORT = "cut_short", UNCHANGED = "unchanged";
}

/// <summary>
/// The prompt an edit by instruction is asked with, and its answer read back. See docs/ai-edit-plan.md section 3.
/// </summary>
internal static partial class InstructEditFormat
{
    /// <summary>What the model is told before anything else; the person's own system prompt comes after it.</summary>
    public const string SYSTEM = "You are a code editing tool inside the Hex editor. Rewrite the code between <edit> and </edit> to follow the instruction. "
        + "Change only what the instruction asks; keep everything else exactly as it is, including indentation, comments and names. "
        + "The lines before <edit> and after </edit> are context only: never repeat them in the answer. "
        + "The answer replaces everything between the tags, so when the instruction adds lines, give the added lines together with all the original lines that stay, in order. "
        + "Answer with the rewritten code only, in one fenced code block, and nothing after it.";

    /// <summary>The conversation to send: the system turn, the earlier turns of a refinement, the request's user turn.</summary>
    public static List<ChatTurn> Build(InstructRequest request, string? system)
    {
        var turns = new List<ChatTurn>
        {
            new("system", string.IsNullOrEmpty(system) ? SYSTEM : SYSTEM + "\n\n" + system),
        };
        turns.AddRange(request.Earlier);

        string text = request.Text, target = request.Target;
        int start = Math.Clamp(request.TargetStart, 0, text.Length);

        // The lines before the target: it starts a line, so these end with a newline
        int from = start;
        for (int n = 0; n < LINES_BEFORE && from > 0; n++)
            from = LineStartBefore(text, from);

        // The lines after it, past the newline that ends its last line when it doesn't carry that itself
        int after = AfterStart(request);
        int to = after;
        for (int n = 0; n < LINES_AFTER; n++)
        {
            int newline = text.IndexOf('\n', to);
            if (newline < 0)
            {
                to = text.Length;
                break;
            }
            to = newline + 1;
        }

        var code = new System.Text.StringBuilder();
        code.Append(text, from, start - from).Append("<edit>\n").Append(target);
        if (!target.EndsWith('\n'))
            code.Append('\n');
        code.Append("</edit>\n").Append(text, after, to - after);
        if (code.Length > 0 && code[^1] != '\n')
            code.Append('\n');

        int run = 0, longest = 0;
        foreach (char c in code.ToString())
        {
            run = c == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        string fence = new('`', Math.Max(3, longest + 1));

        var user = new System.Text.StringBuilder();
        user.Append("File: ").Append(request.Path).Append(" (").Append(request.Language).Append(")\n");

        // What is the same for every target of one instruction comes first, so the server's prompt cache is shared by them
        foreach (InstructContext block in request.Context ?? [])
        {
            string body = block.Text.EndsWith('\n') ? block.Text : block.Text + "\n";
            user.Append("Context from ").Append(block.Name).Append(" (read only, don't change it):\n").Append("~~~\n").Append(body).Append("~~~\n");
        }

        string history = History(request.Events);
        if (history.Length > 0)
            user.Append("Recent edits:\n").Append(history);

        if (request.Problems.Count > 0)
        {
            user.Append("Problems on these lines:\n");
            foreach (string problem in request.Problems.Take(MOST_PROBLEMS))
                user.Append("- ").Append(problem).Append('\n');
        }

        user.Append("Code:\n").Append(fence).Append(request.Language).Append('\n').Append(code).Append(fence).Append('\n');
        user.Append("Instruction: ").Append(request.Instruction);
        turns.Add(new ChatTurn("user", user.ToString()));
        return turns;
    }

    /// <summary>The answer so far read into the target's new text and its edits; <paramref name="complete"/> once the stream ended.</summary>
    public static InstructParsed Parse(InstructRequest request, string answer, bool complete)
    {
        var filter = new ThinkFilter();
        string text = (filter.Feed(answer.Replace("\r\n", "\n")) + filter.End()).Replace('\r', '\n');
        string[] lines = text.Split('\n');

        // The first fenced block, whatever is said before it
        int open = -1, length = 0;
        char mark = '`';
        for (int i = 0; i < lines.Length && open < 0; i++)
        {
            System.Text.RegularExpressions.Match match = OpeningFence().Match(lines[i]);
            if (!match.Success)
                continue;
            open = i;
            mark = match.Groups[1].Value[0];
            length = match.Groups[1].Length;
        }

        if (open >= 0)
        {
            int close = -1;
            for (int i = open + 1; i < lines.Length && close < 0; i++)
            {
                string line = lines[i].Trim();
                if (line.Length >= length && line.All(c => c == mark))
                    close = i;
            }

            if (close < 0)
                return complete ? new InstructParsed(null, [], InstructProblem.CUT_SHORT, true) : InstructParsed.Waiting;
            return Settle(request, string.Join('\n', lines[(open + 1)..close]));
        }

        string trimmed = text.Trim();
        if (!complete && (trimmed.Length < 200 || !trimmed.Contains('\n')))
            return InstructParsed.Waiting;

        int tag = text.IndexOf("<edit>", StringComparison.Ordinal);
        if (tag >= 0)
        {
            int end = text.IndexOf("</edit>", tag, StringComparison.Ordinal);
            if (end >= 0)
                return Settle(request, text[(tag + "<edit>".Length)..end].Trim('\n'));
        }

        if (!complete)
            return InstructParsed.Waiting;

        // No code block: the whole answer counts when it is mostly the target's own lines
        var targetLines = request.Target.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet();
        string[] said = lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (said.Length > 0 && (said.Count(targetLines.Contains) * 2 >= said.Length || said.All(IsComment)))
            return Settle(request, text.Trim('\n'));
        return new InstructParsed(null, [], InstructProblem.NO_CODE, true);
    }

    /// <summary>The most tokens the answer may have: the target's tokens twice and 512 more, and the thinking budget when the model thinks.</summary>
    public static int MaxTokens(InstructRequest request, bool thinks)
        => Math.Min(MOST_TOKENS, PredictionTokens.Of(request.Target) * 2 + 512 + (thinks ? 2048 : 0));

    private const int LINES_BEFORE = 60, LINES_AFTER = 30, MOST_PROBLEMS = 10, EVENT_TOKENS = 1500, MOST_TOKENS = 16384;

    [System.Text.RegularExpressions.GeneratedRegex(@"^[ \t]*(`{3,}|~{3,})[^`]*$")]
    private static partial System.Text.RegularExpressions.Regex OpeningFence();

    // The recent edits, oldest first, the oldest dropped while they come to more than the budget
    private static string History(IReadOnlyList<PredictionEvent> events)
    {
        var chosen = new List<string>();
        int total = 0;
        for (int i = events.Count - 1; i >= 0; i--)
        {
            string written = Zeta2Format.Event(events[i]);
            if (!written.EndsWith('\n'))
                written += "\n";
            total += PredictionTokens.Of(written);
            if (total > EVENT_TOKENS)
                break;
            chosen.Add(written);
        }
        chosen.Reverse();
        return string.Concat(chosen);
    }

    // Where the line before the one starting at the offset starts
    private static int LineStartBefore(string text, int at) => at <= 1 ? 0 : text.LastIndexOf('\n', at - 2) + 1;

    // Where the text after the target starts: past the newline ending its last line, unless the target carries it
    private static int AfterStart(InstructRequest request)
    {
        string text = request.Text;
        int end = Math.Clamp(request.TargetEnd, 0, text.Length);
        return end > request.TargetStart && text[end - 1] == '\n' || end >= text.Length || text[end] != '\n' ? end : end + 1;
    }

    // The code of the answer made into the target's replacement
    private static InstructParsed Settle(InstructRequest request, string code)
    {
        string text = request.Text, target = request.Target;
        var lines = code.Split('\n').ToList();

        // The tags the model copied from the prompt
        if (lines.Count > 0 && lines[0].Trim() == "<edit>")
            lines.RemoveAt(0);
        if (lines.Count > 0 && lines[^1].Trim() == "</edit>")
            lines.RemoveAt(lines.Count - 1);

        // The context lines it copied along with the target
        string body = target.EndsWith('\n') ? target[..^1] : target;
        string[] targetLines = body.Split('\n');
        int start = Math.Clamp(request.TargetStart, 0, text.Length);
        string[] targetSaid = targetLines.Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

        // A run of the lines just above the target copied in front of the answer (blank lines and white space aside)
        if (start > 0 && lines.Count > 1)
        {
            var above = new List<string>();
            for (int at = start, n = 0; at > 0 && n < LINES_BEFORE; n++)
            {
                int from = LineStartBefore(text, at);
                string line = text[from..Math.Max(from, at - 1)].Trim();
                if (line.Length > 0)
                    above.Insert(0, line);
                at = from;
            }
            int[] said = NonBlank(lines);
            for (int k = Math.Min(above.Count, said.Length - 1); k >= 1; k--)
            {
                bool match = true;
                for (int i = 0; i < k && match; i++)
                    match = lines[said[i]].Trim() == above[above.Count - k + i];
                if (!match || StartsWithRun(targetSaid, above.GetRange(above.Count - k, k)))
                    continue;
                int blanks = targetLines.TakeWhile(l => l.Trim().Length == 0).Count();
                lines.RemoveRange(0, said[k - 1] + 1);
                int leading = lines.TakeWhile(l => l.Trim().Length == 0).Count();
                if (leading > blanks)
                    lines.RemoveRange(0, leading - blanks);
                break;
            }
        }

        // A run of the lines just below it copied after the answer
        int after = AfterStart(request);
        if (after < text.Length && lines.Count > 1)
        {
            var below = new List<string>();
            for (int at = after, n = 0; at < text.Length && n < LINES_AFTER; n++)
            {
                int newline = text.IndexOf('\n', at);
                string line = text[at..(newline < 0 ? text.Length : newline)].Trim();
                if (line.Length > 0)
                    below.Add(line);
                if (newline < 0)
                    break;
                at = newline + 1;
            }
            int[] said = NonBlank(lines);
            for (int k = Math.Min(below.Count, said.Length - 1); k >= 1; k--)
            {
                bool match = true;
                for (int i = 0; i < k && match; i++)
                    match = lines[said[said.Length - k + i]].Trim() == below[i];
                if (!match || EndsWithRun(targetSaid, below.GetRange(0, k)))
                    continue;
                int blanks = targetLines.Reverse().TakeWhile(l => l.Trim().Length == 0).Count();
                lines.RemoveRange(said[said.Length - k], lines.Count - said[said.Length - k]);
                int trailing = 0;
                while (trailing < lines.Count && lines[^(trailing + 1)].Trim().Length == 0)
                    trailing++;
                if (trailing > blanks)
                    lines.RemoveRange(lines.Count - (trailing - blanks), trailing - blanks);
                break;
            }
        }

        // The indentation the target has in common, put back when the model took it off
        string indent = CommonIndent(targetLines);

        // An answer of only new comment lines is an insertion: the target stays, below them (above, unless asked for below)
        if (targetSaid.Length > 0 && IsInsertionOnly(lines, targetSaid))
        {
            bool below = request.Instruction.Contains("below", StringComparison.OrdinalIgnoreCase) || request.Instruction.Contains("after", StringComparison.OrdinalIgnoreCase);
            List<string> added = [.. lines.Where(l => l.Trim().Length > 0).Select(l => char.IsWhiteSpace(l[0]) ? l : indent + l)];
            lines = below ? [.. targetLines, .. added] : [.. added, .. targetLines];
        }
        if (indent.Length > 0)
        {
            bool any = false, has = false;
            foreach (string line in lines.Where(l => l.Trim().Length > 0))
            {
                any = true;
                has |= line.StartsWith(indent, StringComparison.Ordinal);
            }
            if (any && !has)
                lines = lines.Select(l => l.Trim().Length > 0 ? indent + l : l).ToList();
        }

        string replacement = string.Join('\n', lines);
        if (target.EndsWith('\n') && replacement.Length > 0)
            replacement += "\n";

        if (Same(target, replacement))
            return new InstructParsed(target, [], InstructProblem.UNCHANGED, true);
        return new InstructParsed(replacement, LineAligned(text, PredictionDiff.Edits(target, replacement, request.TargetStart)), null, true);
    }

    // Lines added in front of an indented line come from the character diff as "text\n  " put in after the indentation; they
    // are made the same lines put in at the start of the line, so an insertion keeps its own lines whatever shows or applies it
    private static IReadOnlyList<PredictionEdit> LineAligned(string text, IReadOnlyList<PredictionEdit> edits)
    {
        var made = new List<PredictionEdit>(edits.Count);
        foreach (PredictionEdit edit in edits)
        {
            if (edit.Start == edit.End && edit.Start <= text.Length && edit.Text.LastIndexOf('\n') is var nl and >= 0)
            {
                int lineStart = text.LastIndexOf('\n', Math.Max(0, edit.Start - 1)) + 1;
                if (edit.Start == 0)
                    lineStart = 0;
                string prefix = text[lineStart..edit.Start];
                if (prefix.Length > 0 && prefix.Trim().Length == 0 && edit.Text[(nl + 1)..] == prefix)
                {
                    made.Add(new PredictionEdit(lineStart, lineStart, prefix + edit.Text[..(nl + 1)]));
                    continue;
                }
            }
            made.Add(edit);
        }
        return made;
    }

    // Whether the lines are all comments and none of them is one of the target's lines, then they are lines to add to it
    private static bool IsInsertionOnly(List<string> lines, string[] targetSaid)
    {
        string[] said = [.. lines.Select(l => l.Trim()).Where(l => l.Length > 0)];
        return said.Length > 0
            && said.All(IsComment)
            && !said.Any(targetSaid.Contains)
            && !targetSaid.All(IsComment);
    }

    private static bool IsComment(string line)
        => line.StartsWith('#') || line.StartsWith("//") || line.StartsWith("/*") || line.StartsWith('*') || line.StartsWith("--")
            || line.StartsWith(';') || line.StartsWith("<!--") || line.StartsWith("%") || line.StartsWith("'");

    // The indexes of the lines that say something
    private static int[] NonBlank(List<string> lines)
    {
        var found = new List<int>();
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim().Length > 0)
                found.Add(i);
        }
        return [.. found];
    }

    // Whether the target's own lines (the ones that say something) start / end with the run, then it is no copy
    private static bool StartsWithRun(string[] target, List<string> run) => target.Length >= run.Count && target.Take(run.Count).SequenceEqual(run);

    private static bool EndsWithRun(string[] target, List<string> run) => target.Length >= run.Count && target.Skip(target.Length - run.Count).SequenceEqual(run);

    // Whether two texts are the same lines, what trails each line aside
    private static bool Same(string a, string b)
        => string.Join('\n', a.Split('\n').Select(l => l.TrimEnd())) == string.Join('\n', b.Split('\n').Select(l => l.TrimEnd()));

    // The leading white space all the lines that say anything start with
    private static string CommonIndent(IEnumerable<string> lines)
    {
        string? common = null;
        foreach (string line in lines.Where(l => l.Trim().Length > 0))
        {
            string indent = line[..(line.Length - line.TrimStart(' ', '\t').Length)];
            if (common is null)
            {
                common = indent;
                continue;
            }
            int n = 0;
            while (n < common.Length && n < indent.Length && common[n] == indent[n])
                n++;
            common = common[..n];
        }
        return common ?? string.Empty;
    }
}

/// <summary>What an edit by instruction changes when nothing says: the selection in whole lines, else the block round the caret.</summary>
internal static class InstructTarget
{
    /// <summary>
    /// The target as offsets into the text: a selection (start &lt; end) grown to whole lines; with none, the block the
    /// caret's line opens (the lines after it that are indented deeper), else the innermost block round it when that is no
    /// more than 60 lines, else the caret's line. Never empty unless the text is.
    /// </summary>
    public static (int Start, int End) Around(string text, int selectionStart, int selectionEnd, int caret)
    {
        if (text.Length == 0)
            return (0, 0);

        int from = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, text.Length);
        int to = Math.Clamp(Math.Max(selectionStart, selectionEnd), 0, text.Length);
        if (from < to)
            return (LineStart(text, from), LineEnd(text, to - 1));

        // The lines, each as where it starts and ends and how far it is indented, a blank one with no indentation
        var lines = new List<(int Start, int End, int Indent, bool Blank)>();
        for (int at = 0; at <= text.Length;)
        {
            int end = LineEnd(text, at);
            lines.Add((at, end, Indentation(text, at, end), IsBlank(text, at, end)));
            at = end + 1;
        }

        caret = Math.Clamp(caret, 0, text.Length);
        int line = lines.FindLastIndex(l => l.Start <= caret);

        // A blank line says nothing to edit: the nearest one with something on it above, else below
        if (lines[line].Blank)
        {
            int near = lines.FindLastIndex(line, l => !l.Blank);
            if (near < 0)
                near = lines.FindIndex(line, l => !l.Blank);
            if (near < 0)
                return (lines[line].Start, lines[line].End);
            line = near;
        }

        // A brace on a line of its own belongs to the line before it
        int Header(int at)
        {
            int before = at > 0 ? lines.FindLastIndex(at - 1, l => !l.Blank) : -1;
            return before >= 0 && IsOnly(text, lines[at], '{') && lines[before].Indent == lines[at].Indent ? before : at;
        }

        // The first and last line of the block a line opens: the lines after it that are blank or deeper, and the line that closes it
        (int First, int Last) Block(int at)
        {
            int first = at, last = at, depth = lines[at].Indent;
            for (int i = at + 1; i < lines.Count; i++)
            {
                // A brace on a line of its own right after the first line opens the block it belongs to
                if (i == last + 1 && last == first && lines[i].Indent == depth && IsOnly(text, lines[i], '{'))
                {
                    last = i;
                    continue;
                }
                if (!lines[i].Blank && lines[i].Indent <= depth)
                {
                    string closer = text[lines[i].Start..lines[i].End].Trim();
                    if (lines[i].Indent == depth && closer.Length > 0 && closer[0] is '}' or ')' or ']' && last > first)
                        last = i;
                    break;
                }
                if (!lines[i].Blank)
                    last = i;
            }
            return (first, last);
        }

        // Whether the lines after one are a block it opens: the next line with something on it is deeper
        bool Opens(int at)
        {
            int next = lines.FindIndex(at + 1, l => !l.Blank);
            return next >= 0 && lines[next].Indent > lines[at].Indent;
        }

        // A line that opens a block is that block, a method's header the whole method
        line = Header(line);
        if (Opens(line) || line + 1 < lines.Count && IsOnly(text, lines[line + 1], '{') && lines[line + 1].Indent == lines[line].Indent && Opens(line + 1))
        {
            (int first, int last) = Block(line);
            return (lines[first].Start, lines[last].End);
        }

        // Else the innermost block round it, when it is no bigger than a screen; a closing bracket goes with the line that opened it
        string own = text[lines[line].Start..lines[line].End].Trim();
        bool closes = own.Length > 0 && own[0] is '}' or ')' or ']';
        int opener = line == 0 ? -1 : lines.FindLastIndex(line - 1, l => !l.Blank && (closes ? l.Indent <= lines[line].Indent : l.Indent < lines[line].Indent));
        if (opener >= 0)
        {
            (int first, int last) = Block(Header(opener));
            if (last - first + 1 <= MOST_BLOCK_LINES && last >= line)
                return (lines[first].Start, lines[last].End);
        }

        // Else just the line
        return (lines[line].Start, lines[line].End);
    }

    // The most lines of a block round the caret that are taken as the target
    private const int MOST_BLOCK_LINES = 60;

    private static int LineStart(string text, int at) => at == 0 ? 0 : text.LastIndexOf('\n', at - 1) + 1;

    private static int LineEnd(string text, int at)
    {
        int end = text.IndexOf('\n', at);
        return end < 0 ? text.Length : end;
    }

    // Columns of leading white space, a tab four of them
    private static int Indentation(string text, int start, int end)
    {
        int columns = 0;
        for (int i = start; i < end && text[i] is ' ' or '\t'; i++)
            columns += text[i] == '\t' ? 4 : 1;
        return columns;
    }

    private static bool IsBlank(string text, int start, int end) => text.AsSpan(start, end - start).Trim().IsEmpty;

    private static bool IsOnly(string text, (int Start, int End, int Indent, bool Blank) line, char c)
        => text.AsSpan(line.Start, line.End - line.Start).Trim().SequenceEqual([c]);
}
