using Horizon.HIDL.Parsing;

namespace Hex.Core;

/// <summary>
/// A change made in the designer written into the code of a layout somebody wrote by hand, as edits of the places it
/// touches and the rest of the text exactly as it was, its handlers, its comments and its spacing. The designer writing
/// the whole layout out again threw away whatever it can't write (a func, a statement after a declaration), so a file
/// like that is only ever edited this way, and a change that can't be is turned down rather than written as something
/// else. Text in and text out, the editor checks that what comes out builds the layout it was asked for.
/// </summary>
internal static class HexLayoutEdits
{
    /// <summary>
    /// The code with the change from one shape of the layout to the other in it, or null and why not.
    /// </summary>
    /// <param name="renames">The components given another name since <paramref name="before"/>, in the order they were.</param>
    /// <param name="original">The code as it was opened and the shape it had, so a value put back is written the way it was.</param>
    public static string? Rewrite(string text, HexLayoutShape before, HexLayoutShape after, IReadOnlyList<(string From, string To)> renames,
        (string Text, HexLayoutShape Shape)? original, out string why)
    {
        why = string.Empty;

        foreach (var (from, to) in renames)
        {
            if (before.Find(from) is null)
                continue;
            if (HexLayoutText.Uses(text, to).Count > 0)
                return Fail($"the code has a {to} in it already", out why);

            text = Renamed(text, from, to);
            before = before.Renamed(from, to);
        }

        // Whatever is inside of something that goes, goes first
        for (int i = before.Parts.Count - 1; i >= 0; i--)
        {
            HexLayoutPart gone = before.Parts[i];
            if (after.Find(gone.Name) is not null)
                continue;
            if (Without(text, gone.Name, out why) is not { } taken)
                return null;
            text = taken;
        }

        foreach (HexLayoutPart gone in before.Parts)
        {
            if (after.Find(gone.Name) is null && HexLayoutText.Uses(text, gone.Name).Count > 0)
                return Fail($"{gone.Name} is used elsewhere in the code, take it out there first", out why);
        }

        text = Call(text, "design", before.Design, after.Design, top: true);
        text = Call(text, "hidden_layers", before.Hidden, after.Hidden, top: false);

        Dictionary<(string, string), (string Value, string Text)> written = original is { } opened ? Originals(opened.Text, opened.Shape) : [];
        foreach (HexLayoutPart part in after.Parts)
        {
            if (before.Find(part.Name) is not { } was)
                continue;
            if (Changed(text, was, part, written, out why) is not { } changed)
                return null;
            text = changed;
        }

        foreach (HexLayoutPart part in after.Parts)
        {
            if (before.Find(part.Name) is not null)
                continue;
            if (Added(text, after, part, out why) is not { } added)
                return null;
            text = added;
        }

        return Ordered(text, after, out why);
    }

    /* Components given another name, taken out and put in */

    // Every use of a name as a variable called something else
    private static string Renamed(string text, string from, string to)
    {
        List<SourceSpan> uses = HexLayoutText.Uses(text, from);
        for (int i = uses.Count - 1; i >= 0; i--)
            text = text[..uses[i].Index] + to + text[(uses[i].Index + uses[i].Length)..];
        return text;
    }

    // A component's declaration taken out, and what was said to it after it was made
    private static string? Without(string text, string name, out string why)
    {
        List<HexStatement> statements = HexLayoutText.Statements(text);
        if (statements.Find(statement => statement.Declares == name) is null)
            return Fail($"{name} isn't declared in the code on its own, so the designer can't take it out", out why);

        why = string.Empty;
        for (int i = statements.Count - 1; i >= 0; i--)
        {
            if (statements[i].Declares == name || statements[i].Subject == name)
                text = Cut(text, statements[i]);
        }

        return text;
    }

