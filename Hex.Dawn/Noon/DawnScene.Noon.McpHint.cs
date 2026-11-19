using Hex.Core;

namespace Hex.Dawn;

// Agentic come up with Hex's MCP server off in the settings. A chat run from here gets the server put up for it all the
// same, but an agent started anywhere else on the machine (Claude Code in a terminal, Codex in its own window) finds no
// Hex to ask, and greps and reads files whole where one call of the harness would have done. Said once a session in a
// card that goes by itself, with the way to the setting's row and Don't show again, kept in the settings.
internal sealed partial class DawnScene
{
    // Said this session already
    private bool mcpHintSaid;

    /// <summary>Whether the hint is said at all, when a test says so. Left alone a self test says nothing and a person's Hex does.</summary>
    internal bool? SayMcpHint { get; set; }

    /// <summary>How many times the hint was said, for the self test.</summary>
    internal int McpHintsSaid { get; private set; }

    /// <summary>What this session said forgotten, so a test can play the next session's start.</summary>
    internal void ForgetMcpHintSaid() => mcpHintSaid = false;

    /// <summary>
    /// Says the server is off and what switching it on saves, the first time Agentic comes up in a session. Nothing when
    /// it's on (by the setting or --mcp), where it couldn't be (no pipes), or once Don't show again was pressed.
    /// </summary>
    private void HintMcp()
    {
        if (!(SayMcpHint ?? !options.SelfTest) || mcpHintSaid || McpWanted || settings.McpHintQuiet || !HexHost.Pipes)
            return;

        mcpHintSaid = true;
        McpHintsSaid++;
        Notify(Said("mcp.hint"), error: false,
            (Said("mcp.hint_open"), () =>
            {
                OpenSettings(null);
                settingsView?.Reveal("mcp_server");
            }),
            (Said("code_server.dont_show_again"), () =>
            {
                settings.McpHintQuiet = true;
                SaveSettings();
            }));
    }
}
