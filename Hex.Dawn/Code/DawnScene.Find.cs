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

using Key = Silk.NET.Input.Key;

namespace Hex.Dawn;

// Finding things. In the file (Ctrl+F, F3, Ctrl+H, Ctrl+G), and in every file of the workspace, opening one by a few
// letters of its name (Ctrl+P), every line something is on (Ctrl+Shift+F) and every file checked for what is wrong (F8).
internal sealed partial class DawnScene
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
            new DialogChoice(Said("keys.leave_it")));

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

    // Ctrl+G, quick open with a colon in it, the lines of the file to go to, the way VS Code has it. It was a dialog of
    // its own with a box, one more kind of window than the rest of Hex has
    private void GoToLine() => QuickOpenWith(":");

    /* The workspace, every .hor of its folders, or of the repository the open file is in when it has none */

    /// <summary>
    /// The repository the document on screen is in (the nearest folder up from it with git in it), failing that the folder
    /// it's in, and with no file the folder files are opened from. A D file with no git above it looked through Hex's own
    /// layouts folder.
    /// </summary>
    private string RepositoryRoot()
    {
        string? from = document.Path is { } path ? Path.GetDirectoryName(Path.GetFullPath(path)) : browseDirectory;
        for (string? at = from; at is not null; at = Path.GetDirectoryName(at))
        {
            if (Directory.Exists(Path.Combine(at, ".git")) || File.Exists(Path.Combine(at, ".git")))
                return at;
        }

        return from is not null && Directory.Exists(from) ? from : browseDirectory;
    }

    /// <summary>The folders whose files are looked through, the workspace's, or the repository of the open file without one.</summary>
    private List<string> WorkspaceRoots() => explorer.workspace.IsEmpty ? [RepositoryRoot()] : [.. explorer.workspace.Folders];

    /// <summary>
    /// What is looked through, said the way a person would, a folder by its name. Its whole path wrapped in the middle of
    /// a word in the dialog that asks.
    /// </summary>
    private string DescribeRoots()
    {
        var roots = WorkspaceRoots();
        return roots.Count == 1 ? Path.GetFileName(roots[0].TrimEnd('/', '\\')) is { Length: > 0 } name ? name : roots[0]
            : Said("find_menu.workspace_folders", ("count", HexText.Count("folder", roots.Count)));
    }

    /// <summary>Every file of every folder looked through that reads as text, once each.</summary>
    private List<string> AllWorkspaceFiles() => HexSearch.TextFilesUnder(WorkspaceRoots());

    /// <summary>A file by where it is in the workspace, with the name of its folder in front when there is more than one.</summary>
    private string Relative(string path) => HexSearch.RelativeTo(WorkspaceRoots(), path);

    /// <summary>Ctrl+P, opens a file of the workspace by a few letters of its name, or with a prefix typed another of its lists.</summary>
    private void QuickOpen() => QuickOpenWith(string.Empty);

    private void QuickOpenWith(string typed)
    {
        List<string> files = AllWorkspaceFiles();
        var byName = new Dictionary<string, string>();
        foreach (string file in files)
            byName[Relative(file)] = file;
        string[] names = [.. byName.Keys];

        // The folder by its name and not its whole path, a line along the bottom of the palette and not a paragraph
        var roots = WorkspaceRoots();
        string where = roots.Count == 1 ? Path.GetFileName(roots[0].TrimEnd('/', '\\')) : DescribeRoots();
        List<(string Text, Action Picked)> choices = [.. names.Select(name => (name, (Action)(() => OpenAt(byName[name], 0, 0))))];

        // Nothing in the workspace (there's none yet), the files worked on last and a folder to open, it said there was
        // nothing to pick from
        if (choices.Count == 0)
        {
            foreach (string file in recent.Where(File.Exists).Take(10))
            {
                string path = file;
                choices.Add((path, () => OpenAt(path, 0, 0)));
            }
            choices.Add((Said("find_menu.open_folder"), () => explorer.AskFolder(explorer.OpenFolder)));
        }

        // Ctrl+G's says what it's going along, the file's lines, not quick open's way of finding a file
        string prompt = typed.StartsWith(':')
            ? Said("find_menu.go_to_line_prompt", ("lines", HexText.Count("line", code.Document.LineCount)), ("file", document.Path is { } shownPath ? Path.GetFileName(shownPath) : document.Name))
            : Said("find_menu.open_file_prompt", ("count", HexText.Count("file", names.Length)), ("where", where));
        ChooseFrom(Said(typed.StartsWith(':') ? "find_menu.go_to_line_title" : "find_menu.open_file_title"), prompt,
            choices, shortestFirst: names.Length > 0, prefixed: QuickOpenPrefix, recent: RecentOpened, typed: typed);
    }

    /// <summary>What quick open lists for what is typed after a prefix, VS Code's, null for a character that isn't one.</summary>
    private List<(string Text, Action Picked)>? QuickOpenPrefix(char prefix)
    {
        switch (prefix)
        {
            case '>':
                return AllCommands();

            case '@':
                return [.. OutlineHere().Select(entry => (new string(' ', entry.Depth * 2) + entry.Name, (Action)(() => running.GoToCode(entry.Line, entry.Column))))];

            case ':':
                // Every line by its number with what's on it dim after, a few letters of the number find the one. Bare
                // numbers said nothing of which line was which
                var lines = new List<(string Text, Action Picked)>();
                int count = code.Document.LineCount;
                for (int line = 1; line <= count; line++)
                {
                    int at = line - 1;
                    string on = code.Document.LineAt(at).Trim();
                    lines.Add((on.Length > 0 ? $"{line}\t{HexEvaluation.Shorten(on, 90)}" : $"{line}", () =>
                    {
                        if (document.View == HexView.Table) SwitchView(HexView.Code);
                        running.GoToCode(at, 0);
                    }));
                }
                return lines;

            case '#':
                return [.. Localisation.Keys.Order(StringComparer.Ordinal).Select(key => ($"{key}   {HexEvaluation.Shorten(Localisation.Get(key), 60)}", (Action)(() => codeEditing.GoToKey(key))))];

            default:
                return null;
        }
    }

    /// <summary>Ctrl+Shift+F, every line of every file of the workspace something is in, listed on the left, as the find bar's toggles say.</summary>
    private void FindInFiles()
    {
        string how = HexSearch.DescribeOptions(codeEditing.findOptions);
        AskText(Said("find_menu.find_in_files_title"), Said("find_menu.find_in_files_prompt", ("where", DescribeRoots()), ("how", how)), codeEditing.lastFind, Said("find_menu.find"), query =>
        {
            if (query.Length == 0)
                return;

            codeEditing.lastFind = query;
            List<string> roots = WorkspaceRoots();
            string where = DescribeRoots();
            UIFindOptions options = codeEditing.findOptions;

            // Every file read off the editor's thread, the results come in when they are all there
            InBackground(Said("find_menu.looking_for", ("query", query), ("where", where)), () => HexSearch.FindIn(roots, query, options), found =>
            {
                ShowResults(Said("find_menu.found_title", ("query", query), ("count", found.Count.ToString())), found);
                Say(found.Count > 0
                    ? Said("find_menu.found_in_files", ("query", query), ("files", HexText.Count("file", found.Select(result => result.Path).Distinct().Count())), ("lines", HexText.Count("line", found.Count)))
                    : Said("find_menu.not_found", ("query", query), ("where", where)), error: found.Count == 0);
            });
        });
    }

    /// <summary>Every line of every file of the workspace something is on, as a word of its own if it says so.</summary>
    private List<HexResult> FindEverywhere(string query, bool wholeWord) => HexSearch.FindIn(WorkspaceRoots(), query, new UIFindOptions(WholeWord: wholeWord));

    /// <summary>
    /// Ctrl+F8, every .hor of the workspace parsed, what doesn't parse listed on the left, and what names a text the game
    /// hasn't got. With them every D file looked over by the compiler and every C# project built, what's wrong in a file
    /// nobody has open is there before anybody builds it.
    /// </summary>
    private void CheckAllFiles()
    {
        List<string> roots = WorkspaceRoots();
        string where = DescribeRoots();
        string? dChecker = languages.DChecker();

        InBackground(Said("find_menu.checking", ("where", where)), () =>
        {
            var (hidlFiles, hidlProblems) = HexSearch.CheckFiles(roots);
            var (codeFiles, codeProblems) = HexLanguages.CheckCode(roots, dChecker);
            return (hidlFiles + codeFiles, (List<HexResult>)[.. hidlProblems, .. codeProblems]);
        }, checkedFiles =>
        {
            var (files, problems) = checkedFiles;

            // Into the problems under the editor, the file that is open goes on being said about as it is now
            workspaceProblems = problems;
            problemsDirty = true;
            ShowPanel(PROBLEMS_PAGE);
            Console.WriteLine($"[Hex] Checked {files} files in {where}, {problems.Count} problems.");
            foreach (var problem in problems)
                Console.WriteLine($"[Hex]   {problem.Text}");

            Say(problems.Count == 0
                ? Said("find_menu.all_parse", ("files", HexText.Count("file", files)), ("where", where))
                : Said("find_menu.problems_found", ("count", HexText.Count("problem", problems.Count)), ("files", HexText.Count("file", files))), error: problems.Count > 0);
        });
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

    /// <summary>Finding across the workspace, and going to a file, a line or something a file declares by a few letters.</summary>
    private void AddFindCommands()
    {
        commands.Add("Edit/files", Said("find_menu.find_in_files"), FindInFiles, "Ctrl+Shift+F", "search", UIKeyUse.BoundWhileTyping);

        commands.Add("Go/open", Said("find_menu.go_to_file"), QuickOpen, "Ctrl+P", "go-to-file", UIKeyUse.BoundWhileTyping);
        commands.Add("Go/open", Said("find_menu.go_to_line"), GoToLine, "Ctrl+G").IsVisible = () => !LayoutTools;
        commands.Add("Go/open", Said("find_menu.go_to_symbol"), GoToSymbol, icon: "symbol-variable").IsVisible = () => !LayoutTools;

        // Ctrl+G groups what is selected in a layout, it only goes to a line where there is code to go about in
        commands.Bind(Key.G, () => { if (!LayoutTools || compositor.Focus is CodeEditor) GoToLine(); }, control: true, whileTyping: true, shift: false);
    }
}
