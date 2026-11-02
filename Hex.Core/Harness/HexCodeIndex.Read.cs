using System.Text;

namespace Hex.Core;

// One declaration's lines out of a file, a method out of a big partial instead of the whole of it, which is where most of
// what an agent reads goes. Found by its name in the file (or anywhere when no file is said), from the comments and
// attributes above it to its end. The end is where its braces close for the languages that have them, where the indent
// comes back for Python and YAML, and the next heading as high for Markdown. A range of lines does without a name.
internal sealed partial class HexCodeIndex
{
    // The most lines a read hands back, past that it's cut and says how to go on
    private const int MOST_LINES = 400;

    /// <summary>hex_read, a symbol's lines (or a range of them) with their numbers in front.</summary>
    public HexAnswer Read(string? path, string? symbol, int? start, int? end, bool numbers, int budget) =>
        Ask("hex_read", $"{path}\n{symbol}\n{start}\n{end}\n{numbers}\n{budget}", index =>
        {
            HexIndexedFile? file = path is { Length: > 0 } ? FileOf(index, path) : null;
            if (path is { Length: > 0 } && file is null)
                return HexAnswer.Wrong(NoFile(index, path));

            string? also = null;
            int first, last;
            HexSymbol? found = null;
            if (symbol is { Length: > 0 })
            {
                List<(HexIndexedFile File, HexSymbol Symbol, int Score)> fits = file is null
                    ? FindSymbols(index, symbol)
                    : [.. FindSymbols(index, symbol).Where(each => ReferenceEquals(each.File, file))];
                fits = [.. fits.Where(each => each.Score >= 90)];
                if (fits.Count == 0)
                    return HexAnswer.Wrong($"Nothing called {symbol} is declared{(file is null ? string.Empty : $" in {file.Relative}")}. hex_symbols finds names by part of them.");

                (file, found) = (fits[0].File, fits[0].Symbol);
                if (fits.Count > 1)
                    also = $"{fits.Count - 1} more declare {symbol}, {string.Join(", ", fits.Skip(1).Take(5).Select(each => $"{each.File.Relative}:{each.Symbol.Line + 1}"))}{(fits.Count > 6 ? " ..." : string.Empty)}, say path to pick one.";
                (first, last) = Extent(file, found);
            }
            else if (file is not null)
            {
                first = Math.Max(1, start ?? 1) - 1;
                last = Math.Min(file.Lines.Length, end ?? first + MOST_LINES) - 1;
            }
            else
            {
                return HexAnswer.Wrong("Say a path, a symbol, or both.");
            }

            string[] lines = file.Lines;
            if (lines.Length == 0)
                return HexAnswer.Wrong($"{file.Relative} has no text Hex reads (it's empty, too big, or not text).");
            first = Math.Clamp(first, 0, lines.Length - 1);
            last = Math.Clamp(last, first, lines.Length - 1);

            var said = new HexBudget(budget);
            said.Line($"{file.Relative}:{first + 1}-{last + 1}{(found is null ? string.Empty : $" {found.Kind} {found.Full}")} of {lines.Length} lines");
            if (also is not null)
                said.Line(also);

            int width = (last + 1).ToString().Length;
            int stop = Math.Min(last, first + MOST_LINES - 1);
            for (int i = first; i <= stop; i++)
            {
                if (!said.Line(numbers ? $"{(i + 1).ToString().PadLeft(width)}  {lines[i]}" : lines[i]))
                {
                    stop = i - 1;
                    break;
                }
            }

            if (stop < last)
                said.Line($"... cut at line {stop + 1}, the rest with start_line {stop + 2} and end_line {last + 1}.");

            // The plain way reads the whole file, Read's own numbers in front of every line
            return new HexAnswer(said.Finish($"read on with start_line {stop + 2}"), file.Length + 7L * lines.Length);
        });

    /// <summary>The lines a symbol takes up, from 0, the comments and attributes above it among them.</summary>
    internal static (int First, int Last) Extent(HexIndexedFile file, HexSymbol symbol)
    {
        string[] lines = file.Lines;
        int at = Math.Clamp(symbol.Line, 0, Math.Max(0, lines.Length - 1));
        if (lines.Length == 0)
            return (0, 0);

        int first = at;
        while (first > 0 && IsPreamble(lines[first - 1].TrimStart(), file.Extension))
            first--;

        int last = file.Extension switch
        {
            ".py" or ".yaml" or ".yml" => ByIndent(lines, at),
            ".md" or ".markdown" => ByHeading(lines, at),
            _ => ByBraces(lines, at)
        };

        return (first, last);
    }

