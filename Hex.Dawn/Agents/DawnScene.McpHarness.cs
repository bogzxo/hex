using System.Text.Json.Nodes;

using Horizon.UI.Components;

namespace Hex.Dawn;

// The agent harness's tools, finding code fast and cheap (Hex.Core's HexCodeIndex does the finding). They touch nothing
// of the editor's, so they all run off its thread and answer while it's busy. The index is made the first time an agent
// asks something, kept on disk between runs, and handed what the language servers of the open files say they declare.
// What changed is a resource a client can subscribe to as well as a tool. docs/agent-harness-plan.md has the why of it.
internal sealed partial class DawnScene
{
    // The resource a client subscribes to for what changed
    internal const string HARNESS_CHANGES = "hex://harness/changes";

    // How often the open files are looked at for symbols their servers haven't been asked for yet, in seconds
    private const float HARNESS_SYMBOLS_EVERY = 3.0f;

    private HexCodeIndex? harness;

    // The workspace's folders for the index, written on the editor's thread and read on the pipe's
    private volatile string[] harnessRoots = [];

    // What the servers were last asked about each open file, its length and time, so a file is asked once a change
    private readonly Dictionary<string, (long Length, long Written)> harnessAsked = new(StringComparer.OrdinalIgnoreCase);
    private float harnessSymbolsWait;

    /// <summary>The index, made the first time it's wanted, its cache in Hex's settings folder unless it's testing itself.</summary>
    internal HexCodeIndex Harness
    {
        get
        {
            if (harness is { } made)
                return made;

            harness = new HexCodeIndex(() => harnessRoots, options.SelfTest ? null : Path.Combine(TerminalFiles.UserFolder, "harness"));
            AppDomain.CurrentDomain.ProcessExit += (_, _) => harness?.SaveCache();
            return harness;
        }
    }

