using System.Text;
using Hex.Core.Git;

namespace Hex.Core;

/// <summary>
/// Zed's Zeta 2 (Seed-Coder fine-tuned, Zed's V0211SeedCoder): the file after the editable region first, then other
/// files' excerpts, the edit history and the file before the region, then the region with the caret marked, and the
/// model writes the region again with its edit made, ending >>>>>>> UPDATED. The answer is diffed against the old region.
/// </summary>
internal sealed class Zeta2Format : IPredictionFormat
{
    public const string SUFFIX = "<[fim-suffix]>";
    public const string PREFIX = "<[fim-prefix]>";
    public const string MIDDLE = "<[fim-middle]>";
    public const string FILE = "<filename>";
    public const string CURRENT = "<<<<<<< CURRENT";
    public const string SEPARATOR = "=======";
    public const string UPDATED = ">>>>>>> UPDATED";
    public const string CURSOR = "<|user_cursor|>";
    public const string ACCEPTED = "// User accepted prediction:\n";

    // The editable region comes back whole, the lines before the caret first, so its size is what a prediction costs:
    // at 50 tokens a second Zed's 350 tokens with the caret in the middle is over three seconds before the edit starts.
    // Here it's smaller and mostly below the caret, where the next edit usually is, a few lines above for the model to
    // find its place. HEX_PREDICT_REGION_TOKENS tries another size.
    private const int USUAL_REGION_TOKENS = 150;
    private const int ABOVE_SHARE = 4;
    private static readonly int REGION_TOKENS = int.TryParse(Environment.GetEnvironmentVariable("HEX_PREDICT_REGION_TOKENS"), out int asked)
        ? Math.Clamp(asked, 30, 350) : USUAL_REGION_TOKENS;

    // Zed's other budgets: the context round the region, the whole prompt (less a tenth to be safe), edit events
    private const int CONTEXT_TOKENS = 150;
    private const int PROMPT_TOKENS = 4096;
    private const int MOST_EVENTS = 6;

    // Lines the old region has, after a change, that settle an answer still streaming
    private const int SAME_TO_STOP = 4;

    // What a file can't hold for its prompt to be read one way only
    private static readonly string[] tokens = [SUFFIX, PREFIX, MIDDLE, FILE, CURSOR];
    private static readonly string[] markerLines = [CURRENT, SEPARATOR, UPDATED];

    public string Name => PredictionFormats.ZETA2;

    public PredictionPrompt? Build(PredictionInput input, int maxTokens)
    {
        if (Ambiguous(input.Text) || input.Caret < 0 || input.Caret > input.Text.Length)
            return null;
        var (regionStart, regionEnd, contextStart, contextEnd) = Ranges(input.Text, input.Caret);
        return Build(input, maxTokens, regionStart, regionEnd, contextStart, contextEnd);
    }

