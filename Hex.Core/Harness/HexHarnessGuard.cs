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
    /// <summary>
    /// How hard the guard holds the agent to the harness. Strict turns back the agent's own Read, Edit and Write of the
    /// workspace as well (hex_read, hex_edit and hex_write do them), Insist only its searches and the shell's reads,
    /// Remind lets everything through with the Hex tool said beside it.
    /// </summary>
    public enum Mode { Strict, Insist, Remind, Off }

    // The agent's own file tools, by the Hex tool in their place, turned back only when the guard is strict
    private static readonly Dictionary<string, string> FILE_TOOLS = new(StringComparer.Ordinal)
    {
        ["Read"] = "hex_read (one declaration by its symbol, or start_line and end_line) or hex_skim (what files are)",
        ["Edit"] = "hex_edit (old_string and new_string, no reading the file first)",
        ["MultiEdit"] = "hex_edit, once for each change",
        ["Write"] = "hex_write (a whole file), or hex_edit for part of one",
        ["NotebookEdit"] = "hex_notebook_edit"
    };

    /// <summary>The mode a --guard argument names, strict for anything it doesn't know.</summary>
    public static Mode ModeOf(string? said) => said switch
    {
        "remind" => Mode.Remind,
        "insist" => Mode.Insist,
        "off" => Mode.Off,
        _ => Mode.Strict
    };

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
        if (mode == Mode.Off)
            return null;
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
            _ when mode == Mode.Strict && FILE_TOOLS.TryGetValue(tool, out string? hex)
                && Inside(Text(input, "file_path") ?? Text(input, "notebook_path") ?? Text(input, "path"), folders) => hex,
            _ => null
        };
        if (instead is null)
            return null;

        string why = FILE_TOOLS.ContainsKey(tool)
            ? $"In this workspace files are read and changed through Hex, the hex MCP server, which keeps the editor, its undo and the index in step. Use {instead}. Your own file tools are for what's outside the workspace."
            : $"This reads the workspace the long way. Hex is connected as the hex MCP server and answers it from its index in a fraction of the tokens, use {instead}. The shell is still yours for builds, tests, git and anything outside the workspace.";
        var said = new JsonObject { ["hookEventName"] = "PreToolUse" };
        if (mode != Mode.Remind)
        {
            said["permissionDecision"] = "deny";
            said["permissionDecisionReason"] = why;
        }
        else
            said["additionalContext"] = why;
        return ((JsonNode)new JsonObject { ["hookSpecificOutput"] = said }).ToJsonString();
    }

    /// <summary>
    /// What Hex has in the place of a shell command line, null for a line that reads and searches nothing of the
    /// workspace. Every command of it counts (cd somewhere && grep, a; b), each by the first stage of its pipe, since a
    /// grep that filters another program's output reads no files. A cd out of the workspace makes what comes after it
    /// the shell's business, a cd back in makes it the harness's again.
    /// </summary>
    internal static string? ForCommand(string command, IReadOnlyList<string> folders)
    {
        bool outside = false;
        foreach (string statement in Statements(command))
        {
            string first = statement;
            int end = first.IndexOfAny(['|', '>']);
            if (end >= 0)
                first = first[..end];
            string[] words = Words(first);
            // A PowerShell variable given what a command says, $files = Get-ChildItem ..., is that command
            if (words.Length > 2 && words[0].StartsWith('$') && words[1] == "=")
                words = words[2..];
            if (words.Length == 0)
                continue;

            string program = Path.GetFileNameWithoutExtension(words[0].Trim('"', '\'')).ToLowerInvariant();
            if (program is "cd" or "pushd" or "set-location" or "sl")
            {
                string to = words.Length > 1 ? words[^1].Trim('"', '\'') : "~";
                if (LooksAbsolute(to))
                    outside = !Inside(to, folders);
                continue;
            }

            if (!outside && ForOne(program, words, folders) is { } instead)
                return instead;
        }

        return null;
    }

    // A line cut at &&, || and ; and new lines, outside quotes, into the commands it runs one after another
    private static List<string> Statements(string line)
    {
        var statements = new List<string>();
        var statement = new System.Text.StringBuilder();
        char quote = '\0';
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                statement.Append(c);
                continue;
            }

            bool twice = i + 1 < line.Length && line[i + 1] == c;
            if (c is '"' or '\'')
                quote = c;
            else if (c is ';' or '\n' || (c is '&' or '|' && twice))
            {
                statements.Add(statement.ToString());
                statement.Clear();
                if (c is '&' or '|')
                    i++;
                continue;
            }

            statement.Append(c);
        }

        statements.Add(statement.ToString());
        return statements;
    }

    // What Hex has in the place of one command, by its program and its words
    private static string? ForOne(string program, string[] words, IReadOnlyList<string> folders)
    {
        // git's own search and list of files read the workspace as grep and find do
        if (program == "git")
        {
            // Past git's own options to its command, a -C into a folder outside the workspace being the shell's business
            string? sub = null;
            for (int i = 1; i < words.Length && sub is null; i++)
            {
                if (words[i] is "-C" or "-c" or "--git-dir" or "--work-tree" && i + 1 < words.Length)
                {
                    string place = words[++i].Trim('"', '\'');
                    if (words[i - 1] == "-C" && Outside(place, folders))
                        return null;
                }
                else if (!words[i].StartsWith('-'))
                    sub = words[i];
            }

            return sub switch
            {
                "grep" => INSTEAD["grep"],
                "ls-files" => INSTEAD["find"],
                _ => null
            };
        }

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
            if (Outside(path, folders))
                return null;
        }

        return instead;
    }

    // A path that's out of the workspace, whole or going up out of it from its first folder (..\horizon), which the
    // shell is for. Anything else, a pattern or a path inside, isn't
    private static bool Outside(string path, IReadOnlyList<string> folders)
    {
        if (LooksAbsolute(path))
            return !Inside(path, folders);
        if (folders.Count == 0 || !(path.StartsWith("..", StringComparison.Ordinal)))
            return false;
        try
        {
            return !Inside(Path.GetFullPath(Path.Combine(folders[0], path)), folders);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
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

    internal static string? MissingPrivateFile(string call, string root)
    {
        if (JsonNode.Parse(call) is not JsonObject given ||
            Text(given, "tool_name") is not ("Read" or "Edit" or "MultiEdit" or "Write" or "NotebookEdit") ||
            given["tool_input"] is not JsonObject input ||
            (Text(input, "file_path") ?? Text(input, "notebook_path") ?? Text(input, "path")) is not { Length: > 0 } asked)
            return null;

        string full = Path.GetFullPath(asked, root);
        if (!PlatformPaths.IsUnder(full, root) || PlatformPaths.Same(full, root) || File.Exists(full))
            return null;
        return Path.GetRelativePath(root, full).Replace('\\', '/');
    }

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
            Mode mode = Mode.Strict;
            string? privateRoot = null, chat = null;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--folder")
                    folders.Add(args[i + 1]);
                else if (args[i] == "--guard")
                    mode = ModeOf(args[i + 1]);
                else if (args[i] == "--private-root")
                    privateRoot = args[i + 1];
                else if (args[i] == "--agent-chat")
                    chat = args[i + 1];
            }

            // With no Hex answering for the workspace (it crashed, or it's restarting) the harness isn't there to turn the
            // agent back to, and the shell is all it has
            using var input = new StreamReader(Console.OpenStandardInput(), System.Text.Encoding.UTF8);
            string call = input.ReadToEnd();
            List<McpListing> running = McpDiscovery.All();
            if (folders.Count > 0 && !folders.Any(folder => McpDiscovery.For(folder, running) is not null))
                return 0;
            string? answer = Answer(call, folders, mode);
            if (answer is null && privateRoot is not null && chat is not null &&
                MissingPrivateFile(call, privateRoot) is { } path)
            {
                var editor = folders.Select(folder => McpDiscovery.For(folder, running)).FirstOrDefault(found => found is not null);
                if (editor is null || !McpBridge.MaterializePrivateFile(editor, chat, path).GetAwaiter().GetResult())
                {
                    answer = new JsonObject
                    {
                        ["hookSpecificOutput"] = new JsonObject
                        {
                            ["hookEventName"] = "PreToolUse",
                            ["permissionDecision"] = "deny",
                            ["permissionDecisionReason"] = "Hex could not open this private repository. Use hex_read or hex_edit for the file to see why."
                        }
                    }.ToJsonString();
                }
            }
            if (answer is { } said)
            {
                using var output = new StreamWriter(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false));
                output.Write(said);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or JsonException or OperationCanceledException)
        {
        }

        return 0;
    }
}
