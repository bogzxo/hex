using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>What a tool call came to: its text (the MCP result's text content joined) and whether it's an error.</summary>
internal sealed record AgentToolResult(string Text, bool IsError);

/// <summary>
/// A small MCP client over a stream pair (JSON-RPC 2.0, one message a line, as Hex's MCP server and its bridge speak):
/// initialize, tools/list, tools/call. One call at a time is enough for the agent; it's safe to call from one task.
/// </summary>
internal sealed class McpClient : IAsyncDisposable
{
    private const string PROTOCOL = "2025-11-25";

    private readonly Stream _from, _to;
    private readonly StreamReader _reader;
    private readonly SemaphoreSlim _one = new(1, 1);
    private int _id;

    /// <summary>A client reading the server's messages from <paramref name="fromServer"/> and writing to <paramref name="toServer"/>.</summary>
    public McpClient(Stream fromServer, Stream toServer)
    {
        _from = fromServer;
        _to = toServer;
        _reader = new StreamReader(fromServer, new UTF8Encoding(false), false, 4096, leaveOpen: true);
    }

    /// <summary>The handshake: initialize with the client's name, then the initialized notification.</summary>
    public async Task InitializeAsync(string clientName, CancellationToken cancel)
    {
        await _one.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            JsonNode response = await RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = PROTOCOL,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = clientName, ["version"] = "1.0" }
            }, cancel).ConfigureAwait(false);
            if (response["error"] is { } error)
                throw new IOException("MCP initialize failed: " + (error["message"]?.ToString() ?? error.ToJsonString()));
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, cancel).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Every tool the server has, its input schema as JSON text.</summary>
    public async Task<IReadOnlyList<ToolSpec>> ListToolsAsync(CancellationToken cancel)
    {
        await _one.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var tools = new List<ToolSpec>();
            string? cursor = null;
            do
            {
                var parameters = new JsonObject();
                if (cursor is not null)
                    parameters["cursor"] = cursor;
                JsonNode response = await RequestAsync("tools/list", parameters, cancel).ConfigureAwait(false);
                if (response["error"] is { } error)
                    throw new IOException("MCP tools/list failed: " + (error["message"]?.ToString() ?? error.ToJsonString()));
                JsonNode? result = response["result"];
                if (result?["tools"] is JsonArray array)
                {
                    foreach (JsonNode? tool in array)
                    {
                        string? name = tool?["name"]?.GetValue<string>();
                        if (string.IsNullOrEmpty(name))
                            continue;
                        string description = tool?["description"] is JsonValue d && d.TryGetValue(out string? text) ? text ?? "" : "";
                        string schema = tool?["inputSchema"] is { } s ? s.ToJsonString() : "{\"type\":\"object\"}";
                        tools.Add(new ToolSpec(name, description, schema));
                    }
                }

                cursor = result?["nextCursor"] is JsonValue c && c.TryGetValue(out string? next) && !string.IsNullOrEmpty(next) ? next : null;
            }
            while (cursor is not null);

            return tools;
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>Calls a tool with its arguments (a JSON object as text).</summary>
    public async Task<AgentToolResult> CallAsync(string name, string argumentsJson, CancellationToken cancel)
    {
        JsonNode arguments;
        try
        {
            arguments = string.IsNullOrWhiteSpace(argumentsJson) ? new JsonObject() : JsonNode.Parse(argumentsJson) ?? new JsonObject();
        }
        catch (System.Text.Json.JsonException e)
        {
            return new AgentToolResult("The arguments aren't valid JSON: " + e.Message, true);
        }

        if (arguments is not JsonObject)
            return new AgentToolResult("The arguments must be a JSON object.", true);

        await _one.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            JsonNode response = await RequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = arguments }, cancel).ConfigureAwait(false);
            if (response["error"] is { } error)
                return new AgentToolResult(error["message"]?.ToString() ?? error.ToJsonString(), true);

            JsonNode? result = response["result"];
            var text = new StringBuilder();
            bool first = true;
            if (result?["content"] is JsonArray content)
            {
                foreach (JsonNode? part in content)
                {
                    if (part?["type"]?.ToString() != "text")
                        continue;
                    if (!first)
                        text.Append('\n');
                    first = false;
                    text.Append(part["text"]?.ToString() ?? "");
                }
            }

            bool isError = result?["isError"] is JsonValue v && v.TryGetValue(out bool flag) && flag;
            return new AgentToolResult(text.ToString(), isError);
        }
        finally
        {
            _one.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        try { _reader.Dispose(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        try { _from.Dispose(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        try { _to.Dispose(); } catch (Exception e) when (e is IOException or ObjectDisposedException) { }
        _one.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task SendAsync(JsonNode message, CancellationToken cancel)
    {
        byte[] line = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        await _to.WriteAsync(line, cancel).ConfigureAwait(false);
        await _to.FlushAsync(cancel).ConfigureAwait(false);
    }

    // Sends a request and reads until its response; notifications and the server's own requests met on the way are
    // passed over (a ping is answered)
    private async Task<JsonNode> RequestAsync(string method, JsonObject parameters, CancellationToken cancel)
    {
        int id = ++_id;
        await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }, cancel).ConfigureAwait(false);
        while (true)
        {
            string? line = await _reader.ReadLineAsync(cancel).ConfigureAwait(false);
            if (line is null)
                throw new IOException("Hex's MCP server hung up.");
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonNode? message;
            try
            {
                message = JsonNode.Parse(line);
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            if (message is not JsonObject obj)
                continue;

            if (obj["method"] is not null)
            {
                // A request of the server's (it has an id) or a notification (none)
                if (obj["id"] is { } theirs)
                {
                    JsonNode reply = obj["method"]!.ToString() == "ping"
                        ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = theirs.DeepClone(), ["result"] = new JsonObject() }
                        : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = theirs.DeepClone(), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" } };
                    await SendAsync(reply, cancel).ConfigureAwait(false);
                }

                continue;
            }

            if (obj["id"] is JsonValue got && ((got.TryGetValue(out int n) && n == id) || got.ToString() == id.ToString()))
                return obj;
        }
    }
}

/// <summary>Which of Hex's tools the local agent gets, and their output cut to its budget.</summary>
internal static class LocalAgentTools
{
    private static readonly string[] Order =
    [
        "hex_read", "hex_search", "hex_symbols", "hex_refs", "hex_files", "hex_skim", "hex_map",
        "hex_edit", "hex_write", "hex_terminal_run", "hex_problems", "hex_plan_update"
    ];

    private static readonly HashSet<string> EditTools = new(StringComparer.Ordinal)
    {
        "hex_edit", "hex_write", "hex_edit_symbol", "hex_rename", "hex_notebook_edit", "hex_plan_update"
    };

    private static readonly HashSet<string> ReadTools = new(StringComparer.Ordinal)
    {
        "hex_read", "hex_search", "hex_symbols", "hex_refs", "hex_files", "hex_skim", "hex_map",
        "hex_problems", "hex_diagnostics", "hex_plan_read", "hex_find_in_files", "hex_outline", "hex_definition",
        "hex_references", "hex_hover", "hex_symbol_info", "hex_calls", "hex_implementations", "hex_types", "hex_changes"
    };

    /// <summary>
    /// The tools a small model is given, of all the server has, in this order: reading and finding (hex_read,
    /// hex_search, hex_symbols, hex_refs, hex_files, hex_skim, hex_map), changing (hex_edit, hex_write), running
    /// (hex_terminal_run), checking (hex_problems or hex_diagnostics), planning (hex_plan_update). Those the server
    /// doesn't have are left out; nothing else is added.
    /// </summary>
    public static IReadOnlyList<ToolSpec> Choose(IReadOnlyList<ToolSpec> all)
    {
        var byName = new Dictionary<string, ToolSpec>(StringComparer.Ordinal);
        foreach (ToolSpec tool in all)
            byName.TryAdd(tool.Name, tool);

        var chosen = new List<ToolSpec>();
        foreach (string name in Order)
        {
            if (name == "hex_problems" && !byName.ContainsKey(name))
            {
                if (byName.TryGetValue("hex_diagnostics", out ToolSpec? diagnostics))
                    chosen.Add(diagnostics);
                continue;
            }

            if (byName.TryGetValue(name, out ToolSpec? spec))
                chosen.Add(spec);
        }

        return chosen;
    }

    private static readonly HashSet<string> ProfileReads = new(StringComparer.Ordinal)
    {
        "hex_read", "hex_search", "hex_symbols", "hex_refs", "hex_files", "hex_skim", "hex_map"
    };

    /// <summary>The tools a profile gives of those chosen: write all, ask the reading ones only, minimal none.</summary>
    public static IReadOnlyList<ToolSpec> ForProfile(IReadOnlyList<ToolSpec> chosen, string profile) => profile switch
    {
        LocalAgentOptions.MINIMAL => [],
        LocalAgentOptions.READ_ONLY => [.. chosen.Where(t => ProfileReads.Contains(t.Name))],
        _ => chosen,
    };

    /// <summary>Whether a tool changes files (hex_edit, hex_write and the like): the approvals' "edits" mode lets these through.</summary>
    public static bool Edits(string tool) => EditTools.Contains(tool);

    /// <summary>Whether a tool only reads (never asks for approval).</summary>
    public static bool ReadsOnly(string tool) => ReadTools.Contains(tool);

    /// <summary>A tool's output cut to at most so many characters, the middle dropped with a line saying how much.</summary>
    public static string Cut(string output, int maxChars)
    {
        if (output.Length <= maxChars || maxChars <= 0)
            return output;

        int head = (int)(maxChars * 0.6), tail = maxChars - head;
        if (head > 0 && char.IsHighSurrogate(output[head - 1]))
            head--;
        int tailStart = output.Length - tail;
        if (tailStart < output.Length && tailStart > 0 && char.IsLowSurrogate(output[tailStart]))
            tailStart++;
        int cut = tailStart - head;
        return output[..head] + $"\n[… {cut} characters cut …]\n" + output[tailStart..];
    }
}

/// <summary>The connection to the Hex that has the folder open, the same pipe Hex's MCP bridge uses, tagged with the agent's chat.</summary>
internal static class LocalAgentPipe
{
    /// <summary>
    /// The stream to the running Hex's MCP server for this folder (found the way McpBridge finds it; never starts a
    /// Hex), its calls attributed to the Agents panel's chat <paramref name="chatId"/>. Throws IOException when no Hex
    /// has the folder open.
    /// </summary>
    public static async Task<Stream> ConnectAsync(string folder, int chatId, CancellationToken cancel)
    {
        Stream? pipe = await Task.Run(() => McpBridge.OpenForFolder(folder, chatId.ToString(System.Globalization.CultureInfo.InvariantCulture)), cancel).ConfigureAwait(false);
        return pipe ?? throw new IOException($"No Hex has {folder} open");
    }
}
