using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// Hex's MCP server without the editor, over pipes in memory. The handshake and the version it settles on, tools/list
/// and its schemas, tools/call finding the tool and failing the right way, the door's token, and the bridge's choice
/// of Hex by folder. The tools themselves are a stand in, the editor's are the self test's (Hex --selftest mcp).
/// </summary>
public sealed class McpTests : IDisposable
{
    private const int WAIT_MS = 10_000;

    // Two tools, one that echoes and one that throws, and one that waits to be cancelled
    private sealed class StandIn : IMcpTools
    {
        public IReadOnlyList<McpTool> Tools { get; } =
        [
            new McpTool("echo", "Echo", "Say the text back.", McpSchema.Object([("text", McpSchema.String("What to say.")), ("times", McpSchema.Integer("How often."))], "text"), ReadOnly: true),
            new McpTool("boom", "Boom", "Always throws.", McpSchema.Empty()),
            new McpTool("slow", "Slow", "Waits until it's cancelled.", McpSchema.Empty())
        ];

        public async Task<McpToolResult> Call(string name, JsonObject arguments, CancellationToken cancel)
        {
            switch (name)
            {
                case "echo":
                    return McpToolResult.Text(string.Concat(Enumerable.Repeat(arguments.String("text"), arguments.Integer("times") ?? 1)));
                case "slow":
                    await Task.Delay(Timeout.Infinite, cancel);
                    return McpToolResult.Text("never");
                default:
                    throw new InvalidOperationException("it went bang");
            }
        }
    }

    private readonly AnonymousPipeServerStream toServer = new(PipeDirection.Out);
    private readonly AnonymousPipeServerStream toClient = new(PipeDirection.Out);
    private readonly AnonymousPipeClientStream serverReads, clientReads;
    private readonly McpSession session;
    private readonly StreamReader answers;
    private int nextId;

