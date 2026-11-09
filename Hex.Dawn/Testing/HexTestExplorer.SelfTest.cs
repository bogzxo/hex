using Hex.Core.Projects;

namespace Hex.Dawn;

internal sealed partial class HexTestExplorer
{
    internal void TestWorkspaceSwitch(HexPointerScript script, string root, Action<string> open)
    {
        string first = Path.Combine(root, "first"), second = Path.Combine(root, "second");
        foreach (var (folder, name) in new[] { (first, "First"), (second, "Second") })
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, name + ".csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><PackageReference Include=\"xunit\" /></ItemGroup></Project>");
            File.WriteAllText(Path.Combine(folder, name + ".cs"), $"namespace Fixture; class {name} {{ [Fact] public void Check() {{ }} }}");
        }
        script.Run(() => { open(first); Show(); });
        script.WaitFor(() => discovered && !discovering, 20f);
        script.Check("test workspace: first folder discovered", () => tests.Values.SelectMany(items => items).SingleOrDefault()?.FullName == "Fixture.First.Check");
        RunScope? oldScope = null;
        script.Run(() =>
        {
            oldScope = Running(Path.Combine(first, "First.csproj"), null, "old workspace");
            open(second);
            Show();
        });
        script.WaitFor(() => discovered && !discovering, 20f);
        script.Check("test workspace: replacement folder has only its own tests", () => tests.Values.SelectMany(items => items).SingleOrDefault()?.FullName == "Fixture.Second.Check");
        script.Run(() => Finished(Path.Combine(first, "First.csproj"),
            [new DotnetTestResult("Fixture.First.Check", DotnetTestOutcome.Failed, TimeSpan.Zero, "old failure", "", "")],
            "stale completion", 1, oldScope!, false));
        script.Check("test workspace: old completion cannot restore results or failures", () => results.Count == 0 && !anyFailed && !running && lastRun is null);
        script.Run(() =>
        {
            string a = Path.Combine(first, "First.csproj"), b = Path.Combine(second, "Second.csproj");
            tests[a] = [new DotnetTest(a, "Shared", "Checks", "Same", "", 0)];
            tests[b] = [new DotnetTest(b, "Shared", "Checks", "Same", "", 0)];
            pressedLast = (new TestTarget(a, ["Shared.Checks.Same"], "Same", "Same", null), a, 0);
        });
        RunScope? sameNameScope = null;
        script.Run(() => sameNameScope = Running(Path.Combine(first, "First.csproj"), null, "same-name project"));
        script.Check("test identity: waiting belongs only to the selected project", () =>
            waiting.Contains(Identity(Path.Combine(first, "First.csproj"), "Shared.Checks.Same"))
            && !waiting.Contains(Identity(Path.Combine(second, "Second.csproj"), "Shared.Checks.Same")));
        script.Run(() => Finished(Path.Combine(first, "First.csproj"),
            [new DotnetTestResult("Shared.Checks.Same", DotnetTestOutcome.Passed, TimeSpan.Zero, "", "", "")],
            "same-name project", 0, sameNameScope!, false));
        script.Check("test identity: one execution cannot pass the other project", () =>
        {
            return Outcome(tests[Path.Combine(first, "First.csproj")].Single()) == DotnetTestOutcome.Passed
                && Outcome(tests[Path.Combine(second, "Second.csproj")].Single()) == DotnetTestOutcome.NotRun
                && waiting.Count == 0;
        });
        script.Run(() => { open(first); Show(); open(second); Show(); });
        script.WaitFor(() => discovered && !discovering, 20f);
        script.Check("test workspace: remembered row selection is cleared", () => pressedLast is null);
        script.Settle(0.5f);
        script.Check("test workspace: rapid switch keeps the final discovery", () => tests.Values.SelectMany(items => items).SingleOrDefault()?.FullName == "Fixture.Second.Check");
    }

    internal void TestRepositorySelection(HexPointerScript script, string project)
    {
        const string method = "Hex.Tests.DotnetTestsTests.FiltersEscapeWhatTheFilterReads";
        const string theory = "Hex.Tests.CanvasResizeTests.ResizeKeepsLayoutPointAtViewportCentre";
        script.Run(Show);
        script.WaitFor(() => discovered && !discovering, 60f);
        script.Check("test explorer: actual Hex project discovered", () => tests.ContainsKey(project));
        script.Run(() => Run(project, [method], "exact method qualification"));
        script.WaitFor(() => !running, 120f);
        script.Check("test explorer: selected method passed", () =>
            results.TryGetValue(Identity(project, method), out var found) && found.Count == 1 && found.Values.All(item => item.Outcome == DotnetTestOutcome.Passed));
        script.Check("test explorer: similarly named method was not run", () =>
            !results.ContainsKey(Identity(project, method + "WithoutMatchingLongerMethodNames")));

        script.Run(() => Run(project, [theory], "theory qualification"));
        script.WaitFor(() => !running, 120f);
        script.Check("test explorer: theory produced three individual cases", () =>
            results.TryGetValue(Identity(project, theory), out var found) && found.Count == 3 && found.Values.All(item => item.Outcome == DotnetTestOutcome.Passed));
        string? selected = null;
        script.Run(() =>
        {
            if (!results.TryGetValue(Identity(project, theory), out var found) || found.Count != 3)
                return;
            selected = found.Keys.Order(StringComparer.Ordinal).First();
            // Clear previous outcomes to prove the next actual test process executes only one case.
            results.Remove(Identity(project, theory));
            Run(project, [selected], "single case qualification");
        });
        script.WaitFor(() => !running, 120f);
        script.Check("test explorer: selecting one case runs exactly that case", () =>
            selected is not null && results.TryGetValue(Identity(project, theory), out var found) && found.Count == 1 && found.ContainsKey(selected)
                && found[selected].Outcome == DotnetTestOutcome.Passed);
    }
}
