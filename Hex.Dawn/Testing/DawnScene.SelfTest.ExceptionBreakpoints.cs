namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestExceptionBreakpoints(HexPointerScript test, bool python, bool cpp = false)
    {
        string root = Path.Combine(testFolder, cpp ? "cpp-exceptions" : python ? "python-exceptions" : "csharp-exceptions");
        string file = Path.Combine(root, cpp ? "main.cpp" : python ? "main.py" : "Program.cs");
        string filter = cpp ? "cpp_throw" : python ? "raised" : "all", kind = cpp ? "lldb-dap" : python ? DebugSession.PYTHON : DebugSession.CORECLR;
        string preference = kind + "/" + filter;
        bool? previous = null;
        int entry = cpp ? 3 : python ? 0 : 4, thrown = cpp ? 4 : python ? 2 : 5, beforeExit = 0;
        test.Run(() =>
        {
            previous = Context.Settings.Values.ExceptionBreakpoints.TryGetValue(preference, out bool value) ? value : null;
            Context.Settings.Values.ExceptionBreakpoints[preference] = false;
            Directory.CreateDirectory(root);
            if (!python && !cpp) File.WriteAllText(Path.Combine(root, "Probe.csproj"), CSHARP_PROJECT);
            File.WriteAllText(file, cpp
                ? "#include <cstdio>\n#include <stdexcept>\nint main() {\n    int value = 0;\n    try { throw std::runtime_error(\"caught fixture\"); }\n    catch (...) { value = 42; }\n    std::printf(\"%d\\n\", value);\n}\n"
                : python
                ? "value = 0\ntry:\n    raise ValueError('caught fixture')\nexcept ValueError:\n    value = 42\nprint(value)\n"
                : "using System;\nclass Program\n{\n    static void Main() {\n        int value = 0;\n        try { throw new InvalidOperationException(\"caught fixture\"); }\n        catch (InvalidOperationException) { value = 42; }\n        Console.WriteLine(value);\n    }\n}\n");
            explorer.OpenFolder(root);
            Open(file);
            debugger.ToggleBreakpoint(file, entry);
            RunOrPlay();
        });
        test.WaitFor(() => debugger.DebugStopped, 45f);
        test.Check("Exception filters: " + kind + " exposes its native thrown-exception filter", () => debugger.DebugStopped && debugger.ExceptionFilters.Any(item => item.Id == filter));
        test.Run(() => debugger.SetExceptionFilter(filter, true));
        test.WaitFor(() => debugger.BreaksOnException(filter) && Context.Settings.Values.ExceptionBreakpoints.GetValueOrDefault(preference), 10f);
        test.Check("Exception filters: selection reaches the adapter and is remembered", () => debugger.BreaksOnException(filter) && Context.Settings.Values.ExceptionBreakpoints.GetValueOrDefault(preference));
        test.Run(RunOrPlay);
        test.WaitFor(() => debugger.DebugStopped && code.ExecutionLine == thrown, 15f);
        test.Run(() =>
        {
            if (cpp && (!debugger.DebugStopped || code.ExecutionLine != thrown))
            {
                Console.WriteLine($"[Exception probe] active={debugger.Debugging} stopped={debugger.DebugStopped} line={code.ExecutionLine}");
                foreach (var frame in debugger.stoppedFrames) Console.WriteLine($"[Exception probe] {frame.Name}: {frame.File}:{frame.Line}");
                foreach (var entry in said.TakeLast(5)) Console.WriteLine("[Exception probe] " + entry.Text);
            }
        });
        test.Check("Exception filters: caught exception stops at its throw site", () => debugger.DebugStopped && code.ExecutionLine == thrown);
        test.Run(() => { beforeExit = said.Count; RunOrPlay(); });
        test.WaitFor(() => !debugger.Debugging, 15f);
        test.Check("Exception filters: continuing a caught exception reports a successful exit", () => !debugger.Debugging
            && said.Skip(beforeExit).Any(entry => entry.Text.Contains("exited with code 0", StringComparison.Ordinal))
            && !said.Skip(beforeExit).Any(entry => entry.Text.Contains("terminated after an exception", StringComparison.Ordinal)));
        test.Run(RunOrPlay);
        test.WaitFor(() => debugger.DebugStopped, 45f);
        test.Check("Exception filters: next launch restores the saved native filter", () => debugger.DebugStopped && debugger.BreaksOnException(filter));
        test.Run(() => debugger.SetExceptionFilter(filter, false));
        test.WaitFor(() => !debugger.BreaksOnException(filter), 10f);
        test.Run(RunOrPlay);
        test.WaitFor(() => !debugger.Debugging, 15f);
        test.Check("Exception filters: disabling the filter lets the caught exception run through", () => !debugger.Debugging);
        test.Run(() =>
        {
            if (debugger.Debugging) debugger.StopDebugging();
            debugger.breakpoints.Clear();
            if (previous is { } saved) Context.Settings.Values.ExceptionBreakpoints[preference] = saved;
            else Context.Settings.Values.ExceptionBreakpoints.Remove(preference);
            Context.Settings.Save();
        });
    }
}
