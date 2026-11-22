using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

// A heavy stdio MCP server shared by the agents of a worktree. An agent system's extension writes what
// hex.shared_server(chat, name, command, args) gives into its agent's MCP config in place of the server's own command,
// and every agent of that worktree talks to one process of it in this Hex (Hex.Core's McpProxy and McpSharedServers)
// rather than each starting its own: the dotnet kit's Roslyn navigator is a gigabyte and half a minute of CPU apiece
internal sealed partial class HexExtensions
{
    /// <summary>
    /// hex.shared_server(chat, name, command, args[, folder]): the { command, args } that starts the proxy for the chat's
    /// worktree (the folder said, else the workspace's first), told which chat and which Hex it's for. Null when the
    /// command isn't on the PATH, there's nothing to share then.
    /// </summary>
    private IRuntimeValue SharedServer(Arguments args)
    {
        string name = args.Text(1);
        string command = args.Text(2);
        string[] arguments = args.Has(3) && args[3] is ListValue listed ? [.. listed.Items.Select(Values.Text)] : [];
        string? folder = args.Has(4) ? args.Text(4) : context.Workspace.Folders.FirstOrDefault();
        string? chat = args.Has(0) && args[0] is ObjectValue said && said.Properties.TryGetValue("id", out IRuntimeValue? id) && id is StringValue { Value.Length: > 0 } named
            ? named.Value : null;
        if (name.Length == 0 || command.Length == 0 || string.IsNullOrEmpty(folder) || Hex.Core.HexAgentProcess.Find(command) is null)
            return Values.Null;

        return McpLaunchCommand(SharedServerArguments(name, folder, chat, System.Environment.ProcessId, command, arguments));
    }

    /// <summary>What the proxy is started with, its own arguments and after -- the server's command line.</summary>
    internal static string[] SharedServerArguments(string name, string folder, string? chat, int hex, string command, IReadOnlyList<string> arguments) =>
    [
        Hex.Core.McpProxy.ARGUMENT, "--name", name, "--folder", folder,
        .. chat is null ? Array.Empty<string>() : ["--agent-chat", chat],
        "--hex", hex.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--", command, .. arguments
    ];

    /// <summary>
    /// Whether ~/.claude/settings.json has a plugin switched on, by its name before the @ (dotnet-claude-kit for
    /// dotnet-claude-kit@its-marketplace). False when it isn't there or the file won't read.
    /// </summary>
    internal static bool ClaudePlugin(string home, string plugin)
    {
        try
        {
            string path = Path.Combine(home, ".claude", "settings.json");
            if (!File.Exists(path) || System.Text.Json.Nodes.JsonNode.Parse(ReadExtensionFile(path)) is not System.Text.Json.Nodes.JsonObject settings ||
                settings["enabledPlugins"] is not System.Text.Json.Nodes.JsonObject enabled)
                return false;
            return enabled.Any(pair => (pair.Key == plugin || pair.Key.StartsWith(plugin + "@", StringComparison.Ordinal))
                && pair.Value is System.Text.Json.Nodes.JsonValue on && on.TryGetValue(out bool yes) && yes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
