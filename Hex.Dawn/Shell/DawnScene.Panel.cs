using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

/// <summary>One problem, where it is, what it is, and whether it is only a warning.</summary>
internal readonly record struct HexProblem(string Path, int Line, int Column, string Text, bool Warning, bool Hint = false);

// The panel under the editor, put away and brought back with Ctrl+J. Problems is everything wrong with the file that is
// open and, after F8, with every file of the workspace, a row a problem, a click goes there. Output is everything Hex has
// said (the status bar only has the last of it) and what running a file printed. With nothing wrong and the problems the
// page on it, it folds down to its tabs, "nothing wrong with this file" took a seventh of the window every time.
internal sealed partial class DawnScene
{
    // The page of the panel the terminals and the console are on
    private const int CONSOLE_PAGE = 2;

    private const int PROBLEMS_PAGE = 0, OUTPUT_PAGE = 1;
    private const int MAX_OUTPUT = 400;
    private const float MIN_PANEL = 80.0f, MAX_PANEL = 600.0f;

    private TabPanel bottom = null!;
    private StackPanel problems = null!, output = null!;
    private ScrollPanel problemsScroll = null!, outputScroll = null!;

    // What F8 found wrong across the workspace, null until it has been run
    private List<HexResult>? workspaceProblems;

    // Everything said, the latest last, and whether either list has to be put together again
    private readonly List<(string Text, bool Error)> said = [];
    private bool problemsDirty = true, outputDirty = true;

    // How many lines have been said in all, and how many of those the output's rows were last brought up to. Every
    // message built all four hundred rows anew out of their template, megabytes a second of garbage with the panel not
    // even showing, so the rows are added as lines are said and only while the output is the page on screen
    private long saidTotal, outputShownUpTo;

    // Whether the rows of the problems are out of date with what is wrong, built again when that page is looked at
    private bool problemRowsStale = true;

    // The rows are built again at most this often, a burst of changes (F8 over the workspace, a file that prints a
    // hundred lines) is one rebuild and not one a tick. How long until they may be next
    private const float PANEL_ROWS_EVERY = 0.1f;
    private float panelRowsWait;

    // How many errors and how many warnings there are, for the status bar and the panel's tab
    private int errorCount, warningCount;

    // Whether the panel is folded down to its tabs, how it could be dragged before it was, and whether it's been folded
    // or opened since Hex came up (the first time is at once, the window opens with it as it was left)
    private bool panelFolded, panelFoldSettled;
    private UIResize panelResize;

    // Whether it was opened by hand while there's nothing wrong, it stays open then rather than folding itself again
    private bool panelOpenedByHand;

    // How much of the middle the panel took the last time the editor was fitted round it, see FitChrome
    private float panelTaken;

    // How tall the panel is wanted open, as it was dragged or as it comes. What it is is that or what the window has room
    // for, worked out again every tick, so a tick with too little room doesn't lose it for good
    private float panelWanted;

    private readonly List<HexProblem> problemList = [];

    // What the problems the panel shows came to the last time, see ProblemsSignature
    private int problemsListed = int.MinValue;

    // The problems as one number, the same while the list is, without making anything
    private int ProblemsSignature()
    {
        var hash = new HashCode();
        hash.Add(problemList.Count);
        foreach (HexProblem problem in problemList)
            hash.Add(problem);
        return hash.ToHashCode();
    }

    private void BuildPanel()
    {
        bottom = layout.Get<TabPanel>("bottom");
        problems = layout.Get<StackPanel>("problems");
        output = layout.Get<StackPanel>("output");
        problemsScroll = layout.Get<ScrollPanel>("problems_scroll");
        outputScroll = layout.Get<ScrollPanel>("output_scroll");

        bottom.Show(!settings.PanelHidden, instantly: true);
        if (settings.PanelHeight > 0.0f)
            bottom.Size = bottom.Size with { Y = Math.Clamp(settings.PanelHeight, MIN_PANEL, MAX_PANEL) };
        panelWanted = bottom.Size.Y;

        bottom.MinSize = new Vector2(0.0f, MIN_PANEL);
        bottom.MaxSize = new Vector2(0.0f, MAX_PANEL);
        bottom.GripSize = 2.0f;
        bottom.GripReach = 8.0f;
        bottom.OnResized = size => settings.PanelHeight = panelWanted = size.Y;
        bottom.OnResizeEnded = _ => SaveSettings();
        bottom.OnFold = CollapsePanel;
        bottom.FoldTip = Said("panel.fold_tip");
        bottom.UnfoldTip = Said("panel.unfold_tip");
    }