    /// <summary>
    /// The prompt with the editable region and the context round it given rather than worked out, offsets into the
    /// input's text; the context's ends and the region's start at the start of a line, the region's end at the end of
    /// one. Null when they don't nest round the caret or the prompt is over budget.
    /// </summary>
    internal PredictionPrompt? Build(PredictionInput input, int maxTokens, int regionStart, int regionEnd, int contextStart, int contextEnd)
    {
        string text = input.Text;
        int caret = input.Caret;
        if (contextStart < 0 || contextStart > regionStart || regionStart > caret || caret > regionEnd || regionEnd > contextEnd || contextEnd > text.Length)
            return null;

        var suffix = new StringBuilder(SUFFIX).Append(text, regionEnd, contextEnd - regionEnd);
        EndLine(suffix);

        var cursor = new StringBuilder(FILE).Append(Slashed(input.Path)).Append('\n')
            .Append(text, contextStart, regionStart - contextStart)
            .Append(CURRENT).Append('\n')
            .Append(text, regionStart, caret - regionStart)
            .Append(CURSOR)
            .Append(text, caret, regionEnd - caret);
        EndLine(cursor);
        cursor.Append(SEPARATOR).Append('\n');

        string suffixText = suffix.ToString(), cursorText = cursor.ToString();
        int budget = PROMPT_TOKENS * 9 / 10;
        int afterCursor = Math.Max(0, budget
            - (PredictionTokens.Of(PredictionTokens.Bytes(suffixText) + PREFIX.Length)
               + PredictionTokens.Of(PredictionTokens.Bytes(cursorText) + MIDDLE.Length)));

        string history = History(input.Events, afterCursor);
        int afterHistory = Math.Max(0, afterCursor - PredictionTokens.Of(PredictionTokens.Bytes(history) + 1));
        string related = Related(input.Related, afterHistory);

        var prompt = new StringBuilder(suffixText.Length + cursorText.Length + history.Length + related.Length + 64)
            .Append(suffixText).Append(PREFIX);
        if (related.Length > 0)
            prompt.Append(related).Append('\n');
        if (history.Length > 0)
            prompt.Append(history).Append('\n');
        prompt.Append(cursorText).Append(MIDDLE);

        string written = prompt.ToString();
        if (PredictionTokens.Of(written) > PROMPT_TOKENS)
            return null;

        // The whole region comes back, so it needs its own tokens, and a quarter of the setting for what's added
        string region = text[regionStart..regionEnd];
        int most = Math.Clamp(PredictionTokens.Of(region) + maxTokens / 4, 1, Math.Max(1, maxTokens));

        // No stop sequence: the end marker is seen as it streams and the request closed then, where a server that stops
        // at it would leave an answer cut short by the token limit looking the same as a finished one
        var request = new PredictionWireRequest(written, null, most, [], Name);
        return new PredictionPrompt(request, regionStart, regionEnd, region, caret);
    }

    public PredictionParsed Parse(PredictionPrompt prompt, string output, bool complete)
    {
        string answer = output.Contains('\r') ? output.Replace("\r\n", "\n") : output;

        int end = answer.IndexOf(UPDATED, StringComparison.Ordinal);
        if (end >= 0)
        {
            string body = answer[..end];
            if (body.EndsWith('\n') && !prompt.Region.EndsWith('\n'))
                body = body[..^1];
            return Settle(prompt, body);
        }

        // Only whole lines count until the answer has ended
        int lastNewline = answer.LastIndexOf('\n');
        string[] arrived = lastNewline < 0 ? [] : answer[..lastNewline].Split('\n');
        string[] old = prompt.Region.Split('\n');

        if (!complete)
            return Early(prompt, old, arrived) ?? PredictionParsed.Unsettled;

        // Ended without the marker, cut short by the token limit: the unfinished last line dropped, the rest taken as
        // far as it goes and the old region's lines after it assumed unchanged
        if (arrived.Length == 0)
            return PredictionParsed.Nothing;
        return Settle(prompt, string.Join('\n', Aligned(old, arrived)));
    }

