using System.Text;

namespace Hex.Core;

/// <summary>The recent edits written out for the prompts that aren't Zeta's: as comments for fill in the middle, as tagged diffs for chat.</summary>
internal static class PredictionEventText
{
    /// <summary>The most tokens one event takes in a prompt, a longer one is cut at a line.</summary>
    public const int EVENT_TOKENS = 400;

    /// <summary>The tokens the history comment of a fill in the middle prompt takes at most.</summary>
    public const int FIM_TOKENS = 600;

    /// <summary>The tokens the recent edits of a chat prompt take at most.</summary>
    public const int CHAT_TOKENS = 1500;

    public const string HEADER = "Recent edits (oldest first):";

    /// <summary>
    /// The events as comment lines in the file's own syntax, oldest first under a header, the newest kept while they fit
    /// <paramref name="most"/> tokens, then the rename's summary line. Empty when there is nothing to say.
    /// </summary>
    public static string Comments(IReadOnlyList<PredictionEvent> events, PredictionRenameHint? rename, string path, string marker, int most)
    {
        string slashed = path.Replace('\\', '/');
        string renamed = rename is null ? "" : marker + " " + rename.Summary(path) + "\n";
        int left = most - PredictionTokens.Of(marker + " " + HEADER + "\n" + renamed);

        var chosen = new List<string>();
        for (int i = events.Count - 1; i >= 0 && left > 0; i--)
        {
            var written = new StringBuilder();
            string diffPath = events[i].Path.Replace('\\', '/');
            if (events[i].Predicted)
                written.Append(marker).Append(" (accepted suggestion)\n");
            foreach (string row in events[i].Diff.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (row.StartsWith("@@", StringComparison.Ordinal))
                    written.Append(marker).Append(' ').Append(diffPath).Append(' ').Append(row).Append('\n');
                else
                    written.Append(marker).Append(' ').Append(row).Append('\n');
            }

            string text = Cut(written.ToString(), Math.Min(EVENT_TOKENS, left));
            if (text.Length == 0)
                break;
            left -= PredictionTokens.Of(text);
            chosen.Add(text);
        }

        if (chosen.Count == 0 && renamed.Length == 0)
            return "";
        var block = new StringBuilder(marker).Append(' ').Append(HEADER).Append('\n');
        for (int i = chosen.Count - 1; i >= 0; i--)
            block.Append(chosen[i]);
        return block.Append(renamed).ToString();
    }

    /// <summary>
    /// The events as diffs, oldest first, each tagged (typed) or (accepted suggestion) above its file header, the newest
    /// kept while they fit <paramref name="most"/> tokens. Empty for none.
    /// </summary>
    public static string Diffs(IReadOnlyList<PredictionEvent> events, int most)
    {
        var chosen = new List<string>();
        int left = most;
        for (int i = events.Count - 1; i >= 0 && left > 0; i--)
        {
            string path = events[i].Path.Replace('\\', '/');
            string written = (events[i].Predicted ? "(accepted suggestion)" : "(typed)") + "\n--- a/" + path + "\n+++ b/" + path + "\n" + events[i].Diff;
            string text = Cut(written, Math.Min(EVENT_TOKENS, left));
            if (text.Length == 0)
                break;
            left -= PredictionTokens.Of(text);
            chosen.Add(text);
        }

        var all = new StringBuilder();
        for (int i = chosen.Count - 1; i >= 0; i--)
            all.Append(chosen[i]);
        return all.ToString();
    }

    /// <summary>The text with whole lines kept while they fit the tokens; the text itself when it fits, empty when not one line does.</summary>
    internal static string Cut(string text, int tokens)
    {
        if (PredictionTokens.Of(text) <= tokens)
            return text;

        var kept = new StringBuilder();
        int at = 0;
        while (at < text.Length)
        {
            int end = text.IndexOf('\n', at);
            end = end < 0 ? text.Length : end + 1;
            if (PredictionTokens.Of(kept.ToString() + text[at..end]) > tokens)
                break;
            kept.Append(text, at, end - at);
            at = end;
        }
        return kept.ToString();
    }
}
