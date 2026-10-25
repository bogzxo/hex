using System.Text.RegularExpressions;

using Horizon.Content;
using Horizon.HIDL.Lexing;
using Horizon.HIDL.Parsing;
using Horizon.UI.Components;

namespace Hex.Core;

/// <summary>A place in a file something was found or is wrong, its line and column from 0, and what is said about it.</summary>
/// <param name="Found">How long what was found is, 0 for a result that isn't a find.</param>
/// <param name="FoundAt">Where it starts in the line without its indent, the way a row shows the line.</param>
internal readonly record struct HexResult(string Path, int Line, int Column, string Text, int Found = 0, int FoundAt = -1, bool Warning = false);

/// <summary>
/// The files of a workspace and what's in them: every .hor under some folders, where one is inside of them, every line
/// something is on, and every file checked for what doesn't parse. Nothing of the editor is read, so all of it goes on
/// in the background, and is tested on its own.
/// </summary>
internal static class HexSearch
{
    /// <summary>The folders never looked in for files of a workspace, what gets built, git's own and old builds kept on purpose.</summary>
    public static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", ".idea", "node_modules", "Build History" };

    /// <summary>The most lines finding something lists, a search for "e" in a big repository would go on for ever.</summary>
    public const int MOST_FOUND = 2000;

    /// <summary>Every .hor under some folders, once each.</summary>
    public static List<string> FilesUnder(IReadOnlyList<string> roots) => Under(roots, "*.hor");

    /// <summary>
    /// Every file under some folders that reads as text by its name, once each, what quick open lists and Find in Files
    /// looks through. A folder of D or C# has no .hor in it, and quick open said there was nothing to open.
    /// </summary>
    public static List<string> TextFilesUnder(IReadOnlyList<string> roots) => Under(roots, "*", UIFileKinds.IsText);

    /// <summary>The files quick open lists, every one that reads as text and whatever else the editor opens (pictures).</summary>
    public static List<string> OpenableFilesUnder(IReadOnlyList<string> roots, Func<string, bool> alsoOpens) =>
        Under(roots, "*", path => UIFileKinds.IsText(path) || alsoOpens(path));

    private static List<string> Under(IReadOnlyList<string> roots, string pattern, Func<string, bool>? keep = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        foreach (string root in roots)
        {
            foreach (string file in FilesIn(root, pattern, keep))
            {
                if (seen.Add(file))
                    files.Add(file);
            }
        }

        return files;
    }

