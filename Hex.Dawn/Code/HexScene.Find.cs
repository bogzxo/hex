using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

using Horizon.Content;
using Horizon.HIDL;
using Horizon.HIDL.Lexing;
using Horizon.HIDL.Parsing;
using Horizon.HIDL.Runtime;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Finding things. In the file (Ctrl+F, F3, Ctrl+H, Ctrl+G), and in every file of the workspace, opening one by a few
// letters of its name (Ctrl+P), every line something is on (Ctrl+Shift+F) and every file checked for what is wrong (F8).
internal sealed partial class HexScene
{
    /* Finding and going places */

    private UIDialog? textDialog;

    /// <summary>Helper method to ask for a line of text in a dialog and do something with it, enter or the first button.</summary>
    private void AskText(string title, string message, string start, string go, Action<string> answered, Action<TextBox>? extra = null)
    {
        if (textDialog is { IsOpen: true })
            textDialog.Close();

        TextBox box = null!;
        UIDialog shown = UIDialog.Show(chrome, title, message,
            new DialogChoice(go, () => answered(box.Text)),
            new DialogChoice("Cancel"));

        box = layout.Instantiate(ASK_TEMPLATE, shown.Body).Get<TextBox>("answer");
        box.Text = start;
        box.OnSubmitted = text =>
        {
            shown.Close();
            answered(text);
        };

        extra?.Invoke(box);
        box.Focus();
        box.SelectAll();
        textDialog = shown;
    }

    private static int CountIn(string text, string query)
    {
        int count = 0;
        for (int at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(query, at + query.Length, StringComparison.OrdinalIgnoreCase))
            count++;
        return count;
    }

    private void GoToLine()
    {
        AskText("Go to line", $"Which line, 1 to {code.Document.LineCount}", string.Empty, "Go", answer =>
        {
            if (int.TryParse(answer.Trim(), out int line))
            {
                code.GoTo(Math.Clamp(line - 1, 0, code.Document.LineCount - 1));
                code.Focus();
            }
        });
    }

    /* The workspace, every .hor of its folders, or of the repository the open file is in when it has none */

    /// <summary>The repository the document on screen is in (the nearest folder up from it with git in it), failing that the folder files are opened from.</summary>
    private string RepositoryRoot()
    {
        string? from = document.Path is { } path ? Path.GetDirectoryName(Path.GetFullPath(path)) : browseDirectory;
        for (string? at = from; at is not null; at = Path.GetDirectoryName(at))
        {
            if (Directory.Exists(Path.Combine(at, ".git")) || File.Exists(Path.Combine(at, ".git")))
                return at;
        }

        return browseDirectory;
    }

    /// <summary>The folders whose files are looked through, the workspace's, or the repository of the open file without one.</summary>
    private List<string> WorkspaceRoots() => workspace.IsEmpty ? [RepositoryRoot()] : [.. workspace.Folders];

    /// <summary>What is looked through, said the way a person would.</summary>
    private string DescribeRoots()
    {
        var roots = WorkspaceRoots();
        return roots.Count == 1 ? roots[0] : $"the {roots.Count} folders of the workspace";
    }

    /// <summary>Every .hor of every folder looked through, once each.</summary>
    private List<string> AllWorkspaceFiles() => FilesUnder(WorkspaceRoots());

