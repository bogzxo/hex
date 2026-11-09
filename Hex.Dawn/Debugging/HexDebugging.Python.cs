using Hex.Core.Notebooks;

namespace Hex.Dawn;

internal sealed partial class HexDebugging
{
    internal static bool IsPython(string? path) => path is not null && path.EndsWith(".py", StringComparison.OrdinalIgnoreCase);
    internal static bool SupportsDebugging(string? path) => !IsDebuggerSourcePath(path) && (IsCSharp(path) || IsD(path) || IsPython(path) || IsNativeSource(path));

    internal static string? PythonAdapter()
    {
        string? configured = Environment.GetEnvironmentVariable("HEX_DEBUGPY_PYTHON");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        return PythonDebuggerSetup.Ready(PythonDebuggerSetup.Folder) ? PythonEnvironment.ExecutableIn(PythonDebuggerSetup.Folder) : null;
    }

    internal void DebugPythonRun(PythonRunLine run, Action<Action> first)
    {
        if (Debugging) context.Notices.Say(context.Strings.Said("debugging.already_running"));
        else first(() => StartDebuggingPython(run.File, run));
    }

    private void StartDebuggingPython(string file, PythonRunLine? run = null)
    {
        if (PythonAdapter() is not { } adapter)
        {
            OfferPythonDebugger(file, () => StartDebuggingPython(file, run));
            return;
        }
        SaveAllCode();
        file = Path.GetFullPath(file);
        string folder = run?.Folder ?? Path.GetDirectoryName(file)!;
        string root = context.Workspace.Roots.Where(root => PlatformPaths.IsUnder(folder, root)).OrderByDescending(root => root.Length).FirstOrDefault() ?? folder;
        string interpreter = run?.Interpreter ?? PythonEnvironment.FindLocal(folder, root) ?? KernelSpecs.Pythons().FirstOrDefault() ?? adapter;
        CancelDebugBuild();
        var attempt = new CancellationTokenSource();
        debugBuild = attempt;
        building = true;
        conditionFailures.Clear();
        debuggedProject = file;
        restart = () => StartDebuggingPython(file, run);
        context.Panels.ShowOutput();
        context.Panels.ShowSide(HexSide.Debug);
        var breakpointsNow = BreakpointsNow();
        var tests = pythonTestDebugRun;
        _ = Task.Run(async () =>
        {
            try
            {
                var session = DebugSession.Start(adapter, ["-m", "debugpy.adapter"])
                    ?? throw new IOException(context.Strings.Said("debugging.debugger_did_not_start"));
                await StartUnder(session, run?.Module ?? Path.GetFileName(file), DebugSession.PYTHON, async launched =>
                {
                    bool ok = await launched.Launch(file, run?.Arguments ?? [], folder, breakpointsNow, DebugSession.PYTHON, environment: run?.Environment, interpreter: interpreter, module: run?.Module);
                    if (!ok) debugWork.Enqueue(() => FinishPythonTestDebug(tests));
                    return ok;
                }, attempt.Token);
            }
            catch (Exception error)
            {
                PostBuild(attempt.Token, () =>
                {
                    building = false;
                    context.Notices.Say(context.Strings.Said("debugging.launch_failed", ("why", error.Message)), error: true);
                });
                debugWork.Enqueue(() => FinishPythonTestDebug(tests));
            }
            finally
            {
                debugWork.Enqueue(() =>
                {
                    if (ReferenceEquals(debugBuild, attempt)) debugBuild = null;
                    attempt.Dispose();
                });
            }
        });
    }
}
