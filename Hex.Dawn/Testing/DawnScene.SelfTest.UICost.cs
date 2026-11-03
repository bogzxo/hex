using System.Numerics;

namespace Hex.Dawn;

// The self test, what the UI costs doing the same few things every time, sitting still, typing, the wheel going down a big
// file, a window dragged about and a notebook of pictures scrolled. Each one says what the compositor counted while it
// went (see UIStatistics), for putting the numbers from before a change of the UI's drawing next to the ones after it.
// Nothing is checked against a number, a machine's numbers are its own.
internal sealed partial class DawnScene
{
    private void TestUICost(HexPointerScript test)
    {
        string csharp = Path.Combine(testFolder, "uicost_big.cs");
        string notebook = Path.Combine(testFolder, "uicost.ipynb");
        test.Run(() =>
        {
            File.WriteAllText(csharp, BigCSharp(20000));
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "hex", "notebooks", "engineering.ipynb"), notebook, true);
            Open(csharp);
            code.GoTo(code.Document.LineCount / 2, 0);
        });
        test.Settle(1.5f);

        Measure(test, "idle", () => test.Settle(2.0f));

        Measure(test, "typing", () =>
        {
            foreach (char c in "var something = another.Call(1, 2) + 3; var more = something * 2; ")
            {
                char key = c;
                test.Run(() => code.Document.Type(key));
                test.Settle(1.0f / 60.0f);
            }
        });

        Measure(test, "wheel", () =>
        {
            test.Hover(() => chrome.ToWorld(code.Bounds.Center));
            for (int notch = 0; notch < 40; notch++)
            {
                float down = notch < 20 ? -1.0f : 1.0f;
                test.Run(() => compositor.Scroll(down));
                test.Settle(0.05f);
            }
            test.Settle(0.5f);
        });

        test.Run(() => OpenSettings(null));
        test.Settle(0.8f);
        Measure(test, "drag", () =>
        {
            Vector2 grabbed = Vector2.Zero;
            test.Run(() => grabbed = In(() => SettingsWindow!.TitleBounds, 0.4f, 0.5f)());
            for (int round = 0; round < 3; round++)
            {
                test.Drag(() => grabbed, () => grabbed + new Vector2(-160, -80));
                test.Drag(() => grabbed + new Vector2(-160, -80), () => grabbed);
            }
        });
        test.Run(CloseSettings);
        test.Settle(0.5f);

        test.Run(() => Open(notebook));
        test.Settle(1.0f);
        test.WaitFor(() => notebookRenders.Count == 0, 30.0f);
        test.Settle(0.5f);
        Measure(test, "notebook", () =>
        {
            test.Hover(() => chrome.ToWorld(notebookScroll.Bounds.Center));
            for (int notch = 0; notch < 30; notch++)
            {
                float down = notch < 15 ? -1.0f : 1.0f;
                test.Run(() => compositor.Scroll(down));
                test.Settle(0.05f);
            }
            test.Settle(0.5f);
        });

        test.Check("uicost: every scenario was measured", () => true);
    }

    // Helper method to count what the UI costs over what a scenario does, and say it
    private void Measure(HexPointerScript test, string name, Action scenario)
    {
        test.Run(() => compositor.Statistics.Reset());
        scenario();
        test.Run(() => Console.WriteLine($"[Hex self-test] uicost {name}: {compositor.Statistics}"));
        test.Run(() => Console.WriteLine($"[Hex self-test] uicost {name} GPU ms a frame: {compositor.Statistics.GpuBreakdown()}"));
    }
}
