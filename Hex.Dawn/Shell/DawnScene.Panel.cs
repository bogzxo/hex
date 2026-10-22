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
        if (problemsDirty)
        {
            problemsDirty = false;
            GatherProblems();
            bottom.Tabs = PanelTabs(problemList.Count > 0 ? Said("panel.problems_count", ("count", problemList.Count.ToString())) : Said("panel.problems"));
            problemRowsStale = true;
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
    /// Helper method to gather what is wrong. The file that is open first (what doesn't parse, and what the editor
    /// has to say about it), then whatever F8 found in the rest.
    /// </summary>
    private void GatherProblems()
    {
        problemList.Clear();

        string here = document.Path ?? document.Name;
        foreach (UICodeDiagnostic error in code.SyntaxErrors)
            problemList.Add(new HexProblem(here, error.Line, error.Column, error.Message, false));

        foreach (UICodeDiagnostic diagnostic in code.Diagnostics)
            problemList.Add(new HexProblem(here, diagnostic.Line, diagnostic.Column, diagnostic.Message, diagnostic.Warning, diagnostic.Hint));

        if (workspaceProblems is not null)
        {
            foreach (HexResult result in workspaceProblems)
            {
                // The open file is said about above, as it is now
                if (document.Path is { } open && string.Equals(Path.GetFullPath(result.Path), Path.GetFullPath(open), PlatformPaths.Comparison))
                    continue;

                problemList.Add(new HexProblem(result.Path, result.Line, result.Column, result.Text, result.Text.Contains("hasn't got", StringComparison.Ordinal)));
            }
        }

        foreach (var (file, problem) in extensions.ProblemsElsewhere())
            problemList.Add(new HexProblem(file, problem.Line, problem.Column, problem.Message, problem.Warning));

        problemList.AddRange(terminalProblems);

        // What's only worth a look is listed, and counted with neither
        errorCount = problemList.Count(problem => !problem.Warning);
        warningCount = problemList.Count(problem => problem.Warning && !problem.Hint);
    }

    /// <summary>Helper method to put the rows of the problems together out of what was gathered.</summary>
    private void RebuildProblems()
    {
        var items = layout.Populate(problems, problemList.Count);
        for (int i = 0; i < items.Count; i++)
        {
            HexProblem problem = problemList[i];
            var row = items[i].Get<Button>("row");
            var caption = items[i].Get<Label>("caption");

            caption.Text = ProblemCaption(problem);
            caption.Color = null;
            row.Tooltip = problem.Path;
            row.OnPressed = () => OpenAt(problem.Path, problem.Line, problem.Column);
            row.OnContextMenu = _ => ShowProblemMenu(problem);
        }

        if (problemList.Count == 0)
            Hint(problems, workspaceProblems is null ? Said("panel_tabs.nothing_wrong_here") : Said("panel_tabs.nothing_wrong_anywhere"));

        FitRowsTo(problems, problemsScroll);
    }

    // What a server says a problem is called, in brackets after what it says about it ("(dscanner.suspicious.unused_variable)")
    private static readonly System.Text.RegularExpressions.Regex ProblemCode = new(@"^(?<said>.*?)\s*\((?<code>[^()\s]+)\)\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// A row of the problems the way VS Code has them, only its icon in the colour of how bad it is, what's wrong in the
    /// text's colour, and what the server calls it and where it is quiet after. The whole line was red or yellow, and a
    /// panel of them shouted.
    /// </summary>
    private static string ProblemCaption(HexProblem problem)
    {
        string shade = problem.Hint ? "dim" : problem.Warning ? "warning" : "error";
        string icon = problem.Hint ? "info" : problem.Warning ? "warning" : "error";
        string where = $"{Path.GetFileName(problem.Path)}:{problem.Line + 1}";

        string text = UIRichText.Literal(problem.Text);
        string code = string.Empty;
        if (ProblemCode.Match(text) is { Success: true } named)
            (text, code) = (named.Groups["said"].Value, named.Groups["code"].Value + "  ");

        return $"[color:{shade}]$({icon})[/color]  {text}   [color:dim]{code}{where}[/color]";
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

        // The latest at the bottom, in sight
        outputScroll.Offset = float.MaxValue;
    }

    private void FillOutputLine(UILayout row, (string Text, bool Error) line)
    {
        var label = row.Get<Label>("line");
        label.Text = UIRichText.Plain(line.Text);
        label.Color = line.Error ? ErrorColor : DimColor;

        // A long line goes on under itself, the width of the panel (FitChrome keeps it so), it ran off the right edge
        label.Wrap = true;
        label.Size = label.Size with { X = MathF.Max(MIN_FLEXIBLE, outputScroll.Size.X - ROW_MARGIN) };
    }

    private void ShowProblemMenu(HexProblem problem)
    {
        var shown = new Menu("problem");
        shown.Add(Said("panel_tabs.go_there"), () => OpenAt(problem.Path, problem.Line, problem.Column), icon: "go-to-file");
        shown.Add(Said("panel_tabs.copy_it"), () => UIClipboard.Copy($"{problem.Path}:{problem.Line + 1}  {problem.Text}"), icon: "copy");
        ContextMenu.ShowAt(chrome, compositor.Pointer.Position, shown);
    }

    private static void FitRowsTo(StackPanel list, ScrollPanel scroll)
    {
        float width = MathF.Max(MIN_FLEXIBLE, scroll.Size.X - ROW_MARGIN);
        Widen(list.Children, width);
    }
}
