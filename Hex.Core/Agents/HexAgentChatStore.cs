using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// The chats of the Agents panel written down and read back, what they are, the session each agent is in (so it goes on
/// where it was), what they used and their rows. A Hex restarting into a build of itself it just made hands its chats to
/// the new one this way, so an agent working on Hex carries on in the Hex it built.
/// </summary>
internal static class HexAgentChatStore
{
    public static JsonObject Write(HexAgentChat chat)
    {
        var rows = new JsonArray();
        foreach (HexAgentEntry entry in chat.Entries)
            rows.Add((JsonNode)RowOf(entry));

        JsonObject written = Head(chat);
        written["rows"] = rows;
        return written;
    }

    /// <summary>
    /// A chat as it's written down without its rows, what it is and what it used, which a link sends on its own when
    /// only that changed.
    /// </summary>
    public static JsonObject Head(HexAgentChat chat, bool includeLinkOwnership = true)
    {
        var options = new JsonObject();
        foreach (var (key, value) in chat.Options)
            options[key] = value;

        return new JsonObject
        {
            ["id"] = chat.Id,
            ["agent"] = chat.Agent,
            ["parent"] = chat.Parent,
            ["depth"] = chat.Depth,
            ["name"] = chat.Name,
            ["title"] = chat.Title,
            ["session"] = chat.Session,
            ["linkDraft"] = chat.LinkDraft?.ToString("N"),
            ["model"] = chat.Model,
            ["options"] = options,
            ["input_tokens"] = chat.InputTokens,
            ["output_tokens"] = chat.OutputTokens,
            ["cost"] = chat.Cost,
            ["context"] = chat.ContextUsed,
            ["context_limit"] = chat.ContextLimit,
            ["cache_read"] = chat.CacheRead,
            ["stopped"] = chat.Stopped,
            ["settled"] = chat.Settled,
            ["key"] = chat.Key,
            ["dropped"] = chat.Dropped,
            ["plan"] = chat.Plan?.ToStored(),
            ["link_starter"] = includeLinkOwnership ? chat.LinkStarter?.ToJson() : null,
            ["link_device"] = includeLinkOwnership ? chat.LinkDevice : null,
            ["link_children"] = includeLinkOwnership && chat.LinkChildren.Count > 0
                ? new JsonArray([.. chat.LinkChildren.Select(child => (JsonNode)child.Address.ToJson())]) : null
        };
    }

    /// <summary>A row as it's written down.</summary>
    public static JsonObject RowOf(HexAgentEntry entry) => new()
    {
        ["checkpoints"] = entry.Checkpoints is { Count: > 0 } taken
            ? new JsonArray([.. taken.Select(each => (JsonNode)new JsonObject { ["top"] = each.Top, ["tree"] = each.Tree })])
            : null,
        ["kind"] = entry.Kind.ToString(),
        ["text"] = entry.Text,
        ["asked"] = entry.AskedText,
        ["at"] = entry.ContextLocation,
        ["tool_id"] = entry.ToolId,
        ["tool"] = entry.ToolName,
        ["input"] = entry.Input,
        ["output"] = entry.Output,
        ["state"] = entry.State.ToString(),
        ["progress"] = entry.Progress,
        ["steps"] = entry.Steps,
        ["seconds"] = entry.Seconds
    };