    private IEnumerable<HexMcpTool> HarnessTools()
    {
        // A schema's node can only be in one schema, so each tool gets its own
        static (string, JsonObject) Max() => ("max_chars", McpSchema.Integer("Stay under this many characters, the agent's profile says how many when it isn't said (8000 usually)."));
        return
        [
            Harnessed(new McpTool("hex_map", "Workspace map",
                "The workspace's folders as a tree a few levels down, with how many files of each language and the main types declared in each. Start here in a codebase you don't know.",
                McpSchema.Object([
                    ("under", McpSchema.String("Only this folder, a path inside the workspace.")),
                    ("depth", McpSchema.Integer("How many folders down, 2 by default.")), Max()]), ReadOnly: true),
                (index, args, budget) => index.Map(args.String("under"), Math.Clamp(args.Integer("depth") ?? 2, 0, 8), budget)),

            Harnessed(new McpTool("hex_files", "Find files",
                "Files by part of their name or path, best first, with their line counts. Words between spaces or slashes all have to fit.",
                McpSchema.Object([
                    ("query", McpSchema.String("Part of a file's name or path, \"mcp schema\" or \"Agents/Mcp\".")),
                    ("most", McpSchema.Integer("At most this many, 30 by default.")), Max()], "query"), ReadOnly: true),
                (index, args, budget) => index.Files(args.String("query") ?? string.Empty, Math.Clamp(args.Integer("most") ?? 30, 1, 500), budget)),

            Harnessed(new McpTool("hex_symbols", "Find declarations",
                "Where things are declared by name, ranked (exact, other case, start of the name, its capitals like QM for QueueMcp, anywhere in it). Container.Name narrows to what's in something. One line each, path:line kind Container.Name and the declaration. Use before grep.",
                McpSchema.Object([
                    ("query", McpSchema.String("A name or part of one, or Container.Name.")),
                    ("kind", McpSchema.String("Only this kind, type, member, class, interface, struct, enum, method, property, field.")),
                    ("path", McpSchema.String("Only files whose path inside the workspace starts with this.")),
                    ("most", McpSchema.Integer("At most this many, 25 by default.")), Max()], "query"), ReadOnly: true),
                (index, args, budget) => index.Symbols(args.String("query") ?? string.Empty, args.String("kind"), args.String("path"), Math.Clamp(args.Integer("most") ?? 25, 1, 500), budget)),

            Harnessed(new McpTool("hex_search", "Search code",
                "Search the workspace's text, ranked rather than in file order. Declarations of what was asked first (marked *), then files by name and how many places they have, places grouped under their file, lines cut short around the match.",
                McpSchema.Object([
                    ("query", McpSchema.String("What to look for.")),
                    ("regex", McpSchema.Flag("The query is a .NET regular expression.")),
                    ("match_case", McpSchema.Flag("Upper and lower case count.")),
                    ("whole_word", McpSchema.Flag("Only whole words.")),
                    ("glob", McpSchema.String("Only files that fit this glob, \"*.cs\" or \"Hex.Core/**\", a comma between several.")),
                    ("context", McpSchema.Integer("Lines either side of each place, 0 to 5, 0 by default.")),
                    ("most", McpSchema.Integer("At most this many places, 60 by default.")), Max()], "query"), ReadOnly: true),
                (index, args, budget) => index.Search(args.String("query") ?? string.Empty, args.Flag("regex"), args.Flag("match_case"), args.Flag("whole_word"),
                    args.String("glob"), args.Integer("context") ?? 0, Math.Clamp(args.Integer("most") ?? 60, 1, 2000), budget)),

            Harnessed(new McpTool("hex_refs", "Where a name is used",
                "Everywhere a name is used, by the whole word outside comments and strings, its declarations marked *, grouped by file. By text, so no file needs to be open, hex_references asks a language server instead.",
                McpSchema.Object([
                    ("name", McpSchema.String("The name, Container.Name is taken as Name.")),
                    ("glob", McpSchema.String("Only files that fit this glob.")),
                    ("most", McpSchema.Integer("At most this many places, 80 by default.")), Max()], "name"), ReadOnly: true),
                (index, args, budget) => index.References(args.String("name") ?? string.Empty, args.String("glob"), Math.Clamp(args.Integer("most") ?? 80, 1, 2000), budget)),

            Harnessed(new McpTool("hex_read", "Read a declaration",
                "Read one declaration out of a file by its name, a method or a class with the comments above it to where it ends, instead of the whole file. Or lines of a file by range. Line numbers in front. Without a path the best declaration of that name anywhere.",
                McpSchema.Object([
                    ("path", McpSchema.String("The file, inside the workspace or whole, or just its name when only one file has it.")),
                    ("symbol", McpSchema.String("The name of what to read, Name or Container.Name.")),
                    ("start_line", McpSchema.Integer("First line, for a range, 1 is the first.")),
                    ("end_line", McpSchema.Integer("Last line, inclusive.")),
                    ("numbers", McpSchema.Flag("Line numbers in front, true by default.")), Max()]), ReadOnly: true),
                (index, args, budget) => index.Read(args.String("path"), args.String("symbol"), args.Integer("start_line"), args.Integer("end_line"), args.Flag("numbers", true), budget)),

            Harnessed(new McpTool("hex_skim", "Skim files",
                "What files are without reading them, each its size, language, the summary or comment it starts with and its outline with line numbers. Several files at once, a comma between them, or a glob.",
                McpSchema.Object([
                    ("paths", McpSchema.String("The files, a comma between them, or a glob like \"Hex.Core/Mcp/*.cs\".")),
                    ("depth", McpSchema.Integer("How deep the outline goes, 0 is only the top, 2 by default.")), Max()], "paths"), ReadOnly: true),
                (index, args, budget) => index.Skim(args.String("paths") ?? string.Empty, Math.Clamp(args.Integer("depth") ?? 2, 0, 10), budget)),

            Harnessed(new McpTool("hex_changes", "What changed",
                "The files that came, changed or went since an index generation (every harness answer ends with the generation it's of). For catching up without reading again.",
                McpSchema.Object([("since", McpSchema.Integer("The generation to go from.")), Max()], "since"), ReadOnly: true),
                (index, args, budget) => index.Changes(args.Integer("since") ?? 0, budget)),

            new(new McpTool("hex_harness_stats", "Harness stats",
                "What the harness was asked this run, calls and characters handed back by tool, against what the plain way (grep and reading files whole) would have read.",
                McpSchema.Empty(), ReadOnly: true),
                null, RunAway: (_, _) =>
                {
                    HexCodeIndex index = Harness;
                    HexAgentProfile? profile = HexAgentProfiles.For(McpServer.CallingClient.Value);
                    return Task.FromResult(McpToolResult.Text($"{index.Current().Files.Length} files indexed, {index.FromCache} of them out of the cache on disk, generation {index.Generation}."
                        + $" You are {McpServer.CallingClient.Value ?? "a client"}, {(profile is null ? "with no agent profile" : $"profile {profile.Name}")}, budget {HexAgentProfiles.BudgetFor(McpServer.CallingClient.Value, null)} characters.\n"
                        + index.Stats.Describe()));
                })
        ];
    }

