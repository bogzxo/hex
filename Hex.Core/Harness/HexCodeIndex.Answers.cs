namespace Hex.Core;

// The answers kept, by what was asked and the generation of the index they were made at, the last few dozen. Agents
// ask the same thing twice more often than you'd think (after a compaction, from a sub agent, to check), and the second
// time costs nothing. Every answer ends with the generation it's of, what hex_changes takes to say what moved since.
internal sealed partial class HexCodeIndex
{
    // The most answers kept
    private const int MOST_KEPT = 64;

    private readonly Dictionary<string, (long Generation, HexAnswer Answer)> kept = [];
    private readonly Queue<string> keptOrder = new();
    private readonly Lock keeping = new();

    /// <summary>
    /// An answer to a query, the kept one when the same was asked of the same generation, made fresh otherwise, and
    /// counted in the stats either way. <paramref name="asked"/> is everything that makes it different from another.
    /// </summary>
    public HexAnswer Ask(string tool, string asked, Func<HexIndexSnapshot, HexAnswer> make)
    {
        HexIndexSnapshot now = Current();
        string key = $"{tool}\n{asked}";
        lock (keeping)
        {
            if (kept.TryGetValue(key, out var was) && was.Generation == now.Generation)
            {
                Stats.Count(tool, was.Answer.Text.Length, was.Answer.Plain, kept: true);
                return was.Answer;
            }
        }

        HexAnswer made = make(now);
        if (!made.Failed)
            made = made with { Text = made.Text + $"(index generation {now.Generation})\n" };

        lock (keeping)
        {
            if (!kept.ContainsKey(key))
                keptOrder.Enqueue(key);
            kept[key] = (now.Generation, made);
            while (keptOrder.Count > MOST_KEPT)
                kept.Remove(keptOrder.Dequeue());
        }

        Stats.Count(tool, made.Text.Length, made.Plain, kept: false);
        return made;
    }

    /// <summary>A path an agent gave, matched against the index, whole, inside a folder of the workspace, or by its end.</summary>
    public static HexIndexedFile? FileOf(HexIndexSnapshot index, string asked)
    {
        string wanted = asked.Trim().Replace('\\', '/').TrimStart('.', '/');
        if (Path.IsPathRooted(asked) && index.File(Path.GetFullPath(asked)) is { } whole)
            return whole;

        foreach (string root in index.Roots)
        {
            if (index.File(Path.GetFullPath(Path.Combine(root, wanted))) is { } inside)
                return inside;
        }

        // Just its name or the end of its path, Mcp/McpSchema.cs, when only one file ends that way
        HexIndexedFile[] ending = [.. index.Files.Where(file => file.Relative.Equals(wanted, StringComparison.OrdinalIgnoreCase)
            || file.Relative.EndsWith("/" + wanted, StringComparison.OrdinalIgnoreCase))];
        return ending.Length == 1 ? ending[0] : null;
    }

    /// <summary>Why a path didn't name a file, with the files it might have meant.</summary>
    public static string NoFile(HexIndexSnapshot index, string asked)
    {
        string name = Path.GetFileName(asked.Replace('\\', '/'));
        string[] like = [.. index.Files.Where(file => file.Relative.Contains(name, StringComparison.OrdinalIgnoreCase)).Take(8).Select(file => file.Relative)];
        return like.Length == 0
            ? $"No file of the workspace is {asked}. hex_files finds one by part of its name."
            : $"No one file of the workspace is {asked}. Maybe {string.Join(", ", like)}.";
    }

    /// <summary>The kinds an agent may narrow symbols by, what the outline and the servers call things.</summary>
    public static bool KindFits(string kind, string? wanted) => wanted is null || wanted.Length == 0 || wanted switch
    {
        "type" => kind is "class" or "interface" or "struct" or "enum" or "namespace",
        "member" => kind is "method" or "property" or "field",
        _ => kind.Equals(wanted, StringComparison.OrdinalIgnoreCase)
    };
}
