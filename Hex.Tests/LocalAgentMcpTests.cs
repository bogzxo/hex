using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;

using Hex.Core;

namespace Hex.Tests;

/// <summary>The local agent's MCP client against an in-memory fake server, and its tool choice and output cutting.</summary>
public sealed class LocalAgentMcpTests
{
    /// <summary>The server's end of two in-memory pipes, one JSON message a line.</summary>
    private sealed class Fake : IAsyncDisposable
    {
        private readonly Pipe toClient = new(), toServer = new();
        private readonly StreamReader heard;
        private readonly StreamWriter said;
        public readonly List<JsonObject> Received = [];

        public Fake()
        {
            heard = new StreamReader(toServer.Reader.AsStream(), new UTF8Encoding(false));
            said = new StreamWriter(toClient.Writer.AsStream(), new UTF8Encoding(false)) { AutoFlush = true };
        }

        public McpClient Client() => new(toClient.Reader.AsStream(), toServer.Writer.AsStream());

        public async Task<JsonObject> ReadAsync()
        {
            string line = await heard.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) ?? throw new IOException("client hung up");
            var message = (JsonObject)JsonNode.Parse(line)!;
            Received.Add(message);
            return message;
        }

        public Task SendAsync(JsonNode message) => said.WriteLineAsync(message.ToJsonString());