    /// <summary>
    /// The panel folded down to its tabs by its chevron, or opened again by it or a click on a tab, and kept that way. The
    /// whole of it put away (Ctrl+J) took the tabs with it, and there was no way to have the tabs and not the panel.
    /// </summary>
    private void CollapsePanel(bool fold)
    {
        settings.PanelCollapsed = fold;
        panelOpenedByHand = !fold;
        SaveSettings();
    }

    /// <summary>
    /// The panel folded down to its tabs (nothing on the page it's on) or opened back up to the height it was dragged to,
    /// eased there by the tab panel itself, its height kept for when it opens. Folded it can't be dragged, a click on
    /// another tab opens it. It was the height set to the tabs' and back in one go, a snap, and the editor over it a tick
    /// behind, which flashed a gap or the two over each other.
    /// </summary>
    private void FoldPanel(bool fold)
    {
        panelFolded = fold;
        if (fold)
        {
            panelResize = bottom.Resizable;
            bottom.Resizable = UIResize.None;
        }
        else
            bottom.Resizable = panelResize;

        bottom.Fold(fold, instantly: !panelFoldSettled);
        panelFoldSettled = true;
    }

    /// <summary>
    /// How much of the middle the panel takes as it'll be laid out this tick, as far folded and slid away as it is now,
    /// and the gap over it, nothing while it's away. The layout comes after the editor is fitted round it.
    /// </summary>
    private float PanelTakes() => bottom.Visible
        ? bottom.FoldedFrom(bottom.Size.Y) * bottom.SlideAmount + (center.Spacing >= 0.0f ? center.Spacing : compositor.Skin?.Spacing ?? 0.0f)
        : 0.0f;

    private void TogglePanel()
    {
        bottom.ToggleShown();
        settings.PanelHidden = !bottom.Shown;
        SaveSettings();
    }

    /// <summary>Brings the panel up on one of its pages.</summary>
    private void ShowPanel(int page)
    {
        if (!bottom.Shown)
            TogglePanel();

        bottom.Select(page);
    }

    /// <summary>Notes something Hex said in the output, the status bar shows the last of it.</summary>
    private void Note(string text, bool error = false)
    {
        said.Add((text, error));
        saidTotal++;
        if (said.Count > MAX_OUTPUT)
            said.RemoveRange(0, said.Count - MAX_OUTPUT);
        outputDirty = true;
    }

    /// <summary>
    /// Every tick, what is wrong counted again when it may have changed (the status bar and the panel's tab say how much),
    /// and the rows of whichever page is on screen brought up to date. The page that isn't showing waits until it is.
    /// </summary>
    private void UpdatePanel(float dt)
    {
        UpdateTerminalProblems();
        if (problemsDirty)
        {
            problemsDirty = false;
            GatherProblems();

            // The rows only when what's wrong is something else now. Checks that come round again and find the same things had the
            // rows of the panel made anew every second, twenty kilobytes of garbage each time for nothing on the screen
            bottom.Tabs = PanelTabs(problemList.Count > 0 ? Said("panel.problems_count", ("count", problemList.Count.ToString())) : Said("panel.problems"));
            int now = ProblemsSignature();
            if (now != problemsListed)
            {
                problemsListed = now;
                problemRowsStale = true;
            }
        }

        // Folded by hand, or by itself with nothing wrong on the page showing unless it was opened by hand since
        bool fold = settings.PanelCollapsed || (bottom.Selected == PROBLEMS_PAGE && problemList.Count == 0 && !panelOpenedByHand);
        if (fold != panelFolded || !panelFoldSettled)
            FoldPanel(fold);

        panelRowsWait -= dt;
        if (!bottom.Visible || panelRowsWait > 0.0f)
            return;

        if ((problemRowsStale && bottom.Selected == PROBLEMS_PAGE) || (outputDirty && bottom.Selected == OUTPUT_PAGE))
            panelRowsWait = PANEL_ROWS_EVERY;

        if (problemRowsStale && bottom.Selected == PROBLEMS_PAGE)
        {
            problemRowsStale = false;
            RebuildProblems();
        }

        if (outputDirty && bottom.Selected == OUTPUT_PAGE)
        {
            outputDirty = false;
            SyncOutput();
        }
    }

