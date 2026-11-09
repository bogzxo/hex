using Hex.Core.Projects;
using System.Collections.Concurrent;

namespace Hex.Dawn;

internal sealed partial class HexDebugging
{
    private int debugBuildGeneration;
    private readonly ConcurrentDictionary<string, string> chosenFrameworks = new(PlatformPaths.Comparer);
    private static string FrameworkKey(string project, string configuration, DotnetRuntimeOptions? runtime = null) =>
        string.Join('\n', project, configuration, runtime?.RuntimeIdentifier ?? string.Empty, runtime?.Architecture ?? string.Empty,
            runtime?.OS ?? string.Empty, runtime?.SelfContained?.ToString() ?? string.Empty);

    private void SelectDebugFramework()
    {
        if (Debugging || ProjectsToDebug() is not { Count: > 0 } projects) return;
        void Select(string project)
        {
            chosenFrameworks.TryRemove(FrameworkKey(project, context.Workspace.Configuration), out _);
            StartDebugging(project);
        }
        if (projects.Count == 1) Select(projects[0]);
        else context.Windows.Choose(context.Strings.Said("debugging.which_project"), context.Strings.Said("debugging.which_project_text"),
            [.. projects.Select(project => (context.Workspace.Relative(project), (Action)(() => Select(project))))]);
    }

    private async Task<(string? Selected, IReadOnlyList<string> Offered)> DebugFramework(string project, string configuration, DotnetRuntimeOptions? runtime, CancellationToken cancel)
    {
        var (code, output) = await DotnetCli.Run(Path.GetDirectoryName(project)!,
            DotnetDebugTarget.PropertyArguments(project, configuration, null, "TargetFramework,TargetFrameworks", runtime), TimeSpan.FromMinutes(2), cancel);
        if (code != 0) throw new InvalidOperationException(output.Trim());
        var offered = DotnetDebugTarget.Frameworks(DotnetEvaluation.Parse(project, output));
        if (offered.Count <= 1) return (offered.FirstOrDefault(), offered);
        string key = FrameworkKey(project, configuration, runtime);
        string? selected = chosenFrameworks.TryGetValue(key, out var chosen) ? offered.FirstOrDefault(framework => framework.Equals(chosen, StringComparison.OrdinalIgnoreCase)) : null;
        return (selected, offered);
    }

    private void ChooseDebugFramework(string project, string configuration, DotnetRunLine? run, string? runFolder, IReadOnlyList<string> offered, CancellationToken cancel)
    {
        // No worker waits on a dialog. Escape leaves the IDE idle; old choices cannot outlive a new build or workspace.
        int generation = debugBuildGeneration;
        PostBuild(cancel, () =>
        {
            building = false;
            context.Panels.RefreshSide(HexSide.Debug);
            context.Notices.Say(context.Strings.Said("debugging.choose_framework_text", ("project", Path.GetFileName(project))));
            context.Windows.Choose(context.Strings.Said("debugging.choose_framework"), context.Strings.Said("debugging.choose_framework_text", ("project", Path.GetFileName(project))),
                offered.Select(framework => (framework, (Action)(() =>
                {
                    if (generation != debugBuildGeneration || (run?.Configuration ?? context.Workspace.Configuration) != configuration) return;
                    chosenFrameworks[FrameworkKey(project, configuration, run?.RuntimeOptions)] = framework;
                    var selected = (run ?? new DotnetRunLine(project, null, null, false, null)) with { Framework = framework };
                    StartDebugging(project, selected, runFolder);
                }))).ToList());
        });
    }
}
