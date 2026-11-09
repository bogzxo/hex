using System.Diagnostics;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestSourceDocumentRetention(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "source-retention");
        string[] files = [Path.Combine(root, "Retention.cs"), Path.Combine(root, "retention.py"), Path.Combine(root, "retention.cpp")];
        var models = new List<WeakReference>();
        var editors = new List<WeakReference>();
        var closed = new List<WeakReference>();
        int initialCount = 0;
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "Retention.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
            for (int i = 0; i < files.Length; i++)
            {
                string prefix = i == 1 ? "#" : "//";
                string declaration = i == 0 ? "class Retention { public static int Value() => 42; }" : i == 1 ? "def value(): return 42" : "int value() { return 42; }";
                File.WriteAllText(files[i], declaration + "\n" + string.Join('\n', Enumerable.Range(0, 5000).Select(line => $"{prefix} source retention line {line}")));
            }
            explorer.OpenFolder(root);
            initialCount = documents.Count;
        });
        for (int cycle = 0; cycle < 18; cycle++)
        {
            int language = cycle % files.Length;
            test.Run(() => Open(files[language]));
            test.Settle(0.3f);
            test.Run(() =>
            {
                foreach (int line in code.Breakpoints?.ToArray() ?? []) debugger.ToggleBreakpoint(files[language], line);
                debugger.Settle(files[language], 0, new HexBreakpoint(Condition: "true"));
                debugger.Settle(files[language], 1, new HexBreakpoint(LogMessage: "retention probe", Enabled: false));
                for (int edit = 0; edit < 20; edit++)
                {
                    code.Document.MoveTo(new(0, 0));
                    code.Document.Insert((language == 1 ? "#" : "//") + " history edit\n");
                    code.Document.Undo();
                    code.Document.Redo();
                }
                code.Document.Select(new(0, 0), new(22, 0));
                code.Document.Delete();
                code.Document.Undo();
                code.Document.Redo();
                models.Add(new WeakReference(code.Document));
                editors.Add(new WeakReference(code));
                closed.Add(new WeakReference(document));
                CloseNow();
            });
            test.Settle(0.3f);
        }
        // Let pending language-service work observe document closure before measuring ownership.
        test.Settle(5f);
        test.Run(() =>
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            HexLogLine($"source retention: modelsAlive={models.Count(reference => reference.IsAlive)} editorsAlive={editors.Count(reference => reference.IsAlive)} documentsAlive={closed.Count(reference => reference.IsAlive)}");
        });
        test.Check("source retention: all eighteen C#/Python/C++ text models and undo histories are collectible", () => models.Count == 18 && models.All(reference => !reference.IsAlive));
        test.Check("source retention: breakpoint subscriptions do not retain closed editors", () => editors.All(reference => !reference.IsAlive));
        test.Check("source retention: all closed source documents are collectible", () => closed.All(reference => !reference.IsAlive));
        test.Check("source retention: source cycles restore the original tab count", () => documents.Count == initialCount);
    }

    private void TestDocumentRetention(HexPointerScript test)
    {
        var closedDocuments = new List<WeakReference>();
        var closedEditors = new List<WeakReference>();
        var closedNotebooks = new List<WeakReference>();
        string text = Path.Combine(testFolder, "retention.txt");
        string notebook = Path.Combine(testFolder, "retention.ipynb");
        int initialCount = 0;
        void Sample(int cycle)
        {
            // Deliberate full collections measure retention, not normal interactive GC latency.
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            using var process = Process.GetCurrentProcess();
            HexLogLine($"retention cycle={cycle} managed={GC.GetTotalMemory(false)} private={process.PrivateMemorySize64} working={process.WorkingSet64} " +
                $"documentsAlive={closedDocuments.Count(r => r.IsAlive)} editorsAlive={closedEditors.Count(r => r.IsAlive)} notebooksAlive={closedNotebooks.Count(r => r.IsAlive)}");
        }
        test.Run(() =>
        {
            File.WriteAllText(text, string.Join('\n', Enumerable.Range(0, 5000).Select(i => $"line {i}: document lifecycle retention fixture")));
            File.Copy(Path.Combine(Hex.Core.HexHost.ProgramFolder, "Assets", "hex", "notebooks", "engineering.ipynb"), notebook, true);
            initialCount = documents.Count;
            Sample(0);
        });
        for (int cycle = 1; cycle <= 20; cycle++)
        {
            int n = cycle;
            test.Run(() => Open(n % 2 == 0 ? notebook : text));
            test.Settle(0.25f);
            test.WaitFor(() => notebookRenders.Count == 0, 15);
            test.Run(() =>
            {
                closedDocuments.Add(new WeakReference(document));
                closedEditors.Add(new WeakReference(document.Editor));
                if (notebookShown is { } shown) closedNotebooks.Add(new WeakReference(shown));
                CloseNow();
            });
            test.Settle(0.25f);
            if (cycle % 5 == 0) test.Run(() => Sample(n));
        }
        test.Settle(2);
        test.Run(() => Sample(21));

        test.Check("document retention: all twenty closed documents are collectible", () => closedDocuments.All(r => !r.IsAlive));
        test.Check("document retention: all closed editors are collectible", () => closedEditors.All(r => !r.IsAlive));
        test.Check("document retention: all closed notebook sessions are collectible", () => closedNotebooks.Count == 10 && closedNotebooks.All(r => !r.IsAlive));
        test.Check("document retention: original tab count is restored", () => documents.Count == initialCount);
    }
}
