using System.Numerics;

using Hex.Core.Notebooks;

using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;
using Horizon.UI.Scripting;

namespace Hex.Dawn;

/// <summary>A cell of the notebook on screen, its card and what's in it.</summary>
internal sealed class NotebookCellView
{
    public required HexNotebook Notebook { get; init; }
    public required NotebookCell Cell { get; init; }
    public required NotebookCellCard Card { get; init; }
    public required StackPanel Body { get; init; }
    public required StackPanel Outputs { get; init; }
    public required Label Count { get; init; }
    public required Button RunButton { get; init; }
    public required NotebookCellEditor Editor { get; init; }
    public StackPanel? Written { get; set; }

    /// <summary>How long its last run took, or has been running, under the count.</summary>
    public required Label Took { get; init; }

    /// <summary>The row of the outputs with the rail beside them, and the line said in their place while they're hidden.</summary>
    public required StackPanel OutputRow { get; init; }
    public required NotebookRail OutputRail { get; init; }
    public required Button OutputHidden { get; init; }

    /// <summary>The rail beside a code cell's code and the line said in its place while it's hidden, null for markdown.</summary>
    public NotebookRail? InputRail { get; init; }
    public Button? InputHidden { get; init; }

    /// <summary>What the gutter last said, a state (CellState) and the seconds or milliseconds it showed, -1 for none.</summary>
    public int StateShown { get; set; } = -1;
    public long TimeShown { get; set; } = -1;

    /// <summary>Whether a running cell's button showed stop, pointed at, rather than its spinner.</summary>
    public bool StopShown { get; set; }

    /// <summary>A code cell's outlined frame round its editor, null for a markdown cell, whose editor goes without.</summary>
    public StackPanel? Frame { get; set; }

    /// <summary>The source the editor and the written out markdown were last given, another string once it changes.</summary>
    public string? Shown { get; set; }

    /// <summary>The count it says, -1 for never run and -2 for waiting or running, int.MinValue until it's said one.</summary>
    public int CountShown { get; set; } = int.MinValue;

    /// <summary>The outputs as they were last drawn, drawn again once the cell's are something else.</summary>
    public int OutputsDrawn { get; set; } = -1;
}

/// <summary>A cell's card, which picks the cell when it's pressed anywhere its editor and outputs aren't.</summary>
internal sealed class NotebookCellCard : Panel
{
    public Action<bool>? Pressed { get; set; }
    private long pressedAt;

    protected override bool HitTestVisible => true;

    protected override void OnPointerDown(Vector2 point)
    {
        long now = System.Environment.TickCount64;
        Pressed?.Invoke(now - pressedAt < 400);
        pressedAt = now;
    }
}

/// <summary>
/// The thin bar down the left of a cell's code or its outputs, JupyterLab's collapser. It's drawn in the accent on the cell
/// picked (solid while it's typed into), in the theme's error colour beside the outputs of a run that failed, and pressed it
/// hides or shows what it's beside.
/// </summary>
internal sealed class NotebookRail : Panel
{
    public Action? Pressed { get; set; }

    protected override bool HitTestVisible => true;

    protected override void OnPointerDown(Vector2 point) => Pressed?.Invoke();
}

/// <summary>A cell's editor, Escape out of it going back to picking cells (Jupyter's command mode) once it has nothing of its own to close.</summary>
internal sealed class NotebookCellEditor : CodeEditor
{
    public Action? Escaped { get; set; }
    public Action<NotebookCellEditor>? Arranged { get; set; }

    protected override void Arrange(UIRect content)
    {
        base.Arrange(content);
        Arranged?.Invoke(this);
    }

    public NotebookCellEditor(string text) : base(text)
    {
    }

    // The editor keeps the keyboard when Escape closes its prediction or hover, otherwise the notebook takes it.
    protected override bool OnEscape()
    {
        if (HasPrediction || HoverUp)
            return base.OnEscape();

        Escaped?.Invoke();
        return true;
    }
}

// The cells of the notebook on screen, each a card with the count of its run and a button to run it down the left, its
// source in an editor of its own (markdown written out while it isn't being typed into), and what running it came to
// under that. One cell is picked at a time, typed into or not, the way Jupyter has edit and command modes
internal sealed partial class DawnScene
{
    // How wide the column of counts down the left of the cells is, and the rail beside a cell's code and outputs with the
    // gap after it
    private const float CELL_GUTTER = 58.0f;
    private const float CELL_RAIL = 3.0f, CELL_RAIL_GAP = 5.0f;

