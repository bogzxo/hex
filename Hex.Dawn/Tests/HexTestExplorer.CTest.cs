using Hex.Core.Projects;
using Horizon.Core.Threading;

namespace Hex.Dawn;

internal sealed partial class HexTestExplorer
{
    private sealed record CTestProject(string Root, string Build, string Configuration);
    private Dictionary<string, CTestProject> ctestProjects = new(PlatformPaths.Comparer);
    private readonly Dictionary<string, CMakeBuildSelection> nativeSelections = new(PlatformPaths.Comparer);
    private readonly Dictionary<string, int> nativeVersions = new(PlatformPaths.Comparer);
    private int nativeDiscoveryVersion;
    private bool CurrentNativeRun(RunScope scope) => scope.NativeRoot is not { } root || scope.NativeVersion == nativeVersions.GetValueOrDefault(root);

    internal void NativeBuildChanged(string root, CMakeBuildSelection selection)
        => RefreshNativeBuild(root, selection, false);

    internal void NativeBuildReconfigured(string root, CMakeBuildSelection selection)
        => RefreshNativeBuild(root, selection, true);

    private void RefreshNativeBuild(string root, CMakeBuildSelection selection, bool reconfigured)
    {
        if (!reconfigured && nativeSelections.TryGetValue(root, out var previous) && previous == selection) return;
        nativeSelections[root] = selection;
        nativeVersions[root] = nativeVersions.GetValueOrDefault(root) + 1;
        nativeDiscoveryVersion++;
        foreach (var (project, native) in ctestProjects.Where(pair => PlatformPaths.Same(pair.Value.Root, root)).ToArray())
        {
            context.Work.Find("ctest:" + project)?.Cancel();
            tests.Remove(project);
            foreach (var identity in results.Keys.Where(identity => PlatformPaths.Same(identity.Project, project)).ToArray()) results.Remove(identity);
        }
        FailuresChanged();
        bool refresh = discovered;
        discovered = false;
        if (refresh && !discovering) Discover();
        context.Panels.RefreshSide(HexSide.Tests);
    }

    private void DebugCTest(string project, CTestProject native, DotnetTest test)
    {
        if (runningIn.Contains(project)) return;
        untilFailure = null;
        RunScope? scope = null;
        debugger.DebugCTest(native.Root, native.Build, native.Configuration, test.Method, () =>
        {
            scope = Running(project, [test.FullName], test.Method);
            runningIn.Add(project);
        }, result =>
        {
            if (scope is null) return;
            List<DotnetTestResult> read = result is null ? [] : [CTestResultOf(result)];
            Finished(project, read, test.Method, result is null || result.Outcome == "failed" ? 1 : 0, scope, true);
        });
    }

    private static DotnetTestResult CTestResultOf(CTestResult row) => new("CTest." + row.Name,
        row.Outcome switch { "passed" => DotnetTestOutcome.Passed, "skipped" => DotnetTestOutcome.Skipped, _ => DotnetTestOutcome.Failed },
        TimeSpan.FromSeconds(row.Seconds), row.Outcome == "failed" ? row.Output : "", "", row.Output)
        { Class = "CTest", ExactName = true };

    private static void DiscoverCTest(string[] roots, Dictionary<string, List<DotnetTest>> found,
        Dictionary<string, CTestProject> projects, List<string> errors)
    {
        if (CMakeProject.Tool("ctest") is not { } ctest) return;
        foreach (string root in roots)
        {
            var selection = CMakeProject.SelectedBuild(root);
            string? build = selection?.Directory;
            if (build is null && CMakeProject.CompilationDirectory(root) is { } compilation
                && File.Exists(Path.Combine(compilation, "CTestTestfile.cmake"))) build = compilation;
            build ??= new[] { root, Path.Combine(root, "build"), Path.Combine(root, "build", "hex-debug") }
                .FirstOrDefault(folder => File.Exists(Path.Combine(folder, "CTestTestfile.cmake")));
            if (build is null || !File.Exists(Path.Combine(build, "CTestTestfile.cmake"))) continue;
            try
            {
                string cache = Path.Combine(build, "CMakeCache.txt");
                string? type = File.Exists(cache) ? File.ReadLines(cache).FirstOrDefault(line => line.StartsWith("CMAKE_BUILD_TYPE:STRING=", StringComparison.Ordinal)) : null;
                string configuration = selection?.Configuration
                    ?? (type is not null && type[(type.IndexOf('=') + 1)..] is { Length: > 0 } value ? value : "Debug");
                string key = Path.Combine(build, "CTestTestfile.cmake");
                var cases = CTest.Discover(ctest, build, configuration).GetAwaiter().GetResult();
                if (cases.Count == 0) continue;
                projects[key] = new(root, build, configuration);
                found[key] = cases.Select(test => new DotnetTest(key, "", "CTest", test.Name, test.File, test.Line)).ToList();
            }
            catch (Exception error) { errors.Add(error.Message); }
        }
    }

    private void RunCTest(string project, IReadOnlyList<string>? names, string what)
    {
        if (runningIn.Contains(project) || context.Work.Find("ctest:" + project) is not null) return;
        if (!ctestProjects.TryGetValue(project, out var native) || CMakeProject.Tool("ctest") is not { } ctest || CMakeProject.Tool("cmake") is not { } cmake) return;
        string[] selected = TestsOf(project, names).Select(test => test.Method).ToArray();
        RunScope scope = Running(project, names, what);
        runningIn.Add(project);
        CTestRun? result = null;
        CMakeResult? built = null;
        List<TerminalProblem> buildProblems = [];
        context.Documents.SaveAll(document => document.IsText && document.Modified);
        context.Work.Start("ctest:" + project, context.Strings.Said("tests.running", ("what", what)), async work =>
        {
            built = await CMakeProject.Run(cmake, native.Root, ["--build", native.Build, "--config", native.Configuration], work.Token);
            buildProblems = HexBuildProblems.Read(built.Output, built.ExitCode, Path.Combine(native.Root, "CMakeLists.txt"));
            if (built.ExitCode != 0) throw new IOException(built.Output);
            result = await CTest.Run(ctest, native.Build, native.Configuration, selected, work.Token);
        }, item =>
        {
            if (scope.Generation != workspaceGeneration || scope.Configuration != context.Workspace.Configuration) return;
            if (!CurrentNativeRun(scope))
            {
                Finished(project, [], what, 0, scope, false);
                return;
            }
            if (built is not null)
            {
                context.Notices.Note(built.Output);
                HexBuildProblems.Show(context, buildProblems, native.Build, built.ExitCode != 0);
            }
            if (result is not null) context.Notices.Note(result.Output);
            if (item.State == WorkState.Failed && (built is null || built.ExitCode == 0)) context.Notices.Note(item.Error ?? "CTest failed", error: true);
            List<DotnetTestResult> read = result?.Results.Select(CTestResultOf).ToList() ?? [];
            Finished(project, read, what, result?.ExitCode ?? 1, scope, false);
        });
    }
}
