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
    private List<string> AllWorkspaceFiles() => HexSearch.FilesUnder(WorkspaceRoots());

    /// <summary>A file by where it is in the workspace, with the name of its folder in front when there is more than one.</summary>
    private string Relative(string path) => HexSearch.RelativeTo(WorkspaceRoots(), path);

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
                return [.. OutlineHere().Select(entry => (new string(' ', entry.Depth * 2) + entry.Name, (Action)(() => running.GoToCode(entry.Line, entry.Column))))];

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
                        running.GoToCode(at, 0);
                    }));
                }
                return lines;

            case '#':
                return [.. Localisation.Keys.Order(StringComparer.Ordinal).Select(key => ($"{key}   {HexEvaluation.Shorten(Localisation.Get(key), 60)}", (Action)(() => GoToKey(key))))];

            default:
                return null;
        }
    }

    /// <summary>Ctrl+Shift+F, every line of every file of the workspace something is in, listed on the left, as the find bar's toggles say.</summary>
    private void FindInFiles()
    {
        string how = HexSearch.DescribeOptions(findOptions);
        AskText("Find in files", $"Look for in every .hor in {DescribeRoots()}{how}", lastFind, "Find", query =>
        {
            if (query.Length == 0)
                return;

            lastFind = query;
            List<string> roots = WorkspaceRoots();
            string where = DescribeRoots();
            UIFindOptions options = findOptions;

            // Every file read off the editor's thread, the results come in when they are all there
            InBackground($"looking for '{query}' in {where}...", () => HexSearch.FindIn(roots, query, options), found =>
            {
                ShowResults($"FOUND '{query}' ({found.Count})", found);
                Say(found.Count > 0 ? $"'{query}' is in {found.Select(result => result.Path).Distinct().Count()} files, {found.Count} lines" : $"no '{query}' in {where}", error: found.Count == 0);
            });
        });
    }

    /// <summary>Every line of every file of the workspace something is on, as a word of its own if it says so.</summary>
    private List<HexResult> FindEverywhere(string query, bool wholeWord) => HexSearch.FindIn(WorkspaceRoots(), query, new UIFindOptions(WholeWord: wholeWord));

    /// <summary>Every .hor of the workspace parsed, what doesn't parse listed on the left, and what names a text the game hasn't got.</summary>
    private void CheckAllFiles()
    {
        List<string> roots = WorkspaceRoots();
        string where = DescribeRoots();

        InBackground($"checking every file in {where}...", () => HexSearch.CheckFiles(roots), checkedFiles =>
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
        commands.Add("Edit/files", "Find in Files...", FindInFiles, "Ctrl+Shift+F", "search", UIKeyUse.BoundWhileTyping);

        commands.Add("Go/open", "Go to File...", QuickOpen, "Ctrl+P", "go-to-file", UIKeyUse.BoundWhileTyping);
        commands.Add("Go/open", "Go to Line...", GoToLine, "Ctrl+G").IsVisible = () => !LayoutTools;
        commands.Add("Go/open", "Go to Symbol...", GoToSymbol, icon: "symbol-variable").IsVisible = () => !LayoutTools;

        // Ctrl+G groups what is selected in a layout, it only goes to a line where there is code to go about in
        commands.Bind(Key.G, () => { if (!LayoutTools || compositor.Focus is CodeEditor) GoToLine(); }, control: true, whileTyping: true, shift: false);
    }
}
