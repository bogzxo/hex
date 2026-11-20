using System.Text;


namespace Hex.Dawn;

// What an agent debugging through Hex's MCP server reads and does (DawnScene.McpDebug): why it last stopped, what the
// program wrote since it was last asked, how it ended, and breakpoints put down or taken up by file and line without the
// file being on screen. Everything else is the debugger's own, F5, the steps and Shift+F5, the way somebody at it has them.
internal sealed partial class HexDebugging
{
    // The most of what the program wrote kept for an agent between its asking, the oldest dropped past it
    private const int AGENT_OUTPUT_MOST = 20_000;

    private readonly StringBuilder agentOutput = new();

    /// <summary>Why it last stopped, the reason and what was thrown, empty before it has.</summary>
    internal string AgentStopReason { get; private set; } = string.Empty;

    /// <summary>The exit code of the last program debugged to its end, null while one is going or none has ended.</summary>
    internal int? AgentExitCode { get; private set; }

    /// <summary>How many times it has stopped, so a step or a continue can wait for the next.</summary>
    internal int Stops => stops;

    /// <summary>The session, for the scopes, variables and evaluations an agent asks for. Null while nothing is debugged.</summary>
    internal DebugSession? Session => debugging;

    /// <summary>The frame looked at in <see cref="stoppedFrames"/>.</summary>
    internal int StoppedFrameIndex => stoppedFrame;

    /// <summary>The C# projects F5 would offer, every one of them, for an agent to name the one it wants.</summary>
    internal IReadOnlyList<string> DebuggableProjects() => ProjectsToDebug();

    /// <summary>What the program wrote since this was last asked, and forgotten.</summary>
    internal string TakeAgentOutput()
    {
        string said = agentOutput.ToString();
        agentOutput.Clear();
        return said;
    }

    /// <summary>What's said of the last program forgotten, as an agent starts the next.</summary>
    internal void ForgetAgentRun()
    {
        AgentStopReason = string.Empty;
        AgentExitCode = null;
        agentOutput.Clear();
    }

    private void NoteAgentOutput(string line)
    {
        agentOutput.Append(line).Append('\n');
        if (agentOutput.Length > AGENT_OUTPUT_MOST)
            agentOutput.Remove(0, agentOutput.Length - AGENT_OUTPUT_MOST);
    }

    /// <summary>
    /// A breakpoint put down on a line (from 0) with a condition when one's said, or taken up. The file's editors and the
    /// debugger are told the way F9 tells them.
    /// </summary>
    internal void SetBreakpoint(string full, int line, bool on, string? condition)
    {
        if (!on)
        {
            if (BreakpointsOf(full).Remove(line))
            {
                if (breakpointSettings.TryGetValue(full, out var settings))
                    settings.Remove(line);
                BreakpointsChanged(full);
            }
            return;
        }

        Settle(full, line, SettingsOf(full, line) with { Condition = string.IsNullOrWhiteSpace(condition) ? null : condition, Enabled = true });
    }

    /// <summary>Every breakpoint there is, its file, its line from 0 and what it has to it.</summary>
    internal IEnumerable<(string File, int Line, HexBreakpoint Settings)> AllBreakpoints() =>
        breakpoints.SelectMany(file => file.Value.Select(line => (file.Key, line, SettingsOf(file.Key, line))));
}
