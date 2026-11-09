using Hex.Core.Projects;
using Horizon.Core.Threading;

namespace Hex.Dawn;

internal sealed partial class HexTestExplorer
{
    // A project is one logical run, with one native invocation and one TRX per evaluated framework. Debug hosts must
    // run in sequence: VSTEST_HOST_DEBUG makes every host wait, and one adapter can only attach to one at a time.
    private sealed class DotnetExecution(RunScope scope, string what, bool debugged)
    {
        public RunScope Scope { get; } = scope;
        public string What { get; } = what;
        public bool Debugged { get; } = debugged;
        public bool Cancelled { get; set; }
        public bool LabelFrameworks { get; set; }
        public bool Parallel { get; set; }
        public string[] ParallelFrameworks { get; set; } = [];
        public int Active { get; set; }
        public HashSet<string> TerminalKeys { get; } = new(StringComparer.Ordinal);
        public List<(string? Framework, IReadOnlyList<string>? Names)> Prepared { get; } = [];
        public int Exit { get; set; }
        public Queue<(string? Framework, IReadOnlyList<string>? Names)> Pending { get; } = new();
        public List<DotnetTestResult> Results { get; } = [];
        public List<string> FailedFrameworks { get; } = [];
        public HashSet<string> CompletedFrameworks { get; } = new(StringComparer.Ordinal);
    }

    private readonly Dictionary<string, DotnetExecution> dotnetRuns = new(PlatformPaths.Comparer);
    private readonly Dictionary<string, HashSet<string>> frameworkBuildFailures = new(PlatformPaths.Comparer);

    private bool Current(DotnetExecution run) => run.Scope.Generation == workspaceGeneration
        && run.Scope.Configuration == context.Workspace.Configuration
        && dotnetRuns.TryGetValue(run.Scope.Project, out var current) && ReferenceEquals(current, run);

    private void StartDotnet(string project, IReadOnlyList<string>? names, string what, bool debugged, string? framework,
        IReadOnlyDictionary<string, string[]>? frameworkNames = null)
    {
        if (runningIn.Contains(project) || debugged && debugger.Debugging) return;
        if (debugged) context.Documents.SaveAll(open => open.IsText && open.Path is not null && (open.Modified || open.CodeEdited));
        var run = new DotnetExecution(Running(project, names, what, framework, frameworkNames), what, debugged);
        dotnetRuns[project] = run;
        runningIn.Add(project);
        context.Notices.Say(context.Strings.Said(debugged ? "tests.debugging" : "tests.running", ("what", what)));
        IReadOnlyList<string> frameworks = [];
        context.Work.Start("dotnet-tests:" + project, context.Strings.Said("tests.finding"), async work =>
        {
            if (framework is not null) { frameworks = [framework]; return; }
            var (exit, output) = await DotnetCli.Run(Path.GetDirectoryName(project)!,
                DotnetDebugTarget.PropertyArguments(project, run.Scope.Configuration, null, "TargetFramework,TargetFrameworks,TestTfmsInParallel"), TimeSpan.FromMinutes(2), work.Token);
            if (exit != 0) throw new InvalidOperationException(output.Trim());
            var evaluation = DotnetEvaluation.Parse(project, output);
            frameworks = DotnetDebugTarget.Frameworks(evaluation);
            string[] selected = frameworks.Where(target => frameworkNames is null || frameworkNames.ContainsKey(target)).ToArray();
            if (!debugged && selected.Length > 1 && evaluation.Properties.GetValueOrDefault("TestTfmsInParallel")?.Trim().Equals("false", StringComparison.OrdinalIgnoreCase) != true)
                run.Parallel = await ParallelDotnetOutputs(project, run.Scope.Configuration, selected, work.Token);
        }, item =>
        {
            if (!Current(run)) return;
            run.Cancelled |= item.State == WorkState.Cancelled;
            if (item.State == WorkState.Failed || run.Cancelled)
            {
                if (item.Error is { } error) context.Notices.Note(error, error: true);
                run.Exit = -1;
                CompleteDotnet(run);
                return;
            }
            run.LabelFrameworks = frameworks.Count > 1 || framework is not null || frameworkNames is not null;
            IEnumerable<string?> targets = frameworks.Select(value => (string?)value).DefaultIfEmpty(null);
            foreach (string? target in targets)
            {
                if (frameworkNames is not null)
                {
                    if (target is not null && frameworkNames.TryGetValue(target, out var selected)) run.Pending.Enqueue((target, selected));
                }
                else run.Pending.Enqueue((target, names));
            }
            if (run.Parallel)
            {
                run.ParallelFrameworks = run.Pending.Select(target => target.Framework!).ToArray();
                PrepareParallelDotnet(run);
            }
            else NextDotnet(run);
        }, seen: false);
    }

