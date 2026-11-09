using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

using Horizon.UI.Scripting;

namespace Hex.Dawn;

// Debugging a test the way Visual Studio's Test Explorer does. dotnet test runs with VSTEST_HOST_DEBUG=1, which has the
// test host say its process id and wait for a debugger before it runs anything, netcoredbg is put on that process with
// the breakpoints there are, and the test goes on into them. What dotnet test says goes in the output, and the .trx it
// writes is read back by the explorer the way a run's is.
internal sealed partial class HexDebugging
{
    // What the test host says once it's waiting, its process id after the words Process Id
    private static readonly Regex HostSaid = new(@"Process Id:\s*(\d+)", RegexOptions.Compiled);

    // The token owns the test process, attachment and all queued callbacks for one attempt.
    private CancellationTokenSource? testAttempt;
    private Action<int>? testFinished;

    private void PostTest(CancellationTokenSource attempt, Action action) => debugWork.Enqueue(() =>
    {
        if (ReferenceEquals(testAttempt, attempt) && !attempt.IsCancellationRequested)
            action();
    });

    // Restart transfers the explorer's outstanding run to the replacement; Stop completes it exactly once.
    private bool CancelTestRun(bool finish)
    {
        if (testAttempt is not { } attempt)
            return false;
        Action<int>? completed = testFinished;
        testAttempt = null;
        testFinished = null;
        building = false;
        hostJustAttached = false;
        attempt.Cancel();
        if (finish)
            completed?.Invoke(-1);
        return true;
    }

    internal void WorkspaceChanged()
    {
        bool replaced = !ReferenceEquals(watchWorkspace, context.Workspace.WatchExpressions);
        RestoreWatches();
        if (testAttempt is null && debugBuild is null && (debugging is null || !replaced))
        {
            if (replaced) ReadKeptBreakpoints();
            return;
        }
        CancelTestRun(finish: true);
        EndDebugging(terminate: true);
        restart = null;
        if (replaced) ReadKeptBreakpoints();
    }

    // Whether the next stop is the first of a test host that was attached to, which goes on by itself when it isn't in the
    // program's own code (a test host that breaks into the debugger once it's on, before any test)
    private bool hostJustAttached;

    /// <summary>The process id a test host said it's waiting under, null for a line that isn't that.</summary>
    internal static int? TestHostIn(string line) =>
        HostSaid.Match(line) is { Success: true } said && int.TryParse(said.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int id) ? id : null;

    /// <summary>
    /// The tests of a project (a filter of dotnet test's, null for every one) run under the debugger, the results written
    /// to a .trx. <paramref name="finished"/> is told how dotnet test ended, <paramref name="gaveUp"/> when it never ran
    /// (something's being debugged already, or there's no debugger to do it with).
    /// </summary>
    internal void DebugTests(string project, string? filter, string trx, Action<int> finished, Action gaveUp, string? framework = null)
    {
        if (Debugging)
        {
            context.Notices.Say(context.Strings.Said("debugging.already_running"));
            gaveUp();
            return;
        }

        if (Netcoredbg() is not { } debugger)
        {
            if (!InstallingFirst(() => DebugTests(project, filter, trx, finished, gaveUp, framework)))
            {
                context.Notices.Say(NoDebugger, error: true);
                gaveUp();
            }

            return;
        }

        SaveAllCode();
        var attempt = new CancellationTokenSource();
        testAttempt = attempt;
        testFinished = finished;
        building = true;
        debuggedProject = project;
        restart = () => DebugTests(project, filter, trx, finished, () => { }, framework);
        context.Panels.ShowOutput();
        context.Panels.ShowSide(HexSide.Debug);
        context.Notices.Note(context.Strings.Said("debugging.building_to_debug", ("project", Path.GetFileName(project))));

        var breakpointsNow = BreakpointsNow();
        string configuration = context.Workspace.Configuration;
        _ = Task.Run(() => RunTestHost(project, filter, trx, debugger, breakpointsNow, configuration, attempt, framework));
    }

    private async Task RunTestHost(string project, string? filter, string trx, string debugger, Dictionary<string, IReadOnlyCollection<DebugBreakpoint>> breakpointsNow, string configuration, CancellationTokenSource attempt, string? framework)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(project)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("test");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("--configuration");
        start.ArgumentList.Add(configuration);
        if (!string.IsNullOrEmpty(framework))
        {
            start.ArgumentList.Add("--framework");
            start.ArgumentList.Add(framework);
        }
        if (filter is not null)
        {
            start.ArgumentList.Add("--filter");
            start.ArgumentList.Add(filter);
        }