    // A new component's declaration put after the one before it inside of the same thing, or after what it's inside of
    private static string? Added(string text, HexLayoutShape after, HexLayoutPart part, out string why)
    {
        why = string.Empty;
        List<HexStatement> statements = HexLayoutText.Statements(text);
        string declaration = part.Declaration.TrimEnd('\n', '\r');

        HexLayoutPart? previous = null;
        foreach (HexLayoutPart sibling in after.ChildrenOf(part.Parent))
        {
            if (sibling == part)
                break;
            previous = sibling;
        }

        string? anchor = previous?.Name ?? part.Parent;
        if (anchor is not null)
        {
            if (statements.Find(statement => statement.Declares == anchor) is not { } at)
                return Fail($"{anchor} isn't declared in the code on its own, so the designer can't put anything next to it", out why);

            int lineEnd = LineEnd(text, LastOf(statements, anchor, at).End);
            return text[..lineEnd] + "\n\n" + declaration + text[lineEnd..];
        }

        // First on the screen, before whatever else is on it, or at the end of the code when nothing is
        HexLayoutPart? next = after.ChildrenOf(null).FirstOrDefault(sibling => sibling != part && statements.Exists(statement => statement.Declares == sibling.Name));
        if (next is not null && statements.Find(statement => statement.Declares == next.Name) is { } first)
        {
            int start = LineStart(text, CommentsOver(text, first.Start));
            return text[..start] + declaration + "\n\n" + text[start..];
        }

        return text.TrimEnd('\n') + "\n\n" + declaration + "\n";
    }

    // The last statement about a component that says something to it right after its declaration, so a new one goes after those
    private static HexStatement LastOf(List<HexStatement> statements, string name, HexStatement declaration)
    {
        HexStatement last = declaration;
        for (int i = statements.IndexOf(declaration) + 1; i < statements.Count && statements[i].Subject == name; i++)
            last = statements[i];
        return last;
    }

    /* The properties of a component */

    private static string? Changed(string text, HexLayoutPart was, HexLayoutPart now, Dictionary<(string, string), (string Value, string Text)> originals, out string why)
    {
        why = string.Empty;
        if (was.Kind == now.Kind && was.Properties.SequenceEqual(now.Properties))
            return text;

        if (was.Kind != now.Kind)
        {
            if (Declaration(text, now.Name, out why) is not { Kind.IsKnown: true } declared)
                return null;
            text = HexSourceEdits.SetValue(text, declared.Kind, now.Kind);
        }

        string? before = null;
        foreach (var (key, value) in now.Properties)
        {
            if (was.Value(key) != value)
            {
                string? kept = originals.TryGetValue((now.Name, key), out var opened) && opened.Value == value ? opened.Text : null;
                if (Set(text, now.Name, key, value, kept, before, out why) is not { } set)
                    return null;
                text = set;
            }

            before = key;
        }

        foreach (var (key, _) in was.Properties)
        {
            if (now.Value(key) is not null)
                continue;
            if (Declaration(text, now.Name, out why) is not { } declared)
                return null;
            if (declared.Entries.Any(entry => entry.Key == key))
                text = HexSourceEdits.Remove(text, declared.Braces, declared.Entries, key);
        }

        return text;
    }

    // A key of a component's properties set, written where it is or put in after the one before it. Where it is, a value
    // put back to how it was opened is written the way it was then
    private static string? Set(string text, string name, string key, string value, string? kept, string? after, out string why)
    {
        if (Declaration(text, name, out why) is not { } declared)
            return null;

        if (!declared.Braces.IsKnown)
        {
            // compositor.label() with nothing in it gets braces
            if (declared.Arguments.Length == 2)
                return text[..declared.Arguments.Index] + "({ " + key + ": " + value + " })" + text[(declared.Arguments.Index + 2)..];
            return Fail($"the properties of {name} aren't written as an object in its declaration, so the designer can't change them", out why);
        }

        IReadOnlyList<HexEntry> entries = declared.Entries;
        if (entries.FirstOrDefault(entry => entry.Key == key) is { Key: not null } entry)
            return HexSourceEdits.SetValue(text, entry.Value, kept ?? Indented(value, IndentOf(text, entry.Name.Index)));

        // After the one it comes after when that one is written, at the end when not, with a comma after it when the
        // ones over lines all have one
        string? behind = after is not null && entries.Any(other => other.Key == after) ? after : null;
        bool last = behind is null || entries.Count == 0 || entries[^1].Key == behind;
        bool overLines = text.AsSpan(declared.Braces.Index, declared.Braces.Length).Contains('\n');
        bool commas = overLines && entries.Count > 0 && CommaAfter(text, entries[^1].End, declared.Braces.Index + declared.Braces.Length - 1);
        return HexSourceEdits.Insert(text, declared.Braces, entries, key, last && commas ? value + "," : value, behind);
    }

