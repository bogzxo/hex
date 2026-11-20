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
    public static string Describe(string title, IReadOnlyList<HexTreeNode> roots, Func<string, string> relative, int budget, string more)
    {
        var said = new HexBudget(budget);
        said.Line(title);
        bool cut = false;
        void Write(HexTreeNode node, int depth)
        {
            if (!said.Line(LineOf(node, depth, relative)))
                return;
            cut |= node.Cut;
            foreach (HexTreeNode child in node.Children)
                Write(child, depth + 1);
            if (node.Outside.Count > 0)
                said.Line($"{new string(' ', (depth + 1) * 2)}outside the workspace: {HexBudget.Cut(string.Join(", ", node.Outside), width: 160)}");
        }

        foreach (HexTreeNode root in roots)
            Write(root, 0);
        if (cut)
            said.Line($"... marked + have more under them, {more}.");
        return said.Finish("ask with a bigger max_chars or less depth");
    }

    /// <summary>One node's line, indented by its depth.</summary>
    public static string LineOf(HexTreeNode node, int depth, Func<string, string> relative)
    {
        // A detail that only says the file (pyright's "shapes.py · Workspace") says nothing the place doesn't
        string detail = node.Detail.Length == 0 || node.Detail == node.Name || node.Detail.Contains(System.IO.Path.GetFileName(node.Path), StringComparison.OrdinalIgnoreCase) ? string.Empty : $"  {HexBudget.Cut(node.Detail.ReplaceLineEndings(" "), width: 90)}";
        string sites = node.Sites > 1 ? $"  x{node.Sites}" : string.Empty;
        string mark = node.Seen ? "  (above)" : node.Cut ? "  +" : string.Empty;
        return $"{new string(' ', depth * 2)}{relative(node.Path)}:{node.Line + 1} {node.Kind} {node.Name}{detail}{sites}{mark}";
    }
}
