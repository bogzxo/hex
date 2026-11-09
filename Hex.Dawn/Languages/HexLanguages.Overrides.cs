using System.Text.RegularExpressions;

namespace Hex.Dawn;

// Ctrl+F12 on a virtual method said csharp-ls had it implemented in the method itself, on its own line, and the
// override in DeferredRenderer2D was nowhere. Visual Studio lists every override. When the server says nothing or just
// the member, the overrides and implementations are found among its references instead, the lines that declare the
// name with override on them or in a type that isn't the interface.
internal sealed partial class HexLanguages
{
    /// <summary>What a place a server said a name is at is to Ctrl+F12, looked at by the line it's on.</summary>
    internal enum MemberKind
    {
        /// <summary>Not a declaration of the name, a call or a mention.</summary>
        Used,

        /// <summary>An abstract member or one of an interface, which has nothing in it to run.</summary>
        Abstract,

        /// <summary>A virtual member, which runs unless something overrides it.</summary>
        Virtual,

        /// <summary>An override, or a member of a class or struct that implements the interface's.</summary>
        Implements
    }

    // A type's declaration on a line, with the kind of type it is
    private static readonly Regex TypeDeclared = new(@"\b(interface|class|struct|record)\s+[A-Za-z_]", RegexOptions.Compiled);

    // Words that put a name being called rather than declared after them
    private static readonly HashSet<string> NotTypes = ["return", "new", "await", "throw", "yield", "else", "case", "goto", "in", "is", "as", "out", "ref", "nameof", "typeof", "using", "var", "not", "and", "or", "when"];

    /// <summary>
    /// Whether the name at a column of a line is a member being declared there, a type or modifier in front of it and
    /// a bracket, a generic, a body or an arrow after it. IFoo.Name for one implemented explicitly counts.
    /// </summary>
    internal static bool DeclaresMember(string line, int column, string word)
    {
        if (column < 0 || column + word.Length > line.Length || string.CompareOrdinal(line, column, word, 0, word.Length) != 0)
            return false;

        string after = line[(column + word.Length)..].TrimStart();
        if (!(after.Length == 0 || after[0] is '(' or '<' or '{' || after.StartsWith("=>", StringComparison.Ordinal)))
            return false;

        string before = line[..column];

        // IFoo.Name, the interface's name goes with the member's
        if (before.EndsWith('.'))
        {
            int start = before.Length - 1;
            while (start > 0 && (char.IsLetterOrDigit(before[start - 1]) || before[start - 1] is '_' or '<' or '>' or ','))
                start--;
            if (start == before.Length - 1 || start == 0 || before[start - 1] != ' ')
                return false;
            before = before[..start];
        }

        before = before.TrimEnd();
        if (before.Length == 0 || before.Contains('=') || before.Contains(';') || before.Contains('"') || before.TrimStart().StartsWith("//", StringComparison.Ordinal))
            return false;

        char last = before[^1];
        if (!(char.IsLetterOrDigit(last) || last is '_' or '>' or ']' or '?' or ')'))
            return false;

        // A bracket in front is a call's or an if's, unless it's a tuple the member gives back after its modifiers
        int open = before.IndexOf('(');
        if (open >= 0)
        {
            string head = before[..open];
            if (head.Length > 0 && !char.IsWhiteSpace(head[^1]) || NotTypes.Contains(LastWord(head.TrimEnd())) || Statements.Contains(LastWord(head.TrimEnd())))
                return false;
        }

        return !NotTypes.Contains(LastWord(before));
    }

    // Words that start a statement with a bracket after them, if (x) Foo() is a call
    private static readonly HashSet<string> Statements = ["if", "while", "for", "foreach", "switch", "lock", "catch", "fixed", "checked", "unchecked"];

    // The word a text ends with, empty when it ends with something else
    private static string LastWord(string text)
    {
        int start = text.Length;
        while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
            start--;
        return text[start..];
    }

    /// <summary>Whether a line is inside an interface, going by the nearest type declared above it.</summary>
    internal static bool InInterface(IReadOnlyList<string> lines, int line)
    {
        for (int at = Math.Min(line, lines.Count - 1); at >= 0; at--)
        {
            string text = lines[at].TrimStart();
            if (text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith('*') || text.StartsWith("/*", StringComparison.Ordinal))
                continue;

            if (TypeDeclared.Match(text) is { Success: true } type)
                return type.Groups[1].Value == "interface";
        }

        return false;
    }

    /// <summary>What the name at a place of a file is, by the line it's on and the type around it.</summary>
    internal static MemberKind KindOf(IReadOnlyList<string> lines, int line, int column, string word)
    {
        if (line < 0 || line >= lines.Count || !DeclaresMember(lines[line].TrimEnd('\r'), column, word))
            return MemberKind.Used;

        string before = lines[line][..column];
        if (Regex.IsMatch(before, @"\boverride\b"))
            return MemberKind.Implements;
        if (Regex.IsMatch(before, @"\babstract\b"))
            return MemberKind.Abstract;
        if (Regex.IsMatch(before, @"\bvirtual\b"))
            return MemberKind.Virtual;

        // One of an interface with a body of its own (a default) still runs, the rest are the interface's word for it
        if (InInterface(lines, line))
            return lines[line].TrimEnd().EndsWith(';') ? MemberKind.Abstract : MemberKind.Virtual;
        return MemberKind.Implements;
    }

    // The lines of a file, off the editor that has it open or the disk, each file once
    private string[] LinesOf(string path, Dictionary<string, string[]> lines)
    {
        if (!lines.TryGetValue(path, out string[]? text))
            lines[path] = text = context.Documents.Find(path) is { } open ? open.Editor.Text.Split('\n') : HexSearch.ReadLines(path);
        return text;
    }

    /// <summary>
    /// Whether what a server said implements a name is only the name itself, nothing at all or a virtual, abstract or
    /// interface member, which is when its overrides are looked for.
    /// </summary>
    private bool OnlyItself(IReadOnlyList<LanguageServerPlace> places, string word, Dictionary<string, string[]> lines) =>
        places.All(place => KindOf(LinesOf(place.Path, lines), place.Line, NameColumn(place, word), word) is MemberKind.Abstract or MemberKind.Virtual);

    /// <summary>
    /// The overrides and implementations of the name at the caret among the places the server says it's used, the
    /// virtual member itself with them since it runs too. Nothing when the server has no references either.
    /// </summary>
    private void FindOverrides(LanguageServer server, string path, UITextPosition caret, string word, Action<List<LanguageServerPlace>> then)
    {
        if (!server.Offers("referencesProvider"))
        {
            then([]);
            return;
        }

        When(server.References(path, caret.Line, caret.Column), all =>
        {
            var lines = new Dictionary<string, string[]>(PlatformPaths.Comparer);
            var found = new List<LanguageServerPlace>();
            bool overridden = false;
            foreach (LanguageServerPlace place in all)
            {
                MemberKind kind = KindOf(LinesOf(place.Path, lines), place.Line, NameColumn(place, word), word);
                overridden |= kind == MemberKind.Implements;
                if (kind is MemberKind.Implements or MemberKind.Virtual)
                    found.Add(place);
            }

            // A virtual member nothing overrides is just itself, and that's what was said already
            then(overridden ? found : []);
        });
    }
}
