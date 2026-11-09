namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestInstructionBreakpoints(HexPointerScript test)
    {
        string root = Path.Combine(testFolder, "instruction breakpoints"), file = Path.Combine(root, "main.cpp");
        const string source = "#include <cstdio>\nint main() {\n int value = 0;\n for (int step = 0; step < 4; ++step) {\n  value += 10;\n  std::printf(\"value %d\\n\", value);\n }\n std::printf(\"instruction-final %d\\n\", value);\n}\n";
        string address = string.Empty;
        Action<int>? oldGutter = null;
        UITreeItem? Points() => sideTree.Items.FirstOrDefault(section => section.Text == Said("debugging.breakpoints"));
        List<UITreeItem> Instructions() => Points()?.Children?.Where(row => row.Text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
        bool Verified(int count = 1) => Instructions().Count == count && Instructions().All(row => row.Detail == Said("debugging.instruction_breakpoint_verified"));
        bool SourceAt(int line) => debugger.DebugStopped && document.Path == file && code.ExecutionLine == line;
        bool InView() => debugger.DebugStopped && HexDebugging.IsDisassemblyPath(document.Path) && code.ReadOnly;
        bool AtAddress() => InView() && code.ExecutionLine == 0 && address.Length > 0
            && DebugSession.SameInstructionAddress(code.Document.LineAt(0).Split(' ')[0], address);
        void Command(string key) => PaletteChoices().FirstOrDefault(command => command.Text == Said(key))?.Picked?.Invoke();
        void Menu(UITreeItem? row) { if (row is not null) sideTree.OnItemContextMenu?.Invoke(row, System.Numerics.Vector2.Zero); }
        void Pick(string key)
        {
            var action = contextMenu?.Showing?.Items.FirstOrDefault(item => item.Label == Said(key))?.OnPicked;
            contextMenu?.Close();
            action?.Invoke();
        }
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(file, source);
            explorer.OpenFolder(root);
            Open(file);
            debugger.ToggleBreakpoint(file, 4);
            debugger.ToggleBreakpoint(file, 5);
            RunOrPlay();
        });
        test.WaitFor(() => SourceAt(4), 60);
        test.Check("Instruction breakpoints: native LLDB source stop is ready", () => SourceAt(4));
        test.Run(() => Command("debugging.disassembly"));
        test.WaitFor(() => InView() && code.OnGutterPressed is not null, 15);
        test.Run(() =>
        {
            if (!InView()) return;
            address = code.Document.LineAt(1).Split(' ')[0];
            oldGutter = code.OnGutterPressed;
            code.OnGutterPressed?.Invoke(1);
        });
        test.WaitFor(() => Verified(), 10);
        test.Check("Instruction breakpoints: a gutter click installs a verified native address", () => address.Length > 0 && Verified()
            && code.Breakpoints?.Contains(1) == true && code.BreakpointMarks?.GetValueOrDefault(1) == CodeBreakpointMark.Plain);
        test.Check("Instruction breakpoints: machine addresses coexist with source breakpoints", () => debugger.breakpoints[file].SetEquals([4, 5])
            && debugger.breakpoints.Keys.All(path => !HexDebugging.IsDisassemblyPath(path)));
        test.Run(() => { code.GoTo(2, 0); Command("debugging.toggle_breakpoint"); });
        test.WaitFor(() => Verified(2), 10);
        test.Check("Instruction breakpoints: F9 adds another instruction without replacing the first", () => Verified(2) && code.Breakpoints?.SetEquals([1, 2]) == true);
        test.Run(() => Command("debugging.toggle_breakpoint"));
        test.WaitFor(() => Verified(), 10);
        test.Check("Instruction breakpoints: F9 removes only the selected address", () => Verified() && code.Breakpoints?.SetEquals([1]) == true);
        test.Run(() => { code.GoTo(1, 0); Command("debugging.enable_disable_breakpoint"); });
        test.WaitFor(() => Instructions().FirstOrDefault()?.Detail == Said("debugging.off"), 10);
        test.Check("Instruction breakpoints: Ctrl+F9 disables the native address and its gutter mark", () => Instructions().FirstOrDefault()?.Detail == Said("debugging.off")
            && code.BreakpointMarks?.GetValueOrDefault(1) == CodeBreakpointMark.Disabled);
        test.Run(() => Command("debugging.enable_disable_breakpoint"));
        test.WaitFor(() => Verified(), 10);
        test.Check("Instruction breakpoints: Ctrl+F9 restores native verification", () => Verified());
        test.Run(RunOrPlay);
        test.WaitFor(AtAddress, 15);
        test.Check("Instruction breakpoints: continuing stops at the selected machine instruction", AtAddress);
        test.Run(() => oldGutter?.Invoke(2));
        test.Settle(0.2f);
        test.Check("Instruction breakpoints: a gutter callback from an earlier stop cannot add an address", () => Verified());
        test.Run(() => { Menu(Instructions().FirstOrDefault()); Pick("debugging.disable_breakpoint"); });
        test.WaitFor(() => Instructions().FirstOrDefault()?.Detail == Said("debugging.off"), 10);
        test.Run(() => { Command("debugging.show_current_frame"); RunOrPlay(); });
        test.WaitFor(() => SourceAt(5), 15);
        test.Check("Instruction breakpoints: disabling preserves normal source breakpoint stops", () => SourceAt(5) && Instructions().FirstOrDefault()?.Detail == Said("debugging.off"));
        test.Run(() => (Instructions().FirstOrDefault()?.Tag as SideTag)?.Pressed?.Invoke());
        test.WaitFor(() => InView() && DebugSession.SameInstructionAddress(code.Document.LineAt(0).Split(' ')[0], address), 10);
        test.Check("Instruction breakpoints: choosing a breakpoint shows its address without moving the execution marker there", () => InView()
            && DebugSession.SameInstructionAddress(code.Document.LineAt(0).Split(' ')[0], address) && code.ExecutionLine != 0 && code.Breakpoints?.Contains(0) == true);
        test.Run(() => Command("debugging.show_current_frame"));
        test.Run(() => { Menu(Points()); Pick("debugging.enable_all"); });
        test.WaitFor(() => Verified(), 10);
        test.Check("Instruction breakpoints: Enable All includes instruction addresses", () => Verified() && debugger.breakpoints[file].All(line => debugger.SettingsOf(file, line).Enabled));
        test.Run(RunOrPlay);
        test.WaitFor(() => SourceAt(4), 15);
        test.Run(() => Command("debugging.disassembly"));
        test.WaitFor(InView, 10);
        test.Run(RunOrPlay);
        test.WaitFor(AtAddress, 15);
        test.Check("Instruction breakpoints: a re-enabled address stops again on the next loop", AtAddress);
        test.Run(() => { oldGutter = code.OnGutterPressed; debugger.RestartDebugging(); });
        test.WaitFor(() => SourceAt(4), 40);
        test.Check("Instruction breakpoints: restart discards session-only addresses while retaining source breakpoints", () => SourceAt(4)
            && Instructions().Count == 0 && debugger.breakpoints[file].SetEquals([4, 5]));
        test.Run(() => Command("debugging.disassembly"));
        test.WaitFor(InView, 10);
        test.Run(() => oldGutter?.Invoke(1));
        test.Settle(0.2f);
        test.Check("Instruction breakpoints: a previous session's gutter cannot modify the new session", () => Instructions().Count == 0);
        test.Run(() =>
        {
            string cache = document.Path!;
            CloseNow();
            Open(cache);
            code.GoTo(1, 0);
            debugger.ToggleBreakpointHere();
        });
        test.Settle(0.2f);
        test.Check("Instruction breakpoints: reopening cached instructions cannot reuse an old address binding", () => Instructions().Count == 0
            && code.ReadOnly && code.OnGutterPressed is null && !PaletteChoices().Any(command => command.Text == Said("debugging.toggle_breakpoint")));
        test.Run(() => Command("debugging.disassembly"));
        test.WaitFor(() => InView() && code.OnGutterPressed is not null, 10);
        test.Run(() => code.OnGutterPressed?.Invoke(1));
        test.WaitFor(() => Verified(), 10);
        test.Run(() => { Menu(Points()); Pick("debugging.disable_all"); });
        test.WaitFor(() => Instructions().FirstOrDefault()?.Detail == Said("debugging.off"), 10);
        test.Check("Instruction breakpoints: Disable All includes instruction and source breakpoints", () => Instructions().FirstOrDefault()?.Detail == Said("debugging.off")
            && debugger.breakpoints[file].All(line => !debugger.SettingsOf(file, line).Enabled));
        test.Run(() => { Menu(Points()); Pick("debugging.enable_all"); });
        test.WaitFor(() => Verified(), 10);
        test.Run(() => { Menu(Points()); Pick("debugging.delete_all"); });
        test.WaitFor(() => Instructions().Count == 0 && debugger.breakpoints[file].Count == 0, 10);
        test.Check("Instruction breakpoints: Delete All clears native addresses and source breakpoints", () => Instructions().Count == 0
            && debugger.breakpoints[file].Count == 0 && code.Breakpoints is { Count: 0 });
        test.Run(RunOrPlay);
        test.WaitFor(() => !debugger.Debugging && said.Any(line => line.Text.Contains("instruction-final 40", StringComparison.Ordinal)), 15);
        test.Run(() =>
        {
            if (!said.Any(line => line.Text.Contains("instruction-final 40", StringComparison.Ordinal)))
                Console.WriteLine("[Hex self-test] Instruction breakpoint output: " + string.Join(" | ", said.TakeLast(12).Select(line => line.Text)));
        });
        test.Check("Instruction breakpoints: final continuation exits with the expected program output", () => !debugger.Debugging
            && said.Any(line => line.Text.Contains("instruction-final 40", StringComparison.Ordinal)) && File.ReadAllText(file) == source);
        test.Check("Instruction breakpoints: ending removes gutter callbacks and session rows", () => code.OnGutterPressed is null && code.Breakpoints is null);
        test.Run(() => { if (debugger.Debugging) debugger.StopDebugging(); debugger.breakpoints.Clear(); });
    }
}
