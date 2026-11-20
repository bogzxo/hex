using System.Numerics;
using System.Text.Json.Nodes;

using Hex.Core.Notebooks;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

/// <summary>Where a code cell's run is, as its gutter says it.</summary>
internal enum CellState
{
    None,
    Queued,
    Running,
    Done,
    Failed
}

/// <summary>The colours the notebook draws its states in, all of them the skin's own, transparent without one.</summary>
internal readonly record struct NotebookLook(Vector4 Accent, Vector4 AccentSoft, Vector4 Error, Vector4 Success, Vector4 Warning, Vector4 Dim, Vector4 Picked, Vector4 Typing);

// How a notebook says what's going on, for somebody who sits in one for hours. Each code cell's gutter says where its run
// is (waiting its turn, running with the seconds going up, how long it took, or that it failed, in the error colour), the
// picked cell has its rails in the accent and a row of tools over its corner, and the bar over the cells says how far a
// Run All has got, which cell is picked and whether it's typed into, and the kernel with a dot in the colour of its state.
// Every colour is the skin's, taken again whenever the skin changes
internal sealed partial class DawnScene
{
    // The skin the notebook's colours were last taken from
    private Horizon.UI.Skinning.UISkin? notebookSkin;

    // How narrow the bar and a cell can get before their buttons drop their words, and the most rows an output shown
    // whole has
    private const float BAR_COMPACT = 900.0f, BAR_TIGHT = 620.0f, TOOLS_COMPACT = 560.0f;

    /// <summary>The notebook's colours out of the skin as it is now.</summary>
    private NotebookLook NotebookLookNow()
    {
        if (compositor.Skin is not { } skin)
            return default;

        Vector4 accent = skin.Palette.Accent;
        Vector4 hover = skin.HoverColor;
        return new NotebookLook(
            Accent: accent with { W = 1.0f },
            AccentSoft: accent with { W = 0.5f },
            Error: skin.ErrorColor,
            Success: skin.SuccessColor,
            Warning: skin.WarningColor,
            Dim: skin.DimTextColor,
            Picked: hover with { W = Math.Clamp(hover.W * 0.5f, 0.04f, 0.07f) },
            Typing: accent with { W = 0.06f });
    }

    /// <summary>
    /// The notebook's colours taken from the skin once it's another (a look or a theme picked), the view's background and
    /// what's in the cells coloured again where it is, nothing made again (the pictures and the math keep their textures).
    /// Taken once when the view was built, a light look left the view dark behind dark words.
    /// </summary>
    private void ColourNotebook()
    {
        if (ReferenceEquals(compositor.Skin, notebookSkin))
            return;

        notebookSkin = compositor.Skin;
        Vector4 panel = notebookSkin is { } skin ? skin.PanelColor with { W = 1.0f } : Vector4.Zero;
        notebookView.Color = panel;
        cellTools.Color = panel;
        cellTools.Outline = NotebookOutline();
        notebookHint.Color = notebookSkin?.DimTextColor;
        kernelShown = default;
        foreach (NotebookCellView view in cellViews)
        {
            view.Editor.Color = CellEditorColor;
            if (view.Frame is { } frame)
                frame.Outline = NotebookOutline();
            view.StateShown = -1;
            ColourOutputs(view.Card);
        }
    }

    // Helper method for the colours of what's in a cell that were the skin's when it was made: text's ANSI colours and an
    // error's tint, an error's words, the boxes of a markdown table
    private void ColourOutputs(UIComponent of)
    {
        Horizon.UI.Skinning.UISkin? skin = compositor.Skin;
        switch (of)
        {
            case NotebookTextOutput text:
                if (skin is not null)
                    text.AnsiColours = TerminalPalette.FromPalette(skin.Palette).Ansi;
                text.Color = text.Error && skin is not null ? skin.ErrorColor with { W = 0.08f } : Vector4.Zero;
                break;
            case NotebookOutputStrip { Error: true } strip:
                strip.Said.Color = skin?.ErrorColor;
                break;
            case NotebookMarkdownTable table:
                foreach (StackPanel[] row in table.Cells)
                {
                    foreach (StackPanel cell in row)
                        cell.Color = skin?.FieldColor ?? Vector4.Zero;
                }
                break;
        }

        for (int i = 0; i < of.Children.Count; i++)
            ColourOutputs(of.Children[i]);
    }

