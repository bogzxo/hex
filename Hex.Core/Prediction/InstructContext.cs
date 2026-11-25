using System.Text;
using System.Text.RegularExpressions;

namespace Hex.Core;

/// <summary>A file (or other text) the person added to an edit with @, as the prompt carries it: its name and the text, already capped.</summary>
internal sealed record InstructContext(string Name, string Text);

/// <summary>
/// The @ mentions in an AI edit's instruction (docs/ai-edit-plan.md): "@diagnostics" for the language server's problems of
/// the whole file, "@path" for a file's text. Pure, so it is tested without the editor.
/// </summary>
internal static partial class InstructMentions
{
    /// <summary>What the person asked for the context to be about: the diagnostics keyword.</summary>
    public const string DIAGNOSTICS = "diagnostics";

    /// <summary>The most tokens one file adds to the prompt.</summary>
    public const int FILE_TOKENS = 2000;

    /// <summary>The instruction with its mentions taken out, the files they name (in order, once each) and whether @diagnostics was there.</summary>
    public static (string Instruction, List<string> Files, bool Diagnostics) Parse(string text)
    {
        var files = new List<string>();
        bool diagnostics = false;
        string rest = Mention().Replace(text, match =>
        {
            string name = match.Groups[1].Value.TrimEnd('.', ',', ';', ':', ')', '?', '!');
            if (name.Length == 0)
                return match.Value;
            if (string.Equals(name, DIAGNOSTICS, StringComparison.OrdinalIgnoreCase))
                diagnostics = true;
            else if (!files.Contains(name, StringComparer.OrdinalIgnoreCase))
                files.Add(name);
            return string.Empty;
        });
        return (Spaces().Replace(rest, " ").Trim(), files, diagnostics);
    }

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex Spaces();

    /// <summary>
    /// The word being typed when it is an @ mention: the text after the last @ that starts a word, up to the end of the text
    /// (no space in it yet). Null when the end of the text isn't in the middle of one.
    /// </summary>
    public static string? Typing(string text)
    {
        int at = text.LastIndexOf('@');
        if (at < 0 || at > 0 && !char.IsWhiteSpace(text[at - 1]))
            return null;
        string word = text[(at + 1)..];
        return word.Any(char.IsWhiteSpace) ? null : word;
    }

    /// <summary>The text with the mention being typed taken off its end.</summary>
    public static string WithoutTyping(string text)
    {
        int at = text.LastIndexOf('@');
        return Typing(text) is null ? text : text[..at];
    }

    /// <summary>
    /// A file's text for the prompt, no more than <paramref name="maxTokens"/>. A longer file is cut round the lines that
    /// use the target's identifiers (<paramref name="terms"/>), each place with a few lines either side, the first lines
    /// when nothing matches; the gaps are marked with "…".
    /// </summary>
    public static string Cap(string text, IReadOnlyCollection<string> terms, int maxTokens = FILE_TOKENS)
    {
        text = text.Replace("\r\n", "\n");
        if (PredictionTokens.Of(text) <= maxTokens)
            return text;

        string[] lines = text.Split('\n');
        var keep = new bool[lines.Length];
        int budget = maxTokens, used = 0;

        bool Take(int line)
        {
            if (keep[line])
                return true;
            int cost = PredictionTokens.Of(lines[line]) + 1;
            if (used + cost > budget)
                return false;
            keep[line] = true;
            used += cost;
            return true;
        }

        // Round the lines naming one of the terms first, nearest rings first so the matches share the budget
        var hits = new List<int>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (terms.Any(term => lines[i].Contains(term, StringComparison.Ordinal)))
                hits.Add(i);
        }
        for (int ring = 0; ring <= 3 && hits.Count > 0; ring++)
        {
            foreach (int hit in hits)
            {
                foreach (int line in ring == 0 ? [hit] : new[] { hit - ring, hit + ring })
                {
                    if (line >= 0 && line < lines.Length && !Take(line))
                        goto full;
                }
            }
        }
        full:

        // Nothing matched, or room left: the start of the file
        for (int i = 0; i < lines.Length && used < budget && hits.Count == 0; i++)
        {
            if (!Take(i))
                break;
        }

        var made = new StringBuilder();
        bool gap = false;
        for (int i = 0; i < lines.Length; i++)
        {
            if (keep[i])
            {
                made.Append(lines[i]).Append('\n');
                gap = false;
            }
            else if (!gap)
            {
                made.Append("…\n");
                gap = true;
            }
        }
        return made.ToString();
    }

    /// <summary>The identifiers of a text worth looking for in another: four letters or more, once each, most used first, at most 40.</summary>
    public static List<string> Terms(string text) =>
        [.. Identifier().Matches(text).Select(m => m.Value).GroupBy(v => v, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(40).Select(g => g.Key)];

    [GeneratedRegex(@"(?<![\w@])@([^\s@]+)")]
    private static partial Regex Mention();

    [GeneratedRegex(@"[A-Za-z_]\w{3,}")]
    private static partial Regex Identifier();
}