    private void NextDotnet(DotnetExecution run)
    {
        if (!Current(run)) return;
        if (run.Cancelled || !run.Pending.TryDequeue(out var next)) { CompleteDotnet(run); return; }
        InvokeDotnet(run, next);
    }

    private void InvokeDotnet(DotnetExecution run, (string? Framework, IReadOnlyList<string>? Names) next, bool prebuilt = false)
    {
        string project = run.Scope.Project, trx = FreshTrx(project);
        string tab = prebuilt ? $"tests:{project}:{next.Framework}" : $"tests:{project}";
        run.Active++;
        bool done = false;
        void Done(int exit)
        {
            if (!Current(run) || done) return;
            done = true;
            run.TerminalKeys.Remove(tab);
            if (exit != 0) run.Exit = exit;
            if (exit == -1) CancelDotnet(run);
            string[] roots = [.. context.Workspace.Roots];
            context.Work.InBackground(context.Strings.Said("tests.reading"), () => Read(trx, roots), read =>
            {
                if (!Current(run)) return;
                run.Active--;
                if (!run.Cancelled) run.CompletedFrameworks.Add(next.Framework ?? string.Empty);
                if (exit != 0 && read.Count == 0 && !run.Cancelled) run.FailedFrameworks.Add(next.Framework ?? string.Empty);
                run.Results.AddRange(read.Select(result => result with { Framework = run.LabelFrameworks ? next.Framework ?? string.Empty : string.Empty }));
                if (!run.Parallel) NextDotnet(run);
                else NextParallelDotnet(run);
            }, quiet: true);
        }
        if (run.Debugged)
            debugger.DebugTests(project, next.Names is { Count: > 0 } ? DotnetTests.Filter(next.Names, quotes: true) : null,
                trx, Done, () => Done(-1), next.Framework);
        else
        {
            string target = next.Framework is null ? string.Empty : $" --framework \"{next.Framework}\"";
            string command = $"dotnet test \"{project}\"{FilterArguments(next.Names, trx)} --configuration \"{run.Scope.Configuration}\"{target}{(prebuilt ? " --no-build --no-restore" : string.Empty)} --logger \"trx;LogFileName={trx}\"";
            run.TerminalKeys.Add(tab);
            string name = Path.GetFileNameWithoutExtension(project) + (prebuilt ? " (" + next.Framework + ")" : string.Empty);
            context.Terminals.RunInTab(tab, context.Strings.Said("tests.tab", ("project", name)),
                Path.GetDirectoryName(project)!, command, (exit, _) => Done(exit));
        }
    }

    private void CompleteDotnet(DotnetExecution run)
    {
        if (!Current(run)) return;
        dotnetRuns.Remove(run.Scope.Project);
        if (!frameworkBuildFailures.TryGetValue(run.Scope.Project, out var failures))
            frameworkBuildFailures[run.Scope.Project] = failures = new(StringComparer.Ordinal);
        failures.ExceptWith(run.CompletedFrameworks);
        failures.UnionWith(run.FailedFrameworks);
        if (failures.Count == 0) frameworkBuildFailures.Remove(run.Scope.Project);
        if (run.FailedFrameworks.Count > 0) untilFailure = null;
        Finished(run.Scope.Project, run.Results, run.What, run.Exit, run.Scope, run.Debugged, run.Cancelled);
        if (run.FailedFrameworks.Count > 0)
            context.Notices.Say(context.Strings.Said("tests.framework_failed", ("what", run.What), ("frameworks", string.Join(", ", run.FailedFrameworks.Select(FrameworkLabel)))), error: true);
    }

    private string FrameworkLabel(string framework) => framework.Length > 0 ? framework : context.Strings.Said("tests.default_framework");

    private bool FrameworkWaiting(string project, string framework) => !dotnetRuns.TryGetValue(project, out var run)
        || (run.Scope.Framework is null || run.Scope.Framework == framework)
            && (run.Scope.FrameworkNames is null || run.Scope.FrameworkNames.ContainsKey(framework));
}
