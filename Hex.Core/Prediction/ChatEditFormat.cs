using System.Text;
using Hex.Core.Git;

namespace Hex.Core;

/// <summary>
/// Edit prediction for an instruct model, asked as a chat (docs/next-edit-plan.md, 4.3): a fixed system message, then one
/// user message with the related excerpts, the recent edits, the rename being made, the file, the lines above the region,
/// the region with the caret marked and the lines below. The model writes the region again between &lt;region&gt; tags
/// with the edit it expects next, and the answer is diffed against the old region like Zeta's. A thinking block, code
/// fences and the caret marker are taken out; an answer without its closing tag was cut off and is no prediction, and one
/// that rewrites most of the region's lines is thrown away.
/// </summary>
internal sealed class ChatEditFormat(int regionLines = 8, double tokenFactor = 1.3) : IPredictionFormat
{
    public const string NAME = "chat";
    public const string CURSOR = "<|cursor|>";
    public const string OPEN = "<region>";
    public const string CLOSE = "</region>";

    /// <summary>The system message, fixed so that a server can reuse the prompt's start from one request to the next.</summary>
    public const string SYSTEM = """
        You predict the next edit a programmer will make. You see their recent edits and a region of the file with <|cursor|>.
        Rewrite ONLY the region, applying the edit they most likely make next, consistent with their recent edits
        (for example continuing a rename). If no edit is likely, return the region unchanged.
        Reply with exactly:
        <region>
        ...the region, every line, without <|cursor|>...
        </region>
        Nothing else.
        """;

    // Lines of the file shown above the region and below it
    private const int ABOVE_LINES = 30, BELOW_LINES = 15;

    // Tokens the related excerpts take at most
    private const int RELATED_TOKENS = 1000;

    // Of the region's lines, the share the answer may change before it is taken for a rewrite, in percent; a region of
    // fewer lines than MIN_LINES_GUARDED can't be told from an edit that way
    private const int MOST_CHANGED = 60, MIN_LINES_GUARDED = 5;

    /// <summary>The format for the near caret prediction: eight lines of region, a little over its size to write it again.</summary>
    public static readonly ChatEditFormat Near = new();

    public string Name => NAME;

    public PredictionPrompt? Build(PredictionInput input, int maxTokens)
    {
        string text = input.Text;
        if (input.Caret < 0 || input.Caret > text.Length || Ambiguous(text))
            return null;

        var starts = new List<int> { 0 };
        for (int i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1))
            starts.Add(i + 1);
        int caretLine = starts.BinarySearch(input.Caret);
        if (caretLine < 0)
            caretLine = ~caretLine - 1;

