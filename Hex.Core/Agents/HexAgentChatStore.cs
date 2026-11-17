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
        var options = new JsonObject();
        foreach (var (key, value) in chat.Options)
            options[key] = value;

        var rows = new JsonArray();
        foreach (HexAgentEntry entry in chat.Entries)
        {
            rows.Add((JsonNode)new JsonObject
            {
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
            });
        }

        return new JsonObject
        {
            ["id"] = chat.Id,
            ["agent"] = chat.Agent,
            ["parent"] = chat.Parent,
            ["depth"] = chat.Depth,
            ["name"] = chat.Name,
            ["title"] = chat.Title,
            ["session"] = chat.Session,
            ["model"] = chat.Model,
            ["options"] = options,
            ["input_tokens"] = chat.InputTokens,
            ["output_tokens"] = chat.OutputTokens,
            ["cost"] = chat.Cost,
            ["context"] = chat.ContextUsed,
            ["context_limit"] = chat.ContextLimit,
            ["cache_read"] = chat.CacheRead,
            ["stopped"] = chat.Stopped,
            ["rows"] = rows
        };
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

        if (said["options"] is JsonObject options)
        {
            foreach (var (key, value) in options)
            {
                if (value?.GetValue<string>() is { } chosen)
                    chat.Options[key] = chosen;
            }
        }

        var rows = new List<HexAgentEntry>();
        foreach (JsonNode? row in said["rows"] as JsonArray ?? [])
        {
            if (row is not JsonObject entry || !Enum.TryParse(entry["kind"]?.GetValue<string>(), out HexAgentEntryKind kind))
                continue;

            // How long it took, an end it had, or every redraw counted on from when it was read back
            long now = Environment.TickCount64;
            long took = (long)((entry["seconds"]?.GetValue<double>() ?? 0) * 1000);
            rows.Add(new HexAgentEntry(kind, entry["text"]?.GetValue<string>() ?? string.Empty)
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
                Steps = entry["steps"]?.GetValue<int>() ?? 0
            });
        }

        chat.Restore(said["title"]?.GetValue<string>() ?? string.Empty, rows);
        return chat;
    }
}
