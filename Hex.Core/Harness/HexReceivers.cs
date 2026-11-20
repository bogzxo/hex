using System.Text.RegularExpressions;

namespace Hex.Core;

/// <summary>
/// Which uses of a name are plausibly of one container's member, for hex_refs Container.Name, by text: a declaration of it
/// inside the container (the index's symbols say what they're in), or a use whose receiver is the container itself, this
/// or nothing in the container's own files, or a name declared as one (a field, a parameter, a local, a property or a
/// method that gives one, anywhere in the file, or a member of that type anywhere). Not a compiler, a cheap guess that
/// leaves out Label.Text when UIDrawList.Text was asked.
/// </summary>
internal sealed class HexReceivers
{
    private readonly string container;
    private readonly Regex mentions;
    private readonly Regex typed;
    private readonly Dictionary<HexIndexedFile, HashSet<string>> local = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<HexIndexedFile, bool> own = new(ReferenceEqualityComparer.Instance);

    // Members of any file declared as one (UIDrawList Draw => ..., a field), what a receiver further away is told by
    private readonly HashSet<string> members = new(StringComparer.Ordinal);

    public HexReceivers(HexIndexSnapshot index, string container)
    {
        this.container = container;
        string type = Regex.Escape(container);
        mentions = new Regex($@"\b{type}\b", RegexOptions.CultureInvariant);

        // C name, C? name, C<T> name, C[] name (C#, Java, C++), name: C (Python, TypeScript, Rust, Go's name C too),
        // name = new C( / name = C( / name := C{
        typed = new Regex($@"\b{type}\b(?:<[^<>]*>)?\??(?:\[\])?[*&]?\s+@?(?<name>[A-Za-z_]\w*)|(?<name>[A-Za-z_]\w*)\s*(?::|:=|=)\s*(?:new\s+|&)?{type}\b",
            RegexOptions.CultureInvariant);

        foreach (HexIndexedFile file in index.Files)
        {
            if (!Mentions(file))
                continue;
            foreach (HexSymbol symbol in file.Symbols)
            {
                if (symbol.Line < file.Lines.Length && file.Lines[symbol.Line] is var line && line.Contains(container, StringComparison.Ordinal)
                    && typed.Matches(line).Any(match => match.Groups["name"].Value == symbol.Name))
                    members.Add(symbol.Name);
            }
        }
    }

    /// <summary>Whether a file says the container anywhere, the ones looked in at all.</summary>
    public bool Mentions(HexIndexedFile file) =>
        file.Symbols.Any(symbol => InContainer(symbol.Container) || symbol.Name == container)
        || file.Lines.Any(line => line.Contains(container, StringComparison.Ordinal) && mentions.IsMatch(line));

    // A symbol's container is this one, Namespace.UIDrawList or UIDrawList.Inner
    private bool InContainer(string said) =>
        said == container || said.EndsWith("." + container, StringComparison.Ordinal) || said.StartsWith(container + ".", StringComparison.Ordinal)
        || said.Contains("." + container + ".", StringComparison.Ordinal);

    // A file the container is declared in (a partial one's every file), where this and nothing are it
    private bool Own(HexIndexedFile file)
    {
        if (!own.TryGetValue(file, out bool is_))
            own[file] = is_ = file.Symbols.Any(symbol => symbol.Name == container && symbol.IsType || InContainer(symbol.Container));
        return is_;
    }

    private HashSet<string> Local(HexIndexedFile file)
    {
        if (local.TryGetValue(file, out HashSet<string>? names))
            return names;
        names = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in file.Lines)
        {
            if (!line.Contains(container, StringComparison.Ordinal))
                continue;
            foreach (Match match in typed.Matches(line))
                names.Add(match.Groups["name"].Value);
        }

        return local[file] = names;
    }

    /// <summary>
    /// The column of the first use of the name on a line that's plausibly the container's, -1 for none, with whether the
    /// line had the name and it was left out.
    /// </summary>
    public int FirstOf(Regex pattern, HexIndexedFile file, int at, string name, out bool dropped)
    {
        dropped = false;
        string line = file.Lines[at];
        if (line.Length == 0)
            return -1;

        // A declaration of the name on the line is the container's or another's, the index says which
        HexSymbol? declares = file.Symbols.FirstOrDefault(symbol => symbol.Line == at && symbol.Name == name);
        bool any = false;
        foreach (ValueMatch match in pattern.EnumerateMatches(line))
        {
            if (HexCodeIndex.InCommentOrText(file, line, match.Index))
                continue;
            any = true;
            if (declares is not null && Math.Abs(declares.Column - match.Index) <= 1)
            {
                if (InContainer(declares.Container))
                    return match.Index;
                continue;
            }
            if (Plausible(file, line, match.Index))
                return match.Index;
        }

        dropped = any;
        return -1;
    }

    private bool Plausible(HexIndexedFile file, string line, int column)
    {
        int at = column - 1;
        while (at >= 0 && line[at] == ' ')
            at--;

        // No receiver, a call or use of its own member from inside it
        if (at < 0 || (line[at] != '.' && !(line[at] == '>' && at > 0 && line[at - 1] == '-') && !(line[at] == ':' && at > 0 && line[at - 1] == ':')))
            return Own(file);

        // Past the . (or ?., ->, ::) to what's before it
        at -= line[at] == '.' ? 1 : 2;
        if (at >= 0 && line[at] == '?')
            at--;
        if (at >= 0 && line[at] == '!')
            at--;
        while (at >= 0 && line[at] == ' ')
            at--;

        // A call or an index, Draw().Text or lists[i].Text: the name before its brackets
        if (at >= 0 && line[at] is ')' or ']')
        {
            char open = line[at] == ')' ? '(' : '[', close = line[at];
            int depth = 0;
            for (; at >= 0; at--)
            {
                if (line[at] == close)
                    depth++;
                else if (line[at] == open && --depth == 0)
                    break;
            }
            at--;
        }

        int end = at + 1;
        while (at >= 0 && (char.IsLetterOrDigit(line[at]) || line[at] == '_'))
            at--;
        string receiver = line[(at + 1)..end];
        if (receiver.Length == 0)
            return false;
        if (receiver == container)
            return true;
        if (receiver is "this" or "base" or "self" or "super" or "Me")
            return Own(file);
        return Local(file).Contains(receiver) || members.Contains(receiver);
    }
}
