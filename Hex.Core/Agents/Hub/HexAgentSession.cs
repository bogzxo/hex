namespace Hex.Core;

/// <summary>How a session came to be, which decides who it answers to and how it's shown.</summary>
internal enum HexSessionKind
{
    /// <summary>A chat somebody started in the Agents panel or in Noon.</summary>
    Panel,

    /// <summary>A chat an agent started with hex_agent_spawn, under the one that started it.</summary>
    Spawned,

    /// <summary>A subagent of an agent system's own (Claude Code's Task), a row of its parent's chat.</summary>
    Native,

    /// <summary>An MCP client Hex didn't start, known by what it does through the tools.</summary>
    External
}

/// <summary>
/// One agent's session as the hub keeps it, the chat of what it said and did with what the hub knows about it around
/// that. Its id, parent and depth are its chat's, so the panel, Noon and the tools all speak of it by one number.
/// </summary>
internal sealed class HexAgentSession(HexAgentChat chat, HexSessionKind? kind = null)
{
    /// <summary>What it said and did, the transcript the rows are drawn from.</summary>
    public HexAgentChat Chat { get; } = chat;

    public int Id => Chat.Id;

    /// <summary>The chat that started it, null for one somebody started.</summary>
    public int? Parent => Chat.Parent;

    public int Depth => Chat.Depth;

    /// <summary>The agent system it runs on, its profile's id (claude_code, codex).</summary>
    public string System => Chat.Agent;

    /// <summary>How it came to be, a spawned one known by its having a parent when nobody said.</summary>
    public HexSessionKind Kind => kind ?? (Chat.Parent is null ? HexSessionKind.Panel : HexSessionKind.Spawned);
}