    private readonly List<NotebookCellView> cellViews = [];

    // The language's extension, for its colours in a cell, by the name a kernel gives it
    private static string ExtensionOfLanguage(string language) => language.ToLowerInvariant() switch
    {
        "python" or "python3" => ".py",
        "julia" => ".jl",
        "r" => ".r",
        "javascript" or "js" or "node" => ".js",
        "typescript" => ".ts",
        "c#" or "csharp" => ".cs",
        "f#" or "fsharp" => ".fs",
        "rust" => ".rs",
        "go" => ".go",
        "c++" or "cpp" or "c++17" or "c++20" => ".cpp",
        "c" => ".c",
        "scala" => ".scala",
        "ruby" => ".rb",
        "kotlin" => ".kt",
        "java" => ".java",
        "bash" or "sh" or "shell" => ".sh",
        "powershell" => ".ps1",
        "sql" => ".sql",
        "haskell" => ".hs",
        "lua" => ".lua",
        "matlab" or "octave" => ".m",
        _ => "." + language.ToLowerInvariant()
    };

    private void ClearCellViews()
    {
        foreach (NotebookCellView view in cellViews)
        {
            ForgetPrediction(CellPredictionTarget(view));
            ForgetOutputs(view);
        }
        cellViews.Clear();
        cellTools.Parent?.Remove(cellTools);
        toolsOn = null;
        foreach (UIComponent child in notebookCells.Children.ToList())
            notebookCells.Remove(child);
    }

    // Helper method for the cells of a notebook made again, all of them, kept as they were picked and typed into
    private void MakeCellViews(HexNotebook notebook)
    {
        notebook.Remake = false;
        bool typing = notebook.Editing;
        ClearCellViews();
        for (int i = 0; i < notebook.Model.Cells.Count; i++)
            cellViews.Add(MakeCellView(notebook, notebook.Model.Cells[i]));
        notebookCells.Add(notebookHintRow);

        if (typing && notebook.Selected < cellViews.Count)
            EditCell(notebook, notebook.Selected);
        ShowCellDiagnostics(notebook);
    }

