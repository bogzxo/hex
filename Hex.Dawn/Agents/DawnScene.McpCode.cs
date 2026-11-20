using System.Text;
using System.Text.Json.Nodes;

using Horizon.UI.Scripting;

namespace Hex.Dawn;

// The tools that know code the way its compiler does, through the language servers of any language Hex has one for
// (csharp-ls, pyright, typescript-language-server, clangd, rust-analyzer, gopls, ...), asked by a name rather than a
// caret: what calls something and what it calls, a few levels deep in one answer (hex_calls), what implements or
// overrides it (hex_implementations), its bases and what derives from it (hex_types), renaming it everywhere at once
// (hex_rename), the fixes there are for a line (hex_fix) and what's wrong with files (hex_diagnostics). A file needn't be
// open, HexLanguages.AgentQueries opens it in its server with no tab. The index finds the name (HexCodeIndex.Locate)
// and replaces a declaration whole (hex_edit_symbol), and reads the build files for what uses what (hex_projects).
internal sealed partial class DawnScene
{
    // How long a server just started is waited on to say it's up, and a question is waited on, in seconds
    private const int MCP_SERVER_START_MS = 60_000;
    private const int MCP_SERVER_ASK_MS = 45_000;

    // How long after a server's said what's wrong with a file it's given to say it again, the second being about the change
    private const int MCP_CHECK_SETTLE_MS = 600;

    // How long after a server's up a file it hasn't said anything of is sent it again, for hex_diagnostics
    private const int MCP_DIAGNOSTICS_LATE_NUDGE_MS = 20_000;

    // How long the check after an edit waits for a file's server, the first time for a server longer (it's warming up).
    // csharp-ls says what's wrong 0.3 to 1.2 s after a change (measured on Hex.sln with 40 files open), so past this
    // it's busy, and the answer says it wasn't checked rather than holding the agent: at 10 and 15 s, three edits sat
    // 15, 24 and 30 s
    private const float MCP_CHECK_SECONDS = 3.0f, MCP_FIRST_CHECK_SECONDS = 6.0f;

    // The servers a check after an edit has waited on before, the first check of each waits longer
    private readonly HashSet<LanguageServer> mcpChecked = new(ReferenceEqualityComparer.Instance);

    // The most a call tree has, past it a node isn't asked about
    private const int MOST_TREE_NODES = 160;
    private const int MOST_CHILDREN = 25;

    private IEnumerable<HexMcpTool> CodeTools()
    {
        // A schema's node can only be in one schema, so each tool gets its own
        static (string, JsonObject) Symbol() => ("symbol", McpSchema.String("The name, Name or Container.Name (QueueMcp, DawnScene.QueueMcp), found by the index. Or path and line for a place."));
        static (string, JsonObject) PathOf() => ("path", McpSchema.String("The file it's in, to pick one of several declarations, or with line for a place."));
        static (string, JsonObject) Line() => ("line", McpSchema.Integer("With path, the line the name's on, 1 is the first."));
        static (string, JsonObject) Column() => ("column", McpSchema.Integer("With line, the column of the name, found on the line by symbol when it isn't said."));
        static (string, JsonObject) Max() => ("max_chars", McpSchema.Integer("Stay under this many characters, the agent's profile says how many when it isn't said (8000 usually)."));
        return
        [
            new(new McpTool("hex_calls", "Call hierarchy",
                "What calls a function or method (direction in, the default) or what it calls (out), as a tree a few levels deep in one answer, path:line of each call. Asked of the file's language server (any language with call hierarchy), no file need be open. Callers three deep here is a dozen hex_refs.",
                McpSchema.Object([Symbol(), PathOf(), Line(), Column(),
                    ("direction", McpSchema.OneOf("in for callers, out for what it calls.", "in", "out")),
                    ("depth", McpSchema.Integer("How many levels, 1 to 5, 2 by default.")),
                    ("skip_constructors", McpSchema.Flag("Leave out the constructors it calls, true by default going out.")), Max()]), ReadOnly: true),
                McpCalls, Timeout: 120.0f),

            new(new McpTool("hex_implementations", "Implementations",
                "What implements an interface or abstract member, or overrides a virtual one, or derives from a class, path:line each. Asked of the file's language server (textDocument/implementation, then the type hierarchy, then the base lists the index read for a server with neither), no file need be open.",
                McpSchema.Object([Symbol(), PathOf(), Line(), Column(), Max()]), ReadOnly: true),
                McpImplementations, Timeout: 90.0f),

            new(new McpTool("hex_types", "Type hierarchy",
                "A type's bases and interfaces (up), what derives from it (down) or both, as a tree a few levels deep, path:line each, by its language server's type hierarchy (by the base lists the index read where the server has none, Python's pyright).",
                McpSchema.Object([Symbol(), PathOf(), Line(), Column(),
                    ("direction", McpSchema.OneOf("up for bases, down for derived, both by default.", "up", "down", "both")),
                    ("depth", McpSchema.Integer("How many levels, 1 to 5, 2 by default.")), Max()]), ReadOnly: true),
                McpTypes, Timeout: 90.0f),

            new(new McpTool("hex_rename", "Rename everywhere",
                "Rename a symbol everywhere it's used, across files, as one change by its language server (not text replace, so other things of the same name are left alone). Written to the disk, or to a file open with unsaved changes in the editor. dry_run lists the places only.",
                McpSchema.Object([Symbol(), PathOf(), Line(), Column(),
                    ("new_name", McpSchema.String("What it's to be called.")),
                    ("dry_run", McpSchema.Flag("Only say what would change.")), Max()], "new_name")),
                McpRename, Timeout: 120.0f),

            new(new McpTool("hex_fix", "Fixes for a line",
                "The quick fixes and refactorings a file's language server has for a line (add a using or import, implement an interface, fix a misspelt name, ...), with what's wrong there. apply takes one by its number or title and makes its edits.",
                McpSchema.Object([
                    ("path", McpSchema.String("The file.")),
                    ("line", McpSchema.Integer("The line, 1 is the first.")),
                    ("end_line", McpSchema.Integer("The last line of a range, the same line by default.")),
                    ("apply", McpSchema.String("The number or the title (or the start of it) of the fix to make.")), Max()], "path", "line")),
                McpFix, Timeout: 90.0f),

            new(new McpTool("hex_diagnostics", "What's wrong with files",
                "The errors and warnings the language servers say of files now, with no build, path:line:col each, opening them in their servers if they aren't. hex_problems is the Problems panel (builds and open files) instead.",
                McpSchema.Object([
                    ("paths", McpSchema.String("The files, a comma between them, at most 10.")),
                    ("errors_only", McpSchema.Flag("Leave the warnings out.")),
                    ("wait", McpSchema.Integer("Seconds to wait for each file's server to say something of it once the server's up, 60 by default (csharp-ls loading a big solution takes most of it, an answer ends the wait). A server still starting is waited on up to 60 s besides.")), Max()], "paths"), ReadOnly: true),
                McpDiagnostics, Timeout: 190.0f),

            new(new McpTool("hex_edit_symbol", "Replace a declaration",
                "Replace a whole declaration (a method, a class, a function, a property) found by its name with new text, no old_string and no reading it first. Its comments and attributes above stay unless with_comments. Empty new_text deletes it. The errors its server says after come back with it.",
                McpSchema.Object([Symbol(), PathOf(),
                    ("new_text", McpSchema.String("The declaration as it's to be, its signature and body, indented as the file is.")),
                    ("with_comments", McpSchema.Flag("Replace the comments and attributes above it too.")),
                    ("check", McpSchema.Flag("Wait a moment for what its language server says is wrong after, true by default."))], "symbol", "new_text")),
                McpEditSymbol, Timeout: 30.0f),

            Harnessed(new McpTool("hex_projects", "Projects and what uses what",
                "The workspace's projects by their build files (.csproj, package.json, Cargo.toml, go.mod, pyproject.toml, CMakeLists.txt): kind, target, the projects each uses and is used by, its packages. What a change to one can break.",
                McpSchema.Object([
                    ("under", McpSchema.String("Only projects under this folder.")),
                    ("packages", McpSchema.Flag("List the packages of each, not only how many.")), Max()]), ReadOnly: true),
                (index, args, budget) => index.Projects(args.String("under"), args.Flag("packages"), budget))
        ];
    }

