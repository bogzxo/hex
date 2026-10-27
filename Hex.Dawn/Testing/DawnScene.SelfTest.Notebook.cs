using System.Text.Json.Nodes;

using Hex.Core.Notebooks;

using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of Jupyter notebooks. A notebook comes up as its cells, markdown written out and code in the language's
// colours, its JSON the Code view. Then on a machine with a Python that has ipykernel, run in a real kernel, Run All,
// what each printed, a result, a picture and an error under its cell, the counts, the JSON with all of it and saved. A
// cell typed into and run with Shift+Enter, cells put in, changed and taken out by Jupyter's keys, input() answered in
// its box, a loop interrupted, the kernel restarted and counting from 1 again, and closed with the notebook.
internal sealed partial class DawnScene
{
    private const string NOTEBOOK_PICTURE = """
        import zlib, struct
        def png(w, h):
            raw = b''.join(b'\x00' + bytes([(x * 255 // w) if c == 0 else (y * 255 // h) if c == 1 else 128 for x in range(w) for c in range(3)]) for y in range(h))
            def chunk(t, d): return struct.pack('>I', len(d)) + t + d + struct.pack('>I', zlib.crc32(t + d) & 0xffffffff)
            return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(raw)) + chunk(b'IEND', b'')
        from IPython.display import Image, display
        display(Image(data=png(64, 32)))
        """;

    private void TestNotebook(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "selftest.ipynb");
        var made = new Notebook();
        made.Cells.Add(new NotebookCell { Kind = CellKind.Markdown, Source = "# A notebook\nwith **bold** words" });
        made.Cells.Add(new NotebookCell { Kind = CellKind.Code, Source = "x = 21\nprint('half is', x)\nx * 2" });
        made.Cells.Add(new NotebookCell { Kind = CellKind.Code, Source = NOTEBOOK_PICTURE.ReplaceLineEndings("\n") });
        made.Cells.Add(new NotebookCell { Kind = CellKind.Code, Source = "1 / 0" });
        made.Metadata["kernelspec"] = new JsonObject { ["display_name"] = "Python 3 (ipykernel)", ["language"] = "python", ["name"] = "python3" };

        test.Run(() =>
        {
            File.WriteAllText(file, NotebookFile.Write(made));
            Open(file);
        });
        test.Wait();
        test.Wait();
        test.Check("notebook: a .ipynb comes up as its cells, one card each", () =>
            document.View == HexView.Notebook && NotebookShown && notebookView.Visible && !codeScroll.Visible && cellViews.Count == 4);
        test.Check("notebook: markdown is written out and code is in its language's colours", () =>
            cellViews[0].Written is { Visible: true } written && written.Text.StartsWith("# A notebook") && !cellViews[0].Editor.Visible
            && cellViews[1].Editor.Highlighter is { } python && python.Name.Contains("python", StringComparison.OrdinalIgnoreCase));
        test.Check("notebook: the Notebook menu is on the bar and the Notebook button picked", () =>
            layout.Get<Button>("notebook_button") is { Visible: true, Selected: true });

        // Its JSON is the Code view, typed into there the cells follow
        test.Run(() => SwitchView(HexView.Code));
        test.Wait();
        test.Check("notebook: the Code view is its JSON", () => codeScroll.Visible && !notebookView.Visible && code.Text.Contains("\"cell_type\": \"markdown\""));
        test.Run(() =>
        {
            code.Document.Replace(code.Text.Replace("x = 21", "x = 20", StringComparison.Ordinal));
            SwitchView(HexView.Notebook);
        });
        test.Wait();
        test.Wait();
        test.Check("notebook: and what's typed into the JSON is in the cells", () => notebookShown!.Model.Cells[1].Source.StartsWith("x = 20"));

        // Jupyter's keys out of a cell
        test.Run(() => PickCell(notebookShown!, 1, edit: false));
        test.Wait();
        PressKeys(test, "B");
        test.Check("notebook: B puts a new code cell under the one picked and types into it", () =>
            notebookShown!.Model.Cells.Count == 5 && notebookShown.Selected == 2 && notebookShown.Model.Cells[2] is { Kind: CellKind.Code, Source: "" } && notebookShown.Editing);
        PressKeys(test, "Escape");
        test.Check("notebook: Escape goes out of the cell back to picking cells", () => !notebookShown!.Editing && compositor.Focus is not NotebookCellEditor);
        test.Wait();
        PressKeys(test, "M");
        test.Check("notebook: M makes it markdown", () => notebookShown!.Model.Cells[2].Kind == CellKind.Markdown);
        PressKeys(test, "D D");
        test.Wait();
        test.Check("notebook: D D takes it out", () => notebookShown!.Model.Cells.Count == 4 && cellViews.Count == 4);

        test.Run(() => Console.WriteLine("[Hex self-test] notebook: kernels found, " + string.Join(", ", KernelSpecs.Find().Select(spec => spec.Name))));
        if (KernelSpecs.Find().All(spec => spec.Language != "python"))
        {
            test.Run(() => Console.WriteLine("[Hex self-test] notebook: no Python with ipykernel here, the kernel's parts are skipped"));
            CloseNotebookTest(test, file);
            return;
        }

