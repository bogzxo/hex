using Hex.Core.Notebooks;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestNotebookZoom(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "zoom-notebook.ipynb");
        NotebookCellView? anchored = null;
        float fraction = 0, originalZoom = settings.CodeZoom, originalScale = compositor.Scale;
        void Capture()
        {
            float y = notebookScroll.Bounds.Center.Y;
            anchored = cellViews.First(view => view.Card.Bounds.Min.Y <= y && view.Card.Bounds.Max.Y >= y);
            fraction = (anchored.Card.Bounds.Max.Y - y) / anchored.Card.Bounds.Height;
        }
        bool Centred() => anchored is not null && MathF.Abs(anchored.Card.Bounds.Max.Y -
            fraction * anchored.Card.Bounds.Height - notebookScroll.Bounds.Center.Y) < 1.5f;
        test.Run(() =>
        {
            var model = Notebook.Empty();
            for (int i = 0; i < 30; i++)
                model.Cells.Add(new NotebookCell { Kind = CellKind.Code,
                    Source = string.Join('\n', Enumerable.Range(0, 12).Select(line => $"value_{i}_{line} = {line}")) });
            File.WriteAllText(file, NotebookFile.Write(model));
            Open(file);
        });
        test.Settle(0.4f);
        test.Run(() => notebookScroll.ScrollToTop(cellViews[12].Card));
        test.Settle(0.3f);
        test.Run(() => { Capture(); ZoomCode(1.1f); ZoomCode(1.1f); ZoomCode(1.1f); });
        test.Settle(0.4f);
        test.Check("notebook zoom: repeated zoom-in keeps the centre content point", Centred);
        test.Run(() => ZoomCode(0.6f));
        test.Settle(0.4f);
        test.Check("notebook zoom: zoom-out keeps the same centre content point", Centred);
        test.Run(() => SetScale(originalScale == 2 ? 1 : 2, remember: false));
        test.Settle(0.5f);
        test.Check("notebook zoom: viewport resize retains the centre content point", Centred);
        test.Run(() =>
        {
            SetScale(originalScale, remember: false);
            ZoomCode(originalZoom / settings.CodeZoom);
        });
        test.Settle(0.3f);
        test.Check("notebook zoom: restored viewport and zoom retain the centre", Centred);
        test.Run(CloseNow);
    }
}
