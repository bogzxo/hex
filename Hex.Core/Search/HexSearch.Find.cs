using System.Text.RegularExpressions;

using Horizon.Core.Platform;
using Horizon.UI.Components;

namespace Hex.Core;

/// <summary>What finding in files came to, every match, and what was wrong with a regex that doesn't build (null when nothing was).</summary>
internal sealed record HexSearchOutcome(List<HexResult> Found, string? Error = null);

// Finding in the files of a workspace, Find in Files and the references by name. Every match on a line is a result of
// its own, a line with three of them is three rows the way VS Code lists them. The files looked through can be narrowed
// to some globs and have others left out, and a file open with changes is looked through as it is in the editor
internal static partial class HexSearch
{
    /// <summary>
    /// Every match in the files under some folders, the case, whole words and a regex as the options say. A regex that
    /// doesn't build finds nothing, <see cref="Search"/> says why.
    /// </summary>
    public static List<HexResult> FindIn(IReadOnlyList<string> roots, string query, UIFindOptions options) => Search(roots, query, options).Found;

    /// <summary>
    /// Every match in the files under some folders, at most <see cref="MOST_FOUND"/> of them, one result each with where
    /// it starts and how long it is.
    /// </summary>
    /// <param name="include">The files to include, comma separated globs, empty for every file.</param>
    /// <param name="exclude">The files to exclude on top of search_exclude, comma separated globs.</param>
    /// <param name="open">The text of files open in the editor by their full path, looked through in place of the disk.</param>
    /// <param name="stale">Asked between files, true gives up (a newer search was asked for).</param>
    public static HexSearchOutcome Search(IReadOnlyList<string> roots, string query, UIFindOptions options, string include = "", string exclude = "",
        IReadOnlyDictionary<string, string>? open = null, Func<bool>? stale = null)
    {
        var found = new List<HexResult>();
        if (query.Length == 0)
            return new HexSearchOutcome(found);

        Regex? pattern = null;
        if (options.Regex && (pattern = Pattern(query, options, out string? error)) is null)
            return new HexSearchOutcome(found, error);

        HexScope? scope = HexScope.Of(include, exclude);
        string[]? fullRoots = scope is null ? null : [.. roots.Select(Path.GetFullPath)];
        StringComparison comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var matches = new List<(int Column, int Length)>();
        foreach (string file in TextFilesUnder(roots))
        {
            if (found.Count >= MOST_FOUND || stale?.Invoke() == true)
                break;

            string full = Path.GetFullPath(file);
            if (scope is not null && !InScope(scope, fullRoots!, full))
                continue;

            string? openText = null;
            bool fromOpen = open is not null && open.TryGetValue(full, out openText);
            string? content = fromOpen ? openText : ReadText(file);
            if (content is null || (pattern is null && !content.AsSpan().Contains(query, comparison)))
                continue;

            // Worked out for a file with a match in it, which a few of the workspace's files have
            string? relative = null;
            int line = 0, start = 0;
            while (found.Count < MOST_FOUND)
            {
                int newline = content.IndexOf('\n', start);
                int end = newline < 0 ? content.Length : newline;
                int limit = !fromOpen && newline >= 0 && end > start && content[end - 1] == '\r' ? end - 1 : end;
                ReadOnlySpan<char> said = content.AsSpan(start, limit - start);

                AllMatches(said, query, options, pattern, matches);
                if (matches.Count > 0)
                {
                    // The row shows the line without its indent, what's lit is where the match is in that
                    relative ??= RelativeTo(roots, file);
                    string trimmed = said.Trim().ToString();
                    int lead = said.Length - said.TrimStart().Length;
                    foreach (var (column, length) in matches)
                    {
                        if (found.Count >= MOST_FOUND)
                            break;
                        found.Add(new HexResult(file, line, column, $"{relative}:{line + 1}  {trimmed}", length, column - lead));
                    }
                }

                if (newline < 0)
                    break;
                start = newline + 1;
                line++;
            }
        }

        return new HexSearchOutcome(found);
    }

