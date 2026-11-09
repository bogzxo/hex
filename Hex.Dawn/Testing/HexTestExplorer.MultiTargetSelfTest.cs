using Hex.Core.Projects;

namespace Hex.Dawn;

internal sealed partial class HexTestExplorer
{
    internal void TestSingleTargetFrameworkRetry(HexPointerScript script, string folder, Action<string, string> open)
    {
        string project = Path.Combine(folder, "Single.csproj"), source = Path.Combine(folder, "Checks.cs");
        const string text = "public class SingleChecks { [Xunit.Fact] public void One() { } [Xunit.Fact] public void Two() { } }";
        DotnetTestResult[] Outcomes(string method) => results.GetValueOrDefault(Identity(project, "SingleChecks." + method))?.Values.ToArray() ?? [];
        script.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net11.0</TargetFramework><IsTestProject>true</IsTestProject></PropertyGroup>
                  <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.10.1"/><PackageReference Include="xunit" Version="2.9.3"/><PackageReference Include="xunit.runner.visualstudio" Version="3.1.5"/></ItemGroup>
                </Project>
                """);
            File.WriteAllText(source, text);
            open(folder, source);
            Show();
        });
        script.WaitFor(() => discovered && !discovering && tests.ContainsKey(project), 30);
        script.Run(() => Run(project, null, "single-target baseline"));
        script.WaitFor(() => !running, 120);
        script.Check("Single-target retry: original native results have one outcome per test", () => Outcomes("One") is [{ Outcome: DotnetTestOutcome.Passed, Framework: "" }]
            && Outcomes("Two") is [{ Outcome: DotnetTestOutcome.Passed, Framework: "" }]);
        script.Run(() =>
        {
            File.WriteAllText(source, text + " this does not compile");
            Run(project, ["SingleChecks.One"], "single-target failed build");
        });
        script.WaitFor(() => !running, 120);
        script.Check("Single-target retry: scoped build failure preserves only unattempted results", () => Outcomes("One").Length == 0
            && Outcomes("Two") is [{ Outcome: DotnetTestOutcome.Passed }] && frameworkBuildFailures.GetValueOrDefault(project)?.Contains("net11.0") == true);
        script.Run(() =>
        {
            File.WriteAllText(source, text);
            Rows();
            if (targets.TryGetValue("framework-build|" + project + "|net11.0", out var retry)) RowActions(retry)[0].Pressed();
        });
        script.WaitFor(() => !running, 120);
        script.Check("Single-target retry: framework retry replaces unqualified results without duplicates", () => Outcomes("One") is [{ Outcome: DotnetTestOutcome.Passed, Framework: "net11.0" }]
            && Outcomes("Two") is [{ Outcome: DotnetTestOutcome.Passed, Framework: "net11.0" }] && !frameworkBuildFailures.ContainsKey(project) && waiting.Count == 0);
    }

    internal void TestTargetFrameworks(HexPointerScript script, string folder, Action<string, string> open)
    {
        string project = Path.Combine(folder, "Targets.csproj"), source = Path.Combine(folder, "Checks.cs");
        string ran = Path.Combine(folder, "ran.txt"), repair = Path.Combine(folder, "repair"), entered = Path.Combine(folder, "entered.txt"), completed = Path.Combine(folder, "completed.txt");
        static string Literal(string path) => "@\"" + path.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        string text = $$"""
            using System;
            using System.IO;
            using System.Reflection;
            using System.Runtime.Versioning;
            public class FrameworkChecks
            {
                private static string Framework => Assembly.GetExecutingAssembly().GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName;
                private static void Record(string file, string text)
                {
                    using var mutex = new System.Threading.Mutex(false, "hex-framework-fixture-{{Guid.NewGuid():N}}");
                    if (!mutex.WaitOne(TimeSpan.FromSeconds(20))) throw new Exception("Fixture marker is locked");
                    try { File.AppendAllText(file, text); } finally { mutex.ReleaseMutex(); }
                }
                [Xunit.Fact] public void Outcome()
                {
                    Record({{Literal(ran)}}, Framework + "|Outcome\n");
            #if NET10_0
                    Xunit.Assert.True(File.Exists({{Literal(repair)}}), "failure on .NET 10");
            #endif
                }
                [Xunit.Theory, Xunit.InlineData(1), Xunit.InlineData(2)] public void Case(int value)
                {
                    Record({{Literal(ran)}}, Framework + "|Case|" + value + "\n");
                    Xunit.Assert.InRange(value, 1, 2);
                }
                [Xunit.Fact] public void Debuggable()
                {
                    string framework = Framework;
                    Record({{Literal(entered)}}, framework + "\n");
                    int answer = 42;
                    Record({{Literal(completed)}}, framework + "\n");
                    Xunit.Assert.Equal(42, answer);
                }
            }
            """;
        int breakpoint = Array.FindIndex(text.Split('\n'), line => line.Contains("int answer = 42", StringComparison.Ordinal));
        string[] Lines(string path) { try { return File.Exists(path) ? File.ReadAllLines(path) : []; } catch (IOException) { return []; } }
        DotnetTestResult[] Outcomes(string method) => results.GetValueOrDefault(Identity(project, "FrameworkChecks." + method))?.Values.ToArray() ?? [];
        UITreeItem? FrameworkRowOf(string method, string framework) { Rows(); return Find(Rows(), "framework|" + project + "|FrameworkChecks." + method + "|" + framework); }
        static UITreeItem? Find(IEnumerable<UITreeItem> rows, string id)
        {
            foreach (var row in rows)
            {
                if ((row.Tag as SideTag)?.Id == id) return row;
                if (row.Children is { } children && Find(children, id) is { } found) return found;
            }
            return null;
        }
        DotnetTestResult? untouched = null;
        DotnetExecution? brokenBuild = null;
        string originalProject = string.Empty;
        string? selectedCase = null;
        int before = 0;
        script.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(project, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFrameworks>net10.0;net11.0</TargetFrameworks><IsTestProject>true</IsTestProject></PropertyGroup>
                  <ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.10.1"/><PackageReference Include="xunit" Version="2.9.3"/><PackageReference Include="xunit.runner.visualstudio" Version="3.1.5"/></ItemGroup>
                </Project>
                """);
            File.WriteAllText(source, text);
            open(folder, source);
            Show();
        });
        script.WaitFor(() => discovered && !discovering && tests.ContainsKey(project), 30);
        script.Run(() => Run(project, null, "all framework variants"));
        script.WaitFor(() => !running, 120);
        script.Check("Multi-target tests: all native framework outcomes survive", () => Outcomes("Outcome").Length == 2
            && Outcomes("Outcome").Any(result => result.Framework == "net10.0" && result.Outcome == DotnetTestOutcome.Failed)
            && Outcomes("Outcome").Any(result => result.Framework == "net11.0" && result.Outcome == DotnetTestOutcome.Passed));
        script.Check("Multi-target tests: theory cases remain distinct across runtimes", () => Outcomes("Case").Length == 4
            && Outcomes("Case").GroupBy(result => result.Framework).All(group => group.Count() == 2 && group.All(result => result.Outcome == DotnetTestOutcome.Passed)));
        script.Check("Multi-target tests: summary counts native executions", () => DotnetTests.Tally(tests.GetValueOrDefault(project) ?? [], ResultsOf) is { Cases: 8, Passed: 7, Failed: 1 });
        script.Check("Multi-target tests: each runtime has a named result row", () => FrameworkRowOf("Outcome", "net10.0") is { Icon: "error" }
            && FrameworkRowOf("Outcome", "net11.0") is { Icon: "pass" });
        script.Check("Multi-target tests: Problems identifies the failing runtime", () => failures.Count == 1 && failures[0].Text.Contains("net10.0", StringComparison.Ordinal));
        script.Run(() => { if (search is not null) search.Text = "net10.0"; });
        script.Check("Multi-target tests: searching a framework shows only its runtime results", () => FrameworkRowOf("Outcome", "net10.0") is not null && FrameworkRowOf("Outcome", "net11.0") is null);
        script.Run(() => { if (search is not null) search.Text = string.Empty; });
        script.Run(() =>
        {
            selectedCase = Outcomes("Case").FirstOrDefault(result => result.Framework == "net11.0")?.FullName;
            untouched = Outcomes("Case").FirstOrDefault(result => result.Framework == "net10.0");
            before = Lines(ran).Length;
            Rows();
            if (targets.Values.FirstOrDefault(target => target.Framework == "net11.0" && target.Names?.SingleOrDefault() == selectedCase) is { } target)
                RowActions(target)[0].Pressed();
        });
        script.WaitFor(() => !running, 120);
        script.Check("Multi-target tests: a theory case row runs only its runtime and case", () => Lines(ran).Length == before + 1
            && Lines(ran).Last().StartsWith(".NETCoreApp,Version=v11.0|Case|", StringComparison.Ordinal));
        script.Check("Multi-target tests: targeted rerun preserves other runtime results", () => Outcomes("Case").Length == 4
            && untouched is not null && Outcomes("Case").Any(result => ReferenceEquals(result, untouched)));
        script.Run(() => { before = Lines(ran).Length; File.WriteAllText(repair, "fixed"); RunFailed(); });
        script.WaitFor(() => !running, 120);
        script.Check("Multi-target tests: Run Failed reruns only the failing runtime", () => Lines(ran).Length == before + 1
            && Lines(ran).Last() == ".NETCoreApp,Version=v10.0|Outcome" && Outcomes("Outcome").Length == 2
            && Outcomes("Outcome").All(result => result.Outcome == DotnetTestOutcome.Passed));
        script.Check("Multi-target tests: repaired runtime clears Problems", () => failures.Count == 0 && !anyFailed && waiting.Count == 0);
        script.Run(() =>
        {
            originalProject = File.ReadAllText(project);
            File.WriteAllText(project, originalProject.Replace("</Project>", "<Target Name=\"RejectRuntime\" BeforeTargets=\"CoreCompile\"><Error Condition=\"'$(TargetFramework)' == 'net11.0'\" Text=\"Framework build failure probe\" /></Target></Project>", StringComparison.Ordinal));
            Run(project, ["FrameworkChecks.Outcome"], "one failed framework build");
            brokenBuild = dotnetRuns.GetValueOrDefault(project);
        });
        script.WaitFor(() => !running, 120);
        script.Check("Multi-target tests: a failed framework build cannot retain its old pass or hide behind another runtime's pass", () => Outcomes("Outcome") is [{ Framework: "net10.0", Outcome: DotnetTestOutcome.Passed }]
            && brokenBuild is { Exit: not 0 } && brokenBuild.FailedFrameworks.SequenceEqual(new[] { "net11.0" }) && Outcomes("Case").Length == 4);
        script.Check("Multi-target tests: project retains a visible framework build error and targeted retry action", () => Rows().Any(row => row.Icon == "error"
            && row.Children?.Any(child => (child.Tag as SideTag)?.Id == "framework-build|" + project + "|net11.0" && child.Actions?.Count == 2) == true));
        script.Run(() => File.WriteAllText(project, originalProject));
        script.Run(() =>
        {
            Rows();
            if (targets.TryGetValue("framework-build|" + project + "|net11.0", out var retry)) RowActions(retry)[0].Pressed();
        });
        script.WaitFor(() => !running, 120);
        script.Check("Multi-target tests: successful targeted retry clears the framework build error", () => !frameworkBuildFailures.ContainsKey(project)
            && Outcomes("Outcome").Length == 2 && Outcomes("Outcome").All(result => result.Outcome == DotnetTestOutcome.Passed));
        script.Run(() =>
        {
            File.Delete(entered); File.Delete(completed);
            debugger.breakpoints.Clear();
            debugger.ToggleBreakpoint(source, breakpoint);
            Debug(project, ["FrameworkChecks.Debuggable"], "debug all runtimes");
        });
        script.WaitFor(() => debugger.DebugStopped && Lines(entered).Length == 1, 90);
        script.Check("Multi-target tests: first native host pauses before its test completes", () => debugger.DebugStopped
            && Lines(entered).SequenceEqual(new[] { ".NETCoreApp,Version=v10.0" }) && !File.Exists(completed));
        script.Run(debugger.RestartDebugging);
        script.WaitFor(() => debugger.DebugStopped && Lines(entered).Length == 2, 90);
        script.Check("Multi-target tests: Restart retains framework and logical run ownership", () => debugger.DebugStopped && running
            && Lines(entered).All(line => line == ".NETCoreApp,Version=v10.0") && !File.Exists(completed));
        script.Run(debugger.ContinueOrPause);
        script.WaitFor(() => debugger.DebugStopped && Lines(entered).Length == 3, 90);
        script.Check("Multi-target tests: next runtime receives its own native debugger", () => debugger.DebugStopped
            && Lines(entered).LastOrDefault() == ".NETCoreApp,Version=v11.0" && Lines(completed).SequenceEqual(new[] { ".NETCoreApp,Version=v10.0" }));
        script.Run(debugger.ContinueOrPause);
        script.WaitFor(() => !running && !debugger.Debugging, 60);
        script.Check("Multi-target tests: debugging publishes results from both hosts", () => Outcomes("Debuggable").Length == 2
            && Outcomes("Debuggable").All(result => result.Outcome == DotnetTestOutcome.Passed) && Lines(completed).Length == 2 && waiting.Count == 0);
        script.Run(() =>
        {
            File.Delete(entered); File.Delete(completed);
            untouched = Outcomes("Debuggable").FirstOrDefault(result => result.Framework == "net10.0");
            FrameworkRowOf("Debuggable", "net11.0")?.Actions?[1].Pressed();
        });
        script.WaitFor(() => debugger.DebugStopped && Lines(entered).Length == 1, 90);
        script.Check("Multi-target tests: runtime row debugs just the selected framework", () => debugger.DebugStopped
            && Lines(entered).SequenceEqual(new[] { ".NETCoreApp,Version=v11.0" }));
        script.Run(debugger.ContinueOrPause);
        script.WaitFor(() => !running && !debugger.Debugging, 60);
        script.Check("Multi-target tests: single-runtime debug preserves its sibling's outcome", () => Outcomes("Debuggable").Length == 2
            && untouched is not null && Outcomes("Debuggable").Any(result => ReferenceEquals(result, untouched)));
        script.Run(() => { File.Delete(entered); File.Delete(completed); Debug(project, ["FrameworkChecks.Debuggable"], "cancel queued runtimes"); });
        script.WaitFor(() => debugger.DebugStopped && Lines(entered).Length == 1, 90);
        script.Run(Cancel);
        script.WaitFor(() => !running && !debugger.Debugging, 20);
        script.Settle(1);
        script.Check("Multi-target tests: Cancel prevents queued runtime hosts from launching", () => !running && !debugger.Debugging
            && Lines(entered).Length == 1 && !File.Exists(completed) && dotnetRuns.Count == 0 && waiting.Count == 0);
        script.Run(() => { debugger.breakpoints.Clear(); if (debugger.Debugging) debugger.StopDebugging(); });
    }
}
