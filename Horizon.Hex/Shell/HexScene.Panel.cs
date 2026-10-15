using System.Numerics;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Horizon.Hex;

// The panel under the editor, put away and brought back with Ctrl+J. Problems is everything wrong with the file that is
// open and, after F8, with every file of the workspace, a row a problem, a click goes there. Output is everything Hex has
// said (the status bar only has the last of it) and what running a file printed.
internal sealed partial class HexScene
{
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

    /// <summary>One problem, where it is, what it is, and whether it is only a warning.</summary>
    private readonly record struct HexProblem(string Path, int Line, int Column, string Text, bool Warning);
    private readonly List<HexProblem> problemList = [];

    private void BuildPanel()
    {
        bottom = layout.Get<TabPanel>("bottom");
        problems = layout.Get<StackPanel>("problems");
        output = layout.Get<StackPanel>("output");
        problemsScroll = layout.Get<ScrollPanel>("problems_scroll");
        outputScroll = layout.Get<ScrollPanel>("output_scroll");

        bottom.Visible = !settings.PanelHidden;
        if (settings.PanelHeight > 0.0f)
            bottom.Size = bottom.Size with { Y = Math.Clamp(settings.PanelHeight, MIN_PANEL, MAX_PANEL) };

        bottom.MinSize = new Vector2(0.0f, MIN_PANEL);
        bottom.MaxSize = new Vector2(0.0f, MAX_PANEL);
        bottom.GripSize = 2.0f;
        bottom.GripReach = 8.0f;
        bottom.OnResized = size => settings.PanelHeight = size.Y;
        bottom.OnResizeEnded = _ => SaveSettings();
    }

    private void TogglePanel()
    {
        bottom.Visible = !bottom.Visible;
        settings.PanelHidden = !bottom.Visible;
        SaveSettings();
    }

    /// <summary>Brings the panel up on one of its pages.</summary>
    private void ShowPanel(int page)
    {
        if (!bottom.Visible)
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
            problemList.Add(new HexProblem(here, diagnostic.Line, diagnostic.Column, diagnostic.Message, diagnostic.Warning));

        if (workspaceProblems is not null)
        {
            foreach (HexResult result in workspaceProblems)
            {
                // The open file is said about above, as it is now
                if (document.Path is { } open && string.Equals(Path.GetFullPath(result.Path), Path.GetFullPath(open), StringComparison.OrdinalIgnoreCase))
                    continue;

                problemList.Add(new HexProblem(result.Path, result.Line, result.Column, result.Text, result.Text.Contains("hasn't got", StringComparison.Ordinal)));
            }
        }

        foreach (var (file, problem) in ExtensionProblemsElsewhere())
            problemList.Add(new HexProblem(file, problem.Line, problem.Column, problem.Message, problem.Warning));

        errorCount = problemList.Count(problem => !problem.Warning);
        warningCount = problemList.Count - errorCount;
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

            string where = $"{Path.GetFileName(problem.Path)}:{problem.Line + 1}";
            caption.Text = $"$({(problem.Warning ? "warning" : "error")})  {Plain(problem.Text)}   {where}";
            caption.Color = problem.Warning ? WarningColor : ErrorColor;
            row.Tooltip = problem.Path;
            row.OnPressed = () => OpenAt(problem.Path, problem.Line, problem.Column);
            row.OnContextMenu = _ => ShowProblemMenu(problem);
        }

        if (problemList.Count == 0)
            Hint(problems, workspaceProblems is null ? "nothing wrong with this file, F8 checks every file of the workspace" : "nothing wrong anywhere");

        FitRowsTo(problems, problemsScroll);
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
                FillOutputLine(layout.Instantiate(output.Template, output), said[i]);

            while (output.Children.Count > MAX_OUTPUT)
                output.Remove(output.Children[0]);
        }

        // The latest at the bottom, in sight
        outputScroll.Offset = float.MaxValue;
    }

    private void FillOutputLine(UILayout row, (string Text, bool Error) line)
    {
        var label = row.Get<Label>("line");
        label.Text = Plain(line.Text);
        label.Color = line.Error ? ErrorColor : DimColor;
    }

    private void ShowProblemMenu(HexProblem problem)
    {
        var shown = new Menu("problem");
        shown.Add("Go there", () => OpenAt(problem.Path, problem.Line, problem.Column), icon: "go-to-file");
        shown.Add("Copy it", () => UIClipboard.Copy($"{problem.Path}:{problem.Line + 1}  {problem.Text}"), icon: "copy");
        ContextMenu.ShowAt(chrome, compositor.Pointer.Position, shown);
    }

    private static readonly Vector4 WarningColor = new(1.0f, 0.78f, 0.3f, 1.0f);

    // What somebody wrote, without the brackets a label would take for its markup
    private static string Plain(string text) => text.Replace('[', '(').Replace(']', ')');

    private static void FitRowsTo(StackPanel list, ScrollPanel scroll)
    {
        float width = MathF.Max(MIN_FLEXIBLE, scroll.Size.X - ROW_MARGIN);
        Widen(list.Children, width);
    }
}