    /// <summary>Whether a file's text holds one of the format's tokens, or one of its markers on a line of its own.</summary>
    public static bool Ambiguous(string text)
    {
        foreach (string token in tokens)
        {
            if (text.Contains(token, StringComparison.Ordinal))
                return true;
        }

        foreach (string marker in markerLines)
        {
            for (int at = text.IndexOf(marker, StringComparison.Ordinal); at >= 0; at = text.IndexOf(marker, at + 1, StringComparison.Ordinal))
            {
                int after = at + marker.Length;
                bool startsLine = at == 0 || text[at - 1] == '\n';
                bool endsLine = after == text.Length || text[after] == '\n' || text[after] == '\r';
                if (startsLine && endsLine)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The editable region round the caret and the context round that, as offsets: the caret's line and the lines
    /// below and above it in turn up to the region's tokens (a quarter of them at most above the caret while there are
    /// lines below to take), cut back to a blank line where one is near its ends, then up to
    /// 150 tokens more of context above and below in turn. Never mid line.
    /// </summary>
    internal static (int RegionStart, int RegionEnd, int ContextStart, int ContextEnd) Ranges(string text, int caret)
    {
        var starts = new List<int> { 0 };
        for (int i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1))
            starts.Add(i + 1);
        int count = starts.Count;
        int End(int line) => line + 1 < count ? starts[line + 1] - 1 : text.Length;
        int Size(int line) => PredictionTokens.Bytes(text, starts[line], End(line) - starts[line]) + 1;
        bool Blank(int line) => text.AsSpan(starts[line], End(line) - starts[line]).IsWhiteSpace();

        int caretLine = starts.BinarySearch(caret);
        if (caretLine < 0)
            caretLine = ~caretLine - 1;

        int top = caretLine, bottom = caretLine, size = Size(caretLine), sizeAbove = 0;
        int most = REGION_TOKENS * PredictionTokens.BYTES;
        for (bool grew = true; grew;)
        {
            grew = false;
            if (bottom + 1 < count && size + Size(bottom + 1) <= most)
            {
                size += Size(++bottom);
                grew = true;
            }
            bool roomAbove = sizeAbove + (top > 0 ? Size(top - 1) : 0) <= most / ABOVE_SHARE || bottom + 1 >= count;
            if (top > 0 && roomAbove && size + Size(top - 1) <= most)
            {
                sizeAbove += Size(top - 1);
                size += Size(--top);
                grew = true;
            }
        }

        // An end that was cut short goes back to a blank line in its outer quarter, so the region is whole blocks
        if (top > 0)
        {
            for (int line = top; line < caretLine && line <= top + (caretLine - top) / 4; line++)
            {
                if (Blank(line))
                {
                    top = line + 1;
                    break;
                }
            }
        }
        if (bottom + 1 < count)
        {
            for (int line = bottom; line > caretLine && line >= bottom - (bottom - caretLine) / 4; line--)
            {
                if (Blank(line))
                {
                    bottom = line - 1;
                    break;
                }
            }
        }

        int above = top, below = bottom, context = 0;
        int contextMost = CONTEXT_TOKENS * PredictionTokens.BYTES;
        for (bool grew = true; grew;)
        {
            grew = false;
            if (above > 0 && context + Size(above - 1) <= contextMost)
            {
                context += Size(--above);
                grew = true;
            }
            if (below + 1 < count && context + Size(below + 1) <= contextMost)
            {
                context += Size(++below);
                grew = true;
            }
        }

        return (starts[top], End(bottom), starts[above], End(below));
    }

    // The edit history, newest first while it fits and at most six events, written oldest first under its header
    private static string History(IReadOnlyList<PredictionEvent> events, int most)
    {
        string header = FILE + "edit_history\n";
        int total = PredictionTokens.Of(header);
        if (total >= most)
            return "";

        var chosen = new List<string>();
        for (int i = events.Count - 1; i >= 0 && chosen.Count < MOST_EVENTS; i--)
        {
            string written = Event(events[i]);
            int cost = PredictionTokens.Of(written);
            if (total + cost > most)
                break;
            total += cost;
            chosen.Add(written);
        }

        if (chosen.Count == 0)
            return "";
        var history = new StringBuilder(header);
        for (int i = chosen.Count - 1; i >= 0; i--)
            history.Append(chosen[i]);
        return history.ToString();
    }

    /// <summary>An edit event as the model saw them in training: the unified diff's file header, then its hunks.</summary>
    internal static string Event(PredictionEvent change)
    {
        string path = Slashed(change.Path);
        return (change.Predicted ? ACCEPTED : "") + "--- a/" + path + "\n+++ b/" + path + "\n" + change.Diff;
    }

    // Other files' excerpts in order while they fit, each file's name once over its excerpts
    private static string Related(IReadOnlyList<PredictionExcerpt> excerpts, int most)
    {
        var related = new StringBuilder();
        int total = 0;
        string? last = null;
        foreach (PredictionExcerpt excerpt in excerpts)
        {
            string path = Slashed(excerpt.Path);
            string header = path == last ? "" : FILE + path + "\n";
            int cost = PredictionTokens.Of(header) + PredictionTokens.Of(PredictionTokens.Bytes(excerpt.Text) + (excerpt.Text.EndsWith('\n') ? 0 : 1));
            if (total + cost > most)
                break;
            total += cost;
            related.Append(header).Append(excerpt.Text);
            if (!excerpt.Text.EndsWith('\n'))
                related.Append('\n');
            last = path;
        }
        return related.ToString();
    }

    // An answer still streaming that can be settled now: lines as the old region has them past the caret's line with no
    // change (nothing near the caret), or a change followed by four lines the old region has (the rest is unchanged)
    private static PredictionParsed? Early(PredictionPrompt prompt, string[] old, string[] arrived)
    {
        if (arrived.Length == 0)
            return null;

        string[] lines = new string[arrived.Length];
        for (int i = 0; i < arrived.Length; i++)
            lines[i] = arrived[i].Replace(CURSOR, "", StringComparison.Ordinal);

        int caretLine = prompt.Region.AsSpan(0, prompt.Caret - prompt.RegionStart).Count('\n');
        int same = 0;
        while (same < lines.Length && same < old.Length && lines[same] == old[same])
            same++;

        if (same == lines.Length)
            return lines.Length >= caretLine + 1 + SAME_TO_STOP ? PredictionParsed.Nothing : null;

        int tail = lines.Length - SAME_TO_STOP;
        if (tail <= same)
            return null;

        // Where the old region has the last four lines, the place nearest where they arrived
        int found = -1;
        for (int at = same; at + SAME_TO_STOP <= old.Length; at++)
        {
            bool match = true;
            for (int i = 0; i < SAME_TO_STOP && match; i++)
                match = old[at + i] == lines[tail + i];
            if (match && (found < 0 || Math.Abs(at - tail) < Math.Abs(found - tail)))
                found = at;
        }
        if (found < 0)
            return null;

        return Settle(prompt, string.Join('\n', arrived.Concat(old.Skip(found + SAME_TO_STOP))));
    }

    // The lines that arrived and the old region's lines after the place they reached
    private static IEnumerable<string> Aligned(string[] old, string[] arrived)
    {
        string[] lines = new string[arrived.Length];
        for (int i = 0; i < arrived.Length; i++)
            lines[i] = arrived[i].Replace(CURSOR, "", StringComparison.Ordinal);

        List<GitHunk> hunks = GitLineDiff.Diff(old, lines);
        int reached = old.Length;
        if (hunks.Count > 0)
        {
            GitHunk last = hunks[^1];
            if (last.NewStart + last.NewCount == lines.Length)
                reached = last.OldStart + Math.Min(last.OldCount, last.NewCount);
        }
        return arrived.Concat(old.Skip(reached));
    }

    // The new region as edits of the old, with the caret where the model marked it
    private static PredictionParsed Settle(PredictionPrompt prompt, string body)
    {
        int cursorAt = body.IndexOf(CURSOR, StringComparison.Ordinal);
        if (cursorAt >= 0)
            body = body.Replace(CURSOR, "", StringComparison.Ordinal);

        if (SameButTrailingSpace(prompt.Region, body))
            return PredictionParsed.Nothing;

        IReadOnlyList<PredictionEdit> edits = PredictionDiff.Edits(prompt.Region, body, prompt.RegionStart);
        if (edits.Count == 0)
            return PredictionParsed.Nothing;
        return new PredictionParsed(edits, cursorAt >= 0 ? prompt.RegionStart + cursorAt : null, true);
    }

    // Whether two texts differ only in whitespace at the ends of lines
    private static bool SameButTrailingSpace(string old, string updated)
    {
        string[] a = old.Split('\n'), b = updated.Split('\n');
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
        {
            if (!a[i].AsSpan().TrimEnd().SequenceEqual(b[i].AsSpan().TrimEnd()))
                return false;
        }
        return true;
    }

    private static void EndLine(StringBuilder builder)
    {
        if (builder.Length == 0 || builder[^1] != '\n')
            builder.Append('\n');
    }

    // A path as the model was trained on them, with forward slashes and none in front
    private static string Slashed(string path) => path.Replace('\\', '/').TrimStart('/');
}
