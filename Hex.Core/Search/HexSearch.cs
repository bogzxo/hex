using System.IO.Enumeration;
using Horizon.Content;
using Horizon.HIDL.Lexing;
using Horizon.HIDL.Parsing;
using Horizon.UI.Components;

namespace Hex.Core;

/// <summary>A place in a file something was found or is wrong, its line and column from 0, and what is said about it.</summary>
/// <param name="Found">How long what was found is, 0 for a result that isn't a find.</param>
/// <param name="FoundAt">Where it starts in the line without its indent, the way a row shows the line.</param>
internal readonly record struct HexResult(string Path, int Line, int Column, string Text, int Found = 0, int FoundAt = -1, bool Warning = false);

/// <summary>A file a walk found, with its length and the time it was last written (UTC ticks) as its folder's listing said them.</summary>
internal readonly record struct HexWalkedFile(string Path, long Length, long Written);

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
        // A submodule is walked unless one of the workspace's folders goes by its name, a copy of something that's open
        var open = new HashSet<string>(roots.Select(root => Path.GetFileName(Path.GetFullPath(root).TrimEnd('\\', '/'))), StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            foreach (string file in FilesIn(root, pattern, keep, excludes, open))
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
    /// dot folder that's none of those is looked in, .github/workflows has files people open. Given the names of the
    /// workspace's folders, a submodule is left out only when it goes by one of them, the copy of something open, and
    /// one of its own (an app's extensions) walked like the rest.
    /// </summary>
    public static List<string> FilesIn(string root, string pattern = "*.hor", Func<string, bool>? keep = null, HexExcludes? excludes = null, HashSet<string>? openNames = null)
    {
        List<HexWalkedFile> found = Walk(root, pattern, keep, excludes ?? Excluded(), openNames);
        var files = new List<string>(found.Count);
        foreach (HexWalkedFile file in found)
            files.Add(file.Path);
        return files;
    }

    /// <summary>
    /// Every file under some folders that reads as text by its name, once each, with its length and the time it was
    /// written as the walk saw them, what the agent harness's index looks at every few seconds. The folder's own listing
    /// says both, so no file is asked about on its own: that was a call to the file system for every file of the workspace.
    /// </summary>
    public static List<HexWalkedFile> TextFilesWithFactsUnder(IReadOnlyList<string> roots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<HexWalkedFile>();
        HexExcludes excludes = Excluded();
        var open = new HashSet<string>(roots.Select(root => Path.GetFileName(Path.GetFullPath(root).TrimEnd('\\', '/'))), StringComparer.OrdinalIgnoreCase);
        foreach (string root in roots)
        {
            foreach (HexWalkedFile file in Walk(root, "*", UIFileKinds.IsText, excludes, open))
            {
                if (seen.Add(file.Path))
                    files.Add(file);
            }
        }

        return files;
    }

    // What a folder's listing is read with, the way Directory.GetFiles reads it: nothing skipped for its attributes, a
    // folder that can't be read an error, and a pattern matched as Windows matches it
    private static readonly EnumerationOptions Listing = new() { MatchType = MatchType.Win32, AttributesToSkip = 0, IgnoreInaccessible = false };

    // FilesIn's walk, each file with its length and time. A folder is listed once for its files and its folders together,
    // where it was listed twice and asked whether it had a .gitignore besides
    private static List<HexWalkedFile> Walk(string root, string pattern, Func<string, bool>? keep, HexExcludes leftOut, HashSet<string>? openNames)
    {
        var files = new List<HexWalkedFile>();
        string? expression = pattern == "*" ? null : FileSystemName.TranslateWin32Expression(pattern);
        var entries = new List<(string Path, bool Folder, long Length, long Written)>();

        // Each folder with where it is inside the root and the .gitignores from the root down to it, shared until a
        // folder has one of its own
        var pending = new Stack<(string Folder, string Relative, HexIgnoreFile[] Ignores)>();
        pending.Push((root, string.Empty, []));

        while (pending.Count > 0)
        {
            var (folder, relative, ignores) = pending.Pop();
            try
            {
                entries.Clear();
                bool hasIgnoreFile = false;
                foreach (var entry in new FileSystemEnumerable<(string, bool, long, long)>(folder,
                    static (ref FileSystemEntry entry) => (entry.ToSpecifiedFullPath(), entry.IsDirectory, entry.Length, entry.LastWriteTimeUtc.UtcTicks), Listing))
                {
                    entries.Add(entry);
                    if (!entry.Item2 && Path.GetFileName(entry.Item1.AsSpan()).Equals(HexIgnoreFile.FILE, PlatformPaths.Comparison))
                        hasIgnoreFile = true;
                }

                if (leftOut.UseIgnoreFiles && hasIgnoreFile && HexIgnoreFile.In(folder, relative) is { } own)
                    ignores = [.. ignores, own];

                string Inside(string name) => relative.Length == 0 ? name : $"{relative}/{name}";
                bool Out(string inside, bool isFolder) => leftOut.Skipped(inside, isFolder) || (ignores.Length > 0 && HexIgnoreFile.Ignored(ignores, inside, isFolder));

                foreach (var (file, isFolder, length, written) in entries)
                {
                    if (isFolder)
                        continue;
                    ReadOnlySpan<char> name = Path.GetFileName(file.AsSpan());
                    if ((expression is null || FileSystemName.MatchesWin32Expression(expression, name, ignoreCase: true))
                        && !name.StartsWith("~syncthing~", StringComparison.Ordinal)
                        && (keep is null || keep(file))
                        && !Out(relative.Length == 0 ? name.ToString() : string.Concat(relative, "/", name), false))
                        files.Add(new HexWalkedFile(file, length, written));
                }

                foreach (var (inside, isFolder, _, _) in entries)
                {
                    if (!isFolder)
                        continue;
                    string name = Path.GetFileName(inside);
                    string under = Inside(name);

                    // A repository of its own inside the folder (the engine and Hex cloned into the workspace the game's
                    // repository is) is out of reach of the .gitignores above it, the way git and ripgrep take it: they
                    // belong to another repository. A workspace whose .gitignore leaves out the repositories cloned into it
                    // had them walked as if they weren't there, a few files of the top found and nothing of the code.
                    // A folder the settings leave out is left out whatever its .git is, which isn't asked about. Its .git is
                    // asked about once otherwise, whether it's a folder, a file or not there
                    if (leftOut.Skipped(under, true))
                        continue;
                    FileAttributes git = new FileInfo(Path.Combine(inside, ".git")).Attributes;
                    bool repository = (int)git != -1 && git.HasFlag(FileAttributes.Directory);
                    bool gitFile = (int)git != -1 && !repository;
                    if ((!repository && ignores.Length > 0 && HexIgnoreFile.Ignored(ignores, under, true)) || IsRepositoryCopy(inside, repository, gitFile)
                        || (gitFile && (openNames is null || openNames.Contains(name))))
                        continue;
                    pending.Push((inside, under, repository ? [] : ignores));
                }
            }
            catch (Exception)
            {
                // A folder that can't be read has no files as far as this goes
            }
        }

        files.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        return files;
    }

    /// <summary>
    /// Whether a folder inside the workspace is a copy of a repository rather than its own files: a git worktree (its
    /// .git a file pointing into another clone's .git/worktrees, hexwt-* beside the clones) or a dot folder with a .git
    /// of its own (.hexbase, a snapshot kept of a clone). Their files are the same names as the real ones, and hex_symbols
    /// said each declaration three times and hex_rename picked one no server had loaded. A submodule (.git a file pointing
    /// into .git/modules) is walked as it was. Never asked of a workspace's own folder, which can be a worktree.
    /// </summary>
    internal static bool IsRepositoryCopy(string folder)
    {
        string git = Path.Combine(folder, ".git");
        return IsRepositoryCopy(folder, Directory.Exists(git), File.Exists(git));
    }

    // The same with what its .git is already known, a folder, a file or neither
    private static bool IsRepositoryCopy(string folder, bool gitFolder, bool gitFile)
    {
        try
        {
            if (Path.GetFileName(folder.AsSpan()) is ['.', ..] && (gitFolder || gitFile))
                return true;
            if (!gitFile)
                return false;
            using var reader = new StreamReader(Path.Combine(folder, ".git"));
            string? first = reader.ReadLine();
            return first is not null && first.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase)
                && first.Replace('\\', '/').Contains("/worktrees/", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A path as it is inside of the folders looked through, with the name of its folder in front when there is more than one.</summary>
    public static string RelativeTo(IReadOnlyList<string> roots, string path)
    {
        string full = Path.GetFullPath(path);
        string? root = null;
        for (int i = 0; i < roots.Count; i++)
        {
            string folder = roots[i];
            if (full.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && (root is null || folder.Length > root.Length))
                root = folder;
        }
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
    public static bool InTextOrComment(string line, int column) => InTextOrComment(line.AsSpan(), column);

    public static bool InTextOrComment(ReadOnlySpan<char> line, int column)
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
                int rel = line[(i + 2)..].IndexOf("*/", StringComparison.Ordinal);
                if (rel < 0 || i + 2 + rel + 2 > column)
                    return true;
                i = i + 2 + rel + 1;
            }
        }

        return quote != '\0';
    }

    /// <summary>The lines of a file, none for one that can't be read, is huge, or has nothings (zero bytes) in it and isn't text after all.</summary>
    public static string[] ReadLines(string file) => ReadText(file) is { } text ? SplitLines(text) : [];

    /// <summary>The text of a file as it is on the disk, null for one that can't be read, is huge, or has nothings in it and isn't text after all.</summary>
    internal static string? ReadText(string file)
    {
        try
        {
            if (new FileInfo(file).Length > MOST_READ)
                return null;

            string text = File.ReadAllText(file);
            return text.Contains('\0') ? null : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The lines of a text, split at each \n with a \r before it taken off too, what replacing \r\n with \n and
    /// splitting comes to without the whole text made again in between.
    /// </summary>
    internal static string[] SplitLines(string text)
    {
        var lines = new string[text.AsSpan().Count('\n') + 1];
        int start = 0, at = 0;
        for (int end; (end = text.IndexOf('\n', start)) >= 0; start = end + 1)
            lines[at++] = text[start..(end > start && text[end - 1] == '\r' ? end - 1 : end)];
        lines[at] = text[start..];
        return lines;
    }
}
