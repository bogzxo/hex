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
internal static partial class HexSearch
{
    /// <summary>
    /// The folders the look for repositories inside of the workspace never goes into (HexGit.Nested), what gets built,
    /// git's own and old builds kept on purpose. The files of a workspace go by <see cref="Excluded"/> instead.
    /// </summary>
    public static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", ".idea", "node_modules", "Build History" };

    /// <summary>
    /// What a walk of the files of a workspace leaves out, asked as each walk starts. Dawn has it go by its settings
    /// (files_exclude, search_exclude and search_use_ignore_files), anything else by what they come as.
    /// </summary>
    public static Func<HexExcludes> Excluded { get; set; } = static () => HexExcludes.Usual;

    /// <summary>The most matches finding something lists, a search for "e" in a big repository would go on for ever.</summary>
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
        HexExcludes excludes = Excluded();
        foreach (string root in roots)
        {
            foreach (string file in FilesIn(root, pattern, keep, excludes))
            {
                if (seen.Add(file))
                    files.Add(file);
            }
        }

        return files;
    }

    /// <summary>
    /// Every .hor under a folder, leaving out what files_exclude and search_exclude say (what gets built, git's own, old
    /// builds), what each .gitignore on the way down says while search_use_ignore_files is on, and the checkouts of
    /// submodules (which are the files of another repository, and there are copies of the engine in more than one). A
    /// dot folder that's none of those is looked in, .github/workflows has files people open.
    /// </summary>
    public static List<string> FilesIn(string root, string pattern = "*.hor", Func<string, bool>? keep = null, HexExcludes? excludes = null)
    {
        HexExcludes leftOut = excludes ?? Excluded();
        var files = new List<string>();

        // Each folder with where it is inside the root and the .gitignores from the root down to it, shared until a
        // folder has one of its own
        var pending = new Stack<(string Folder, string Relative, HexIgnoreFile[] Ignores)>();
        pending.Push((root, string.Empty, []));

        while (pending.Count > 0)
        {
            var (folder, relative, ignores) = pending.Pop();
            try
            {
                if (leftOut.UseIgnoreFiles && HexIgnoreFile.In(folder, relative) is { } own)
                    ignores = [.. ignores, own];

                string Inside(string name) => relative.Length == 0 ? name : $"{relative}/{name}";
                bool Out(string inside, bool isFolder) => leftOut.Skipped(inside, isFolder) || (ignores.Length > 0 && HexIgnoreFile.Ignored(ignores, inside, isFolder));

                foreach (string file in Directory.GetFiles(folder, pattern))
                {
                    string name = Path.GetFileName(file);
                    if (!name.StartsWith("~syncthing~") && !Out(Inside(name), false) && (keep is null || keep(file)))
                        files.Add(file);
                }

                foreach (string inside in Directory.GetDirectories(folder))
                {
                    string under = Inside(Path.GetFileName(inside));
                    if (Out(under, true) || File.Exists(Path.Combine(inside, ".git")))
                        continue;
                    pending.Push((inside, under, ignores));
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