    public McpTests()
    {
        serverReads = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        clientReads = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);
        session = new McpSession(new StreamReader(serverReads, new UTF8Encoding(false)), toClient, new McpServer(new StandIn(), "hex", "1.2.3", "be nice")).Start();
        answers = new StreamReader(clientReads, new UTF8Encoding(false));
    }

    public void Dispose()
    {
        session.Dispose();
        toServer.Dispose();
        toClient.Dispose();
    }

    private void Write(string line)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        toServer.Write(bytes);
        toServer.Flush();
    }

    private JsonObject ReadAnswer()
    {
        Task<string?> line = answers.ReadLineAsync();
        Assert.True(line.Wait(WAIT_MS), "no answer came");
        Assert.NotNull(line.Result);
        Assert.DoesNotContain('\n', line.Result);
        return JsonNode.Parse(line.Result!)!.AsObject();
    }

    private JsonObject Ask(string method, JsonObject? parameters = null)
    {
        int id = ++nextId;
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null)
            message["params"] = parameters;
        Write(message.ToJsonString());

        JsonObject answer = ReadAnswer();
        Assert.Equal(id, answer["id"]!.GetValue<int>());
        Assert.Equal("2.0", answer["jsonrpc"]!.GetValue<string>());
        return answer;
    }

    private JsonObject Initialize(string version = "2025-11-25") =>
        Ask("initialize", new JsonObject { ["protocolVersion"] = version, ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "tests", ["version"] = "9" } });

    private JsonObject Call(string tool, JsonObject? arguments = null) =>
        Ask("tools/call", new JsonObject { ["name"] = tool, ["arguments"] = arguments ?? new JsonObject() });

    [Fact]
    public void The_handshake_says_the_version_what_it_can_do_and_who_it_is()
    {
        JsonObject result = Initialize()["result"]!.AsObject();
        Assert.Equal("2025-11-25", result["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(result["capabilities"]!["tools"]);
        Assert.Equal("hex", result["serverInfo"]!["name"]!.GetValue<string>());
        Assert.Equal("1.2.3", result["serverInfo"]!["version"]!.GetValue<string>());
        Assert.Equal("be nice", result["instructions"]!.GetValue<string>());
        Assert.Equal("tests 9", session.Server.Client);
    }

    [Theory]
    [InlineData("2024-11-05", "2024-11-05")]
    [InlineData("2025-03-26", "2025-03-26")]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2099-01-01", "2025-11-25")]
    [InlineData("1999-01-01", "2025-11-25")]
    [InlineData("not a date", "2025-11-25")]
    public void An_older_client_gets_its_own_version_and_one_it_doesnt_know_gets_the_newest(string asked, string settled)
    {
        Assert.Equal(settled, McpProtocol.Negotiate(asked));
        Assert.Equal(settled, Initialize(asked)["result"]!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public void Initialized_and_other_notifications_get_no_answer_and_ping_gets_an_empty_one()
    {
        Initialize();
        Write("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Write("""{"jsonrpc":"2.0","method":"notifications/something_else","params":{}}""");
        JsonObject ping = Ask("ping");
        Assert.Empty(ping["result"]!.AsObject());
        Assert.True(session.Server.Ready);
    }

    [Fact]
    public void Tools_list_gives_every_tool_with_an_object_schema_and_its_annotations_by_version()
    {
        Initialize("2025-06-18");
        JsonArray tools = Ask("tools/list")["result"]!["tools"]!.AsArray();
        Assert.Equal(["echo", "boom", "slow"], tools.Select(tool => tool!["name"]!.GetValue<string>()));
        foreach (JsonNode? tool in tools)
        {
            Assert.Equal("object", tool!["inputSchema"]!["type"]!.GetValue<string>());
            Assert.NotNull(tool["inputSchema"]!["properties"]);
            Assert.False(string.IsNullOrWhiteSpace(tool["description"]!.GetValue<string>()));
            Assert.NotNull(tool["title"]);
        }

        Assert.True(tools[0]!["annotations"]!["readOnlyHint"]!.GetValue<bool>());
        Assert.Equal("text", tools[0]!["inputSchema"]!["required"]![0]!.GetValue<string>());
    }

    [Fact]
    public void The_oldest_version_gets_no_titles_or_annotations()
    {
        Initialize("2024-11-05");
        JsonObject tool = Ask("tools/list")["result"]!["tools"]![0]!.AsObject();
        Assert.Null(tool["title"]);
        Assert.Null(tool["annotations"]);
    }

    [Fact]
    public void A_call_goes_to_its_tool_with_its_arguments()
    {
        Initialize();
        JsonObject result = Call("echo", new JsonObject { ["text"] = "hi", ["times"] = 3 })["result"]!.AsObject();
        Assert.False(result["isError"]!.GetValue<bool>());
        Assert.Equal("text", result["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("hihihi", result["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void A_tool_that_isnt_there_is_a_protocol_error()
    {
        Initialize();
        JsonObject error = Call("nope")["error"]!.AsObject();
        Assert.Equal(McpProtocol.INVALID_PARAMS, error["code"]!.GetValue<int>());
        Assert.Contains("nope", error["message"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{}", "'text' is required")]
    [InlineData("""{"text":5}""", "'text' should be a string")]
    [InlineData("""{"text":"a","times":1.5}""", "'times' should be a whole number")]
    [InlineData("""{"text":"a","colour":"red"}""", "'colour' isn't an argument")]
    public void Arguments_that_dont_fit_the_schema_come_back_as_the_tool_failing(string arguments, string said)
    {
        Initialize();
        JsonObject result = Call("echo", JsonNode.Parse(arguments)!.AsObject())["result"]!.AsObject();
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains(said, result["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void A_tool_that_throws_is_the_tool_failing_and_the_session_goes_on()
    {
        Initialize();
        JsonObject result = Call("boom")["result"]!.AsObject();
        Assert.True(result["isError"]!.GetValue<bool>());
        Assert.Contains("it went bang", result["content"]![0]!["text"]!.GetValue<string>());
        Assert.Empty(Ask("ping")["result"]!.AsObject());
    }

    [Fact]
    public void A_call_can_be_cancelled_and_a_ping_is_answered_while_it_waits()
    {
        Initialize();
        Write("""{"jsonrpc":"2.0","id":"slow-1","method":"tools/call","params":{"name":"slow","arguments":{}}}""");
        JsonObject ping = Ask("ping");
        Assert.NotNull(ping["result"]);

        Write("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"slow-1"}}""");
        JsonObject cancelled = ReadAnswer();
        Assert.Equal("slow-1", cancelled["id"]!.GetValue<string>());
        Assert.True(cancelled["result"]!["isError"]!.GetValue<bool>());
        Assert.Contains("cancelled", cancelled["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void A_method_that_isnt_there_and_json_that_isnt_json_get_jsonrpcs_errors()
    {
        Assert.Equal(McpProtocol.METHOD_NOT_FOUND, Ask("resources/list")["error"]!["code"]!.GetValue<int>());

        Write("{ this isn't json");
        JsonObject parse = ReadAnswer();
        Assert.Equal(McpProtocol.PARSE_ERROR, parse["error"]!["code"]!.GetValue<int>());

        Write("""{"jsonrpc":"1.0","id":1,"method":"ping"}""");
        Assert.Equal(McpProtocol.INVALID_REQUEST, ReadAnswer()["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public void A_batch_gets_an_array_of_answers()
    {
        Write("""[{"jsonrpc":"2.0","id":1,"method":"ping"},{"jsonrpc":"2.0","method":"notifications/initialized"},{"jsonrpc":"2.0","id":2,"method":"tools/list"}]""");
        Task<string?> line = answers.ReadLineAsync();
        Assert.True(line.Wait(WAIT_MS));
        JsonArray batch = JsonNode.Parse(line.Result!)!.AsArray();
        Assert.Equal(2, batch.Count);
        Assert.Equal(3, batch[1]!["result"]!["tools"]!.AsArray().Count);
    }

    [Fact]
    public void An_image_result_is_base64_with_its_type()
    {
        McpToolResult picture = McpToolResult.Image([1, 2, 3], "a picture");
        JsonObject written = picture.ToJson();
        Assert.Equal("a picture", written["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("image", written["content"]![1]!["type"]!.GetValue<string>());
        Assert.Equal("AQID", written["content"]![1]!["data"]!.GetValue<string>());
        Assert.Equal("image/png", written["content"]![1]!["mimeType"]!.GetValue<string>());
    }

    [Fact]
    public void The_session_ends_when_the_client_hangs_up()
    {
        bool closed = false;
        session.Closed = _ => closed = true;
        toServer.Dispose();
        Assert.True(session.Wait(WAIT_MS));
        Assert.True(closed);
    }
}

/// <summary>The door's token, the bridge's command line, and its choice of Hex by folder.</summary>
public sealed class McpDoorTests
{
    private static (bool Admitted, string Said) Knocking(string first, string token)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(first));
        using var output = new MemoryStream();
        bool admitted = McpGate.Admit(new StreamReader(input), output, token);
        return (admitted, Encoding.ASCII.GetString(output.ToArray()));
    }

    [Fact]
    public void The_right_token_is_let_in()
    {
        Assert.Equal((true, "OK\n"), Knocking("HEX-MCP abc123\n{\"jsonrpc\":\"2.0\"}\n", "abc123"));
    }

    [Theory]
    [InlineData("HEX-MCP abc124\n")]
    [InlineData("HEX-MCP \n")]
    [InlineData("abc123\n")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}\n")]
    [InlineData("")]
    public void Anything_else_is_turned_away(string first)
    {
        Assert.False(Knocking(first, "abc123").Admitted);
    }

    [Fact]
    public void The_client_side_of_the_door_reads_its_answer_and_nothing_after_it()
    {
        // What the server would write, the answer and then the first message of the session
        using var stream = new DuplexStub(Encoding.ASCII.GetBytes("OK\n{\"next\":1}\n"));
        Assert.True(McpGate.Knock(stream, "abc123", out string? why));
        Assert.Null(why);
        Assert.Equal("HEX-MCP abc123\n", Encoding.UTF8.GetString(stream.Written.ToArray()));
        Assert.Equal('{', (char)stream.ReadByte());

        using var refused = new DuplexStub(Encoding.ASCII.GetBytes("NO\n"));
        Assert.False(McpGate.Knock(refused, "wrong", out why));
        Assert.NotNull(why);
    }

    [Fact]
    public void Tokens_are_long_random_and_different()
    {
        string a = McpDiscovery.NewToken(), b = McpDiscovery.NewToken();
        Assert.Equal(64, a.Length);
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData(@"C:\a folder\x", "\"C:\\a folder\\x\"")]
    [InlineData(@"C:\a folder\", "\"C:\\a folder\\\\\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData("", "\"\"")]
    public void Arguments_are_quoted_the_way_windows_reads_them_back(string argument, string quoted)
    {
        Assert.Equal(quoted, McpBridge.Quoted(argument));
    }

    [Fact]
    public void The_bridge_picks_the_hex_with_the_nearest_folder_open()
    {
        string root = Path.Combine(Path.GetTempPath(), "mcp-pick");
        var outer = new McpListing(1, "a", "t", [root]);
        var inner = new McpListing(2, "b", "t", [Path.Combine(root, "game")]);
        var other = new McpListing(3, "c", "t", [Path.Combine(Path.GetTempPath(), "elsewhere")]);
        McpListing[] all = [outer, inner, other];

        Assert.Equal(2, McpDiscovery.For(Path.Combine(root, "game", "src"), all)?.Pid);
        Assert.Equal(1, McpDiscovery.For(Path.Combine(root, "docs"), all)?.Pid);
        Assert.Equal(1, McpDiscovery.For(root, all)?.Pid);
        Assert.Null(McpDiscovery.For(Path.Combine(Path.GetTempPath(), "mcp-pickle"), all));
    }

    [Fact]
    public void A_listing_written_reads_back_and_one_of_a_process_thats_gone_is_dropped()
    {
        string home = Path.Combine(Path.GetTempPath(), "mcp-home-" + Guid.NewGuid().ToString("N"));
        string? was = Environment.GetEnvironmentVariable("HEX_HOME");
        Environment.SetEnvironmentVariable("HEX_HOME", home);
        try
        {
            var mine = new McpListing(Environment.ProcessId, McpDiscovery.PipeOf(Environment.ProcessId), McpDiscovery.NewToken(), [@"C:\code"]);
            McpDiscovery.Write(mine);
            Assert.Equal(mine.Token, McpDiscovery.Of(Environment.ProcessId)?.Token);
            Assert.Equal([@"C:\code"], McpDiscovery.Of(Environment.ProcessId)!.Folders);

            // A process id nothing has
            McpDiscovery.Write(mine with { Pid = int.MaxValue - 7 });
            List<McpListing> running = McpDiscovery.All();
            Assert.Single(running);
            Assert.False(File.Exists(Path.Combine(McpDiscovery.Folder, $"{int.MaxValue - 7}.json")));

            McpDiscovery.Remove(Environment.ProcessId);
            Assert.Null(McpDiscovery.Of(Environment.ProcessId));
        }
        finally
        {
            Environment.SetEnvironmentVariable("HEX_HOME", was);
            try { Directory.Delete(home, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void A_folders_own_settings_cant_switch_the_server_on()
    {
        string folder = Path.Combine(Path.GetTempPath(), "mcp-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, HexSettings.WORKSPACE_FOLDER));
        try
        {
            string user = Path.Combine(folder, "user.hor");
            File.WriteAllText(user, "let settings = {\n    editor: {\n        minimap: false\n    }\n};\n");
            File.WriteAllText(HexSettings.WorkspacePathOf(folder), "let settings = {\n    editor: {\n        mcp_server: true,\n        tab_size: 2\n    }\n};\n");

            var settings = new HexSettings(user);
            Assert.False(settings.McpServer);
            settings.LoadWorkspace(folder);
            Assert.Equal(2, settings.TabSize);
            Assert.False(settings.McpServer);

            File.WriteAllText(user, "let settings = {\n    editor: {\n        mcp_server: true\n    }\n};\n");
            Assert.True(new HexSettings(user).McpServer);
            Assert.False(HexSettings.IsWorkspaceKey("mcp_server"));
        }
        finally
        {
            try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
        }
    }

    // A stream that reads what it was given and keeps what's written to it
    private sealed class DuplexStub(byte[] toRead) : Stream
    {
        private readonly MemoryStream reading = new(toRead);
        public MemoryStream Written { get; } = new();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => reading.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
    }
}
