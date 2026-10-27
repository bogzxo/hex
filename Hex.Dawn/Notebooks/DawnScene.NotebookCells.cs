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
    public required NotebookCell Cell { get; init; }
    public required NotebookCellCard Card { get; init; }
    public required StackPanel Body { get; init; }
    public required StackPanel Outputs { get; init; }
    public required Label Count { get; init; }
    public required Button RunButton { get; init; }
    public required NotebookCellEditor Editor { get; init; }
    public StackPanel? Written { get; set; }

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

/// <summary>A cell's editor, Escape out of it going back to picking cells (Jupyter's command mode) once it has nothing of its own to close.</summary>
internal sealed class NotebookCellEditor : CodeEditor
{
    public Action? Escaped { get; set; }

    public NotebookCellEditor(string text) : base(text)
    {
    }

    // The editor's own Escape keeps the keyboard however it goes, so only its hover is left to it
    protected override bool OnEscape()
    {
        if (HoverUp)
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
    // How wide the column of counts down the left of the cells is
    private const float CELL_GUTTER = 58.0f;

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
            ForgetOutputs(view);
        cellViews.Clear();
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

        if (typing && notebook.Selected < cellViews.Count)
            EditCell(notebook, notebook.Selected);
    }

    private NotebookCellView MakeCellView(HexNotebook notebook, NotebookCell cell)
    {
        var card = notebookCells.Add(new NotebookCellCard { Radius = 6.0f, Padding = new UIEdges(4.0f) });
        var row = card.Add(new StackPanel { Direction = UIDirection.Horizontal, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Spacing = 4.0f });

        var gutter = row.Add(new StackPanel { Direction = UIDirection.Vertical, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Size = new Vector2(CELL_GUTTER, 0.0f), Spacing = 2.0f });
        var run = gutter.Add(new Button { Icon = "play", Size = new Vector2(26.0f, 24.0f), Anchor = Origin.TopRight, Tooltip = Said("notebook.run_cell") });
        var count = gutter.Add(new Label { Anchor = Origin.TopRight, Align = Origin.TopRight, TextScale = 0.125f, Dim = true });

        var body = row.Add(new StackPanel { Direction = UIDirection.Vertical, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Spacing = 4.0f });
        var editor = body.Add(new NotebookCellEditor(cell.Source));
        PrepareCellEditor(notebook, editor, cell);
        var outputs = body.Add(new StackPanel { Direction = UIDirection.Vertical, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Spacing = 2.0f });

        var view = new NotebookCellView { Cell = cell, Card = card, Body = body, Outputs = outputs, Count = count, RunButton = run, Editor = editor };
        card.Pressed = twice => PressCell(notebook, view, twice);
        run.OnPressed = () =>
        {
            PickCell(notebook, notebook.Model.Cells.IndexOf(cell), edit: false);
            RunCells(notebook, [cell]);
        };

        if (cell.Kind == CellKind.Markdown)
        {
            view.Written = body.Add(new StackPanel { Direction = UIDirection.Vertical, Anchor = Origin.TopLeft, Pivot = Origin.TopLeft, Spacing = 4.0f });
            body.MoveChild(view.Written, 0);
        }

        return view;
    }

    // Helper method for a cell's editor, the code's own font and size, the language's colours, as tall as its code
    private void PrepareCellEditor(HexNotebook notebook, NotebookCellEditor editor, NotebookCell cell)
    {
        editor.TextScale = CODE_TEXT * settings.CodeZoom;
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
        editor.Color = compositor.Skin is { } skin ? skin.FieldColor with { W = MathF.Max(skin.FieldColor.W, 0.6f) } : new Vector4(0.05f, 0.06f, 0.08f, 1.0f);
        editor.Highlighter = cell.Kind switch
        {
            CellKind.Markdown => CodeLanguages.ForExtension(".md"),
            CellKind.Raw => null,
            _ => CodeLanguages.ForExtension(ExtensionOfLanguage(notebook.Model.Language))
        };
        editor.OnChanged = () =>
        {
            if (editor.Text == cell.Source)
                return;
            cell.Source = editor.Text;
            NotebookChanged(notebook);
        };
        editor.Escaped = () => StopEditing(notebook);
        if (cell.Kind == CellKind.Code)
        {
            editor.CompletionSource = asked => KernelCompletions(notebook, asked);
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

        Vector4 plain = compositor.Skin is { } skin ? skin.PanelColor with { W = 0.0f } : Vector4.Zero;
        Vector4 picked = compositor.Skin is { } lit ? lit.HoverColor with { W = MathF.Max(lit.HoverColor.W, 0.18f) } : new Vector4(1.0f, 1.0f, 1.0f, 0.06f);
        Vector4 typing = compositor.Skin is { } accent ? accent.HighlightColor with { W = 0.12f } : new Vector4(0.3f, 0.5f, 1.0f, 0.12f);

        for (int i = 0; i < cellViews.Count; i++)
        {
            NotebookCellView view = cellViews[i];
            NotebookCell cell = view.Cell;
            bool selected = i == notebook.Selected;
            view.Card.Color = !selected ? plain : notebook.Editing ? typing : picked;

            // The word under the caret lit down a cell only while it's typed into, every cell's caret sits at its start
            view.Editor.LightOccurrences = view.Editor.IsFocused;

            bool code = cell.Kind == CellKind.Code;
            view.RunButton.Visible = code;
            view.Count.Visible = code;
            if (code)
            {
                // Written only when it's another, a count made into text every tick for every cell was garbage for nothing
                bool queued = notebook.Run.IsQueued(cell) || notebook.Waiting.Contains(cell);
                int counted = queued ? -2 : cell.ExecutionCount ?? -1;
                if (view.CountShown != counted)
                {
                    view.CountShown = counted;
                    view.Count.Text = queued ? "[*]" : counted >= 0 ? $"[{counted}]" : "[ ]";
                    view.RunButton.Icon = queued ? "loading" : "play";
                }
            }

            // The source as it is now, from its editor as it's typed or from outside it (moved, made again from the JSON),
            // looked at only when it's another string, its text made every tick was every cell's whole source
            if (!ReferenceEquals(view.Shown, cell.Source))
            {
                if (!view.Editor.IsFocused)
                    view.Editor.Text = cell.Source;
                view.Shown = cell.Source;
                if (view.Written is { } markdown)
                    WriteMarkdown(markdown, cell.Source.Length > 0 ? cell.Source : Said("notebook.empty_markdown"), notebook.Model.Language);
            }

            // A markdown cell is written out unless it's being typed into, or empty
            if (view.Written is { } written)
            {
                bool editing = selected && notebook.Editing;
                view.Editor.Visible = editing || cell.Source.Length == 0 && selected;
                written.Visible = !view.Editor.Visible;
            }

            if (view.OutputsDrawn != OutputsSignature(cell, notebook.Run.Input))
                DrawOutputs(notebook, view);
        }
    }

    // Helper method for every cell as wide as the notebook, less its column of counts
    private void FitCellViews(float width)
    {
        float body = MathF.Max(120.0f, width - CELL_GUTTER - 12.0f);
        foreach (NotebookCellView view in cellViews)
        {
            view.Card.Size = view.Card.Size with { X = width };
            view.Body.Size = view.Body.Size with { X = body };
            view.Editor.Size = view.Editor.Size with { X = body };
            if (view.Written is { } written)
                FitWritten(written, body);
            FitOutputs(view, body);
        }
    }
}
