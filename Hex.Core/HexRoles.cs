namespace Hex.Core;

/// <summary>
/// The roles the one Hex executable is started in without a window: the dev guard of a restart, the harness hook, the shared
/// MCP server's proxy, the MCP bridge, HIDL's language server, the local model as an agent, and asking for its help or version.
/// None of them is an app to a person, so on a Mac none may become a foreground one (a Dock icon that appears and goes again
/// with every restart), see <see cref="IsWindowMode"/> and Program.Main.
/// </summary>
internal static class HexRoles
{
    /// <summary>The argument that has Hex say its version and leave, with no window.</summary>
    public const string VERSION = "--version";

    /// <summary>The argument of the local model run as an agent, see LocalAgentProgram.ARGUMENT.</summary>
    public const string LOCAL_AGENT = "--local-agent";

    /// <summary>The argument of the dev guard, see DevGuard.ARGUMENT, which is only compiled into a Debug build.</summary>
    public const string DEV_GUARD = "--dev-guard";

    /// <summary>
    /// Whether this command line is a Hex that opens a window (Dawn or Dusk, a self test or a check of layouts included), false
    /// for each of the roles that never does. Pure, it only reads the arguments, so Main can ask it before anything is touched.
    /// </summary>
    public static bool IsWindowMode(string[] args) => !IsBackground(args);

    /// <summary>Whether this command line is one of the roles that has no window.</summary>
    public static bool IsBackground(string[] args)
    {
        if (args.Length > 0 && args[0] == DEV_GUARD)
            return true;

        // The same tests Program.Main makes, in its order (--mcp with --window or --selftest is the editor, McpBridge says so)
        return args.Contains("--harness-hook")
            || McpProxy.IsProxyMode(args)
            || args.Any(arg => arg is "--help" or "-h" or "-?" or "/?" or "/h" or VERSION)
            || args.Contains("--lsp")
            || McpBridge.IsBridgeMode(args)
            || args.Contains(LOCAL_AGENT, StringComparer.Ordinal);
    }
}