        public Task ReplyAsync(JsonObject request, JsonNode result) =>
            SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = result });

        public ValueTask DisposeAsync()
        {
            said.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task Initialize_sends_the_handshake_then_the_initialized_notification()
    {
        await using var fake = new Fake();
        await using McpClient client = fake.Client();
        Task init = client.InitializeAsync("hex-local-agent", default);

        JsonObject request = await fake.ReadAsync();
        Assert.Equal("initialize", request["method"]!.ToString());
        Assert.Equal("hex-local-agent", request["params"]!["clientInfo"]!["name"]!.ToString());
        Assert.NotNull(request["params"]!["protocolVersion"]);
        await fake.ReplyAsync(request, new JsonObject { ["protocolVersion"] = "2025-11-25", ["capabilities"] = new JsonObject() });
        await init.WaitAsync(TimeSpan.FromSeconds(5));

        JsonObject initialized = await fake.ReadAsync();
        Assert.Equal("notifications/initialized", initialized["method"]!.ToString());
        Assert.Null(initialized["id"]);
    }

    [Fact]
    public async Task ListTools_follows_pages_and_fills_defaults()
    {
        await using var fake = new Fake();
        await using McpClient client = fake.Client();
        Task<IReadOnlyList<ToolSpec>> list = client.ListToolsAsync(default);

        JsonObject first = await fake.ReadAsync();
        Assert.Equal("tools/list", first["method"]!.ToString());
        await fake.ReplyAsync(first, new JsonObject
        {
            ["tools"] = new JsonArray(new JsonObject { ["name"] = "hex_read", ["description"] = "Reads", ["inputSchema"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() } }),
            ["nextCursor"] = "page2"
        });
        JsonObject second = await fake.ReadAsync();
        Assert.Equal("page2", second["params"]!["cursor"]!.ToString());
        await fake.ReplyAsync(second, new JsonObject { ["tools"] = new JsonArray(new JsonObject { ["name"] = "hex_map" }) });

        IReadOnlyList<ToolSpec> tools = await list.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, tools.Count);
        Assert.Equal("hex_read", tools[0].Name);
        Assert.Equal("Reads", tools[0].Description);
        Assert.Contains("\"properties\"", tools[0].ParametersJson);
        Assert.Equal("hex_map", tools[1].Name);
        Assert.Equal("", tools[1].Description);
        Assert.Equal("{\"type\":\"object\"}", tools[1].ParametersJson);
    }

    [Fact]
    public async Task Call_joins_text_content_and_reports_isError()
    {
        await using var fake = new Fake();
        await using McpClient client = fake.Client();
        Task<AgentToolResult> call = client.CallAsync("hex_read", "{\"path\":\"a.cs\"}", default);

        JsonObject request = await fake.ReadAsync();
        Assert.Equal("tools/call", request["method"]!.ToString());
        Assert.Equal("hex_read", request["params"]!["name"]!.ToString());
        Assert.Equal("a.cs", request["params"]!["arguments"]!["path"]!.ToString());
        await fake.ReplyAsync(request, new JsonObject
        {
            ["content"] = new JsonArray(
                new JsonObject { ["type"] = "text", ["text"] = "one" },
                new JsonObject { ["type"] = "image", ["data"] = "x" },
                new JsonObject { ["type"] = "text", ["text"] = "two" }),
            ["isError"] = true
        });

        AgentToolResult result = await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("one\ntwo", result.Text);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Call_turns_a_json_rpc_error_into_an_error_result()
    {
        await using var fake = new Fake();
        await using McpClient client = fake.Client();
        Task<AgentToolResult> call = client.CallAsync("nope", "{}", default);

        JsonObject request = await fake.ReadAsync();
        await fake.SendAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = request["id"]!.DeepClone(),
            ["error"] = new JsonObject { ["code"] = -32602, ["message"] = "Unknown tool" }
        });

        AgentToolResult result = await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("Unknown tool", result.Text);
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Call_ignores_notifications_and_answers_ping()
    {
        await using var fake = new Fake();
        await using McpClient client = fake.Client();
        Task<AgentToolResult> call = client.CallAsync("hex_map", "{}", default);

        JsonObject request = await fake.ReadAsync();
        await fake.SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/progress", ["params"] = new JsonObject() });
        await fake.SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "p1", ["method"] = "ping" });
        JsonObject pong = await fake.ReadAsync();
        Assert.Equal("p1", pong["id"]!.ToString());
        Assert.NotNull(pong["result"]);
        Assert.Null(pong["method"]);

        await fake.ReplyAsync(request, new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "ok" }) });
        AgentToolResult result = await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("ok", result.Text);
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task Call_ids_increase()
    {
        await using var fake = new Fake();
        await using McpClient client = fake.Client();
        for (int i = 0; i < 2; i++)
        {
            Task<AgentToolResult> call = client.CallAsync("hex_map", "{}", default);
            JsonObject request = await fake.ReadAsync();
            await fake.ReplyAsync(request, new JsonObject { ["content"] = new JsonArray() });
            await call.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.True(fake.Received[1]["id"]!.GetValue<int>() > fake.Received[0]["id"]!.GetValue<int>());
    }

    private static ToolSpec Spec(string name) => new(name, "", "{}");

    [Fact]
    public void Choose_keeps_the_documented_order_and_only_those_present()
    {
        string[] server = ["hex_plan_update", "hex_write", "hex_debug_start", "hex_read", "hex_diagnostics", "hex_map", "hex_terminal_run", "hex_edit"];
        var chosen = LocalAgentTools.Choose(server.Select(Spec).ToList()).Select(t => t.Name).ToArray();
        Assert.Equal(["hex_read", "hex_map", "hex_edit", "hex_write", "hex_terminal_run", "hex_diagnostics", "hex_plan_update"], chosen);
    }

    [Fact]
    public void Choose_prefers_problems_over_diagnostics()
    {
        var chosen = LocalAgentTools.Choose([Spec("hex_diagnostics"), Spec("hex_problems")]).Select(t => t.Name).ToArray();
        Assert.Equal(["hex_problems"], chosen);
    }

    [Fact]
    public void Edits_and_ReadsOnly_classify_tools()
    {
        foreach (string tool in new[] { "hex_edit", "hex_write", "hex_edit_symbol", "hex_rename", "hex_notebook_edit", "hex_plan_update" })
        {
            Assert.True(LocalAgentTools.Edits(tool), tool);
            Assert.False(LocalAgentTools.ReadsOnly(tool), tool);
        }

        foreach (string tool in new[] { "hex_read", "hex_search", "hex_symbols", "hex_refs", "hex_files", "hex_skim", "hex_map", "hex_problems", "hex_diagnostics", "hex_plan_read" })
        {
            Assert.True(LocalAgentTools.ReadsOnly(tool), tool);
            Assert.False(LocalAgentTools.Edits(tool), tool);
        }

        Assert.False(LocalAgentTools.Edits("hex_terminal_run"));
        Assert.False(LocalAgentTools.ReadsOnly("hex_terminal_run"));
    }

    [Fact]
    public void Cut_leaves_short_output_alone()
    {
        Assert.Equal("short", LocalAgentTools.Cut("short", 100));
    }

    [Fact]
    public void Cut_keeps_head_and_tail_with_a_marker()
    {
        string text = new string('a', 500) + new string('b', 500);
        string cut = LocalAgentTools.Cut(text, 100);
        Assert.StartsWith(new string('a', 60), cut);
        Assert.EndsWith(new string('b', 40), cut);
        Assert.Contains("[… 900 characters cut …]", cut);
        Assert.True(cut.Length < 200);
    }

    [Fact]
    public void Cut_never_splits_a_surrogate_pair()
    {
        string text = new string('a', 59) + "😀" + new string('b', 200) + "😀" + new string('c', 39);
        string cut = LocalAgentTools.Cut(text, 100);
        for (int i = 0; i < cut.Length; i++)
        {
            if (char.IsHighSurrogate(cut[i]))
                Assert.True(i + 1 < cut.Length && char.IsLowSurrogate(cut[i + 1]));
            if (char.IsLowSurrogate(cut[i]))
                Assert.True(i > 0 && char.IsHighSurrogate(cut[i - 1]));
        }
    }

    [Fact]
    public async Task Connect_without_a_Hex_throws_IOException()
    {
        string folder = Path.Combine(Path.GetTempPath(), "hex-la-nohex-" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<IOException>(() => LocalAgentPipe.ConnectAsync(folder, 1, default));
    }
}