    private static HexStatement? Declaration(string text, string name, out string why)
    {
        why = string.Empty;
        if (HexLayoutText.Statements(text).Find(statement => statement.Declares == name) is { } found)
            return found;

        Fail($"{name} isn't declared in the code on its own, so the designer can't change it", out why);
        return null;
    }

    // The values of every component as they were written in the file it was opened from, by name and key, with what Hex
    // writes for each, so a value put back to what it was is written the way it was and not the way Hex would
    private static Dictionary<(string, string), (string Value, string Text)> Originals(string text, HexLayoutShape shape)
    {
        var originals = new Dictionary<(string, string), (string Value, string Text)>();
        foreach (HexStatement statement in HexLayoutText.Statements(text))
        {
            if (statement.Declares is not { } name || shape.Find(name) is not { } part)
                continue;

            foreach (HexEntry entry in statement.Entries)
            {
                if (part.Value(entry.Key) is { } value)
                    originals[(name, entry.Key)] = (value, text.Substring(entry.Value.Index, entry.Value.Length));
            }
        }

        return originals;
    }

    /* The two calls that aren't components */

    // compositor.design and compositor.hidden_layers written again, taken out or put in, the design near the top and the
    // hidden layers at the end
    private static string Call(string text, string call, string? was, string? now, bool top)
    {
        if (was == now)
            return text;

        List<HexStatement> statements = HexLayoutText.Statements(text);
        HexStatement? found = statements.Find(statement => statement.Call == call);
        if (found is not null)
            return now is null ? Cut(text, found) : text[..found.Start] + now.TrimEnd('\n') + text[found.End..];
        if (now is null)
            return text;

        if (!top)
            return text.TrimEnd('\n') + "\n\n" + now.TrimEnd('\n') + "\n";

        int define = text.IndexOf("#define", StringComparison.Ordinal);
        int at = define >= 0 ? LineEnd(text, define) : 0;
        return define >= 0 ? text[..at] + "\n\n" + now.TrimEnd('\n') + text[at..] : now.TrimEnd('\n') + "\n\n" + text;
    }

    /* The order things are in */

    // The declarations of whatever is inside of the same thing put in the order the designer has them in, a component
    // that's out of place moved after the one before it with everything inside of it and said to it
    private static string? Ordered(string text, HexLayoutShape after, out string why)
    {
        why = string.Empty;
        var parents = new List<string?> { null };
        parents.AddRange(after.Parts.Select(part => part.Name));

        foreach (string? parent in parents)
        {
            HexLayoutPart[] children = [.. after.ChildrenOf(parent)];
            for (int i = 1; i < children.Length; i++)
            {
                List<HexStatement> statements = HexLayoutText.Statements(text);
                HexStatement? previous = statements.Find(statement => statement.Declares == children[i - 1].Name);
                HexStatement? here = statements.Find(statement => statement.Declares == children[i].Name);
                if (previous is null || here is null)
                    return Fail($"{(previous is null ? children[i - 1] : children[i]).Name} isn't declared in the code on its own, so the designer can't move things about around it", out why);
                if (here.Start > previous.Start)
                    continue;

                text = Moved(text, after, children[i].Name, children[i - 1].Name);
            }
        }

        return text;
    }

