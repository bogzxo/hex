namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestDisassembly(HexPointerScript test, string language)
    {
        bool cpp = language == "cpp", python = language == "python";
        string root = Path.Combine(testFolder, "disassembly " + language);
        string file = Path.Combine(root, cpp ? "main.cpp" : python ? "main.py" : "Program.cs"), notes = Path.Combine(root, "notes.txt");
        string source = cpp ? "#include <cstdio>\nint twice(int value) {\n int result = value * 2;\n return result;\n}\nint main() {\n int value = 3;\n value = twice(value);\n std::printf(\"disassembly-result %d\\n\", value);\n}\n"
            : python ? "value = 3\nprint(value)\nvalue += 1\nprint(value)\n" : "int value = 3;\nSystem.Console.WriteLine(value);\nvalue += 1;\nSystem.Console.WriteLine(value);\n";
        int entry = cpp ? 7 : 1;
        bool HasCommand() => AllCommands().Any(command => command.Text.EndsWith(Said("debugging.disassembly"), StringComparison.Ordinal));
        void Ask() => AllCommands().FirstOrDefault(command => command.Text.EndsWith(Said("debugging.disassembly"), StringComparison.Ordinal)).Picked?.Invoke();
        bool InDisassembly() => HexDebugging.IsDisassemblyPath(document.Path) && code.ReadOnly && code.ExecutionLine == 0;
        string view = string.Empty, previous = string.Empty;
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            if (language == "csharp") File.WriteAllText(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, source);
            File.WriteAllText(notes, "Plain notes\n");
            explorer.OpenFolder(root);
            Open(file);
            debugger.ToggleBreakpoint(file, entry);
            RunOrPlay();
        });
        test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == entry, 60);
        test.Check("Disassembly: " + language + " native debugger stops at source", () => debugger.DebugStopped && code.ExecutionLine == entry);
        test.Check("Disassembly: " + language + " command follows native adapter capability", () => HasCommand() == cpp);
        if (cpp)
        {
            test.Run(() => { Ask(); Open(notes); });
            test.Settle(0.5f);
            test.Check("Disassembly: changing documents rejects a pending native view", () => document.Path == notes);
            test.Run(() => { Open(file); Ask(); });
            test.WaitFor(InDisassembly, 15);
            test.Check("Disassembly: native addresses, bytes and instructions open read-only", () => InDisassembly()
                && code.Document.LineCount > 5 && code.Document.LineAt(0).StartsWith("0x", StringComparison.OrdinalIgnoreCase));
            test.Check("Disassembly: instruction views do not create source-file breakpoint storage", () => code.Breakpoints is { Count: 0 }
                && debugger.breakpoints.Keys.All(path => !HexDebugging.IsDisassemblyPath(path)));
            test.Run(() =>
            {
                if (!InDisassembly()) return;
                view = document.Path!;
                previous = code.Text;
                typeof(CodeEditor).GetMethod("OnTextInput", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(code, ['x']);
                SaveFormatted();
            });
            test.Check("Disassembly: reading instructions cannot edit or save generated text", () => view.Length > 0 && code.Text == previous && !document.Modified
                && File.ReadAllText(view) == previous && File.ReadAllText(file) == source);
            test.Run(debugger.StepOver);
            test.WaitFor(() => debugger.DebugStopped && InDisassembly() && code.Text != previous, 15);
            test.Check("Disassembly: native instruction Step Over advances the address in the same view", () => debugger.DebugStopped && InDisassembly()
                && document.Path == view && code.Text != previous && documents.Count(open => open.Path == view) == 1);
            test.Run(() => { previous = code.Text; debugger.StepIn(); });
            test.WaitFor(() => debugger.DebugStopped && InDisassembly() && code.Text != previous, 15);
            test.Check("Disassembly: native instruction Step Into refreshes the instruction pointer", () => debugger.DebugStopped && InDisassembly() && code.Text != previous);
            test.Run(() => AllCommands().FirstOrDefault(command => command.Text.EndsWith(Said("debugging.show_current_frame"), StringComparison.Ordinal)).Picked?.Invoke());
            test.Check("Disassembly: Show Current Frame returns to source", () => document.Path == file && code.ExecutionLine >= 0 && !code.ReadOnly);
            test.Run(() =>
            {
                File.Move(file, file + ".hidden");
                var frame = sideTree.Items.FirstOrDefault(section => section.Text == Said("debugging.call_stack"))?.Children?.FirstOrDefault();
                (frame?.Tag as SideTag)?.Pressed?.Invoke();
            });
            test.WaitFor(InDisassembly, 15);
            test.Check("Disassembly: a selected frame without a source file falls back to native instructions", InDisassembly);
            test.Run(() =>
            {
                File.Move(file + ".hidden", file);
                CloseNow();
                Open(view);
            });
            test.Check("Disassembly: reopening its cached file remains read-only", () => code.ReadOnly && document.Path == view && !document.Modified);
            test.Run(() => { Ask(); debugger.StopDebugging(); Open(notes); });
            test.WaitFor(() => !debugger.Debugging, 10);
            test.Settle(0.5f);
            test.Check("Disassembly: stopping retires pending views and execution markers", () => document.Path == notes && !HasCommand()
                && documents.All(open => open.Editor.ExecutionLine < 0));
        }
        test.Run(() => { if (debugger.Debugging) debugger.StopDebugging(); debugger.breakpoints.Clear(); });
        test.WaitFor(() => !debugger.Debugging, 10);
        test.Check("Disassembly: " + language + " command is absent after the session", () => !HasCommand());
    }
}