        // Run All in a real kernel, up to the error, which stops what comes after it as Jupyter does
        test.Run(() => RunCells(notebookShown!, notebookShown!.Model.Cells));
        test.WaitFor(() => notebookShown!.Model.Cells[3].Outputs.Count > 0 && notebookShown.Run.Queued.Count == 0, 90.0f);
        test.Settle(0.5f);
        test.Check("notebook: Run All, what the cell printed and its result under it, counted", () =>
            notebookShown!.Model.Cells[1] is { ExecutionCount: 1 } first && first.Outputs.Count == 2
            && NotebookFile.Text(first.Outputs[0]["text"]) == "half is 20\n" && first.Outputs[1]["data"]?["text/plain"]?.GetValue<string>() == "40");
        test.Check("notebook: a picture it displayed is drawn", () =>
            cellViews[2].Outputs.Children.OfType<NotebookPicture>().Any(picture => picture.Texture is { IsValid: true }));
        test.Check("notebook: an error is its traceback, coloured, the kernel's name for it in it", () =>
            notebookShown!.Model.Cells[3].Outputs.Single()["ename"]?.GetValue<string>() == "ZeroDivisionError"
            && cellViews[3].Outputs.Children.OfType<CodeEditor>().Single() is { } traceback && traceback.Text.Contains("ZeroDivisionError") && traceback.SemanticSpans.Count > 0);
        test.Check("notebook: the bar says the kernel's idle", () => kernelButton.Label.Contains("idle"));
        test.WaitFor(() => !notebookShown!.Changed, 2.0f);
        test.Check("notebook: the outputs are in its JSON and it's got changes to save", () =>
            document.Modified && document.Editor.Text.Contains("\"image/png\"") && document.Editor.Text.Contains("\"execution_count\": 1"));

        // Typed into and run with Shift+Enter, the next cell picked
        test.Run(() => EditCell(notebookShown!, 1));
        test.Run(() =>
        {
            cellViews[1].Editor.Document.MoveToEnd(false);
            cellViews[1].Editor.Document.Insert("\nx + 1");
        });
        test.Wait();
        PressKeys(test, "Shift+Enter");
        test.WaitFor(() => notebookShown!.Run.Queued.Count == 0 && notebookShown.Model.Cells[1].ExecutionCount == 4, 20.0f);
        test.Check("notebook: Shift+Enter runs the cell typed into and goes on to the next", () =>
            notebookShown!.Model.Cells[1].Outputs.Last()["data"]?["text/plain"]?.GetValue<string>() == "21" && notebookShown.Selected == 2);

        // input() asks in a box under the cell
        test.Run(() =>
        {
            notebookShown!.Model.Cells.Add(new NotebookCell { Kind = CellKind.Code, Source = "name = input('who? ')\nprint('hi', name)" });
            notebookShown.Remake = true;
        });
        test.Wait();
        test.Run(() => RunCells(notebookShown!, [notebookShown!.Model.Cells[^1]]));
        test.WaitFor(() => cellViews.Count == 5 && cellViews[4].Outputs.Children.OfType<TextBox>().Any(), 20.0f);
        test.Check("notebook: input() asks in a box under its cell", () => cellViews[4].Outputs.Children.OfType<TextBox>().Single().Placeholder == "who?");
        test.Run(() => cellViews[4].Outputs.Children.OfType<TextBox>().Single().OnSubmitted!("bogz"));
        test.WaitFor(() => notebookShown!.Run.Queued.Count == 0, 20.0f);
        test.Check("notebook: and what's typed there goes to the code", () =>
            notebookShown!.Model.Cells[4].Outputs.Any(output => NotebookFile.Text(output["text"]).Contains("hi bogz")));

        // A loop interrupted, the kernel carrying on
        test.Run(() =>
        {
            notebookShown!.Model.Cells[4].Source = "while True:\n    pass";
            RunCells(notebookShown!, [notebookShown!.Model.Cells[4]]);
        });
        test.WaitFor(() => notebookShown!.Kernel?.State == KernelState.Busy, 10.0f);
        test.Settle(0.5f);
        test.Run(() => InterruptKernel(notebookShown!));
        test.WaitFor(() => notebookShown!.Run.Queued.Count == 0, 20.0f);
        test.Check("notebook: a running cell is interrupted, KeyboardInterrupt under it", () =>
            notebookShown!.Model.Cells[4].Outputs.Any(output => output["ename"]?.GetValue<string>() == "KeyboardInterrupt"));

        // Restarted, what it knew gone and counting from 1
        test.Run(() => RestartKernel(notebookShown!));
        test.WaitFor(() => notebookShown!.Kernel?.State == KernelState.Idle && !notebookShown.Starting, 60.0f);
        test.Run(() =>
        {
            notebookShown!.Model.Cells[4].Source = "'x' in globals()";
            RunCells(notebookShown!, [notebookShown!.Model.Cells[4]]);
        });
        test.WaitFor(() => notebookShown!.Run.Queued.Count == 0 && notebookShown.Model.Cells[4].ExecutionCount is not null, 20.0f);
        test.Check("notebook: restarted, it knows nothing it was told and counts from 1", () =>
            notebookShown!.Model.Cells[4] is { ExecutionCount: 1 } cell && cell.Outputs.Single()["data"]?["text/plain"]?.GetValue<string>() == "False");

        // Saved, the file is a notebook Jupyter would read with all of it in
        test.Run(Save);
        test.Wait();
        test.Check("notebook: saved, the file is the notebook with its outputs", () =>
            !document.Modified && NotebookFile.Read(File.ReadAllText(file)) is { } saved && saved.Cells.Count == 5
            && saved.Cells[2].Outputs.Any(output => output["data"]?["image/png"] is not null) && saved.KernelName == "python3");

        HexNotebook? kept = null;
        test.Run(() => kept = notebookShown);
        CloseNotebookTest(test, file);
        test.Check("notebook: closed, its kernel goes with it", () => kept is not null && kept.Kernel is null && !notebooks.ContainsKey(kept.Document));
    }

    private void CloseNotebookTest(HexPointerScript test, string file)
    {
        test.Run(() =>
        {
            if (document.Path is { } open && PlatformPaths.Same(open, file))
                CloseNow();
        });
        test.Wait();
    }
}
