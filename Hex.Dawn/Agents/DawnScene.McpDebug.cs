using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

// The debugger for an agent, what somebody at Hex has from F9, F5, the steps, the stack and the variables. A start, a
// step or a continue waits until the program stops again, ends or the wait is up, and says where it is then, the reason,
// the top of the stack and what the program wrote since it was last asked, so an agent needn't poll. Lines are from 1,
// frames counted from 0 at the top of the stack as hex_debug_state lists them.
internal sealed partial class DawnScene
{
    // How much of the stack a report has unless it's asked for more, and the most it'll have
    private const int MCP_DEBUG_FRAMES = 8, MCP_DEBUG_MOST_FRAMES = 60;

    // The most variables of a scope listed, the most characters of a value, and the deepest an object is opened
    private const int MCP_DEBUG_MOST_VARIABLES = 200, MCP_DEBUG_VALUE = 300, MCP_DEBUG_DEPTH = 3;

    private IEnumerable<HexMcpTool> DebugTools() =>
    [
        new(new McpTool("hex_debug_breakpoint", "Set or clear a breakpoint",
            "Put a breakpoint on a line of a file (the file needn't be open), with a condition if one is given, or take it up with remove. Says the file's breakpoints after.",
            McpSchema.Object([
                ("path", McpSchema.String("The file, absolute or relative to the first workspace folder.")),
                ("line", McpSchema.Integer("The line, 1 is the first.")),
                ("condition", McpSchema.String("Stop only when this expression is true, in the program's language.")),
                ("remove", McpSchema.Flag("Take the breakpoint up instead."))], "path", "line")),
            DebugBreakpoint),

        new(new McpTool("hex_debug_breakpoints", "List breakpoints",
            "List every breakpoint, file, line and condition.", McpSchema.Empty(), ReadOnly: true),
            DebugBreakpoints),

        new(new McpTool("hex_debug_start", "Start debugging",
            "Build and start a program under the debugger (F5) and wait until it stops at a breakpoint, ends, or wait_seconds pass, then say where it is. Without a project it is the one F5 would pick, an error lists them when there are several. Python, C++ and D files on screen are debugged as F5 does.",
            McpSchema.Object([
                ("project", McpSchema.String("The .csproj to debug, absolute or relative to the first workspace folder.")),
                ("wait_seconds", McpSchema.Number("How long to wait for it to stop or end, 120 by default (the build is in it)."))])),
            DebugStart, Timeout: 610.0f),

        new(new McpTool("hex_debug_step", "Step or continue",
            "While stopped: continue, or step over, in or out, then wait until it stops again or ends and say where it is. While running: pause.",
            McpSchema.Object([
                ("action", McpSchema.OneOf("What to do.", "continue", "over", "in", "out", "pause")),
                ("wait_seconds", McpSchema.Number("How long to wait for it to stop or end, 10 by default."))], "action")),
            DebugStep, Timeout: 610.0f),

        new(new McpTool("hex_debug_stop", "Stop debugging",
            "Stop what's being debugged (Shift+F5), the program ended, and say how it ended.", McpSchema.Empty()),
            DebugStop, Timeout: 20.0f),

        new(new McpTool("hex_debug_state", "Debugger state",
            "Whether something is being debugged, running or stopped, why it last stopped, the stack (frame 0 is the top, the one Hex shows marked), and what the program wrote since this was last asked.",
            McpSchema.Object([
                ("frames", McpSchema.Integer("How many frames of the stack, 8 by default."))]), ReadOnly: true),
            DebugState),

        new(new McpTool("hex_debug_variables", "Variables",
            "While stopped, the variables of a frame by scope (locals, arguments...), objects opened down to depth levels. Frame 0 is the top of the stack.",
            McpSchema.Object([
                ("frame", McpSchema.Integer("The frame, the one Hex shows by default.")),
                ("depth", McpSchema.Integer("How deep objects are opened, 1 (the default) to 3.")),
                ("filter", McpSchema.String("Only variables whose name has this in it, at the top level."))]), ReadOnly: true),
            DebugVariables, Timeout: 30.0f),

        new(new McpTool("hex_debug_screenshot", "Screenshot of the program",
            "Take a PNG of the debugged program's window (not Hex's, hex_screenshot is that), to see what it draws, running or stopped, even behind other windows. When the window won't draw itself the screen is copied where it is, and the result says so.",
            McpSchema.Object([
                ("scale", McpSchema.Number("Shrink it, 0.1 to 1, 1 by default.")),
                ("raise", McpSchema.Flag("When it won't draw itself (stopped at a breakpoint), bring its window to the front for a moment so what's copied is it and not what's over it. Hex is put back in front after."))]), ReadOnly: true),
            DebugScreenshot, Timeout: 20.0f),

        new(new McpTool("hex_debug_evaluate", "Evaluate",
            "While stopped, evaluate an expression in a frame the way the debug console does and say its value and type. It runs in the program, so a call in it can change things.",
            McpSchema.Object([
                ("expression", McpSchema.String("The expression, in the program's language.")),
                ("frame", McpSchema.Integer("The frame, the one Hex shows by default."))], "expression")),
            DebugEvaluate, Timeout: 30.0f)
    ];

