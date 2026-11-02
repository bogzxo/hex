using System.Collections.Concurrent;
using System.Text;

namespace Hex.Core;

/// <summary>
/// What the harness was asked this run, by tool. How many calls, how many came back out of the kept answers, the
/// characters handed back and the characters the plain way would have read (grep's whole paths and lines, files read
/// whole), which is the saving. A token is about four characters of code, near enough for this.
/// </summary>
internal sealed class HexHarnessStats
{
    private sealed class Tool
    {
        public long Calls, Kept, Said, Plain;
    }

    private readonly ConcurrentDictionary<string, Tool> tools = new();

    /// <summary>A call counted, what it said, what the plain way would have read and whether it came from the kept answers.</summary>
    public void Count(string tool, long said, long plain, bool kept)
    {
        Tool counted = tools.GetOrAdd(tool, _ => new Tool());
        Interlocked.Increment(ref counted.Calls);
        if (kept)
            Interlocked.Increment(ref counted.Kept);
        Interlocked.Add(ref counted.Said, said);
        Interlocked.Add(ref counted.Plain, Math.Max(plain, said));
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

    private static string Saved(long said, long plain) => plain <= 0 ? "0%" : $"{100.0 * (plain - said) / plain:0}%";
}