    /// <summary>The regex a query is as the options say, null and what's wrong with it for one that doesn't build.</summary>
    public static Regex? Pattern(string query, UIFindOptions options, out string? error)
    {
        error = null;
        try
        {
            return new Regex(query, (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException e)
        {
            error = e.Message;
            return null;
        }
    }

    /// <summary>Every place something is on a line as the options say (the pattern built from them for a regex), where it starts and how long.</summary>
    public static List<(int Column, int Length)> AllMatches(string line, string query, UIFindOptions options, Regex? pattern)
    {
        var into = new List<(int Column, int Length)>();
        AllMatches(line.AsSpan(), query, options, pattern, into);
        return into;
    }

    /// <summary>The same into a list that's kept, emptied first.</summary>
    public static void AllMatches(string line, string query, UIFindOptions options, Regex? pattern, List<(int Column, int Length)> into) =>
        AllMatches(line.AsSpan(), query, options, pattern, into);

    /// <summary>The same over a line span, avoiding per-line string allocations.</summary>
    public static void AllMatches(ReadOnlySpan<char> line, string query, UIFindOptions options, Regex? pattern, List<(int Column, int Length)> into)
    {
        into.Clear();
        if (pattern is not null)
        {
            try
            {
                foreach (ValueMatch match in pattern.EnumerateMatches(line))
                {
                    if (match.Length > 0 && (!options.WholeWord || IsWholeWord(line, match.Index, match.Length)))
                        into.Add((match.Index, match.Length));
                }
            }
            catch (RegexMatchTimeoutException)
            {
            }
            return;
        }

        if (query.Length == 0)
            return;

        StringComparison comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int offset = 0;
        while (offset <= line.Length - query.Length)
        {
            int rel = line[offset..].IndexOf(query, comparison);
            if (rel < 0)
                break;
            int at = offset + rel;
            if (!options.WholeWord || IsWholeWord(line, at, query.Length))
                into.Add((at, query.Length));
            offset = at + query.Length;
        }
    }

    /// <summary>Where something is first on a line as the options say (the pattern built from them for a regex), null for nowhere.</summary>
    public static int? FirstMatch(string line, string query, UIFindOptions options, Regex? pattern)
    {
        ReadOnlySpan<char> span = line.AsSpan();
        if (pattern is not null)
        {
            try
            {
                foreach (ValueMatch match in pattern.EnumerateMatches(span))
                {
                    if (match.Length > 0 && (!options.WholeWord || IsWholeWord(span, match.Index, match.Length)))
                        return match.Index;
                }
            }
            catch (RegexMatchTimeoutException)
            {
            }
            return null;
        }

        StringComparison comparison = options.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (int at = line.IndexOf(query, comparison); at >= 0; at = line.IndexOf(query, at + query.Length, comparison))
        {
            if (!options.WholeWord || IsWholeWord(span, at, query.Length))
                return at;
        }
        return null;
    }

    /// <summary>The toggles of the find bar that are on, said after what's asked, empty for none.</summary>
    public static string DescribeOptions(UIFindOptions options)
    {
        var on = new List<string>();
        if (options.MatchCase) on.Add("the case matching");
        if (options.WholeWord) on.Add("whole words");
        if (options.Regex) on.Add("as a regex");
        return on.Count == 0 ? string.Empty : $", {string.Join(", ", on)} (the find bar's toggles)";
    }

    private static bool IsWholeWord(ReadOnlySpan<char> line, int at, int length) =>
        (at == 0 || !UITextDocument.IsWordChar(line[at - 1])) && (at + length >= line.Length || !UITextDocument.IsWordChar(line[at + length]));

    // Whether the include and exclude boxes keep a file, by where it is in the folder it's under, and with more than one
    // folder by that with the folder's name in front as well
    private static bool InScope(HexScope scope, string[] fullRoots, string full)
    {
        string? root = null;
        for (int i = 0; i < fullRoots.Length; i++)
        {
            string whole = fullRoots[i];
            if (full.StartsWith(whole, PlatformPaths.Comparison) && (root is null || whole.Length > root.Length))
                root = whole;
        }

        if (root is null)
            return scope.Keeps(Path.GetFileName(full));

        string inside = HexExcludes.Relative(root, full);
        return scope.Keeps(inside, fullRoots.Length > 1 ? $"{Path.GetFileName(root.TrimEnd('\\', '/'))}/{inside}" : null);
    }
}
