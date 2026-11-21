using System.Diagnostics;
using System.Runtime.CompilerServices;

using Horizon.UI.Scripting;

namespace Hex.Dawn;

// A C# server warmed once it has loaded its solution and the editor has been left alone a moment. csharp-ls answers the
// first prepare of a call or type hierarchy in a few milliseconds, the solution is loaded by then, but the first look
// across the solution waits on Roslyn: the first callers asked for compile every project that depends on the one the
// method is in (2.5 to 4 s on the workspace's solution), and the first derived types build the index of every project's
// declarations and of the assemblies they reference (7 s). Each later one takes milliseconds. hex_calls and hex_types
// paid for both the first time an agent asked, 8.7 and 6.6 s. Asked here once for a method and a type of a file open in
// the server while nobody is typing, the agent's first question is answered like the rest. Again after the server loads
// its projects again, a project file it was told of.
internal sealed partial class HexLanguages
{
    // How long nothing is typed before the server is warmed, and how often that's looked at again while it's typed in
    private const int WARM_QUIET_MS = 2000;

    private sealed class Warmth
    {
        public volatile bool Loaded, Going, Done;
    }

    private readonly ConditionalWeakTable<LanguageServer, Warmth> warmth = new();
    private long lastEdited = Environment.TickCount64;
    private Timer? warmWaker;

    /// <summary>
    /// Interfaces declared near a file, nearest first, for when the files open in a server have only sealed classes in
    /// them (the scene's, from its index). Asked for on the editor's thread as a warming starts, what it gives is asked
    /// off it, where the index can be walked.
    /// </summary>
    internal Func<Func<string, IEnumerable<(string Path, int Line, int Column)>>>? TypesNear { get; set; }

    /// <summary>How long the last warming of a server took and what it asked, for the self tests and the log.</summary>
    internal string? LastWarmed { get; private set; }

    // Only a server that has the hierarchies and a solution to load is worth it, csharp-ls
    private static bool Warms(LanguageServer server) =>
        server.LanguageId == "csharp" && server.Failure is null && server.Offers("callHierarchyProvider");

    // The server said something of a file, so its projects are loaded (csharp-ls says nothing until they are)
    private void WarmHeard(LanguageServer server)
    {
        if (!Warms(server))
            return;
        Warmth state = warmth.GetOrCreateValue(server);
        if (state.Loaded)
            return;
        state.Loaded = true;
        WakeToWarm(WARM_QUIET_MS);
    }

    // A project file it was told of has it load the solution again, and everything it compiled goes with it
    private void WarmAgainAfter(LanguageServer server)
    {
        if (warmth.TryGetValue(server, out Warmth? state) && !state.Going)
            state.Loaded = state.Done = false;
    }

    private void WarmTyped() => lastEdited = Environment.TickCount64;

    private void WakeToWarm(long inMs)
    {
        warmWaker ??= new Timer(_ => context.Wake());
        warmWaker.Change(Math.Max(1, inMs), Timeout.Infinite);
    }

    /// <summary>Every tick, the servers that have loaded and not been warmed are, once nothing's been typed for a moment.</summary>
    private void UpdateWarm()
    {
        IReadOnlyList<LanguageServer> servers = LanguageServers.Running;
        for (int i = 0; i < servers.Count; i++)
        {
            LanguageServer server = servers[i];
            if (!warmth.TryGetValue(server, out Warmth? state) || !state.Loaded || state.Going || state.Done || !Warms(server))
                continue;

            long quiet = Environment.TickCount64 - lastEdited;
            if (quiet < WARM_QUIET_MS)
            {
                WakeToWarm(WARM_QUIET_MS - quiet);
                continue;
            }

            if (WarmFiles(server) is not { Count: > 0 } paths)
                continue;
            state.Going = true;
            Task<string> warming = Warm(server, paths, TypesNear?.Invoke());
            warming.ContinueWith(done =>
            {
                state.Going = false;
                state.Done = true;
                LastWarmed = done.IsCompletedSuccessfully ? done.Result : "failed";
                Console.WriteLine($"[Hex] {server.Name} warmed, {LastWarmed}.");
            }, TaskScheduler.Default);
        }
    }