    /// <summary>
    /// A place to ask a server about, found by name or given, the name that's there, the others declaring it, and the
    /// index's declaration when it has one (what a fallback by the index starts from, and what overloads are told by).
    /// </summary>
    private sealed record McpCodeAt(string Path, int Line, int Column, string Name, string Also, HexLocated? Located = null)
    {
        public string Place(Func<string, string> relative) => $"{relative(Path)}:{Line + 1}";

        /// <summary>The same name in the same container of the same file, an overload of this, asked together with it.</summary>
        public bool Overloads(McpCodeAt other) => PlatformPaths.Same(Path, other.Path) && Name == other.Name
            && (Located?.Symbol.Container ?? string.Empty) == (other.Located?.Symbol.Container ?? string.Empty);
    }

    /// <summary>
    /// Where the arguments point, by path and line or by a name the index finds, worked out off the editor's thread. The
    /// last thing yielded is the places to try, best first (the declarations of a name, in a project's folder first), or
    /// an error.
    /// </summary>
    private IEnumerable<object?> McpLocate(JsonObject args)
    {
        string? symbol = args.String("symbol"), path = args.String("path");
        if (args.Integer("line") is { } line && path is { Length: > 0 })
        {
            string full = McpPath(path);
            string[] lines = Context.Documents.Find(full) is { } open ? open.Editor.Text.Split('\n') : File.Exists(full) ? File.ReadAllLines(full) : [];
            if (line < 1 || line > lines.Length)
            {
                yield return McpToolResult.Error(lines.Length == 0 ? $"There's no file {full}." : $"{full} has {lines.Length} lines, there's no line {line}.");
                yield break;
            }

            // Asked on the name, not the line's start: on a declaration line that's past its modifiers and return type,
            // where the index says the name is when it declares something there
            string text = lines[line - 1].TrimEnd('\r');
            HexIndexedFile? indexed = Harness.Latest.File(full);
            HexSymbol? declared = indexed?.Symbols.FirstOrDefault(each => each.Line == line - 1 && (symbol is not { Length: > 0 } || each.Name == symbol));
            int column = args.Integer("column") is { } said ? Math.Max(0, said - 1)
                : symbol is { Length: > 0 } ? HexCodeIndex.NameColumn(text, symbol)
                : declared is not null ? HexCodeIndex.NameColumn(text, declared.Name, declared.Column)
                : HexCodeIndex.DeclarationColumn(text);
            column = Math.Min(column, text.Length);
            int end = column;
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
                end++;
            HexLocated? located = declared is not null && indexed is not null ? HexCodeIndex.LocateIn(HexCodeIndex.Fresh(indexed), declared, []) : null;
            yield return new List<McpCodeAt> { new(full, line - 1, column, symbol is { Length: > 0 } ? symbol : text[column..end], string.Empty, located) };
            yield break;
        }

        if (symbol is not { Length: > 0 })
        {
            yield return McpToolResult.Error("Say symbol (a name), or path and line.");
            yield break;
        }

        HexCodeIndex index = Harness;
        string? wrong = null;
        Task<List<HexLocated>> finding = Task.Run(() => index.LocateAll(symbol, path, out wrong));
        while (!finding.IsCompleted)
            yield return null;
        if (finding.IsFaulted || finding.Result.Count == 0)
        {
            yield return McpToolResult.Error(wrong ?? $"{symbol} couldn't be found, {finding.Exception?.InnerException?.Message}");
            yield break;
        }

        yield return finding.Result.Select(found => new McpCodeAt(found.File.Path, found.Line, found.Column, found.Symbol.Name, found.AlsoSaid, found)).ToList();
    }

    /// <summary>
    /// The places to ask in turn: each with its overloads (the same name in the same container and file) as one group,
    /// the groups in the order they were found.
    /// </summary>
    private static List<List<McpCodeAt>> McpGroups(List<McpCodeAt> all)
    {
        var groups = new List<List<McpCodeAt>>();
        foreach (McpCodeAt at in all)
        {
            if (groups.Find(group => group[0].Overloads(at)) is { } same)
                same.Add(at);
            else
                groups.Add([at]);
        }

        return groups;
    }

    /// <summary>What a tool says when none of the places it tried had anything, each of them and how to pick one.</summary>
    private string McpTriedAll(string what, string name, List<McpCodeAt> tried)
    {
        Func<string, string> relative = McpRelative();
        return tried.Count <= 1
            ? $"{what} at {name} ({tried[0].Place(relative)})."
            : $"{what} at any of the {tried.Count} declarations of {name}: {string.Join(", ", tried.Select(each => each.Place(relative)))}. Say path (and line) to pick one.";
    }

    /// <summary>
    /// The server for a file, waited on until it's up and does what's wanted. The last thing yielded is the server, or an
    /// error saying why there's none.
    /// </summary>
    private IEnumerable<object?> McpServerFor(string path, string? provider, string what)
    {
        if (languages.AgentServer(path, start: true, out string? why) is not { } server)
        {
            yield return McpToolResult.Error(why ?? $"No language server reads {path}.");
            yield break;
        }

        long giveUp = System.Environment.TickCount64 + MCP_SERVER_START_MS;
        while (!server.IsReady && server.Failure is null && System.Environment.TickCount64 < giveUp)
            yield return null;
        if (server.Failure is { } failure)
        {
            yield return McpToolResult.Error($"{server.Name}'s server stopped, {failure}");
            yield break;
        }
        if (!server.IsReady)
        {
            yield return McpToolResult.Error($"{server.Name}'s server didn't start in {MCP_SERVER_START_MS / 1000} s, ask again in a moment.");
            yield break;
        }
        if (provider is not null && !server.Offers(provider))
        {
            yield return McpToolResult.Error($"{server.Name}'s server can't say {what} ({provider}).");
            yield break;
        }

        yield return server;
    }

    /// <summary>A task waited on, a step a tick, null while it isn't done. Its result or an error comes last.</summary>
    private static IEnumerable<object?> McpAwait<T>(Task<T> asked, string what)
    {
        long giveUp = System.Environment.TickCount64 + MCP_SERVER_ASK_MS;
        while (!asked.IsCompleted && System.Environment.TickCount64 < giveUp)
            yield return null;
        if (!asked.IsCompleted)
            yield return McpToolResult.Error($"The language server didn't say {what} in {MCP_SERVER_ASK_MS / 1000} s, it may still be loading the project.");
        else if (!asked.IsCompletedSuccessfully)
            yield return McpToolResult.Error($"The language server couldn't say {what}, {asked.Exception?.GetBaseException().Message ?? "it was stopped"}.");
        else
            yield return asked.Result;
    }

