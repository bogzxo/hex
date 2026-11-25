using System.Text.RegularExpressions;

namespace Hex.Core;

// Search and references. Both find lines and both hand them back grouped under their file, the path once and then the
// places, the best file first rather than the first in the alphabet. A line that declares what was looked for comes
// first of all and says so with a *, then files by how much of the query is in their path and by how many places they
// have. References are search for a whole name with the ones in comments and strings left out.
internal sealed partial class HexCodeIndex
{
    // The widest a line is shown, cut around what was found
    private const int LINE_WIDTH = 150;

    private sealed record Place(int Line, int Column, bool Declares);
    private delegate int LineMatch(HexIndexedFile file, ReadOnlySpan<char> line);

    /// <summary>
    /// hex_search, text or a regular expression over the files a glob says (all of them without one), ranked and
    /// grouped, with a few lines either side when <paramref name="context"/> asks for them.
    /// </summary>
    public HexAnswer Search(string query, bool regex, bool matchCase, bool wholeWord, string? glob, int context, int most, int budget) =>
        Ask("hex_search", $"{query}\n{regex}\n{matchCase}\n{wholeWord}\n{glob}\n{context}\n{most}\n{budget}", index =>
        {
            if (query.Length == 0)
                return HexAnswer.Wrong("Say what to look for in query.");

            Regex pattern;
            try
            {
                string text = regex ? query : Regex.Escape(query);
                pattern = new Regex(wholeWord ? $@"\b(?:{text})\b" : text, (matchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
            }
            catch (ArgumentException e)
            {
                return HexAnswer.Wrong($"That isn't a regular expression .NET reads, {e.Message}");
            }

            HexGlob[] only = glob is { Length: > 0 } ? HexGlob.All(glob.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) : [];
            string bare = regex ? string.Empty : query;
            StringComparison how = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            LineMatch find = regex
                ? (_, line) => FirstMatch(pattern, line, null, null, how)
                : wholeWord
                    ? (_, line) => FirstMatch(pattern, line, null, query, how)
                    // Plain text is found by a vectorised search of the line, no expression involved
                    : (_, line) => line.IndexOf(query, how);
            return Grouped(index, file => InGlobs(only, file), find, regex ? null : query, how,
                bare, query, Math.Clamp(context, 0, 5), most, budget, "hex_search");
        });

    /// <summary>
    /// hex_refs, a name by the whole word wherever it's used, outside of comments and strings, its declarations marked.
    /// By text, so another thing with the same name is among them, which a language server's hex_references isn't fooled by.
    /// </summary>
    public HexAnswer References(string name, string? glob, int most, int budget) =>
        Ask("hex_refs", $"{name}\n{glob}\n{most}\n{budget}", index =>
        {
            string asked = name.Trim();
            string container = string.Empty;
            if (asked.Contains('.'))
            {
                container = asked[..asked.LastIndexOf('.')];
                container = container[(container.LastIndexOf('.') + 1)..];
                asked = asked[(asked.LastIndexOf('.') + 1)..];
            }
            if (asked.Length == 0 || !asked.All(c => char.IsLetterOrDigit(c) || c == '_'))
                return HexAnswer.Wrong("hex_refs takes a name, letters, digits and underscores. hex_search finds anything else.");

            var pattern = new Regex($@"\b{Regex.Escape(asked)}\b", RegexOptions.CultureInvariant);
            HexGlob[] only = glob is { Length: > 0 } ? HexGlob.All(glob.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) : [];

            // Container.Name, by the place rather than the file: a declaration of it inside the container, or a use whose
            // receiver is plausibly one (the container itself, this or nothing in the container's own files, a name
            // declared as one). By text still and approximate, hex_calls and hex_references ask the language server
            Regex? of = container.Length > 0 && container.All(c => char.IsLetterOrDigit(c) || c == '_')
                ? new Regex($@"\b{Regex.Escape(container)}\b", RegexOptions.CultureInvariant) : null;
            if (of is null)
            {
                return Grouped(index, file => InGlobs(only, file),
                    (file, line) => FirstMatch(pattern, line, file, asked, StringComparison.Ordinal), asked, StringComparison.Ordinal,
                    asked, asked, 0, most, budget, "hex_refs");
            }

            var receivers = new HexReceivers(index, container);
            int leftOut = 0;
            HexAnswer found = GroupedByIndex(index, file => InGlobs(only, file) && receivers.Mentions(file),
                (file, at) =>
                {
                    int column = receivers.FirstOf(pattern, file, at, asked, out bool dropped);
                    if (dropped)
                        leftOut++;
                    return column;
                },
                asked, asked, 0, most, budget, "hex_refs");
            return found with
            {
                Text = $"{container}.{asked} by text, approximate: declarations inside {container}, and uses on {container} itself, on this or nothing in its own files, or on a name declared as a {container}. {leftOut} other lines with {asked} left out.\n" + found.Text
            };
        });

    // Whether a file is one of those the globs say, any file when there are none. A loop rather than Any, which made a
    // closure for every file of every search
    private static bool InGlobs(HexGlob[] only, HexIndexedFile file)
    {
        if (only.Length == 0)
            return true;
        foreach (HexGlob glob in only)
        {
            if (glob.Matches(file.Relative, false))
                return true;
        }

        return false;
    }

    // The column of the first match in a line, -1 for none. With a file the ones in comments and texts are passed over.
    // Walked as spans so a line that has nothing costs no allocation, which nearly all of them are. A line without the
    // text the expression needs in it (when there is one) is passed over before the expression looks at it.
    private static int FirstMatch(Regex pattern, ReadOnlySpan<char> line, HexIndexedFile? outsideOf, string? needs, StringComparison how)
    {
        if (line.Length == 0 || (needs is not null && !line.Contains(needs, how)))
            return -1;

        try
        {
            foreach (ValueMatch match in pattern.EnumerateMatches(line))
            {
                if (outsideOf is null || !InCommentOrText(outsideOf, line, match.Index))
                    return match.Index;
            }
        }
        catch (RegexMatchTimeoutException)
        {
        }

        return -1;
    }

    // Whether a place is in a comment or a text, by the line alone, the way a language without // still has its own
    internal static bool InCommentOrText(HexIndexedFile file, string line, int column) =>
        InCommentOrText(file, line.AsSpan(), column);

    internal static bool InCommentOrText(HexIndexedFile file, ReadOnlySpan<char> line, int column)
    {
        ReadOnlySpan<char> before = line[..column].TrimStart();
        if ((before.Length > 0 && before[0] == '*') || before.StartsWith("/*", StringComparison.Ordinal))
            return true;
        if (file.Extension is ".py" or ".yaml" or ".yml" or ".sh" && line.IndexOf('#') is var hash and >= 0 && hash < column)
            return true;
        if (file.Extension is ".md" or ".markdown" or ".txt")
            return false;
        return HexSearch.InTextOrComment(line, column);
    }

    private static HashSet<int> DeclaredLines(HexIndexedFile file, string name)
    {
        var set = new HashSet<int>();
        if (name.Length == 0)
            return set;
        foreach (HexSymbol symbol in file.Symbols)
        {
            if (symbol.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                set.Add(symbol.Line);
        }

        return set;
    }

    /// <summary>
    /// The places found, ranked and grouped, and what grep would have said for them, which is what was saved. The plain
    /// way's cost counts every place, not just the ones that fit. Scans cold disk files as raw text spans without
    /// allocating a string[] per file.
    /// </summary>
    private static HexAnswer Grouped(HexIndexSnapshot index, Func<HexIndexedFile, bool> include, LineMatch find,
        string? prefilter, StringComparison prefilterHow, string name, string query, int context, int most, int budget, string tool)
    {
        var files = new List<(HexIndexedFile File, List<Place> Places, int Score)>();
        long plain = 0;
        int total = 0;
        foreach (HexIndexedFile file in index.Files)
        {
            if (!include(file))
                continue;

            List<Place>? places = null;
            HashSet<int>? declared = null;
            if (file.TryGetCachedLines(out string[] cached))
            {
                for (int i = 0; i < cached.Length; i++)
                {
                    int first = find(file, cached[i]);
                    if (first < 0)
                        continue;

                    declared ??= DeclaredLines(file, name);
                    (places ??= []).Add(new Place(i, first, declared.Contains(i)));
                    plain += file.Path.Length + 8 + cached[i].Length;
                }
            }
            else if (file.HasInMemoryLines)
            {
                string[] lines = file.Lines;
                for (int i = 0; i < lines.Length; i++)
                {
                    int first = find(file, lines[i]);
                    if (first < 0)
                        continue;

                    declared ??= DeclaredLines(file, name);
                    (places ??= []).Add(new Place(i, first, declared.Contains(i)));
                    plain += file.Path.Length + 8 + lines[i].Length;
                }
            }
            else
            {
                string? raw = HexSearch.ReadText(file.Path);
                if (raw is null || (prefilter is not null && !raw.AsSpan().Contains(prefilter, prefilterHow)))
                    continue;

                int line = 0, start = 0;
                while (true)
                {
                    int newline = raw.IndexOf('\n', start);
                    int end = newline < 0 ? raw.Length : newline;
                    int content = newline >= 0 && end > start && raw[end - 1] == '\r' ? end - 1 : end;
                    ReadOnlySpan<char> span = raw.AsSpan(start, content - start);
                    int first = find(file, span);
                    if (first >= 0)
                    {
                        declared ??= DeclaredLines(file, name);
                        (places ??= []).Add(new Place(line, first, declared.Contains(line)));
                        plain += file.Path.Length + 8 + span.Length;
                    }

                    if (newline < 0)
                        break;
                    start = newline + 1;
                    line++;
                }
            }

            if (places is null)
                continue;

            total += places.Count;
            int score = (places.Any(place => place.Declares) ? 1000 : 0)
                + (query.Length > 0 && Path.GetFileNameWithoutExtension(file.Path.AsSpan()).Contains(query, StringComparison.OrdinalIgnoreCase) ? 300 : 0)
                + Math.Min(places.Count, 50);
            files.Add((file, places, score));
        }

        return FormatGrouped(files, total, plain, name, query, context, most, budget, tool);
    }

    private static HexAnswer GroupedByIndex(HexIndexSnapshot index, Func<HexIndexedFile, bool> include, Func<HexIndexedFile, int, int> find,
        string name, string query, int context, int most, int budget, string tool)
    {
        var files = new List<(HexIndexedFile File, List<Place> Places, int Score)>();
        long plain = 0;
        int total = 0;
        foreach (HexIndexedFile file in index.Files)
        {
            if (!include(file))
                continue;

            string[] lines = file.Lines;
            List<Place>? places = null;
            HashSet<int>? declared = null;
            for (int i = 0; i < lines.Length; i++)
            {
                int first = find(file, i);
                if (first < 0)
                    continue;

                declared ??= DeclaredLines(file, name);
                (places ??= []).Add(new Place(i, first, declared.Contains(i)));
                plain += file.Path.Length + 8 + lines[i].Length;
            }

            if (places is null)
                continue;

            total += places.Count;
            int score = (places.Any(place => place.Declares) ? 1000 : 0)
                + (query.Length > 0 && Path.GetFileNameWithoutExtension(file.Path.AsSpan()).Contains(query, StringComparison.OrdinalIgnoreCase) ? 300 : 0)
                + Math.Min(places.Count, 50);
            files.Add((file, places, score));
        }

        return FormatGrouped(files, total, plain, name, query, context, most, budget, tool);
    }

    private static HexAnswer FormatGrouped(List<(HexIndexedFile File, List<Place> Places, int Score)> files, int total, long plain,
        string name, string query, int context, int most, int budget, string tool)
    {
        files.Sort((a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : string.Compare(a.File.Relative, b.File.Relative, StringComparison.OrdinalIgnoreCase));

        var said = new HexBudget(budget);
        if (total == 0)
        {
            said.Line(tool == "hex_refs" ? $"{name} isn't used anywhere outside comments and strings." : $"Nothing has {query}.");
            return new HexAnswer(said.Finish(string.Empty), plain);
        }

        said.Line($"{total} places in {files.Count} files, best first, * declares it.");
        int shown = 0;
        foreach (var (file, places, _) in files)
        {
            if (shown >= most)
                break;

            said.Line($"{file.Relative} ({places.Count})");
            string[] lines = file.Lines;
            // The lines written so far, a place already written as a line around another isn't again. Counted as a set:
            // the declarations go first, so a use further up the file than one came after it and was taken for written
            var written = new HashSet<int>();
            foreach (Place place in places.OrderByDescending(place => place.Declares).ThenBy(place => place.Line))
            {
                if (shown++ >= most)
                    break;

                if (written.Contains(place.Line))
                    continue;

                if (context > 0)
                {
                    for (int around = Math.Max(0, place.Line - context); around < place.Line; around++)
                    {
                        if (written.Add(around))
                            said.Line($"  {around + 1}- {HexBudget.Cut(lines[around], 0, LINE_WIDTH)}");
                    }
                }

                said.Line($"  {place.Line + 1}{(place.Declares ? "*" : string.Empty)}: {HexBudget.Cut(lines[place.Line], place.Column, LINE_WIDTH)}");
                written.Add(place.Line);
                if (context > 0)
                {
                    for (int around = place.Line + 1; around <= Math.Min(lines.Length - 1, place.Line + context); around++)
                    {
                        if (written.Add(around))
                            said.Line($"  {around + 1}- {HexBudget.Cut(lines[around], 0, LINE_WIDTH)}");
                    }
                }
            }
        }

        if (total > most)
            said.Line($"... {total - most} more places, raise most, narrow with glob, or hex_search with something longer.");
        return new HexAnswer(said.Finish("ask with a bigger max_chars or narrow it with glob"), plain);
    }
}
