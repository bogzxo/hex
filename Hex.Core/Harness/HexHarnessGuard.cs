using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// The hook an agent system calls before it runs one of its own tools (Claude Code's PreToolUse), which keeps the agent
/// on Hex's harness. An agent told in its system prompt to find and read code through Hex drifts back to grep, find and
/// cat as a long session goes on and the prompt is far behind it, and each of those reads the workspace the long way. So
/// a search or a read of the workspace by the shell or by the agent's own Grep and Glob is turned back with the Hex tool
/// that does it, or, more gently, let through with that said beside it. Anything outside the workspace, a pipe's later
/// stages and every other command are left alone.
/// </summary>
internal static class HexHarnessGuard
{
    /// <summary>How hard the guard holds the agent to the harness.</summary>
    public enum Mode { Insist, Remind }

    // The commands that read or search files, by what Hex has in their place
    private static readonly Dictionary<string, string> INSTEAD = new(StringComparer.OrdinalIgnoreCase)
    {
        ["grep"] = "hex_search (text or a regex, ranked and grouped by file), hex_refs for where a name is used, hex_symbols for where it's declared",
        ["egrep"] = "hex_search with regex true",
        ["rg"] = "hex_search (text or a regex, ranked and grouped by file), hex_refs for where a name is used",
        ["findstr"] = "hex_search",
        ["select-string"] = "hex_search",
        ["sls"] = "hex_search",
        ["find"] = "hex_files (files by part of their name) or hex_map",
        ["ls"] = "hex_files or hex_map (the folders and what's in them)",
        ["dir"] = "hex_files or hex_map",
        ["tree"] = "hex_map",
        ["get-childitem"] = "hex_files or hex_map",
        ["gci"] = "hex_files or hex_map",
        ["cat"] = "hex_read (one declaration by its symbol, or a range of lines) or hex_skim (what files are)",
        ["type"] = "hex_read or hex_skim",
        ["get-content"] = "hex_read or hex_skim",
        ["gc"] = "hex_read or hex_skim",
        ["head"] = "hex_read with start_line and end_line",
        ["tail"] = "hex_read with start_line and end_line",
        ["less"] = "hex_read",
        ["more"] = "hex_read",
        ["wc"] = "hex_files (it says every file's line count)",
        ["sed"] = "hex_read with start_line and end_line"
    };

    /// <summary>
    /// What the hook answers for a tool call, the JSON Claude Code reads on its stdout, or null to say nothing and let
    /// it run. <paramref name="call"/> is what the hook was given, <paramref name="folders"/> the workspace's.
    /// </summary>
    public static string? Answer(string call, IReadOnlyList<string> folders, Mode mode)
    {
        JsonObject? given;
        try
        {
            given = JsonNode.Parse(call) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        string tool = given?["tool_name"]?.GetValue<string>() ?? string.Empty;
        JsonObject? input = given?["tool_input"] as JsonObject;
        string? instead = tool switch
        {
            "Grep" => Inside(Text(input, "path"), folders) ? INSTEAD["grep"] : null,
            "Glob" => Inside(Text(input, "path"), folders) ? INSTEAD["find"] : null,
            "Bash" or "PowerShell" => ForCommand(Text(input, "command") ?? string.Empty, folders),
            _ => null
        };
        if (instead is null)
            return null;

        string why = $"This reads the workspace the long way. Hex is connected as the hex MCP server and answers it from its index in a fraction of the tokens, use {instead}. The shell is still yours for builds, tests, git and anything outside the workspace.";
        var said = new JsonObject { ["hookEventName"] = "PreToolUse" };
        if (mode == Mode.Insist)
        {
            said["permissionDecision"] = "deny";
            said["permissionDecisionReason"] = why;
        }
        else
            said["additionalContext"] = why;
        return ((JsonNode)new JsonObject { ["hookSpecificOutput"] = said }).ToJsonString();
    }

    /// <summary>
    /// What Hex has in the place of a shell command, null for a command that isn't a read or a search of the workspace.
    /// Only the first command of a line counts, a grep that filters git's output reads no files.
    /// </summary>
    internal static string? ForCommand(string command, IReadOnlyList<string> folders)
    {
        string first = command.TrimStart();
        int end = first.IndexOfAny(['|', ';', '&', '\n', '>']);
        if (end >= 0)
            first = first[..end];
        string[] words = Words(first);
        if (words.Length == 0)
            return null;

        // cd somewhere first and then the command, the place counts
        string program = Path.GetFileNameWithoutExtension(words[0].Trim('"', '\'')).ToLowerInvariant();
        if (!INSTEAD.TryGetValue(program, out string? instead))
            return null;

        // sed that edits isn't a read, and a find, ls or cat of a place outside the workspace is the shell's business
        if (program == "sed" && !words.Contains("-n"))
            return null;
        if (words.Length == 1 && program is "cat" or "type" or "get-content" or "gc" or "less" or "more" or "wc" or "head" or "tail")
            return null;
        foreach (string word in words.Skip(1))
        {
            string path = word.Trim('"', '\'');
            if (LooksAbsolute(path) && !Inside(path, folders))
                return null;
        }

        return instead;
    }

    // A command's words, quotes kept together
    private static string[] Words(string line)
    {
        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        char quote = '\0';
        foreach (char c in line)
        {
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                else
                    word.Append(c);
            }
            else if (c is '"' or '\'')
                quote = c;
            else if (char.IsWhiteSpace(c))
            {
                if (word.Length > 0)
                    words.Add(word.ToString());
                word.Clear();
            }
            else
                word.Append(c);
        }

        if (word.Length > 0)
            words.Add(word.ToString());
        return [.. words];
    }

