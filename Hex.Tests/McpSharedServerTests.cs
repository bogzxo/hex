using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// The shared MCP server's multiplexer with a stand in for the heavy server (an in-process JSON-RPC one with a thread of
/// its own): two agents on one process, the handshake said once and the answer kept, ids and progress tokens made the
/// server's and back, cancelling, a crash failing the calls and the next call starting it again, the idle stop, and
/// worktrees never sharing.
/// </summary>
public sealed class McpSharedServerTests : IDisposable
{
    private const int WAIT_MS = 5_000;
    private readonly string temp = Path.Combine(Path.GetTempPath(), "hex-shared-tests-" + Guid.NewGuid().ToString("N"));

    public McpSharedServerTests() => Directory.CreateDirectory(temp);

    public void Dispose()
    {
        try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
    }

    /// <summary>The heavy server, its processes counted and what each heard kept.</summary>
    private sealed class Fake
    {
        public int Starts, Initializes, Initialized;
        public readonly ConcurrentQueue<JsonObject> Heard = new();
        public readonly List<Run> Runs = [];

        public IMcpUpstream Start(McpSharedKey key, Action<string> heard, Action<string> ended)
        {
            Interlocked.Increment(ref Starts);
            var run = new Run(this, heard, ended);
            lock (Runs) Runs.Add(run);
            return run;
        }

        public sealed class Run : IMcpUpstream
        {
            private readonly Fake fake;
            private readonly Action<string> heard;
            private readonly Action<string> ended;
            private readonly BlockingCollection<string> incoming = new();
            private readonly Dictionary<string, long> slow = [];
            public bool Disposed;
            public string? Roots;

            public Run(Fake fake, Action<string> heard, Action<string> ended)
            {
                this.fake = fake;
                this.heard = heard;
                this.ended = ended;
                new Thread(Loop) { IsBackground = true }.Start();
            }

            public int Pid => 0;

            public void Send(string line)
            {
                try { incoming.Add(line); } catch (InvalidOperationException) { }
            }

            public void Dispose()
            {
                Disposed = true;
                incoming.CompleteAdding();
            }

            private void Say(JsonObject message) => heard(message.ToJsonString());

            private void Loop()
            {
                foreach (string line in incoming.GetConsumingEnumerable())
                {
                    var message = (JsonObject)JsonNode.Parse(line)!;
                    fake.Heard.Enqueue(message);
                    string? method = message["method"]?.GetValue<string>();
                    JsonNode? id = message["id"]?.DeepClone();
                    switch (method)
                    {
                        case "initialize":
                            Interlocked.Increment(ref fake.Initializes);
                            Say(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JsonObject { ["protocolVersion"] = "2025-06-18", ["serverInfo"] = new JsonObject { ["name"] = "fake" }, ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() } } });
                            break;
                        case "notifications/initialized":
                            Interlocked.Increment(ref fake.Initialized);
                            break;
                        case "tools/list":
                            Say(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = new JsonObject { ["tools"] = new JsonArray() } });
                            break;
                        case "tools/call":
                            Call(id, message["params"]!.AsObject());
                            break;
                        case "notifications/cancelled":
                            break;
                        case null:
                            // The answer to roots/list
                            Roots = message["result"]?["roots"]?[0]?["uri"]?.GetValue<string>();
                            Say(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = slow["roots"], ["result"] = Text(Roots ?? "none") });
                            break;
                    }