    private IEnumerable<McpToolResult?> DebugBreakpoint(JsonObject arguments)
    {
        string full = McpPath(arguments.String("path") ?? string.Empty);
        int line = arguments.Integer("line") ?? 0;
        if (line < 1)
        {
            yield return McpToolResult.Error("The line is counted from 1.");
            yield break;
        }

        if (!File.Exists(full))
        {
            yield return McpToolResult.Error($"There's no file {full}.");
            yield break;
        }

        bool remove = arguments.Flag("remove");
        debugger.SetBreakpoint(full, line - 1, !remove, arguments.String("condition"));
        var said = new StringBuilder(remove ? $"Took up the breakpoint at {full}:{line}.\n" : $"Put a breakpoint at {full}:{line}.\n");
        McpListBreakpoints(said, debugger.AllBreakpoints().Where(each => PlatformPaths.Same(each.File, full)));
        yield return McpToolResult.Text(said.ToString());
    }

    private IEnumerable<McpToolResult?> DebugBreakpoints(JsonObject arguments)
    {
        var said = new StringBuilder();
        McpListBreakpoints(said, debugger.AllBreakpoints());
        yield return McpToolResult.Text(said.ToString());
    }

    private static void McpListBreakpoints(StringBuilder said, IEnumerable<(string File, int Line, HexBreakpoint Settings)> listed)
    {
        var all = listed.OrderBy(each => each.File, StringComparer.OrdinalIgnoreCase).ThenBy(each => each.Line).ToList();
        said.Append(all.Count == 0 ? "No breakpoints.\n" : $"{all.Count} breakpoint{(all.Count == 1 ? string.Empty : "s")}:\n");
        foreach (var (file, line, settings) in all)
        {
            said.Append($"{file}:{line + 1}");
            if (!settings.Enabled)
                said.Append(" (off)");
            if (settings.Condition is { } condition)
                said.Append($" when {condition}");
            if (settings.LogMessage is { } log)
                said.Append($" logs \"{log}\"");
            said.Append('\n');
        }
    }

    private IEnumerable<McpToolResult?> DebugStart(JsonObject arguments)
    {
        if (debugger.Debugging)
        {
            yield return McpToolResult.Error("Something is being debugged already. hex_debug_state says where it is, hex_debug_stop ends it.");
            yield break;
        }

        string? project = arguments.String("project") is { Length: > 0 } named ? McpPath(named) : null;
        if (project is not null && !File.Exists(project))
        {
            yield return McpToolResult.Error($"There's no project {project}.");
            yield break;
        }

        // F5 asks which of several with a window, an agent is told them instead
        if (project is null && debugger.DebuggableProjects() is { Count: > 1 } several)
        {
            yield return McpToolResult.Error("Say which project, there are several:\n" + string.Join('\n', several));
            yield break;
        }

        debugger.ForgetAgentRun();
        int stopsBefore = debugger.Stops;
        if (project is not null)
            debugger.DebugProject(project);
        else
            debugger.DebugOrContinue();

        // Nothing going a moment later is F5 having said why not, in the status bar
        var waited = Stopwatch.StartNew();
        while (!debugger.Debugging && waited.Elapsed.TotalSeconds < 2.0)
            yield return null;
        if (!debugger.Debugging)
        {
            yield return McpToolResult.Error($"It didn't start. Hex said: {Plain(messageItem.Text)}");
            yield break;
        }

        foreach (McpToolResult? waiting in McpDebugWait(stopsBefore, arguments.Number("wait_seconds") ?? 120.0))
            yield return waiting;
    }

