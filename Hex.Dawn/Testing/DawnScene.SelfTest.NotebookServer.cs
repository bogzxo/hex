using System.Text.Json.Nodes;

using Hex.Core.Notebooks;

using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of a notebook's code read by its language's server (HexLanguages.Notebooks), through the real pyright,
// left out when it isn't on this machine. A module that isn't there is underlined in the cell that imports it, a name
// never made in the cell that uses it, a line of Jupyter's own (%pip) is nothing wrong, a name made in one cell is known
// in the next, and what's typed after a dot completes from the server with no kernel ever started.
internal sealed partial class DawnScene
{
    private void TestNotebookServer(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "selftest_server.ipynb");
        bool there = HexServerCatalog.Locate("pyright-langserver") is not null;
        var made = new Notebook();
        made.Cells.Add(new NotebookCell { Kind = CellKind.Markdown, Source = "# Cells read by pyright" });
        made.Cells.Add(new NotebookCell { Kind = CellKind.Code, Source = "%pip install nothing\nimport os\nfrom not_a_module_anywhere import thing\ncount = 3" });
        made.Cells.Add(new NotebookCell { Kind = CellKind.Code, Source = "total = count + never_made\ntotal" });
        made.Cells.Add(new NotebookCell { Kind = CellKind.Code, Source = "os." });
        made.Metadata["kernelspec"] = new JsonObject { ["display_name"] = "Python 3 (ipykernel)", ["language"] = "python", ["name"] = "python3" };

        test.Run(() =>
        {
            if (!there)
            {
                Console.WriteLine("[Hex self-test] pyright isn't on this machine, the notebook's server part is skipped");
                return;
            }
            File.WriteAllText(file, NotebookFile.Write(made));
            Open(file);
        });
        if (!there)
            return;

        test.Wait();
        test.WaitFor(() => NotebookShown && cellViews.Count == 4 && cellViews[1].Editor.Diagnostics.Count > 0, 60.0f);
        test.Check("notebook server: a module that isn't there is underlined in the cell that imports it, on its own line", () =>
            cellViews[1].Editor.Diagnostics.Any(found => found.Line == 2 && found.Message.Contains("not_a_module_anywhere")));
        test.Check("notebook server: a line of Jupyter's own isn't something wrong", () =>
            !cellViews[1].Editor.Diagnostics.Any(found => found.Line == 0));
        test.WaitFor(() => cellViews[2].Editor.Diagnostics.Count > 0, 30.0f);
        test.Check("notebook server: a name never made is said in the cell that uses it, one made in a cell before isn't", () =>
            cellViews[2].Editor.Diagnostics.Any(found => found.Line == 0 && found.Message.Contains("never_made"))
            && !cellViews[2].Editor.Diagnostics.Any(found => found.Message.Contains("\"count\"")));
        test.Check("notebook server: a cell's last line on its own, what it shows when it runs, isn't a value thrown away", () =>
            !cellViews[2].Editor.Diagnostics.Any(found => found.Line == 1));
        test.Check("notebook server: and the markdown cell has nothing said of it", () => cellViews[0].Editor.Diagnostics.Count == 0);

        // After a dot in the last cell, the module's own names, with no kernel ever started
        test.Run(() =>
        {
            NotebookOf(document).Selected = 3;
            EditCell(NotebookOf(document), 3);
            NotebookCellEditor editor = cellViews[3].Editor;
            editor.Focus();
            editor.Document.MoveTo(new UITextPosition(0, 3));
            editor.Complete(typed: true);
        });
        test.WaitFor(() => cellViews[3].Editor.CompletionOpen && cellViews[3].Editor.Completions.Any(item => item.Text == "path"), 30.0f);
        test.Check("notebook server: after os. the module's names complete from the server, no kernel running", () =>
            NotebookOf(document).Kernel is null && cellViews[3].Editor.CompletionOpen && cellViews[3].Editor.Completions.Any(item => item.Text == "path"));
        test.Run(() => Shot("notebook-server"));

        // Typed into, what's said follows: the name made, its line has nothing to say
        test.Run(() =>
        {
            cellViews[3].Editor.CloseCompletion();
            StopEditing(NotebookOf(document));
            EditCell(NotebookOf(document), 1);
            NotebookCellEditor editor = cellViews[1].Editor;
            editor.Document.MoveTo(new UITextPosition(3, 9));
            editor.Document.Insert("\nnever_made = 1");
        });
        test.WaitFor(() => !cellViews[2].Editor.Diagnostics.Any(found => found.Message.Contains("never_made")), 30.0f);
        test.Check("notebook server: a name made in an earlier cell as it's typed, the later cell's line has nothing more to say", () =>
            !cellViews[2].Editor.Diagnostics.Any(found => found.Message.Contains("never_made")));

        test.Run(() =>
        {
            CloseNow();
            LanguageServers.StopAll();
        });
    }
}
