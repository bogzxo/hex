using Hex.Core.Notebooks;

using Horizon.Graphics;

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
        test.Check("notebook cells: a running cell's seconds go up under its count and its button is a spinner that stops it", () =>
            cellViews[^1] is { } running && running.Count.Text == "[*]" && running.Took.Text is "1 s" or "2 s"
            && running.RunButton.Icon == "loading~spin" && running.RunButton.Tooltip == Said("notebook.stop_cell_tip")
            && running.Took.Color == NotebookLookNow().Accent);
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
        TestNotebookThrobbers(test, animated: true);
        TestNotebookThrobbers(test, animated: false);
        test.Run(() => StopKernel(notebookShown!));
    }

    /// <summary>
    /// The second cell made to sleep 3 s at its end and the third waiting behind it, the two buttons in their gutters
    /// looked at three times as they go and the running one's seconds read. With the UI's motion on both are spinners
    /// that turn, no two pictures of either the same, and pointed at, the running one's is stop. At 0 they stand
    /// still and the seconds still go up.
    /// </summary>
    private void TestNotebookThrobbers(HexPointerScript test, bool animated)
    {
        string how = animated ? "with the UI's motion on" : "with the UI's motion at 0";
        float animation = 1.0f;
        string source = string.Empty;
        Task<ImagePixels>? capture = null;
        var running = new List<byte[]>();
        var queued = new List<byte[]>();
        var seconds = new List<string>();
        var icons = new List<string>();
        bool inside = true;
        void Picture()
        {
            test.Run(() => { Engine.WindowManager.RequestFrames(); capture = Engine.CapturePixels(); });
            test.WaitFor(() => capture!.IsCompleted, 10);
            test.Run(() =>
            {
                ImagePixels frame = capture!.Result;
                foreach (var (index, pictures) in ((int, List<byte[]>)[])[(1, running), (2, queued)])
                {
                    Button button = cellViews[index].RunButton;
                    System.Numerics.Vector4 box = McpBox(button.Module!, button.Bounds);
                    inside &= box.X >= 0 && box.Y >= 0 && box.X + box.Z <= frame.Width && box.Y + box.W <= frame.Height;
                    pictures.Add(PixelsOf(frame, box));
                }

                seconds.Add(cellViews[1].Took.Text);
                icons.Add(cellViews[1].RunButton.Icon + " " + cellViews[2].RunButton.Icon);
            });
        }

        bool Turned(List<byte[]> pictures) => pictures.Count == 3 && pictures[0].Length > 0
            && !pictures[0].AsSpan().SequenceEqual(pictures[1]) && !pictures[1].AsSpan().SequenceEqual(pictures[2]);
        // The last two, the first can be taken as the waiting cell's button is still changing from play to its spinner
        bool Still(List<byte[]> pictures) => pictures.Count == 3 && pictures[1].Length > 0 && pictures[1].AsSpan().SequenceEqual(pictures[2]);

        test.Run(() =>
        {
            animation = settings.UiAnimation;
            settings.UiAnimation = animated ? 1.0f : 0.0f;
            ApplyEffects();
            source = notebookShown!.Model.Cells[1].Source;
            notebookShown.Model.Cells[1].Source = source + "\nimport time\ntime.sleep(3)";
            notebookShown.Remake = true;
        });
        test.Wait();
        test.Run(() =>
        {
            PickCell(notebookShown!, 1, edit: false);
            RunCells(notebookShown!, [notebookShown!.Model.Cells[1], notebookShown.Model.Cells[2]]);
        });
        test.WaitFor(() => notebookShown!.Run.RunningSince(notebookShown.Model.Cells[1]) is not null, 30);
        test.Settle(0.2f);
        Picture();
        test.Settle(0.3f);
        Picture();
        test.Settle(1.0f);
        Picture();
        test.Check($"notebook cells: {how}, the running cell and the one waiting behind it have spinners in their gutters", () =>
            inside && icons.Count == 3 && icons.All(icon => icon == "loading~spin loading~spin"));
        if (animated)
        {
            test.Check("notebook cells: the running cell's spinner turns", () => Turned(running));
            test.Check("notebook cells: the waiting cell's spinner turns", () => Turned(queued));
        }
        else
            test.Check("notebook cells: with the UI's motion at 0, both spinners stand still", () => Still(running) && Still(queued));

        test.Check($"notebook cells: {how}, the running cell's seconds go up", () =>
            seconds.Count == 3 && seconds[0] == "0 s" && seconds[2] is "1 s" or "2 s");
        if (animated)
        {
            // Pointed at, the running cell's spinner is the stop it always was, and back to turning once the pointer's off it
            test.Hover(() => cellViews[1].RunButton.Module!.ToWorld(cellViews[1].RunButton.Bounds.Center));
            test.Wait();
            test.Check("notebook cells: pointed at, the running cell's spinner is stop", () =>
                notebookShown!.Run.RunningSince(notebookShown.Model.Cells[1]) is not null && cellViews[1].RunButton.Icon == "debug-stop");
            test.Hover(() => cellViews[1].Count.Module!.ToWorld(cellViews[1].Count.Bounds.Center));
            test.Wait();
            test.Check("notebook cells: and a spinner again once the pointer's off it", () =>
                notebookShown!.Run.RunningSince(notebookShown.Model.Cells[1]) is null || cellViews[1].RunButton.Icon == "loading~spin");
        }

        test.WaitFor(() => notebookShown!.Run.Queued.Count == 0 && notebookShown.Waiting.Count == 0, 20);
        test.Wait();
        test.Check($"notebook cells: {how}, done, both buttons are play again", () =>
            cellViews[1].RunButton.Icon == "play" && cellViews[2].RunButton.Icon == "play");
        test.Run(() =>
        {
            notebookShown!.Model.Cells[1].Source = source;
            notebookShown.Remake = true;
            settings.UiAnimation = animation;
            ApplyEffects();
        });
        test.Wait();
    }

    // Helper method for the pixels of a box of the window, a box in window pixels, x and y its top left
    private static byte[] PixelsOf(ImagePixels frame, System.Numerics.Vector4 box)
    {
        int x = Math.Clamp((int)box.X, 0, frame.Width), y = Math.Clamp((int)box.Y, 0, frame.Height);
        int width = Math.Clamp((int)MathF.Ceiling(box.Z), 0, frame.Width - x), height = Math.Clamp((int)MathF.Ceiling(box.W), 0, frame.Height - y);
        byte[] cut = new byte[width * height * 4];
        for (int row = 0; row < height; row++)
            Array.Copy(frame.Pixels, ((y + row) * frame.Width + x) * 4, cut, row * width * 4, width * 4);
        return cut;
    }
}
