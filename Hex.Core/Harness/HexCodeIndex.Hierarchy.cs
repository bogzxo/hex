namespace Hex.Core;

// The bases and derived types of a type by what its declaration says, for a language whose server has no type hierarchy
// (pyright has neither typeHierarchy nor implementation): class Square(Shape) in Python, class A extends B implements C
// in TypeScript, JavaScript, Java and Kotlin's class A : B, read off the index's declarations. hex_types and
// hex_implementations answer from it where the server can't, by name rather than by type, said so in the answer.
internal sealed partial class HexCodeIndex
{
    /// <summary>The names of the bases a type's declaration line says, the last part of a dotted name, type arguments left out.</summary>
    internal static List<string> BasesOf(string line, string name)
    {
        string code = line.Split('#')[0];
        int comment = code.IndexOf("//", StringComparison.Ordinal);
        if (comment >= 0)
            code = code[..comment];
        int at = NameColumn(code, name);
        if (at >= code.Length || !code.AsSpan(at).StartsWith(name, StringComparison.Ordinal))
            return [];
        string rest = code[(at + name.Length)..];

        // Type parameters after the name (Foo<T>, Foo[T]) aren't bases
        rest = SkipBracketed(rest.TrimStart(), '<', '>');
        rest = SkipBracketed(rest.TrimStart(), '[', ']');
        string trimmed = rest.TrimStart();

        string list;
        if (trimmed.StartsWith('('))
        {
            // Python, class Square(Shape, metaclass=ABCMeta)
            int close = trimmed.LastIndexOf(')');
            list = close > 0 ? trimmed[1..close] : trimmed[1..];
        }
        else if (trimmed.StartsWith(':'))
            list = UpTo(trimmed[1..], "{", " where ", "=>");
        else
        {
            int extends = trimmed.IndexOf("extends ", StringComparison.Ordinal), implements = trimmed.IndexOf("implements ", StringComparison.Ordinal);
            if (extends < 0 && implements < 0)
                return [];
            list = string.Join(',', new[] { (extends, 8), (implements, 11) }.Where(each => each.Item1 >= 0)
                .Select(each => UpTo(trimmed[(each.Item1 + each.Item2)..], "{", "implements ", "extends ")));
        }

        var bases = new List<string>();
        foreach (string part in SplitTopLevel(list))
        {
            string piece = part.Trim();
            if (piece.Length == 0 || piece.Contains('=') || piece.StartsWith('*'))
                continue;
            piece = piece.Split('<', '[', '(')[0].Trim();
            if (piece.Contains(' '))
                piece = piece[(piece.LastIndexOf(' ') + 1)..];
            piece = piece[(piece.LastIndexOf('.') + 1)..];
            if (piece.Length > 0 && IsWordChar(piece[0]) && piece is not ("object" or "Object"))
                bases.Add(piece);
        }

        return bases;
    }

    private static string SkipBracketed(string text, char open, char close)
    {
        if (!text.StartsWith(open))
            return text;
        for (int i = 0, depth = 0; i < text.Length; i++)
        {
            if (text[i] == open) depth++;
            else if (text[i] == close && --depth == 0) return text[(i + 1)..];
        }

        return text;
    }

    private static string UpTo(string text, params string[] stops)
    {
        int end = text.Length;
        foreach (string stop in stops)
        {
            int at = text.IndexOf(stop, StringComparison.Ordinal);
            if (at >= 0 && at < end)
                end = at;
        }

        return text[..end];
    }

    // A list split at its commas, not those inside <> [] ()
    private static IEnumerable<string> SplitTopLevel(string list)
    {
        int depth = 0, start = 0;
        for (int i = 0; i < list.Length; i++)
        {
            char c = list[i];
            if (c is '<' or '[' or '(') depth++;
            else if (c is '>' or ']' or ')') depth--;
            else if (c == ',' && depth == 0)
            {
                yield return list[start..i];
                start = i + 1;
            }
        }

        yield return list[start..];
    }

    // The languages a type of one is looked for in, a .ts class deriving from a .js one
    private static string FamilyOf(string extension) => extension switch
    {
        ".py" or ".pyi" => "py",
        ".ts" or ".tsx" or ".js" or ".jsx" or ".mjs" or ".cjs" or ".mts" or ".cts" => "js",
        ".kt" or ".kts" or ".java" => "jvm",
        _ => extension
    };

