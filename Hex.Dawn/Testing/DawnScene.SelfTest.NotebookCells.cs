using Hex.Core.Notebooks;

using Horizon.UI.Components;

namespace Hex.Dawn;

// The notebook's cells as somebody running them sees them, on the review notebook notebooklooks opens: a heading over its
// text written as a heading, the tools on the picked cell only while cells are picked, an error's line over its traceback
// with the way to the line it failed on, a long log said to be longer with all of it a press away, output hidden and shown
// by O and by its rail, and with a kernel here, a running cell's seconds going up, its button stopping it and a run's
// progress on the bar.
internal sealed partial class DawnScene
{
    private void TestNotebookCellLooks(HexPointerScript test)
    {
        test.Check("notebook cells: a heading with its text on the next line is written as its own heading", () =>
            cellViews[0].Written!.Children.OfType<Label>().FirstOrDefault() is { } heading && heading.Text == "# Beam deflection study");
        test.Check("notebook cells: a table in a markdown cell has no CSV strip, a DataFrame's has", () =>
            Descendants(cellViews[0].Written!).OfType<NotebookTablePanel>().All(table => !table.Strip.Visible)
            && Descendants(cellViews[2].Outputs).OfType<NotebookTablePanel>().Any(table => table.Strip.Visible));

        // The tools are over the picked cell's corner while cells are picked, and gone while one's typed into
        test.Run(() => PickCell(notebookShown!, 2, edit: false));
        test.Wait();
        test.Check("notebook cells: the picked cell has the tools over its corner", () => cellTools.Parent == cellViews[2].Card && toolsOn == cellViews[2]);
        test.Check("notebook cells: the picked cell's rails are in the accent", () =>
            cellViews[2].InputRail!.Color is { W: > 0 } && cellViews[1].InputRail!.Color is not { W: > 0 });
        test.Check("notebook cells: the bar says which cell is picked", () => notebookMode.Text == Said("notebook.mode_command", ("n", "3"), ("count", "9")));
        test.Run(() => EditCell(notebookShown!, 2));
        test.Wait();
        test.Check("notebook cells: typed into, the tools go and the bar says it's being edited", () =>
            cellTools.Parent is null && notebookMode.Text == Said("notebook.mode_edit", ("n", "3"), ("count", "9")));
        test.Run(() => StopEditing(notebookShown!));

        // An error's line over its traceback, its button to the line of the cell that failed
        test.Check("notebook cells: an error says what it was over its traceback, in the error colour", () =>
            cellViews[5].Outputs.Children.OfType<NotebookOutputStrip>().FirstOrDefault() is { } header
            && header.Said.Text.Contains("NameError: name 'I_section' is not defined") && header.Said.Color == compositor.Skin!.ErrorColor
            && cellViews[5].Outputs.Children.OfType<CodeEditor>().Single().Text.Contains("Traceback"));
        test.Check("notebook cells: the outputs of a cell that failed keep an error coloured rail", () =>
            cellViews[5].OutputRail.Color == compositor.Skin!.ErrorColor || notebookShown!.Run.Failed(cellViews[5].Cell) == false);
        test.Run(() =>
        {
            Button go = cellViews[5].Outputs.Children.OfType<NotebookOutputStrip>().First().Children.OfType<Button>().Single();
            go.OnPressed!();
        });
        test.Wait();
        test.Check("notebook cells: Go to line puts the caret on the line the traceback names", () =>
            notebookShown!.Editing && notebookShown.Selected == 5 && cellViews[5].Editor.Document.Caret.Line == 3);
        test.Run(() => StopEditing(notebookShown!));
        test.Check("notebook cells: the line an error failed on is read from IPython's traceback", () =>
            FailingLine("Cell In[5], line 4\n----> 4 k = f()\nCell In[5], line 2, in f\n", 5) == 2 && FailingLine("<ipython-input-3-x> in <module>\n----> 7 x()", null) == 7
            && FailingLine("nothing here", 1) is null);

        // A long log said to be longer, all of it a press away
        test.Check("notebook cells: a long log says how much of it there is", () =>
            cellViews[4].Outputs.Children.OfType<NotebookOutputStrip>().SingleOrDefault() is { } footer
            && footer.Said.Text == Said("notebook.lines_some", ("shown", "30"), ("count", "120")));
        test.Run(() =>
        {
            PickCell(notebookShown!, 4, edit: false);
            cellViews[4].Outputs.Children.OfType<NotebookOutputStrip>().Single().Children.OfType<Button>().First().OnPressed!();
        });
        test.Wait();
        test.Check("notebook cells: Show all shows the whole log", () =>
            cellViews[4].Outputs.Children.OfType<CodeEditor>().Single().FitRows > 120
            && cellViews[4].Outputs.Children.OfType<NotebookOutputStrip>().Single().Said.Text == Said("notebook.lines_all", ("count", "120")));
        test.Run(() => Shot("notebook-stream-whole"));
        test.Run(() => cellViews[4].Outputs.Children.OfType<NotebookOutputStrip>().Single().Children.OfType<Button>().First().OnPressed!());
        test.Wait();

        // O hides the picked cell's output, written into its metadata as Jupyter does, its rail shows it again
        test.Run(() => PickCell(notebookShown!, 3, edit: false));
        PressKeys(test, "O");
        test.Wait();
        test.Check("notebook cells: O hides the output behind a line that says so", () =>
            !cellViews[3].OutputRow.Visible && cellViews[3].OutputHidden.Visible
            && cellViews[3].Cell.Metadata["jupyter"]?["outputs_hidden"]?.GetValue<bool>() == true);
        test.Run(() => Shot("notebook-output-hidden"));
        test.Run(() => cellViews[3].OutputHidden.OnPressed!());
        test.Wait();
        test.Check("notebook cells: pressed, the output's back and the metadata as it was", () =>
            cellViews[3].OutputRow.Visible && !cellViews[3].OutputHidden.Visible && cellViews[3].Cell.Metadata["jupyter"] is null
            && cellViews[3].Cell.Metadata["collapsed"] is null);
        test.Run(() => cellViews[1].InputRail!.Pressed!());
        test.Wait();
        test.Check("notebook cells: the code's rail hides the code", () => !cellViews[1].Frame!.Visible && cellViews[1].InputHidden!.Visible);
        test.Run(() => cellViews[1].InputHidden!.OnPressed!());
        test.Wait();
        test.Check("notebook cells: and the line in its place shows it again", () => cellViews[1].Frame!.Visible && !cellViews[1].InputHidden!.Visible);

        // With a kernel here, a cell running says its seconds and its button stops it, and a run says how far it's got
        if (KernelSpecs.For(KernelSpecs.Find(), "python3", "python") is null)
            return;

        test.Run(() =>
        {
            NotebookCell last = notebookShown!.Model.Cells[^1];
            last.Source = "import time\ntime.sleep(2.6)\nprint('slept')";
            notebookShown.Remake = true;
        });
        test.Wait();
        test.Run(() => RunCells(notebookShown!, [notebookShown!.Model.Cells[1], notebookShown.Model.Cells[^1]]));
        test.WaitFor(() => notebookShown!.Run.RunningSince(notebookShown.Model.Cells[^1]) is not null, 90);
        test.Settle(1.3f);
        test.Check("notebook cells: a running cell's seconds go up under its count and its button stops it", () =>
            cellViews[^1] is { } running && running.Count.Text == "[*]" && running.Took.Text is "1 s" or "2 s"
            && running.RunButton.Icon == "debug-stop" && running.Took.Color == NotebookLookNow().Accent);
        test.Check("notebook cells: the bar says how far the run's got", () => runProgress.Visible && runProgress.Text == Said("notebook.running_of", ("n", "2"), ("count", "2")));
        test.Check("notebook cells: the kernel's dot is busy", () => kernelButton.Icon == "loading~spin" && interruptButton.Enabled);
        test.Run(() => PickCell(notebookShown!, cellViews.Count - 1, edit: false));
        test.Wait();
        test.Run(() => Shot("notebook-running"));
        test.WaitFor(() => notebookShown!.Run.Queued.Count == 0, 20);
        test.Wait();
        test.Check("notebook cells: done, it says how long it took", () =>
            cellViews[^1].Took.Text.EndsWith(" s", StringComparison.Ordinal) && cellViews[^1].Took.Text.StartsWith('2') && cellViews[^1].RunButton.Icon == "play");
        test.Check("notebook cells: done, the bar's progress goes", () => !runProgress.Visible);
        test.Check("notebook cells: the kernel's dot is idle, in the success colour", () =>
            kernelButton.Icon == "circle-filled" && kernelButton.IconColor == compositor.Skin!.SuccessColor && !interruptButton.Enabled);
        test.Run(() => Shot("notebook-ran"));
        test.Run(() => StopKernel(notebookShown!));
    }
}
