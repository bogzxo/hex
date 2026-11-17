using System.Collections.Concurrent;
using System.Text;

namespace Hex.Core;

/// <summary>
/// What the harness has been asked, by tool. How many calls, how many came back out of the kept answers, the
/// characters handed back and the characters the plain way would have read (grep's whole paths and lines, files read
/// whole), which is the saving. A token is about four characters of code, near enough for this. Kept in a file when
/// it's given one, so the count goes on across Hex's restarts rather than starting at nothing each time.
/// </summary>
internal sealed class HexHarnessStats
{
    private sealed class Tool
    {
        public long Calls, Kept, Said, Plain;
    }

    // The least time between two writes of the file, in milliseconds, a busy agent calls the harness many times a second
    private const int SAVE_EVERY = 5000;

    private readonly ConcurrentDictionary<string, Tool> tools = new();

    // The same again by the MCP client that asked, so each agent system can be told what the harness saved it
    private readonly ConcurrentDictionary<string, Tool> clients = new(StringComparer.OrdinalIgnoreCase);

    private readonly string? file;
    private readonly Lock saving = new();
    private long saved;
    private int dirty;

    /// <param name="file">Where the counts are kept between runs, null for this run only (a self test).</param>
    public HexHarnessStats(string? file = null)
    {
        this.file = file;
        Load();
    }

    /// <summary>A call counted, what it said, what the plain way would have read and whether it came from the kept answers.</summary>
    public void Count(string tool, long said, long plain, bool kept)
    {
        CountOnly(tool, said, plain, kept);
        Interlocked.Exchange(ref dirty, 1);
        Save(force: false);
    }

    private void CountOnly(string tool, long said, long plain, bool kept)
    {
        Tool counted = tools.GetOrAdd(tool, _ => new Tool());
        Interlocked.Increment(ref counted.Calls);
        if (kept)
            Interlocked.Increment(ref counted.Kept);
        Interlocked.Add(ref counted.Said, said);
        Interlocked.Add(ref counted.Plain, Math.Max(plain, said));

        if (McpServer.CallingClient.Value is { Length: > 0 } client)
        {
            Tool by = clients.GetOrAdd(client, _ => new Tool());
            Interlocked.Increment(ref by.Calls);
            Interlocked.Add(ref by.Said, said);
            Interlocked.Add(ref by.Plain, Math.Max(plain, said));
        }
    }

    /// <summary>
    /// The counts written to the file, at most every few seconds unless <paramref name="force"/> (as Hex closes). Written
    /// beside it and moved over it, so a Hex killed halfway leaves the last whole one. A line a tool or a client, its
    /// kind, name and four numbers between tabs, which needs no JSON reflection in a native build.
    /// </summary>
    public void Save(bool force = true)
    {
        if (file is null || Volatile.Read(ref dirty) == 0 || (!force && Environment.TickCount64 - Interlocked.Read(ref saved) < SAVE_EVERY))
            return;

        lock (saving)
        {
            if (Interlocked.Exchange(ref dirty, 0) == 0)
                return;
            Interlocked.Exchange(ref saved, Environment.TickCount64);
            var text = new StringBuilder();
            foreach (var (name, tool) in tools)
                text.Append($"tool\t{name}\t{Interlocked.Read(ref tool.Calls)}\t{Interlocked.Read(ref tool.Kept)}\t{Interlocked.Read(ref tool.Said)}\t{Interlocked.Read(ref tool.Plain)}\n");
            foreach (var (name, client) in clients)
                text.Append($"client\t{name}\t{Interlocked.Read(ref client.Calls)}\t{Interlocked.Read(ref client.Kept)}\t{Interlocked.Read(ref client.Said)}\t{Interlocked.Read(ref client.Plain)}\n");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                string writing = file + ".new";
                File.WriteAllText(writing, text.ToString());
                File.Move(writing, file, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Tried again with the next call, the counts are still here
                Interlocked.Exchange(ref dirty, 1);
            }
        }
    }

