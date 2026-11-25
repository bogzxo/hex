using System.Text.RegularExpressions;

namespace Hex.Core;

/// <summary>A place a far (jump) prediction is asked for: a line of the text (from 0), its offsets, and why it was picked.</summary>
internal readonly record struct NextEditCandidate(int Line, int Start, int End, string Reason);

/// <summary>
/// Finds where the user's next edit may be, away from the caret (docs/next-edit-plan.md, 2.2 A), from the newest event
/// of the edit history: the lines that still write an identifier the event removed, and the lines that call or declare a
/// name whose parameters the event changed (a simple token match, no language server). The lines the event itself wrote
/// and the caret's own line are left out; the nearest to the caret come first.
/// </summary>
internal static partial class NextEditLocator
{
    /// <summary>The most places asked for in one pause.</summary>
    public const int MOST = 3;

    // Texts bigger than this aren't searched
    private const int MOST_CHARS = 1_000_000;

    // Names that come up in every file, and say nothing when an edit takes one away
    private static readonly HashSet<string> common = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "foreach", "while", "do", "return", "var", "let", "const", "new", "int", "string", "bool", "void",
        "true", "false", "null", "this", "self", "def", "fn", "func", "function", "class", "public", "private", "static",
        "using", "import", "from", "in", "is", "as", "not", "and", "or", "try", "catch", "await", "async", "get", "set",
    };

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Identifier { get; }

    [GeneratedRegex(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*\(([^()]*)\)")]
    private static partial Regex Call { get; }

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@")]
    private static partial Regex Header { get; }

    /// <summary>
    /// The places to ask about, at most <paramref name="most"/>, nearest to the caret first. The newest event of
    /// <paramref name="path"/> is the one looked at (the newest of all when no path is given).
    /// </summary>
    public static IReadOnlyList<NextEditCandidate> Candidates(string text, IReadOnlyList<PredictionEvent> history, int caret = 0, string? path = null, int most = MOST)
    {
        if (text.Length == 0 || text.Length > MOST_CHARS || most <= 0)
            return [];

        PredictionEvent? latest = null;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            if (path is null || history[i].Path == path)
            {
                latest = history[i];
                break;
            }
        }
        if (latest is null)
            return [];

        Read(latest.Diff, out HashSet<string> removed, out HashSet<string> changedCalls, out HashSet<int> written);
        if (removed.Count == 0 && changedCalls.Count == 0)
            return [];

        string[] lines = text.Split('\n');
        int caretLine = 0;
        var starts = new int[lines.Length];
        for (int i = 0, at = 0; i < lines.Length; i++)
        {
            starts[i] = at;
            if (caret >= at && caret <= at + lines[i].Length)
                caretLine = i;
            at += lines[i].Length + 1;
        }

        var found = new List<NextEditCandidate>();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i == caretLine || written.Contains(i) || lines[i].AsSpan().Trim().IsEmpty)
                continue;

            string? reason = null;
            foreach (string name in removed)
            {
                if (HasWord(lines[i], name))
                {
                    reason = "still uses " + name;
                    break;
                }
            }
            if (reason is null)
            {
                foreach (string name in changedCalls)
                {
                    if (HasCall(lines[i], name))
                    {
                        reason = "uses " + name + " whose parameters changed";
                        break;
                    }
                }
            }
            if (reason is not null)
                found.Add(new NextEditCandidate(i, starts[i], starts[i] + lines[i].Length, reason));
        }

        found.Sort((a, b) => Math.Abs(a.Line - caretLine).CompareTo(Math.Abs(b.Line - caretLine)));
        return found.Count > most ? found.GetRange(0, most) : found;
    }

    // What an event's hunks say: the identifiers taken away (and not written back anywhere in the event), the names whose
    // parenthesised part differs between a removed and an added line, and the lines of the new text the event wrote
    private static void Read(string diff, out HashSet<string> removed, out HashSet<string> changedCalls, out HashSet<int> written)
    {
        removed = new(StringComparer.Ordinal);
        changedCalls = new(StringComparer.Ordinal);
        written = [];

        var gone = new List<string>();
        var added = new List<string>();
        var addedIdentifiers = new HashSet<string>(StringComparer.Ordinal);
        int line = 0;
        foreach (string row in diff.Split('\n'))
        {
            if (row.Length == 0)
                continue;
            if (row[0] == '@')
            {
                if (Header.Match(row) is { Success: true } header)
                    line = int.Parse(header.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) - 1;
                continue;
            }
            switch (row[0])
            {
                case '-':
                    gone.Add(row[1..]);
                    break;
                case '+':
                    added.Add(row[1..]);
                    written.Add(line++);
                    foreach (Match match in Identifier.Matches(row, 1))
                        addedIdentifiers.Add(match.Value);
                    break;
                default:
                    line++;
                    break;
            }
        }

        foreach (string row in gone)
        {
            foreach (Match match in Identifier.Matches(row))
            {
                string name = match.Value;
                if (name.Length >= 2 && !common.Contains(name) && !addedIdentifiers.Contains(name))
                    removed.Add(name);
            }
        }

        // A name called or declared with other parameters than before
        foreach (string row in gone)
        {
            foreach (Match old in Call.Matches(row))
            {
                string name = old.Groups[1].Value;
                if (common.Contains(name))
                    continue;
                foreach (string now in added)
                {
                    foreach (Match match in Call.Matches(now))
                    {
                        if (match.Groups[1].Value == name && match.Groups[2].Value.Trim() != old.Groups[2].Value.Trim())
                            changedCalls.Add(name);
                    }
                }
            }
        }
    }

    // Whether a line writes a name as a whole word
    private static bool HasWord(string line, string name)
    {
        for (int at = line.IndexOf(name, StringComparison.Ordinal); at >= 0; at = line.IndexOf(name, at + 1, StringComparison.Ordinal))
        {
            bool before = at == 0 || !IsWordChar(line[at - 1]);
            int end = at + name.Length;
            bool after = end >= line.Length || !IsWordChar(line[end]);
            if (before && after)
                return true;
        }
        return false;
    }

    // Whether a line calls or declares a name: the whole word followed by an opening parenthesis
    private static bool HasCall(string line, string name)
    {
        for (int at = line.IndexOf(name, StringComparison.Ordinal); at >= 0; at = line.IndexOf(name, at + 1, StringComparison.Ordinal))
        {
            if (at > 0 && IsWordChar(line[at - 1]))
                continue;
            int end = at + name.Length;
            while (end < line.Length && line[end] == ' ')
                end++;
            if (end < line.Length && line[end] == '(')
                return true;
        }
        return false;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
