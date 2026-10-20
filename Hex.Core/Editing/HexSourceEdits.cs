using Horizon.HIDL.Parsing;

namespace Hex.Core;

/// <summary>One key of an object as it is written, where its name is and where its value is.</summary>
internal readonly record struct HexEntry(string Key, SourceSpan Name, SourceSpan Value)
{
    public int End => Value.Index + Value.Length;
}

/// <summary>
/// Changes to the text of a file of HIDL by where things are written in it, the rest of the file left exactly as it was,
/// its comments, its spacing and its blank lines. Nothing here runs anything or knows about Hex, it's text in and text
/// out, which is what the edits an extension asks for (hex.edit and the rest, see DawnScene.ExtensionEdits) come down
/// to and what Horizon.Tests can check without a window.
/// </summary>
internal static class HexSourceEdits
{
    private const string INDENT = "    ";

    /// <summary>The value at a place written anew.</summary>
    public static string SetValue(string text, SourceSpan value, string written) =>
        text[..value.Index] + written + text[(value.Index + value.Length)..];

    /// <summary>A key given another name, its value left alone.</summary>
    public static string Rename(string text, SourceSpan name, string key) =>
        text[..name.Index] + key + text[(name.Index + name.Length)..];

    /// <summary>
    /// A key added to an object, written the way the object is. Over lines at the indentation of its other keys, on one
    /// line after the last of them, or the object opened up over lines if it was empty. After a key if it says which,
    /// at the end if not.
    /// </summary>
    /// <param name="braces">Where the object is, from its { to its }.</param>
    /// <param name="entries">Its keys as written, in order.</param>
    /// <param name="written">The value, written by <see cref="HexValueText"/> at no indentation, lines and all.</param>
    public static string Insert(string text, SourceSpan braces, IReadOnlyList<HexEntry> entries, string key, string written, string? after = null)
    {
        int close = braces.Index + braces.Length - 1;
        bool overLines = text.AsSpan(braces.Index, braces.Length).Contains('\n');

        if (entries.Count == 0)
        {
            string outer = IndentOf(text, braces.Index);
            string inner = outer + INDENT;
            return text[..(braces.Index + 1)] + "\n" + inner + key + ": " + Indented(written, inner) + "\n" + outer + text[close..];
        }

        int at = after is null ? entries.Count - 1 : Math.Max(0, IndexOf(entries, after));
        HexEntry before = entries[at];

        // The comma of the one it goes after, written now if that one was the last and had none
        int comma = CommaAfter(text, before.End, close);
        string head = comma >= 0 ? text[..(comma + 1)] : text[..before.End] + ",";
        string tail = comma >= 0 ? text[(comma + 1)..] : text[before.End..];
        bool last = at == entries.Count - 1;

        if (!overLines)
            return head + " " + key + ": " + written + (last ? string.Empty : ",") + tail;

        // Over lines it goes on a line of its own after the one it follows, past a comment at the end of that line
        int rest = comma >= 0 ? comma + 1 : before.End;
        int lineEnd = LineEnd(text, rest);
        if (IsBlankOrComment(text, rest, lineEnd))
        {
            head += text[rest..lineEnd];
            tail = text[lineEnd..];
        }

        string indent = IndentOf(text, before.Name.Index);
        return head + "\n" + indent + key + ": " + Indented(written, indent) + (last ? string.Empty : ",") + tail;
    }

    /// <summary>
    /// A key taken out of an object with its value, its comma and the comments over it. Over lines the lines it was on go,
    /// on one line only it. The last key of an object takes the comma of the one before it with it.
    /// </summary>
    public static string Remove(string text, SourceSpan braces, IReadOnlyList<HexEntry> entries, string key)
    {
        int index = IndexOf(entries, key);
        if (index < 0)
            return text;

        HexEntry entry = entries[index];
        int close = braces.Index + braces.Length - 1;
        bool last = index == entries.Count - 1;
        int start = StartOf(text, entry);
        int comma = CommaAfter(text, entry.End, close);
        int end = comma >= 0 ? comma + 1 : entry.End;

        // Whole lines when it had them to itself
        int lineStart = LineStart(text, start);
        int lineEnd = LineEnd(text, end);
        bool aloneOnLines = text.AsSpan(lineStart, start - lineStart).IsWhiteSpace() && IsBlankOrComment(text, end, lineEnd);
        if (aloneOnLines)
        {
            start = lineStart;
            end = Math.Min(text.Length, lineEnd + 1);
        }
        else
        {
            // On one line, the space after its comma goes with it
            while (end < close && text[end] == ' ')
                end++;
        }

        // The last one goes, the comma of the one before is the last comma left
        if (last && index > 0 && comma < 0)
        {
            int previous = CommaAfter(text, entries[index - 1].End, start);
            if (previous >= 0)
            {
                text = text[..previous] + text[(previous + 1)..];
                start--;
                end--;
            }

            if (!aloneOnLines)
            {
                // "a: 1, b: 2" loses ", b: 2" rather than "b: 2"
                while (start > 0 && text[start - 1] == ' ')
                    start--;
            }
        }

        return text[..start] + text[end..];
    }