    /// <summary>
    /// Helper method to gather what is wrong. The file on screen first (what doesn't parse, and what the editor has to
    /// say about it), then every other file that's open the same way, then whatever F8, the extensions and the terminal
    /// found in files that aren't. A file of another tab had what its server said of it nowhere but under its own code,
    /// VS Code lists every open file's.
    /// </summary>
    private void GatherProblems()
    {
        problemList.Clear();

        // A diff on screen has nothing wrong with it, both versions in one are of no file anybody has
        string here = document.Path ?? document.Name;
        bool quiet = document.DiffOf is not null;
        foreach (UICodeDiagnostic error in quiet ? [] : code.SyntaxErrors)
            problemList.Add(new HexProblem(here, error.Line, error.Column, error.Message, false));

        foreach (UICodeDiagnostic diagnostic in quiet ? [] : code.Diagnostics)
            problemList.Add(new HexProblem(here, diagnostic.Line, diagnostic.Column, diagnostic.Message, diagnostic.Warning, diagnostic.Hint));

        // The files that are open are said about as they are in their editors, nothing about them from elsewhere
        var open = new HashSet<string>(PlatformPaths.Comparer);
        foreach (HexDocument other in documents)
        {
            if (other.Path is not { } path || !other.IsText)
                continue;

            open.Add(Path.GetFullPath(path));
            if (other == document)
                continue;

            foreach (UICodeDiagnostic error in other.Editor.SyntaxErrors)
                problemList.Add(new HexProblem(path, error.Line, error.Column, error.Message, false));
            foreach (UICodeDiagnostic diagnostic in other.Editor.Diagnostics)
                problemList.Add(new HexProblem(path, diagnostic.Line, diagnostic.Column, diagnostic.Message, diagnostic.Warning, diagnostic.Hint));
        }

        bool Elsewhere(string path) => !open.Contains(Path.GetFullPath(path));

        if (workspaceProblems is not null)
        {
            foreach (HexResult result in workspaceProblems)
            {
                if (Elsewhere(result.Path))
                    problemList.Add(new HexProblem(result.Path, result.Line, result.Column, result.Text, result.Warning || result.Text.Contains("hasn't got", StringComparison.Ordinal)));
            }
        }

        foreach (var (file, problem) in extensions.ProblemsElsewhere())
        {
            if (Elsewhere(file))
                problemList.Add(new HexProblem(file, problem.Line, problem.Column, problem.Message, problem.Warning));
        }

        // An open file's are among its own diagnostics above, underlined in its code
        foreach (HexProblem problem in terminalProblems)
        {
            if (Elsewhere(problem.Path))
                problemList.Add(problem);
        }

        // What's wrong with the settings files written by hand, an open one's underlined in it the same way
        foreach (HexProblem problem in SettingsProblems())
        {
            if (Elsewhere(problem.Path))
                problemList.Add(problem);
        }

        // What's only worth a look is listed, and counted with neither
        errorCount = problemList.Count(problem => !problem.Warning);
        warningCount = problemList.Count(problem => problem.Warning && !problem.Hint);
    }

    /// <summary>
    /// Helper method to put the rows of the problems together out of what was gathered, under a row for each file the way
    /// VS Code has them, the file on screen first. Each row says where on its line first, a long message cut off at the
    /// panel's edge took where it was with it.
    /// </summary>
    private void RebuildProblems()
    {
        var groups = problemList.GroupBy(problem => Path.GetFullPath(problem.Path), PlatformPaths.Comparer).ToList();
        var items = layout.Populate(problems, problemList.Count + groups.Count);
        int at = 0;
        foreach (var group in groups)
        {
            HexProblem first = group.First();
            var header = items[at++];
            header.Get<Label>("caption").Text = FileCaption(first.Path, group.Count());
            header.Get<Label>("caption").Color = null;
            var headerRow = header.Get<Button>("row");
            headerRow.Tooltip = HexText.MiddleOf(Relative(first.Path));
            headerRow.OnPressed = () => OpenAt(first.Path, first.Line, first.Column);
            headerRow.OnContextMenu = null;

            foreach (HexProblem problem in group)
            {
                var row = items[at].Get<Button>("row");
                var caption = items[at++].Get<Label>("caption");
                caption.Text = ProblemCaption(problem);
                caption.Color = null;
                row.Tooltip = $"{problem.Text}\n{HexText.MiddleOf(Relative(problem.Path))}:{problem.Line + 1}:{problem.Column + 1}";
                row.OnPressed = () => OpenAt(problem.Path, problem.Line, problem.Column);
                row.OnContextMenu = _ => ShowProblemMenu(problem);
            }
        }

        if (problemList.Count == 0)
            Hint(problems, workspaceProblems is null ? Said("panel_tabs.nothing_wrong_here") : Said("panel_tabs.nothing_wrong_anywhere"));

        FitRowsTo(problems, problemsScroll);
    }