    /// <summary>A path as the harness says it, inside the workspace.</summary>
    private Func<string, string> McpRelative()
    {
        HexIndexSnapshot index = Harness.Latest ?? Harness.Current();
        string[] folders = [.. explorer.workspace.Folders];
        return path =>
        {
            if (index.File(path)?.Relative is { } known)
                return known;

            // A file made since the index last looked, said the way the index would
            if (folders.FirstOrDefault(folder => PlatformPaths.IsUnder(path, folder)) is not { } root)
                return path;
            string inside = Path.GetRelativePath(root, path).Replace('\\', '/');
            return folders.Length > 1 ? $"{WorkspacePaths.FolderName(root)}/{inside}" : inside;
        };
    }

    /// <summary>Whether a path is in one of the workspace's folders, for leaving out what a library declares.</summary>
    private Func<string, bool> McpInside()
    {
        string[] folders = [.. explorer.workspace.Folders];
        return path => !path.Contains(":/", StringComparison.Ordinal) && folders.Any(folder => PlatformPaths.IsUnder(path, folder))
            && !path.Contains("node_modules", StringComparison.OrdinalIgnoreCase);
    }

    // The steps of a lookup run through, their nulls handed on as waiting, an error ending the tool, the last value kept
    // in result. Written once rather than in each tool: foreach (var step in Steps(..., out box)) yield return step
    private sealed class McpStepBox
    {
        public object? Value;
    }

    private static IEnumerable<McpToolResult?> McpSteps(IEnumerable<object?> steps, McpStepBox box)
    {
        foreach (object? step in steps)
        {
            if (step is null)
                yield return null;
            else
                box.Value = step;
        }
    }

    private IEnumerable<McpToolResult?> McpCalls(JsonObject args)
    {
        var box = new McpStepBox();
        foreach (var step in McpSteps(McpLocate(args), box)) yield return step;
        if (box.Value is McpToolResult located) { yield return located; yield break; }
        var all = (List<McpCodeAt>)box.Value!;
        McpCodeAt first = all[0];
        bool incoming = args.String("direction") != "out";
        bool skipConstructors = args.Flag("skip_constructors", !incoming);
        int depth = Math.Clamp(args.Integer("depth") ?? 2, 1, 5);
        int budget = HexAgentProfiles.BudgetFor(McpServer.CallingClient.Value, args.Integer("max_chars"));

        // A server with no call hierarchy has its callers said by the index's references instead
        if (languages.AgentServer(first.Path, start: true, out _) is { } early && early.IsReady && !early.Offers("callHierarchyProvider") && incoming)
        {
            yield return McpToolResult.Text($"{early.Name}'s server has no call hierarchy, the uses of {first.Name} by name instead.\n" + Harness.References(first.Name, null, 80, budget).Text);
            yield break;
        }

        // Each declaration in turn (a copy outside the projects the server loaded has nothing), its overloads together
        LanguageServer? server = null;
        McpToolResult? failure = null;
        var roots = new List<HexTreeNode>();
        List<McpCodeAt> used = [], tried = [];
        int constructors = 0;
        foreach (List<McpCodeAt> group in McpGroups(all))
        {
            foreach (var step in McpSteps(McpServerFor(group[0].Path, "callHierarchyProvider", "a call hierarchy"), box)) yield return step;
            if (box.Value is McpToolResult noServer)
            {
                failure ??= noServer;
                tried.AddRange(group);
                continue;
            }
            server = (LanguageServer)box.Value!;

            foreach (McpCodeAt at in group)
            {
                tried.Add(at);
                foreach (var step in McpSteps(McpAwait(CallTree(server, at, incoming, depth, McpInside(), skipConstructors), "the calls"), box)) yield return step;
                if (box.Value is McpToolResult failed)
                {
                    failure ??= failed;
                    continue;
                }

                var (found, skipped) = ((List<HexTreeNode>, int))box.Value!;
                foreach (HexTreeNode root in found.Where(root => !roots.Exists(other => other.Key == root.Key)))
                    roots.Add(root);
                if (found.Count > 0)
                    used.Add(at);
                constructors += skipped;
            }

            if (roots.Count > 0)
                break;
        }

        if (roots.Count == 0)
        {
            yield return server is null && failure is not null ? failure
                : McpToolResult.Error(McpTriedAll($"{server?.Name ?? "The"}'s server has no call hierarchy", first.Name, tried) + " It's not a function, or the server is still loading the project.");
            yield break;
        }

        Func<string, string> relative = McpRelative();
        int count = CountNodes(roots) - roots.Count;
        string overloads = used.Count > 1 ? $" ({used.Count} overloads together)" : string.Empty;
        string moved = used[0] != first ? $" {first.Place(relative)} had nothing, {used[0].Place(relative)} was asked instead." : string.Empty;
        // The other declarations said are the ones not asked together with it
        string[] asked = [.. used.Select(each => each.Place(relative))];
        string[] rest = [.. used[0].Located?.Others.Where(other => !asked.Contains(other, StringComparer.OrdinalIgnoreCase)) ?? []];
        string also = used.Count == 1 ? (used[0].Also.Length > 0 ? $" {used[0].Also}" : string.Empty)
            : rest.Length == 0 ? string.Empty : $" {rest.Length} more declare {used[0].Name}, {string.Join(", ", rest.Take(5))}{(rest.Length > 5 ? " ..." : string.Empty)}, say path to pick one.";
        string title = $"{(incoming ? "Callers of" : "Calls from")} {used[0].Name}{overloads}, {depth} deep, {count} in all.{moved}{also}"
            + (incoming ? " Each caller's line is where it calls." : string.Empty)
            + (constructors > 0 ? $" {constructors} constructor calls left out, skip_constructors false shows them." : string.Empty);
        HexHierarchy.MarkShared(roots);
        string tree = HexHierarchy.Describe(title, roots, relative, budget, "ask again from one of them", incoming ? "also called by" : "also calls");

        // No callers by the server (csharp-ls has none for an interface's member, the calls go through the interface), the
        // uses by name, which are where it's called from whatever it's called through
        if (incoming && count == 0)
            tree += $"{server!.Name}'s server knows no callers, the uses of {used[0].Name} by name:\n" + Harness.References(used[0].Name, null, 30, Math.Max(1000, budget - tree.Length)).Text;
        yield return McpToolResult.Text(tree);
    }

    private static int CountNodes(IEnumerable<HexTreeNode> nodes) => nodes.Sum(node => 1 + CountNodes(node.Children));

    // A call of a constructor, which hex_calls out leaves out unless asked (a method that makes a few lists says nothing by them)
    private static bool IsConstructorCall(LanguageServerCall call) =>
        call.Other.Kind == 9 || call.Other.Name is ".ctor" or "constructor" or "__init__" || call.Other.Name.EndsWith(".ctor", StringComparison.Ordinal);