    /// <summary>
    /// The keys of an object put in another order, each with the comments over it. What is between them (their commas,
    /// the line breaks, blank lines) stays where it is, so the commas come out right whichever key ends up last.
    /// </summary>
    /// <param name="order">Every key of the object, in the order wanted.</param>
    public static string Reorder(string text, IReadOnlyList<HexEntry> entries, IReadOnlyList<string> order)
    {
        if (entries.Count < 2 || order.Count != entries.Count)
            return text;

        var blocks = new string[entries.Count];
        var starts = new int[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            starts[i] = StartOf(text, entries[i]);
            blocks[i] = text[starts[i]..entries[i].End];
        }

        var result = new System.Text.StringBuilder(text.Length);
        result.Append(text, 0, starts[0]);
        for (int slot = 0; slot < entries.Count; slot++)
        {
            int from = IndexOf(entries, order[slot]);
            if (from < 0)
                return text;

            result.Append(blocks[from]);
            int next = slot + 1 < entries.Count ? starts[slot + 1] : text.Length;
            result.Append(text, entries[slot].End, next - entries[slot].End);
        }

        return result.ToString();
    }

    /* Finding things in the text */

    private static int IndexOf(IReadOnlyList<HexEntry> entries, string key)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].Key == key)
                return i;
        }

        return -1;
    }

    /// <summary>Where an entry starts, the first of the comment lines right over it if it is first on its line and has some.</summary>
    private static int StartOf(string text, HexEntry entry)
    {
        int start = entry.Name.Index;
        int line = LineStart(text, start);
        if (!text.AsSpan(line, start - line).IsWhiteSpace())
            return start;

        while (line > 0)
        {
            int above = LineStart(text, line - 1);
            ReadOnlySpan<char> content = text.AsSpan(above, line - 1 - above).Trim();
            if (!content.StartsWith("//"))
                break;

            start = above + LeadingSpaces(text, above);
            line = above;
        }

        return start;
    }

    /// <summary>The comma after a value, past spaces and line breaks but nothing else, -1 if there isn't one before <paramref name="limit"/>.</summary>
    private static int CommaAfter(string text, int from, int limit)
    {
        for (int i = from; i < limit && i < text.Length; i++)
        {
            if (text[i] == ',')
                return i;
            if (!char.IsWhiteSpace(text[i]))
                return -1;
        }

        return -1;
    }

    private static int LineStart(string text, int index) => index <= 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;

    private static int LineEnd(string text, int index)
    {
        int at = text.IndexOf('\n', index);
        return at < 0 ? text.Length : at;
    }

    private static bool IsBlankOrComment(string text, int from, int to)
    {
        ReadOnlySpan<char> rest = text.AsSpan(from, Math.Max(0, to - from)).Trim();
        return rest.IsEmpty || rest.StartsWith("//");
    }

    /// <summary>The spaces at the start of the line a place is on.</summary>
    private static string IndentOf(string text, int index)
    {
        int line = LineStart(text, index);
        return text.Substring(line, LeadingSpaces(text, line));
    }

    private static int LeadingSpaces(string text, int line)
    {
        int end = line;
        while (end < text.Length && (text[end] == ' ' || text[end] == '\t'))
            end++;
        return end - line;
    }

    /// <summary>A value written over lines moved to an indentation, its first line where it already is.</summary>
    private static string Indented(string written, string indent) => written.Replace("\n", "\n" + indent);
}
