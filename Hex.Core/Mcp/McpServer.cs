using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// One client's side of the Model Context Protocol, whatever it comes over. A message in, the answer out (null for a
/// notification, which nobody answers). It knows initialize and the version it settles on, ping, tools/list and
/// tools/call, and a call that's cancelled. The tools themselves are whoever's <see cref="IMcpTools"/>, Hex's editor.
/// A message is handled on whatever thread brings it, two calls at once are the tools' business.
/// </summary>
internal sealed class McpServer(IMcpTools tools, string name, string version, string? instructions = null)
{
    // The calls going on, by their request's id, for notifications/cancelled to stop
    private readonly ConcurrentDictionary<string, CancellationTokenSource> running = new();

    /// <summary>The version the handshake settled on, the newest Hex has until then.</summary>
    public string Version { get; private set; } = McpProtocol.Latest;

    /// <summary>Who the client said it was in its initialize, "claude-code 2.1.0", null until then.</summary>
    public string? Client { get; private set; }

    /// <summary>Whether the client has said initialized after the handshake.</summary>
    public bool Ready { get; private set; }

    /// <summary>Called once the handshake is done, with who the client is, for the status bar.</summary>
    public Action<string>? Initialized { get; set; }

    /// <summary>
    /// Answers a message, or a batch of them (an array, which 2025-03-26 allowed), null when there's nothing to say back.
    /// Something that isn't JSON-RPC at all gets JSON-RPC's error for it.
    /// </summary>
    public async Task<JsonNode?> Handle(JsonNode? message)
    {
        if (message is JsonArray batch)
        {
            if (batch.Count == 0)
                return McpProtocol.Error(null, McpProtocol.INVALID_REQUEST, "An empty batch.");

            var answers = new JsonArray();
            foreach (JsonNode? each in batch.ToArray())
            {
                if (await HandleOne(each) is { } answer)
                    // Select the DOM overload. Add<JsonObject> serializes a CLR value and needs reflection metadata.
                    answers.Add((JsonNode)answer);
            }

            return answers.Count > 0 ? answers : null;
        }

        return await HandleOne(message);
    }

    /// <summary>A line as it came over the wire, read and answered, JSON that won't parse being JSON-RPC's parse error.</summary>
    public async Task<JsonNode?> HandleLine(string line)
    {
        JsonNode? message;
        try
        {
            message = JsonNode.Parse(line);
        }
        catch (JsonException e)
        {
            return McpProtocol.Error(null, McpProtocol.PARSE_ERROR, $"That isn't JSON, {e.Message}");
        }

        return await Handle(message);
    }

    private async Task<JsonObject?> HandleOne(JsonNode? message)
    {
        if (message is not JsonObject received || received["jsonrpc"]?.GetValueKind() != JsonValueKind.String || received["jsonrpc"]!.GetValue<string>() != "2.0")
            return McpProtocol.Error(null, McpProtocol.INVALID_REQUEST, "Not a JSON-RPC 2.0 message.");

        JsonNode? id = received["id"];
        string? method = received["method"] is JsonValue named && named.GetValueKind() == JsonValueKind.String ? named.GetValue<string>() : null;
        JsonObject parameters = received["params"] as JsonObject ?? [];

        // An answer to something of ours, Hex asks clients nothing, so it's dropped
        if (method is null)
            return id is null ? McpProtocol.Error(null, McpProtocol.INVALID_REQUEST, "A message with no method.") : null;

        if (id is null)
        {
            Notified(method, parameters);
            return null;
        }

        try
        {
            return method switch
            {
                "initialize" => McpProtocol.Result(id, Initialize(parameters)),
                "ping" => McpProtocol.Result(id, new JsonObject()),
                "tools/list" => McpProtocol.Result(id, ListTools()),
                "tools/call" => await CallTool(id, parameters),
                _ => McpProtocol.Error(id, McpProtocol.METHOD_NOT_FOUND, $"Hex has no {method}, it does tools (tools/list, tools/call) and ping.")
            };
        }
        catch (Exception e)
        {
            return McpProtocol.Error(id, McpProtocol.INTERNAL_ERROR, e.Message);
        }
    }

    private void Notified(string method, JsonObject parameters)
    {
        switch (method)
        {
            case "notifications/initialized":
                Ready = true;
                break;

            case "notifications/cancelled":
                if (parameters["requestId"] is { } cancelled && running.TryGetValue(cancelled.ToJsonString(), out var stop))
                    stop.Cancel();
                break;
        }
    }

    private JsonObject Initialize(JsonObject parameters)
    {
        Version = McpProtocol.Negotiate(parameters["protocolVersion"] is JsonValue asked && asked.GetValueKind() == JsonValueKind.String ? asked.GetValue<string>() : null);

        if (parameters["clientInfo"] is JsonObject info)
        {
            string clientName = info["name"]?.ToString() ?? "a client";
            Client = info["version"] is { } clientVersion ? $"{clientName} {clientVersion}" : clientName;
        }

        Client ??= "a client";
        Initialized?.Invoke(Client);

        var answer = new JsonObject
        {
            ["protocolVersion"] = Version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = name, ["version"] = version }
        };

        if (instructions is not null)
            answer["instructions"] = instructions;
        return answer;
    }

    private JsonObject ListTools() =>
        new() { ["tools"] = new JsonArray([.. tools.Tools.Select(tool => (JsonNode)tool.Describe(Version))]) };

    /// <summary>
    /// A tool called. One Hex hasn't got is a protocol error, arguments that don't fit its schema come back as the tool
    /// failing, so the model sees what was wrong and tries again, which the spec asks for since 2025-11-25.
    /// </summary>
    private async Task<JsonObject> CallTool(JsonNode id, JsonObject parameters)
    {
        string? called = parameters["name"] is JsonValue named && named.GetValueKind() == JsonValueKind.String ? named.GetValue<string>() : null;
        if (called is null || tools.Tools.FirstOrDefault(tool => tool.Name == called) is not { } tool)
            return McpProtocol.Error(id, McpProtocol.INVALID_PARAMS, called is null ? "tools/call needs the name of a tool." : $"There's no tool called {called}, tools/list has them.");

        JsonObject arguments = parameters["arguments"] as JsonObject ?? [];
        if (McpSchema.Check(tool.InputSchema, arguments) is { } wrong)
            return McpProtocol.Result(id, McpToolResult.Error($"{called} can't take those arguments. {wrong}").ToJson());

        string key = id.ToJsonString();
        using var cancel = new CancellationTokenSource();
        running[key] = cancel;
        try
        {
            McpToolResult result = await tools.Call(called, (JsonObject)arguments.DeepClone(), cancel.Token);
            return McpProtocol.Result(id, result.ToJson());
        }
        catch (OperationCanceledException)
        {
            return McpProtocol.Result(id, McpToolResult.Error($"{called} was cancelled.").ToJson());
        }
        catch (Exception e)
        {
            return McpProtocol.Result(id, McpToolResult.Error($"{called} went wrong, {e.Message}").ToJson());
        }
        finally
        {
            running.TryRemove(key, out _);
        }
    }
}
