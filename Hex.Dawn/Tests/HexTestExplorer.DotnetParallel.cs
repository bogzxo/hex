using Hex.Core.Projects;
using Horizon.Core.Threading;

namespace Hex.Dawn;

internal sealed partial class HexTestExplorer
{
    private int parallelTestHosts = Math.Max(1, Environment.ProcessorCount);

    private static async Task<bool> ParallelDotnetOutputs(string project, string configuration, IReadOnlyList<string> frameworks, CancellationToken cancel)
    {
        try
        {
            var outputs = new List<DotnetEvaluation>();
            foreach (string framework in frameworks)
            {
                var (exit, output) = await DotnetCli.Run(Path.GetDirectoryName(project)!,
                    DotnetDebugTarget.PropertyArguments(project, configuration, framework, "OutDir,TargetPath,IntermediateOutputPath,TestTfmsInParallel"), TimeSpan.FromMinutes(2), cancel);
                if (exit != 0) return false;
                outputs.Add(DotnetEvaluation.Parse(project, output));
            }
            return DotnetTests.CanRunFrameworksInParallel(outputs);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    // Finish all builds before starting hosts. Concurrent dotnet test builds would share restore state even when
    // framework binaries have separate folders. Failed builds never reach --no-build and cannot run stale binaries.
    private void PrepareParallelDotnet(DotnetExecution run)
    {
        if (!Current(run)) return;
        if (run.Cancelled) { CompleteDotnet(run); return; }
        if (!run.Pending.TryDequeue(out var next)) { LaunchParallelDotnet(run); return; }
        string project = run.Scope.Project, tab = "tests:" + project;
        run.TerminalKeys.Add(tab);
        string command = $"dotnet build \"{project}\" --configuration \"{run.Scope.Configuration}\" --framework \"{next.Framework}\" --nologo --verbosity quiet";
        context.Terminals.RunInTab(tab, context.Strings.Said("tests.tab", ("project", Path.GetFileNameWithoutExtension(project))),
            Path.GetDirectoryName(project)!, command, (exit, _) =>
            {
                if (!Current(run)) return;
                run.TerminalKeys.Remove(tab);
                if (exit == -1) CancelDotnet(run);
                if (run.Cancelled) { CompleteDotnet(run); return; }
                if (exit == 0) run.Prepared.Add(next);
                else
                {
                    run.Exit = exit;
                    run.CompletedFrameworks.Add(next.Framework ?? string.Empty);
                    run.FailedFrameworks.Add(next.Framework ?? string.Empty);
                }
                PrepareParallelDotnet(run);
            });
    }

    private void LaunchParallelDotnet(DotnetExecution run)
    {
        if (run.Prepared.Count == 0) { CompleteDotnet(run); return; }
        // Restore may have introduced package props that change output paths. Check the final evaluated contexts too.
        bool isolated = false;
        context.Work.Start("dotnet-tests:" + run.Scope.Project, context.Strings.Said("tests.finding"), async work =>
        {
            isolated = await ParallelDotnetOutputs(run.Scope.Project, run.Scope.Configuration, run.ParallelFrameworks, work.Token);
        }, item =>
        {
            if (!Current(run)) return;
            run.Cancelled |= item.State == WorkState.Cancelled;
            if (run.Cancelled) { CompleteDotnet(run); return; }
            if (item.State == WorkState.Failed || !isolated)
            {
                // Rebuild each prepared target immediately before its test if isolation changed during restore.
                run.Parallel = false;
                foreach (var target in run.Prepared) run.Pending.Enqueue(target);
                NextDotnet(run);
                return;
            }
            foreach (var target in run.Prepared) run.Pending.Enqueue(target);
            NextParallelDotnet(run);
        }, seen: false);
    }

    private void NextParallelDotnet(DotnetExecution run)
    {
        if (!Current(run)) return;
        if (run.Cancelled) run.Pending.Clear();
        while (!run.Cancelled && run.Active < parallelTestHosts && run.Pending.TryDequeue(out var next))
            InvokeDotnet(run, next, prebuilt: true);
        if (run.Active == 0) CompleteDotnet(run);
    }

    private void CancelDotnet(DotnetExecution run)
    {
        run.Cancelled = true;
        context.Work.Find("dotnet-tests:" + run.Scope.Project)?.Cancel();
        foreach (string tab in run.TerminalKeys.ToArray()) context.Terminals.InterruptTab(tab);
    }
}
