namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestRunToCursor(HexPointerScript test, string language)
    {
        string root = Path.Combine(testFolder, "run-to-" + language);
        string file = Path.Combine(root, language == "cpp" ? "main.cpp" : language == "python" ? "main.py" : "Program.cs");
        int entry = language == "cpp" ? 2 : 0, target = language == "cpp" ? 5 : language == "python" ? 3 : 4;
        var setting = new HexBreakpoint(Condition: language == "python" ? "False" : "false");
        void RunTo() { document.Editor.Document.Select(new(target, 0), new(target, 0)); debugger.RunToCursor(); }
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            if (language == "csharp") File.WriteAllText(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, language == "cpp"
                ? "#include <cstdio>\nint main() {\n int value = 0;\n for (int i = 0; i < 3; i++) {\n  value++;\n  std::printf(\"%d\\n\", value);\n }\n std::printf(\"done\\n\");\n}\n"
                : language == "python" ? "value = 0\nfor i in range(3):\n    value += 1\n    print(value)\nprint('done')\n"
                : "int value = 0;\nfor (int i = 0; i < 3; i++)\n{\n value++;\n System.Console.WriteLine(value);\n}\nSystem.Console.WriteLine(\"done\");\n");
            explorer.OpenFolder(root);
            Open(file);
            debugger.ToggleBreakpoint(file, entry);
            debugger.Settle(file, target, setting);
            RunOrPlay();
        });
        test.WaitFor(() => debugger.DebugStopped && document.Editor.ExecutionLine == entry, 45f);
        test.Check("Run to Cursor: native session stops at entry", () => debugger.DebugStopped && document.Editor.ExecutionLine == entry);
        test.Run(RunTo);
        test.WaitFor(() => debugger.DebugStopped && document.Editor.ExecutionLine == target, 15f);
        test.Check("Run to Cursor: temporary stop overrides a false condition at the cursor", () => debugger.DebugStopped && document.Editor.ExecutionLine == target);
        test.Check("Run to Cursor: user's breakpoint settings are unchanged", () => debugger.SettingsOf(file, target) == setting && debugger.breakpoints[file].Count == 2);
        test.Run(RunOrPlay);
        test.WaitFor(() => !debugger.Debugging && said.Any(line => line.Text.Contains("done", StringComparison.Ordinal)), 15f);
        test.Check("Run to Cursor: next iterations do not retain the temporary breakpoint", () => !debugger.Debugging && said.Any(line => line.Text.Contains("done", StringComparison.Ordinal)));
        test.Run(() => { if (debugger.Debugging) debugger.StopDebugging(); });
        test.WaitFor(() => !debugger.Debugging, 10f);
        test.Run(() => { debugger.ToggleBreakpoint(file, target - 1); RunOrPlay(); });
        test.WaitFor(() => debugger.DebugStopped && document.Editor.ExecutionLine == entry, 45f);
        test.Run(RunTo);
        test.WaitFor(() => debugger.DebugStopped && document.Editor.ExecutionLine == target - 1, 15f);
        test.Check("Run to Cursor: other user breakpoints still interrupt", () => debugger.DebugStopped && document.Editor.ExecutionLine == target - 1);
        test.Run(() => { debugger.ToggleBreakpoint(file, target - 1); RunOrPlay(); });
        test.WaitFor(() => !debugger.Debugging, 15f);
        test.Check("Run to Cursor: interrupted temporary stop is removed", () => !debugger.Debugging && debugger.SettingsOf(file, target) == setting);
        test.Run(() => { if (debugger.Debugging) debugger.StopDebugging(); debugger.breakpoints.Clear(); CloseDocument(document); });
    }
}
