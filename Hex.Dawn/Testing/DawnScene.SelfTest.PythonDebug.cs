namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestPythonDebug(HexPointerScript test)
    {
        if (HexDebugging.PythonAdapter() is null)
        {
            test.Check("Python debug: installed debugpy adapter is required", () => false);
            return;
        }
        string folder = Path.Combine(testFolder, "python-debug");
        string file = Path.Combine(folder, "main.py");
        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(file, "answer = 42\nprint(answer)\nanswer += 1\nprint(answer)\n");
            Open(file);
        });
        test.Wait();
        test.Check("Python debug: gutter accepts breakpoints", () => document.Editor.OnGutterPressed is not null);
        test.Run(() =>
        {
            document.Editor.Document.MoveTo(new Horizon.UI.Components.UITextPosition(1, 0));
            debugger.ToggleBreakpointHere();
        });
        test.Check("Python debug: F9 marks the selected line", () => document.Editor.Breakpoints?.Contains(1) == true);
        test.Run(RunOrPlay);
        test.WaitFor(() => debugger.DebugStopped, 30f);
        test.Check("Python debug: F5 stops and highlights the source line", () => debugger.DebugStopped && document.Editor.ExecutionLine == 1);
        bool HasAnswer() => sideTree.Items.Any(section => section.Text == "Variables" && section.Children?.Any(row => row.Text == "answer = 42") == true);
        test.WaitFor(HasAnswer, 10f);
        test.Check("Python debug: locals show answer 42", HasAnswer);
        test.Run(debugger.StepOver);
        test.WaitFor(() => debugger.DebugStopped && document.Editor.ExecutionLine == 2, 10f);
        test.Check("Python debug: Step Over moves to the next source line", () => debugger.DebugStopped && document.Editor.ExecutionLine == 2);
        test.Run(debugger.RestartDebugging);
        test.WaitFor(() => debugger.DebugStopped && document.Editor.ExecutionLine == 1, 20f);
        test.Check("Python debug: Restart returns to the breakpoint", () => debugger.DebugStopped && document.Editor.ExecutionLine == 1);
        test.Run(debugger.StopDebugging);
        test.WaitFor(() => !debugger.Debugging, 10f);
        test.Check("Python debug: Stop ends the session", () => !debugger.Debugging);
        string taskFile = Path.Combine(folder, "task.py");
        string result = Path.Combine(folder, "task-result.txt");
        test.Run(() =>
        {
            File.WriteAllText(taskFile, "import os, sys\nargument = sys.argv[1]\nopen('task-result.txt', 'w').write(argument + '\\n' + os.getcwd())\n");
            tasks.WriteRunTasks(folder, "", "python task.py 'hello world'", ".", HexTask.PANEL);
            Context.Workspace.RunTarget = new HexRunTarget(HexRunTarget.TASK, folder, "run");
            debugger.ToggleBreakpoint(taskFile, 2);
            RunOrPlay();
        });
        test.WaitFor(() => debugger.DebugStopped && document.Editor.ExecutionLine == 2, 20f);
        test.Check("Python task: selected task stops in its own script", () => debugger.DebugStopped && PlatformPaths.Same(document.Path!, taskFile));
        test.Run(RunOrPlay);
        test.WaitFor(() => !debugger.Debugging && File.Exists(result), 15f);
        test.Check("Python task: arguments and working folder are preserved", () => File.Exists(result) && File.ReadAllText(result).ReplaceLineEndings("\n") == "hello world\n" + folder);
        test.Run(() =>
        {
            File.Delete(result);
            tasks.WriteRunTasks(folder, "", "python -m task 'module argument'", ".", HexTask.PANEL);
            RunOrPlay();
        });
        test.WaitFor(() => debugger.DebugStopped && document.Editor.ExecutionLine == 2, 20f);
        test.Check("Python module: F5 stops at the module breakpoint", () => debugger.DebugStopped && PlatformPaths.Same(document.Path!, taskFile));
        test.Run(RunOrPlay);
        test.WaitFor(() => !debugger.Debugging && File.Exists(result), 15f);
        test.Check("Python module: arguments and working folder are preserved", () => File.Exists(result) && File.ReadAllText(result).ReplaceLineEndings("\n") == "module argument\n" + folder);
        test.Run(() => Context.Workspace.RunTarget = null);
        test.Run(() => { debugger.breakpoints.Clear(); CloseDocument(document); });
    }
}