    private IEnumerable<McpToolResult?> DebugStep(JsonObject arguments)
    {
        string action = arguments.String("action") ?? string.Empty;
        if (action == "pause")
        {
            if (!debugger.DebugRunning)
            {
                yield return McpToolResult.Error(debugger.DebugStopped ? "It's stopped already." : "Nothing is running under the debugger.");
                yield break;
            }
        }
        else if (!debugger.DebugStopped)
        {
            yield return McpToolResult.Error(debugger.Debugging ? "It's running, it can only be paused (action pause)." : "Nothing is being debugged, hex_debug_start starts it.");
            yield break;
        }

        int stopsBefore = debugger.Stops;
        switch (action)
        {
            case "continue": debugger.DebugOrContinue(); break;
            case "over": debugger.StepOver(); break;
            case "in": debugger.StepIn(); break;
            case "out": debugger.StepOut(); break;
            case "pause": debugger.ContinueOrPause(); break;
            default:
                yield return McpToolResult.Error($"{action} isn't an action, it's continue, over, in, out or pause.");
                yield break;
        }

        foreach (McpToolResult? waiting in McpDebugWait(stopsBefore, arguments.Number("wait_seconds") ?? 10.0))
            yield return waiting;
    }

    // Waits until it stops for a new reason, ends, or the time's up, and says where it is then
    private IEnumerable<McpToolResult?> McpDebugWait(int stopsBefore, double seconds)
    {
        var waited = Stopwatch.StartNew();
        double timeout = Math.Clamp(seconds, 0.1, 600.0);
        while (debugger.Debugging && !(debugger.DebugStopped && debugger.Stops > stopsBefore) && waited.Elapsed.TotalSeconds < timeout)
            yield return null;

        // The frames come in with the stop, a tick more for the editor to have shown where
        yield return null;
        yield return McpToolResult.Text(McpDebugReport(MCP_DEBUG_FRAMES, waited.Elapsed.TotalSeconds >= timeout));
    }

    private IEnumerable<McpToolResult?> DebugStop(JsonObject arguments)
    {
        if (!debugger.Debugging)
        {
            yield return McpToolResult.Text(McpDebugReport(0, timedOut: false));
            yield break;
        }

        debugger.StopDebugging();
        var waited = Stopwatch.StartNew();
        while (debugger.Debugging && waited.Elapsed.TotalSeconds < 10.0)
            yield return null;
        yield return McpToolResult.Text(McpDebugReport(0, timedOut: debugger.Debugging));
    }

    private IEnumerable<McpToolResult?> DebugState(JsonObject arguments)
    {
        yield return McpToolResult.Text(McpDebugReport(Math.Clamp(arguments.Integer("frames") ?? MCP_DEBUG_FRAMES, 0, MCP_DEBUG_MOST_FRAMES), timedOut: false));
    }

    // Where the debugger is, said for an agent: running, stopped and why with the stack, or ended and how
    private string McpDebugReport(int frames, bool timedOut)
    {
        var said = new StringBuilder();
        if (debugger.DebugStopped)
        {
            said.Append($"Stopped: {debugger.AgentStopReason}.\n");
            var stack = debugger.stoppedFrames;
            for (int i = 0; i < Math.Min(frames, stack.Count); i++)
            {
                var frame = stack[i];
                string where = frame.File is { } file ? $"{file}:{frame.Line + 1}" : "(no source)";
                said.Append($"{(i == debugger.StoppedFrameIndex ? "> " : "  ")}{i}  {frame.Name}  {where}\n");
            }
            if (stack.Count > frames && frames > 0)
                said.Append($"  ... {stack.Count - frames} more frames\n");
        }
        else if (debugger.Debugging)
        {
            said.Append(debugger.DebugRunning
                ? timedOut ? "Running, it hasn't stopped yet. hex_debug_step with pause stops it, hex_debug_state looks again.\n" : "Running.\n"
                : "Starting (building or launching).\n");
        }
        else
        {
            said.Append(debugger.AgentExitCode is { } code ? $"Not debugging. The program ended with exit code {code}.\n" : "Not debugging.\n");
        }

        string output = debugger.TakeAgentOutput();
        if (output.Length > 0)
            said.Append("Output since last asked:\n").Append(output);
        return said.ToString();
    }

