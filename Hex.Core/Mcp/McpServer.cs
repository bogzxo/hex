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

    /// <summary>What a client is told as it connects by who it is, the instructions it was made with when there's nobody to ask.</summary>
    public Func<string?, string?>? InstructionsFor { get; init; }

    /// <summary>The resources it has, null for none, the agent harness's log of changes.</summary>
    public McpResources? Resources { get; init; }

    /// <summary>How a message goes to the client unasked, a notification, set by the session it's talking over.</summary>
    public Action<JsonNode>? Notify { get; set; }

    /// <summary>
    /// Who the client of the call going on is, for a tool that answers clients differently (the harness by their
    /// profile). Set around a call and seen by whatever runs inside it, on any thread it goes to.
    /// </summary>
    public static readonly AsyncLocal<string?> CallingClient = new();

    // The resources this client asked to be told about, what stops telling it when it goes
    private readonly ConcurrentDictionary<string, IDisposable> subscribed = new();

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
                "resources/list" when Resources is not null => McpProtocol.Result(id, Resources.List()),
                "resources/templates/list" when Resources is not null => McpProtocol.Result(id, new JsonObject { ["resourceTemplates"] = new JsonArray() }),
                "resources/read" when Resources is not null => ReadResource(id, parameters),
                "resources/subscribe" when Resources is not null => Subscribe(id, parameters, true),
                "resources/unsubscribe" when Resources is not null => Subscribe(id, parameters, false),
                _ => McpProtocol.Error(id, McpProtocol.METHOD_NOT_FOUND, $"Hex has no {method}, it does tools (tools/list, tools/call){(Resources is null ? string.Empty : ", resources")} and ping.")
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
            ["capabilities"] = Resources is null
                ? new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } }
                : new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false }, ["resources"] = new JsonObject { ["subscribe"] = true, ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = name, ["version"] = version }
        };

        if ((InstructionsFor is not null ? InstructionsFor(Client) : instructions) is { } said)
            answer["instructions"] = said;
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
        CallingClient.Value = Client;
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

    private JsonObject ReadResource(JsonNode id, JsonObject parameters)
    {
        string uri = parameters["uri"]?.ToString() ?? string.Empty;
        if (Resources!.Read(uri) is not { } text)
            return McpProtocol.Error(id, McpProtocol.INVALID_PARAMS, $"There's no resource {uri}, resources/list has them.");

        return McpProtocol.Result(id, new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject { ["uri"] = uri, ["mimeType"] = "text/plain", ["text"] = text })
        });
    }

    // Telling the client when a resource changes, from now on or no more
    private JsonObject Subscribe(JsonNode id, JsonObject parameters, bool on)
    {
        string uri = parameters["uri"]?.ToString() ?? string.Empty;
        if (on)
        {
            if (Resources!.Read(uri) is null)
                return McpProtocol.Error(id, McpProtocol.INVALID_PARAMS, $"There's no resource {uri}, resources/list has them.");

            if (!subscribed.ContainsKey(uri) && Resources.Subscribe(uri, () => Notify?.Invoke(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/resources/updated",
                ["params"] = new JsonObject { ["uri"] = uri }
            })) is { } watching && !subscribed.TryAdd(uri, watching))
                watching.Dispose();
        }
        else if (subscribed.TryRemove(uri, out IDisposable? watching))
        {
            watching.Dispose();
        }

        return McpProtocol.Result(id, new JsonObject());
    }

    /// <summary>Stops telling the client about anything, as it goes.</summary>
    public void Forget()
    {
        foreach (string uri in subscribed.Keys)
        {
            if (subscribed.TryRemove(uri, out IDisposable? watching))
                watching.Dispose();
        }
    }
}

/// <summary>
/// Resources a server has, read as text and watched. <see cref="Subscribe"/> hands back what stops the watching, null when
/// there's nothing of that name to watch.
/// </summary>
internal sealed class McpResources(IReadOnlyList<(string Uri, string Name, string Description)> listed, Func<string, string?> read, Func<string, Action, IDisposable?> subscribe)
{
    public JsonObject List() => new()
    {
        ["resources"] = new JsonArray([.. listed.Select(each => (JsonNode)new JsonObject
        {
            ["uri"] = each.Uri,
            ["name"] = each.Name,
            ["description"] = each.Description,
            ["mimeType"] = "text/plain"
        })])
    };

    public string? Read(string uri) => read(uri);

    public IDisposable? Subscribe(string uri, Action changed) => subscribe(uri, changed);
}