        // A couple of lines above the caret, the rest below, the region kept at its size at either end of the file
        int bottom = Math.Min(starts.Count - 1, Math.Max(0, caretLine - 2) + regionLines - 1);
        int top = Math.Max(0, bottom - regionLines + 1);
        int regionEnd = bottom + 1 < starts.Count ? starts[bottom + 1] - 1 : text.Length;
        return Build(input, maxTokens, starts[top], regionEnd);
    }

    /// <summary>
    /// The prompt with the region given, offsets into the input's text: it starts at the start of a line, ends at the end
    /// of one and holds the caret. Null when it doesn't, or the file holds the format's own markers.
    /// </summary>
    internal PredictionPrompt? Build(PredictionInput input, int maxTokens, int regionStart, int regionEnd)
    {
        string text = input.Text;
        int caret = input.Caret;
        if (regionStart < 0 || regionStart > caret || caret > regionEnd || regionEnd > text.Length || Ambiguous(text)
            || regionStart > 0 && text[regionStart - 1] != '\n' || regionEnd < text.Length && text[regionEnd] != '\n')
            return null;

        string region = text[regionStart..regionEnd];
        var user = new StringBuilder();

        var excerpts = input.Related.AsEnumerable();
        if (input.Notebook is { } notebook)
        {
            var cells = new List<PredictionExcerpt>(2);
            if (notebook.Before.Length > 0 && !Ambiguous(notebook.Before))
                cells.Add(new PredictionExcerpt(input.Path + "/before", notebook.Before));
            if (notebook.After.Length > 0 && !Ambiguous(notebook.After))
                cells.Add(new PredictionExcerpt(input.Path + "/after", notebook.After));
            excerpts = cells.Concat(excerpts);
        }
        int related = 0;
        foreach (PredictionExcerpt excerpt in excerpts)
        {
            string written = "### " + excerpt.Path.Replace('\\', '/') + "\n" + excerpt.Text + (excerpt.Text.EndsWith('\n') ? "" : "\n");
            int cost = PredictionTokens.Of(written);
            if (related + cost > RELATED_TOKENS)
                break;
            if (related == 0)
                user.Append("Related files:\n");
            related += cost;
            user.Append(written);
        }
        if (related > 0)
            user.Append('\n');

        string edits = PredictionEventText.Diffs(input.Events, PredictionEventText.CHAT_TOKENS);
        if (edits.Length > 0)
            user.Append(PredictionEventText.HEADER).Append('\n').Append(edits).Append('\n');
        if (input.Rename is { } rename)
            user.Append(rename.Summary(input.Path)).Append("\n\n");

        user.Append("File: ").Append(input.Path.Replace('\\', '/')).Append(" (").Append(input.Language).Append(")\n\n");
        user.Append(text, Back(text, regionStart, ABOVE_LINES), regionStart - Back(text, regionStart, ABOVE_LINES));
        user.Append(OPEN).Append('\n')
            .Append(text, regionStart, caret - regionStart).Append(CURSOR).Append(text, caret, regionEnd - caret)
            .Append('\n').Append(CLOSE).Append('\n');
        if (regionEnd < text.Length)
        {
            int belowEnd = Forward(text, regionEnd + 1, BELOW_LINES);
            user.Append(text, regionEnd + 1, belowEnd - regionEnd - 1);
            if (user[^1] != '\n')
                user.Append('\n');
        }

        string userText = user.ToString();
        int most = Math.Max(16, (int)(PredictionTokens.Of(region) * tokenFactor) + 16);
        var turns = new ChatTurn[] { new("system", SYSTEM), new("user", userText) };
        var request = new PredictionWireRequest(SYSTEM + "\n\n" + userText, null, most, [], NAME, turns);
        return new PredictionPrompt(request, regionStart, regionEnd, region, caret);
    }

    public PredictionParsed Parse(PredictionPrompt prompt, string output, bool complete)
    {
        string answer = output.Contains('\r') ? output.Replace("\r\n", "\n") : output;
        if (answer.Contains("<think", StringComparison.OrdinalIgnoreCase))
        {
            var filter = new ThinkFilter();
            answer = filter.Feed(answer) + filter.End();
        }

        int open = answer.IndexOf(OPEN, StringComparison.Ordinal);
        if (open < 0)
            return complete ? PredictionParsed.Nothing : PredictionParsed.Unsettled;
        int bodyStart = open + OPEN.Length;
        int close = answer.IndexOf(CLOSE, bodyStart, StringComparison.Ordinal);
        if (close < 0)
        {
            // Never closed: the token limit cut it off, and half a region is not one
            return complete ? PredictionParsed.Nothing : PredictionParsed.Unsettled;
        }

        string body = answer[bodyStart..close];
        if (body.StartsWith('\n'))
            body = body[1..];
        if (body.EndsWith('\n'))
            body = body[..^1];

        List<string> lines = [.. body.Split('\n')];
        if (lines.Count > 0 && lines[0].TrimStart().StartsWith("```", StringComparison.Ordinal))
            lines.RemoveAt(0);
        if (lines.Count > 0 && lines[^1].Trim() == "```")
            lines.RemoveAt(lines.Count - 1);

        // The caret marker comes out. A line that was only the marker goes with it, unless the caret's line was blank
        // already, and then the line is the same as it was
        string[] old = prompt.Region.Split('\n');
        int caretLine = prompt.Region.AsSpan(0, Math.Clamp(prompt.Caret - prompt.RegionStart, 0, prompt.Region.Length)).Count('\n');
        bool blankCaretLine = caretLine < old.Length && old[caretLine].AsSpan().IsWhiteSpace();
        var kept = new List<string>(lines.Count);
        int? caretAt = null;
        int length = 0;
        foreach (string line in lines)
        {
            int marker = line.IndexOf(CURSOR, StringComparison.Ordinal);
            string clean = marker < 0 ? line : line.Replace(CURSOR, "", StringComparison.Ordinal);
            if (marker >= 0 && line.Trim() == CURSOR && !blankCaretLine)
                continue;
            if (marker >= 0 && caretAt is null)
                caretAt = prompt.RegionStart + length + marker;
            kept.Add(clean);
            length += clean.Length + 1;
        }

        string updated = string.Join('\n', kept);
        if (updated == prompt.Region)
            return PredictionParsed.Nothing;

        // Most of the region's lines changed: a rewrite, not the next edit
        if (old.Length >= MIN_LINES_GUARDED)
        {
            int changed = 0;
            foreach (GitHunk hunk in GitLineDiff.Diff(old, [.. kept]))
                changed += hunk.OldCount;
            if (changed * 100 > old.Length * MOST_CHANGED)
                return PredictionParsed.Rewrote;
        }

        IReadOnlyList<PredictionEdit> edits = PredictionDiff.Edits(prompt.Region, updated, prompt.RegionStart);
        edits = PredictionDiff.WithoutRepeatedIndent(edits, prompt.Region, prompt.RegionStart, prompt.Caret, ref caretAt);
        return edits.Count == 0 ? PredictionParsed.Nothing : new PredictionParsed(edits, caretAt, true);
    }

    // Whether a text holds the markers the answer is read by, which would make the prompt ambiguous
    private static bool Ambiguous(string text) =>
        text.Contains(CURSOR, StringComparison.Ordinal) || text.Contains(OPEN, StringComparison.Ordinal) || text.Contains(CLOSE, StringComparison.Ordinal);

    // Where the line this many lines before the one starting at the offset starts
    private static int Back(string text, int at, int lines)
    {
        int start = at;
        for (int i = 0; i < lines && start > 0; i++)
            start = start < 2 ? 0 : text.LastIndexOf('\n', start - 2) + 1;
        return start;
    }

    // Where this many lines from the offset end, after the last one's newline, or at the end of the text
    private static int Forward(string text, int at, int lines)
    {
        int end = at;
        for (int i = 0; i < lines && end < text.Length; i++)
        {
            int next = text.IndexOf('\n', end);
            end = next < 0 ? text.Length : next + 1;
        }
        return end;
    }
}
