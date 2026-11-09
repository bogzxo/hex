namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestSetNextStatement(HexPointerScript test, string language)
    {
        bool python = language == "python", cpp = language == "cpp";
        string root = Path.Combine(testFolder, "set next " + language);
        string file = Path.Combine(root, python ? "main.py" : cpp ? "main.cpp" : "Program.cs");
        string source = python ? "value = 1\nvalue += 10\nvalue += 100\nprint(value)\nvalue += 1000\nprint('set-next-result', value)\ndef other():\n    return 99\n"
            : cpp ? "#include <cstdio>\nint main() {\n int value = 1;\n std::printf(\"%d\\n\", value);\n}\n"
            : "int value = 1;\nSystem.Console.WriteLine(value);\n";
        int entry = python ? 1 : cpp ? 2 : 0;
        bool HasCommand() => AllCommands().Any(command => command.Text.Contains(Said("debugging.set_next_statement") + "   (", StringComparison.Ordinal));
        void Move(int line)
        {
            code.Document.Select(new(line, 0), new(line, 0));
            AllCommands().FirstOrDefault(command => command.Text.Contains(Said("debugging.set_next_statement") + "   (", StringComparison.Ordinal)).Picked?.Invoke();
        }
        bool Value(string value) => sideTree.Items.Any(section => section.Children?.Any(row => row.Text == "value = " + value
            && row.Tag is SideTag { DebugCurrent: {} current } && current()) == true);
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            if (language == "csharp") File.WriteAllText(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, source);
            explorer.OpenFolder(root);
            Open(file);
            debugger.ToggleBreakpoint(file, entry);
            RunOrPlay();
        });
        test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == entry, 60f);
        test.Check("Set Next Statement: " + language + " native breakpoint is reached", () => debugger.DebugStopped && code.ExecutionLine == entry);
        test.Check("Set Next Statement: " + language + " command follows native capability", () => HasCommand() == python);
        if (python)
        {
            int before = 0;
            test.Run(() => { Move(3); code.Document.MoveTo(new(2, 0)); });
            test.Settle(0.5f);
            test.Check("Set Next Statement: moving the caret retires a pending target query", () => debugger.DebugStopped && code.ExecutionLine == entry);
            string other = Path.Combine(root, "other.py");
            test.Run(() => { File.WriteAllText(other, "print('other')\n"); Open(other); });
            test.Check("Set Next Statement: another source document cannot move the paused frame", () => !HasCommand());
            test.Run(() => Open(file));
            test.Run(() => { before = said.Count; Move(7); });
            test.WaitFor(() => said.Skip(before).Any(line => line.Error), 10f);
            test.Check("Set Next Statement: adapter rejection explains failure and preserves the stop", () => debugger.DebugStopped
                && code.ExecutionLine == entry && said.Skip(before).Any(line => line.Error));
            test.Run(() =>
            {
                code.Document.MoveTo(new(0, 0));
                code.Document.Type('#');
                before = said.Count;
                Move(3);
            });
            test.Check("Set Next Statement: modified source is rejected before moving execution", () => debugger.DebugStopped
                && code.ExecutionLine == entry && said.Skip(before).Any(line => line.Error && line.Text.Contains("restart", StringComparison.Ordinal)));
            test.Run(() => { code.Document.Undo(); Move(3); });
            test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == 3 && Value("1"), 10f);
            test.Check("Set Next Statement: forward jump skips both intervening assignments and refreshes locals", () => debugger.DebugStopped && code.ExecutionLine == 3 && Value("1"));
            test.Run(() => Move(1));
            test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == 1 && Value("1"), 10f);
            test.Check("Set Next Statement: backward jump moves the instruction pointer without changing values", () => debugger.DebugStopped && code.ExecutionLine == 1 && Value("1"));
            test.Run(debugger.StepOver);
            test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == 2 && Value("11"), 10f);
            test.Check("Set Next Statement: stepping executes the relocated statement normally", () => debugger.DebugStopped && code.ExecutionLine == 2 && Value("11"));
            test.Run(() => Move(3));
            test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == 3 && Value("11"), 10f);
            test.Check("Set Next Statement: jumps preserve breakpoint and source state", () => debugger.DebugStopped && code.ExecutionLine == 3
                && debugger.breakpoints[file].SetEquals([entry]) && code.Text == source && File.ReadAllText(file) == source && !document.Modified);
            test.Run(() => { before = said.Count; RunOrPlay(); });
            test.WaitFor(() => !debugger.Debugging && said.Skip(before).Any(line => line.Text.Contains("set-next-result 1011", StringComparison.Ordinal)), 15f);
            test.Check("Set Next Statement: continuing uses the relocated execution state", () => !debugger.Debugging
                && said.Skip(before).Any(line => line.Text.Contains("set-next-result 1011", StringComparison.Ordinal)));
        }
        test.Run(() => { if (debugger.Debugging) debugger.StopDebugging(); debugger.breakpoints.Clear(); });
        test.WaitFor(() => !debugger.Debugging, 10f);
        test.Check("Set Next Statement: " + language + " command retires with the session", () => !HasCommand());
    }
}
