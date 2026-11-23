using Hex.Core;

namespace Hex.Dawn;

/// <summary>
/// The parts of the terminal's predictions that need no terminal: whether to ask for the line being typed, what the model
/// is shown (the commands that were run, then the line) and the ghost text out of what it answered. The model is never
/// shown input typed at a prompt for a password: such a line isn't in the history (the terminal keeps none of them) and
/// a prompt for one is a closed gate, see <see cref="Gate"/>.
/// </summary>
internal static class HexTerminalPredictionGlue
{
    /// <summary>The commands run before, newest last, shown to the model with the line.</summary>
    public const int HISTORY_LINES = 20;

    /// <summary>A line longer than this is not a command anybody means to have continued, and isn't shown.</summary>
    public const int LONGEST_LINE = 400;

    /// <summary>The name the model is told the terminal's text is a file of.</summary>
    public const string PATH = "terminal.sh";

    public const string LANGUAGE = "shellscript";

    /// <summary>Whether a line may be predicted for now: on, the terminal page showing, a shell at its prompt with the caret at the end of a line, nothing asked for a secret.</summary>
    public static bool Gate(bool on, bool focused, bool atShellPrompt, bool atSecretPrompt, bool caretAtEnd, string line) =>
        on && focused && atShellPrompt && !atSecretPrompt && caretAtEnd && line.Trim().Length > 0 && line.Length <= LONGEST_LINE;

    /// <summary>Whether predictions are on for the terminal: on at all, in this folder, and the terminal's own setting.</summary>
    public static bool On(HexSettings settings) => settings.EditPredictionsTerminal && PredictionSettings.On(settings);

    /// <summary>The lines the model is shown: the last commands run, the line being typed last.</summary>
    public static string[] Lines(IReadOnlyList<string> history, string line)
    {
        var kept = new List<string>(HISTORY_LINES + 1);
        for (int i = Math.Max(0, history.Count - HISTORY_LINES); i < history.Count; i++)
        {
            string entry = history[i];
            if (entry.Length is 0 or > LONGEST_LINE || entry.Contains('\n') || LooksSecret(entry))
                continue;
            kept.Add(entry);
        }

        kept.Add(line);
        return [.. kept];
    }

    /// <summary>
    /// The text to show after the line, null when the answer isn't that: a single insertion at the end of the text, the
    /// first line of it, with nothing but white space after the line dropped.
    /// </summary>
    public static string? Ghost(PredictionResult result, IReadOnlyList<string> lines)
    {
        if (!result.Ghost || result.Edits.Count != 1)
            return null;

        int length = Math.Max(0, lines.Count - 1);
        for (int i = 0; i < lines.Count; i++)
            length += lines[i].Length;

        PredictionEdit edit = result.Edits[0];
        if (edit.Start != length || edit.End != length || edit.Text.Length == 0)
            return null;

        string text = edit.Text.ReplaceLineEndings("\n");
        int newline = text.IndexOf('\n');
        if (newline >= 0)
            text = text[..newline];

        // It is typed into the pty: no control characters or escape sequences from a model
        return Horizon.UI.Components.TerminalView.CleanGhost(text);
    }

    private static readonly System.Text.RegularExpressions.Regex SecretBearing = new(
        @"(key\s*=|token|secret|passw(or)?d|authorization\s*:|bearer\s|(^|\s)-p\S+|://[^/\s:]+:[^@\s]+@|\b(aws|gh|github)_\w*\s*=)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Whether a command in the history looks like it carries a credential, and so isn't sent to the model.</summary>
    public static bool LooksSecret(string command) => SecretBearing.IsMatch(command);
}