    private static bool LooksAbsolute(string path) =>
        path.StartsWith('~') || path.StartsWith('/') || path.StartsWith('\\') || path.StartsWith('$')
        || path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';

    /// <summary>Whether a path an agent gave is in the workspace, where it ran when it gave none or a relative one.</summary>
    internal static bool Inside(string? path, IReadOnlyList<string> folders)
    {
        if (string.IsNullOrWhiteSpace(path) || folders.Count == 0)
            return folders.Count > 0;
        if (path.StartsWith('~') || path.StartsWith('$'))
            return false;

        // A Git Bash path, /c/Users/..., is C:\Users\...
        string native = path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == '/'
            ? $"{path[1]}:{path[2..]}"
            : path;
        if (!Path.IsPathRooted(native))
            return true;

        string full;
        try
        {
            full = Path.GetFullPath(native).TrimEnd('\\', '/');
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        StringComparison compare = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (string folder in folders)
        {
            string root = Path.GetFullPath(folder).TrimEnd('\\', '/');
            if (full.Equals(root, compare) || full.StartsWith(root + Path.DirectorySeparatorChar, compare) || full.StartsWith(root + '/', compare))
                return true;
        }

        return false;
    }

    private static string? Text(JsonObject? input, string key) =>
        input?[key] is JsonValue value && value.TryGetValue(out string? text) ? text : null;

    /// <summary>
    /// The hook as a program, Hex started with --harness-hook by the agent system before one of its tools runs. The call
    /// comes on stdin, the answer goes on stdout, and anything that goes wrong lets the tool run, a broken guard must
    /// never stop an agent.
    /// </summary>
    public static int Run(string[] args)
    {
        try
        {
            var folders = new List<string>();
            Mode mode = Mode.Insist;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--folder")
                    folders.Add(args[i + 1]);
                else if (args[i] == "--guard" && args[i + 1] == "remind")
                    mode = Mode.Remind;
            }

            // With no Hex answering for the workspace (it crashed, or it's restarting) the harness isn't there to turn the
            // agent back to, and the shell is all it has
            using var input = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8);
            string call = input.ReadToEnd();
            List<McpListing> running = McpDiscovery.All();
            if (folders.Count > 0 && !folders.Any(folder => McpDiscovery.For(folder, running) is not null))
                return 0;
            if (Answer(call, folders, mode) is { } said)
            {
                using var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false));
                output.Write(said);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        return 0;
    }
}