    // The frame an agent named, or the one Hex shows, null when it isn't stopped or there's no such frame
    private (int Index, Horizon.UI.Scripting.DebugFrame Frame)? McpDebugFrame(JsonObject arguments)
    {
        var stack = debugger.stoppedFrames;
        int index = arguments.Integer("frame") ?? debugger.StoppedFrameIndex;
        return debugger.DebugStopped && debugger.Session is not null && index >= 0 && index < stack.Count ? (index, stack[index]) : null;
    }

    private IEnumerable<McpToolResult?> DebugVariables(JsonObject arguments)
    {
        if (McpDebugFrame(arguments) is not { } picked || debugger.Session is not { } session)
        {
            yield return McpToolResult.Error(debugger.DebugStopped ? "There's no such frame, hex_debug_state lists them." : "It isn't stopped, variables are read while it is.");
            yield break;
        }

        int depth = Math.Clamp(arguments.Integer("depth") ?? 1, 1, MCP_DEBUG_DEPTH);
        string? filter = arguments.String("filter");
        Task<string> reading = McpReadVariables(session, picked.Frame, depth, filter);
        while (!reading.IsCompleted)
            yield return null;

        yield return reading.IsCompletedSuccessfully
            ? McpToolResult.Text($"Frame {picked.Index}, {picked.Frame.Name}:\n{reading.Result}")
            : McpToolResult.Error($"The debugger didn't say, {reading.Exception?.GetBaseException().Message}");
    }

