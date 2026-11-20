using System.Text.Json;
using System.Text.Json.Nodes;

using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Hex.Dawn;

// What an extension that's an agent system has, Claude Code or Codex or the next one. hex.agent says who it is to the
// agent harness (Hex.Core's HexAgentProfiles) and how its agent is run from the Agents panel, the rest is what setting
// its host up and running its agent takes, the JSON its agent speaks among it. The folders open, where Hex
// is, the user's home for a host whose config lives there, and a key of a JSON file set without HIDL having to read or
// write JSON (an MCP config is JSON for most of them). docs/agent-harness-plan.md has the why.
internal sealed partial class HexExtensions
{
    // How each agent system an extension said runs its agent, by the profile's id, for the Agents panel
    private readonly Dictionary<string, (HexExtension Extension, ObjectValue Run)> agentRuns = [];

    /// <summary>The agents that can be run from the Agents panel, the extension that said each and its run object.</summary>
    internal IReadOnlyDictionary<string, (HexExtension Extension, ObjectValue Run)> AgentRuns => agentRuns;

    /// <summary>Every run an extension said taken away, as it's switched off.</summary>
    internal void ForgetAgents(HexExtension extension)
    {
        HexAgentProfiles.Forget(extension.Id);
        foreach (string id in agentRuns.Where(pair => pair.Value.Extension == extension).Select(pair => pair.Key).ToList())
            agentRuns.Remove(id);
    }

