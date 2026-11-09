using System.Text.Json.Nodes;

namespace Hex.Dawn;

internal sealed partial class HexDebugging
{
    private sealed record PythonTestDebugRun(string Directory, string Report, Action<JsonObject?> Finished);
    private PythonTestDebugRun? pythonTestDebugRun;

    internal void DebugPythonTests(string folder, string projectRoot, string python, IReadOnlyList<string> names,
        Action started, Action<JsonObject?> finished)
    {
        if (Debugging) { context.Notices.Say(context.Strings.Said("debugging.already_running")); return; }
        if (PythonAdapter() is null)
        {
            OfferPythonDebugger(folder, () => DebugPythonTests(folder, projectRoot, python, names, started, finished));
            return;
        }
        string directory = Path.Combine(Path.GetTempPath(), "hex-unittest-debug-" + Guid.NewGuid());
        string script = Path.Combine(directory, "runner.py"), report = Path.Combine(directory, "results.json");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(script, PythonUnitTests.RunnerFor(projectRoot));
        }
        catch (IOException error)
        {
            context.Notices.Say(context.Strings.Said("debugging.launch_failed", ("why", error.Message)), error: true);
            return;
        }
        pythonTestDebugRun = new(directory, report, finished);
        started();
        StartDebuggingPython(script, new PythonRunLine(script, projectRoot, python,
            [report, "debug", folder, .. names], Environment: PythonUnitTests.ImportEnvironment(folder, projectRoot)));
        restart = () => DebugPythonTests(folder, projectRoot, python, names, started, finished);
    }

    private void FinishPythonTestDebug(PythonTestDebugRun? run, bool cleanup = true)
    {
        if (run is null || !ReferenceEquals(pythonTestDebugRun, run)) return;
        pythonTestDebugRun = null;
        JsonObject? report = null;
        try
        {
            if (File.Exists(run.Report)) report = JsonNode.Parse(File.ReadAllText(run.Report))?.AsObject();
        }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException)
        {
            context.Notices.Note(error.Message, error: true);
        }
        run.Finished(report);
        if (cleanup) DeletePythonTestFiles(run);
    }

    private static void DeletePythonTestFiles(PythonTestDebugRun? run)
    {
        if (run is null) return;
        // Each attempt owns its own files, so Restart cannot reuse a previous report.
        try { Directory.Delete(run.Directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal void StopPythonTestDebug()
    {
        if (pythonTestDebugRun is not null) StopDebugging();
    }
}