    /* A cell's run, in its gutter */

    private static CellState StateOf(HexNotebook notebook, NotebookCell cell)
    {
        if (notebook.Run.RunningSince(cell) is not null)
            return CellState.Running;
        if (notebook.Run.IsQueued(cell) || notebook.Waiting.Contains(cell))
            return CellState.Queued;
        if (notebook.Run.Failed(cell))
            return CellState.Failed;
        return cell.ExecutionCount is not null || notebook.Run.Took(cell) is not null ? CellState.Done : CellState.None;
    }

    // Helper method for the count, the time and the run button of a code cell, said again only when one of them is
    // another. The seconds of a running cell go up once a second, the text made once a second and not every tick
    private void ShowCellState(HexNotebook notebook, NotebookCellView view, CellState state, long now, in NotebookLook look)
    {
        NotebookCell cell = view.Cell;
        int counted = state is CellState.Queued or CellState.Running ? -2 : cell.ExecutionCount ?? -1;
        long time = state switch
        {
            CellState.Running => (now - notebook.Run.RunningSince(cell)!.Value) / 1000,
            CellState.Done or CellState.Failed => notebook.Run.Took(cell) ?? -1,
            _ => -1
        };
        if (view.CountShown == counted && view.StateShown == (int)state && view.TimeShown == time)
            return;

        if (view.CountShown != counted)
            view.Count.Text = counted == -2 ? "[*]" : counted >= 0 ? $"[{counted}]" : "[ ]";

        view.Took.Text = state switch
        {
            CellState.Queued => Said("notebook.queued"),
            CellState.Running => Elapsed(time * 1000, running: true),
            CellState.Done or CellState.Failed when time >= 0 => Elapsed(time, running: false),
            CellState.Failed => Said("notebook.failed"),
            _ => string.Empty
        };

        if (view.StateShown != (int)state)
        {
            Vector4? colour = state switch
            {
                CellState.Running => look.Accent,
                CellState.Failed => look.Error,
                _ => null
            };
            view.Count.Color = colour;
            view.Count.Dim = colour is null;
            view.Took.Color = colour;
            view.Took.Dim = colour is null;
            view.RunButton.Icon = state switch
            {
                CellState.Running => "debug-stop",
                CellState.Queued => "loading",
                _ => "play"
            };
            view.RunButton.IconColor = state == CellState.Running ? look.Accent : null;
            view.RunButton.Tooltip = Said(state == CellState.Running ? "notebook.stop_cell_tip" : "notebook.run_cell_tip");
        }

        view.CountShown = counted;
        view.StateShown = (int)state;
        view.TimeShown = time;
    }

    /// <summary>A time the way the gutter says it, milliseconds under a second, tenths of a second under a minute, then minutes.</summary>
    private string Elapsed(long milliseconds, bool running)
    {
        if (!running && milliseconds < 1000)
            return Said("notebook.took_ms", ("ms", milliseconds.ToString(System.Globalization.CultureInfo.CurrentCulture)));
        if (milliseconds < 60_000)
        {
            string seconds = running ? (milliseconds / 1000).ToString(System.Globalization.CultureInfo.CurrentCulture)
                : (milliseconds / 1000.0).ToString("0.0", System.Globalization.CultureInfo.CurrentCulture);
            return Said("notebook.took_s", ("s", seconds));
        }

        long whole = milliseconds / 1000;
        return Said("notebook.took_min", ("m", (whole / 60).ToString(System.Globalization.CultureInfo.CurrentCulture)),
            ("s", (whole % 60).ToString(System.Globalization.CultureInfo.CurrentCulture)));
    }

    /* Code and outputs hidden, as Jupyter writes it into the cell's metadata */

    private static bool MetadataSays(NotebookCell cell, string key) =>
        cell.Metadata["jupyter"] is JsonObject jupyter && jupyter[key] is JsonValue value && value.TryGetValue(out bool said) && said;