    /// <summary>A row as it was written down, null for something that isn't one.</summary>
    public static HexAgentEntry? EntryOf(JsonNode? node)
    {
        if (node is not JsonObject entry || !Enum.TryParse(entry["kind"]?.GetValue<string>(), out HexAgentEntryKind kind))
            return null;

        // How long it took, an end it had, or every redraw counted on from when it was read back
        long now = Environment.TickCount64;
        long took = (long)((entry["seconds"]?.GetValue<double>() ?? 0) * 1000);
        return new HexAgentEntry(kind, entry["text"]?.GetValue<string>() ?? string.Empty)
        {
            Started = now - took,
            Ended = now,
            AskedText = entry["asked"]?.GetValue<string>(),
            ContextLocation = entry["at"]?.GetValue<string>(),
            ToolId = entry["tool_id"]?.GetValue<string>(),
            ToolName = entry["tool"]?.GetValue<string>(),
            Input = entry["input"]?.GetValue<string>(),
            Output = entry["output"]?.GetValue<string>(),
            State = Enum.TryParse(entry["state"]?.GetValue<string>(), out HexAgentToolState state) ? state : HexAgentToolState.Done,
            Progress = entry["progress"]?.GetValue<string>(),
            Steps = entry["steps"]?.GetValue<int>() ?? 0,
            Checkpoints = ReadCheckpoints(entry["checkpoints"] as JsonArray)
        };
    }

    private static List<(string Top, string Tree)>? ReadCheckpoints(JsonArray? taken)
    {
        if (taken is not { Count: > 0 })
            return null;

        List<(string Top, string Tree)>? list = null;
        foreach (JsonNode? item in taken)
        {
            if (item is not JsonObject each)
                continue;
            string top = each["top"]?.GetValue<string>() ?? string.Empty;
            string tree = each["tree"]?.GetValue<string>() ?? string.Empty;
            if (top.Length > 0 && tree.Length > 0)
                (list ??= new List<(string, string)>(taken.Count)).Add((top, tree));
        }

        return list;
    }

