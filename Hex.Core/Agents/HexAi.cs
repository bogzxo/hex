namespace Hex.Core;

/// <summary>
/// Whether Hex has its agents at all this run. With No AI on (the no_ai setting, or <c>--no-ai</c> for the one run) the
/// agents' face, their panel, their chats, the extensions that are agent systems and the MCP tools that are about
/// agents are never made, rather than made and hidden, so none of it is there to start up or to tick. The rest of the
/// MCP server, the tools an agent outside Hex finds code and drives the editor with, stays as it is, it's the
/// mcp_server setting's to say. Said once as the editor starts and kept for the run, switching the setting takes a
/// restart, there's no taking the agents out of an editor that's built with them or putting them into one that isn't.
/// </summary>
public static class HexAi
{
    /// <summary>The command line's word for No AI, this run only whatever the settings say.</summary>
    public const string ARGUMENT = "--no-ai";

    private static bool decided;

    /// <summary>Whether No AI is on this run, the agents never made.</summary>
    public static bool Off { get; private set; }

    /// <summary>
    /// Said once, by whichever comes first, the command line as Hex starts or the settings as the editor is made. Said
    /// again it's what it was, a run doesn't change its mind halfway.
    /// </summary>
    public static void Decide(bool off)
    {
        if (decided)
            return;
        decided = true;
        Off = off;
    }

    /// <summary>On for this run whatever was said before, for the command line, which has the last word.</summary>
    public static void Force()
    {
        decided = true;
        Off = true;
    }
}
