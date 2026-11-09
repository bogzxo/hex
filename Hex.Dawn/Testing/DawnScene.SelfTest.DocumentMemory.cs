using System.Diagnostics;
using System.Globalization;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    // A long-running observation of the normal application GC policy. Unlike document-retention, this never
    // forces a collection or waits for finalizers. Passing means the workload completed, not that memory plateaued.
    private void TestDocumentMemory(HexPointerScript test)
    {
        const int cycles = 60;
        double seconds = 1800;
        if (double.TryParse(Environment.GetEnvironmentVariable("HEX_MEMORY_SESSION_SECONDS"), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double requested) && double.IsFinite(requested) && requested >= 10 && requested <= 86400)
            seconds = requested;
        var elapsed = new Stopwatch();
        var closed = new List<WeakReference>();
        string text = Path.Combine(testFolder, "memory-session.txt");
        string notebook = Path.Combine(testFolder, "memory-session.ipynb");
        int initialCount = 0, completed = 0;

        void Sample(int cycle, string phase)
        {
            using var process = Process.GetCurrentProcess();
            HexLogLine(FormattableString.Invariant($"memory-session cycle={cycle} phase={phase} elapsedMs={elapsed.Elapsed.TotalMilliseconds:F0} managed={GC.GetTotalMemory(false)} private={process.PrivateMemorySize64} working={process.WorkingSet64} gen0={GC.CollectionCount(0)} gen1={GC.CollectionCount(1)} gen2={GC.CollectionCount(2)} pauseMs={GC.GetTotalPauseDuration().TotalMilliseconds:F1} closedAlive={closed.Count(item => item.IsAlive)}"));
        }

        test.Run(() =>
        {
            File.WriteAllText(text, string.Join('\n', Enumerable.Range(0, 5000).Select(i => $"line {i}: normal document memory workload")));
            File.Copy(Path.Combine(Hex.Core.HexHost.ProgramFolder, "Assets", "hex", "notebooks", "engineering.ipynb"), notebook, true);
            initialCount = documents.Count;
            elapsed.Start();
            HexLogLine(FormattableString.Invariant($"memory-session requestedSeconds={seconds:F0} cycles={cycles} testForcedCollections=false gcPolicy=application"));
            Sample(0, "start");
        });
        for (int cycle = 1; cycle <= cycles; cycle++)
        {
            int current = cycle;
            test.Run(() => Open(current % 2 == 0 ? notebook : text));
            test.WaitFor(() => notebookRenders.Count == 0, 30);
            test.Run(() =>
            {
                if (notebookShown is { } shown)
                {
                    shown.Model.Cells[0].Source += $"\nMemory session {current}.";
                    shown.Remake = true;
                    NotebookChanged(shown, now: true);
                }
                else
                {
                    code.GoTo(2500, 0);
                    code.Document.Type('x');
                }
            });
            test.Settle(0.25f);
            test.Run(Save);
            test.WaitFor(() => !document.Modified && notebookRenders.Count == 0, 30);
            test.Check($"document memory: cycle {current} saves its edit", () => !document.Modified &&
                (current % 2 == 0
                    ? File.ReadAllText(notebook).Contains($"Memory session {current}.", StringComparison.Ordinal)
                    : File.ReadLines(text).ElementAt(2500).StartsWith(new string('x', (current + 1) / 2) + "line 2500:", StringComparison.Ordinal)));
            test.Run(() => Sample(current, "open"));
            test.Settle((float)(seconds / cycles / 2));
            test.Run(() =>
            {
                closed.Add(new WeakReference(document));
                CloseNow();
                completed++;
            });
            test.Settle((float)(seconds / cycles / 2));
            test.Run(() => Sample(current, "closed"));
        }
        test.Check("document memory: sixty edit/save/close cycles completed", () => completed == cycles && documents.Count == initialCount);
        test.Check("document memory: requested observation time elapsed", () => elapsed.Elapsed.TotalSeconds >= seconds * 0.99);
    }
}
