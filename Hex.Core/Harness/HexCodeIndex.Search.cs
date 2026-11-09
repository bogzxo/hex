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
            return Grouped(index, file => only.Length == 0 || only.Any(each => each.Matches(file.Relative, false)), (file, line) => Matches(pattern, line),
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
            if (asked.Contains('.'))
                asked = asked[(asked.LastIndexOf('.') + 1)..];
            if (asked.Length == 0 || !asked.All(c => char.IsLetterOrDigit(c) || c == '_'))
                return HexAnswer.Wrong("hex_refs takes a name, letters, digits and underscores. hex_search finds anything else.");

            var pattern = new Regex($@"\b{Regex.Escape(asked)}\b", RegexOptions.CultureInvariant);
            HexGlob[] only = glob is { Length: > 0 } ? HexGlob.All(glob.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) : [];
            return Grouped(index, file => only.Length == 0 || only.Any(each => each.Matches(file.Relative, false)),
                (file, line) => Matches(pattern, line).Where(column => !InCommentOrText(file, line, column)),
                asked, asked, 0, most, budget, "hex_refs");
        });

    private static IEnumerable<int> Matches(Regex pattern, string line)
    {
        if (line.Length == 0)
            yield break;

        Match match;
        try
        {
            match = pattern.Match(line);
        }
        catch (RegexMatchTimeoutException)
        {
            yield break;
        }

        for (; match.Success; match = match.NextMatch())
            yield return match.Index;
    }

    // Whether a place is in a comment or a text, by the line alone, the way a language without // still has its own
    private static bool InCommentOrText(HexIndexedFile file, string line, int column)
    {
        string before = line[..column].TrimStart();
        if (before.StartsWith('*') || before.StartsWith("/*", StringComparison.Ordinal))
            return true;
        if (file.Extension is ".py" or ".yaml" or ".yml" or ".sh" && line.IndexOf('#') is var hash and >= 0 && hash < column)
            return true;
        if (file.Extension is ".md" or ".markdown" or ".txt")
            return false;
        return HexSearch.InTextOrComment(line, column);
    }

    /// <summary>
    /// The places found, ranked and grouped, and what grep would have said for them, which is what was saved. The plain
    /// way's cost counts every place, not just the ones that fit.
    /// </summary>
    private static HexAnswer Grouped(HexIndexSnapshot index, Func<HexIndexedFile, bool> include, Func<HexIndexedFile, string, IEnumerable<int>> find,
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
                int first = -1;
                foreach (int column in find(file, lines[i]))
                {
                    first = column;
                    break;
                }

                if (first < 0)
                    continue;

                declared ??= [.. file.Symbols.Where(symbol => name.Length > 0 && symbol.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(symbol => symbol.Line)];
                (places ??= []).Add(new Place(i, first, declared.Contains(i)));
                plain += file.Path.Length + 8 + lines[i].Length;
            }

            if (places is null)
                continue;

            total += places.Count;
            int score = (places.Any(place => place.Declares) ? 1000 : 0)
                + (query.Length > 0 && Path.GetFileNameWithoutExtension(file.Path).Contains(query, StringComparison.OrdinalIgnoreCase) ? 300 : 0)
                + Math.Min(places.Count, 50);
            files.Add((file, places, score));
        }

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
            int lastShown = -1;
            foreach (Place place in places.OrderByDescending(place => place.Declares).ThenBy(place => place.Line))
            {
                if (shown++ >= most)
                    break;

                // Already written as a line around the one before
                if (place.Line <= lastShown)
                    continue;

                if (context > 0)
                {
                    for (int around = Math.Max(Math.Max(0, place.Line - context), lastShown + 1); around < place.Line; around++)
                        said.Line($"  {around + 1}- {HexBudget.Cut(lines[around], 0, LINE_WIDTH)}");
                }

                said.Line($"  {place.Line + 1}{(place.Declares ? "*" : string.Empty)}: {HexBudget.Cut(lines[place.Line], place.Column, LINE_WIDTH)}");
                lastShown = place.Line;
                if (context > 0)
                {
                    for (int around = place.Line + 1; around <= Math.Min(lines.Length - 1, place.Line + context); around++)
                    {
                        said.Line($"  {around + 1}- {HexBudget.Cut(lines[around], 0, LINE_WIDTH)}");
                        lastShown = around;
                    }
                }
            }
        }

        if (total > most)
            said.Line($"... {total - most} more places, raise most, narrow with glob, or hex_search with something longer.");
        return new HexAnswer(said.Finish("ask with a bigger max_chars or narrow it with glob"), plain);
    }
}