    /// <summary>
    /// The call tree of a place, breadth first to a depth, each node asked about once (a recursion's said as above), and
    /// how many constructor calls were left out.
    /// </summary>
    private static async Task<(List<HexTreeNode> Roots, int Skipped)> CallTree(LanguageServer server, McpCodeAt at, bool incoming, int depth, Func<string, bool> inside, bool skipConstructors)
    {
        IReadOnlyList<LanguageServerCallItem> items = await server.CallItems(at.Path, at.Line, at.Column).ConfigureAwait(false);
        var roots = new List<HexTreeNode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var level = new List<(HexTreeNode Node, LanguageServerCallItem Item)>();
        foreach (LanguageServerCallItem item in items)
        {
            var node = new HexTreeNode(item.Name, item.Detail, HexHierarchy.KindName(item.Kind), item.Declared.Path, item.Declared.Line);
            roots.Add(node);
            seen.Add(node.Key);
            level.Add((node, item));
        }

        int made = roots.Count, skipped = 0;
        for (int down = 0; down < depth && level.Count > 0; down++)
        {
            var next = new List<(HexTreeNode, LanguageServerCallItem)>();
            foreach (var (node, item) in level)
            {
                if (made >= MOST_TREE_NODES)
                {
                    node.Cut = true;
                    continue;
                }

                IReadOnlyList<LanguageServerCall> asked = incoming ? await server.IncomingCalls(item).ConfigureAwait(false) : await server.OutgoingCalls(item).ConfigureAwait(false);
                if (skipConstructors && !incoming)
                {
                    skipped += asked.Count(IsConstructorCall);
                    asked = [.. asked.Where(call => !IsConstructorCall(call))];
                }

                // What's called outside the workspace (the standard library, a package) is only named, once for its overloads
                List<LanguageServerCall> calls = [.. asked.Where(call => inside(call.Other.Declared.Path))];
                node.Outside.AddRange(asked.Where(call => !inside(call.Other.Declared.Path)).Select(call => call.Other.Name).Distinct(StringComparer.Ordinal));
                foreach (LanguageServerCall call in calls.Take(MOST_CHILDREN))
                {
                    // A caller is shown where it calls, what's called where it's declared
                    LanguageServerPlace where = incoming && call.Sites.Count > 0 ? call.Sites[0] : call.Other.Declared;
                    var child = new HexTreeNode(call.Other.Name, call.Other.Detail, HexHierarchy.KindName(call.Other.Kind), incoming ? call.Other.Declared.Path : where.Path, where.Line, call.Sites.Count);
                    node.Children.Add(child);
                    made++;
                    string key = $"{call.Other.Declared.Path}\n{call.Other.Declared.Line}\n{call.Other.Name}";
                    if (!seen.Add(key))
                        child.Seen = true;
                    else if (down + 1 < depth)
                        next.Add((child, call.Other));
                    else
                        child.Cut = true;
                }

                if (calls.Count > MOST_CHILDREN)
                    node.Children.Add(new HexTreeNode($"... {calls.Count - MOST_CHILDREN} more", string.Empty, "more", item.Declared.Path, item.Declared.Line));
            }
            level = next;
        }

        // A leaf at the bottom with nothing under it isn't cut, only one that was never asked. Asking every leaf whether it
        // has more would double the questions, so + is only said of the ones the depth stopped at
        return (roots, skipped);
    }

    private IEnumerable<McpToolResult?> McpImplementations(JsonObject args)
    {
        var box = new McpStepBox();
        foreach (var step in McpSteps(McpLocate(args), box)) yield return step;
        if (box.Value is McpToolResult located) { yield return located; yield break; }
        var all = (List<McpCodeAt>)box.Value!;
        int budget = HexAgentProfiles.BudgetFor(McpServer.CallingClient.Value, args.Integer("max_chars"));

        // Each declaration in turn until one has something: the server's implementations, its type hierarchy's derived
        // types, and for a server with neither (pyright) or one that knows nothing, the index's base lists
        LanguageServer? server = null;
        McpToolResult? failure = null;
        List<LanguageServerPlace> places = [];
        List<McpCodeAt> tried = [];
        McpCodeAt? used = null;
        string how = string.Empty;
        foreach (McpCodeAt at in all)
        {
            foreach (var step in McpSteps(McpServerFor(at.Path, null, "implementations"), box)) yield return step;
            if (box.Value is McpToolResult noServer)
            {
                failure ??= noServer;
                continue;
            }
            server = (LanguageServer)box.Value!;
            tried.Add(at);

            if (server.Offers("implementationProvider"))
            {
                foreach (var step in McpSteps(McpAwait(server.Implementation(at.Path, at.Line, at.Column), "the implementations"), box)) yield return step;
                if (box.Value is IReadOnlyList<LanguageServerPlace> found)
                    places = [.. found.Where(place => !(PlatformPaths.Same(place.Path, at.Path) && place.Line == at.Line))];
                else
                    failure ??= box.Value as McpToolResult;
            }

            // Nothing but itself, a class's derived types by the type hierarchy instead
            if (places.Count == 0 && server.Offers("typeHierarchyProvider"))
            {
                Task<List<HexTreeNode>> derived = TypeTree(server, at, up: false, down: true, depth: 1);
                foreach (var step in McpSteps(McpAwait(derived, "the derived types"), box)) yield return step;
                if (box.Value is List<HexTreeNode> tree)
                    places = [.. tree.SelectMany(root => root.Children).Select(node => new LanguageServerPlace(node.Path, node.Line, 0, node.Line, 0))];
            }

            if (places.Count == 0 && at.Located is { } declared)
            {
                HexCodeIndex index = Harness;
                foreach (var step in McpSteps(McpAwait(Task.Run(() => index.IndexImplementations(declared)), "the implementations by the index"), box)) yield return step;
                if (box.Value is List<(HexIndexedFile File, HexSymbol Symbol)> { Count: > 0 } byIndex)
                {
                    places = [.. byIndex.Select(each => new LanguageServerPlace(each.File.Path, each.Symbol.Line, 0, each.Symbol.Line, 0))];
                    how = $" {server.Name}'s server {(server.Offers("implementationProvider") ? "knew none" : "can't say implementations")}, these are by the base lists the index read, by name.";
                }
            }

            if (places.Count > 0)
            {
                used = at;
                break;
            }
        }

        if (server is null)
        {
            yield return failure ?? McpToolResult.Error($"No language server reads {all[0].Path}.");
            yield break;
        }

        Func<string, string> relative = McpRelative();
        McpCodeAt shown = used ?? all[0];
        string moved = used is not null && used != all[0] ? $" {all[0].Place(relative)} had nothing, {used.Place(relative)} was asked instead." : string.Empty;

        // A partial class's every file said once, the one with its base list, with how many more there are
        var lines = new Dictionary<(string, int), string>();
        string Text((string Path, int Line) place) => lines.TryGetValue(place, out string? known) ? known : lines[place] = LineAt(place.Path, place.Line);
        List<(LanguageServerPlace Item, int Partials)> folded = HexHierarchy.FoldPartials(places.DistinctBy(place => (place.Path, place.Line)),
            place => Text((place.Path, place.Line)) is var line && line.Contains("partial ", StringComparison.Ordinal) ? "partial\n" + DeclaredOn(line) : $"{place.Path}\n{place.Line}",
            place => Text((place.Path, place.Line)));

        var said = new HexBudget(budget);
        said.Line(folded.Count == 0
            ? McpTriedAll($"Nothing implements or overrides it that {server.Name}'s server knows of", shown.Name, tried.Count > 0 ? tried : [shown]) + (shown.Also.Length > 0 && tried.Count <= 1 ? $" {shown.Also}" : string.Empty)
            : $"{folded.Count} implementations of {shown.Name}.{moved}{how}{(shown.Also.Length > 0 ? $" {shown.Also}" : string.Empty)}");
        foreach (var (place, partials) in folded)
            said.Line($"{relative(place.Path)}:{place.Line + 1}  {HexBudget.Cut(Text((place.Path, place.Line)).Trim(), width: 110)}{(partials > 0 ? $"  +{partials} partial file{(partials == 1 ? string.Empty : "s")}" : string.Empty)}");
        yield return McpToolResult.Text(said.Finish("ask with a bigger max_chars"));
    }