    /// <summary>Reads a saved chat directly from UTF-8 JSON on disk via JsonDocument to avoid intermediate string and JsonNode DOM allocations.</summary>
    public static HexAgentChat? ReadFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using JsonDocument document = JsonDocument.Parse(stream);
        return Read(document.RootElement);
    }

    /// <summary>A chat as it was written, not running. Null for something that isn't one.</summary>
    public static HexAgentChat? Read(JsonElement said)
    {
        if (said.ValueKind != JsonValueKind.Object
            || !TryGetInt32(said, "id", out int id)
            || GetString(said, "agent") is not { } agent)
            return null;

        var chat = new HexAgentChat(id, agent)
        {
            Parent = TryGetInt32(said, "parent", out int parent) ? parent : null,
            Depth = TryGetInt32(said, "depth", out int depth) ? depth : 0,
            Name = GetString(said, "name"),
            Session = GetString(said, "session"),
            Model = GetString(said, "model"),
            InputTokens = TryGetInt64(said, "input_tokens", out long inputTokens) ? inputTokens : 0,
            OutputTokens = TryGetInt64(said, "output_tokens", out long outputTokens) ? outputTokens : 0,
            Cost = TryGetDouble(said, "cost", out double cost) ? cost : 0,
            ContextUsed = TryGetInt64(said, "context", out long contextUsed) ? contextUsed : 0,
            ContextLimit = TryGetInt64(said, "context_limit", out long contextLimit) ? contextLimit : 0,
            CacheRead = TryGetInt64(said, "cache_read", out long cacheRead) ? cacheRead : 0,
            Stopped = TryGetBool(said, "stopped", out bool stopped) && stopped
        };

        if (said.TryGetProperty("link_starter", out JsonElement starterProp) && starterProp.ValueKind == JsonValueKind.Object)
            chat.LinkStarter = HexLinkAgentStarter.Read(JsonNode.Parse(starterProp.GetRawText()) as JsonObject);
        chat.LinkDevice = GetString(said, "link_device") is { Length: 64 } startedBy ? startedBy : chat.LinkStarter?.Device;

        if (said.TryGetProperty("link_children", out JsonElement childrenProp) && childrenProp.ValueKind == JsonValueKind.Array)
        {
            int count = 0;
            foreach (JsonElement child in childrenProp.EnumerateArray())
            {
                if (++count > 128)
                    break;
                if (child.ValueKind == JsonValueKind.Object
                    && HexLinkAgentStarter.Read(JsonNode.Parse(child.GetRawText()) as JsonObject) is { } address)
                    chat.LinkChildren.Add(new HexLinkAgentChild(address));
            }
        }

        // Chats kept before there was settling were in the Finished group once read back, as they still are
        chat.RestoreSettled(!TryGetBool(said, "settled", out bool settled) || settled);

        if (GetString(said, "linkDraft") is { } savedDraft && Guid.TryParseExact(savedDraft, "N", out Guid draftId))
            chat.LinkDraft = draftId;

        if (said.TryGetProperty("options", out JsonElement optionsProp) && optionsProp.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty opt in optionsProp.EnumerateObject())
            {
                if (opt.Value.ValueKind == JsonValueKind.String && opt.Value.GetString() is { } chosen)
                    chat.Options[opt.Name] = chosen;
            }
        }

        List<HexAgentEntry> rows;
        if (said.TryGetProperty("rows", out JsonElement rowsProp) && rowsProp.ValueKind == JsonValueKind.Array)
        {
            rows = new List<HexAgentEntry>(rowsProp.GetArrayLength());
            foreach (JsonElement row in rowsProp.EnumerateArray())
            {
                if (EntryOf(row) is { } entry)
                    rows.Add(entry);
            }
        }
        else
        {
            rows = [];
        }

        // The same key, so the rows it sent to its file before are still its own, and how many of them there were
        if (GetString(said, "key") is { Length: > 0 } fileKey)
            chat.Key = fileKey;
        if (said.TryGetProperty("plan", out JsonElement planProp) && planProp.ValueKind == JsonValueKind.Object
            && JsonNode.Parse(planProp.GetRawText()) is JsonObject plan)
            chat.SetPlan(HexAgentPlan.FromJson(plan));
        chat.Restore(GetString(said, "title") ?? string.Empty, rows, TryGetInt32(said, "dropped", out int dropped) ? dropped : 0);
        return chat;
    }

    private static HexAgentEntry? EntryOf(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object
            || !Enum.TryParse(GetString(entry, "kind"), out HexAgentEntryKind kind))
            return null;

        long now = Environment.TickCount64;
        long took = (long)((TryGetDouble(entry, "seconds", out double seconds) ? seconds : 0) * 1000);

        List<(string Top, string Tree)>? checkpoints = null;
        if (entry.TryGetProperty("checkpoints", out JsonElement takenProp) && takenProp.ValueKind == JsonValueKind.Array)
        {
            int len = takenProp.GetArrayLength();
            if (len > 0)
            {
                foreach (JsonElement each in takenProp.EnumerateArray())
                {
                    if (each.ValueKind != JsonValueKind.Object)
                        continue;
                    string top = GetString(each, "top") ?? string.Empty;
                    string tree = GetString(each, "tree") ?? string.Empty;
                    if (top.Length > 0 && tree.Length > 0)
                        (checkpoints ??= new List<(string, string)>(len)).Add((top, tree));
                }
            }
        }

        return new HexAgentEntry(kind, GetString(entry, "text") ?? string.Empty)
        {
            Started = now - took,
            Ended = now,
            AskedText = GetString(entry, "asked"),
            ContextLocation = GetString(entry, "at"),
            ToolId = GetString(entry, "tool_id"),
            ToolName = GetString(entry, "tool"),
            Input = GetString(entry, "input"),
            Output = GetString(entry, "output"),
            State = Enum.TryParse(GetString(entry, "state"), out HexAgentToolState state) ? state : HexAgentToolState.Done,
            Progress = GetString(entry, "progress"),
            Steps = TryGetInt32(entry, "steps", out int steps) ? steps : 0,
            Checkpoints = checkpoints
        };
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out JsonElement prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;

    private static bool TryGetInt32(JsonElement obj, string name, out int value)
    {
        if (obj.TryGetProperty(name, out JsonElement prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out value))
            return true;
        value = 0;
        return false;
    }

    private static bool TryGetInt64(JsonElement obj, string name, out long value)
    {
        if (obj.TryGetProperty(name, out JsonElement prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out value))
            return true;
        value = 0;
        return false;
    }

    private static bool TryGetDouble(JsonElement obj, string name, out double value)
    {
        if (obj.TryGetProperty(name, out JsonElement prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetDouble(out value))
            return true;
        value = 0;
        return false;
    }

    private static bool TryGetBool(JsonElement obj, string name, out bool value)
    {
        if (obj.TryGetProperty(name, out JsonElement prop))
        {
            if (prop.ValueKind == JsonValueKind.True) { value = true; return true; }
            if (prop.ValueKind == JsonValueKind.False) { value = false; return true; }
        }
        value = false;
        return false;
    }

    /// <summary>A chat as it was written, not running. Null for something that isn't one.</summary>
    public static HexAgentChat? Read(JsonNode? node)
    {
        if (node is not JsonObject said || said["id"]?.GetValue<int>() is not { } id || said["agent"]?.GetValue<string>() is not { } agent)
            return null;

        var chat = new HexAgentChat(id, agent)
        {
            Parent = said["parent"]?.GetValue<int>(),
            Depth = said["depth"]?.GetValue<int>() ?? 0,
            Name = said["name"]?.GetValue<string>(),
            Session = said["session"]?.GetValue<string>(),
            Model = said["model"]?.GetValue<string>(),
            InputTokens = said["input_tokens"]?.GetValue<long>() ?? 0,
            OutputTokens = said["output_tokens"]?.GetValue<long>() ?? 0,
            Cost = said["cost"]?.GetValue<double>() ?? 0,
            ContextUsed = said["context"]?.GetValue<long>() ?? 0,
            ContextLimit = said["context_limit"]?.GetValue<long>() ?? 0,
            CacheRead = said["cache_read"]?.GetValue<long>() ?? 0,
            Stopped = said["stopped"]?.GetValue<bool>() ?? false
        };
        chat.LinkStarter = HexLinkAgentStarter.Read(said["link_starter"] as JsonObject);
        chat.LinkDevice = HexLinkData.String(said, "link_device", 64) is { Length: 64 } startedBy ? startedBy : chat.LinkStarter?.Device;
        foreach (JsonObject child in HexLinkData.Objects(said, "link_children", 128))
            if (HexLinkAgentStarter.Read(child) is { } address)
                chat.LinkChildren.Add(new HexLinkAgentChild(address));

        // Chats kept before there was settling were in the Finished group once read back, as they still are
        chat.RestoreSettled(said["settled"]?.GetValue<bool>() ?? true);

        if (said["linkDraft"] is JsonValue draft && draft.TryGetValue(out string? savedDraft) && Guid.TryParseExact(savedDraft, "N", out Guid draftId))
            chat.LinkDraft = draftId;
        if (said["options"] is JsonObject options)
        {
            foreach (var (key, value) in options)
            {
                if (value?.GetValue<string>() is { } chosen)
                    chat.Options[key] = chosen;
            }
        }

        JsonArray? rowArray = said["rows"] as JsonArray;
        var rows = rowArray is { Count: > 0 } ? new List<HexAgentEntry>(rowArray.Count) : [];
        if (rowArray is not null)
        {
            foreach (JsonNode? row in rowArray)
            {
                if (EntryOf(row) is { } entry)
                    rows.Add(entry);
            }
        }

        // The same key, so the rows it sent to its file before are still its own, and how many of them there were
        if (said["key"]?.GetValue<string>() is { Length: > 0 } fileKey)
            chat.Key = fileKey;
        if (said["plan"] is JsonObject plan)
            chat.SetPlan(HexAgentPlan.FromJson(plan));
        chat.Restore(said["title"]?.GetValue<string>() ?? string.Empty, rows, said["dropped"]?.GetValue<int>() ?? 0);
        return chat;
    }
}