    // What a server says a problem is called, in brackets after what it says about it ("(dscanner.suspicious.unused_variable)")
    private static readonly System.Text.RegularExpressions.Regex ProblemCode = new(@"^(?<said>.*?)\s*\((?<code>[^()\s]+)\)\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// A row of the problems, only its icon in the colour of how bad it is, where it is and what the server calls it quiet,
    /// then what's wrong in the text's colour. The whole line was red or yellow, and a panel of them shouted.
    /// </summary>
    private static string ProblemCaption(HexProblem problem)
    {
        string shade = problem.Hint ? "dim" : problem.Warning ? "warning" : "error";
        string icon = problem.Hint ? "info" : problem.Warning ? "warning" : "error";
        string where = $"{problem.Line + 1}:{problem.Column + 1}";

        // A compiler's backticks round a name as quotes, they came out as they are
        string text = UIRichText.Literal(problem.Text.Replace('`', '\''));
        string code = string.Empty;
        if (ProblemCode.Match(text) is { Success: true } named)
            (text, code) = (named.Groups["said"].Value, named.Groups["code"].Value);

        // Where up front, quiet, then the message, and what it's called after it quiet too. With the code between them the
        // messages of a server that names none started further left than the rest
        return $"      [color:{shade}]$({icon})[/color]  [color:dim]{where}[/color]   {text}{(code.Length > 0 ? $"   [color:dim]{code}[/color]" : string.Empty)}";
    }

    /// <summary>The row over a file's problems, the file by its name with its folder and how many there are quiet after.</summary>
    private string FileCaption(string path, int count)
    {
        // With slashes the way quick open writes a path, it said dubapp\source here and dubapp/source there
        string folder = (Path.GetDirectoryName(Relative(path)) ?? string.Empty).Replace('\\', '/');
        return $"$({UIFileKinds.IconOf(path)})  {UIRichText.Literal(Path.GetFileName(path))}   [color:dim]{UIRichText.Literal(folder)}   {count}[/color]";
    }

    /// <summary>
    /// Helper method to bring the rows of the output up to what has been said, a row added for every line said since
    /// the last time and the oldest let go of past the most there are. All of them anew only when more was said than
    /// there are rows for.
    /// </summary>
    private void SyncOutput()
    {
        long fresh = saidTotal - outputShownUpTo;
        outputShownUpTo = saidTotal;

        if (fresh >= said.Count)
        {
            var items = layout.Populate(output, said.Count);
            for (int i = 0; i < items.Count; i++)
                FillOutputLine(items[i], said[i]);
        }
        else
        {
            for (int i = said.Count - (int)fresh; i < said.Count; i++)
                FillOutputLine(layout.Item(output), said[i]);

            while (output.Children.Count > MAX_OUTPUT)
                output.Remove(output.Children[0]);
        }

        // The latest at the bottom, in sight, once they're laid out (the panel sticks to its end)
        outputScroll.StickToEnd = true;
        outputScroll.ScrollToEnd();
    }

    private void FillOutputLine(UILayout row, (string Text, bool Error) line)
    {
        var label = row.Get<Label>("line");
        label.Text = UIRichText.Plain(line.Text);
        label.Color = line.Error ? ErrorColor : DimColor;

        // A long line goes on under itself, the width of the panel (FitChrome keeps it so), it ran off the right edge
        label.Wrap = true;
        label.Size = label.Size with { X = RowWidthOf(outputScroll) };
    }

    private void ShowProblemMenu(HexProblem problem)
    {
        var shown = new Menu("problem");
        shown.Add(Said("panel_tabs.go_there"), () => OpenAt(problem.Path, problem.Line, problem.Column), icon: "go-to-file");
        shown.Add(Said("panel_tabs.copy_it"), () => UIClipboard.Copy($"{problem.Path}:{problem.Line + 1}  {problem.Text}"), icon: "copy");
        ContextMenu.ShowAt(chrome, compositor.Pointer.Position, shown);
    }

    private static void FitRowsTo(StackPanel list, ScrollPanel scroll) => Widen(list.Children, RowWidthOf(scroll));

    // How wide a row of a list in the panel is, the width the list was laid out at. It fills the panel, so it has no size
    // of its own, and the output's lines wrapped at the least a row can be, a column ten letters wide
    private static float RowWidthOf(ScrollPanel scroll) =>
        MathF.Max(MIN_FLEXIBLE, (scroll.Bounds.Width > 0.0f ? scroll.Bounds.Width : scroll.Size.X) - ROW_MARGIN);
}