    // A line that goes with the declaration under it, its doc comment, an attribute or a decorator
    private static bool IsPreamble(string line, string extension) => line.Length > 0 && extension switch
    {
        ".py" => line.StartsWith('@') || line.StartsWith('#'),
        ".md" or ".markdown" or ".yaml" or ".yml" or ".json" => false,
        _ => line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("/*", StringComparison.Ordinal) || line.StartsWith('*') || line.StartsWith('[') || line.StartsWith('@')
    };

    // Where the braces opened on or after a line close again, or the ; that ends it first when it has none (a field,
    // an expression bodied member). Strings and comments are stepped over so a "{" in a text doesn't count
    private static int ByBraces(string[] lines, int at)
    {
        int depth = 0;
        bool opened = false, blockComment = false;
        for (int i = at; i < lines.Length; i++)
        {
            string line = lines[i];
            char quote = '\0';
            for (int c = 0; c < line.Length; c++)
            {
                char ch = line[c];
                if (blockComment)
                {
                    if (ch == '*' && c + 1 < line.Length && line[c + 1] == '/')
                    {
                        blockComment = false;
                        c++;
                    }
                    continue;
                }

                if (quote != '\0')
                {
                    if (ch == '\\')
                        c++;
                    else if (ch == quote)
                        quote = '\0';
                    continue;
                }

                switch (ch)
                {
                    case '"' or '\'' or '`':
                        quote = ch;
                        break;
                    case '/' when c + 1 < line.Length && line[c + 1] == '/':
                        c = line.Length;
                        break;
                    case '/' when c + 1 < line.Length && line[c + 1] == '*':
                        blockComment = true;
                        c++;
                        break;
                    case '{':
                        depth++;
                        opened = true;
                        break;
                    case '}':
                        depth--;
                        if (opened && depth <= 0)
                            return i;
                        break;
                    case ';' when !opened && depth == 0:
                        return i;
                }
            }

            // A declaration that never opens a brace in a dozen lines isn't going to, it's one line of something
            if (!opened && i - at > 12)
                return at;
        }

        return lines.Length - 1;
    }

    // Where the lines come back to the indent of the one a declaration's on, blank lines not counting
    private static int ByIndent(string[] lines, int at)
    {
        int indent = IndentOf(lines[at]);
        int last = at;
        for (int i = at + 1; i < lines.Length; i++)
        {
            if (lines[i].Trim().Length == 0)
                continue;
            if (IndentOf(lines[i]) <= indent)
                break;
            last = i;
        }

        return last;
    }

    private static int IndentOf(string line)
    {
        int columns = 0;
        foreach (char c in line)
        {
            if (c == ' ') columns++;
            else if (c == '\t') columns += 4;
            else break;
        }

        return columns;
    }

    // The line before the next heading as high as this one or higher
    private static int ByHeading(string[] lines, int at)
    {
        int level = lines[at].TakeWhile(c => c == '#').Count();
        bool fenced = false;
        for (int i = at + 1; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                fenced = !fenced;
            else if (!fenced && level > 0 && lines[i].StartsWith('#') && lines[i].TakeWhile(c => c == '#').Count() <= level)
                return i - 1;
        }

        return lines.Length - 1;
    }

    /// <summary>A file's first few lines of what it's about, its summary or the comment it starts with, null for none.</summary>
    internal static string? SummaryOf(HexIndexedFile file)
    {
        string[] lines = file.Lines;
        var about = new StringBuilder();
        bool inSummary = false;
        for (int i = 0; i < Math.Min(lines.Length, 80) && about.Length < 300; i++)
        {
            string line = lines[i].Trim();
            if (file.Extension is ".md" or ".markdown")
            {
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("```", StringComparison.Ordinal))
                {
                    if (about.Length > 0) break;
                    continue;
                }

                about.Append(line).Append(' ');
                continue;
            }

            if (line.StartsWith("using ", StringComparison.Ordinal) || line.StartsWith("namespace", StringComparison.Ordinal) || line.StartsWith("import ", StringComparison.Ordinal) || line.Length == 0)
            {
                if (about.Length > 0 && !inSummary) break;
                continue;
            }

            if (line.StartsWith("/// <summary>", StringComparison.Ordinal)) { inSummary = true; continue; }
            if (line.StartsWith("/// </summary>", StringComparison.Ordinal)) break;
            if (line.StartsWith("///", StringComparison.Ordinal) || line.StartsWith("//", StringComparison.Ordinal) || (file.Extension == ".py" && line.StartsWith('#')))
            {
                about.Append(line.TrimStart('/', '#', ' ')).Append(' ');
                continue;
            }

            if (line.StartsWith("\"\"\"", StringComparison.Ordinal))
            {
                about.Append(line.Trim('"', ' ')).Append(' ');
                continue;
            }

            break;
        }

        string said = about.ToString().Trim();
        return said.Length == 0 ? null : said.Length > 300 ? said[..297] + "..." : said;
    }
}
