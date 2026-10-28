using Hex.Core.Notebooks;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestNotebookFile(HexPointerScript test)
    {
        string source = Environment.GetEnvironmentVariable("HEX_NOTEBOOK_TEST_FILE") ?? string.Empty;
        string file = Path.Combine(testFolder, "real-notebook.ipynb");
        test.Run(() => { File.Copy(source, file, true); Open(file); });
        test.Wait();
        test.Wait();
        test.WaitFor(() => notebookRenders.Count == 0, 60);
        test.Run(() =>
        {
            foreach (var picture in cellPictures.Values.Concat(markdownPictures.Values).SelectMany(pictures => pictures).Where(picture => picture.Texture is null))
                Console.WriteLine("[Hex self-test] render error: " + picture.RenderStatus?.Text);
        });
        test.Check("real notebook: every source cell opens", () => notebookShown is not null && cellViews.Count == NotebookFile.Read(File.ReadAllText(source)).Cells.Count);
        test.Check("real notebook: saved rich output and formulas have valid textures", () =>
            cellPictures.Values.Concat(markdownPictures.Values).SelectMany(pictures => pictures).All(picture => picture.Texture is { IsValid: true }));
        test.Check("real notebook: Markdown assumptions wrap and retain formatting", () =>
            Descendants(cellViews[0].Written!).OfType<NotebookMarkdownTable>().Any(table => table.Cells.Count > 4
                && table.Cells[1][1].Bounds.Height > table.Cells[0][1].Bounds.Height
                && table.Cells[1][1].Children.OfType<Horizon.UI.Components.Label>().Any(label => label.Markdown && label.Text.Contains("**"))));
        test.Run(() => Shot("real-notebook-top"));
        test.Settle(0.2f);
        foreach (int index in new[] { 4, 9, 16, 19, 23 })
        {
            test.Run(() => notebookScroll.ScrollToTop(cellViews[Math.Min(index, cellViews.Count - 1)].Card));
            test.Settle(0.2f);
            test.Run(() => Shot("real-notebook-cell-" + index));
            test.Settle(0.2f);
        }
        float originalZoom = settings.CodeZoom;
        float originalText = 0, originalFormula = 0;
        Horizon.Graphics.Texture? originalTexture = null;
        test.Run(() =>
        {
            originalText = cellViews[1].Editor.TextScale;
            var formula = markdownPictures.Values.SelectMany(pictures => pictures).First(picture => picture.Reflow is null && picture.MathInk);
            originalFormula = formula.Size.Y;
            originalTexture = formula.Texture;
            ZoomCode(1.4f);
        });
        test.Wait();
        test.WaitFor(() => notebookRenders.Count == 0, 30);
        test.Check("real notebook: zoom updates existing source, tables and math without losing textures", () =>
            cellViews[1].Editor.TextScale > originalText
            && markdownPictures.Values.SelectMany(pictures => pictures).Any(picture => ReferenceEquals(picture.Texture, originalTexture) && picture.Size.Y > originalFormula)
            && Descendants(cellViews[0].Written!).OfType<Horizon.UI.Components.Label>().Where(label => label.Markdown).Any(label => label.TextScale > 0.16f));
        test.Run(() => notebookScroll.ScrollToTop(cellViews[0].Card));
        test.Settle(0.2f);
        test.Run(() => Shot("real-notebook-zoomed"));
        test.Settle(0.2f);
        test.Run(() => { ZoomCode(0); ((NotebookScrollPanel)notebookScroll).Zoom!(1); });
        test.WaitFor(() => notebookRenders.Count == 0, 30);
        long idleBytes = 0;
        test.Run(() =>
        {
            for (int i = 0; i < 30; i++) { UpdateCellViews(notebookShown!); FitNotebook(); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) { UpdateCellViews(notebookShown!); FitNotebook(); }
            idleBytes = (GC.GetAllocatedBytesForCurrentThread() - before) / 100;
            Console.WriteLine($"[Hex self-test] real notebook: idle cell update/layout {idleBytes} bytes per tick");
        });
        test.Check("real notebook: cached cell updates and fitting avoid idle garbage", () => idleBytes == 0);
        test.Check("real notebook: notebook wheel uses the shared zoom command", () => settings.CodeZoom > 1 && cellViews[1].Editor.OnZoom is not null);
        test.Run(() => ZoomCode(originalZoom / settings.CodeZoom));
        test.WaitFor(() => notebookRenders.Count == 0, 30);
        if (Environment.GetEnvironmentVariable("HEX_NOTEBOOK_TEST_EXECUTE") == "1")
        {
            test.Run(() => RunCells(notebookShown!, notebookShown!.Model.Cells));
            test.WaitFor(() => notebookShown!.Model.Cells[^1].ExecutionCount is not null && notebookShown.Run.Queued.Count == 0 && !notebookShown.Starting, 600);
            test.Settle(0.5f);
            test.WaitFor(() => notebookRenders.Count == 0, 60);
            test.Check("real notebook: Run All finishes without Python errors", () => notebookShown!.Model.Cells[^1].ExecutionCount is not null
                && !notebookShown.Model.Cells.SelectMany(cell => cell.Outputs).Any(output => output["output_type"]?.GetValue<string>() == "error"));
            test.Check("real notebook: executed graphics all upload", () => cellPictures.Values.SelectMany(pictures => pictures).All(picture => picture.Texture is { IsValid: true }));
            test.Check("real notebook: CAD files stay in the isolated working directory", () =>
                File.Exists(Path.Combine(testFolder, "cad_output", "beam_side_profile.dxf")) && File.Exists(Path.Combine(testFolder, "cad_output", "beam_parameters.json")));
            test.Run(() => { FlushNotebook(document); Save(); });
            test.Check("real notebook: executed output roundtrips", () =>
                NotebookFile.Write(NotebookFile.Read(File.ReadAllText(file))) == NotebookFile.Write(notebookShown!.Model));
        }
        CloseNotebookTest(test, file);
    }
}