    private static string Moved(string text, HexLayoutShape after, string name, string behind)
    {
        var inside = new HashSet<string> { name };
        foreach (HexLayoutPart part in after.Parts)
        {
            if (part.Parent is { } parent && inside.Contains(parent))
                inside.Add(part.Name);
        }

        List<HexStatement> statements = HexLayoutText.Statements(text);
        var moving = statements.Where(statement => statement.Declares is { } declared && inside.Contains(declared)
            || statement.Subject is { } subject && inside.Contains(subject)).ToList();
        string block = string.Join("\n\n", moving.Select(statement => Block(text, statement)));

        for (int i = moving.Count - 1; i >= 0; i--)
            text = Cut(text, moving[i]);

        statements = HexLayoutText.Statements(text);
        HexStatement anchor = statements.Find(statement => statement.Declares == behind)!;
        int lineEnd = LineEnd(text, LastOf(statements, behind, anchor).End);
        return text[..lineEnd] + "\n\n" + block + text[lineEnd..];
    }

    /* Statements as lines of text */

    // A statement with the comments right over it, as it is written
    private static string Block(string text, HexStatement statement)
    {
        int start = FirstOnLine(text, statement.Start) ? LineStart(text, CommentsOver(text, statement.Start)) : statement.Start;
        int end = LineEnd(text, statement.End);
        return text[start..(IsBlankOrComment(text, statement.End, end) ? end : statement.End)];
    }

    // A statement taken out with the comments over it, the whole of its lines when it has them to itself, and a blank
    // line of the two round it, so taking out what was put in gives the text back as it was
    private static string Cut(string text, HexStatement statement)
    {
        int start = statement.Start, end = statement.End;
        int lineEnd = LineEnd(text, end);
        if (FirstOnLine(text, start) && IsBlankOrComment(text, end, lineEnd))
        {
            start = LineStart(text, CommentsOver(text, start));
            end = Math.Min(text.Length, lineEnd + 1);
        }

        text = text[..start] + text[end..];
        if (start >= 2 && text[start - 1] == '\n' && text[start - 2] == '\n')
            text = text[..(start - 1)] + text[start..];
        return text;
    }

    // Where the comments right over a statement start, the statement itself when there are none
    private static int CommentsOver(string text, int start)
    {
        int line = LineStart(text, start);
        while (line > 0)
        {
            int above = LineStart(text, line - 1);
            if (!text.AsSpan(above, line - 1 - above).Trim().StartsWith("//"))
                break;
            start = above;
            line = above;
        }

        return start;
    }

    private static bool FirstOnLine(string text, int index) => text.AsSpan(LineStart(text, index), index - LineStart(text, index)).IsWhiteSpace();

    private static bool CommaAfter(string text, int from, int limit)
    {
        for (int i = from; i < limit && i < text.Length; i++)
        {
            if (text[i] == ',')
                return true;
            if (!char.IsWhiteSpace(text[i]))
                return false;
        }

        return false;
    }

    private static int LineStart(string text, int index) => index <= 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;

    private static int LineEnd(string text, int index)
    {
        int at = text.IndexOf('\n', Math.Min(index, text.Length));
        return at < 0 ? text.Length : at;
    }

    private static bool IsBlankOrComment(string text, int from, int to)
    {
        ReadOnlySpan<char> rest = text.AsSpan(from, Math.Max(0, to - from)).Trim();
        return rest.IsEmpty || rest.StartsWith("//");
    }

    private static string IndentOf(string text, int index)
    {
        int line = LineStart(text, index), end = line;
        while (end < text.Length && text[end] is ' ' or '\t')
            end++;
        return text[line..end];
    }

    private static string Indented(string written, string indent) => written.Replace("\n", "\n" + indent);

    private static string? Fail(string reason, out string why)
    {
        why = reason;
        return null;
    }
}
