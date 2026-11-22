using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// Which of Hex's tools a linked device may call, and the right each takes (docs/hex-link-plan.md, 3.4). It's an allow
/// list: a tool not named here is never reachable over a link whatever the device was granted, so a tool added to Hex
/// later stays this machine's own until somebody decides it's safe to share and adds it. The terminal, tasks, clicking,
/// typing, screenshots, the debugger, settings, Hex's own builds and the self test are left out on purpose.
/// </summary>
internal static class HexLinkAllowed
{
    /// <summary>The argument a tool call is told its calling chat by, which a peer is never let say.</summary>
    public const string CALLER = "_hex_caller";

    internal sealed record HeldRead(string Note, string? Text, bool Absent);
    internal static readonly AsyncLocal<Func<string, HeldRead?>?> RemoteHeldRead = new();
    internal static readonly AsyncLocal<Func<string, bool>?> RemoteFileAllowed = new();
    internal static readonly AsyncLocal<HexLinkDevice?> RelayOrigin = new();
    private static readonly ConditionalWeakTable<JsonObject, HexLinkAgentCall> agentCalls = new();

    internal static HexLinkAgentCall? AgentCall(JsonObject arguments) => agentCalls.TryGetValue(arguments, out var call) ? call : null;
    internal static void SetAgentCall(JsonObject arguments, HexLinkAgentCall call) => agentCalls.Add(arguments, call);
    private static readonly ConditionalWeakTable<JsonObject, Func<string, bool>> editorFileRules = new();

    internal static void SetEditorFileRule(JsonObject arguments, Func<string, bool> allowed) => editorFileRules.Add(arguments, allowed);
    internal static Func<string, bool>? EditorFileRule(JsonObject arguments) => editorFileRules.TryGetValue(arguments, out var allowed) ? allowed : null;

    internal static bool IsRelayTool(string name) => name is "hex_link_devices" or "hex_link_call" or "hex_link_spawn" or "hex_link_sync";

    private static readonly FrozenDictionary<string, HexLinkRights> needs = new Dictionary<string, HexLinkRights>(StringComparer.Ordinal)
    {
        // Looking: the fleet, chats, plans, the workspace's files and search, git's state
        ["hex_agent_list"] = HexLinkRights.View,
        ["hex_agent_read"] = HexLinkRights.View,
        ["hex_plan_read"] = HexLinkRights.View,
        ["hex_map"] = HexLinkRights.View,
        ["hex_files"] = HexLinkRights.View,
        ["hex_symbols"] = HexLinkRights.View,
        ["hex_search"] = HexLinkRights.View,
        ["hex_refs"] = HexLinkRights.View,
        ["hex_read"] = HexLinkRights.View,
        ["hex_skim"] = HexLinkRights.View,
        ["hex_changes"] = HexLinkRights.View,
        ["hex_projects"] = HexLinkRights.View,
        ["hex_git_diff"] = HexLinkRights.View,
        // Waiting on the agents a remote caller started here, and reading what they sent it
        ["hex_agent_wait"] = HexLinkRights.View,
        ["hex_agent_inbox"] = HexLinkRights.View,

        // Steering agents, as much as sitting at the machine: an agent told to run a command runs it
        ["hex_agent_send"] = HexLinkRights.Steer,
        ["hex_agent_message"] = HexLinkRights.Steer,
        ["hex_agent_stop"] = HexLinkRights.Steer,
        ["hex_agent_restart"] = HexLinkRights.Steer,
        ["hex_agent_spawn"] = HexLinkRights.Steer,
        ["hex_agent_approve"] = HexLinkRights.Steer,
        ["hex_agent_close"] = HexLinkRights.Steer,
        ["hex_agent_rename"] = HexLinkRights.Steer,
        ["hex_link_devices"] = HexLinkRights.Steer,
        ["hex_link_call"] = HexLinkRights.Steer,
        ["hex_link_spawn"] = HexLinkRights.Steer,
        ["hex_link_sync"] = HexLinkRights.Steer,

        // Writing the workspace's files, which runs nothing
        ["hex_edit"] = HexLinkRights.Edit,
        ["hex_write"] = HexLinkRights.Edit,
        ["hex_edit_symbol"] = HexLinkRights.Edit,
        ["hex_rename"] = HexLinkRights.Edit,
        ["hex_link_worktree_gate"] = HexLinkRights.Edit
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Every tool a link may ever reach, by name.</summary>
    public static IEnumerable<string> Names => needs.Keys;

    /// <summary>The right a tool takes over a link, null for one that's never let through.</summary>
    public static HexLinkRights? Needs(string tool) => needs.TryGetValue(tool, out HexLinkRights right) ? right : null;

    /// <summary>
    /// Whether a device with these rights may call a tool. Steer and Edit each take View as well, a device that may
    /// change things but not see them is a mistake in somebody's ticks, not a thing to allow.
    /// </summary>
    public static bool May(HexLinkRights rights, string tool) => Needs(tool) is { } right && Has(rights, right);

    /// <summary>Whether rights cover what a method or tool takes, View always with it.</summary>
    public static bool Has(HexLinkRights rights, HexLinkRights right) =>
        right == HexLinkRights.None || (rights.HasFlag(HexLinkRights.View) && rights.HasFlag(right));

    /// <summary>The tools of <paramref name="all"/> a device with these rights may call, in the order they came.</summary>
    public static IEnumerable<McpTool> Tools(HexLinkRights rights, IEnumerable<McpTool> all) => all.Where(tool => May(rights, tool.Name));

    /// <summary>
    /// A call's arguments as a peer sent them, with what's only Hex's to say taken out: the calling chat, which would let
    /// a remote caller pass itself off as one of this machine's agents.
    /// </summary>
    public static JsonObject Stripped(JsonObject arguments)
    {
        arguments.Remove(CALLER);
        return arguments;
    }
}