    // The name a line declares, what a partial type's files are told the same by
    private static string DeclaredOn(string line)
    {
        int at = HexCodeIndex.DeclarationColumn(line), end = at;
        while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '_'))
            end++;
        return line[at..end];
    }

    // A line of a file as it is in the editor or on the disk, empty when there's none
    private string LineAt(string path, int line)
    {
        try
        {
            if (Context.Documents.Find(path) is { } open)
                return line < open.Editor.Document.LineCount ? open.Editor.Document.LineAt(line) : string.Empty;
            return DiskLineAt(path, line);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    // A line of a file on the disk, off the editor's thread, empty when there's none
    private static string DiskLineAt(string path, int line)
    {
        try
        {
            return File.Exists(path) ? File.ReadLines(path).Skip(line).FirstOrDefault() ?? string.Empty : string.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private IEnumerable<McpToolResult?> McpTypes(JsonObject args)
    {
        var box = new McpStepBox();
        foreach (var step in McpSteps(McpLocate(args), box)) yield return step;
        if (box.Value is McpToolResult located) { yield return located; yield break; }
        var all = (List<McpCodeAt>)box.Value!;
        string direction = args.String("direction") ?? "both";
        int depth = Math.Clamp(args.Integer("depth") ?? 2, 1, 5);
        int budget = HexAgentProfiles.BudgetFor(McpServer.CallingClient.Value, args.Integer("max_chars"));
        bool up = direction != "down", down = direction != "up";

        // Each declaration in turn until one's a type the server knows, the index's base lists for a server with no type
        // hierarchy (pyright) or one that knows nothing of it
        LanguageServer? server = null;
        McpToolResult? failure = null;
        List<HexTreeNode> roots = [];
        List<McpCodeAt> tried = [];
        McpCodeAt? used = null;
        string how = string.Empty;
        foreach (McpCodeAt at in all)
        {
            foreach (var step in McpSteps(McpServerFor(at.Path, null, "a type hierarchy"), box)) yield return step;
            if (box.Value is McpToolResult noServer)
            {
                failure ??= noServer;
                continue;
            }
            server = (LanguageServer)box.Value!;
            tried.Add(at);

            if (server.Offers("typeHierarchyProvider"))
            {
                foreach (var step in McpSteps(McpAwait(TypeTree(server, at, up, down, depth), "the type hierarchy"), box)) yield return step;
                if (box.Value is List<HexTreeNode> tree)
                    roots = tree;
                else
                    failure ??= box.Value as McpToolResult;
            }

            if (roots.Count == 0 && at.Located is { Symbol.IsType: true } declared)
            {
                HexCodeIndex index = Harness;
                foreach (var step in McpSteps(McpAwait(Task.Run(() => index.IndexTypeTree(declared, up, down, depth)), "the types by the index"), box)) yield return step;
                if (box.Value is List<HexTreeNode> byIndex && byIndex.Exists(root => root.Children.Count > 0 || root.Outside.Count > 0))
                {
                    roots = byIndex;
                    how = $" {server.Name}'s server {(server.Offers("typeHierarchyProvider") ? "knew nothing of it" : "has no type hierarchy")}, this is by the base lists the index read, by name.";
                }
            }

            if (roots.Count > 0)
            {
                used = at;
                break;
            }
        }

        if (server is null || used is null)
        {
            yield return server is null && failure is not null ? failure
                : McpToolResult.Error(McpTriedAll($"{server?.Name ?? "The"}'s server has no type", all[0].Name, tried.Count > 0 ? tried : [all[0]]));
            yield break;
        }

        Func<string, string> relative = McpRelative();
        string moved = used != all[0] ? $" {all[0].Place(relative)} had nothing, {used.Place(relative)} was asked instead." : string.Empty;
        string title = $"The types of {used.Name}, {depth} deep, under it ^ are its bases and v what derives from it.{moved}{how}{(used.Also.Length > 0 ? $" {used.Also}" : string.Empty)}";
        yield return McpToolResult.Text(HexHierarchy.Describe(title, roots, relative, budget, "ask again from one of them"));
    }

    /// <summary>
    /// A type's bases (named ^) and what derives from it (named v) under it, to a depth. A partial type the server says
    /// once a file is said once, the file with its base list, with how many more.
    /// </summary>
    private static async Task<List<HexTreeNode>> TypeTree(LanguageServer server, McpCodeAt at, bool up, bool down, int depth)
    {
        var lines = new Dictionary<(string, int), string>();
        string LineOf(LanguageServerTypeItem item) => lines.TryGetValue((item.Declared.Path, item.Declared.Line), out string? known) ? known
            : lines[(item.Declared.Path, item.Declared.Line)] = DiskLineAt(item.Declared.Path, item.Declared.Line);
        List<(LanguageServerTypeItem Item, int Partials)> Fold(IEnumerable<LanguageServerTypeItem> items) =>
            HexHierarchy.FoldPartials(items, item => $"{item.Name}\n{item.Kind}\n{item.Detail}", LineOf);

        IReadOnlyList<LanguageServerTypeItem> items = await server.TypeItems(at.Path, at.Line, at.Column).ConfigureAwait(false);
        var roots = new List<HexTreeNode>();
        int made = 0;
        async Task Grow(HexTreeNode node, LanguageServerTypeItem item, bool bases, int left, HashSet<string> seen)
        {
            if (left == 0 || made >= MOST_TREE_NODES)
            {
                node.Cut = left == 0 ? node.Cut : true;
                return;
            }

            IReadOnlyList<LanguageServerTypeItem> related = await server.RelatedTypes(item, bases).ConfigureAwait(false);
            foreach (var (other, partials) in Fold(related).Take(MOST_CHILDREN * 2))
            {
                var child = new HexTreeNode((bases ? "^ " : "v ") + other.Name, other.Detail, HexHierarchy.KindName(other.Kind), other.Declared.Path, other.Declared.Line) { Partials = partials };
                node.Children.Add(child);
                made++;
                if (!seen.Add(child.Key))
                    child.Seen = true;
                else if (left > 1)
                    await Grow(child, other, bases, left - 1, seen).ConfigureAwait(false);
            }
        }

        foreach (var (item, partials) in Fold(items))
        {
            var root = new HexTreeNode(item.Name, item.Detail, HexHierarchy.KindName(item.Kind), item.Declared.Path, item.Declared.Line) { Partials = partials };
            roots.Add(root);
            if (up)
                await Grow(root, item, bases: true, depth, new HashSet<string>(StringComparer.Ordinal) { root.Key }).ConfigureAwait(false);
            if (down)
                await Grow(root, item, bases: false, depth, new HashSet<string>(StringComparer.Ordinal) { root.Key }).ConfigureAwait(false);
        }

        return roots;
    }

    private IEnumerable<McpToolResult?> McpRename(JsonObject args)
    {
        string renamed = (args.String("new_name") ?? string.Empty).Trim();
        if (renamed.Length == 0)
        {
            yield return McpToolResult.Error("Say new_name.");
            yield break;
        }

        var box = new McpStepBox();
        foreach (var step in McpSteps(McpLocate(args), box)) yield return step;
        if (box.Value is McpToolResult located) { yield return located; yield break; }
        var all = (List<McpCodeAt>)box.Value!;
        int budget = HexAgentProfiles.BudgetFor(McpServer.CallingClient.Value, args.Integer("max_chars"));

        // The first declaration the server will rename, one in a copy it hasn't loaded gives no edits
        LanguageServer? server = null;
        McpToolResult? failure = null;
        List<McpCodeAt> tried = [];
        foreach (McpCodeAt at in all)
        {
            foreach (var step in McpSteps(McpServerFor(at.Path, "renameProvider", "a rename"), box)) yield return step;
            if (box.Value is McpToolResult noServer)
            {
                failure ??= noServer;
                continue;
            }
            server = (LanguageServer)box.Value!;
            tried.Add(at);

            foreach (var step in McpSteps(McpAwait(server.Rename(at.Path, at.Line, at.Column, renamed), "the rename"), box)) yield return step;
            if (box.Value is McpToolResult failed)
            {
                failure ??= failed;
                continue;
            }
            if (box.Value is not IReadOnlyDictionary<string, IReadOnlyList<LanguageServerEdit>> { Count: > 0 } edits || edits.Values.All(list => list.Count == 0))
                continue;

            Func<string, string> relative = McpRelative();
            string moved = at != all[0] ? $" ({all[0].Place(relative)} gave nothing to rename, {at.Place(relative)} did)" : string.Empty;
            string said = McpApplyEdits(edits, args.Flag("dry_run"), $"{(args.Flag("dry_run") ? "Would rename" : "Renamed")} {at.Name} to {renamed}{moved}", budget);
            yield return said.StartsWith('!') ? McpToolResult.Error(said[1..]) : McpToolResult.Text(said);
            yield break;
        }

        yield return server is null && failure is not null ? failure
            : McpToolResult.Error(McpTriedAll($"{server?.Name ?? "The"}'s server wouldn't rename it", all[0].Name, tried.Count > 0 ? tried : [all[0]]) + " It's not something it can rename, or the project isn't loaded yet.");
    }

    /// <summary>
    /// A server's edits made, every file worked out before any is written and the ones written put back when one can't
    /// be, so it's all or nothing. A file open with unsaved changes is changed in the editor (one step to undo), any other
    /// on the disk with its line endings and byte order mark kept. What changed, a line a place, or ! and why not.
    /// </summary>
    private string McpApplyEdits(IReadOnlyDictionary<string, IReadOnlyList<LanguageServerEdit>> edits, bool dryRun, string title, int budget)
    {
        var made = new List<(string Path, HexDocument? Open, string Was, string Now, bool Bom)>();
        foreach (var (file, changes) in edits)
        {
            if (changes.Count == 0)
                continue;
            if (Context.Documents.Find(file) is { Modified: true } open)
            {
                string text = open.Editor.Text;
                made.Add((file, open, text, LanguageServer.Apply(text, changes), false));
                continue;
            }

            if (!File.Exists(file))
                return $"!{file} isn't there to change, nothing was changed.";
            try
            {
                var (raw, bom) = ReadKept(file);
                bool crlf = raw.Contains("\r\n", StringComparison.Ordinal);
                IReadOnlyList<LanguageServerEdit> fitted = crlf ? [.. changes.Select(edit => edit with { Text = edit.Text.Replace("\r\n", "\n").Replace("\n", "\r\n") })] : changes;
                made.Add((file, null, raw, LanguageServer.Apply(raw, fitted), bom));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return $"!{file} couldn't be read, {e.Message}, nothing was changed.";
            }
        }

        Func<string, string> relative = McpRelative();
        int places = edits.Values.Sum(list => list.Count);
        var said = new HexBudget(budget);
        said.Line($"{title}, {Places(places)} in {(made.Count == 1 ? "one file" : $"{made.Count} files")}{(dryRun ? ", nothing changed yet" : string.Empty)}.");
        foreach (var (file, changes) in edits)
        {
            foreach (int line in changes.Select(edit => edit.Line).Distinct().Order())
                said.Line($"{relative(file)}:{line + 1}");
        }

        if (dryRun)
            return said.Finish("ask with a bigger max_chars");

        var written = new List<(string Path, string Was, bool Bom)>();
        foreach (var (file, open, was, now, bom) in made)
        {
            if (open is not null)
                continue;
            try
            {
                WriteKept(file, now, bom);
                written.Add((file, was, bom));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                foreach (var (back, text, hadBom) in written)
                {
                    try { WriteKept(back, text, hadBom); } catch (Exception again) when (again is IOException or UnauthorizedAccessException) { }
                }
                return $"!{file} couldn't be written, {e.Message}, the {written.Count} files written before it were put back.";
            }
        }

        foreach (var (file, open, _, now, _) in made)
        {
            if (open is not null)
                Context.Documents.Rewrite(open, now.Replace("\r\n", "\n"));
            else
                languages.AgentFileWritten(file);
        }

        if (made.Any(each => each.Open is not null))
            said.Line($"{made.Count(each => each.Open is not null)} of them were open with unsaved changes and were changed in the editor, not on the disk.");
        return said.Finish("ask with a bigger max_chars");
    }

    private IEnumerable<McpToolResult?> McpFix(JsonObject args)
    {
        string path = McpPath(args.String("path") ?? string.Empty);
        int line = Math.Max(1, args.Integer("line") ?? 1) - 1, endLine = Math.Max(line, (args.Integer("end_line") ?? line + 1) - 1);
        string? apply = args.String("apply");
        int budget = HexAgentProfiles.BudgetFor(McpServer.CallingClient.Value, args.Integer("max_chars"));

        var box = new McpStepBox();
        int heard = languages.AgentDiagnostics(path).Count;
        foreach (var step in McpSteps(McpServerFor(path, "codeActionProvider", "fixes"), box)) yield return step;
        if (box.Value is McpToolResult noServer) { yield return noServer; yield break; }
        var server = (LanguageServer)box.Value!;

        // Its fixes go by what it said is wrong, which a file just opened in it hasn't been told yet
        if (heard == 0)
        {
            long giveUp = System.Environment.TickCount64 + 8_000;
            while (languages.AgentDiagnostics(path).Count == 0 && System.Environment.TickCount64 < giveUp)
                yield return null;
        }

        string lastText = LineAt(path, endLine);
        foreach (var step in McpSteps(McpAwait(server.Actions(path, line, 0, endLine, lastText.Length), "its fixes"), box)) yield return step;
        if (box.Value is McpToolResult failed) { yield return failed; yield break; }
        List<LanguageServerAction> actions = [.. ((IReadOnlyList<LanguageServerAction>)box.Value!).OrderBy(action => action.Fixes ? 0 : 1)];

        IReadOnlyList<UICodeDiagnostic> wrong = languages.AgentDiagnostics(path).Found ?? [];
        Func<string, string> relative = McpRelative();
        if (apply is not { Length: > 0 })
        {
            var said = new HexBudget(budget);
            foreach (UICodeDiagnostic diagnostic in wrong.Where(each => each.Line >= line && each.Line <= endLine))
                said.Line($"{relative(path)}:{diagnostic.Line + 1}:{diagnostic.Column + 1} {(diagnostic.Hint ? "hint" : diagnostic.Warning ? "warning" : "error")} {diagnostic.Message}");
            said.Line(actions.Count == 0 ? $"{server.Name}'s server has no fixes for line {line + 1}{(endLine > line ? $" to {endLine + 1}" : string.Empty)}." : $"{actions.Count} fixes, apply one by its number or title.");
            for (int i = 0; i < actions.Count; i++)
                said.Line($"{i + 1}. {(actions[i].Fixes ? "fix" : "refactor")}: {actions[i].Title}{(actions[i].Edits.Count > 1 ? $" ({actions[i].Edits.Count} files)" : string.Empty)}");
            yield return McpToolResult.Text(said.Finish("ask with a bigger max_chars"));
            yield break;
        }

        LanguageServerAction? chosen = int.TryParse(apply, out int number) && number >= 1 && number <= actions.Count ? actions[number - 1]
            : actions.Find(action => action.Title.Equals(apply, StringComparison.OrdinalIgnoreCase)) ?? actions.Find(action => action.Title.StartsWith(apply, StringComparison.OrdinalIgnoreCase));
        if (chosen is null)
        {
            yield return McpToolResult.Error($"There's no fix {apply} for line {line + 1}, there are {actions.Count}: {string.Join("; ", actions.Take(8).Select((action, i) => $"{i + 1}. {action.Title}"))}.");
            yield break;
        }

        if (chosen.Resolve is { } resolve)
        {
            foreach (var step in McpSteps(McpAwait(resolve(), "what the fix does"), box)) yield return step;
            if (box.Value is not LanguageServerAction resolved) { yield return box.Value as McpToolResult ?? McpToolResult.Error("The fix couldn't be worked out."); yield break; }
            chosen = resolved;
        }

        string result = chosen.Edits.Count > 0 ? McpApplyEdits(chosen.Edits, false, $"Applied {chosen.Title}", budget) : $"{chosen.Title} is a command of the server's.";
        if (result.StartsWith('!'))
        {
            yield return McpToolResult.Error(result[1..]);
            yield break;
        }

        if (chosen.Command is { } command)
        {
            // What the command asks for comes back as edits the editor makes in the files it opens for them, unsaved
            Task running = server.Execute(command);
            long giveUp = System.Environment.TickCount64 + 20_000;
            while (!running.IsCompleted && System.Environment.TickCount64 < giveUp)
                yield return null;
            result += running.IsCompletedSuccessfully ? " Its command ran, any edits it made are in the editor, unsaved." : " Its command didn't finish.";
        }

        yield return McpToolResult.Text(result);
    }

    private IEnumerable<McpToolResult?> McpDiagnostics(JsonObject args)
    {
        List<string> paths = [.. (args.String("paths") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(10).Select(McpPath)];
        if (paths.Count == 0)
        {
            yield return McpToolResult.Error("Say paths, the files to look at.");
            yield break;
        }

        bool errorsOnly = args.Flag("errors_only");
        int budget = HexAgentProfiles.BudgetFor(McpServer.CallingClient.Value, args.Integer("max_chars"));
        int waitSeconds = Math.Clamp(args.Integer("wait") ?? 60, 0, 120);
        long asked = System.Environment.TickCount64;

        var notes = new Dictionary<string, string>(PlatformPaths.Comparer);
        var before = new Dictionary<string, int>(PlatformPaths.Comparer);
        foreach (string path in paths)
        {
            before[path] = languages.AgentDiagnostics(path).Count;
            if (languages.AgentServer(path, start: true, out string? why) is not { } server)
                notes[path] = why ?? "no language server reads it";
            // A file whose text was just sent is asked about at once, where its server can be asked
            else if (server.IsReady && languages.AgentSentSince(path, asked))
                languages.AgentAskDiagnostics(path, server);
        }

        // Each file waited on until its server's said something of it, a file it already has said of no longer. A server
        // still starting is waited on first, up to the time a server's given to start, and wait counts from when it's up:
        // then a file it took while starting is sent it again at once (one may not look at it until it changes), and any
        // still unsaid of after a long while once more. Not oftener: each sending is a new version, and what the server
        // was about to say of the one before is thrown away as stale when it comes (csharp-ls takes longer than a few
        // seconds loading a solution, sent the file every 4 s it never answered at all)
        bool Waiting(string path) => !notes.ContainsKey(path) && languages.AgentDiagnostics(path).Count == 0;
        var startedCold = new HashSet<string>(paths.Where(path => !notes.ContainsKey(path) && languages.AgentServer(path, start: false, out _) is { IsReady: false }), PlatformPaths.Comparer);
        var readySince = new Dictionary<string, long>(PlatformPaths.Comparer);
        var nudged = new Dictionary<string, int>(PlatformPaths.Comparer);
        bool Due(string path, long now) => readySince.TryGetValue(path, out long since) ? now - since < waitSeconds * 1000L : now - asked < MCP_SERVER_START_MS;
        while (paths.Any(path => Waiting(path) && Due(path, System.Environment.TickCount64)))
        {
            foreach (string path in paths.Where(Waiting))
            {
                if (languages.AgentServer(path, start: false, out _) is not { IsReady: true })
                    continue;
                long now = System.Environment.TickCount64;
                if (!readySince.TryGetValue(path, out long since))
                    readySince[path] = since = now;
                int times = nudged.GetValueOrDefault(path);
                if (times == 0 && startedCold.Contains(path) || times <= (startedCold.Contains(path) ? 1 : 0) && now - since > MCP_DIAGNOSTICS_LATE_NUDGE_MS)
                {
                    nudged[path] = times + 1;
                    languages.AgentNudge(path);
                }
            }
            yield return null;
        }

        // One said of before whose text was sent again just now (it changed on the disk) is given a moment for what's said
        // of the latest text. One that hasn't changed isn't waited on: every call sat out the whole 1.5 s for a word that
        // was never coming, the file not having been sent
        long settle = System.Environment.TickCount64 + 1500;
        while (paths.Any(path => !notes.ContainsKey(path) && before[path] > 0 && languages.AgentSentSince(path, asked) && !languages.AgentSaidSince(path, asked))
            && System.Environment.TickCount64 < settle)
            yield return null;

        Func<string, string> relative = McpRelative();
        var said = new HexBudget(budget);
        int errors = 0, warnings = 0, notReady = 0;
        var lines = new List<string>();
        foreach (string path in paths)
        {
            var (count, found, server) = languages.AgentDiagnostics(path);
            if (notes.TryGetValue(path, out string? note))
            {
                lines.Add($"{relative(path)}: {note}");
                continue;
            }
            if (count == 0 || found is null)
            {
                notReady++;
                long waited = (System.Environment.TickCount64 - asked) / 1000;
                lines.Add(readySince.ContainsKey(path) || languages.AgentServer(path, start: false, out _) is { IsReady: true }
                    ? $"{relative(path)}: not ready, its server is up but said nothing of it in {waitSeconds} s (it may still be loading the project), ask again in a moment or with a bigger wait."
                    : $"{relative(path)}: not ready, its server is still starting ({waited} s), ask again in a moment.");
                continue;
            }

            List<UICodeDiagnostic> shown = [.. found.Where(each => !each.Hint && (!errorsOnly || !each.Warning)).OrderBy(each => each.Warning).ThenBy(each => each.Line)];
            errors += found.Count(each => !each.Warning);
            warnings += found.Count(each => each.Warning && !each.Hint);
            if (shown.Count == 0)
                lines.Add($"{relative(path)}: nothing wrong ({server}).");
            foreach (UICodeDiagnostic each in shown)
                lines.Add($"{relative(path)}:{each.Line + 1}:{each.Column + 1} {(each.Warning ? "warning" : "error")} {each.Message}");
        }

        said.Line(DiagnosticsTitle(errors, warnings, paths.Count, notReady, notes.Count));
        foreach (string line in lines)
            said.Line(line);
        yield return McpToolResult.Text(said.Finish("errors_only, or a bigger max_chars"));
    }

    /// <summary>
    /// hex_diagnostics's first line, the files whose server said nothing in time and those no server reads counted, so
    /// "0 errors" isn't read as all clear when one of them wasn't looked at.
    /// </summary>
    internal static string DiagnosticsTitle(int errors, int warnings, int files, int notReady, int unread)
    {
        string looked = notReady + unread == 0 ? string.Empty
            : $" ({string.Join(", ", new[] { notReady > 0 ? $"{notReady} {(notReady == 1 ? "file" : "files")} not ready" : null, unread > 0 ? $"{unread} no server reads" : null }.OfType<string>())}, not counted)";
        return $"{errors} errors and {warnings} warnings in {files} files{looked}, by their language servers.";
    }

    /// <summary>
    /// What a file's server says is wrong after it was changed, waited on a moment (a server that isn't running for it
    /// isn't started for this), the errors as lines to add to the answer of the change. Nulls while waiting, the text
    /// last, empty when there's nothing to say.
    /// </summary>
    private IEnumerable<string?> McpCheckAfter(string path, long edited)
    {
        // A server that isn't up yet is started for the next check rather than waited on now, the change has been made
        if (languages.AgentServer(path, start: true, out _) is not { } server || server.Failure is not null)
        {
            yield return string.Empty;
            yield break;
        }
        if (!server.IsReady)
        {
            yield return $"\nNo check yet, {server.Name}'s server is still starting (hex_diagnostics waits for it).";
            yield break;
        }

        // The first check a server's asked for waits longer, a server just up is still loading the project. What's waited
        // for is something it says after the edit was sent, not what it was saying of the text before
        float seconds = mcpChecked.Add(server) ? MCP_FIRST_CHECK_SECONDS : MCP_CHECK_SECONDS;
        long giveUp = System.Environment.TickCount64 + (long)(seconds * 1000);

        // A server that can be asked is, at once, rather than waited on until it publishes by itself (csharp-ls offers
        // it, whichever comes first is the answer)
        languages.AgentAskDiagnostics(path, server);
        while (!languages.AgentSaidSince(path, edited) && System.Environment.TickCount64 < giveUp)
            yield return null;

        // A server often says a file twice, at once as it's opened or changed (pyright with nothing) and again once it's
        // looked at it, the last of what comes in a moment is the one
        for (int heard = languages.AgentDiagnostics(path).Count; languages.AgentSaidSince(path, edited);)
        {
            long settle = System.Environment.TickCount64 + MCP_CHECK_SETTLE_MS;
            while (languages.AgentDiagnostics(path).Count == heard && System.Environment.TickCount64 < Math.Min(settle, giveUp + MCP_CHECK_SETTLE_MS))
                yield return null;
            if (languages.AgentDiagnostics(path).Count == heard)
                break;
            heard = languages.AgentDiagnostics(path).Count;
        }

        var (_, found, _) = languages.AgentDiagnostics(path);
        if (!languages.AgentSaidSince(path, edited) || found is null)
        {
            int before = found?.Count(each => !each.Warning) ?? -1;
            yield return $"\n{server.Name}: not checked yet, its server hadn't answered of the edit after {seconds:0} s (not the same as no errors{(before >= 0 ? $", before it there {(before == 1 ? "was 1 error" : $"were {before} errors")}" : string.Empty)}), hex_diagnostics asks again.";
            yield break;
        }

        List<UICodeDiagnostic> errors = [.. found.Where(each => !each.Warning).OrderBy(each => each.Line)];
        int warnings = found.Count(each => each.Warning && !each.Hint);
        if (errors.Count == 0)
        {
            yield return $"\n{server.Name}: clean, no errors after the edit{(warnings > 0 ? $", {warnings} warnings" : string.Empty)}.";
            yield break;
        }

        string relative = McpRelative()(path);
        var said = new StringBuilder($"\n{server.Name}: {(errors.Count == 1 ? "1 error" : $"{errors.Count} errors")}{(warnings > 0 ? $", {warnings} warnings" : string.Empty)} in it now.");
        foreach (UICodeDiagnostic each in errors.Take(10))
            said.Append($"\n{relative}:{each.Line + 1}:{each.Column + 1} {HexBudget.Cut(each.Message.ReplaceLineEndings(" "), width: 200)}");
        if (errors.Count > 10)
            said.Append($"\n... {errors.Count - 10} more, hex_diagnostics lists them.");
        yield return said.ToString();
    }

    /// <summary>
    /// Lines first to last taken out of a file, the blank lines either side of them made one run as long as the longer
    /// of the two was, so the spacing between the neighbours stays as it was (two blank lines between Python's functions,
    /// not four).
    /// </summary>
    internal static string[] WithoutLines(string[] lines, int first, int last)
    {
        static bool Blank(string line) => line.Trim().Length == 0;
        int above = 0, below = 0;
        while (first - above - 1 >= 0 && Blank(lines[first - above - 1]))
            above++;
        while (last + below + 1 < lines.Length && Blank(lines[last + below + 1]))
            below++;

        // At the end of the file the blank lines before it go too (the file still ends with its line break), at the top
        // the ones after it
        if (last + below + 1 >= lines.Length)
            return [.. lines.Take(first - above), .. lines.Length > 0 && lines[^1].Length == 0 && first - above > 0 ? [string.Empty] : Array.Empty<string>()];
        int skip = first - above == 0 ? below : Math.Min(above, below);
        return [.. lines.Take(first), .. lines.Skip(last + 1 + skip)];
    }

    private IEnumerable<McpToolResult?> McpEditSymbol(JsonObject args)
    {
        string symbol = args.String("symbol") ?? string.Empty;
        string replacement = (args.String("new_text") ?? string.Empty).Replace("\r\n", "\n");
        bool withComments = args.Flag("with_comments");

        var box = new McpStepBox();
        HexCodeIndex index = Harness;
        string? wrong = null;
        string? asked = args.String("path");
        Task<HexLocated?> finding = Task.Run(() => index.Locate(symbol, asked, out wrong));
        while (!finding.IsCompleted)
            yield return null;
        if (finding.IsFaulted || finding.Result is not { } found)
        {
            yield return McpToolResult.Error(wrong ?? $"{symbol} couldn't be found, {finding.Exception?.InnerException?.Message}");
            yield break;
        }

        string path = found.File.Path;
        HexDocument? open = Context.Documents.Find(path) is { Modified: true } modified ? modified : null;
        string raw;
        bool bom = false;
        if (open is not null)
            raw = open.Editor.Text;
        else
        {
            try
            {
                (raw, bom) = ReadKept(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                raw = string.Empty;
                wrong = e.Message;
            }
            if (wrong is not null)
            {
                yield return McpToolResult.Error($"{path} couldn't be read, {wrong}");
                yield break;
            }
        }

        // Found again in the text as it is this moment, the editor's for a file with unsaved changes
        bool crlf = raw.Contains("\r\n", StringComparison.Ordinal);
        string[] lines = raw.Replace("\r\n", "\n").Split('\n');
        HexLocated here = HexCodeIndex.LocateIn(new HexIndexedFile(path, found.File.Relative, raw.Length, 0, lines), found.Symbol, found.Others);
        int first = withComments ? here.First : here.Line, last = here.Last;
        if (last < first || last >= lines.Length)
        {
            yield return McpToolResult.Error($"Where {symbol} ends in {found.File.Relative} couldn't be told, hex_edit it instead.");
            yield break;
        }

        string[] put = replacement.Length == 0 ? [] : replacement.TrimEnd('\n').Split('\n');
        string[] changed = put.Length == 0 ? WithoutLines(lines, first, last) : [.. lines.Take(first), .. put, .. lines.Skip(last + 1)];
        string text = string.Join('\n', changed);
        string done = $"{(put.Length == 0 ? "Deleted" : "Replaced")} {here.Symbol.Kind} {here.Symbol.Full}, {found.File.Relative}:{first + 1}-{last + 1} ({last - first + 1} lines, now {put.Length}).{(here.AlsoSaid.Length > 0 ? $" {here.AlsoSaid}" : string.Empty)}";
        if (open is not null)
        {
            Context.Documents.Rewrite(open, text);
            done += " It had unsaved changes, so it was changed in the editor, not on the disk.";
        }
        else
        {
            try
            {
                WriteKept(path, crlf ? text.Replace("\n", "\r\n") : text, bom);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                wrong = e.Message;
            }
            if (wrong is not null)
            {
                yield return McpToolResult.Error($"{path} couldn't be written, {wrong}");
                yield break;
            }
            languages.AgentFileWritten(path);
        }

        foreach (McpToolResult? step in McpThenCheck(args, path, done))
            yield return step;
    }
}
