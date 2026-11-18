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
        ("temp", new StringValue(Path.Combine(Path.GetTempPath(), "hex-agents"))),
        // What an agent is told in its system prompt about the harness, the same for every agent system
        ("harness_rules", new StringValue(HexAgentProfiles.HARNESS_RULES)),
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
        ("find_file", Natives.Function("find_file", args => FindFile(PathFor(extension, args.Text(0)), args.Text(1)) is { } found ? new StringValue(found) : Values.Null))
    ];

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