    private (string, IRuntimeValue)[] Agents(HexExtension extension) =>
    [
        ("agent", Natives.Action("agent", args =>
        {
            ObjectValue said = args.Object(0);
            string id = Text(said, "id") ?? throw args.Wrong("an agent needs an id.");
            string[] clients = said.Properties.TryGetValue("clients", out IRuntimeValue? listed) && listed is ListValue list
                ? [.. list.Items.OfType<StringValue>().Select(each => each.Value)]
                : [id];
            HexAgentProfiles.Set(new HexAgentProfile(id, Text(said, "name") ?? extension.Name, clients, (int)(Number(said, "budget") ?? 0),
                Text(said, "instructions") ?? string.Empty, extension.Id, Text(said, "colour"), Text(said, "colour_light")));
            if (said.Properties.TryGetValue("run", out IRuntimeValue? run) && run is ObjectValue running)
                agentRuns[id] = (extension, running);
            else
                agentRuns.Remove(id);
        })),
        ("json_parse", Natives.Function("json_parse", args => HexJson.Parse(args.Text(0)) ?? Values.Null)),
        // What a program prints run with some arguments, for an agent system to say what it has on this machine (agy
        // models lists the models the account may use), so its extension needn't be brought up to date for every model
        // that comes out. Run off the editor's thread for at most 20 seconds, its output and its error output handed to
        // then on the editor's, null when it couldn't be run or didn't end
        ("program_output", Natives.Action("program_output", args =>
        {
            string program = args.Text(0);
            string[] arguments = args.Has(1) && args[1] is ListValue listed ? [.. listed.Items.Select(Values.Text)] : [];
            IRuntimeValue then = args[2];
            string? folder = args.Has(3) ? args.Text(3) : null;
            if (string.IsNullOrEmpty(folder)) folder = context.Workspace.Folders.FirstOrDefault();
            string? input = args.Has(4) ? args.Text(4) : null;
            context.Work.InBackground($"{extension.Name}: {program}", () => ProgramOutput(program, arguments, folder, input),
                said => Answered(extension, then, said is null ? Values.Null : new StringValue(said)), quiet: true);
        })),
        ("temp", new StringValue(Path.Combine(Path.GetTempPath(), "hex-agents"))),
        // What an agent is told in its system prompt about the harness, the same for every agent system
        ("harness_rules", new StringValue(HexAgentProfiles.HARNESS_RULES)),
        // What the agent of a chat is told about its Plan tab and the agents beside it, for its own system prompt and
        // never its subagents', which share its chat's connection
        ("plan_rules", new StringValue(HexAgentProfiles.PLAN_RULES)),
        // Whether a shell command reads or searches the workspace the long way, the Hex tool to use instead when it does,
        // null when it doesn't, for a system whose own approvals Hex answers (Codex's app-server)
        ("harness_check", Natives.Function("harness_check", args =>
            HexHarnessGuard.ForCommand(args.Text(0, string.Empty), [.. context.Workspace.Folders]) is { } instead ? new StringValue(instead) : Values.Null)),
        // Given the chat a run is for, the bridge says which it is, so what the agent does through Hex is known to be its
        // (the agents it starts are that chat's, and how deep it is)
        ("mcp_server", Natives.Function("mcp_server", args => Natives.Object(
            ("command", new StringValue(System.Environment.ProcessPath ?? Path.Combine(Hex.Core.HexHost.ProgramFolder, "Hex.exe"))),
            ("args", Natives.List(["--mcp-connect", .. context.Workspace.Folders.Take(1).SelectMany(folder => new[] { "--folder", folder }),
                .. args.Has(0) && args[0] is ObjectValue chat && chat.Properties.TryGetValue("id", out IRuntimeValue? id) && id is StringValue { Value.Length: > 0 } named
                    ? new[] { "--agent-chat", named.Value } : []]))))),
        ("agents", Natives.Function("agents", _ => new ListValue([.. HexAgentProfiles.All.Select(profile => (IRuntimeValue)new StringValue(profile.Name))]))),
        ("workspace", Natives.Function("workspace", _ => new ListValue([.. context.Workspace.Folders.Select(folder => (IRuntimeValue)new StringValue(folder))]))),
        ("executable", new StringValue(System.Environment.ProcessPath ?? Path.Combine(Hex.Core.HexHost.ProgramFolder, "Hex.exe"))),
        ("home", new StringValue(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile))),
        ("json_set", Natives.Function("json_set", args =>
        {
            string path = PathFor(extension, args.Text(0));
            string[] keys = args[1] is ListValue named ? [.. named.Items.Select(item => item is StringValue text ? text.Value : item.ToString() ?? string.Empty)] : [args.Text(1)];
            return JsonSet(path, keys, args.Has(2) ? args[2] : Values.Null) is { } why ? new StringValue(why) : Values.Null;
        })),
        // The whole lines a file has gained since a place in it, for a log an agent system keeps beside its output (Codex's
        // session file says its limits and compactions where its --json doesn't). { lines, end }, end where to go on from,
        // and a place below 0 gives no lines and the end, to read only what comes after
        ("read_lines", Natives.Function("read_lines", args =>
        {
            var (lines, end) = ReadLinesFrom(PathFor(extension, args.Text(0)), args.Has(1) && args[1] is NumberValue from ? (long)from.Value : 0);
            return Natives.Object(("lines", new ListValue([.. lines.Select(line => (IRuntimeValue)new StringValue(line))])), ("end", new NumberValue(end)));
        })),
        // A file under a folder by how its name ends, the newest when there are several, null when there's none
        ("find_file", Natives.Function("find_file", args => FindFile(PathFor(extension, args.Text(0)), args.Text(1)) is { } found ? new StringValue(found) : Values.Null)),
        // The MCP servers Claude Code would start for a folder by itself, the user's own (~/.claude.json, its own and the
        // folder's project's) and the folder's .mcp.json, by name, for a config that says them all and nothing else
        // (--strict-mcp-config), so plugins' servers aren't started once for every agent
        ("claude_servers", Natives.Function("claude_servers", args => Hex.Core.HexJson.ToHidl(ClaudeServers(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), args.Text(0, context.Workspace.Folders.FirstOrDefault() ?? string.Empty)))))
    ];

    /// <summary>
    /// The MCP servers Claude Code starts for a folder out of the user's own configuration: ~/.claude.json's mcpServers,
    /// the same under its projects entry for the folder, then the folder's .mcp.json, later ones over earlier ones by name.
    /// Hex's own server is left out, it's written in by the extension for the chat. Whatever won't read is skipped.
    /// </summary>
    internal static System.Text.Json.Nodes.JsonObject ClaudeServers(string home, string folder)
    {
        var servers = new System.Text.Json.Nodes.JsonObject();
        if (Read(Path.Combine(home, ".claude.json")) is System.Text.Json.Nodes.JsonObject user)
        {
            Take(user["mcpServers"]);
            // Its projects are keyed by the folder written with forward slashes, a drive letter in either case
            if (folder.Length > 0 && user["projects"] is System.Text.Json.Nodes.JsonObject projects)
            {
                string wanted = Path.GetFullPath(folder).Replace('\\', '/').TrimEnd('/');
                foreach (var (key, project) in projects)
                {
                    if (string.Equals(key.Replace('\\', '/').TrimEnd('/'), wanted, StringComparison.OrdinalIgnoreCase))
                        Take(project?["mcpServers"]);
                }
            }
        }
        if (folder.Length > 0 && Read(Path.Combine(folder, ".mcp.json")) is System.Text.Json.Nodes.JsonObject local)
            Take(local["mcpServers"]);
        servers.Remove("hex");
        return servers;

        void Take(System.Text.Json.Nodes.JsonNode? from)
        {
            if (from is not System.Text.Json.Nodes.JsonObject named)
                return;
            foreach (var (name, server) in named)
            {
                if (server is not null)
                    servers[name] = server.DeepClone();
            }
        }

        static System.Text.Json.Nodes.JsonNode? Read(string path)
        {
            try
            {
                return File.Exists(path) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                return null;
            }
        }
    }

    // How long program_output waits for a program to end, and the most of what it prints it keeps
    private const int PROGRAM_OUTPUT_MS = 20_000, PROGRAM_OUTPUT_MOST = 1 << 20;

    /// <summary>
    /// What a program printed run with arguments, found the way an agent's program is (PATH, the system's PATH as it is now,
    /// a .cmd through cmd). Optional input is written before closing stdin. Returns null if the process cannot start
    /// or times out.
    /// </summary>
    internal static string? ProgramOutput(string program, string[] arguments, string? folder = null, string? input = null)
    {
        if (HexAgentProcess.Find(program) is not { } found)
            return null;
        try
        {
            System.Diagnostics.ProcessStartInfo start = HexAgentProcess.StartInfo(found, arguments, folder ?? System.Environment.CurrentDirectory);
            start.RedirectStandardOutput = start.RedirectStandardError = start.RedirectStandardInput = true;
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null)
                return null;
            Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            Task writing = CloseProgramInput(process, input);
            if (!process.WaitForExit(PROGRAM_OUTPUT_MS))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            writing.GetAwaiter().GetResult();
            string said = output.Result + error.Result;
            return said.Length > PROGRAM_OUTPUT_MOST ? said[..PROGRAM_OUTPUT_MOST] : said;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or AggregateException)
        {
            return null;
        }
    }

    private static async Task CloseProgramInput(System.Diagnostics.Process process, string? input)
    {
        using StreamWriter writer = process.StandardInput;
        if (input is not null)
            await writer.WriteLineAsync(input).ConfigureAwait(false);
    }

    // The most a read_lines takes at once, a log that grew more than that is read on from where this stopped
    private const int READ_LINES_MOST = 4 << 20;

    /// <summary>
    /// The whole lines of a file from a byte offset, as far as the last line ending, and the offset after them. A file
    /// shorter than the offset was written again, so it's read from its start.
    /// </summary>
    internal static (List<string> Lines, long End) ReadLinesFrom(string path, long from)
    {
        var lines = new List<string>();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Below 0 is the end, for a reader that wants only what's written from now on
            if (from < 0)
                return (lines, stream.Length);
            if (from > stream.Length)
                from = 0;
            stream.Position = from;
            byte[] buffer = new byte[(int)Math.Min(READ_LINES_MOST, stream.Length - from)];
            int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (read == 0)
                return (lines, from);
            int last = Array.LastIndexOf(buffer, (byte)'\n', read - 1);
            if (last < 0)
                return (lines, from);
            foreach (string line in System.Text.Encoding.UTF8.GetString(buffer, 0, last + 1).Split('\n'))
            {
                if (line.TrimEnd('\r') is { Length: > 0 } whole)
                    lines.Add(whole);
            }

            return (lines, from + last + 1);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (lines, from);
        }
    }

    /// <summary>The newest file under a folder whose name ends as said, null when there's none or the folder isn't there.</summary>
    internal static string? FindFile(string folder, string ending)
    {
        if (ending.Length == 0 || !Directory.Exists(folder))
            return null;
        try
        {
            return new DirectoryInfo(folder).EnumerateFiles("*" + ending, SearchOption.AllDirectories)
                .OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault()?.FullName;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// A key of a JSON file set to a value, the objects on the way made when they aren't there and the rest of the file
    /// kept as it was (its spacing isn't, it's written indented). Null when it went, why it didn't as a text.
    /// </summary>
    internal static string? JsonSet(string path, IReadOnlyList<string> keys, IRuntimeValue value)
    {
        if (keys.Count == 0)
            return "say which key, a list of them from the top";

        JsonObject top;
        try
        {
            top = File.Exists(path) && File.ReadAllText(path) is { Length: > 0 } text
                ? JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? throw new JsonException("the file isn't a JSON object")
                : [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return $"{Path.GetFileName(path)} couldn't be read, {e.Message}";
        }

        JsonObject at = top;
        foreach (string key in keys.Take(keys.Count - 1))
        {
            if (at[key] is not JsonObject inside)
                at[key] = inside = [];
            at = inside;
        }

        at[keys[^1]] = HexJson.FromHidl(value);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, top.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"{Path.GetFileName(path)} couldn't be written, {e.Message}";
        }
    }
}
