namespace Hex.Core;

/// <summary>
/// Something in a tree a language server's answers make, a caller or a callee, a base type or a type deriving from one, an
/// implementation: its name, what the server says besides (a signature, a container), its kind, where it's declared
/// (from 0) and how many places the call is made at. <see cref="Seen"/> is one already shown higher in the tree (a
/// recursion, or two paths to the same), <see cref="Cut"/> one with more under it than the depth asked for.
/// </summary>
internal sealed record HexTreeNode(string Name, string Detail, string Kind, string Path, int Line, int Sites = 0)
{
    public List<HexTreeNode> Children { get; } = [];

    /// <summary>What it calls outside the workspace (the standard library, a package), named only.</summary>
    public List<string> Outside { get; } = [];
    public bool Seen { get; set; }
    public bool Cut { get; set; }

    /// <summary>How many other files a partial type is declared in besides this one's (the one with its base list).</summary>
    public int Partials { get; set; }

    /// <summary>The same place, what Seen is told by.</summary>
    public string Key => $"{Path}\n{Line}\n{Name}";
}

// The trees hex_calls and hex_types answer with, written the way the harness writes, a line each with path:line in front
// and the depth as the indent, under a budget, the end saying what was cut. An agent reading callers three deep gets in
// one answer what it would otherwise have asked of hex_refs a dozen times and read every hit of.
internal static class HexHierarchy
{
    /// <summary>A language server's SymbolKind as a word, the ones the harness's own symbols use where they're the same.</summary>
    public static string KindName(int kind) => kind switch
    {
        1 => "file",
        2 => "module",
        3 => "namespace",
        4 => "package",
        5 => "class",
        6 => "method",
        7 => "property",
        8 => "field",
        9 => "constructor",
        10 => "enum",
        11 => "interface",
        12 => "function",
        13 => "variable",
        14 => "constant",
        22 => "enum-member",
        23 => "struct",
        24 => "event",
        25 => "operator",
        26 => "type-parameter",
        _ => "symbol"
    };

    /// <summary>
    /// A tree as lines under a title, each node its place, kind, name and detail, the sites of a call counted after it.
    /// <paramref name="relative"/> says a path as the harness does (inside the workspace), <paramref name="more"/> how to
    /// see more when the depth cut something.
    /// </summary>
    public static string Describe(string title, IReadOnlyList<HexTreeNode> roots, Func<string, string> relative, int budget, string more, string leaves = "also")
    {
        // Written whole when it fits, else folded a step at a time until it does: the leaves that have nothing under them
        // named on one line under their parent, then the ones the depth stopped at too, then a node's leaves on its own
        // line, and last the tree one level deep with how many each has under it
        string said = string.Empty;
        for (int fold = 0; fold <= MOST_FOLD; fold++)
        {
            said = Render(title, roots, relative, budget, more, leaves, fold, out bool fits);
            if (fits)
                break;
        }

        return said;
    }

    private const int MOST_FOLD = 5;

    /// <summary>
    /// The trees of several roots (overloads asked together) made one: a node under a later root that's already under an
    /// earlier one is marked seen and what's under it dropped, so what overloads share is said once.
    /// </summary>
    public static void MarkShared(IReadOnlyList<HexTreeNode> roots)
    {
        if (roots.Count < 2)
            return;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (HexTreeNode root in roots)
        {
            var mine = new HashSet<string>(StringComparer.Ordinal);
            void Walk(HexTreeNode node)
            {
                foreach (HexTreeNode child in node.Children)
                {
                    if (child.Kind == "more")
                        continue;
                    if (seen.Contains(child.Key) && !child.Seen)
                    {
                        child.Seen = true;
                        child.Cut = false;
                        child.Children.Clear();
                        child.Outside.Clear();
                        continue;
                    }
                    mine.Add(child.Key);
                    Walk(child);
                }
            }

            Walk(root);
            seen.UnionWith(mine);
        }
    }

    /// <summary>Fewest characters a folded leaf's name is said in, its place left out.</summary>
    private static string LeafName(HexTreeNode node) =>
        node.Name + (node.Sites > 1 ? $" x{node.Sites}" : string.Empty) + (node.Children.Count > 0 ? $" ({CountUnder(node)} under)" : string.Empty)
        + (node.Seen ? " (above)" : node.Cut ? " +" : string.Empty);