    /// <summary>Every .hor under some folders, once each. Reads nothing of the editor, for the background.</summary>
    private static List<string> FilesUnder(IReadOnlyList<string> roots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new List<string>();
        foreach (string root in roots)
        {
            foreach (string file in WorkspaceFiles(root))
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
    private static List<string> WorkspaceFiles(string root)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            string folder = pending.Pop();
            try
            {
                files.AddRange(Directory.GetFiles(folder, "*.hor").Where(file => !Path.GetFileName(file).StartsWith("~syncthing~")));

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

    /// <summary>A file by where it is in the workspace, with the name of its folder in front when there is more than one.</summary>
    private string Relative(string path) => RelativeTo(WorkspaceRoots(), path);

    /// <summary>A path as it is inside of the folders looked through, for the background.</summary>
    private static string RelativeTo(IReadOnlyList<string> roots, string path)
    {
        string full = Path.GetFullPath(path);
        string? root = roots.Where(folder => full.StartsWith(folder, StringComparison.OrdinalIgnoreCase)).OrderByDescending(folder => folder.Length).FirstOrDefault();
        if (root is null)
            return full.Replace('\\', '/');

        string inside = Path.GetRelativePath(root, full).Replace('\\', '/');
        return roots.Count > 1 ? $"{Path.GetFileName(root.TrimEnd('\\', '/'))}/{inside}" : inside;
    }

    /// <summary>Ctrl+P, opens a file of the workspace by a few letters of its name.</summary>
    private void QuickOpen()
    {
        List<string> files = AllWorkspaceFiles();
        var byName = new Dictionary<string, string>();
        foreach (string file in files)
            byName[Relative(file)] = file;
        string[] names = [.. byName.Keys];

        ChooseFrom("Open a file", $"{names.Length} files in {DescribeRoots()}, a few letters narrow them down. > for a command, @ for something in the file, : for a line, # for a text",
            [.. names.Select(name => (name, (Action)(() => OpenAt(byName[name], 0, 0))))],
            shortestFirst: true, prefixed: QuickOpenPrefix, recent: RecentOpened);
    }

    /// <summary>What quick open lists for what is typed after a prefix, VS Code's, null for a character that isn't one.</summary>
    private List<(string Text, Action Picked)>? QuickOpenPrefix(char prefix)
    {
        switch (prefix)
        {
            case '>':
                return AllCommands();

            case '@':
                return [.. OutlineHere().Select(entry => (new string(' ', entry.Depth * 2) + entry.Name, (Action)(() => GoToCode(entry.Line, entry.Column))))];

            case ':':
                // A line of every ten, a few letters of the number find the one, and the end of the file
                var lines = new List<(string Text, Action Picked)>();
                int count = code.Document.LineCount;
                for (int line = 1; line <= count; line++)
                {
                    int at = line - 1;
                    lines.Add(($"{line}", () =>
                    {
                        if (document.View == HexView.Table) SwitchView(HexView.Code);
                        GoToCode(at, 0);
                    }));
                }
                return lines;

            case '#':
                return [.. Localisation.Keys.Order(StringComparer.Ordinal).Select(key => ($"{key}   {Shorten(Localisation.Get(key), 60)}", (Action)(() => GoToKey(key))))];

            default:
                return null;
        }
    }

    // Whether a name has the letters typed in it in order, the way every editor's quick open goes
    private static bool Matches(string name, string typed)
    {
        int at = 0;
        foreach (char wanted in typed)
        {
            if (char.IsWhiteSpace(wanted)) continue;
            at = name.IndexOf(wanted.ToString(), at, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return false;
            at++;
        }

        return true;
    }

    /// <summary>Ctrl+Shift+F, every line of every file of the workspace something is in, listed on the left, as the find bar's toggles say.</summary>
    private void FindInFiles()
    {
        string how = DescribeFindOptions(findOptions);
        AskText("Find in files", $"Look for in every .hor in {DescribeRoots()}{how}", lastFind, "Find", query =>
        {
            if (query.Length == 0)
                return;

            lastFind = query;
            List<string> roots = WorkspaceRoots();
            string where = DescribeRoots();
            UIFindOptions options = findOptions;

            // Every file read off the editor's thread, the results come in when they are all there
            InBackground($"looking for '{query}' in {where}...", () => FindIn(roots, query, options), found =>
            {
                ShowResults($"FOUND '{query}' ({found.Count})", found);
                Say(found.Count > 0 ? $"'{query}' is in {found.Select(result => result.Path).Distinct().Count()} files, {found.Count} lines" : $"no '{query}' in {where}", error: found.Count == 0);
            });
        });
    }

    /// <summary>Every line of every file of the workspace something is on, as a word of its own if it says so.</summary>
    private List<HexResult> FindEverywhere(string query, bool wholeWord) => FindIn(WorkspaceRoots(), query, new UIFindOptions(WholeWord: wholeWord));

    /// <summary>
    /// Every line something is on in the files under some folders, the first place on each, the case, whole words and a
    /// regex as the options say. Reads nothing of the editor, for the background. A regex that doesn't build finds nothing.
    /// </summary>
    private static List<HexResult> FindIn(IReadOnlyList<string> roots, string query, UIFindOptions options)
    {
        var found = new List<HexResult>();
        Regex? pattern = null;
        if (options.Regex)
        {
            try { pattern = new Regex(query, (options.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase) | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)); }
            catch (ArgumentException) { return found; }
        }

        foreach (string file in FilesUnder(roots))
        {
            string[] lines = ReadLines(file);
            for (int line = 0; line < lines.Length && found.Count < 2000; line++)
            {
                if (FirstMatch(lines[line], query, options, pattern) is { } at)
                    found.Add(new HexResult(file, line, at, $"{RelativeTo(roots, file)}:{line + 1}  {lines[line].Trim()}"));
            }
        }

        return found;
    }

    // Where something is first on a line as the options say, null for nowhere
    private static int? FirstMatch(string line, string query, UIFindOptions options, Regex? pattern)
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

    // The toggles of the find bar that are on, said after what's asked, empty for none
    private static string DescribeFindOptions(UIFindOptions options)
    {
        var on = new List<string>();
        if (options.MatchCase) on.Add("the case matching");
        if (options.WholeWord) on.Add("whole words");
        if (options.Regex) on.Add("as a regex");
        return on.Count == 0 ? string.Empty : $", {string.Join(", ", on)} (the find bar's toggles)";
    }

    private static bool IsWholeWord(string line, int at, int length) =>
        (at == 0 || !UITextDocument.IsWordChar(line[at - 1])) && (at + length >= line.Length || !UITextDocument.IsWordChar(line[at + length]));

    /// <summary>Every .hor of the workspace parsed, what doesn't parse listed on the left, and what names a text the game hasn't got.</summary>
    private void CheckAllFiles()
    {
        List<string> roots = WorkspaceRoots();
        string where = DescribeRoots();

        InBackground($"checking every file in {where}...", () => CheckFiles(roots), checkedFiles =>
        {
            var (files, problems) = checkedFiles;

            // Into the problems under the editor, the file that is open goes on being said about as it is now
            workspaceProblems = problems;
            problemsDirty = true;
            ShowPanel(PROBLEMS_PAGE);
            Console.WriteLine($"[Hex] Checked {files} files in {where}, {problems.Count} problems.");
            foreach (var problem in problems)
                Console.WriteLine($"[Hex]   {problem.Text}");

            Say(problems.Count == 0 ? $"all {files} files in {where} parse" : $"{problems.Count} problems in {files} files, listed under the editor", error: problems.Count > 0);
        });
    }

    /// <summary>Every .hor under some folders parsed, and what names a text the game hasn't got. For the background.</summary>
    private static (int Files, List<HexResult> Problems) CheckFiles(IReadOnlyList<string> roots)
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

    private static string[] ReadLines(string file)
    {
        try
        {
            return File.ReadAllText(file).Replace("\r\n", "\n").Split('\n');
        }
        catch (Exception)
        {
            return [];
        }
    }

    private const string SEARCH_EMPTY_TEMPLATE = "search_empty.hor", ASK_TEMPLATE = "ask.hor";

    /// <summary>Lists the places something was found in the side bar's search view, and shows it.</summary>
    private void ShowResults(string title, List<HexResult> found)
    {
        results = found;
        resultsTitle = title;
        left.Show(true);
        Side = HexSide.Search;
        treeScroll.Offset = 0;
    }

    private void CloseResults()
    {
        results = null;
        if (Side == HexSide.Search)
            Side = HomeSide;
        treeDirty = true;
    }
}
