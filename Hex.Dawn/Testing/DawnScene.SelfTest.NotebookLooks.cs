using Hex.Core.Notebooks;
using Hex.Shell;

using Horizon.UI.Components;

namespace Hex.Dawn;

// A notebook of real outputs (samples/notebooks/review.ipynb, made by a real kernel: a DataFrame, a Matplotlib plot, a
// long stream, a traceback and an empty cell) in every look Hex comes with at 100, 150 and 200%, the cells laid out
// inside the view at each. HEX_SELFTEST_SHOTS names a folder for a picture of its top and of its error at each, and of
// the table, the plot and an empty notebook in Solstice at 100%.
internal sealed partial class DawnScene
{
    private void TestNotebookLooks(HexPointerScript test)
    {
        string file = Path.Combine(testFolder, "review.ipynb");
        string empty = Path.Combine(testFolder, "empty.ipynb");
        string themeBefore = settings.EditorTheme, skinBefore = settings.EditorSkin;
        float scaleBefore = 1.0f;
        test.Run(() =>
        {
            scaleBefore = compositor.Scale;
            File.Copy(Path.Combine(Hex.Core.HexHost.ProgramFolder, "Assets", "hex", "notebooks", "review.ipynb"), file, true);
            Open(file);
        });
        test.Wait();
        test.Wait();
        test.Check("notebook looks: the review notebook comes up as its nine cells", () => NotebookShown && cellViews.Count == 9);

        void Look(string name, int cell)
        {
            test.Run(() => { PickCell(notebookShown!, cell, edit: false); Engine.WindowManager.RequestFrames(); });
            test.Settle(0.5f);
            test.Run(() => Shot(name));
            test.Settle(0.1f);
        }

        foreach (HexLook look in HexLooks.BuiltIn)
        {
            foreach (float scale in (float[])[1.0f, 1.5f, 2.0f])
            {
                string named = $"{look.Name} at {scale * 100:0}%";
                string shot = $"notebook-{look.Name.Replace(' ', '-').ToLowerInvariant()}-{scale * 100:0}";
                test.Run(() =>
                {
                    PickLook(look);
                    SetInterfaceSize(scale, remember: false);
                });
                test.Settle(0.8f);
                test.Check($"notebook looks: {named}, every cell is inside the view's width", () =>
                    cellViews.All(view => view.Card.Size.X > 0 && view.Card.Size.X <= notebookView.Size.X + 0.5f));
                test.Check($"notebook looks: {named}, the view is the look's panel colour, taken again as the look changed", () =>
                    compositor.Skin is { } skin && notebookView.Color == skin.PanelColor with { W = 1.0f });
                test.Check($"notebook looks: {named}, the bar's two sides don't overlap", () =>
                    notebookBar.Bounds.Max.X <= notebookStatus.Bounds.Min.X + 0.5f);
                Look(shot + "-top", 0);
                Look(shot + "-error", 5);
                if (look == HexLooks.Solstice && scale == 1.0f)
                {
                    Look(shot + "-table", 2);
                    Look(shot + "-plot", 3);
                    Look(shot + "-stream", 4);
                }
            }
        }

        test.Run(() =>
        {
            PickLook(HexLooks.Solstice);
            SetInterfaceSize(1.0f, remember: false);
        });
        test.Settle(0.3f);
        TestNotebookCellLooks(test);

        test.Run(() =>
        {
            File.WriteAllText(empty, NotebookFile.Write(Notebook.Empty()));
            Open(empty);
        });
        test.Wait();
        test.Wait();
        test.Run(() => Engine.WindowManager.RequestFrames());
        test.Settle(0.5f);
        test.Check("notebook looks: an empty notebook says how to start under its one cell", () => notebookHint is { Visible: true, Parent: not null } && notebookHint.Text.Contains("Shift+Enter"));
        test.Run(() => Shot("notebook-empty"));
        test.Settle(0.1f);
        CloseNotebookTest(test, empty);
        CloseNotebookTest(test, file);
        test.Run(() =>
        {
            SetInterfaceSize(scaleBefore, remember: false);
            PickLook(HexLooks.Solstice);
            UseEditorSkin(skinBefore, themeBefore, save: false);
        });
        test.Settle(0.3f);
    }
}
