using System.Numerics;

using Hex.Core.Projects;

using Horizon.UI.Components;

namespace Hex.Dawn;

// Visual Studio's Test Explorer, a side of its own. The tests are found in the source the moment it's opened (DotnetTests,
// no build needed), shown by project, namespace, class and test, and run with dotnet test in a terminal tab of their own
// so what it prints is there to read. The .trx it writes is read back and every test gets its tick or its cross, a
// failed one its message and stack under it, a click on a test or a frame goes there. Ctrl+R A runs them all, Ctrl+R T
// the ones the caret is in, Ctrl+R Ctrl+T debugs them, Ctrl+R L the last run again, VS's keys. Ctrl+E T shows the
// explorer when Visual Studio's keys are on, Emmet has Ctrl+E otherwise. Debug on a row runs its tests under the
// debugger, which stops at the breakpoints in them (HexDebugging.Tests).
internal sealed partial class HexTestExplorer
{
    private readonly DawnContext context;
    private readonly HexDebugging debugger;

    // What was found, by project, and how each test (by its full name) last came out, a theory's every case by the case's
    // own name under it
    private Dictionary<string, List<DotnetTest>> tests = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<TestIdentity, Dictionary<string, DotnetTestResult>> results = new();
    private bool discovering, discovered;
    private int workspaceGeneration;
    private string runningWhat = string.Empty;
    private Action? lastRun;
    private Action? afterDiscovery;

    // How many runs are going, Run All has one a project at once
    private int runs;
    private bool running => runs > 0;

    // The tests and the theories' cases a run that's going has still to say about, by their full names. Their rows keep
    // what they said last time with a clock on them until it does, the way VS has them, rather than going back to not run
    // and the list shrinking under whoever's reading it
    private readonly HashSet<TestIdentity> waiting = new();

    // The projects whose terminal tabs have a run going, what Cancel stops
    private readonly HashSet<string> runningIn = new(StringComparer.OrdinalIgnoreCase);

    // What a run takes in, the tests it runs whole and the cases it runs on their own
    private sealed record RunScope(string Project, List<string> Whole, List<string> Cases, int Generation, string Configuration,
        string? NativeRoot = null, int NativeVersion = 0, int PythonVersion = 0, string? Framework = null,
        IReadOnlyDictionary<string, string[]>? FrameworkNames = null);

    private readonly record struct TestIdentity(string Project, string Name);
    private static TestIdentity Identity(string project, string name)
    {
        string full = Path.GetFullPath(project);
        return new(OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full, name);
    }

    // The rows' colours, the skin has no green of its own
    private static readonly Vector4 Passed = new(0.36f, 0.78f, 0.47f, 1.0f);