    /// <summary>A harness tool, run off the editor's thread with the index and the budget of whoever's asking.</summary>
    private HexMcpTool Harnessed(McpTool meta, Func<HexCodeIndex, JsonObject, int, HexAnswer> answer) =>
        new(meta, null, RunAway: (arguments, cancel) =>
        {
            int budget = HexAgentProfiles.BudgetFor(McpServer.CallingClient.Value, arguments.Integer("max_chars"));
            return Task.Run(() =>
            {
                HexAnswer said = answer(Harness, arguments, budget);
                return said.Failed ? McpToolResult.Error(said.Text) : McpToolResult.Text(said.Text);
            }, cancel);
        });

    /// <summary>The resources the server has, the log of changes for a client to subscribe to.</summary>
    private McpResources HarnessResources() => new(
        [(HARNESS_CHANGES, "Workspace changes", "Every file that came, changed or went since Hex started indexing, told when the workspace changes once subscribed. hex_changes says it from a generation.")],
        uri => uri == HARNESS_CHANGES ? Harness.Changes(0, HexAgentProfiles.USUAL_BUDGET).Text : null,
        (uri, changed) =>
        {
            if (uri != HARNESS_CHANGES)
                return null;

            HexCodeIndex index = Harness;
            Action<long> told = _ => changed();
            index.Changed += told;
            index.Watch();
            return new HarnessWatch(() =>
            {
                index.Changed -= told;
                index.Unwatch();
            });
        });

    private sealed class HarnessWatch(Action stop) : IDisposable
    {
        private int stopped;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref stopped, 1) == 0)
                stop();
        }
    }

    /// <summary>
    /// Every few seconds while there's an index, the open files whose servers say what they declare asked for it, once
    /// each time a file's saved as something new. A file with changes that aren't saved isn't, it isn't what's on disk.
    /// </summary>
    private void UpdateHarness(float dt)
    {
        if (harness is null || (harnessSymbolsWait -= dt) > 0.0f)
            return;

        harnessSymbolsWait = HARNESS_SYMBOLS_EVERY;
        foreach (HexDocument open in documents)
        {
            if (open.Path is not { } path || open.Modified || !File.Exists(path))
                continue;

            var info = new FileInfo(path);
            (long Length, long Written) stamp = (info.Length, info.LastWriteTimeUtc.Ticks);
            if (harnessAsked.TryGetValue(path, out var asked) && asked == stamp)
                continue;

            HexCodeIndex index = harness;
            if (languages.AskSymbols(open, symbols => index.TakeServerSymbols(path, stamp.Length, stamp.Written, HexCodeIndex.FromServer(symbols))))
                harnessAsked[path] = stamp;
        }
    }
}
