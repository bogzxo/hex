using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

using Hex.Core;
using Hex.Dawn;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

/// <summary>
/// Serve AI over a link (docs/hex-link-plan.md, 5.7): A is the desktop with the model, B the laptop that borrows it. The
/// server's chunks (content, reasoning, tool calls) arrive in order and parse as a local server's would, a cancel reaches A's
/// model at once, the Ai right and A's serving both have to be so, a device has one chat running and the rest queue (and
/// are told their place), a request is bounded, linked models group in the picker, the one device serving AI is the
/// effective chat model when there is none here, and the loopback endpoint answers only with its token.
/// </summary>
public sealed class HexLinkAiTests
{
    private static string Chunk(string delta, string? finish = null) =>
        $"{{\"model\":\"m\",\"choices\":[{{\"index\":0,\"delta\":{delta},\"finish_reason\":{(finish is null ? "null" : $"\"{finish}\"")}}}]}}";

    private static readonly string ASKED = new JsonObject
    {
        ["model"] = "ignored",
        ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hi" }),
        ["max_tokens"] = 100000,
    }.ToJsonString();

    private sealed class AiServices(LinkServices inner) : IHexLinkServices
    {
        public LinkServices Inner { get; } = inner;

        public volatile bool Serving = true;

        public Func<HexLinkAiRequest, CancellationToken, IAsyncEnumerable<string>> Model { get; set; } = (_, cancel) => Say(cancel, Chunk("{\"content\":\"a\"}"));

        public HexLinkDevice Device => Inner.Device;
        public HexLinkWorkspace Workspace => Inner.Workspace;
        public HexHubSnapshot Fleet => Inner.Fleet;
        public IReadOnlyDictionary<int, HexLinkChatExtra> FleetExtras => Inner.FleetExtras;
        public Task<JsonObject?> Chat(int id, Func<HexAgentChat, JsonObject?> read) => Inner.Chat(id, read);
        public IMcpTools Tools => Inner.Tools;
        public Task<bool> Answer(int chat, string answer) => Inner.Answer(chat, answer);
        public void Presence(HexLinkDevice device, HexLinkPresence? presence) => Inner.Presence(device, presence);
        public Task<string> PatchOffered(HexLinkDevice device, string title, string diff) => Inner.PatchOffered(device, title, diff);
        public void Audit(string what, HexLinkDevice who, string detail) => Inner.Audit(what, who, detail);

        public bool ServesAi => Serving;

        public Task<IReadOnlyList<HexLinkAiModel>> AiModels(CancellationToken cancel) =>
            Task.FromResult<IReadOnlyList<HexLinkAiModel>>([new HexLinkAiModel("qwen", "Qwen 3.5", (int)(LocalModelCaps.Chat | LocalModelCaps.Tools), 8192, "9B · Q4")]);

        public IAsyncEnumerable<string> Ai(HexLinkDevice device, HexLinkAiRequest request, CancellationToken cancel) => Model(request, cancel);
    }

    private sealed class Linked : IDisposable
    {
        public AiServices Desktop { get; } = new(new LinkServices(DeviceA));
        public LinkServices Laptop { get; } = new(DeviceB);
        public HexLink DesktopLink { get; }
        public HexLink LaptopLink { get; }
        public HexLinkRights LaptopMay { get; set; }
        public HexLinkPeer Desk { get; private set; } = null!;

        private Linked(HexLinkRights laptopMay)
        {
            LaptopMay = laptopMay;
            DesktopLink = new HexLink(Desktop);
            LaptopLink = new HexLink(Laptop);
        }

        public static async Task<Linked> Up(HexLinkRights laptopMay, bool serving = true)
        {
            var linked = new Linked(laptopMay);
            linked.Desktop.Serving = serving;
            var (a, b) = Pair();
            Task<HexLinkPeer> laptop = linked.DesktopLink.Add(new HexLinkConnection(a, DeviceB, "memory", true, () => linked.LaptopMay));
            Task<HexLinkPeer> desk = linked.LaptopLink.Add(new HexLinkConnection(b, DeviceA, "memory", false, () => HexLinkRights.None));
            await laptop.WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS));
            linked.Desk = await desk.WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS));
            return linked;
        }

        public void Dispose()
        {
            DesktopLink.Dispose();
            LaptopLink.Dispose();
            Desktop.Inner.Dispose();
            Laptop.Dispose();
        }
    }

    private static async IAsyncEnumerable<string> Say([EnumeratorCancellation] CancellationToken cancel, params string[] chunks)
    {
        foreach (string chunk in chunks)
        {
            await Task.Yield();
            cancel.ThrowIfCancellationRequested();
            yield return chunk;
        }
    }

    private static async Task<List<string>> All(IAsyncEnumerable<string> stream)
    {
        var said = new List<string>();
        await foreach (string piece in stream)
            said.Add(piece);
        return said;
    }

    private static HexLinkAiRequest Asked => new("qwen", ASKED);

    [Fact]
    public async Task Chunks_stream_in_order_and_come_out_as_content_reasoning_and_tool_calls()
    {
        using Linked linked = await Linked.Up(HexLinkRights.Ai);
        string[] chunks =
        [
            Chunk("{\"reasoning_content\":\"hmm \"}"),
            Chunk("{\"content\":\"Hel\"}"),
            Chunk("{\"content\":\"lo\"}"),
            Chunk("{\"tool_calls\":[{\"index\":0,\"id\":\"c1\",\"function\":{\"name\":\"read\",\"arguments\":\"{\\\"p\"}}]}"),
            Chunk("{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"\\\":1}\"}}]}", "tool_calls"),
        ];
        HexLinkAiRequest? seen = null;
        linked.Desktop.Model = (request, cancel) =>
        {
            seen = request;
            return Say(cancel, chunks);
        };

        Assert.True(linked.Desk.ServesAi);
        Assert.Equal(chunks, await All(linked.Desk.Ai(Asked)).WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS)));
        // Bounded on the machine serving it: streaming forced, the tokens clamped, the model its own name for it
        JsonObject body = JsonNode.Parse(seen!.Body)!.AsObject();
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal(HexLinkAiRequest.MOST_TOKENS, body["max_tokens"]!.GetValue<int>());
        Assert.Equal("qwen", body["model"]!.GetValue<string>());
        Assert.Contains(linked.Desktop.Inner.Audited, line => line.What == "ai");

        // The agent side reads them as a local server's: thinking, text, one joined tool call
        LinkAiRouter.Find = fingerprint => fingerprint == DeviceA.Fingerprint ? linked.Desk : null;
        try
        {
            string id = LinkAiModelId.Make(DeviceA, "qwen");
            linked.Desktop.Model = (_, cancel) => Say(cancel, chunks);
            var agent = new LinkAgentChat(id, PredictionTuning.Usual, 200);
            var deltas = new List<Hex.Core.AgentDelta>();
            await foreach (Hex.Core.AgentDelta delta in agent.Chat([new AgentMessage("user", "hi")], [new ToolSpec("read", "reads", "{\"type\":\"object\"}")], default))
                deltas.Add(delta);
            Assert.Contains(deltas, delta => delta is Hex.Core.AgentDelta.Thinking { Value: "hmm " });
            Assert.Equal("Hello", string.Concat(deltas.OfType<Hex.Core.AgentDelta.Text>().Select(text => text.Value)));
            Hex.Core.AgentDelta.Call call = Assert.Single(deltas.OfType<Hex.Core.AgentDelta.Call>());
            Assert.Equal(("c1", "read", "{\"p\":1}"), (call.Value.Id, call.Value.Name, call.Value.ArgumentsJson));

            var streamed = new List<Hex.Core.ChatPiece>();
            await foreach (Hex.Core.ChatPiece piece in new LinkChatStreamer(id, PredictionTuning.Usual, 200).Stream([new ChatTurn("user", "hi")], default))
                streamed.Add(piece);
            Assert.Equal("Hello", string.Concat(streamed.Where(piece => !piece.Thinking).Select(piece => piece.Text)));
        }
        finally
        {
            LinkAiRouter.Find = null;
        }
    }

    // Against a real llama-server when HEX_LIVE_LLAMA (its address) and HEX_LIVE_LLAMA_MODEL are set, else nothing is asked
    [Fact]
    public async Task Live_a_real_model_answers_and_calls_a_tool_through_the_link()
    {
        if (Environment.GetEnvironmentVariable("HEX_LIVE_LLAMA") is not { Length: > 0 } server || Environment.GetEnvironmentVariable("HEX_LIVE_LLAMA_MODEL") is not { Length: > 0 } name)
            return;
        using Linked linked = await Linked.Up(HexLinkRights.Ai);
        linked.Desktop.Model = (request, cancel) => Forward(server, request, cancel);
        LinkAiRouter.Find = fingerprint => fingerprint == DeviceA.Fingerprint ? linked.Desk : null;
        try
        {
            string id = LinkAiModelId.Make(DeviceA, name);
            PredictionTuning plain = PredictionTuning.Usual with { Thinking = PredictionTuning.THINK_OFF };
            var said = new StringBuilder();
            await foreach (Hex.Core.ChatPiece piece in new LinkChatStreamer(id, plain, 64).Stream([new ChatTurn("user", "Reply with exactly the word: pong")], default))
                if (!piece.Thinking)
                    said.Append(piece.Text);
            Assert.Contains("pong", said.ToString(), StringComparison.OrdinalIgnoreCase);

            var tool = new ToolSpec("get_weather", "Gets the weather of a city", "{\"type\":\"object\",\"properties\":{\"city\":{\"type\":\"string\"}},\"required\":[\"city\"]}");
            var calls = new List<ToolCall>();
            await foreach (Hex.Core.AgentDelta delta in new LinkAgentChat(id, plain, 200).Chat([new AgentMessage("user", "What is the weather in Paris? Use the tool.")], [tool], default))
                if (delta is Hex.Core.AgentDelta.Call call)
                    calls.Add(call.Value);
            Assert.Equal("get_weather", Assert.Single(calls).Name);
            Assert.Contains("Paris", calls[0].ArgumentsJson, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            LinkAiRouter.Find = null;
        }
    }

    private static async IAsyncEnumerable<string> Forward(string server, HexLinkAiRequest request, [EnumeratorCancellation] CancellationToken cancel)
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        using var message = new HttpRequestMessage(HttpMethod.Post, server.TrimEnd('/') + "/v1/chat/completions") { Content = new StringContent(request.Body, Encoding.UTF8, "application/json") };
        using HttpResponseMessage response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancel));
        while (await reader.ReadLineAsync(cancel) is { } line)
            if (line.StartsWith("data:", StringComparison.Ordinal) && line[5..].Trim() is { Length: > 0 } data && data != "[DONE]")
                yield return data;
    }

    [Fact]
    public async Task A_cancel_reaches_the_model_at_once()
    {
        using Linked linked = await Linked.Up(HexLinkRights.Ai);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Cancelling = cancelled;
        linked.Desktop.Model = Run;

        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (string _ in linked.Desk.Ai(Asked, null, stop.Token))
                stop.Cancel();
        });
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async IAsyncEnumerable<string> Run(HexLinkAiRequest request, [EnumeratorCancellation] CancellationToken cancel)
    {
        yield return Chunk("{\"content\":\"first\"}");
        try
        {
            await Task.Delay(Timeout.Infinite, cancel);
        }
        finally
        {
            Cancelling?.TrySetResult();
        }
    }

    // The test running now's wait, one test uses it at a time
    private static TaskCompletionSource? Cancelling;

    [Fact]
    public async Task Without_the_right_or_the_setting_a_chat_is_refused_with_the_next_step()
    {
        using (Linked none = await Linked.Up(HexLinkRights.Predict))
        {
            Assert.False(none.Desk.ServesAi);
            HexLinkException refused = await Assert.ThrowsAsync<HexLinkException>(() => All(none.Desk.Ai(Asked)));
            Assert.Equal(HexLinkErrors.NOT_ALLOWED, refused.Code);
            Assert.Equal("ai", ((JsonObject)refused.Detail!)["needs"]!.GetValue<string>());
        }

        using Linked off = await Linked.Up(HexLinkRights.Ai, serving: false);
        Assert.False(off.Desk.ServesAi);
        HexLinkException notServed = await Assert.ThrowsAsync<HexLinkException>(() => All(off.Desk.Ai(Asked)));
        Assert.Equal(HexLinkAiRequest.REFUSED, HexLinkAiRequest.StatusOf(notServed));

        // The sentence a person reads says who and what to do
        Assert.Contains("stopped serving AI", LinkAiRouter.Failure(notServed, "Beta").Message);
        Assert.Contains("Beta", LinkAiRouter.Failure(notServed, "Beta").Message);

        // Taking the right away takes the serving with it, and a right given later is heard
        using Linked rights = await Linked.Up(HexLinkRights.Ai);
        Assert.True(rights.Desk.ServesAi);
        rights.LaptopMay = HexLinkRights.None;
        rights.DesktopLink.PredictionsMoved();
        Assert.True(Eventually(() => !rights.Desk.ServesAi));
    }

    [Fact]
    public async Task A_device_has_one_chat_running_the_rest_queue_in_order_and_are_told_their_place()
    {
        using Linked linked = await Linked.Up(HexLinkRights.Ai);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        linked.Desktop.Model = (request, cancel) => Hold(request, release, order, cancel);

        var places = new List<int>();
        Task<List<string>> first = All(linked.Desk.Ai(new HexLinkAiRequest("qwen", Marked("one"))));
        Assert.True(Eventually(() => order.Contains("one")));
        Task<List<string>> second = All(linked.Desk.Ai(new HexLinkAiRequest("qwen", Marked("two")), places.Add));
        // Each is let in as it reaches the host, so the next is asked once the one before is waiting there
        Assert.True(Eventually(() => places.Contains(2)));
        Task<List<string>> third = All(linked.Desk.Ai(new HexLinkAiRequest("qwen", Marked("three")), places.Add));
        Assert.True(Eventually(() => places.Contains(3)));
        Assert.DoesNotContain("two", order);

        // A fourth is one past what a device may have in flight
        HexLinkException busy = await Assert.ThrowsAsync<HexLinkException>(() => All(linked.Desk.Ai(new HexLinkAiRequest("qwen", Marked("four")))));
        Assert.Equal(HexLinkAiRequest.BUSY, HexLinkAiRequest.StatusOf(busy));

        release.SetResult();
        await Task.WhenAll(first, second, third).WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS));
        Assert.Equal(["one", "two", "three"], order);
    }

    private static string Marked(string name) => new JsonObject
    {
        ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = name }),
    }.ToJsonString();

    private static async IAsyncEnumerable<string> Hold(HexLinkAiRequest request, TaskCompletionSource release, List<string> order, [EnumeratorCancellation] CancellationToken cancel)
    {
        string name = JsonNode.Parse(request.Body)!["messages"]![0]!["content"]!.GetValue<string>();
        lock (order)
            order.Add(name);
        await release.Task.WaitAsync(cancel);
        yield return Chunk("{\"content\":\"" + name + "\"}");
    }

    [Fact]
    public async Task Stopping_the_serving_ends_a_running_chat_and_the_asker_is_told()
    {
        using Linked linked = await Linked.Up(HexLinkRights.Ai);
        Cancelling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        linked.Desktop.Model = Run;
        Task<List<string>> chat = All(linked.Desk.Ai(Asked));
        await Task.Delay(300);
        linked.Desktop.Serving = false;
        linked.DesktopLink.PredictionsMoved();
        await Cancelling.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Said as stopped serving, not as a cancel: the person is told who stopped and what to do
        HexLinkException ended = await Assert.ThrowsAsync<HexLinkException>(() => chat);
        Assert.Equal(HexLinkAiRequest.REFUSED, HexLinkAiRequest.StatusOf(ended));
        Assert.Contains("stopped serving AI", LinkAiRouter.Failure(ended, "Beta").Message);
    }

    [Fact]
    public void A_request_is_bounded_and_one_that_isnt_a_chat_is_refused()
    {
        JsonObject body = JsonNode.Parse(HexLinkAiRequest.Bound("{\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"n\":5,\"stream\":false}", "m"))!.AsObject();
        Assert.True(body["stream"]!.GetValue<bool>());
        Assert.Equal(1, body["n"]!.GetValue<int>());
        Assert.Equal(HexLinkAiRequest.USUAL_TOKENS, body["max_tokens"]!.GetValue<int>());
        Assert.Throws<HexLinkException>(() => HexLinkAiRequest.Bound("{\"messages\":[]}", "m"));
        Assert.Throws<HexLinkException>(() => HexLinkAiRequest.Bound("[1]", "m"));
        Assert.Throws<HexLinkException>(() => HexLinkAiRequest.Of(new JsonObject { ["model"] = "m", ["body"] = new string('x', HexLinkAiRequest.MOST_BODY + 1) }));
    }

    [Fact]
    public void Linked_models_group_under_their_device_and_say_where_they_run()
    {
        LocalModel linked = LinkedModel("link:AAAA-AAAA-AAAA-AAAA:qwen", "Qwen", "Beta");
        LocalModel own = new("file:c", "Local", LocalModelSource.File, "c", 1, LocalModelCaps.Chat, null, null, null, null, LocalModelFit.Fits, null);
        List<ModelPickerRow> rows = ModelPicker.Rows([own, linked], LocalAiRole.Chat, new HashSet<string>());
        ModelPickerRow row = Assert.Single(rows, each => each.Model.Source == LocalModelSource.Linked);
        Assert.Equal(ModelPickerGroup.Linked, row.Group);
        Assert.Null(row.Unsuitable);
        Assert.Contains("runs on Beta", row.Detail);
        Assert.Contains("Chat", string.Join(' ', row.Badges), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ModelPickerGroup.Computer, rows[0].Group);
        // It can chat but not predict
        Assert.Equal("cant_predict", ModelPicker.UnsuitableFor(linked, LocalAiRole.Predict));
    }

    private static LocalModel LinkedModel(string id, string name, string device) =>
        new(id, name, LocalModelSource.Linked, null, 0, LocalModelCaps.Chat | LocalModelCaps.Tools, 8192, "9B", null, null, LocalModelFit.Fits, null, Device: device);

    [Fact]
    public void With_no_model_here_the_one_serving_device_is_the_chat_model_and_a_chosen_one_wins()
    {
        LocalModelLibrary library = LocalModelLibrary.Shared;
        try
        {
            var settings = new HexSettings(null);
            Assert.Null(AiEditModels.LinkedFallback(settings));

            library.SetLinked([LinkedModel("link:AAAA-AAAA-AAAA-AAAA:qwen", "Qwen", "Beta")]);
            AiEditModel? fallback = AiEditModels.LinkedFallback(settings);
            Assert.NotNull(fallback);
            Assert.Equal(("linkai", "Beta", "link:AAAA-AAAA-AAAA-AAAA:qwen"), (fallback!.Provider, fallback.Device, fallback.Model));
            Assert.Equal("Beta", AiEditModels.Resolve(settings, out string? problem)?.Device);
            Assert.Null(problem);
            Assert.Equal("link:AAAA-AAAA-AAAA-AAAA:qwen", AiEditModels.ForAgent(settings, null)?.Model);

            // Two devices could answer: the person picks, nothing is chosen for them
            library.SetLinked([LinkedModel("link:AAAA-AAAA-AAAA-AAAA:qwen", "Qwen", "Beta"), LinkedModel("link:BBBB-BBBB-BBBB-BBBB:mellum", "Mellum", "Gamma")]);
            Assert.Null(AiEditModels.LinkedFallback(settings));
            Assert.Null(AiEditModels.Resolve(settings, out _));

            // Chosen for the chat role it is the one used, with the device it runs on
            settings.LocalAiChatModel = "link:BBBB-BBBB-BBBB-BBBB:mellum";
            AiEditModel? chosen = AiEditModels.Resolve(settings, out _);
            Assert.Equal(("linkai", "Gamma", "Mellum"), (chosen!.Provider, chosen.Device, chosen.Title));
            Assert.Equal("link:BBBB-BBBB-BBBB-BBBB:mellum", AiEditModels.ForAgent(settings, null)!.Model);
        }
        finally
        {
            library.SetLinked([]);
        }
    }

    [Fact]
    public void A_linked_models_id_names_its_device_and_the_model_which_may_have_colons()
    {
        Assert.True(LinkAiModelId.TryParse("link:AAAA-BBBB-CCCC-DDDD:qwen3:14b", out string fingerprint, out string model));
        Assert.Equal(("AAAA-BBBB-CCCC-DDDD", "qwen3:14b"), (fingerprint, model));
        Assert.False(LinkAiModelId.TryParse("ollama:qwen", out _, out _));
        Assert.False(LinkAiModelId.TryParse("link:nomodel:", out _, out _));
    }

    [Fact]
    public async Task The_loopback_answers_only_with_its_token_and_not_a_browser()
    {
        using var endpoint = new LinkAiLoopback(() => [new LinkAiLoopbackModel("link:AAAA-AAAA-AAAA-AAAA:qwen", "Beta")]);
        Assert.StartsWith("http://127.0.0.1:", endpoint.Url);
        Assert.Equal(32 + 16, endpoint.Token.Length);

        Assert.Equal(401, await Status(endpoint, "GET", "/v1/models", null));
        Assert.Equal(401, await Status(endpoint, "GET", "/v1/models", "Bearer wrong"));
        Assert.Equal(403, await Status(endpoint, "GET", "/v1/models", "Bearer " + endpoint.Token, "Origin: http://evil.example\r\n"));
        Assert.Equal(403, await Status(endpoint, "GET", "/v1/models", "Bearer " + endpoint.Token, host: "evil.example"));
        Assert.Equal(404, await Status(endpoint, "GET", "/nothing", "Bearer " + endpoint.Token));

        using var http = new HttpClient();
        using var asked = new HttpRequestMessage(HttpMethod.Get, endpoint.Url + "/models");
        asked.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", endpoint.Token);
        using HttpResponseMessage answer = await http.SendAsync(asked);
        Assert.True(answer.IsSuccessStatusCode);
        JsonObject listed = JsonNode.Parse(await answer.Content.ReadAsStringAsync())!.AsObject();
        Assert.Equal("link:AAAA-AAAA-AAAA-AAAA:qwen", listed["data"]![0]!["id"]!.GetValue<string>());

        // A chat for a device that isn't linked is said so, not left hanging
        LinkAiRouter.Find = null;
        Assert.Equal(503, await Status(endpoint, "POST", "/v1/chat/completions", "Bearer " + endpoint.Token, body: "{\"model\":\"link:AAAA-AAAA-AAAA-AAAA:qwen\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]}"));
    }

    [Fact]
    public async Task A_chat_through_the_loopback_streams_what_the_linked_model_says()
    {
        using Linked linked = await Linked.Up(HexLinkRights.Ai);
        linked.Desktop.Model = (_, cancel) => Say(cancel, Chunk("{\"content\":\"yes\"}"), Chunk("{}", "stop"));
        LinkAiRouter.Find = fingerprint => fingerprint == DeviceA.Fingerprint ? linked.Desk : null;
        try
        {
            using var endpoint = new LinkAiLoopback(() => []);
            using var http = new HttpClient();
            using var asked = new HttpRequestMessage(HttpMethod.Post, endpoint.Url + "/chat/completions")
            {
                Content = new StringContent("{\"model\":\"" + LinkAiModelId.Make(DeviceA, "qwen") + "\",\"stream\":true,\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]}", Encoding.UTF8, "application/json")
            };
            asked.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", endpoint.Token);
            using HttpResponseMessage answer = await http.SendAsync(asked);
            string text = await answer.Content.ReadAsStringAsync();
            Assert.Contains("\"content\":\"yes\"", text);
            Assert.Contains("data: [DONE]", text);

            // And the same, whole, for a client that didn't ask for a stream
            using var whole = new HttpRequestMessage(HttpMethod.Post, endpoint.Url + "/chat/completions")
            {
                Content = new StringContent("{\"model\":\"" + LinkAiModelId.Make(DeviceA, "qwen") + "\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]}", Encoding.UTF8, "application/json")
            };
            whole.Headers.Authorization = asked.Headers.Authorization;
            using HttpResponseMessage joined = await http.SendAsync(whole);
            Assert.Equal("yes", JsonNode.Parse(await joined.Content.ReadAsStringAsync())!["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        }
        finally
        {
            LinkAiRouter.Find = null;
        }
    }

    private static async Task<int> Status(LinkAiLoopback endpoint, string method, string path, string? authorization, string extra = "", string? host = null, string body = "")
    {
        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", endpoint.Port);
        using NetworkStream stream = client.GetStream();
        string request = $"{method} {path} HTTP/1.1\r\nHost: {host ?? "127.0.0.1:" + endpoint.Port}\r\n{(authorization is null ? "" : "Authorization: " + authorization + "\r\n")}{extra}Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n\r\n{body}";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(request));
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        return int.Parse(line!.Split(' ')[1]);
    }
}