    /// <summary>Every type of the index of one family with the bases its declaration says, and what derives from each name.</summary>
    private static (List<(HexIndexedFile File, HexSymbol Type, List<string> Bases)> Types, Dictionary<string, List<int>> Derived) TypesOf(HexIndexSnapshot index, string family)
    {
        var types = new List<(HexIndexedFile, HexSymbol, List<string>)>();
        var derived = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        foreach (HexIndexedFile file in index.Files)
        {
            if (FamilyOf(file.Extension) != family)
                continue;
            foreach (HexSymbol symbol in file.Symbols)
            {
                if (!symbol.IsType || symbol.Kind == "namespace")
                    continue;
                List<string> bases = BasesOf(LineOf(file, symbol.Line), symbol.Name);
                foreach (string name in bases)
                {
                    if (!derived.TryGetValue(name, out var list))
                        derived[name] = list = [];
                    list.Add(types.Count);
                }
                types.Add((file, symbol, bases));
            }
        }

        return (types, derived);
    }

    /// <summary>
    /// A type's tree by the index, its bases (^) and what derives from it (v) to a depth, the way hex_types writes a
    /// server's, for a type whose server has no type hierarchy. A base the workspace doesn't declare is named outside.
    /// </summary>
    public List<HexTreeNode> IndexTypeTree(HexLocated at, bool up, bool down, int depth)
    {
        HexIndexSnapshot index = Current();
        var (types, derived) = TypesOf(index, FamilyOf(at.File.Extension));
        var byName = types.Select((type, i) => (type, i)).GroupBy(each => each.type.Type.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().i, StringComparer.Ordinal);

        var root = new HexTreeNode(at.Symbol.Name, string.Empty, at.Symbol.Kind, at.File.Path, at.Line);
        void Grow(HexTreeNode node, string name, bool bases, int left, HashSet<string> seen)
        {
            if (left == 0)
                return;
            IEnumerable<int> related = bases
                ? (byName.TryGetValue(name, out int self) ? types[self].Bases : []).Select(each => byName.TryGetValue(each, out int found) ? found : -1)
                : derived.TryGetValue(name, out var under) ? under : [];
            if (bases && byName.TryGetValue(name, out int me))
                node.Outside.AddRange(types[me].Bases.Where(each => !byName.ContainsKey(each)));
            foreach (int i in related.Where(i => i >= 0).Distinct().Take(MOST_RELATED))
            {
                var (file, type, _) = types[i];
                var child = new HexTreeNode((bases ? "^ " : "v ") + type.Name, type.Container, type.Kind, file.Path, type.Line);
                node.Children.Add(child);
                if (!seen.Add(child.Key))
                    child.Seen = true;
                else if (left > 1)
                    Grow(child, type.Name, bases, left - 1, seen);
                else if (bases ? types[i].Bases.Count > 0 : derived.ContainsKey(type.Name))
                    child.Cut = true;
            }
        }

        if (up)
            Grow(root, at.Symbol.Name, bases: true, depth, [root.Key]);
        if (down)
            Grow(root, at.Symbol.Name, bases: false, depth, [root.Key]);
        return [root];
    }

    // The most of a type's bases or derived types the index's tree lists under it
    private const int MOST_RELATED = 50;

    /// <summary>
    /// What implements or overrides something by the index: for a type every type deriving from it however far down, for
    /// a member the members of the same name in those types. The places as file and line (from 0).
    /// </summary>
    public List<(HexIndexedFile File, HexSymbol Symbol)> IndexImplementations(HexLocated at)
    {
        HexIndexSnapshot index = Current();
        var (types, derived) = TypesOf(index, FamilyOf(at.File.Extension));
        bool member = !at.Symbol.IsType;
        string typeName = member ? at.Symbol.Container.Split('.')[^1] : at.Symbol.Name;

        var found = new List<(HexIndexedFile, HexSymbol)>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { typeName };
        var pending = new Queue<string>([typeName]);
        while (pending.Count > 0 && found.Count < MOST_RELATED * 4)
        {
            if (!derived.TryGetValue(pending.Dequeue(), out var under))
                continue;
            foreach (int i in under)
            {
                var (file, type, _) = types[i];
                if (!seen.Add(type.Name))
                    continue;
                pending.Enqueue(type.Name);
                if (!member)
                    found.Add((file, type));
                else if (file.Symbols.FirstOrDefault(each => each.Name == at.Symbol.Name && each.Container.Split('.')[^1] == type.Name) is { } overriding)
                    found.Add((file, overriding));
            }
        }

        return found;
    }
}
