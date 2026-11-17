using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Hex.Core;

/// <summary>
/// What a model is called wherever Hex says one. The model the agent says it runs (claude-opus-5-5) over the alias it
/// was started with (opus), which says nothing of the version, and written in full, Claude Opus 5.5 rather than
/// opus-5-5. The names an agent system says of its models itself (Codex's model/list, Claude Code's initialize) are kept
/// and come first.
/// </summary>
internal static partial class HexAgentModels
{
    // The names agent systems said of their models, by id, the most recent kept
    private static readonly ConcurrentDictionary<string, string> said = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A model's name as its agent system says it, kept for every place a model is said.</summary>
    public static void Said(string id, string name)
    {
        if (id.Length > 0 && name.Length > 0 && said.Count < 500)
            said[id] = name;
    }

    /// <summary>The model a chat runs, in full, null when nothing's known of it.</summary>
    public static string? Of(HexAgentChat chat) =>
        Name(chat.Model is { Length: > 0 } running ? running : chat.Options.GetValueOrDefault("model"));

    /// <summary>A model by its id or alias, in full. Null or empty stays null.</summary>
    public static string? Name(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;
        id = id.Trim();
        if (said.TryGetValue(id, out string? named))
            return named;

        // A context window said after it, sonnet[1m]
        string window = string.Empty;
        if (Window().Match(id) is { Success: true } bracket)
        {
            window = $" ({bracket.Groups[1].Value.ToUpperInvariant()} context)";
            id = id[..bracket.Index];
        }

        if (id.StartsWith("claude", StringComparison.OrdinalIgnoreCase) || id is "opus" or "sonnet" or "haiku" or "fable")
            return Claude(id) + window;
        if (id.StartsWith("gpt", StringComparison.OrdinalIgnoreCase))
            return "GPT-" + string.Join('-', id[3..].Split('-', StringSplitOptions.RemoveEmptyEntries).Select(Title)) + window;
        if (id.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
            return string.Join(' ', id.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(Title)) + window;
        return id + window;
    }

    // claude-opus-5-5, claude-sonnet-4-5-20250929, claude-3-5-haiku-20241022, opus, as Claude Opus 5.5 and the like
    private static string Claude(string id)
    {
        string[] parts = [.. id.Split('-', StringSplitOptions.RemoveEmptyEntries).Where(part => !(part.Length == 8 && part.All(char.IsAsciiDigit)))];
        string[] words = [.. parts.Where(part => !part.All(char.IsAsciiDigit) && !part.Equals("claude", StringComparison.OrdinalIgnoreCase)).Select(Title)];
        string version = string.Join('.', parts.Where(part => part.All(char.IsAsciiDigit)));
        return "Claude " + string.Join(' ', words) + (version.Length > 0 ? " " + version : string.Empty);
    }

    // A word of a model's id with its first letter up, a word that's a version (5.1, 2.5) left as it is
    private static string Title(string part) =>
        part.Length == 0 || char.IsAsciiDigit(part[0]) ? part : char.ToUpper(part[0], CultureInfo.InvariantCulture) + part[1..];

    [GeneratedRegex(@"\[(\w+)\]$")]
    private static partial Regex Window();
}
