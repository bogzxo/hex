namespace Hex.Core;

// What files are without reading them. Skimming says a file's size, what it says it's about and its outline with lines,
// several files a call. The map is the workspace as a tree a few folders down with the languages of each and the types
// declared in it, for an agent that's just arrived. Files finds a file by part of its name or path, ranked the way
// quick open ranks them.
internal sealed partial class HexCodeIndex
{
    // The most outline lines a file gets in a skim, a huge file's outline is a file of its own
    private const int MOST_OUTLINE = 120;

    /// <summary>hex_skim, the files a comma separated list says (or a glob), each its size, summary and outline.</summary>
    public HexAnswer Skim(string paths, int depth, int budget) =>
        Ask("hex_skim", $"{paths}\n{depth}\n{budget}", index =>
        {
            var files = new List<HexIndexedFile>();
            var missing = new List<string>();
            foreach (string asked in paths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (asked.Contains('*') || asked.Contains('?'))
                {
                    HexGlob[] globs = HexGlob.All([asked]);
                    files.AddRange(index.Files.Where(file => globs.Any(glob => glob.Matches(file.Relative, false))));
                }
                else if (FileOf(index, asked) is { } file)
                {
                    files.Add(file);
                }
                else
                {
                    missing.Add(NoFile(index, asked));
                }
            }

            if (files.Count == 0)
                return HexAnswer.Wrong(missing.Count > 0 ? string.Join("\n", missing) : "Say some files in paths, a comma between them, or a glob.");

            var said = new HexBudget(budget);
            foreach (string why in missing)
                said.Line(why);

            long plain = 0;
            foreach (HexIndexedFile file in files.Distinct())
            {
                plain += file.Length + 7L * file.LineCount;
                said.Line($"{file.Relative}  {file.LineCount} lines, {Kilobytes(file.Length)}, {file.Language}{(file.FromServer ? ", symbols from its language server" : string.Empty)}");
                if (SummaryOf(file) is { } about)
                    said.Line($"  {about}");

                int shown = 0;
                foreach (HexSymbol symbol in file.Symbols)
                {
                    if (symbol.Depth > depth)
                        continue;
                    if (shown++ >= MOST_OUTLINE)
                    {
                        said.Line($"  ... {file.Symbols.Count - MOST_OUTLINE} more, hex_symbols with path narrows to this file.");
                        break;
                    }

                    said.Line($"  {new string(' ', Math.Min(symbol.Depth, 6) * 2)}{symbol.Line + 1} {symbol.Kind} {symbol.Name}");
                }
            }

            return new HexAnswer(said.Finish("skim fewer files at once or lower depth"), plain);
        });

    private static string Kilobytes(long length) => length < 1024 ? $"{length} bytes" : $"{length / 1024.0:0.#} KB";