    private static string Render(string title, IReadOnlyList<HexTreeNode> roots, Func<string, string> relative, int budget, string more, string leaves, int fold, out bool fits)
    {
        var said = new HexBudget(budget);
        said.Line(title);
        bool cut = false;
        int hidden = 0;

        // Whether a node goes onto its parent's line of leaves rather than one of its own
        // (the last fold puts every child of a root there, how many it has under it said with its name)
        bool Folded(HexTreeNode node) => node.Kind != "more" && (fold >= 5 || fold >= 1 && node.Children.Count == 0 && node.Outside.Count == 0 && (fold >= 2 || !node.Cut));

        void Write(HexTreeNode node, int depth)
        {
            string indent = new(' ', (depth + 1) * 2);
            bool inLine = fold >= 3 && depth > 0 && node.Children.Count > 0 && node.Children.All(Folded);
            string line = LineOf(node, depth, relative);
            if (fold >= 4 && depth >= 1 && node.Children.Count > 0)
            {
                // One level deep only, how many are under it said instead
                hidden += CountUnder(node);
                line += $"  ({CountUnder(node)} under it)";
            }
            else if (inLine)
                line += $"  -> {HexBudget.Cut(string.Join(", ", node.Children.Select(LeafName).Distinct()), width: 200)}";
            if (!said.Line(line))
                return;
            cut |= node.Cut || (inLine && node.Children.Any(child => child.Cut));

            if (!inLine && !(fold >= 4 && depth >= 1))
            {
                foreach (HexTreeNode child in node.Children.Where(child => !Folded(child)))
                    Write(child, depth + 1);
                List<HexTreeNode> folded = [.. node.Children.Where(Folded)];
                cut |= folded.Any(child => child.Cut);
                if (folded.Count > 0)
                    said.Line($"{indent}{leaves}: {HexBudget.Cut(string.Join(", ", folded.Select(LeafName).Distinct()), width: fold >= 5 ? 1500 : 400)}");
            }
            if (node.Outside.Count > 0)
                said.Line($"{indent}outside the workspace: {HexBudget.Cut(string.Join(", ", node.Outside), width: 160)}");
        }

        foreach (HexTreeNode root in roots)
            Write(root, 0);
        if (fold == 4 && hidden > 0)
            said.Line($"... {hidden} further down didn't fit and were left out, ask from one of them for those.");
        if (cut)
            said.Line($"... marked + have more under them, {more}.");
        fits = said.LeftOut == 0;
        return said.Finish("ask with a bigger max_chars or less depth");
    }

    private static int CountUnder(HexTreeNode node) => node.Children.Sum(child => 1 + CountUnder(child));

    /// <summary>One node's line, indented by its depth.</summary>
    public static string LineOf(HexTreeNode node, int depth, Func<string, string> relative)
    {
        // A detail that only says the file (pyright's "shapes.py · Workspace") says nothing the place doesn't
        string detail = node.Detail.Length == 0 || node.Detail == node.Name || node.Detail.Contains(System.IO.Path.GetFileName(node.Path), StringComparison.OrdinalIgnoreCase) ? string.Empty : $"  {HexBudget.Cut(node.Detail.ReplaceLineEndings(" "), width: 90)}";
        string sites = node.Sites > 1 ? $"  x{node.Sites}" : string.Empty;
        string mark = node.Seen ? "  (above)" : node.Cut ? "  +" : string.Empty;
        string partials = node.Partials > 0 ? $"  +{node.Partials} partial file{(node.Partials == 1 ? string.Empty : "s")}" : string.Empty;
        return $"{new string(' ', depth * 2)}{relative(node.Path)}:{node.Line + 1} {node.Kind} {node.Name}{detail}{sites}{partials}{mark}";
    }

    /// <summary>
    /// The places of one type said once, a partial class's every file (CodeEditor was listed 25 times) folded to the one
    /// whose line has its base list (a ':' or extends after the name), else the first, with how many more files it's in.
    /// Things are the same type by <paramref name="key"/> (its name, kind and container); other things keep their order.
    /// </summary>
    public static List<(T Item, int Partials)> FoldPartials<T>(IEnumerable<T> items, Func<T, string> key, Func<T, string> lineOf)
    {
        var folded = new List<(T Item, int Partials)>();
        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (T item in items)
        {
            string same = key(item);
            if (!at.TryGetValue(same, out int index))
            {
                at[same] = folded.Count;
                folded.Add((item, 0));
                continue;
            }

            var (kept, partials) = folded[index];
            folded[index] = (HasBases(lineOf(item)) && !HasBases(lineOf(kept)) ? item : kept, partials + 1);
        }

        return folded;
    }

    private static bool HasBases(string line)
    {
        string code = line.Split("//")[0];
        return code.Contains(':') || code.Contains(" extends ", StringComparison.Ordinal) || code.Contains(" implements ", StringComparison.Ordinal);
    }
}
