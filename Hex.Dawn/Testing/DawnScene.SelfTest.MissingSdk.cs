namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestMissingSdk(HexPointerScript test)
    {
        if (debugger.Netcoredbg() is null)
        {
            test.Check("missing SDK: installed netcoredbg is required for the launch workflow", () => false);
            return;
        }
        string root = Path.Combine(testFolder, "missing SDK");
        string global = Path.Combine(root, "global.json"), source = Path.Combine(root, "Program.cs");
        test.Run(() =>
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(global, "{\"sdk\":{\"version\":\"999.0.100\",\"rollForward\":\"disable\"}}");
            File.WriteAllText(source, "System.Console.WriteLine(42);\n");
            explorer.OpenFolder(root);
            Open(source);
            RunOrPlay();
        });
        test.WaitFor(() => !debugger.Debugging && terminalProblems.Any(problem => PlatformPaths.Same(problem.Path, global)), 30);
        test.WaitFor(() => Descendants(problems).OfType<Button>().Any(button => button.Tooltip.Contains("999.0.100")), 5);
        test.Check("missing SDK: launch ends without a debug session", () => !debugger.Debugging && !debugger.DebugStopped);
        test.Check("missing SDK: Problems identifies the requested version and global.json", () => terminalProblems.Any(problem =>
            PlatformPaths.Same(problem.Path, global) && !problem.Warning && problem.Text.Contains("999.0.100")));
        test.Run(() => Descendants(problems).OfType<Button>().FirstOrDefault(button => button.Tooltip.Contains("999.0.100"))?.OnPressed?.Invoke());
        test.Check("missing SDK: Problems opens the SDK configuration", () => PlatformPaths.Same(document.Path!, global) && code.Document.Caret.Line == 0);
        test.Run(() =>
        {
            code.Document.SelectAll();
            code.Document.Insert("{}\n");
        });
        test.Check("missing SDK: configuration remains editable after failed launch", () => PlatformPaths.Same(document.Path!, global) && code.Text == "{}\n");
        test.Run(() =>
        {
            Save();
            // Saving global.json can clear its own diagnostics. Retain a build error
            // on the source to verify that the subsequent successful build clears it.
            Context.Terminals.AddProblem(source, 1, 1, "stale SDK build error", false);
            Open(source);
            debugger.ToggleBreakpoint(source, 0);
            RunOrPlay();
        });
        test.WaitFor(() => debugger.DebugStopped, 90);
        test.Check("missing SDK: repaired configuration builds and reaches a breakpoint", () => debugger.DebugStopped
            && PlatformPaths.Same(document.Path!, source) && code.ExecutionLine == 0);
        test.Check("missing SDK: successful rebuild clears the failed build diagnostics", () => !terminalProblems.Any(problem =>
            problem.Text.Contains("999.0.100") || problem.Text == "stale SDK build error"));
        test.Run(debugger.StopDebugging);
        test.WaitFor(() => !debugger.Debugging, 10);
        test.Check("missing SDK: recovered debug session stops cleanly", () => !debugger.Debugging);
        test.Run(() => debugger.ToggleBreakpoint(source, 0));
    }
}
