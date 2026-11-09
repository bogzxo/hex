using System.Text.Json.Nodes;
using Hex.Core.Notebooks;
using Hex.Core.Projects;
using Horizon.Core.Threading;

namespace Hex.Dawn;

internal sealed partial class HexTestExplorer
{
    private readonly Dictionary<string, int> pythonVersions = new(PlatformPaths.Comparer);
    private int pythonDiscoveryVersion;

    internal void PythonInterpreterChanged(string root)
    {
        pythonDiscoveryVersion++;
        foreach (string project in tests.Keys.Concat(runningIn).Distinct(PlatformPaths.Comparer).Where(project =>
            Directory.Exists(project) && PlatformPaths.IsUnder(project, root)).ToArray())
        {
            pythonVersions[project] = pythonVersions.GetValueOrDefault(project) + 1;
            context.Work.Find("python-tests:" + project)?.Cancel();
            tests.Remove(project);
            foreach (var identity in results.Keys.Where(identity => PlatformPaths.Same(identity.Project, project)).ToArray()) results.Remove(identity);
        }
        lastRun = null;
        untilFailure = null;
        FailuresChanged();
        bool refresh = discovered || discovering;
        discovered = false;
        if (refresh && !discovering) Discover();
        context.Panels.RefreshSide(HexSide.Tests);
    }

    private bool DiscoverBeforePythonAction(Action then)
    {
        if (discovered || !HexDebugging.IsPython(context.Documents.Current.Path)) return false;
        HexDocument requested = context.Documents.Current;
        int line = requested.Editor.Document.Caret.Line;
        afterDiscovery = () =>
        {
            if (ReferenceEquals(context.Documents.Current, requested) && requested.Editor.Document.Caret.Line == line) then();
        };
        Show();
        return true;
    }
    internal static string PythonWorkingFolder(string folder, IEnumerable<string> roots) =>
        roots.Where(root => PlatformPaths.IsUnder(folder, root)).OrderByDescending(root => root.Length).FirstOrDefault() ?? folder;

    private static string? TestPython(string folder, string projectRoot) => PythonEnvironment.FindLocal(folder, projectRoot)
        ?? KernelSpecs.Pythons().FirstOrDefault();

    private static IEnumerable<string> PythonTestFolders(IEnumerable<string> roots)
    {
        foreach (string root in roots)
        {
            if (PythonUnitTests.DiscoveryFolder(root) is { } folder) yield return folder;
        }
    }

    private static List<DotnetTest> PythonTests(string folder, JsonObject report) => report["tests"]!.AsArray().Select(row =>
    {
        string id = row!["id"]!.GetValue<string>();
        if (report["framework"]?.GetValue<string>() == "pytest")
            return new DotnetTest(folder, "", row["class"]!.GetValue<string>(), row["name"]!.GetValue<string>(), row["file"]!.GetValue<string>(), row["line"]!.GetValue<int>()) { NativeName = id };
        int dot = id.LastIndexOf('.');
        string fullClass = dot < 0 ? "Tests" : id[..dot];
        var (ns, cls) = DotnetTests.SplitClass(fullClass);
        return new DotnetTest(folder, ns, cls, dot < 0 ? id : id[(dot + 1)..], row["file"]!.GetValue<string>(), row["line"]!.GetValue<int>());
    }).ToList();

    private static List<DotnetTestResult> PythonResults(JsonObject report) => report["results"]!.AsArray().Select(row =>
    {
        string id = row!["id"]!.GetValue<string>();
        return new DotnetTestResult(id, row["outcome"]!.GetValue<string>() switch
        {
            "passed" => DotnetTestOutcome.Passed, "skipped" => DotnetTestOutcome.Skipped, _ => DotnetTestOutcome.Failed
        }, TimeSpan.FromSeconds(row["duration"]?.GetValue<double>() ?? 0), row["message"]?.GetValue<string>() ?? "",
            row["stack"]?.GetValue<string>() ?? "", row["output"]?.GetValue<string>() ?? report["output"]?.GetValue<string>() ?? "")
        {
            ExactName = report["framework"]?.GetValue<string>() == "pytest",
            Class = row["class"]?.GetValue<string>() ?? (id.Contains('.') ? id[..id.LastIndexOf('.')] : ""),
            At = row["failureFile"] is { } file ? (file.GetValue<string>(), row["failureLine"]!.GetValue<int>()) : null
        };
    }).ToList();

    private void RunPythonTests(string folder, IReadOnlyList<string>? names, string what)
    {
        if (runningIn.Contains(folder) || context.Work.Find("python-tests:" + folder) is not null) return;
        string[]? selected = names is null ? null : TestsOf(folder, names).Select(test => test.FullName).ToArray();
        if (selected is { Length: 0 }) return;
        string projectRoot = PythonWorkingFolder(folder, context.Workspace.Roots);
        if (TestPython(folder, projectRoot) is not { } python)
        {
            context.Notices.Say(context.Strings.Said("debugging.python_required"), error: true);
            return;
        }
        RunScope scope = Running(folder, names, what);
        runningIn.Add(folder);
        JsonObject? report = null;
        context.Work.Start("python-tests:" + folder, context.Strings.Said("tests.running", ("what", what)),
            async work => report = await PythonUnitTests.Execute(python, folder, true, selected, work.Token, projectRoot), item =>
            {
                if (scope.Generation != workspaceGeneration) return;
                if (report is not null)
                {
                    context.Notices.Note(report["output"]?.GetValue<string>() ?? "");
                    context.Notices.Note(report["stderr"]?.GetValue<string>() ?? "");
                    foreach (var error in report["errors"]!.AsArray()) context.Notices.Note(error!.GetValue<string>(), error: true);
                }
                if (item.State == WorkState.Failed) context.Notices.Note(item.Error ?? "Python tests failed", error: true);
                var read = report is null ? [] : PythonResults(report);
                Finished(folder, read, what, item.State == WorkState.Succeeded && report?["errors"] is JsonArray { Count: 0 } ? 0 : 1, scope, false,
                    cancelled: item.State == WorkState.Cancelled);
            });
    }

    private void DebugPythonTests(string folder, string projectRoot, string python, IReadOnlyList<string>? names, string what)
    {
        if (runningIn.Contains(folder)) return;
        RunScope? scope = null;
        debugger.DebugPythonTests(folder, projectRoot, python, TestsOf(folder, names).Select(test => test.FullName).ToArray(), () =>
        {
            scope = Running(folder, names, what);
            runningIn.Add(folder);
        }, report =>
        {
            if (scope is null) return;
            if (report?["errors"] is JsonArray errors)
                foreach (var error in errors) context.Notices.Note(error!.GetValue<string>(), error: true);
            var read = report is null ? [] : PythonResults(report);
            Finished(folder, read, what, read.Count == 0 || report?["errors"] is JsonArray { Count: > 0 }
                || read.Any(result => result.Outcome == DotnetTestOutcome.Failed) ? 1 : 0, scope, true);
        });
    }
}
