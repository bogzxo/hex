using Hex.Core.Notebooks;

using Key = Silk.NET.Input.Key;
using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The notebook's commands, in a Notebook menu that's only on the bar while a notebook's cells are on screen (its keys first
// then), and the bar over the cells. Jupyter's keys. Shift+Enter runs the cell and goes to the next, Ctrl+Enter runs it and
// stays, Alt+Enter runs it and puts a new one under it, typing or not. Out of a cell (Escape) A and B put a cell above and
// below, D D deletes it, M, Y and R make it markdown, code or raw, Enter types into it, the arrows go up and down, I I
// interrupts the kernel and 0 0 restarts it
internal sealed partial class DawnScene
{
    // The notebook on screen, for a command that does nothing without one
    private bool HasNotebook => NotebookShown && notebookShown!.Unreadable is null;

    /// <summary>A new notebook, saved where it's asked to be and opened on its one empty code cell.</summary>
    private void NewNotebook()
    {
        string directory = explorer.workspace.IsEmpty ? browseDirectory : explorer.workspace.Folders[0];
        Ask(HexDialogs.SaveFile(directory, "notebook.ipynb", [(Said("dialogs.files_of_kind", ("kind", ".ipynb")), "*.ipynb")], DialogHost), path =>
        {
            if (!IsNotebookFile(path))
                path += ".ipynb";
            // The kernel it'll run with said in it from the start, as Jupyter's new notebooks have. Saved before a cell
            // ran, it said nothing about what it was for
            Notebook made = Notebook.Empty();
            if (KernelSpecs.For(KernelSpecs.Find(), null, "python") is { } spec)
                made.UseKernel(spec, []);
            File.WriteAllText(path, NotebookFile.Write(made));
            Open(path);
        });
    }

