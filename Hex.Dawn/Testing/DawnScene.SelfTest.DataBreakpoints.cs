namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestDataBreakpoints(HexPointerScript test, string language)
    {
        bool cpp = language == "cpp", python = language == "python";
        string root = Path.Combine(testFolder, "data breakpoints " + language);
        string file = Path.Combine(root, cpp ? "main.cpp" : python ? "main.py" : "Program.cs");
        string source = cpp ? "#include <cstdio>\nint main() {\n int value = 1;\n value += 10;\n std::printf(\"%d\\n\", value);\n value += 100;\n std::printf(\"%d\\n\", value);\n value += 1000;\n std::printf(\"data-final %d\\n\", value);\n}\n"
            : python ? "value = 1\nprint(value)\nvalue += 10\nprint(value)\n" : "int value = 1;\nSystem.Console.WriteLine(value);\nvalue += 10;\nSystem.Console.WriteLine(value);\n";
        int entry = cpp ? 3 : 1;
        UITreeItem? Value() => sideTree.Items.FirstOrDefault(section => section.Text == Said("debugging.variables"))?.Children?
            .FirstOrDefault(row => row.Text.StartsWith("value = ", StringComparison.Ordinal) && row.Tag is SideTag { DebugCurrent: {} current } && current());
        UITreeItem? Points() => sideTree.Items.FirstOrDefault(section => section.Text == Said("debugging.breakpoints"));
        UITreeItem? Data() => Points()?.Children?.FirstOrDefault(row => row.Icon is "debug-breakpoint-data" or "debug-breakpoint-data-unverified");
        void Menu(UITreeItem? row) { if (row is not null) sideTree.OnItemContextMenu?.Invoke(row, System.Numerics.Vector2.Zero); }
        void Pick(string key)
        {
            var picked = contextMenu?.Showing?.Items.FirstOrDefault(item => item.Label == Said(key))?.OnPicked;
            contextMenu?.Close();
            picked?.Invoke();
        }
        void Add() { Menu(Value()); Pick("debugging.break_on_change"); }
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            if (language == "csharp") File.WriteAllText(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, source);
            explorer.OpenFolder(root);
            Open(file);
            debugger.ToggleBreakpoint(file, entry);
            if (cpp) debugger.ToggleBreakpoint(file, 7);
            RunOrPlay();
        });
        test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == entry && Value()?.Text == "value = 1", 60f);
        test.Check("Data breakpoints: " + language + " native local is available", () => debugger.DebugStopped && Value()?.Text == "value = 1");
        test.Run(() => Menu(Value()));
        test.Check("Data breakpoints: " + language + " action follows adapter capability", () =>
            (contextMenu?.Showing?.Items.Any(item => item.Label == Said("debugging.break_on_change")) == true) == cpp);
        test.Run(() => contextMenu?.Close());
        if (cpp)
        {
            Action? stale = null;
            test.Run(() =>
            {
                Menu(Value());
                stale = contextMenu?.Showing?.Items.FirstOrDefault(item => item.Label == Said("debugging.break_on_change"))?.OnPicked;
                contextMenu?.Close();
                debugger.StepOver();
            });
            test.WaitFor(() => debugger.DebugStopped && Value()?.Text == "value = 11", 10f);
            test.Run(() => stale?.Invoke());
            test.Settle(0.2f);
            test.Check("Data breakpoints: an old variable menu cannot install a watchpoint at the next stop", () => Data() is null);
            test.Run(debugger.RestartDebugging);
            test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == entry && Value()?.Text == "value = 1", 30f);
            test.Run(Add);
            test.WaitFor(() => Data()?.Detail == Said("debugging.data_breakpoint_verified"), 10f);
            test.Check("Data breakpoints: native write watchpoint is verified", () => Data()?.Detail == Said("debugging.data_breakpoint_verified"));
            test.Run(RunOrPlay);
            test.WaitFor(() => debugger.DebugStopped && Value()?.Text == "value = 11", 10f);
            test.Check("Data breakpoints: a native memory write stops execution and refreshes the local", () => debugger.DebugStopped && Value()?.Text == "value = 11");
            test.Run(() => { Menu(Data()); Pick("debugging.disable_breakpoint"); });
            test.WaitFor(() => Data()?.Detail == Said("debugging.off"), 10f);
            test.Run(RunOrPlay);
            test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == 7 && Value()?.Text == "value = 111", 10f);
            test.Check("Data breakpoints: disabling skips the next write while source breakpoints still stop", () => debugger.DebugStopped
                && code.ExecutionLine == 7 && Value()?.Text == "value = 111" && Data()?.Detail == Said("debugging.off"));
            test.Run(() => { Menu(Data()); Pick("debugging.enable_breakpoint"); });
            test.WaitFor(() => Data()?.Detail == Said("debugging.data_breakpoint_verified"), 10f);
            test.Run(RunOrPlay);
            test.WaitFor(() => debugger.DebugStopped && Value()?.Text == "value = 1111", 10f);
            test.Check("Data breakpoints: re-enabling stops at the next native write", () => debugger.DebugStopped && Value()?.Text == "value = 1111");
            test.Run(() => { Menu(Data()); Pick("debugging.delete_breakpoint"); });
            test.WaitFor(() => Data() is null, 10f);
            test.Check("Data breakpoints: removing a watchpoint preserves source breakpoints", () => Data() is null && debugger.breakpoints[file].SetEquals([entry, 7]));
            test.Run(Add);
            test.WaitFor(() => Data()?.Detail == Said("debugging.data_breakpoint_verified"), 10f);
            test.Run(() => { Menu(Points()); Pick("debugging.disable_all"); });
            test.WaitFor(() => Data()?.Detail == Said("debugging.off"), 10f);
            test.Check("Data breakpoints: Disable All includes native and source breakpoints", () => Data()?.Detail == Said("debugging.off")
                && debugger.breakpoints[file].All(line => !debugger.SettingsOf(file, line).Enabled));
            test.Run(() => { Menu(Points()); Pick("debugging.enable_all"); });
            test.WaitFor(() => Data()?.Detail == Said("debugging.data_breakpoint_verified"), 10f);
            test.Check("Data breakpoints: Enable All restores native and source breakpoints", () => Data()?.Detail == Said("debugging.data_breakpoint_verified")
                && debugger.breakpoints[file].All(line => debugger.SettingsOf(file, line).Enabled));
            test.Run(() => { Menu(Points()); Pick("debugging.delete_all"); });
            test.WaitFor(() => Data() is null, 10f);
            test.Check("Data breakpoints: Delete All clears native and source breakpoints", () => Data() is null && debugger.breakpoints[file].Count == 0);
            test.Run(RunOrPlay);
            test.WaitFor(() => !debugger.Debugging && said.Any(line => line.Text.Contains("data-final 1111", StringComparison.Ordinal)), 15f);
            test.Check("Data breakpoints: execution continues with the watched value", () => !debugger.Debugging
                && said.Any(line => line.Text.Contains("data-final 1111", StringComparison.Ordinal)));
            test.Run(() => { debugger.ToggleBreakpoint(file, entry); RunOrPlay(); });
            test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == entry && Value()?.Text == "value = 1", 30f);
            test.Check("Data breakpoints: memory watchpoints do not leak into a restarted session", () => debugger.DebugStopped && Value()?.Text == "value = 1" && Data() is null);
        }
        test.Check("Data breakpoints: " + language + " source text stays unchanged", () => code.Text == source && File.ReadAllText(file) == source);
        test.Run(() => { if (debugger.Debugging) debugger.StopDebugging(); debugger.breakpoints.Clear(); });
        test.WaitFor(() => !debugger.Debugging, 10f);
    }
}