    private static async Task<string> McpReadVariables(Horizon.UI.Scripting.DebugSession session, Horizon.UI.Scripting.DebugFrame frame, int depth, string? filter)
    {
        var said = new StringBuilder();
        foreach (var scope in await session.Scopes(frame.Id))
        {
            said.Append(scope.Name).Append(":\n");
            var variables = await session.Variables(scope.Reference);
            int listed = 0;
            foreach (var variable in variables)
            {
                if (filter is { Length: > 0 } && !variable.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (listed++ >= MCP_DEBUG_MOST_VARIABLES)
                {
                    said.Append($"  ... {variables.Count - MCP_DEBUG_MOST_VARIABLES} more\n");
                    break;
                }

                await McpVariable(session, said, variable, 1, depth);
            }
        }

        return said.Length > 0 ? said.ToString() : "No variables.\n";
    }

    private static async Task McpVariable(Horizon.UI.Scripting.DebugSession session, StringBuilder said, Horizon.UI.Scripting.DebugVariable variable, int level, int depth)
    {
        string value = variable.Value.Length > MCP_DEBUG_VALUE ? variable.Value[..MCP_DEBUG_VALUE] + "..." : variable.Value;
        said.Append(' ', level * 2).Append(variable.Name);
        if (variable.Type is { Length: > 0 } type)
            said.Append(" (").Append(type).Append(')');
        said.Append(" = ").Append(value.ReplaceLineEndings(" ")).Append('\n');

        if (variable.Reference <= 0 || level >= depth)
            return;

        var children = await session.Variables(variable.Reference);
        foreach (var child in children.Take(MCP_DEBUG_MOST_VARIABLES))
            await McpVariable(session, said, child, level + 1, depth);
        if (children.Count > MCP_DEBUG_MOST_VARIABLES)
            said.Append(' ', (level + 1) * 2).Append($"... {children.Count - MCP_DEBUG_MOST_VARIABLES} more\n");
    }

    private IEnumerable<McpToolResult?> DebugScreenshot(JsonObject arguments)
    {
        if (debugger.Session?.ProcessId is not { } program)
        {
            yield return McpToolResult.Error(debugger.Debugging ? "The debugger hasn't said which process the program is yet, try again in a moment." : "Nothing is being debugged.");
            yield break;
        }

        // The window asked to draw itself first, off the editor's thread so one that doesn't answer can't hang Hex. Windows
        // usually answers for a program stopped at a breakpoint from the last frame it has, when it doesn't in a few
        // seconds the screen is copied where the window is instead
        float scale = (float)Math.Clamp(arguments.Number("scale") ?? 1.0, 0.1, 1.0);
        Task<(byte[]? Png, string Said)> drawn = Task.Run(() => McpWindowPicture(program, fromScreen: false, scale));
        var waited = Stopwatch.StartNew();
        while (!drawn.IsCompleted && waited.Elapsed.TotalSeconds < 3.0)
            yield return null;

        Task<(byte[]? Png, string Said)> taking = drawn;
        if (!drawn.IsCompleted)
        {
            // Asked to, the window comes to the front and the compositor gets a moment to show it before it's copied
            nint front = arguments.Flag("raise") ? HexWindowCapture.Raise(program) : 0;
            var raised = Stopwatch.StartNew();
            while (front != 0 && raised.Elapsed.TotalSeconds < 0.4)
                yield return null;

            taking = Task.Run(() => McpWindowPicture(program, fromScreen: true, scale, raised: front != 0));
            while (!taking.IsCompleted && waited.Elapsed.TotalSeconds < 10.0)
                yield return null;
            HexWindowCapture.Restore(front);
        }

        if (!taking.IsCompletedSuccessfully)
        {
            yield return McpToolResult.Error(taking.IsCompleted ? $"It couldn't be taken, {taking.Exception?.GetBaseException().Message}" : "The window couldn't be taken in 10 seconds.");
            yield break;
        }

        yield return taking.Result.Png is { } picture ? McpToolResult.Image(picture, taking.Result.Said) : McpToolResult.Error(taking.Result.Said);
    }

    // A PNG of the program's window shrunk by the scale, and what to say of it
    private static (byte[]? Png, string Said) McpWindowPicture(int program, bool fromScreen, float scale, bool raised = false)
    {
        var (pixels, said) = HexWindowCapture.Take(program, fromScreen);
        if (pixels is not { } taken)
            return (null, said);

        var (png, width, height) = HexPictures.Cut(taken, 0, 0, taken.Width, taken.Height, scale);
        string shrunk = scale < 1.0f ? $" shown at {width}x{height}" : string.Empty;
        string copied = !fromScreen ? string.Empty
            : raised ? ", copied off the screen with its window brought to the front, as it didn't draw itself"
            : ", copied off the screen as it didn't draw itself, so whatever is over its window is in it too (raise brings it to the front first)";
        return (png, $"The window of the debugged program (process {program}), {said}, {taken.Width}x{taken.Height}{shrunk}{copied}.");
    }

    private IEnumerable<McpToolResult?> DebugEvaluate(JsonObject arguments)
    {
        string expression = arguments.String("expression") ?? string.Empty;
        if (McpDebugFrame(arguments) is not { } picked || debugger.Session is not { } session)
        {
            yield return McpToolResult.Error(debugger.DebugStopped ? "There's no such frame, hex_debug_state lists them." : "It isn't stopped, expressions are evaluated while it is.");
            yield break;
        }

        var evaluating = session.EvaluateSaying(expression, picked.Frame.Id, "repl");
        while (!evaluating.IsCompleted)
            yield return null;

        if (!evaluating.IsCompletedSuccessfully)
        {
            yield return McpToolResult.Error($"The debugger didn't say, {evaluating.Exception?.GetBaseException().Message}");
            yield break;
        }

        var result = evaluating.Result;
        yield return result.Value is { } value
            ? McpToolResult.Text($"{expression} = {value.Value}{(value.Type is { Length: > 0 } type ? $" ({type})" : string.Empty)}")
            : McpToolResult.Error($"{expression}: {result.Error ?? "no value"}");
    }
}
