using System.Numerics;
using Hex.Core.Notebooks;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestNotebookWrap(HexPointerScript test)
    {
        var saved = (settings.BackdropOn, settings.BackdropWrap, settings.WrapCode, settings.BackdropOpacity);
        bool aligned = false;
        float error = 0, settledHeight = 0;
        long idleBytes = -1;
        test.Run(() =>
        {
            settings.BackdropOn = settings.BackdropWrap = settings.WrapCode = true;
            settings.BackdropOpacity = 0.5f;
            ApplyBackdrop();
            var model = Notebook.Empty();
            for (int i = 0; i < 20; i++) model.Cells.Add(new NotebookCell { Kind = CellKind.Code,
                Source = string.Join('\n', Enumerable.Repeat("value = long_variable_name + another_variable_name + third_variable_name + final_variable_name", 8)) });
            string file = Path.Combine(testFolder, "wrap-notebook.ipynb");
            File.WriteAllText(file, NotebookFile.Write(model));
            Open(file);
        });
        test.Settle(0.5f);
        test.Check("notebook wrap: real image and alpha shape are loaded", () =>
            backdropPicture is { IsValid: true } && backdropShape is not null && cellViews[0].Editor.WrapAreas.Count == 1);
        test.Run(() =>
        {
            // Layout may move between FitNotebook and painting, including inertial scrolling.
            FitNotebook();
            notebookScroll.Offset += 80;
            notebookScroll.ArrangeTree(notebookScroll.Bounds);
            var editor = cellViews[0].Editor;
            if (editor.BackdropRoom is not { } room || backdropPicture is not { } picture || editor.WrapAreas.Count != 1) return;
            UIRect expected = CodeEditor.PlaceBackdrop(notebookBackdrop.Bounds, notebookBackdrop.Module?.Frame ?? notebookBackdrop.Bounds,
                picture, settings.BackdropScale, new Vector2(settings.BackdropAcross, settings.BackdropUp), settings.BackdropStretch);
            error = Vector2.Distance(editor.WrapAreas[0].Bounds.Min + room.Min, expected.Min);
            aligned = error < 0.1f;
            HexLogLine($"notebook wrap post-arrange error: {error:0.###}");
        });
        test.Check("notebook wrap: exclusion remains aligned after scrolling layout", () => aligned);
        test.Settle(0.5f);
        test.Run(() => settledHeight = notebookCells.Size.Y);
        test.Settle(0.5f);
        test.Check("notebook wrap: cell heights settle after scrolling", () => MathF.Abs(notebookCells.Size.Y - settledHeight) < 0.1f);
        test.Run(() =>
        {
            for (int i = 0; i < 30; i++) { UpdateCellViews(notebookShown!); FitNotebook(); }
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) { UpdateCellViews(notebookShown!); FitNotebook(); }
            idleBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            HexLogLine($"notebook wrap idle update/layout allocation: {idleBytes} bytes / 100 iterations");
        });
        test.Check("notebook wrap: stationary wrapped cell update/layout allocates nothing", () => idleBytes == 0);
        test.Run(() =>
        {
            CloseNow();
            (settings.BackdropOn, settings.BackdropWrap, settings.WrapCode, settings.BackdropOpacity) = saved;
            ApplyBackdrop();
        });
    }
}