    private void AddNotebookCommands()
    {
        commands.Add("File/new", Said("notebook.new"), NewNotebook, icon: "notebook");
        commands.ShowMenuWhen("Notebook", () => NotebookShown);
        commands.DeclareSubmenu("Notebook/kernel", "kernel", Said("notebook.pick_kernel_title"), "server-process", "run", "pick");

        void Add(string where, string label, Action<HexNotebook> picked, string key = "", string icon = "", UIKeyUse use = UIKeyUse.Bound, Func<HexNotebook, bool>? can = null)
        {
            var item = commands.Add(where, Said(label), () =>
            {
                if (HasNotebook)
                    picked(notebookShown!);
            }, key, icon, key.Length > 0 ? use : UIKeyUse.Shown);
            item.IsEnabled = () => HasNotebook && (can?.Invoke(notebookShown!) ?? true);
        }

        bool Picked(HexNotebook notebook) => notebook.Cell is not null;

        Add("Notebook/run", "notebook.run_cell", notebook => RunPicked(notebook, then: 0), "Ctrl+Enter", "play", UIKeyUse.BoundWhileTyping, Picked);
        Add("Notebook/run", "notebook.run_and_next", notebook => RunPicked(notebook, then: 1), "Shift+Enter", "run-below", UIKeyUse.BoundWhileTyping, Picked);
        Add("Notebook/run", "notebook.run_and_insert", notebook => RunPicked(notebook, then: 2), "Alt+Enter", "add", UIKeyUse.BoundWhileTyping, Picked);
        Add("Notebook/run", "notebook.run_all", notebook => RunCells(notebook, notebook.Model.Cells), icon: "run-all");
        Add("Notebook/run", "notebook.run_above", notebook => RunCells(notebook, notebook.Model.Cells.Take(notebook.Selected)), icon: "run-above", can: Picked);
        Add("Notebook/run", "notebook.run_below", notebook => RunCells(notebook, notebook.Model.Cells.Skip(notebook.Selected)), icon: "run-below", can: Picked);

        Add("Notebook/cells", "notebook.add_code", notebook => AddCell(notebook, CellKind.Code, below: true, edit: false), "B", "add");
        Add("Notebook/cells", "notebook.add_code_above", notebook => AddCell(notebook, CellKind.Code, below: false, edit: false), "A", "add");
        Add("Notebook/cells", "notebook.add_markdown", notebook => AddCell(notebook, CellKind.Markdown, below: true), icon: "markdown");
        Add("Notebook/cells", "notebook.delete_cell", DeleteCell, "D D", "trash", can: Picked);
        Add("Notebook/cells", "notebook.move_up", notebook => MoveCell(notebook, -1), "Ctrl+Shift+Up", "arrow-up", UIKeyUse.BoundWhileTyping, notebook => notebook.Selected > 0);
        Add("Notebook/cells", "notebook.move_down", notebook => MoveCell(notebook, 1), "Ctrl+Shift+Down", "arrow-down", UIKeyUse.BoundWhileTyping, notebook => notebook.Selected < notebook.Model.Cells.Count - 1);
        Add("Notebook/cells", "notebook.to_code", notebook => ChangeCell(notebook, CellKind.Code), "Y", "code", can: Picked);
        Add("Notebook/cells", "notebook.to_markdown", notebook => ChangeCell(notebook, CellKind.Markdown), "M", "markdown", can: Picked);
        Add("Notebook/cells", "notebook.to_raw", notebook => ChangeCell(notebook, CellKind.Raw), "R", "file", can: Picked);

        Add("Notebook/clear", "notebook.clear_outputs", notebook => ClearOutputs(notebook, [notebook.Cell!]), icon: "clear-all", can: Picked);
        Add("Notebook/clear", "notebook.clear_all", notebook => ClearOutputs(notebook, notebook.Model.Cells), icon: "clear-all");

        Add("Notebook/kernel/kernel:run", "notebook.interrupt", InterruptKernel, "I I", "debug-pause", can: notebook => notebook.Kernel is not null);
        Add("Notebook/kernel/kernel:run", "notebook.restart", notebook => RestartKernel(notebook), "0 0", "debug-restart");
        Add("Notebook/kernel/kernel:run", "notebook.restart_run_all", notebook => RestartKernel(notebook, runAll: true), icon: "debug-restart");
        Add("Notebook/kernel/kernel:run", "notebook.shutdown", StopKernel, icon: "debug-stop", can: notebook => notebook.Kernel is not null);
        Add("Notebook/kernel/kernel:pick", "notebook.pick_kernel", PickKernel, icon: "server-process");

        // Keys out of a cell that aren't in the menu, typing into it and going up and down
        Add("Notebook/cells", "notebook.edit_cell", notebook => EditCell(notebook, notebook.Selected), "Enter", "edit", can: notebook => !notebook.Editing && Picked(notebook));
        commands.Bind(Key.Up, () => StepCell(-1));
        commands.Bind(Key.Down, () => StepCell(1));
        commands.Bind(Key.K, () => StepCell(-1));
        commands.Bind(Key.J, () => StepCell(1));

        // Z out of a cell undoes, a cell taken out with D D put back, Jupyter's undo of a cell's deletion
        commands.Bind(Key.Z, () =>
        {
            if (HasNotebook && !notebookShown!.Editing)
                Undo();
        });
    }

    // An arrow out of a cell, the one above or below picked
    private void StepCell(int by)
    {
        if (HasNotebook && !notebookShown!.Editing)
            PickCell(notebookShown, notebookShown.Selected + by, edit: false);
    }

    // Helper method for the picked cell run, then the next picked (1), a new one put under it (2) or neither (0)
    private void RunPicked(HexNotebook notebook, int then)
    {
        if (notebook.Cell is not { } cell)
            return;

        if (cell.Kind == CellKind.Markdown)
            StopEditing(notebook);
        RunCells(notebook, [cell]);

        switch (then)
        {
            case 1 when notebook.Selected == notebook.Model.Cells.Count - 1:
                AddCell(notebook, CellKind.Code, below: true);
                break;
            case 1:
                PickCell(notebook, notebook.Selected + 1, edit: notebook.Editing && notebook.Model.Cells[notebook.Selected + 1].Kind == CellKind.Code);
                break;
            case 2:
                AddCell(notebook, CellKind.Code, below: true);
                break;
        }
    }

    /// <summary>
    /// A new cell above or below the one picked, picked, and typed into unless it came from A or B out of a cell, which
    /// stay picking cells as Jupyter's do.
    /// </summary>
    private void AddCell(HexNotebook notebook, CellKind kind, bool below, bool edit = true)
    {
        int at = notebook.Model.Cells.Count == 0 ? 0 : Math.Clamp(notebook.Selected + (below ? 1 : 0), 0, notebook.Model.Cells.Count);
        notebook.Model.Cells.Insert(at, new NotebookCell { Kind = kind });
        notebook.Selected = at;
        notebook.Editing = edit;
        notebook.Remake = true;
        NotebookChanged(notebook, now: true);
    }

