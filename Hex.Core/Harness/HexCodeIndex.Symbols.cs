namespace Hex.Core;

// Declarations by name, ranked. The exact name first, then the name in another case, then names it starts, then the
// capitals of a name (QM for QueueMcp), then names it's anywhere in. Container.Name narrows to what's inside something
// whose name ends that way. Types win a tie over members and members over values, then the shorter name, then the path.
internal sealed partial class HexCodeIndex
{
    /// <summary>How well a name fits what was asked, 0 for not at all.</summary>
    public static int NameScore(string name, string asked)
    {
        if (name == asked)
            return 100;
        if (name.Equals(asked, StringComparison.OrdinalIgnoreCase))
            return 90;
        if (name.StartsWith(asked, StringComparison.OrdinalIgnoreCase))
            return 70;
        if (asked.Length > 1 && Capitals(name).StartsWith(asked, StringComparison.OrdinalIgnoreCase))
            return 55;
        if (name.Contains(asked, StringComparison.OrdinalIgnoreCase))
            return 40;
        return 0;
    }

    // The capitals of a name and the letter after each underscore, QueueMcp is QM, run_task is RT
    private static string Capitals(string name)
    {
        var capitals = new System.Text.StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (i == 0 || char.IsUpper(name[i]) || (name[i - 1] == '_' && name[i] != '_'))
                capitals.Append(char.ToUpperInvariant(name[i]));
        }

        return capitals.ToString();
    }

    private static int KindScore(string kind) => kind switch
    {
        "class" or "interface" or "struct" or "enum" => 6,
        "method" => 4,
        "property" => 3,
        "namespace" => 2,
        _ => 0
    };

    /// <summary>The symbols that fit, best first, with their scores.</summary>
    public static List<(HexIndexedFile File, HexSymbol Symbol, int Score)> FindSymbols(HexIndexSnapshot index, string query, string? kind = null, string? under = null)
    {
        string asked = query.Trim();
        string? container = null;
        int dot = asked.LastIndexOf('.');
        if (dot > 0 && dot < asked.Length - 1)
            (container, asked) = (asked[..dot], asked[(dot + 1)..]);

        var found = new List<(HexIndexedFile, HexSymbol, int)>();
        IEnumerable<(HexIndexedFile File, HexSymbol Symbol)> candidates = index.ByName.TryGetValue(asked.ToLowerInvariant(), out var exact) && asked.Length > 0
            ? exact.Concat(index.Files.SelectMany(file => file.Symbols.Where(symbol => !symbol.Name.Equals(asked, StringComparison.OrdinalIgnoreCase)).Select(symbol => (file, symbol))))
            : index.Files.SelectMany(file => file.Symbols.Select(symbol => (file, symbol)));

        foreach (var (file, symbol) in candidates)
        {
            if (under is not null && !file.Relative.StartsWith(under, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!KindFits(symbol.Kind, kind))
                continue;
            if (container is not null && !symbol.Container.EndsWith(container, StringComparison.OrdinalIgnoreCase))
                continue;

            int score = NameScore(symbol.Name, asked);
            if (score > 0)
                found.Add((file, symbol, score + KindScore(symbol.Kind)));
        }

        found.Sort((a, b) =>
        {
            int by = b.Item3.CompareTo(a.Item3);
            if (by != 0) return by;
            by = a.Item2.Name.Length.CompareTo(b.Item2.Name.Length);
            return by != 0 ? by : string.Compare(a.Item1.Relative, b.Item1.Relative, StringComparison.OrdinalIgnoreCase);
        });
        return found;
    }

    /// <summary>
    /// hex_symbols, the declarations that fit, a line each, where, what and its declaration line cut short. The plain
    /// way is a grep for the name over the workspace, every line it's on with the whole path.
    /// </summary>
    public HexAnswer Symbols(string query, string? kind, string? under, int most, int budget) =>
        Ask("hex_symbols", $"{query}\n{kind}\n{under}\n{most}\n{budget}", index =>
        {
            if (query.Trim().Length == 0)
                return HexAnswer.Wrong("Say a name, or part of one, in query.");

            List<(HexIndexedFile File, HexSymbol Symbol, int Score)> found = FindSymbols(index, query, kind, under);
            var said = new HexBudget(budget);
            said.Line(found.Count == 0 ? $"Nothing declares {query}. hex_search looks through the text." : $"{found.Count} declarations fit {query}, best first.");
            foreach (var (file, symbol, _) in found.Take(most))
                said.Line($"{file.Relative}:{symbol.Line + 1} {symbol.Kind} {symbol.Full}  {HexBudget.Cut(LineOf(file, symbol.Line), width: 110)}");
            if (found.Count > most)
                said.Line($"... {found.Count - most} more, raise most or narrow with kind or path.");

            return new HexAnswer(said.Finish("ask with a bigger max_chars or narrow it"), GrepCost(index, query.Contains('.') ? query[(query.LastIndexOf('.') + 1)..] : query));
        });

    /// <summary>A line of a file, empty past its end, reading the file if it wasn't yet.</summary>
    internal static string LineOf(HexIndexedFile file, int line)
    {
        string[] lines = file.Lines;
        return line >= 0 && line < lines.Length ? lines[line] : string.Empty;
    }

    /// <summary>
    /// What grep -rnw for a name would print over the workspace, every line with it on and the path in front,
    /// the plain way an agent finds things and what the harness is measured against.
    /// </summary>
    internal static long GrepCost(HexIndexSnapshot index, string text)
    {
        if (text.Length == 0)
            return 0;

        // By the whole word, grep -rnw, which is the least an agent that knows what it's doing would ask for
        var word = new System.Text.RegularExpressions.Regex($@"\b{System.Text.RegularExpressions.Regex.Escape(text)}\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        long cost = 0;
        foreach (HexIndexedFile file in index.Files)
        {
            string[] lines = file.Lines;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(text, StringComparison.Ordinal) && word.IsMatch(lines[i]))
                    cost += file.Relative.Length + 8 + lines[i].Length;
            }
        }

        return cost;
    }
}
