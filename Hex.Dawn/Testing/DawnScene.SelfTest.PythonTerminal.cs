using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestPythonTerminal(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "python terminal");
        string environment = Path.Combine(root, "custom environment");
        string interpreter = PythonEnvironment.ExecutableIn(environment);
        Task? setup = null;
        TerminalView? original = null, selected = null;
        test.Run(() => setup = Task.Run(async () =>
        {
            Directory.CreateDirectory(root);
            var result = await CMakeProject.Run("python", root, ["-m", "venv", environment], default);
            if (result.ExitCode != 0) throw new IOException(result.Output);
            File.WriteAllText(Path.Combine(root, "main.py"), "print('hello')\n");
        }));
        test.WaitFor(() => setup?.IsCompleted == true, 45f);
        test.Check("Python terminal: isolated environment created with pip", () => setup?.IsCompletedSuccessfully == true);
        test.Run(() =>
        {
            explorer.OpenFolder(root);
            Open(Path.Combine(root, "main.py"));
            terminalPanel.AnimationTime = 0;
            NewTerminal();
            original = terminalPanel.FocusedView;
        });
        test.WaitFor(() => original?.Screen.AtPrompt == true, 15f);
        test.Run(() => original?.Send(Probe("original.txt")));
        test.WaitFor(() => File.Exists(Path.Combine(root, "original.txt")), 10f);
        test.Run(() =>
        {
            PythonEnvironment.Select(root, interpreter);
            OpenPythonTerminal();
            selected = terminalPanel.FocusedView;
        });
        test.WaitFor(() => selected?.Screen.AtPrompt == true, 15f);
        test.Check("Python terminal: opens a separate shell", () => selected is not null && !ReferenceEquals(original, selected));
        test.Run(() => selected?.Send(Probe("selected.txt")));
        test.WaitFor(() => File.Exists(Path.Combine(root, "selected.txt")), 10f);
        test.Check("Python terminal: python resolves to the selected interpreter", () => PlatformPaths.Same(Read("selected.txt"), interpreter));
        test.WaitFor(() => selected?.Screen.AtPrompt == true, 10f);
        test.Run(() => selected?.Send("pip --version > pip-proof.txt\r"));
        test.WaitFor(() => Read("pip-proof.txt").Contains("site-packages"), 15f);
        test.Check("Python terminal: pip belongs to the selected environment", () => Read("pip-proof.txt").Contains(environment, StringComparison.OrdinalIgnoreCase));
        test.Run(() => original?.Send(Probe("original-after.txt")));
        test.WaitFor(() => File.Exists(Path.Combine(root, "original-after.txt")), 10f);
        test.Check("Python terminal: existing shell keeps its interpreter", () => Read("original.txt").Length > 0 && Read("original-after.txt") == Read("original.txt") && !PlatformPaths.Same(Read("original-after.txt"), interpreter));
        TerminalView? split = null, restored = null;
        test.Run(() =>
        {
            if (selected is not null) terminalPanel.FocusPane(selected);
            split = terminalPanel.Split(SplitOrientation.Horizontal);
        });
        test.WaitFor(() => split?.Screen.AtPrompt == true, 15f);
        test.Run(() => split?.Send(Probe("split.txt")));
        test.WaitFor(() => File.Exists(Path.Combine(root, "split.txt")), 10f);
        test.Check("Python terminal: split retains interpreter", () => PlatformPaths.Same(Read("split.txt"), interpreter));
        test.Run(() =>
        {
            var kept = TerminalSessions.Read(TerminalSessions.Write(terminalPanel));
            foreach (var tab in terminalPanel.Tabs.ToArray()) terminalPanel.CloseTab(tab);
            if (kept is { } session) TerminalSessions.Restore(terminalPanel, session, RestoredTerminalProfile);
            restored = terminalPanel.FocusedView;
        });
        test.WaitFor(() => restored?.Screen.AtPrompt == true, 15f);
        test.Check("Python terminal: session restores split layout", () => terminalPanel.Tabs.Count == 2 && terminalPanel.Tabs.Last().Tree.Count == 2);
        test.Run(() => restored?.Send(Probe("restored.txt")));
        test.WaitFor(() => File.Exists(Path.Combine(root, "restored.txt")), 10f);
        test.Check("Python terminal: restored shell retains interpreter", () => PlatformPaths.Same(Read("restored.txt"), interpreter));
        test.Check("Python terminal: missing interpreter never falls back to global shell", () =>
            RestoredTerminalProfile(PythonProfile(terminals.UsualProfile!, Path.Combine(root, "missing.exe")).Id)?.Kind == TerminalProfileKind.Embedded);
        test.Run(() =>
        {
            PythonEnvironment.Select(root, null);
            foreach (var tab in terminalPanel.Tabs.ToArray()) terminalPanel.CloseTab(tab);
        });
        static string Probe(string output) => "python -c \"import pathlib,sys; pathlib.Path('" + output + "').write_text(sys.executable)\"\r";
        string Read(string name) { try { return File.ReadAllText(Path.Combine(root, name)).Trim(); } catch (IOException) { return ""; } }
    }
}