    private NotebookCellView MakeCellView(HexNotebook notebook, NotebookCell cell)
    {
        var card = notebookCells.Add(new NotebookCellCard { Radius = 6.0f, Padding = new UIEdges(4.0f) });
        var row = card.Add(new StackPanel { Direction = UIDirection.Horizontal, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Spacing = 4.0f });

        var gutter = row.Add(new StackPanel { Direction = UIDirection.Vertical, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Size = new Vector2(CELL_GUTTER, 0.0f), Spacing = 2.0f });
        var run = gutter.Add(new Button { Icon = "play", Size = new Vector2(26.0f, 24.0f), Anchor = Origin.TopRight, Tooltip = Said("notebook.run_cell_tip") });
        var count = gutter.Add(new Label { Anchor = Origin.TopRight, Align = Origin.TopRight, TextScale = 0.125f, Dim = true });
        var took = gutter.Add(new Label { Anchor = Origin.TopRight, Align = Origin.TopRight, TextScale = 0.12f, Dim = true });

        var body = row.Add(new StackPanel { Direction = UIDirection.Vertical, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Spacing = 4.0f });
        // The code in a thin outline of the theme's border colour with the rail beside it, its outputs under it with a rail
        // of their own, so where the code ends and what it put out starts is plain at a glance. A markdown cell's text goes
        // without either
        bool framed = cell.Kind != CellKind.Markdown;
        StackPanel? inputRow = framed ? body.Add(new StackPanel { Direction = UIDirection.Horizontal, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Spacing = CELL_RAIL_GAP }) : null;
        NotebookRail? inputRail = inputRow?.Add(new NotebookRail { Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Size = new Vector2(CELL_RAIL, 0.0f), Radius = CELL_RAIL * 0.5f, Tooltip = Said("notebook.hide_code") });
        StackPanel? frame = inputRow?.Add(new StackPanel
        {
            Direction = UIDirection.Vertical, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Padding = new UIEdges(CELL_FRAME), Radius = 5.0f, Outline = NotebookOutline()
        });
        var editor = (frame ?? body).Add(new NotebookCellEditor(cell.Source) { Arranged = FitNotebookWrap });
        PrepareCellEditor(notebook, editor, cell);
        Button? inputHidden = framed ? body.Add(HiddenLine(Said("notebook.code_hidden"))) : null;

        var outputRow = body.Add(new StackPanel { Direction = UIDirection.Horizontal, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Spacing = CELL_RAIL_GAP, Visible = false });
        var outputRail = outputRow.Add(new NotebookRail { Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Size = new Vector2(CELL_RAIL, 0.0f), Radius = CELL_RAIL * 0.5f, Tooltip = Said("notebook.hide_output") });
        var outputs = outputRow.Add(new StackPanel
        {
            Direction = UIDirection.Vertical, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Spacing = 4.0f,
            Padding = new UIEdges(CELL_OUTPUT_INSET, 2.0f, CELL_OUTPUT_INSET, 2.0f)
        });
        Button outputHidden = body.Add(HiddenLine(Said("notebook.output_hidden")));

        var view = new NotebookCellView
        {
            Notebook = notebook, Cell = cell, Card = card, Body = body, Outputs = outputs, Count = count, RunButton = run, Editor = editor, Frame = frame, Took = took,
            OutputRow = outputRow, OutputRail = outputRail, OutputHidden = outputHidden, InputRail = inputRail, InputHidden = inputHidden
        };
        card.Pressed = twice => PressCell(notebook, view, twice);
        run.OnPressed = () =>
        {
            // A cell that's running is stopped by the same button, the kernel's interrupt
            if (notebook.Run.RunningSince(cell) is not null)
            {
                InterruptKernel(notebook);
                return;
            }

            PickCell(notebook, notebook.Model.Cells.IndexOf(cell), edit: false);
            RunCells(notebook, [cell]);
        };
        if (inputRail is not null && inputHidden is not null)
        {
            inputRail.Pressed = () => HideSource(notebook, cell, hide: true);
            inputHidden.OnPressed = () => HideSource(notebook, cell, hide: false);
        }
        outputRail.Pressed = () => HideOutputs(notebook, cell, hide: true);
        outputHidden.OnPressed = () => HideOutputs(notebook, cell, hide: false);

        if (cell.Kind == CellKind.Markdown)
        {
            view.Written = body.Add(new StackPanel { Direction = UIDirection.Vertical, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Spacing = 4.0f });
            body.MoveChild(view.Written, 0);
        }

        return view;
    }

    // How the code of a cell is filled in, not at all while there's a picture behind the code so it shows through the
    // cells the way it does through the code view. Filled, the cells covered all of it but the gaps between them, and
    // only half filled the gaps were bands of the picture brighter than the rest
    private Vector4 CellEditorColor => backdropPicture is not null
        ? Vector4.Zero
        : compositor.Skin is { } skin ? skin.FieldColor with { W = MathF.Max(skin.FieldColor.W, 0.6f) } : new Vector4(0.05f, 0.06f, 0.08f, 1.0f);

    // Helper method for a cell's editor, the code's own font and size, the language's colours, as tall as its code
    private void PrepareCellEditor(HexNotebook notebook, NotebookCellEditor editor, NotebookCell cell)
    {
        editor.TextScale = CODE_TEXT * settings.CodeZoom;
        editor.OnZoom = delta => ZoomCode(delta > 0 ? CODE_ZOOM_STEP : 1 / CODE_ZOOM_STEP);
        editor.Font = codeEditing.codeFont;
        editor.Padding = new UIEdges(6.0f, 5.0f, 6.0f, 5.0f);
        editor.LineNumbers = false;
        editor.Minimap = false;
        editor.StickyScroll = false;
        editor.CheckSyntax = false;
        editor.WordWrap = settings.WrapCode;
        editor.CaretGlide = CaretGlide;
        editor.FitRows = 400;
        editor.Anchor = Origin.TopLeft;
        editor.Pivot = Origin.TopLeft;
        editor.Color = CellEditorColor;
        editor.Highlighter = cell.Kind switch
        {
            CellKind.Markdown => CodeLanguages.ForExtension(".md"),
            CellKind.Raw => null,
            _ => CodeLanguages.ForExtension(ExtensionOfLanguage(notebook.Model.Language))
        };
        var prediction = new PredictionTarget(notebook.Document, editor, notebook, cell);
        if (predictionSession is not null && cell.Kind != CellKind.Raw)
            PreparePrediction(prediction);
        editor.OnChanged = () =>
        {
            string source = editor.Text;
            if (source == cell.Source)
                return;
            cell.Source = source;
            NotebookChanged(notebook);
            PredictionEdited(prediction);
        };
        editor.Escaped = () => StopEditing(notebook);
        if (cell.Kind == CellKind.Code)
        {
            // The language's server where there's one, what it knows of the code without anything having run, the
            // kernel's (what's in its memory) otherwise
            editor.CompletionSource = asked => languages.NotebookServed(notebook)
                ? languages.NotebookCompletions(notebook, notebook.Model.Cells.IndexOf(cell), asked) ?? KernelCompletions(notebook, asked)
                : KernelCompletions(notebook, asked);
            editor.HoverSource = (asked, at) => KernelHover(notebook, asked, at);
        }
    }

