namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestTestDebugCancel(HexPointerScript test)
    {
        if (debugger.Netcoredbg() is null)
        {
            test.Check("test debug cancel: real netcoredbg is required", () => false);
            return;
        }
        string folder = Path.Combine(testFolder, "test-debug-cancel");
        string project = Path.Combine(folder, "Checks.csproj"), source = Path.Combine(folder, "Checks.cs");
        string entered = Path.Combine(folder, "entered.txt"), completed = Path.Combine(folder, "completed.txt"), ran = Path.Combine(folder, "ran.txt");
        string trx = Path.Combine(folder, "result.trx");
        int completions = 0, gaveUp = 0, lastExit = int.MinValue;
        const string head = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"18.10.1\"/><PackageReference Include=\"xunit\" Version=\"2.9.3\"/><PackageReference Include=\"xunit.runner.visualstudio\" Version=\"3.1.5\"/></ItemGroup>";
        int Entries() { try { return File.Exists(entered) ? File.ReadAllLines(entered).Length : 0; } catch (IOException) { return 0; } }
        void Start() => debugger.DebugTests(project, "FullyQualifiedName=Review.Checks.One", trx,
            exit => { completions++; lastExit = exit; }, () => gaveUp++);
        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            string wait = OperatingSystem.IsWindows() ? "ping -n 9 127.0.0.1 &gt;nul" : "sleep 8";
            File.WriteAllText(project, head + "<Target Name=\"Delay\" BeforeTargets=\"CoreCompile\" Condition=\"'$(VSTEST_HOST_DEBUG)' == '1'\"><WriteLinesToFile File=\"entered.txt\" Lines=\"entered\" Overwrite=\"false\"/><Exec Command=\"" + wait + "\"/><WriteLinesToFile File=\"completed.txt\" Lines=\"completed DesignTimeBuild=$(DesignTimeBuild) VSTEST_HOST_DEBUG=$(VSTEST_HOST_DEBUG)\" Overwrite=\"false\"/></Target></Project>");
            File.WriteAllText(source, "namespace Review;\npublic class Checks\n{\n    [Xunit.Fact]\n    public void One()\n    {\n        int answer = 42;\n        System.IO.File.WriteAllText(@\"" + ran + "\", answer.ToString());\n        Xunit.Assert.Equal(42, answer);\n    }\n}\n");
            Open(source);
            debugger.ToggleBreakpoint(Path.GetFullPath(source), 7);
            Start();
        });
        test.WaitFor(() => Entries() >= 1, 60f);
        test.Check("test debug cancel: initial delayed build entered", () => Entries() == 1 && debugger.Debugging);
        test.Run(debugger.RestartDebugging);
        test.WaitFor(() => Entries() >= 2, 60f);
        test.Check("test debug cancel: replacement retains debugger ownership", () => Entries() == 2 && debugger.Debugging && completions == 0);
        test.Run(debugger.RestartDebugging);
        test.WaitFor(() => Entries() >= 3, 60f);
        test.Check("test debug cancel: repeated Restart retains one outstanding completion", () => Entries() == 3 && debugger.Debugging && completions == 0 && gaveUp == 0);
        test.Run(debugger.StopDebugging);
        test.Check("test debug cancel: Stop completes the logical run once", () => !debugger.Debugging && completions == 1 && lastExit == -1);
        test.Settle(10f);
        test.Check("test debug cancel: stale attempts cannot complete, attach or run later", () =>
        {
            Console.WriteLine($"[Hex self-test] cancelled test diagnostics: debugging={debugger.Debugging}, completions={completions}, completed={File.Exists(completed)}, ran={File.Exists(ran)}, trx={File.Exists(trx)}");
            if (File.Exists(completed)) Console.WriteLine("[Hex self-test] completion origin: " + File.ReadAllText(completed));
            return !debugger.Debugging && completions == 1 && !File.Exists(completed) && !File.Exists(ran) && !File.Exists(trx);
        });
        test.Run(() => { Start(); debugger.StopDebugging(); });
        test.Settle(1f);
        test.Check("test debug cancel: immediate Stop owns even a not-yet-published process", () =>
            !debugger.Debugging && completions == 2 && Entries() == 3 && !File.Exists(completed));
        test.Run(Start);
        test.WaitFor(() => Entries() >= 4, 60f);
        test.Check("test debug cancel: workspace test has a real pending build", () => Entries() == 4 && debugger.Debugging);
        test.Run(() =>
        {
            string replacement = Path.Combine(testFolder, "replacement-workspace");
            Directory.CreateDirectory(replacement);
            explorer.OpenFolder(replacement);
        });
        test.Settle(10f);
        test.Check("test debug cancel: workspace replacement cancels its pending test build", () =>
            !debugger.Debugging && completions == 3 && !File.Exists(completed) && !File.Exists(ran));
        test.Run(() =>
        {
            File.WriteAllText(project, head + "</Project>");
            // Workspace replacement clears its breakpoints. Set this fresh attempt's breakpoint explicitly.
            debugger.breakpoints.Clear();
            debugger.ToggleBreakpoint(Path.GetFullPath(source), 7);
            Start();
        });
        test.WaitFor(() => debugger.DebugStopped, 60f);
        test.Check("test debug cancel: fresh attempt attaches at a real test breakpoint", () => debugger.DebugStopped && !File.Exists(ran));
        test.Run(debugger.ContinueOrPause);
        test.WaitFor(() => completions == 4 && !debugger.Debugging, 30f);
        test.Check("test debug cancel: successful attempt reports once and publishes its TRX", () =>
            completions == 4 && lastExit == 0 && File.Exists(trx) && File.Exists(ran));
        test.Run(() => { debugger.StopDebugging(); debugger.breakpoints.Clear(); CloseDocument(document); });
        test.Wait();
    }
}
