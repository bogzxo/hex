using Horizon.UI.Components;

namespace Hex.Dawn;

// The self test of debugging C#. A little program of its own in the test's folder, a breakpoint on it, F5, and the
// program stopped there with the line lit, the stack and its variables on the Debug side, a step, and on to its end with
// what it wrote in the output. Only where netcoredbg is.
internal sealed partial class DawnScene
{
    private void TestDebug(HexPointerScript test)
    {
        if (debugger.Netcoredbg() is null)
        {
            test.Check("debug: no netcoredbg on this machine, nothing to try", () => true);
            return;
        }

        string folder = Path.Combine(testFolder, "debugme");
        string program = Path.Combine(folder, "Program.cs");
        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "debugme.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
            File.WriteAllText(program, "int answer = 6 * 7;\nstring said = \"forty two\";\nSystem.Console.WriteLine(said);\nSystem.Console.WriteLine(answer + 1);\n");
            Open(program);
        });
        test.Wait();
        test.Check("debug: a .cs file's editor takes breakpoints in its gutter", () => document.IsText && HexDebugging.IsCSharp(document.Path) && document.Editor.OnGutterPressed is not null);

        test.Run(() =>
        {
            code.Document.MoveTo(new Horizon.UI.Components.UITextPosition(1, 0));
            debugger.ToggleBreakpointHere();
        });
        test.Check("debug: F9 puts one down on the caret's line", () => document.Editor.Breakpoints?.Contains(1) == true);
        test.Run(() => debugger.ToggleBreakpoint(Path.GetFullPath(program), 3));
        test.Run(() => debugger.ToggleBreakpoint(Path.GetFullPath(program), 3));
        test.Check("debug: and the same again takes it up", () => document.Editor.Breakpoints is { Count: 1 } lines && lines.Contains(1));

        test.Run(RunOrPlay);
        test.WaitFor(() => debugger.DebugStopped, 90.0f);
        test.Settle(0.3f);
        test.Check("debug: F5 builds it and runs it to the breakpoint", () => debugger.DebugStopped && debugger.stoppedFrames.Count > 0 && debugger.stoppedFrames[debugger.stoppedFrame].Line == 1);
        test.Check("debug: the file is on screen with the line lit", () =>
            document.Path is { } path && Path.GetFullPath(path) == Path.GetFullPath(program) && document.Editor.ExecutionLine == 1);

        UITreeItem? Section(string name) => sideTree.Items.FirstOrDefault(item => item.Text == name);
        test.WaitFor(() => Section("Variables")?.Children?.Any(row => row.Text == "answer = 42") == true, 10.0f);
        test.Check("debug: the Debug side has where it stopped and what its variables are", () =>
            Side == HexSide.Debug && Section("Call stack")?.Children is { Count: > 0 } && Section("Variables")?.Children?.Any(row => row.Text == "answer = 42") == true);

        test.Run(debugger.StepOver);
        test.WaitFor(() => debugger.DebugStopped && debugger.stoppedFrames.Count > 0 && debugger.stoppedFrames[debugger.stoppedFrame].Line == 2, 20.0f);
        test.Check("debug: F10 steps to the next line", () => debugger.DebugStopped && debugger.stoppedFrames[debugger.stoppedFrame].Line == 2 && document.Editor.ExecutionLine == 2);

        test.Run(RunOrPlay);
        test.WaitFor(() => !debugger.Debugging, 20.0f);
        test.Check("debug: F5 goes on to its end, and nothing's lit any more", () => !debugger.Debugging && document.Editor.ExecutionLine == -1);
        test.Check("debug: what it wrote is in the output", () => said.Any(line => line.Text.Contains("forty two")) && said.Any(line => line.Text.Contains("43")));

        test.Run(() =>
        {
            debugger.breakpoints.Clear();
            CloseDocument(document);
        });
        test.Wait();
    }
}
