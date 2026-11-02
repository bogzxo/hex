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
                Text(said, "instructions") ?? string.Empty, extension.Id));
            if (said.Properties.TryGetValue("run", out IRuntimeValue? run) && run is ObjectValue running)
                agentRuns[id] = (extension, running);
            else
                agentRuns.Remove(id);
        })),
        ("json_parse", Natives.Function("json_parse", args => HexJson.Parse(args.Text(0)) ?? Values.Null)),
        ("temp", new StringValue(Path.Combine(Path.GetTempPath(), "hex-agents"))),
        ("mcp_server", Natives.Function("mcp_server", _ => Natives.Object(
            ("command", new StringValue(System.Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Hex.exe"))),
            ("args", Natives.List(["--mcp-connect", .. context.Workspace.Folders.Take(1).SelectMany(folder => new[] { "--folder", folder })]))))),
        ("agents", Natives.Function("agents", _ => new ListValue([.. HexAgentProfiles.All.Select(profile => (IRuntimeValue)new StringValue(profile.Name))]))),
        ("workspace", Natives.Function("workspace", _ => new ListValue([.. context.Workspace.Folders.Select(folder => (IRuntimeValue)new StringValue(folder))]))),
        ("executable", new StringValue(System.Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Hex.exe"))),
        ("home", new StringValue(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile))),
        ("json_set", Natives.Function("json_set", args =>
        {
            string path = PathFor(extension, args.Text(0));
            string[] keys = args[1] is ListValue named ? [.. named.Items.Select(item => item is StringValue text ? text.Value : item.ToString() ?? string.Empty)] : [args.Text(1)];
            return JsonSet(path, keys, args.Has(2) ? args[2] : Values.Null) is { } why ? new StringValue(why) : Values.Null;
        }))
    ];

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