    /// <summary>hex_map, the folders of the workspace (or under one) as a tree, with what's in each.</summary>
    public HexAnswer Map(string? under, int depth, int budget) =>
        Ask("hex_map", $"{under}\n{depth}\n{budget}", index =>
        {
            string prefix = (under ?? string.Empty).Replace('\\', '/').Trim('/');
            HexIndexedFile[] files = [.. index.Files.Where(file => prefix.Length == 0 || file.Relative.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))];
            if (files.Length == 0)
                return HexAnswer.Wrong(prefix.Length == 0 ? "The workspace has no files Hex indexes, is a folder open?" : $"Nothing is under {prefix}.");

            // Every folder down to the depth asked, what's under it all the way down and what's declared right in it
            var folders = new SortedDictionary<string, (List<HexIndexedFile> All, List<HexIndexedFile> Own)>(StringComparer.OrdinalIgnoreCase);
            foreach (HexIndexedFile file in files)
            {
                string inside = prefix.Length == 0 ? file.Relative : file.Relative[(prefix.Length + 1)..];
                string[] parts = inside.Split('/');
                for (int level = 0; level < parts.Length && level <= depth; level++)
                {
                    string folder = string.Join('/', parts.Take(level));
                    if (!folders.TryGetValue(folder, out var entry))
                        folders[folder] = entry = ([], []);
                    entry.All.Add(file);
                    if (level == parts.Length - 1 || level == depth)
                        entry.Own.Add(file);
                }
            }

            var said = new HexBudget(budget);
            foreach (var (folder, (all, own)) in folders)
            {
                int level = folder.Length == 0 ? 0 : folder.Count(c => c == '/') + 1;
                string name = folder.Length == 0 ? (prefix.Length == 0 ? "." : prefix) : folder[(folder.LastIndexOf('/') + 1)..];
                string languages = string.Join(", ", all.GroupBy(file => file.Extension.Length == 0 ? "other" : file.Extension)
                    .OrderByDescending(group => group.Count()).Take(4).Select(group => $"{group.Key} {group.Count()}"));

                // The types declared in the folder itself (or below it at the last level), the biggest files' first
                string[] types = [.. own.OrderByDescending(file => file.Length).SelectMany(file => file.Symbols.Where(symbol => symbol.IsType && symbol.Depth <= 1).Select(symbol => symbol.Name)).Distinct().Take(8)];
                int moreTypes = own.SelectMany(file => file.Symbols.Where(symbol => symbol.IsType && symbol.Depth <= 1).Select(symbol => symbol.Name)).Distinct().Count() - types.Length;
                said.Line($"{new string(' ', level * 2)}{name}/ {all.Count} files ({languages}){(types.Length > 0 ? "  " + string.Join(" ", types) + (moreTypes > 0 ? $" +{moreTypes}" : string.Empty) : string.Empty)}");
            }

            // The plain way is a find over it all, a path a line, and a look in each folder for what's there
            long plain = files.Sum(file => (long)file.Path.Length + 1) * 2;
            return new HexAnswer(said.Finish("lower depth or map a folder with under"), plain);
        });

    /// <summary>hex_files, files whose name or path has what was asked in it, best first, with their sizes.</summary>
    public HexAnswer Files(string query, int most, int budget) =>
        Ask("hex_files", $"{query}\n{most}\n{budget}", index =>
        {
            string[] words = query.Replace('\\', '/').Split([' ', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0)
                return HexAnswer.Wrong("Say part of a file's name or path in query.");

            var found = new List<(HexIndexedFile File, int Score)>();
            foreach (HexIndexedFile file in index.Files)
            {
                string name = Path.GetFileName(file.Path), bare = Path.GetFileNameWithoutExtension(file.Path);
                int score = 0;
                foreach (string word in words)
                {
                    int each = bare.Equals(word, StringComparison.OrdinalIgnoreCase) || name.Equals(word, StringComparison.OrdinalIgnoreCase) ? 100
                        : name.StartsWith(word, StringComparison.OrdinalIgnoreCase) ? 80
                        : name.Contains(word, StringComparison.OrdinalIgnoreCase) ? 60
                        : file.Relative.Contains(word, StringComparison.OrdinalIgnoreCase) ? 30
                        : Subsequence(name, word) ? 10 : 0;
                    if (each == 0)
                    {
                        score = 0;
                        break;
                    }

                    score += each;
                }

                if (score > 0)
                    found.Add((file, score));
            }

            found.Sort((a, b) => a.Score != b.Score ? b.Score.CompareTo(a.Score) : a.File.Relative.Length != b.File.Relative.Length
                ? a.File.Relative.Length.CompareTo(b.File.Relative.Length) : string.Compare(a.File.Relative, b.File.Relative, StringComparison.OrdinalIgnoreCase));

            var said = new HexBudget(budget);
            said.Line(found.Count == 0 ? $"No file's name or path has {query}." : $"{found.Count} files fit {query}, best first.");
            foreach (var (file, _) in found.Take(most))
                said.Line($"{file.Relative}  {file.LineCount} lines");
            if (found.Count > most)
                said.Line($"... {found.Count - most} more, say more of the name.");

            // The plain way is a find over the workspace for it
            long plain = index.Files.Sum(file => (long)file.Path.Length + 1) / 4 + found.Sum(each => (long)each.File.Path.Length + 1);
            return new HexAnswer(said.Finish("say more of the name"), plain);
        });

    // Whether the letters of a word are in a name in order, rd for Read
    private static bool Subsequence(string name, string word)
    {
        int at = 0;
        foreach (char c in name)
        {
            if (at < word.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(word[at]))
                at++;
        }

        return at == word.Length;
    }

    /// <summary>hex_changes, the files that came, changed or went since a generation.</summary>
    public HexAnswer Changes(long since, int budget)
    {
        var (list, complete) = ChangesSince(since);
        long now = Generation;
        var said = new HexBudget(budget);
        said.Line(list.Count == 0 ? $"Nothing changed since generation {since}." : $"{list.Count} files changed since generation {since}, + came, ~ changed, - went.");
        if (!complete)
            said.Line("The log doesn't go back that far, more may have changed than this says.");
        foreach (HexChange change in list)
            said.Line($"{change.What} {change.Relative}");
        said.Line($"(index generation {now})");
        string text = said.Finish("ask from a later generation");
        Stats.Count("hex_changes", text.Length, list.Sum(change => (long)change.Relative.Length + 40), kept: false);
        return new HexAnswer(text, 0);
    }
}