    private static bool SourceHidden(NotebookCell cell) => MetadataSays(cell, "source_hidden");

    private static bool OutputsHidden(NotebookCell cell) =>
        MetadataSays(cell, "outputs_hidden") || cell.Metadata["collapsed"] is JsonValue value && value.TryGetValue(out bool collapsed) && collapsed;

    /// <summary>A cell's code hidden behind a line that says so, or shown again.</summary>
    private void HideSource(HexNotebook notebook, NotebookCell cell, bool hide)
    {
        if (SourceHidden(cell) == hide)
            return;
        SayInMetadata(cell, "source_hidden", hide);
        NotebookChanged(notebook, now: true);
    }

    /// <summary>A cell's outputs hidden behind a line that says so, or shown again. Classic Jupyter's collapsed written with it.</summary>
    private void HideOutputs(HexNotebook notebook, NotebookCell cell, bool hide)
    {
        if (OutputsHidden(cell) == hide)
            return;
        SayInMetadata(cell, "outputs_hidden", hide);
        if (hide)
            cell.Metadata["collapsed"] = true;
        else
            cell.Metadata.Remove("collapsed");
        NotebookChanged(notebook, now: true);
    }

    // Helper method for a flag under the metadata's jupyter, taken out again rather than written false so a notebook
    // that's only had something hidden and shown again is as it was
    private static void SayInMetadata(NotebookCell cell, string key, bool on)
    {
        if (cell.Metadata["jupyter"] is not JsonObject jupyter)
        {
            if (!on)
                return;
            cell.Metadata["jupyter"] = jupyter = [];
        }

        if (on)
            jupyter[key] = true;
        else
            jupyter.Remove(key);
        if (jupyter.Count == 0)
            cell.Metadata.Remove("jupyter");
    }

    /// <summary>Everything a cell put out as text (what it printed, its result's text, an error's traceback), its colours left out.</summary>
    private static string OutputsText(NotebookCell cell)
    {
        var text = new System.Text.StringBuilder();
        foreach (JsonObject output in cell.Outputs)
        {
            string? said = output["output_type"]?.GetValue<string>() switch
            {
                "stream" => NotebookFile.Text(output["text"]),
                "error" => output["traceback"] is JsonArray lines
                    ? string.Join('\n', lines.Select(line => line?.GetValue<string>() ?? string.Empty))
                    : $"{output["ename"]?.GetValue<string>()}: {output["evalue"]?.GetValue<string>()}",
                "execute_result" or "display_data" => output["data"]?["text/plain"] is { } plain ? NotebookFile.Text(plain) : null,
                _ => null
            };
            if (said is null)
                continue;
            if (text.Length > 0 && text[^1] != '\n')
                text.Append('\n');
            text.Append(AnsiText.Read(said).Item1);
        }

        return text.ToString();
    }

    private void CopyOutputs(HexNotebook notebook)
    {
        if (notebook.Cell is not { } cell)
            return;
        UIClipboard.Copy(OutputsText(cell));
        Say(Said("notebook.output_copied"));
    }

    /* The tools over the picked cell's corner, one row of them moved to whichever cell's picked */

    private StackPanel cellTools = null!;
    private Button toolRunAbove = null!, toolRunBelow = null!, toolUp = null!, toolDown = null!, toolAddCode = null!;
    private Button toolKind = null!, toolHideOutput = null!, toolCopyOutput = null!, toolClearOutput = null!, toolDelete = null!;
    private Divider toolsRunDivider = null!, toolsOutputDivider = null!;
    private NotebookCellView? toolsOn;
    private bool? toolsCompact;