        start.ArgumentList.Add("--logger");
        string attemptTrx = Path.Combine(Path.GetDirectoryName(trx)!, $"{Path.GetFileNameWithoutExtension(trx)}-{Guid.NewGuid():N}.trx");
        start.ArgumentList.Add($"trx;LogFileName={attemptTrx}");
        start.Environment["VSTEST_HOST_DEBUG"] = "1";

        CancellationToken cancel = attempt.Token;
        Process? run = null;
        int exit = -1;
        string? failure = null;
        try
        {
            cancel.ThrowIfCancellationRequested();
            run = Process.Start(start) ?? throw new InvalidOperationException("dotnet test did not start.");
            Horizon.Core.Platform.ChildProcesses.Adopt(run);
            using var stop = cancel.Register(() => KillQuietly(run));
            var host = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Said(object _, DataReceivedEventArgs line)
            {
                if (line.Data is not { } text || cancel.IsCancellationRequested)
                    return;
                PostTest(attempt, () => context.Notices.Note(text));
                if (TestHostIn(text) is { } id)
                    host.TrySetResult(id);
            }
            run.OutputDataReceived += Said;
            run.ErrorDataReceived += Said;
            run.BeginOutputReadLine();
            run.BeginErrorReadLine();
            Task ended = run.WaitForExitAsync();
            if (await Task.WhenAny(host.Task, ended) == host.Task)
            {
                cancel.ThrowIfCancellationRequested();
                int hostId = await host.Task;
                PostTest(attempt, () => hostJustAttached = true);
                DebugSession? session = DebugSession.Start(debugger, ["--interpreter=vscode"]);
                await StartUnder(session, $"testhost {hostId}", DebugSession.CORECLR,
                    started => started.Attach(hostId, breakpointsNow), cancel);
                if (session is null || session.State == DebugState.Ended)
                {
                    PostTest(attempt, () => context.Notices.Say(context.Strings.Said("tests.attach_failed",
                        ("why", session?.Failed ?? context.Strings.Said("debugging.debugger_did_not_start"))), error: true));
                    KillQuietly(run);
                }
            }
            await ended;
            exit = run.ExitCode;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        catch (Exception e)
        {
            failure = e.Message;
            if (run is not null)
                KillQuietly(run);
        }
        finally
        {
            if (run is not null)
            {
                if (cancel.IsCancellationRequested)
                    KillQuietly(run);
                await run.WaitForExitAsync();
                run.Dispose();
            }
            // Only the current attempt publishes its TRX or completes the explorer run. Old callbacks must not
            // erase the replacement's process, building state or pending result accounting.
            debugWork.Enqueue(() =>
            {
                try
                {
                    if (!ReferenceEquals(testAttempt, attempt) || cancel.IsCancellationRequested)
                        return;
                    Action<int>? completed = testFinished;
                    testAttempt = null;
                    testFinished = null;
                    building = false;
                    if (failure is not null)
                        context.Notices.Say(context.Strings.Said("debugging.launch_failed", ("why", failure)), error: true);
                    try
                    {
                        if (File.Exists(attemptTrx))
                            File.Move(attemptTrx, trx, overwrite: true);
                    }
                    catch (IOException e)
                    {
                        context.Notices.Say(context.Strings.Said("debugging.launch_failed", ("why", e.Message)), error: true);
                        exit = -1;
                    }
                    // The host has exited. Retire its adapter before a multi-framework run attaches to its next host.
                    EndDebugging();
                    completed?.Invoke(exit);
                }
                finally
                {
                    if (File.Exists(attemptTrx))
                        try { File.Delete(attemptTrx); } catch (IOException) { }
                    attempt.Dispose();
                }
            });
        }
    }

    /// <summary>Stops even an attempt whose process has not yet been posted to the UI thread.</summary>
    private bool StopTestRun()
    {
        if (!CancelTestRun(finish: true))
            return false;
        context.Panels.RefreshSide(HexSide.Debug);
        context.Notices.Say(context.Strings.Said("debugging.stopping"));
        return true;
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // gone already
        }
    }

    /// <summary>
    /// The first stop of a test host just attached to, gone on from when it's nowhere in the program's own code, the
    /// host's own break into the debugger rather than a breakpoint of a test. Whether it went on.
    /// </summary>
    private bool GoOnFromHostStop(DebugEvent happened)
    {
        if (!hostJustAttached)
            return false;

        hostJustAttached = false;
        if (happened.Text.StartsWith("exception", StringComparison.Ordinal) || happened.Frames.Any(OwnCode))
            return false;

        debugging?.Continue();
        return true;
    }
}
