namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestDebugPause(HexPointerScript test) => TestDebugPause(test, "csharp");

    private void TestDebugPause(HexPointerScript test, string language, bool switchWorkspace = false)
    {
        string root = Path.Combine(testFolder, "debug pause " + language + (switchWorkspace ? " workspace" : "")), ready = Path.Combine(root, "ready");
        string file = Path.Combine(root, language == "python" ? "main.py" : language == "cpp" ? "main.cpp" : "Program.cs");
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            if (language == "csharp") File.WriteAllText(Path.Combine(root, "Pause.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, language == "cpp" ? "#include <cstdio>\n#include <thread>\n#include <chrono>\nint main() { auto file = std::fopen(\"ready\", \"w\"); std::fclose(file);\nwhile (true) std::this_thread::sleep_for(std::chrono::milliseconds(10)); }\n" : language == "python" ? "from pathlib import Path\nimport time\nPath('ready').write_text('ready')\nwhile True:\n    time.sleep(0.01)\n" : "System.IO.File.WriteAllText(\"ready\", System.Environment.ProcessId.ToString());\nwhile (true) { System.Threading.Thread.Sleep(10); }\n");
            explorer.OpenFolder(root);
            Open(file);
            RunOrPlay();
        });
        test.WaitFor(() => debugger.Debugging && File.Exists(ready), 45f);
        test.Check("Pause: menu command available while " + language + " runs", () => AllCommands().Any(command => command.Text.EndsWith(Said("debug_bar.pause"), StringComparison.Ordinal)));
        test.Run(() => AllCommands().First(command => command.Text.EndsWith(Said("debug_bar.pause"), StringComparison.Ordinal)).Picked?.Invoke());
        test.WaitFor(() => debugger.DebugStopped && debugger.stoppedFrames.Count > 0, 15f);
        test.Run(() => { if (!debugger.DebugStopped) foreach (var entry in said.TakeLast(12)) Console.WriteLine("[Pause probe] " + entry.Text); });
        test.Check("Pause: menu stops " + language + " before any breakpoint", () => debugger.DebugStopped && debugger.stoppedFrames.Count > 0);
        test.Check("Pause: call stack and continue control appear", () => sideTree.Items.Any(section => section.Text == Said("debugging.call_stack") && section.Children is { Count: > 0 })
            && debugContinue?.Icon == "debug-continue");
        test.Check("Stack copy: command is available while paused", () => AllCommands().Any(command => command.Text.EndsWith(Said("debugging.copy_call_stack"), StringComparison.Ordinal)));
        test.Check("Stack copy: paused native frames are included", () => debugger.stoppedFrames.All(frame => debugger.CurrentStackText().Contains(frame.Name, StringComparison.Ordinal)) && debugger.CurrentStackText().Length > 0);
        test.Run(() => AllCommands().First(command => command.Text.Contains(Said("debugging.continue"), StringComparison.Ordinal)).Picked?.Invoke());
        test.WaitFor(() => debugger.Debugging && !debugger.DebugStopped && debugContinue?.Icon == "debug-pause", 10f);
        test.Check("Pause: continue resumes the program", () => debugger.Debugging && !debugger.DebugStopped);
        test.Check("Stack copy: running program has no stale stack", () => debugger.CurrentStackText().Length == 0);
        test.Run(() => debugContinue?.OnPressed?.Invoke());
        test.WaitFor(() => debugger.DebugStopped && debugger.stoppedFrames.Count > 0, 15f);
        test.Check("Pause: toolbar can pause again after continue", () => debugger.DebugStopped && debugger.stoppedFrames.Count > 0);
        if (switchWorkspace)
        {
            System.Diagnostics.Process? debuggee = null;
            test.Run(() =>
            {
                debuggee = System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(ready)));
                debugger.ContinueOrPause();
            });
            test.WaitFor(() => !debugger.DebugStopped, 5f);
            test.Run(() =>
            {
                string extra = Path.Combine(testFolder, "extra debug folder");
                Directory.CreateDirectory(extra);
                explorer.AddFolder(extra);
            });
            test.Check("Workspace switch: adding a folder keeps the active debug session", () => debugger.Debugging && debuggee?.HasExited == false);

            test.Run(() =>
            {
                string replacement = Path.Combine(testFolder, "debug replacement workspace");
                Directory.CreateDirectory(replacement);
                explorer.OpenFolder(replacement);
            });
            test.Check("Workspace switch: active debugger and source markers are retired", () => !debugger.Debugging && debugger.stoppedFrames.Count == 0
                && documents.All(open => open.Editor.ExecutionLine < 0));
            test.WaitFor(() => debuggee?.HasExited == true, 10f);
            test.Check("Workspace switch: previous debuggee is terminated", () => debuggee?.HasExited == true);
            test.Run(() => debuggee?.Dispose());
            return;
        }
        test.Run(debugger.StopDebugging);
        test.WaitFor(() => !debugger.Debugging, 10f);
        test.Check("Pause: stop ends the paused program", () => !debugger.Debugging);
    }
}