    // The counts of earlier runs taken back, a line that doesn't read skipped
    private void Load()
    {
        if (file is null || !File.Exists(file))
            return;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (string line in lines)
        {
            string[] parts = line.Split('\t');
            if (parts.Length != 6 || parts[1].Length == 0 || !long.TryParse(parts[2], out long calls) || !long.TryParse(parts[3], out long kept)
                || !long.TryParse(parts[4], out long said) || !long.TryParse(parts[5], out long plain))
                continue;

            ConcurrentDictionary<string, Tool>? into = parts[0] switch { "tool" => tools, "client" => clients, _ => null };
            if (into is null)
                continue;
            Tool counted = into.GetOrAdd(parts[1], _ => new Tool());
            (counted.Calls, counted.Kept, counted.Said, counted.Plain) = (calls, kept, said, plain);
        }
    }

    /// <summary>
    /// The calls, characters handed back and what the plain way would have read for the clients whose names have one of
    /// <paramref name="names"/> in them, the way an agent profile's clients match the client that connected.
    /// </summary>
    public (long Calls, long Said, long Plain) TotalsFor(IReadOnlyCollection<string> names)
    {
        long calls = 0, said = 0, plain = 0;
        foreach (var (client, tool) in clients)
        {
            if (!names.Any(name => client.Contains(name, StringComparison.OrdinalIgnoreCase)))
                continue;
            (calls, said, plain) = (calls + Interlocked.Read(ref tool.Calls), said + Interlocked.Read(ref tool.Said), plain + Interlocked.Read(ref tool.Plain));
        }

        return (calls, said, plain);
    }

    /// <summary>Every tool's together, the calls, the characters handed back and what the plain way would have read.</summary>
    public (long Calls, long Said, long Plain) Totals()
    {
        long calls = 0, said = 0, plain = 0;
        foreach (Tool tool in tools.Values)
            (calls, said, plain) = (calls + Interlocked.Read(ref tool.Calls), said + Interlocked.Read(ref tool.Said), plain + Interlocked.Read(ref tool.Plain));
        return (calls, said, plain);
    }

    /// <summary>The whole of it as a few lines, a tool a line and the total at the end.</summary>
    public string Describe()
    {
        if (tools.IsEmpty)
            return "Nothing has been asked of the harness yet.\n";

        var said = new StringBuilder("tool  calls  kept  chars back  plain way  saved\n");
        long allSaid = 0, allPlain = 0, allCalls = 0;
        foreach (var (name, tool) in tools.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            said.Append($"{name}  {tool.Calls}  {tool.Kept}  {tool.Said}  {tool.Plain}  {Saved(tool.Said, tool.Plain)}\n");
            (allSaid, allPlain, allCalls) = (allSaid + tool.Said, allPlain + tool.Plain, allCalls + tool.Calls);
        }

        said.Append($"all  {allCalls} calls, {allSaid} chars (about {allSaid / 4} tokens) where the plain way reads {allPlain} (about {allPlain / 4}), {Saved(allSaid, allPlain)} saved\n");
        return said.ToString();
    }

    /// <summary>
    /// The same as a Markdown table for people, a tool a row with its tokens (a quarter of its characters, near enough)
    /// and the total under them in bold. Null while nothing has been asked.
    /// </summary>
    public string? Table()
    {
        if (tools.IsEmpty)
            return null;

        var said = new StringBuilder("| Tool | Calls | Cache hits | Returned | Baseline | Saved |\n|:--|--:|--:|--:|--:|--:|\n");
        long allSaid = 0, allPlain = 0, allCalls = 0, allKept = 0;
        foreach (var (name, tool) in tools.OrderByDescending(pair => pair.Value.Plain - pair.Value.Said))
        {
            said.Append($"| `{name}` | {tool.Calls} | {tool.Kept} | {Tokens(tool.Said)} | {Tokens(tool.Plain)} | {Saved(tool.Said, tool.Plain)} |\n");
            (allSaid, allPlain, allCalls, allKept) = (allSaid + tool.Said, allPlain + tool.Plain, allCalls + tool.Calls, allKept + tool.Kept);
        }

        said.Append($"| **All** | **{allCalls}** | **{allKept}** | **{Tokens(allSaid)}** | **{Tokens(allPlain)}** | **{Saved(allSaid, allPlain)}** |\n");
        return said.ToString();
    }

    // Characters as tokens, short, 1.2k past a thousand
    private static string Tokens(long chars)
    {
        long tokens = chars / 4;
        return tokens >= 1000 ? $"{tokens / 1000.0:0.#}k" : tokens.ToString();
    }

    private static string Saved(long said, long plain) => plain <= 0 ? "0%" : $"{100.0 * (plain - said) / plain:0}%";
}