    private void BuildCellTools()
    {
        cellTools = new StackPanel
        {
            Direction = UIDirection.Horizontal,
            Anchor = Origin.TopRight,
            Pivot = Origin.TopRight,
            Position = new Vector2(-10.0f, -6.0f),
            Padding = new UIEdges(2.0f),
            Spacing = 1.0f,
            Radius = 5.0f
        };

        Button Tool(string icon, string tip, Action<HexNotebook> pressed)
        {
            var button = cellTools.Add(new Button { Icon = icon, Tooltip = Said(tip), Size = new Vector2(24.0f, 22.0f) });
            button.OnPressed = () =>
            {
                if (HasNotebook)
                    pressed(notebookShown!);
            };
            return button;
        }

        Divider Gap() => cellTools.Add(new Divider { Size = new Vector2(1.0f, 14.0f) });

        toolRunAbove = Tool("run-above", "notebook.run_above_tip", notebook => RunCells(notebook, notebook.Model.Cells.Take(notebook.Selected)));
        toolRunBelow = Tool("run-below", "notebook.run_below_tip", notebook => RunCells(notebook, notebook.Model.Cells.Skip(notebook.Selected)));
        toolsRunDivider = Gap();
        toolUp = Tool("arrow-up", "notebook.move_up_tip", notebook => MoveCell(notebook, -1));
        toolDown = Tool("arrow-down", "notebook.move_down_tip", notebook => MoveCell(notebook, 1));
        toolAddCode = Tool("add", "notebook.add_code_tip", notebook => AddCell(notebook, CellKind.Code, below: true, edit: false));
        toolKind = Tool("markdown", "notebook.to_markdown_tip", notebook => ChangeCell(notebook, notebook.Cell?.Kind == CellKind.Code ? CellKind.Markdown : CellKind.Code));
        toolsOutputDivider = Gap();
        toolHideOutput = Tool("fold", "notebook.hide_output_tip", notebook =>
        {
            if (notebook.Cell is { } cell)
                HideOutputs(notebook, cell, !OutputsHidden(cell));
        });
        toolCopyOutput = Tool("copy", "notebook.copy_output_tip", CopyOutputs);
        toolClearOutput = Tool("clear-all", "notebook.clear_output_tip", notebook => ClearOutputs(notebook, [notebook.Cell!]));
        Gap();
        toolDelete = Tool("trash", "notebook.delete_cell_tip", DeleteCell);
    }

    // Every tick, the tools on the picked cell while cells are being picked (not while one's typed into, its corner is
    // the code's then), what they offer as it is for that cell
    private void UpdateCellTools(HexNotebook notebook)
    {
        NotebookCellView? on = !notebook.Editing && notebook.Selected < cellViews.Count ? cellViews[notebook.Selected] : null;
        if (on != toolsOn)
        {
            cellTools.Parent?.Remove(cellTools);
            on?.Card.Add(cellTools);
            toolsOn = on;
        }

        if (on is null)
            return;

        bool code = on.Cell.Kind == CellKind.Code;
        bool outputs = code && on.Cell.Outputs.Count > 0;
        toolUp.Enabled = notebook.Selected > 0;
        toolDown.Enabled = notebook.Selected < cellViews.Count - 1;
        toolRunAbove.Enabled = notebook.Selected > 0;
        string kind = code ? "markdown" : "code";
        if (toolKind.Icon != kind)
        {
            toolKind.Icon = kind;
            toolKind.Tooltip = Said(code ? "notebook.to_markdown_tip" : "notebook.to_code_tip");
        }

        toolsOutputDivider.Visible = outputs;
        toolHideOutput.Visible = outputs;
        toolCopyOutput.Visible = outputs;
        toolClearOutput.Visible = outputs;
        if (outputs)
        {
            bool hidden = OutputsHidden(on.Cell);
            string fold = hidden ? "unfold" : "fold";
            if (toolHideOutput.Icon != fold)
            {
                toolHideOutput.Icon = fold;
                toolHideOutput.Tooltip = Said(hidden ? "notebook.show_output_tip" : "notebook.hide_output_tip");
            }
        }
    }

    // Helper method for the tools of a narrow cell, the ones that are only quicker ways of the bar's left out
    private void FitCellTools(float width)
    {
        bool compact = width < TOOLS_COMPACT;
        if (compact == toolsCompact)
            return;

        toolsCompact = compact;
        toolRunAbove.Visible = !compact;
        toolRunBelow.Visible = !compact;
        toolsRunDivider.Visible = !compact;
    }

    /* What an empty notebook says, under its one cell */

    private Label notebookHint = null!;
    private StackPanel notebookHintRow = null!;