    // A press on a cell's card, it's picked, and a markdown cell pressed twice is typed into
    private void PressCell(HexNotebook notebook, NotebookCellView view, bool twice)
    {
        int index = cellViews.IndexOf(view);
        if (index < 0)
            return;

        PickCell(notebook, index, edit: twice && view.Cell.Kind == CellKind.Markdown);
    }

    /// <summary>A cell picked, typed into or not, scrolled into sight.</summary>
    private void PickCell(HexNotebook notebook, int index, bool edit)
    {
        if (notebook.Model.Cells.Count == 0)
            return;

        notebook.Selected = Math.Clamp(index, 0, notebook.Model.Cells.Count - 1);
        if (edit)
        {
            EditCell(notebook, notebook.Selected);
            return;
        }

        StopEditing(notebook);
        if (notebook.Selected < cellViews.Count)
            notebookScroll.ScrollIntoView(cellViews[notebook.Selected].Card);
    }

    /// <summary>A cell typed into, its editor given the keyboard, a markdown cell shown as its source while it is.</summary>
    private void EditCell(HexNotebook notebook, int index)
    {
        if (index < 0 || index >= cellViews.Count)
            return;

        notebook.Selected = index;
        notebook.Editing = true;
        NotebookCellView view = cellViews[index];
        view.Editor.Visible = true;
        view.Editor.Focus();
        notebookScroll.ScrollIntoView(view.Card);
    }

    /// <summary>Out of typing into the cell and back to picking cells, the keys go to the notebook's commands then.</summary>
    private void StopEditing(HexNotebook notebook)
    {
        notebook.Editing = false;
        foreach (NotebookCellView view in cellViews)
        {
            if (view.Editor.IsFocused)
                view.Editor.Unfocus();
        }
    }