    public HexTestExplorer(DawnContext with, HexDebugging debugging)
    {
        context = with;
        debugger = debugging;
        context.Panels.AddSide(HexSide.Tests, context.Strings.Said("tests.title"), Rows);
        context.Panels.AddProblems(Failures);
        BuildSearch();

        UICommands commands = context.Commands;
        commands.Add("Test/tests", context.Strings.Said("tests.show"), Show, icon: "beaker");
        commands.Add("Test/tests", context.Strings.Said("tests.run_all"), RunAll, "Ctrl+R A", "run-all", UIKeyUse.BoundWhileTyping).IsEnabled = () => !running;
        commands.Add("Test/tests", context.Strings.Said("tests.run_here"), RunHere, "Ctrl+R T", "play", UIKeyUse.BoundWhileTyping).IsEnabled = () => !running;
        commands.Add("Test/tests", context.Strings.Said("tests.debug_here"), DebugHere, "Ctrl+R Ctrl+T", "debug-alt", UIKeyUse.BoundWhileTyping).IsEnabled = () => !running && !debugger.Debugging;
        commands.Add("Test/tests", context.Strings.Said("tests.run_failed"), RunFailed, icon: "run-errors").IsEnabled = () => !running && anyFailed;
        commands.Add("Test/tests", context.Strings.Said("tests.run_last"), () => lastRun?.Invoke(), "Ctrl+R L", "debug-rerun", UIKeyUse.BoundWhileTyping).IsEnabled = () => !running && lastRun is not null;
        commands.Add("Test/tests", context.Strings.Said("tests.refresh"), Discover, icon: "refresh");

        // A test file saved is looked at again, a test added or renamed shows up without asking
        context.Documents.Saved += saved =>
        {
            if (discovered && saved.Path is { } path && (path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".py", StringComparison.OrdinalIgnoreCase)) && tests.Keys.Any(project => PlatformPaths.IsUnder(path, Directory.Exists(project) ? project : Path.GetDirectoryName(project)!)))
                Discover();
        };
    }

    /// <summary>The explorer shown (Ctrl+E T with Visual Studio's keys), its tests found the first time.</summary>
    internal void Show()
    {
        context.Panels.ShowSide(HexSide.Tests);
        if (!discovered && !discovering)
            Discover();
    }

    internal void WorkspaceChanged()
    {
        Cancel();
        workspaceGeneration++;
        discovering = discovered = false;
        tests.Clear();
        ctestProjects.Clear();
        nativeSelections.Clear();
        nativeVersions.Clear();
        pythonVersions.Clear();
        nativeDiscoveryVersion++;
        results.Clear();
        dotnetRuns.Clear();
        frameworkBuildFailures.Clear();
        waiting.Clear();
        runningIn.Clear();
        opened.Clear();
        openedSearching.Clear();
        targets.Clear();
        pressedLast = null;
        runs = 0;
        lastRun = null;
        afterDiscovery = null;
        runningWhat = string.Empty;
        FailuresChanged();
        context.Panels.RefreshSide(HexSide.Tests);
    }

    /// <summary>Every test of the workspace found again, off the editor's thread.</summary>
    internal void Discover()
    {
        if (discovering)
            return;

        discovering = true;
        int generation = workspaceGeneration;
        int nativeVersion = nativeDiscoveryVersion;
        int pythonVersion = pythonDiscoveryVersion;
        context.Panels.RefreshSide(HexSide.Tests);
        string[] roots = [.. context.Workspace.Roots];
        string? selectedSolution = context.Workspace.Solution;
        var pythonErrors = new List<string>();
        var nativeProjects = new Dictionary<string, CTestProject>(PlatformPaths.Comparer);
        context.Work.InBackground(context.Strings.Said("tests.finding"), () =>
        {
            var found = new Dictionary<string, List<DotnetTest>>(StringComparer.OrdinalIgnoreCase);
            foreach (string project in DotnetTests.TestProjects(roots, selectedSolution))
                found[project] = DotnetTests.Discover(project);
            foreach (string folder in PythonTestFolders(roots))
            {
                try
                {
                    string projectRoot = PythonWorkingFolder(folder, roots);
                    if (TestPython(folder, projectRoot) is not { } python) { pythonErrors.Add("Python is required to discover tests in " + folder); continue; }
                    var report = PythonUnitTests.Execute(python, folder, false, projectRoot: projectRoot).GetAwaiter().GetResult();
                    found[folder] = PythonTests(folder, report);
                    foreach (var error in report["errors"]!.AsArray()) pythonErrors.Add(error!.GetValue<string>());
                }
                catch (Exception error) { pythonErrors.Add(error.Message); }
            }
            DiscoverCTest(roots, found, nativeProjects, pythonErrors);
            return found;
        }, found =>
        {
            if (generation != workspaceGeneration)
                return;
            if (nativeVersion != nativeDiscoveryVersion || pythonVersion != pythonDiscoveryVersion)
            {
                discovering = false;
                Discover();
                return;
            }
            tests = found;
            ctestProjects = nativeProjects;
            foreach (var native in nativeProjects.Values)
                nativeSelections[native.Root] = new(native.Build, native.Configuration);
            foreach (string error in pythonErrors) context.Notices.Note(error, error: true);
            discovering = false;
            discovered = true;
            context.Panels.RefreshSide(HexSide.Tests);
            Action? then = afterDiscovery;
            afterDiscovery = null;
            then?.Invoke();
        }, quiet: true);
    }

    /* Running */

    /// <summary>Ctrl+R A, every test of every test project.</summary>
    private void RunAll()
    {
        if (!discovered)
        {
            afterDiscovery = RunAll;
            Show();
            return;
        }

        lastRun = RunAll;
        foreach (string project in tests.Keys.Count > 0 ? tests.Keys.ToList() : DotnetTests.TestProjects(context.Workspace.Roots, context.Workspace.Solution))
            Run(project, null, context.Strings.Said("tests.all_of", ("project", Path.GetFileNameWithoutExtension(project))));
    }

    /// <summary>
    /// Ctrl+R T, the row picked in the explorer while it has the keyboard (a theory's case on its own), otherwise the test
    /// the caret is in, or the class's when it's between them, or the file's.
    /// </summary>
    private void RunHere()
    {
        if (DiscoverBeforePythonAction(RunHere)) return;
        if (TestsHere() is not { } here)
            return;

        lastRun = () => Run(here.Project, here.Names, here.What, here.Framework);
        lastRun();
    }

    /// <summary>Ctrl+R Ctrl+T, the same tests as Ctrl+R T run under the debugger.</summary>
    private void DebugHere()
    {
        if (DiscoverBeforePythonAction(DebugHere)) return;
        if (TestsHere() is { } here)
            Debug(here.Project, here.Names, here.What, here.Framework);
    }

    /// <summary>
    /// The tests the caret is in, the test whose declaration is the last at or above it, or the class's when it's between
    /// them, or the file's, with what to call them. Null, and said, when the file on screen has none.
    /// </summary>
    private (string Project, string[]? Names, string What, string? Framework)? TestsHere()
    {
        if (PickedRow() is { } picked)
            return (picked.Project, picked.Names, picked.What, picked.Framework);

        HexDocument current = context.Documents.Current;
        if (HexDebugging.IsPython(current.Path))
        {
            var inPythonFile = tests.Values.SelectMany(items => items)
                .Where(test => test.File.Length > 0 && PlatformPaths.Same(test.File, current.Path!))
                .OrderBy(test => test.Line).ToList();
            if (inPythonFile.Count > 0)
            {
                DotnetTest? atPython = inPythonFile.LastOrDefault(test => test.Line <= current.Editor.Document.Caret.Line);
                string[] pythonNames = atPython is { NativeName: not null }
                    ? inPythonFile.Where(test => test.Line == atPython.Line).Select(test => test.FullName).ToArray()
                    : atPython is not null ? [atPython.FullName] : inPythonFile.Select(test => test.FullName).ToArray();
                return (inPythonFile[0].Project, pythonNames,
                    atPython?.Method ?? Path.GetFileNameWithoutExtension(current.Path!), null);
            }
        }
        if (current.Path is not { } path || !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || DotnetProjects.ProjectOf(path) is not { } project
            || DotnetTests.Find(project, path, current.Editor.Text) is not { Count: > 0 } inFile)
        {
            context.Notices.Say(context.Strings.Said("tests.not_in_tests"), error: true);
            return null;
        }

        int caret = current.Editor.Document.Caret.Line;
        DotnetTest? at = inFile.LastOrDefault(test => test.Line <= caret);
        string[] names = at is not null ? [at.FullName] : [.. inFile.Select(test => test.FullClass + ".").Distinct()];
        return (project, names, at?.Method ?? Path.GetFileNameWithoutExtension(path), null);
    }

    /// <summary>The tests that failed last time again, only the cases of a theory that did.</summary>
    private void RunFailed()
    {
        foreach (var (project, its) in tests)
        {
            string[] names = [.. its.SelectMany(ResultsOf).Where(result => result.Outcome == DotnetTestOutcome.Failed).Select(result => result.FullName).Distinct()];
            if (names.Length > 0)
            {
                string what = context.Strings.Said("tests.failed_of", ("project", Path.GetFileNameWithoutExtension(project)));
                var failed = its.SelectMany(ResultsOf).Where(result => result.Outcome == DotnetTestOutcome.Failed).ToArray();
                if (failed.Any(result => result.Framework.Length > 0))
                    StartDotnet(project, names, what, false, null, failed.GroupBy(result => result.Framework).ToDictionary(group => group.Key, group => group.Select(result => result.FullName).Distinct().ToArray()));
                else Run(project, names, what);
            }
        }

        lastRun = RunFailed;
    }

    /// <summary>
    /// dotnet test on a project in a terminal tab, all of it or the tests named (their full names, or a class's). The
    /// .trx it writes is read once it's done and every test it ran gets its result.
    /// </summary>
    internal void Run(string project, IReadOnlyList<string>? names, string what, string? framework = null)
    {
        untilFailure = null;
        Start(project, names, what, framework);
    }

    // A run started, by hand or as the next round of Run Until Failure
    private void Start(string project, IReadOnlyList<string>? names, string what, string? framework = null)
    {
        if (ctestProjects.ContainsKey(project)) { RunCTest(project, names, what); return; }
        if (Directory.Exists(project)) { RunPythonTests(project, names, what); return; }
        StartDotnet(project, names, what, false, framework);
    }

    /// <summary>
    /// Debug, the tests of a project (all of it or the ones named) run with the debugger on their test host, stopping at
    /// the breakpoints in them. The .trx is read back once it's done the way a run's is.
    /// </summary>
    internal void Debug(string project, IReadOnlyList<string>? names, string what, string? framework = null)
    {
        if (ctestProjects.TryGetValue(project, out var native))
        {
            var selected = TestsOf(project, names).ToArray();
            if (selected.Length == 1) DebugCTest(project, native, selected[0]);
            else if (selected.Length > 1) context.Windows.Choose(context.Strings.Said("tests.debug"), context.Strings.Said("debugging.ctest_choose"),
                selected.Select(test => (test.Method, (Action)(() => DebugCTest(project, native, test)))).ToList());
            return;
        }
        untilFailure = null;
        if (Directory.Exists(project))
        {
            string projectRoot = PythonWorkingFolder(project, context.Workspace.Roots);
            if (TestPython(project, projectRoot) is { } python)
                DebugPythonTests(project, projectRoot, python, names, what);
            else context.Notices.Say(context.Strings.Said("debugging.python_required"), error: true);
            return;
        }
        StartDotnet(project, names, what, true, framework);
    }

    // What a run's command line narrows it down with, its filter, or a .runsettings next to its .trx with the filter in
    // when a case has a quote in its arguments that the shell would take for the end of it
    private static string FilterArguments(IReadOnlyList<string>? names, string trx)
    {
        if (names is not { Count: > 0 })
            return string.Empty;

        string filter = DotnetTests.Filter(names, quotes: true);
        if (!filter.Contains('"'))
            return $" --filter \"{filter}\"";

        string settings = Path.ChangeExtension(trx, ".runsettings");
        try
        {
            File.WriteAllText(settings, DotnetTests.RunSettings(filter));
            return $" --settings \"{settings}\"";
        }
        catch (IOException)
        {
            return $" --filter \"{DotnetTests.Filter(names)}\"";
        }
    }

    /// <summary>The runs going stopped, a Ctrl+C in each of their tabs, Run Until Failure with them. What ran so far is read back.</summary>
    private void Cancel()
    {
        untilFailure = null;
        foreach (var execution in dotnetRuns.Values.ToArray())
        {
            CancelDotnet(execution);
            if (execution.Debugged) debugger.StopDebugging();
        }
        debugger.StopCTestDebug();
        debugger.StopPythonTestDebug();
        foreach (string project in runningIn)
        {
            context.Work.Find("python-tests:" + project)?.Cancel();
            context.Work.Find("ctest:" + project)?.Cancel();
            context.Terminals.InterruptTab($"tests:{project}");
        }
    }

    // Where a run of a project writes its .trx, what was there from the last one gone
    private static string FreshTrx(string project)
    {
        string trx = Path.Combine(Path.GetTempPath(), "hex-tests", $"{Path.GetFileNameWithoutExtension(project)}-{Environment.ProcessId}-{Guid.NewGuid():N}.trx");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(trx)!);
            File.Delete(trx);
        }
        catch (IOException)
        {
        }

        return trx;
    }

    // What's about to run marked as waiting, so the rows say so, what they said last time still there until the results
    // come in. A theory's case run on its own is the only one of its cases waiting, the others still say how they came out
    private RunScope Running(string project, IReadOnlyList<string>? names, string what, string? framework = null, IReadOnlyDictionary<string, string[]>? frameworkNames = null)
    {
        string? nativeRoot = ctestProjects.GetValueOrDefault(project)?.Root;
        var scope = new RunScope(project, [], [], workspaceGeneration, context.Workspace.Configuration,
            nativeRoot, nativeRoot is null ? 0 : nativeVersions.GetValueOrDefault(nativeRoot), pythonVersions.GetValueOrDefault(project), framework, frameworkNames);
        foreach (DotnetTest test in TestsOf(project, names))
        {
            if (names is not null && !names.Any(name => Whole(test, name)))
            {
                foreach (string name in names)
                {
                    if (DotnetTests.WithoutArguments(name) == test.FullName)
                        scope.Cases.Add(name);
                }
            }
            else
                scope.Whole.Add(test.FullName);
        }

        waiting.UnionWith(scope.Whole.Select(name => Identity(project, name)));
        waiting.UnionWith(scope.Cases.Select(name => Identity(project, name)));
        runs++;
        runningWhat = what;
        context.Panels.RefreshSide(HexSide.Tests);
        return scope;
    }

    // Whether a name a run goes by takes a test in whole, its own name, its class's, or a namespace or class ending in a dot
    private static bool Whole(DotnetTest test, string name) =>
        name == test.FullName || name == test.FullClass || name == test.FullClass + "." || (name.EndsWith('.') && test.FullName.StartsWith(name, StringComparison.Ordinal));

    // The tests a run of a project names, all of them for none
    private IEnumerable<DotnetTest> TestsOf(string project, IReadOnlyList<string>? names) =>
        tests.TryGetValue(project, out List<DotnetTest>? its)
            ? its.Where(test => names is null || names.Any(name => Whole(test, name) || DotnetTests.WithoutArguments(name) == test.FullName))
            : [];

    // What a run was waiting on not waited on any more
    private void Waited(RunScope scope)
    {
        foreach (string name in scope.Whole)
            waiting.Remove(Identity(scope.Project, name));
        foreach (string name in scope.Cases)
            waiting.Remove(Identity(scope.Project, name));
    }

    // A run done, its .trx read off the editor's thread (a project's of a thousand tests is a few megabytes) with where
    // each failure failed, then what it came to said. One debugged that has no results was stopped, or never got to its
    // test host, and what dotnet test said about it is in the output rather than a terminal
    private void Finished(string project, string trx, string what, int exit, RunScope scope, bool debugged = false)
    {
        string[] roots = [.. context.Workspace.Roots];
        context.Work.InBackground(context.Strings.Said("tests.reading"), () => Read(trx, roots), read => Finished(project, read, what, exit, scope, debugged), quiet: true);
    }

    private static List<DotnetTestResult> Read(string trx, IReadOnlyList<string>? roots = null)
    {
        try
        {
            if (!File.Exists(trx))
                return [];

            return [.. DotnetTests.ReadTrx(File.ReadAllText(trx)).Select(result => result.Outcome == DotnetTestOutcome.Failed ? result with { At = DotnetTests.FailedAt(result.Stack, roots) } : result)];
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Hex] The test results couldn't be read, {e.Message}");
            return [];
        }
    }

    private void Finished(string project, List<DotnetTestResult> read, string what, int exit, RunScope scope, bool debugged, bool cancelled = false)
    {
            if (scope.Generation != workspaceGeneration || scope.Configuration != context.Workspace.Configuration)
            return;
        runs = Math.Max(0, runs - 1);
        runningIn.Remove(project);
        Waited(scope);
        if (!CurrentNativeRun(scope) || scope.PythonVersion != pythonVersions.GetValueOrDefault(project))
        {
            context.Panels.RefreshSide(HexSide.Tests);
            return;
        }

        // Completion supersedes prior results in the attempted scope, even when
        // compilation failed or the filter matched nothing. Keep other tests alone;
        // the terminal and outcome notice distinguish build failure from a test failure.
        void RemoveResults(string name, string? singleCase = null)
        {
            var identity = Identity(project, name);
            if (!results.TryGetValue(identity, out var existing)) return;
            foreach (var (key, result) in existing.ToArray())
                // An unqualified single-target result has no sibling-framework identity to preserve.
                if ((scope.Framework is null || result.Framework.Length == 0 || result.Framework == scope.Framework)
                    && (singleCase is null || result.FullName == singleCase)
                    && (scope.FrameworkNames is null || scope.FrameworkNames.TryGetValue(result.Framework, out var selected) && selected.Contains(result.FullName)))
                    existing.Remove(key);
            if (existing.Count == 0) results.Remove(identity);
        }
        foreach (string name in scope.Whole)
            RemoveResults(name);
        foreach (string name in scope.Cases)
        {
            RemoveResults(DotnetTests.WithoutArguments(name), name);
        }

        // Two cases of a theory can have the one name (two typeofs xUnit names alike), and they're two results all the
        // same. The second keeps its own under a key of its own, one went missing and the cases came out one short
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (DotnetTestResult result in read)
        {
            if (!results.TryGetValue(Identity(project, result.Test), out Dictionary<string, DotnetTestResult>? cases))
                results[Identity(project, result.Test)] = cases = new(StringComparer.Ordinal);
            string baseKey = result.Framework.Length == 0 ? result.FullName : result.Framework + "\0" + result.FullName;
            string key = baseKey;
            for (int again = 2; !named.Add(key); again++)
                key = $"{baseKey}#{again}";
            cases[key] = result;
        }

        // What ran that reading the source didn't find goes under the class it really is in
        if (DotnetTests.Reconcile(project, tests.GetValueOrDefault(project) ?? [], read) is { } reconciled)
            tests[project] = reconciled;
        FailuresChanged();

        int passed = read.Count(result => result.Outcome == DotnetTestOutcome.Passed);
        int failed = read.Count(result => result.Outcome == DotnetTestOutcome.Failed);
        int skipped = read.Count(result => result.Outcome == DotnetTestOutcome.Skipped);
        if (cancelled)
            context.Notices.Say(context.Strings.Said("tests.stopped", ("what", what), ("count", Number(read.Count))));
        else if (read.Count == 0)
        {
            untilFailure = null;
            context.Notices.Say(context.Strings.Said(exit == 0 ? "tests.none_ran" : debugged ? "tests.no_results" : "tests.build_failed", ("what", what)), error: exit != 0);
        }
        else if (!AgainUntilFailure(failed, debugged))
            context.Notices.Say(context.Strings.Said("tests.ran", ("what", what), ("passed", Number(passed)), ("failed", Number(failed)), ("skipped", Number(skipped))), error: failed > 0);

        // The rows put right where they're showing, nothing brought up. Showing the explorer as a run ended took the
        // keyboard off whatever was being typed in meanwhile, the failures are in Problems and the notice says how it went
        context.Panels.RefreshSide(HexSide.Tests);
    }
}
