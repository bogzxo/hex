namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestFunctionBreakpoints(HexPointerScript test, string language)
    {
        bool python = language == "python", cpp = language == "cpp";
        string root = Path.Combine(testFolder, "function breakpoint " + language);
        string file = Path.Combine(root, python ? "main.py" : cpp ? "main.cpp" : "Program.cs");
        string name = python || cpp ? "target" : "Program.Target";
        string source = python ? "def target(value):\n    print(value)\ntarget(1)\ntarget(2)\n"
            : cpp ? "#include <cstdio>\nvoid target(int value) {\n std::printf(\"%d\\n\", value);\n}\nint main() {\n target(1);\n target(2);\n}\n"
            : "class Program {\n static void Target(int value) {\n  System.Console.WriteLine(value);\n }\n static void Main() {\n  Target(1);\n  Target(2);\n }\n}\n";
        int caller = Array.FindIndex(source.Split('\n'), line => line.Contains(python || cpp ? "target(2)" : "Target(2)", StringComparison.Ordinal));
        UITreeItem? Row() => sideTree.Items.FirstOrDefault(section => section.Text == Said("debugging.breakpoints"))?.Children?.FirstOrDefault(item => item.Text == name);
        bool InTarget() => debugger.DebugStopped && debugger.stoppedFrames.Count > 0 && debugger.stoppedFrames[0].Name.Contains(python || cpp ? "target" : "Target", StringComparison.Ordinal);
        void Choose(string key)
        {
            if (Row() is not {} row) return;
            sideTree.OnItemContextMenu?.Invoke(row, System.Numerics.Vector2.Zero);
            var action = contextMenu?.Showing?.Items.FirstOrDefault(item => item.Label == Said(key))?.OnPicked;
            contextMenu?.Close();
            action?.Invoke();
        }
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            if (!python && !cpp) File.WriteAllText(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, source);
            explorer.OpenFolder(root);
            Open(file);
            AllCommands().FirstOrDefault(command => command.Text.EndsWith(Said("debugging.add_function_breakpoint"), StringComparison.Ordinal)).Picked?.Invoke();
        });
        test.Run(() =>
        {
            if (textDialog is not { IsOpen: true } dialog) return;
            var input = Descendants(dialog.Body).OfType<TextBox>().First();
            input.Text = name;
            input.OnSubmitted?.Invoke(name);
        });
        test.Check("Function breakpoint: " + language + " named breakpoint appears before launch", () => Row() is not null && code.Breakpoints is { Count: 0 });
        test.Run(() => { debugger.ToggleBreakpoint(file, caller); RunOrPlay(); });
        test.WaitFor(InTarget, 60f);
        test.Check("Function breakpoint: " + language + " stops in the named function", InTarget);
        test.WaitFor(() => debugger.FunctionBreakpointStatus(name)?.Verified == true, 5f);
        test.Check("Function breakpoint: " + language + " adapter verification reaches the UI", () => debugger.FunctionBreakpointStatus(name)?.Verified == true
            && Row()?.Icon == "debug-breakpoint-function");
        test.Run(() => Choose("debugging.disable_breakpoint"));
        test.WaitFor(() => debugger.FunctionBreakpointStatus(name) is null && Row()?.Icon == "circle", 5f);
        test.Check("Function breakpoint: " + language + " disabling removes the native function breakpoint", () => debugger.FunctionBreakpointStatus(name) is null && Row()?.Icon == "circle");
        test.Run(RunOrPlay);
        test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == caller, 10f);
        test.Check("Function breakpoint: " + language + " source breakpoint remains active", () => debugger.DebugStopped && code.ExecutionLine == caller && !InTarget());
        test.Run(() => { Choose("debugging.enable_breakpoint"); debugger.ToggleBreakpoint(file, caller); });
        test.WaitFor(() => debugger.FunctionBreakpointStatus(name)?.Verified == true, 5f);
        test.Run(RunOrPlay);
        test.WaitFor(InTarget, 10f);
        test.Check("Function breakpoint: " + language + " re-enabling stops at the next call", InTarget);
        test.Run(() => Choose("debugging.delete_breakpoint"));
        test.WaitFor(() => Row() is null && debugger.FunctionBreakpointStatus(name) is null, 5f);
        test.Check("Function breakpoint: " + language + " removal updates both UI and adapter", () => Row() is null && debugger.FunctionBreakpointStatus(name) is null);
        test.Run(RunOrPlay);
        test.WaitFor(() => !debugger.Debugging, 15f);
        test.Check("Function breakpoint: " + language + " continues without modifying source", () => !debugger.Debugging && code.Text == source && File.ReadAllText(file) == source);
        test.Run(() => { if (debugger.Debugging) debugger.StopDebugging(); debugger.breakpoints.Clear(); });
    }
}