    /// <summary>
    /// Every .hor under a folder, leaving out what gets built, git's own, old builds and the checkouts of submodules
    /// (which are the files of another repository, and there are copies of the engine in more than one).
    /// </summary>
    public static List<string> FilesIn(string root, string pattern = "*.hor", Func<string, bool>? keep = null)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string folder = pending.Pop();
            try
            {
                files.AddRange(Directory.GetFiles(folder, pattern).Where(file => !Path.GetFileName(file).StartsWith("~syncthing~") && (keep is null || keep(file))));

                foreach (string inside in Directory.GetDirectories(folder))
                {
                    string name = Path.GetFileName(inside);
                    if (SkippedFolders.Contains(name) || name.StartsWith('.') || File.Exists(Path.Combine(inside, ".git")))
                        continue;
                    pending.Push(inside);
                }
            }
            catch (Exception)
            {
                // A folder that can't be read has no files as far as this goes
            }
        }

        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    /// <summary>A path as it is inside of the folders looked through, with the name of its folder in front when there is more than one.</summary>
    public static string RelativeTo(IReadOnlyList<string> roots, string path)
    {
        string full = Path.GetFullPath(path);
        string? root = roots.Where(folder => full.StartsWith(folder, StringComparison.OrdinalIgnoreCase)).OrderByDescending(folder => folder.Length).FirstOrDefault();
        if (root is null)
            return full.Replace('\\', '/');

        string inside = Path.GetRelativePath(root, full).Replace('\\', '/');
        return roots.Count > 1 ? $"{Path.GetFileName(root.TrimEnd('\\', '/'))}/{inside}" : inside;
    }

    /// <summary>
    /// Every line something is on in the files under some folders, the first place on each, the case, whole words and a
    /// regex as the options say. A regex that doesn't build finds nothing.
    /// </summary>
    public static List<HexResult> FindIn(IReadOnlyList<string> roots, string query, UIFindOptions options)
    {
        var found = new List<HexResult>();
        Regex? pattern = null;
        if (options.Regex)
        {
            try { pattern = new Regex(query, (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
            catch (ArgumentException) { return found; }
        }

        foreach (string file in TextFilesUnder(roots))
        {
            string[] lines = ReadLines(file);
            for (int line = 0; line < lines.Length && found.Count < MOST_FOUND; line++)
            {
                if (FirstMatch(lines[line], query, options, pattern) is { } at)
                {
                    // How long what was found is, for the row to light it, the query's length or the regex's match
                    int length = pattern is null ? query.Length : Math.Max(1, pattern.Match(lines[line], at).Length);
                    int lead = lines[line].Length - lines[line].TrimStart().Length;
                    found.Add(new HexResult(file, line, at, $"{RelativeTo(roots, file)}:{line + 1}  {lines[line].Trim()}", length, at - lead));
                }
            }
        }

        return found;
    }

    /// <summary>Where something is first on a line as the options say (the pattern built from them for a regex), null for nowhere.</summary>
    public static int? FirstMatch(string line, string query, UIFindOptions options, Regex? pattern)
    {
        if (pattern is not null)
        {
            try
            {
                for (Match match = pattern.Match(line); match.Success; match = match.NextMatch())
                {
                    if (match.Length > 0 && (!options.WholeWord || IsWholeWord(line, match.Index, match.Length)))
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
            if (!options.WholeWord || IsWholeWord(line, at, query.Length))
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

    private static bool IsWholeWord(string line, int at, int length) =>
        (at == 0 || !UITextDocument.IsWordChar(line[at - 1])) && (at + length >= line.Length || !UITextDocument.IsWordChar(line[at + length]));

    /// <summary>Every .hor under some folders parsed, what doesn't parse, and what names a text the game hasn't got.</summary>
    public static (int Files, List<HexResult> Problems) CheckFiles(IReadOnlyList<string> roots)
    {
        List<string> files = FilesUnder(roots);
        var problems = new List<HexResult>();
        string Relative(string path) => RelativeTo(roots, path);

        foreach (string file in files)
        {
            string source;
            try
            {
                source = File.ReadAllText(file);
            }
            catch (Exception e)
            {
                problems.Add(new HexResult(file, 0, 0, $"{Relative(file)}  couldn't be read, {e.Message}"));
                continue;
            }

            try
            {
                new Parser().ProduceSyntaxTree(Lexer.Tokenize(source));
            }
            catch (ParseException e)
            {
                problems.Add(new HexResult(file, Math.Max(0, e.Line - 1), Math.Max(0, e.Col - 1), $"{Relative(file)}:{e.Line}  {e.Message}"));
            }
            catch (Exception e)
            {
                problems.Add(new HexResult(file, 0, 0, $"{Relative(file)}  {e.Message}"));
            }

            foreach (UICodeDiagnostic missing in LocalisationDiagnostics(source))
                problems.Add(new HexResult(file, missing.Line, missing.Column, $"{Relative(file)}:{missing.Line + 1}  {missing.Message}"));
        }

        return (files.Count, problems);
    }

    /// <summary>
    /// The texts a piece of code names by key ("@main_menu.quit") that the language of its game hasn't got, with no
    /// languages loaded there is nothing to say.
    /// </summary>
    public static IReadOnlyList<UICodeDiagnostic> LocalisationDiagnostics(string text)
    {
        if (Localisation.Count == 0 || !text.Contains("\"@", StringComparison.Ordinal))
            return [];

        var found = new List<UICodeDiagnostic>();
        foreach (Token token in Lexer.Tokenize(text, lenient: true))
        {
            if (token.Type != TokenType.TextLiteral || !Localisation.IsKey(token.Value))
                continue;

            if (!Localisation.Has(token.Value[1..]))
                found.Add(new UICodeDiagnostic(token.Line - 1, token.Col - 1, token.Length, $"{Localisation.Language} has no text for {token.Value[1..]}", Warning: true));
        }

        return found;
    }

    // The biggest file looked through for what's found, a log of a few hundred megabytes isn't code
    private const long MOST_READ = 4L * 1024 * 1024;

    /// <summary>
    /// Whether a place on a line of C-like code is inside a text ("", ``, '') or a comment (// to the end, /* */ on the
    /// line). A name renamed by the word, and the references a server finds by name, went into strings that said it.
    /// What starts on a line before isn't known of here.
    /// </summary>
    public static bool InTextOrComment(string line, int column)
    {
        char quote = '\0';
        for (int i = 0; i < column && i < line.Length; i++)
        {
            char at = line[i];
            if (quote != '\0')
            {
                if (at == '\\' && quote != '`')
                    i++;
                else if (at == quote)
                    quote = '\0';
                continue;
            }

            if (at is '"' or '`' or '\'')
            {
                quote = at;
            }
            else if (at == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                return true;
            }
            else if (at == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                int end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0 || end + 2 > column)
                    return true;
                i = end + 1;
            }
        }

        return quote != '\0';
    }

    /// <summary>The lines of a file, none for one that can't be read, is huge, or has nothings (zero bytes) in it and isn't text after all.</summary>
    public static string[] ReadLines(string file)
    {
        try
        {
            if (new FileInfo(file).Length > MOST_READ)
                return [];

            string text = File.ReadAllText(file);
            return text.Contains('\0') ? [] : text.Replace("\r\n", "\n").Split('\n');
        }
        catch (Exception)
        {
            return [];
        }
    }
}
