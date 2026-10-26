using System.Text.RegularExpressions;

using Horizon.HIDL.Lexing;

namespace Hex.Core;

// Errors that say what to do, Thonny's assistant cut down to a line. A name HIDL doesn't know is null rather than a
// mistake, so what goes wrong is a misspelt function called ("moev isn't a function, it is null"), a member of
// nothing ("there is nothing to get 'damage' from") or a member a list or a text hasn't got ("a list has no 'pus'"),
// and nine times in ten the name was a letter or two off one the file or the language has. That one is said after.
internal static class HexFixes
{
    /// <summary>"Did you mean ...?" after an error that names something a letter or two off a name there is, empty otherwise.</summary>
    public static string LikelyFix(string error, string code)
    {
        // The language says it itself now, for a name nobody declared and a member a list or a text hasn't got
        if (error.Contains("did you mean", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        var named = new List<string>();
        foreach (Match quoted in Regex.Matches(error, "'([A-Za-z_][A-Za-z0-9_]*)'"))
            named.Add(quoted.Groups[1].Value);
        if (Regex.Match(error, @"([A-Za-z_][A-Za-z0-9_]*) isn't a function") is { Success: true } called)
            named.Add(called.Groups[1].Value);
        if (named.Count == 0)
            return string.Empty;

        // What there is, the file's names, the language's, and whatever the error lists as what it has
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (Token token in Lexer.Tokenize(code, lenient: true))
        {
            if (token.Type == TokenType.Identifier)
                known.Add(token.Value);
        }
        foreach (string word in new[] { "print", "include", "keys", "values", "entries", "has", "range", "len", "text", "number", "format", "typeof", "error", "defined", "definition", "merge", "math", "vec" })
            known.Add(word);
        if (error.IndexOf("It has ", StringComparison.Ordinal) is >= 0 and var listed)
        {
            foreach (Match word in Regex.Matches(error[listed..], "[a-z_]+"))
                known.Add(word.Value);
        }

        foreach (string name in named)
        {
            string? best = null;
            int closest = int.MaxValue;
            foreach (string candidate in known)
            {
                if (candidate == name || Math.Abs(candidate.Length - name.Length) > 2)
                    continue;

                int distance = Distance(name, candidate);
                if (distance < closest && distance <= (name.Length <= 4 ? 1 : 2))
                {
                    closest = distance;
                    best = candidate;
                }
            }

            if (best is not null)
                return $" Did you mean {best}?";
        }

        return string.Empty;
    }

    /// <summary>How many letters have to change to turn one word into another (Levenshtein), stopping early past three.</summary>
    public static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 3)
            return 4;

        Span<int> previous = stackalloc int[b.Length + 1];
        Span<int> current = stackalloc int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            current.CopyTo(previous);
        }

        return previous[b.Length];
    }
}