    private void DeleteCell(HexNotebook notebook)
    {
        if (notebook.Cell is not { } cell)
            return;

        notebook.Model.Cells.Remove(cell);
        if (notebook.Model.Cells.Count == 0)
            notebook.Model.Cells.Add(new NotebookCell { Kind = CellKind.Code });
        notebook.Selected = Math.Min(notebook.Selected, notebook.Model.Cells.Count - 1);
        notebook.Editing = false;
        notebook.Remake = true;
        NotebookChanged(notebook, now: true);
    }

    private void MoveCell(HexNotebook notebook, int by)
    {
        int to = notebook.Selected + by;
        if (notebook.Cell is not { } cell || to < 0 || to >= notebook.Model.Cells.Count)
            return;

        notebook.Model.Cells.RemoveAt(notebook.Selected);
        notebook.Model.Cells.Insert(to, cell);
        notebook.Selected = to;
        notebook.Remake = true;
        NotebookChanged(notebook, now: true);
    }

    // A cell made another kind, a code cell's outputs and count go with what it was
    private void ChangeCell(HexNotebook notebook, CellKind kind)
    {
        if (notebook.Cell is not { } cell || cell.Kind == kind)
            return;

        cell.Kind = kind;
        if (kind != CellKind.Code)
        {
            cell.Outputs.Clear();
            cell.ExecutionCount = null;
        }

        notebook.Remake = true;
        NotebookChanged(notebook, now: true);
    }

    private void ClearOutputs(HexNotebook notebook, IEnumerable<NotebookCell> cells)
    {
        foreach (NotebookCell cell in cells)
        {
            cell.Outputs.Clear();
            cell.ExecutionCount = null;
        }

        NotebookChanged(notebook, now: true);
    }

    /* The bar over the cells */

    private void BuildNotebookBar()
    {
        Button Tool(string icon, string label, Action<HexNotebook> pressed, bool text = false)
        {
            var button = notebookBar.Add(new Button
            {
                Icon = icon,
                Label = text ? Said(label) : string.Empty,
                Tooltip = Said(label),
                Size = new System.Numerics.Vector2(0.0f, 26.0f),
                LabelScale = 0.13f
            });
            button.OnPressed = () =>
            {
                if (HasNotebook)
                    pressed(notebookShown!);
            };
            return button;
        }

        Tool("run-all", "notebook.run_all", notebook => RunCells(notebook, notebook.Model.Cells), text: true);
        Tool("debug-pause", "notebook.interrupt", InterruptKernel);
        Tool("debug-restart", "notebook.restart", notebook => RestartKernel(notebook));
        Tool("clear-all", "notebook.clear_all", notebook => ClearOutputs(notebook, notebook.Model.Cells));
        notebookBar.Add(new Divider { Size = new System.Numerics.Vector2(1.0f, 18.0f) });
        Tool("add", "notebook.code_button", notebook => AddCell(notebook, CellKind.Code, below: true), text: true);
        Tool("add", "notebook.markdown_button", notebook => AddCell(notebook, CellKind.Markdown, below: true), text: true);
        notebookBar.Add(new Divider { Size = new System.Numerics.Vector2(1.0f, 18.0f) });
        kernelButton = Tool("server-process", "notebook.kernel_tip", PickKernel, text: true);
    }

    // Every tick, what the kernel's doing said on its button
    private void UpdateNotebookBar(HexNotebook notebook)
    {
        // Said again only when it's another, the words made every tick were garbage for nothing
        var now = (notebook, notebook.Kernel, notebook.Kernel?.State, notebook.Starting, notebook.Spec, notebook.Model.KernelName);
        if (now == kernelShown)
            return;

        kernelShown = now;
        kernelButton.Label = KernelStatus(notebook);
        kernelButton.Icon = notebook.Kernel?.State == KernelState.Busy || notebook.Starting ? "loading" : "server-process";
    }

    // What the kernel's button was last made to say from
    private (HexNotebook?, Kernel?, KernelState?, bool, KernelSpec?, string?) kernelShown;
}