    private void UpdateNotebookHint(HexNotebook notebook)
    {
        bool empty = notebook.Model.Cells.Count == 1 && notebook.Model.Cells[0] is { Kind: CellKind.Code, Source.Length: 0, Outputs.Count: 0 };
        if (notebookHint.Visible != empty)
        {
            notebookHint.Visible = empty;
            notebookHintRow.Visible = empty;
        }
    }

    /* The bar's right, how far a run's got, the cell picked and the kernel */

    private StackPanel notebookStatus = null!;
    private Label runProgress = null!, notebookMode = null!;
    private Button interruptButton = null!, restartButton = null!, clearButton = null!, runAllButton = null!, addCodeButton = null!, addMarkdownButton = null!;
    private (int Done, int Total) progressShown = (-1, -1);
    private (int Selected, int Count, bool Editing) modeShown = (-1, -1, false);
    private int barShown = -1;

    // Every tick, the bar's words, each made again only when what it says is another
    private void UpdateNotebookStatus(HexNotebook notebook)
    {
        int left = notebook.Run.Queued.Count + notebook.Waiting.Count;
        (int, int) progress = left > 0 && notebook.RunTotal > 1 ? (notebook.RunTotal - left + 1, notebook.RunTotal) : (0, 0);
        if (progress != progressShown)
        {
            progressShown = progress;
            runProgress.Visible = progress.Item2 > 0;
            if (runProgress.Visible)
                runProgress.Text = Said("notebook.running_of", ("n", progress.Item1.ToString(System.Globalization.CultureInfo.CurrentCulture)),
                    ("count", progress.Item2.ToString(System.Globalization.CultureInfo.CurrentCulture)));
        }

        var mode = (notebook.Selected, notebook.Model.Cells.Count, notebook.Editing);
        if (mode != modeShown)
        {
            modeShown = mode;
            notebookMode.Text = Said(notebook.Editing ? "notebook.mode_edit" : "notebook.mode_command",
                ("n", (notebook.Selected + 1).ToString(System.Globalization.CultureInfo.CurrentCulture)),
                ("count", notebook.Model.Cells.Count.ToString(System.Globalization.CultureInfo.CurrentCulture)));
            notebookMode.Tooltip = Said(notebook.Editing ? "notebook.mode_edit_tip" : "notebook.mode_command_tip");
        }

        interruptButton.Enabled = notebook.Kernel?.State == KernelState.Busy || left > 0;
    }

    // Helper method for the bar as wide as the view, its buttons only their icons once the words don't fit, and the cell
    // picked left out when even that's too narrow
    private void FitNotebookBar(float width)
    {
        int fit = width >= BAR_COMPACT ? 0 : width >= BAR_TIGHT ? 1 : 2;
        if (fit == barShown)
            return;

        barShown = fit;
        interruptButton.Label = fit == 0 ? Said("notebook.interrupt_button") : string.Empty;
        restartButton.Label = fit == 0 ? Said("notebook.restart_button") : string.Empty;
        clearButton.Label = fit == 0 ? Said("notebook.clear_button") : string.Empty;
        runAllButton.Label = fit < 2 ? Said("notebook.run_all") : string.Empty;
        addCodeButton.Label = fit < 2 ? Said("notebook.code_button") : string.Empty;
        addMarkdownButton.Label = fit < 2 ? Said("notebook.markdown_button") : string.Empty;
        notebookMode.Visible = fit < 2;
        kernelShown = default;
    }

    /// <summary>The dot by the kernel's name, the colour of its state.</summary>
    private void ShowKernelState(HexNotebook notebook)
    {
        NotebookLook look = NotebookLookNow();
        bool busy = notebook.Kernel?.State == KernelState.Busy;
        bool starting = notebook.Starting || notebook.Kernel?.State == KernelState.Starting;
        kernelButton.Icon = busy || starting ? "loading~spin" : "circle-filled";
        kernelButton.IconColor = (notebook.Kernel, notebook.Kernel?.State) switch
        {
            _ when starting => look.Warning,
            _ when busy => look.Accent,
            (null, _) => look.Dim,
            (_, KernelState.Idle) => look.Success,
            _ => look.Error
        };
    }
}
