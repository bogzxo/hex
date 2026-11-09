namespace Hex.Dawn;

internal sealed partial class HexDebugging
{
    private sealed class CTestDebugRun(Action<CTestResult?> finished)
    {
        internal readonly Action<CTestResult?> Finished = finished;
        internal readonly System.Diagnostics.Stopwatch Time = new();
        internal CTestCase? Test;
    }

    private CTestDebugRun? ctestDebugRun;

    private void FinishCTestDebug(CTestDebugRun? run, CTestResult? result = null)
    {
        if (run is null || !ReferenceEquals(ctestDebugRun, run)) return;
        ctestDebugRun = null;
        run.Finished(result);
    }

    internal void StopCTestDebug()
    {
        if (ctestDebugRun is not null) StopDebugging();
    }

    internal void DebugCTest(string root, string build, string configuration, string testName, Action started, Action<CTestResult?> finished)
    {
        if (Debugging) { context.Notices.Say(context.Strings.Said("debugging.already_running")); return; }
        string? cmake = CMakeProject.Tool("cmake"), ctest = CMakeProject.Tool("ctest"), adapter = LldbDap();
        if (cmake is null || ctest is null || adapter is null)
        {
            context.Notices.Say(context.Strings.Said("debugging.cmake_tools_required"), error: true);
            return;
        }
        SaveAllCode();
        CancelDebugBuild();
        var run = new CTestDebugRun(finished);
        ctestDebugRun = run;
        started();
        var attempt = new CancellationTokenSource();
        debugBuild = attempt;
        building = true;
        conditionFailures.Clear();
        debuggedProject = root;
        restart = () => DebugCTest(root, build, configuration, testName, started, finished);
        context.Panels.ShowOutput();
        context.Panels.ShowSide(HexSide.Debug);
        var breakpointsNow = BreakpointsNow();
        _ = Task.Run(async () =>
        {
            try
            {
                var built = await CMakeProject.Run(cmake, root, ["--build", build, "--config", configuration], attempt.Token);
                if (!ReportBuild(built.Output, build, built.ExitCode, testName, Path.Combine(root, "CMakeLists.txt"), attempt.Token))
                {
                    debugWork.Enqueue(() => FinishCTestDebug(run));
                    return;
                }
                // Re-discover after building: CTest resolves executable locations and generated tests then.
                var test = (await CTest.Discover(ctest, build, configuration, attempt.Token)).SingleOrDefault(test => test.Name == testName)
                    ?? throw new IOException(context.Strings.Said("debugging.ctest_missing"));
                if (test.DebugUnavailable is { } reason) throw new NotSupportedException(reason);
                if (test.Command.Length == 0 || !File.Exists(test.Command[0])) throw new IOException(context.Strings.Said("debugging.ctest_missing_program"));
                var session = DebugSession.Start(adapter, []) ?? throw new IOException(context.Strings.Said("debugging.debugger_did_not_start"));
                PostBuild(attempt.Token, () =>
                {
                    run.Test = test;
                    run.Time.Start();
                    if (!test.DebugResultSupported) context.Notices.Note(context.Strings.Said("debugging.ctest_result_requires_run"));
                });
                await StartUnder(session, testName, LLDB, async launched =>
                {
                    bool ok = await launched.Launch(test.Command[0], test.Command[1..], test.WorkingDirectory, breakpointsNow, LLDB, ProgramOutputFile(), test.Environment);
                    if (!ok) debugWork.Enqueue(() => FinishCTestDebug(run));
                    return ok;
                }, attempt.Token);
            }
            catch (OperationCanceledException) when (attempt.IsCancellationRequested)
            {
                debugWork.Enqueue(() => FinishCTestDebug(run));
            }
            catch (Exception error)
            {
                PostBuild(attempt.Token, () =>
                {
                    building = false;
                    context.Notices.Say(context.Strings.Said("debugging.launch_failed", ("why", error.Message)), error: true);
                });
                debugWork.Enqueue(() => FinishCTestDebug(run));
            }
            finally
            {
                debugWork.Enqueue(() => { if (ReferenceEquals(debugBuild, attempt)) debugBuild = null; attempt.Dispose(); });
            }
        });
    }
}