    // How many of the files open in a server are looked through for a method and a type that can be derived from
    private const int WARM_FILES = 6;

    // The files the server has open to find a method and a type in, the one being looked at first, then the other tabs,
    // then those an agent's tools opened in it with no tab of their own (hex_diagnostics on a file nobody looks at)
    private List<string> WarmFiles(LanguageServer server)
    {
        var paths = new List<string>();
        void Add(string path)
        {
            if (paths.Count < WARM_FILES && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !paths.Contains(path, PlatformPaths.Comparer))
                paths.Add(path);
        }

        HexDocument current = context.Documents.Current;
        if (current.Path is { } shown && linters.TryGetValue(current, out LanguageServer? its) && ReferenceEquals(its, server))
            Add(shown);
        foreach (var (of, by) in linters)
        {
            if (ReferenceEquals(by, server) && of.Path is { } path)
                Add(path);
        }
        foreach (var (path, file) in agentFiles)
        {
            if (ReferenceEquals(file.Server, server))
                Add(path);
        }
        return paths;
    }

    /// <summary>
    /// The callers of a method and the types derived from a class or interface asked for, the first of each in the
    /// files, and what each took. Nothing shown, nothing kept, the server keeps what it worked out. A sealed or static
    /// class won't do: Roslyn knows nothing derives from it without looking, and the index stays unbuilt.
    /// </summary>
    internal static async Task<string> Warm(LanguageServer server, IReadOnlyList<string> paths, Func<string, IEnumerable<(string Path, int Line, int Column)>>? typesNear = null)
    {
        (string Path, LanguageServerSymbol Symbol)? method = null, type = null;
        foreach (string path in paths)
        {
            if (method is not null && type is not null)
                break;
            if (await server.Symbols(path).ConfigureAwait(false) is not { } symbols)
                continue;

            string[] lines;
            try
            {
                lines = await File.ReadAllLinesAsync(path).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var pending = new Stack<LanguageServerSymbol>(symbols.Reverse());
            while (pending.Count > 0 && (type is null || method is null))
            {
                LanguageServerSymbol next = pending.Pop();
                if (type is null && (next.Kind is "interface" || (next.Kind is "class" && Derivable(lines, next))))
                    type = (path, next);
                else if (method is null && next.Kind is "method")
                    method = (path, next);
                foreach (LanguageServerSymbol child in next.Children.Reverse())
                    pending.Push(child);
            }
        }

        var said = new List<string>();
        var clock = Stopwatch.StartNew();
        if (method is var (methodPath, called) && await server.CallItems(methodPath, called.Line, called.Character).ConfigureAwait(false) is { Count: > 0 } calls)
        {
            await server.IncomingCalls(calls[0]).ConfigureAwait(false);
            said.Add($"callers of {called.Name} {clock.ElapsedMilliseconds} ms");
        }

        clock.Restart();
        if (!server.Offers("typeHierarchyProvider"))
            return said.Count == 0 ? "nothing to ask" : string.Join(", ", said);

        // The open files' own, or an interface near them, which none of them is sealed
        var candidates = new List<(string Path, int Line, int Column)>();
        if (type is var (typePath, derived))
            candidates.Add((typePath, derived.Line, derived.Character));
        else if (typesNear is not null && paths.Count > 0)
            candidates.AddRange(typesNear(paths[0]).Take(3));
        foreach (var (where, line, column) in candidates)
        {
            if (await server.TypeItems(where, line, column).ConfigureAwait(false) is not { Count: > 0 } types)
                continue;
            await server.RelatedTypes(types[0], bases: false).ConfigureAwait(false);
            said.Add($"types under {types[0].Name} {clock.ElapsedMilliseconds} ms");
            break;
        }

        return said.Count == 0 ? "nothing to ask" : string.Join(", ", said);
    }

    // Whether a class can be derived from, by the words on its line before its name
    private static bool Derivable(string[] lines, LanguageServerSymbol type)
    {
        if (type.Line >= lines.Length)
            return false;
        string line = lines[type.Line];
        string before = line[..Math.Min(type.Character, line.Length)];
        return !before.Contains("sealed ", StringComparison.Ordinal) && !before.Contains("static ", StringComparison.Ordinal);
    }
}
