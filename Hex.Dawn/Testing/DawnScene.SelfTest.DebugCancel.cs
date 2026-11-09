namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestDebugCancel(HexPointerScript test)
    {
        if (debugger.Netcoredbg() is null)
        {
            test.Check("debug cancel: real netcoredbg is required", () => false);
            return;
        }
        string folder = Path.Combine(testFolder, "debug-cancel");
        string project = Path.Combine(folder, "Cancel.csproj");
        string program = Path.Combine(folder, "Program.cs");
        string began = Path.Combine(folder, "build-started.txt");
        string completed = Path.Combine(folder, "build-completed.txt");
        string launched = Path.Combine(folder, "launched.txt");
        const string head = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>";
        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            string wait = OperatingSystem.IsWindows() ? "ping -n 9 127.0.0.1 &gt;nul" : "sleep 8";
            File.WriteAllText(project, head + "<Target Name=\"Delay\" BeforeTargets=\"CoreCompile\"><WriteLinesToFile File=\"build-started.txt\" Lines=\"started\" Overwrite=\"true\"/><Exec Command=\"" + wait + "\"/><WriteLinesToFile File=\"build-completed.txt\" Lines=\"completed\" Overwrite=\"true\"/></Target></Project>");
            File.WriteAllText(program, "int answer = 42;\nSystem.IO.File.WriteAllText(@\"" + launched + "\", answer.ToString());\nSystem.Console.WriteLine(answer);\n");
            Open(program);
            debugger.ToggleBreakpoint(Path.GetFullPath(program), 1);
            debugger.DebugOrContinue();
        });
        test.WaitFor(() => File.Exists(began), 60f);
        test.Check("debug cancel: delayed build really started", () => File.Exists(began) && debugger.Debugging);
        test.Run(debugger.StopDebugging);
        test.Check("debug cancel: Stop leaves the building state", () => !debugger.Debugging);
        test.Settle(10f);
        test.Check("debug cancel: cancelled build cannot finish or launch later", () =>
            !debugger.Debugging && !File.Exists(completed) && !File.Exists(launched));
        test.Run(() =>
        {
            File.WriteAllText(project, head + "</Project>");
            debugger.DebugOrContinue();
        });
        test.WaitFor(() => debugger.DebugStopped, 60f);
        test.Check("debug cancel: a fresh attempt reaches its breakpoint", () => debugger.DebugStopped && !File.Exists(launched));
        test.Run(debugger.StopDebugging);
        test.WaitFor(() => !debugger.Debugging, 10f);
        test.Check("debug cancel: stopping the fresh session cleans up", () => !debugger.Debugging);
        test.Run(() => { debugger.breakpoints.Clear(); CloseDocument(document); });
    }
}