    // Every tick, the cells as they are now, which one's picked and typed into, the counts and markdown written out
    private void UpdateCellViews(HexNotebook notebook)
    {
        // Whichever cell has the keyboard is the one picked, typed into
        for (int i = 0; i < cellViews.Count; i++)
        {
            if (cellViews[i].Editor.IsFocused)
            {
                notebook.Selected = i;
                notebook.Editing = true;
            }
        }

        if (notebook.Editing && (notebook.Selected >= cellViews.Count || !cellViews[notebook.Selected].Editor.IsFocused) && compositor.Focus is not null)
            notebook.Editing = false;

        NotebookLook look = NotebookLookNow();
        long now = System.Environment.TickCount64;
        for (int i = 0; i < cellViews.Count; i++)
        {
            NotebookCellView view = cellViews[i];
            NotebookCell cell = view.Cell;
            bool selected = i == notebook.Selected;
            bool editing = selected && notebook.Editing;

            // The word under the caret lit down a cell only while it's typed into, every cell's caret sits at its start
            view.Editor.LightOccurrences = view.Editor.IsFocused;

            bool code = cell.Kind == CellKind.Code;
            view.RunButton.Visible = code;
            view.Count.Visible = code;
            view.Took.Visible = code;
            CellState state = code ? StateOf(notebook, cell) : CellState.None;
            if (code)
                ShowCellState(notebook, view, state, now, look);

            // The source as it is now, from its editor as it's typed or from outside it (moved, made again from the JSON),
            // looked at only when it's another string, its text made every tick was every cell's whole source
            if (!ReferenceEquals(view.Shown, cell.Source))
            {
                if (!view.Editor.IsFocused)
                    view.Editor.Text = cell.Source;
                view.Shown = cell.Source;
                if (view.Written is { } markdown)
                {
                    List<NotebookPicture> previous = markdownPictures.TryGetValue(view, out var old) ? old : [];
                    var pictures = new List<NotebookPicture>();
                    markdownPictures[view] = pictures;
                    WriteMarkdown(markdown, cell.Source.Length > 0 ? cell.Source : Said("notebook.empty_markdown"), notebook.Model.Language, pictures, cell.Rest["attachments"] as System.Text.Json.Nodes.JsonObject, tools: false);
                    ReleaseNotebookPictures(previous);
                }
            }

            // A markdown cell is written out unless it's being typed into, or empty
            if (view.Written is { } written)
            {
                view.Editor.Visible = editing || cell.Source.Length == 0 && selected;
                written.Visible = !view.Editor.Visible;
            }

            if (view.OutputsDrawn != OutputsSignature(cell, notebook.Run.Input))
                DrawOutputs(notebook, view);

            // Code and outputs hidden or shown as the cell's metadata says (Jupyter's source_hidden and outputs_hidden). A
            // cell typed into always shows its code
            bool sourceHidden = view.InputHidden is not null && !editing && SourceHidden(cell);
            bool outputsHidden = OutputsHidden(cell);
            bool hasOutputs = view.Outputs.Children.Count > 0;
            if (view.Frame is { } frame)
            {
                frame.Visible = !sourceHidden;
                view.InputRail!.Visible = !sourceHidden;
                view.InputHidden!.Visible = sourceHidden;
            }
            view.OutputRow.Visible = hasOutputs && !outputsHidden;
            view.OutputHidden.Visible = hasOutputs && outputsHidden;

            // The picked cell lifted a little off the page and its rails in the accent, solid while it's typed into. The
            // outputs of a run that failed keep the error colour's rail whether it's picked or not, so it's found scrolling
            view.Card.Color = !selected ? Vector4.Zero : editing ? look.Typing : look.Picked;
            Vector4 rail = editing ? look.Accent : selected ? look.AccentSoft : Vector4.Zero;
            if (view.InputRail is { } input)
                input.Color = rail;
            view.OutputRail.Color = state == CellState.Failed ? look.Error : rail;
        }

        UpdateCellTools(notebook);
        UpdateNotebookHint(notebook);
    }

    // How far in from its outline a code cell's editor sits, and a cell's outputs from their rail
    private const float CELL_FRAME = 2.0f, CELL_OUTPUT_INSET = 6.0f;

    /// <summary>The outline round a cell's code, the theme's border a touch stronger so it shows on the page.</summary>
    private Vector4 NotebookOutline()
    {
        Vector4 border = compositor.Skin?.BorderColor ?? Vector4.Zero;
        return border with { W = MathF.Min(1.0f, border.W * 1.6f + 0.08f) };
    }

    // Helper method for the line said in place of a cell's hidden code or outputs, pressed it shows them again
    private Button HiddenLine(string said) => new()
    {
        Icon = "unfold",
        Label = said,
        Tooltip = said,
        Align = Origin.Left,
        LabelScale = 0.125f,
        Anchor = Origin.TopLeft,
        Pivot = Origin.TopLeft,
        Size = new Vector2(0.0f, 24.0f),
        Visible = false
    };

    // Helper method for every cell as wide as the notebook, less its column of counts
    private void FitCellViews(float width)
    {
        float body = MathF.Max(120.0f, width - CELL_GUTTER - 12.0f);
        float inner = body - CELL_RAIL - CELL_RAIL_GAP;
        foreach (NotebookCellView view in cellViews)
        {
            view.Card.Size = view.Card.Size with { X = width };
            view.Body.Size = view.Body.Size with { X = body };
            if (view.Frame is { } frame)
            {
                frame.Size = frame.Size with { X = inner };
                view.InputRail!.Size = new Vector2(CELL_RAIL, frame.Size.Y);
            }
            view.Editor.Size = view.Editor.Size with { X = view.Frame is null ? body : inner - CELL_FRAME * 2.0f };
            if (view.Written is { } written)
                FitWritten(written, body, settings.CodeZoom);
            view.Outputs.Size = view.Outputs.Size with { X = inner };
            view.OutputRail.Size = new Vector2(CELL_RAIL, view.Outputs.Size.Y);
            FitOutputs(view, inner - CELL_OUTPUT_INSET * 2.0f, settings.CodeZoom);
        }

        FitCellTools(body);
    }
}