                    if (incoming.IsAddingCompleted)
                        return;
                }
            }

            private void Call(JsonNode? id, JsonObject said)
            {
                string tool = said["name"]!.GetValue<string>();
                switch (tool)
                {
                    case "echo":
                        Say(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = Text(said["arguments"]?["text"]?.GetValue<string>() ?? string.Empty) });
                        break;
                    case "slow":
                        // Never answered, cancelled or not
                        break;
                    case "progress":
                        JsonNode token = said["_meta"]!["progressToken"]!.DeepClone();
                        Say(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/progress", ["params"] = new JsonObject { ["progressToken"] = token, ["progress"] = 1 } });
                        Say(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = Text("done") });
                        break;
                    case "notify":
                        Say(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/tools/list_changed" });
                        Say(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = Text("told") });
                        break;
                    case "roots":
                        slow["roots"] = id!.GetValue<long>();
                        Say(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = "ask-1", ["method"] = "roots/list" });
                        break;
                    case "crash":
                        incoming.CompleteAdding();
                        ended("exit code 3");
                        break;
                }
            }

            private static JsonObject Text(string text) => new() { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
        }
    }

    /// <summary>An agent, what it's told queued to be waited for.</summary>
    private sealed class Agent
    {
        public readonly BlockingCollection<string> Told = new();
        public readonly McpSharedClient Client;

        public Agent(string who) => Client = new McpSharedClient(line => Told.Add(line), who);

        public JsonObject Next()
        {
            Assert.True(Told.TryTake(out string? line, WAIT_MS), "nothing came back in time");
            return (JsonObject)JsonNode.Parse(line!)!;
        }

        public bool Quiet(int milliseconds = 200) => !Told.TryTake(out _, milliseconds);
    }

    private static JsonNode Node(object id) => id switch
    {
        int number => JsonValue.Create(number),
        string text => JsonValue.Create(text),
        _ => throw new ArgumentException("an id is a number or a text")
    };

    private static string Request(object id, string method, JsonObject? said = null) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = Node(id), ["method"] = method, ["params"] = said ?? new JsonObject() }.ToJsonString();

    private static string Call(object id, string tool, string? text = null, object? progress = null)
    {
        var said = new JsonObject { ["name"] = tool, ["arguments"] = new JsonObject { ["text"] = text } };
        if (progress is not null)
            said["_meta"] = new JsonObject { ["progressToken"] = Node(progress) };
        return Request(id, "tools/call", said);
    }

    private static string Cancelled(object id) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/cancelled", ["params"] = new JsonObject { ["requestId"] = Node(id) } }.ToJsonString();

    private const string INITIALIZED = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    private static string TextOf(JsonObject answer) => answer["result"]!["content"]![0]!["text"]!.GetValue<string>();

    private McpSharedKey Key(string name = "roslyn", string? root = null) => new(name, root ?? temp, "fake", []);

    private static McpSharedServer Server(Fake fake, McpSharedKey key, int most = 4, int idleMs = 60_000) =>
        new(key, fake.Start, most, TimeSpan.FromMilliseconds(idleMs), _ => { });

    private static Agent Join(McpSharedServer server, string who)
    {
        var agent = new Agent(who);
        Assert.True(server.Attach(agent.Client));
        server.FromClient(agent.Client, Request(1, "initialize", new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = who } }));
        JsonObject answer = agent.Next();
        Assert.Equal(1, answer["id"]!.GetValue<int>());
        Assert.Equal("fake", answer["result"]!["serverInfo"]!["name"]!.GetValue<string>());
        server.FromClient(agent.Client, INITIALIZED);
        return agent;
    }

    private static void WaitFor(Func<bool> condition)
    {
        long giveUp = Environment.TickCount64 + WAIT_MS;
        while (!condition())
        {
            Assert.True(Environment.TickCount64 < giveUp, "it never happened");
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void Two_agents_share_one_process_shaken_hands_with_once()
    {
        var fake = new Fake();
        McpSharedServer server = Server(fake, Key());
        Join(server, "a");
        Join(server, "b");

        WaitFor(() => fake.Initialized == 1);
        Assert.Equal(1, fake.Starts);
        Assert.Equal(1, fake.Initializes);
        Assert.Equal(1, fake.Initialized);
        Assert.Equal(2, server.Clients);

        // Told it has roots, which Hex answers
        JsonObject init = fake.Heard.First(heard => heard["method"]?.GetValue<string>() == "initialize");
        Assert.NotNull(init["params"]!["capabilities"]!["roots"]);
    }

    [Fact]
    public void Each_agent_gets_its_own_answer_under_its_own_id()
    {
        var fake = new Fake();
        McpSharedServer server = Server(fake, Key());
        Agent a = Join(server, "a"), b = Join(server, "b");

        server.FromClient(a.Client, Call(5, "echo", "from a"));
        server.FromClient(b.Client, Call(5, "echo", "from b"));
        server.FromClient(b.Client, Call("x", "echo", "b again"));

        JsonObject fromA = a.Next();
        Assert.Equal(5, fromA["id"]!.GetValue<int>());
        Assert.Equal("from a", TextOf(fromA));
        JsonObject first = b.Next(), second = b.Next();
        Assert.Equal(5, first["id"]!.GetValue<int>());
        Assert.Equal("from b", TextOf(first));
        Assert.Equal("x", second["id"]!.GetValue<string>());
        Assert.Equal("b again", TextOf(second));

        // Up they went under ids of the server's, each its own
        var ids = fake.Heard.Where(heard => heard["method"]?.GetValue<string>() == "tools/call").Select(heard => heard["id"]!.ToJsonString()).ToList();
        Assert.Equal(3, ids.Count);
        Assert.Equal(3, ids.Distinct().Count());
    }

    [Fact]
    public void A_ping_is_answered_by_Hex_and_an_initialize_after_the_first_from_what_was_kept()
    {
        var fake = new Fake();
        McpSharedServer server = Server(fake, Key());
        Agent a = Join(server, "a");
        server.FromClient(a.Client, Request(9, "ping"));
        Assert.Equal(9, a.Next()["id"]!.GetValue<int>());
        Join(server, "b");
        Assert.Equal(1, fake.Initializes);
        Assert.DoesNotContain(fake.Heard, heard => heard["method"]?.GetValue<string>() == "ping");
    }

    [Fact]
    public void A_cancelled_call_is_cancelled_under_the_servers_id_and_frees_its_place()
    {
        var fake = new Fake();
        McpSharedServer server = Server(fake, Key(), most: 1);
        Agent a = Join(server, "a"), b = Join(server, "b");

        server.FromClient(a.Client, Call(7, "slow"));
        WaitFor(() => fake.Heard.Any(heard => heard["params"]?["name"]?.GetValue<string>() == "slow"));
        long up = fake.Heard.First(heard => heard["params"]?["name"]?.GetValue<string>() == "slow")["id"]!.GetValue<long>();

        // With the one place taken, b's call waits
        server.FromClient(b.Client, Call(1, "echo", "waited"));
        Assert.True(b.Quiet());

        server.FromClient(a.Client, Cancelled(7));
        WaitFor(() => fake.Heard.Any(heard => heard["method"]?.GetValue<string>() == "notifications/cancelled"));
        JsonObject cancel = fake.Heard.First(heard => heard["method"]?.GetValue<string>() == "notifications/cancelled");
        Assert.Equal(up, cancel["params"]!["requestId"]!.GetValue<long>());
        Assert.Equal("waited", TextOf(b.Next()));
        Assert.True(a.Quiet());
    }

    [Fact]
    public void A_call_cancelled_while_it_waits_never_goes_up()
    {
        var fake = new Fake();
        McpSharedServer server = Server(fake, Key(), most: 1);
        Agent a = Join(server, "a"), b = Join(server, "b");

        server.FromClient(a.Client, Call(1, "slow"));
        server.FromClient(b.Client, Call(2, "slow"));
        server.FromClient(b.Client, Cancelled(2));
        server.FromClient(a.Client, Cancelled(1));
        server.FromClient(a.Client, Call(3, "echo", "after"));
        Assert.Equal("after", TextOf(a.Next()));
        Assert.Single(fake.Heard, heard => heard["params"]?["name"]?.GetValue<string>() == "slow");
    }

    [Fact]
    public void Progress_tokens_are_the_servers_and_go_back_as_the_agents()
    {
        var fake = new Fake();
        McpSharedServer server = Server(fake, Key());
        Agent a = Join(server, "a"), b = Join(server, "b");

        server.FromClient(a.Client, Call(1, "progress", progress: "tok"));
        server.FromClient(b.Client, Call(1, "progress", progress: "tok"));
        foreach (Agent agent in new[] { a, b })
        {
            JsonObject progress = agent.Next();
            Assert.Equal("notifications/progress", progress["method"]!.GetValue<string>());
            Assert.Equal("tok", progress["params"]!["progressToken"]!.GetValue<string>());
            Assert.Equal("done", TextOf(agent.Next()));
        }

        var tokens = fake.Heard.Where(heard => heard["params"]?["name"]?.GetValue<string>() == "progress")
            .Select(heard => heard["params"]!["_meta"]!["progressToken"]!.GetValue<string>()).ToList();
        Assert.Equal(2, tokens.Distinct().Count());
    }

    [Fact]
    public void What_the_server_says_to_nobody_goes_to_everybody()
    {
        var fake = new Fake();
        McpSharedServer server = Server(fake, Key());
        Agent a = Join(server, "a"), b = Join(server, "b");

        server.FromClient(a.Client, Call(1, "notify"));
        Assert.Equal("notifications/tools/list_changed", a.Next()["method"]!.GetValue<string>());
        Assert.Equal("told", TextOf(a.Next()));
        Assert.Equal("notifications/tools/list_changed", b.Next()["method"]!.GetValue<string>());
        Assert.True(b.Quiet());
    }

    [Fact]
    public void Roots_are_answered_by_Hex_with_the_worktree()
    {
        var fake = new Fake();
        McpSharedServer server = Server(fake, Key());
        Agent a = Join(server, "a");

        server.FromClient(a.Client, Call(1, "roots"));
        string uri = TextOf(a.Next());
        Assert.Equal(new Uri(temp + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/'), uri);
    }

    [Fact]
    public void A_crash_fails_the_calls_up_and_the_next_call_starts_it_again()
    {
        var fake = new Fake();
        McpSharedServer server = Server(fake, Key());
        Agent a = Join(server, "a"), b = Join(server, "b");

        server.FromClient(b.Client, Call(4, "slow"));
        WaitFor(() => fake.Heard.Any(heard => heard["params"]?["name"]?.GetValue<string>() == "slow"));
        server.FromClient(a.Client, Call(3, "crash"));

        JsonObject failedA = a.Next(), failedB = b.Next();
        Assert.Equal(3, failedA["id"]!.GetValue<int>());
        Assert.Equal(McpSharedServer.SHARED_ERROR, failedA["error"]!["code"]!.GetValue<int>());
        Assert.Equal(4, failedB["id"]!.GetValue<int>());
        Assert.Contains("exit code 3", failedB["error"]!["message"]!.GetValue<string>());
        Assert.Equal(1, fake.Starts);

        // Not started again until it's wanted, then shaken hands with again
        server.FromClient(b.Client, Call(5, "echo", "back"));
        Assert.Equal("back", TextOf(b.Next()));
        Assert.Equal(2, fake.Starts);
        Assert.Equal(2, fake.Initializes);
        WaitFor(() => fake.Initialized == 2);
    }

    [Fact]
    public void A_server_that_never_comes_up_fails_the_calls_waiting()
    {
        McpSharedServer server = new(Key(), (_, _, _) => throw new FileNotFoundException("not there"), 4, TimeSpan.FromMinutes(1), _ => { });
        var a = new Agent("a");
        server.Attach(a.Client);
        server.FromClient(a.Client, Request(1, "initialize"));
        JsonObject failed = a.Next();
        Assert.Contains("not there", failed["error"]!["message"]!.GetValue<string>());
    }

    [Fact]
    public void An_agent_going_cancels_what_it_had_up()
    {
        var fake = new Fake();
        McpSharedServer server = Server(fake, Key(), most: 1);
        Agent a = Join(server, "a"), b = Join(server, "b");

        server.FromClient(a.Client, Call(1, "slow"));
        WaitFor(() => fake.Heard.Any(heard => heard["params"]?["name"]?.GetValue<string>() == "slow"));
        server.FromClient(b.Client, Call(1, "echo", "next"));
        server.Detach(a.Client);
        Assert.Equal("next", TextOf(b.Next()));
        Assert.Contains(fake.Heard, heard => heard["method"]?.GetValue<string>() == "notifications/cancelled");
    }

    [Fact]
    public void With_no_agent_it_stops_after_a_while_and_the_next_one_gets_another()
    {
        var fake = new Fake();
        var servers = new McpSharedServers(fake.Start, 4, TimeSpan.FromMilliseconds(100), _ => { });
        var a = new Agent("a");
        McpSharedServer first = servers.Join(Key(), a.Client);
        first.FromClient(a.Client, Request(1, "initialize"));
        a.Next();
        first.Detach(a.Client);

        WaitFor(() => first.Retired);
        Assert.True(fake.Runs[0].Disposed);
        Assert.Empty(servers.Servers);

        var b = new Agent("b");
        McpSharedServer second = servers.Join(Key(), b.Client);
        Assert.NotSame(first, second);
        second.FromClient(b.Client, Request(1, "initialize"));
        b.Next();
        Assert.Equal(2, fake.Starts);
    }

    [Fact]
    public void An_agent_back_in_time_keeps_it()
    {
        var fake = new Fake();
        var servers = new McpSharedServers(fake.Start, 4, TimeSpan.FromMilliseconds(300), _ => { });
        var a = new Agent("a");
        McpSharedServer first = servers.Join(Key(), a.Client);
        first.Detach(a.Client);
        var b = new Agent("b");
        Assert.Same(first, servers.Join(Key(), b.Client));
        Thread.Sleep(500);
        Assert.False(first.Retired);
    }

    [Fact]
    public void Worktrees_never_share_and_folders_in_one_do()
    {
        string one = Path.Combine(temp, "one"), two = Path.Combine(temp, "two"), worktree = Path.Combine(temp, "worktree");
        Directory.CreateDirectory(Path.Combine(one, ".git"));
        Directory.CreateDirectory(Path.Combine(one, "src", "deep"));
        Directory.CreateDirectory(Path.Combine(two, ".git"));
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: elsewhere");

        Assert.Equal(one, McpSharedKey.RootOf(Path.Combine(one, "src", "deep")));
        Assert.Equal(worktree, McpSharedKey.RootOf(worktree + Path.DirectorySeparatorChar));

        var fake = new Fake();
        var servers = new McpSharedServers(fake.Start, 4, TimeSpan.FromMinutes(1), _ => { });
        McpSharedServer inOne = servers.Join(Key(root: McpSharedKey.RootOf(Path.Combine(one, "src"))), new Agent("a").Client);
        McpSharedServer alsoOne = servers.Join(Key(root: McpSharedKey.RootOf(one)), new Agent("b").Client);
        McpSharedServer inTwo = servers.Join(Key(root: McpSharedKey.RootOf(two)), new Agent("c").Client);
        McpSharedServer other = servers.Join(Key(name: "other", root: McpSharedKey.RootOf(one)), new Agent("d").Client);
        Assert.Same(inOne, alsoOne);
        Assert.NotSame(inOne, inTwo);
        Assert.NotSame(inOne, other);
        Assert.Equal(3, servers.Servers.Count);
    }

    [Fact]
    public void A_proxys_first_line_says_its_key()
    {
        string folder = Path.Combine(temp, "repo");
        Directory.CreateDirectory(Path.Combine(folder, ".git"));
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        var request = new McpProxy.Request("roslyn", Path.Combine(folder, "sub"), "cwm-roslyn-navigator", ["--all-solutions"], "12", 0);
        McpSharedKey? key = McpSharedServers.KeyOf(request.Hello(), out string who);
        Assert.NotNull(key);
        Assert.Equal(folder, key.Root);
        Assert.Equal(["--all-solutions"], key.Arguments);
        Assert.Equal("chat 12", who);
        Assert.Null(McpSharedServers.KeyOf("""{"name":"x"}""", out _));
        Assert.Null(McpSharedServers.KeyOf("not json", out _));
    }

    [Fact]
    public void The_proxys_arguments_are_its_own_before_the_dash_and_the_servers_after()
    {
        string[] args = ["--mcp-proxy", "--name", "roslyn", "--folder", temp, "--agent-chat", "4", "--hex", "77", "--", "cwm-roslyn-navigator", "--mcp", "-h"];
        Assert.True(McpProxy.IsProxyMode(args));
        Assert.False(McpProxy.IsProxyMode(["--mcp", "--", "x", "--mcp-proxy"]));
        McpProxy.Request? request = McpProxy.Parse(args);
        Assert.NotNull(request);
        Assert.Equal("roslyn", request.Name);
        Assert.Equal(Path.GetFullPath(temp), request.Folder);
        Assert.Equal("cwm-roslyn-navigator", request.Command);
        Assert.Equal(["--mcp", "-h"], request.Arguments);
        Assert.Equal("4", request.Chat);
        Assert.Equal(77, request.Hex);
        Assert.Null(McpProxy.Parse(["--mcp-proxy", "--name", "x"]));
    }
}
