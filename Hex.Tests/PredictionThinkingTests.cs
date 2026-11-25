using System.Net;
using System.Text.Json;
using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// The model's tuning and thinking models: what the providers send for the settings, thinking taken out of a prediction
/// however it arrives, and Ollama's chat with what the model thought kept beside its answer.
/// </summary>
public sealed class PredictionThinkingTests
{
    private static readonly PredictionWireRequest raw = new("PROMPT", null, 77, ["\n\n"], "qwen");
    private static readonly PredictionWireRequest templated = new("PROMPT", "SUFFIX", 77, ["\n\n"], "qwen");

    private static OllamaProvider Ollama(FakeServer server, PredictionTuning tuning, string system = "") =>
        new(new HttpClient(server), PredictionProviders.OllamaUrl("http://localhost:11434")!, "qwen3:" + Guid.NewGuid())
        {
            FirstPiece = TimeSpan.FromSeconds(5), Loading = TimeSpan.FromSeconds(5), Tuning = tuning, SystemPrompt = system,
        };

    private static async Task<string> Read(IPredictionProvider provider, PredictionWireRequest request)
    {
        var text = new System.Text.StringBuilder();
        await foreach (string piece in provider.Complete(request, default))
            text.Append(piece);
        return text.ToString();
    }

    // A server that says what /api/show says the model can do, and streams the generate lines given
    private static FakeServer Server(string[] lines, bool thinks = true) => new(request =>
    {
        if (request.RequestUri!.AbsolutePath.EndsWith("/show", StringComparison.Ordinal))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(thinks ? "{\"capabilities\":[\"completion\",\"thinking\"]}" : "{\"capabilities\":[\"completion\"]}")
            });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new PieceStream(lines)) });
    });

    private static JsonElement Generate(FakeServer server)
    {
        string body = server.Sent.Single(sent => sent.Request.RequestUri!.AbsolutePath.EndsWith("/generate", StringComparison.Ordinal)).Body;
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Theory]
    [InlineData(new[] { "<think>", "plan it", "</think>", "\n\nanswer" }, "answer")]
    [InlineData(new[] { "<thi", "nk>pla", "n</th", "ink>", "\nans", "wer" }, "answer")]
    [InlineData(new[] { "a < b and <thinking", " isn't a tag" }, "a < b and <thinking isn't a tag")]
    [InlineData(new[] { "before<THINK>x</THINK>after" }, "beforeafter")]
    [InlineData(new[] { "<think>never closed" }, "")]
    [InlineData(new[] { "ends on <th" }, "ends on <th")]
    public void Thinking_is_taken_out_however_the_pieces_fall(string[] pieces, string expected)
    {
        var filter = new ThinkFilter();
        string shown = string.Concat(pieces.Select(filter.Feed)) + filter.End();
        Assert.Equal(expected, shown);
    }

    [Fact]
    public async Task The_tuning_is_sent_and_what_is_left_at_zero_is_not()
    {
        var server = Server(["{\"response\":\"x\",\"done\":true}\n"]);
        await Read(Ollama(server, new PredictionTuning(0.7f, 0.9f, 40, 8192, 1.1f)), raw);

        JsonElement options = Generate(server).GetProperty("options");
        Assert.Equal(0.7, options.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(0.9, options.GetProperty("top_p").GetDouble(), 3);
        Assert.Equal(40, options.GetProperty("top_k").GetInt32());
        Assert.Equal(8192, options.GetProperty("num_ctx").GetInt32());
        Assert.Equal(1.1, options.GetProperty("repeat_penalty").GetDouble(), 3);

        server = Server(["{\"response\":\"x\",\"done\":true}\n"]);
        await Read(Ollama(server, PredictionTuning.Usual), raw);
        options = Generate(server).GetProperty("options");
        Assert.Equal(0, options.GetProperty("temperature").GetDouble());
        Assert.False(options.TryGetProperty("top_p", out _));
        Assert.False(options.TryGetProperty("top_k", out _));
        Assert.False(options.TryGetProperty("num_ctx", out _));
        Assert.False(options.TryGetProperty("repeat_penalty", out _));
    }

    [Fact]
    public async Task A_raw_prompt_gets_no_system_prompt_or_think_and_a_templated_one_gets_both()
    {
        var server = Server(["{\"response\":\"x\",\"done\":true}\n"]);
        await Read(Ollama(server, PredictionTuning.Usual with { Thinking = "off" }, "Be terse."), raw);
        JsonElement body = Generate(server);
        Assert.False(body.TryGetProperty("system", out _));
        Assert.False(body.TryGetProperty("think", out _));

        server = Server(["{\"response\":\"x\",\"done\":true}\n"]);
        await Read(Ollama(server, PredictionTuning.Usual with { Thinking = "off" }, "Be terse."), templated);
        body = Generate(server);
        Assert.Equal("Be terse.", body.GetProperty("system").GetString());
        Assert.False(body.GetProperty("think").GetBoolean());
    }

    [Fact]
    public async Task Think_is_never_sent_to_a_model_that_cannot_think()
    {
        var server = Server(["{\"response\":\"x\",\"done\":true}\n"], thinks: false);
        await Read(Ollama(server, PredictionTuning.Usual with { Thinking = "on" }), templated);
        Assert.False(Generate(server).TryGetProperty("think", out _));
    }

    [Fact]
    public async Task Asked_to_think_a_prediction_gets_a_budget_and_its_stops_are_kept_after_the_thinking()
    {
        // The stop ("\n\n") comes inside the thinking and must not end the answer, then again after it, where it does
        var server = Server([
            "{\"response\":\"\",\"thinking\":\"step one\\n\\nstep two\",\"done\":false}\n",
            "{\"response\":\"<think>also\\n\\ninline</think>\",\"done\":false}\n",
            "{\"response\":\"int x = 1;\",\"done\":false}\n",
            "{\"response\":\"\\n\\nnot this\",\"done\":false}\n",
            "{\"response\":\"\",\"done\":true}\n",
        ]);
        string shown = await Read(Ollama(server, PredictionTuning.Usual with { Thinking = "on" }), templated);

        Assert.Equal("int x = 1;", shown);
        JsonElement body = Generate(server);
        Assert.True(body.GetProperty("think").GetBoolean());
        Assert.Equal(77 + OllamaProvider.THINKING_BUDGET, body.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.False(body.GetProperty("options").TryGetProperty("stop", out _));
    }

    [Fact]
    public async Task OpenAI_completions_take_out_a_think_block_too()
    {
        var server = FakeServer.Streaming(new PieceStream([
            "data: {\"choices\":[{\"text\":\"<think>hm\"}]}\n\n",
            "data: {\"choices\":[{\"text\":\"m</think>\\nreturn 1;\"}]}\n\n",
            "data: [DONE]\n\n",
        ]));
        var provider = new OpenAiProvider(new HttpClient(server), PredictionProviders.OpenAiUrl("http://localhost:1/v1")!, "m", null)
        {
            Tuning = new PredictionTuning(0.5f, 0.8f),
        };

        Assert.Equal("return 1;", await Read(provider, raw));
        using var json = JsonDocument.Parse(Assert.Single(server.Sent).Body);
        Assert.Equal(0.5, json.RootElement.GetProperty("temperature").GetDouble(), 3);
        Assert.Equal(0.8, json.RootElement.GetProperty("top_p").GetDouble(), 3);
    }

    [Fact]
    public void Tuning_is_held_to_its_ends()
    {
        PredictionTuning held = new PredictionTuning(9, -1, 999, -5, float.NaN, "lots").Clamped();
        Assert.Equal(new PredictionTuning(2, 0, 200, 0, 0, PredictionTuning.THINK_MODEL), held);
    }

    [Fact]
    public async Task Ollamas_chat_keeps_what_the_model_thought_beside_its_answer()
    {
        var server = new FakeServer(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/show", StringComparison.Ordinal)
                ? "{\"capabilities\":[\"completion\",\"thinking\"]}"
                : "{\"message\":{\"role\":\"assistant\",\"content\":\"Four.\",\"thinking\":\"2 + 2 is 4.\"},\"done\":true}")
        }));
        var chat = new OllamaChat(new HttpClient(server), OllamaChat.Url("http://localhost:11434")!, "qwen3:" + Guid.NewGuid())
        {
            Tuning = PredictionTuning.Usual with { Thinking = "on", Temperature = 0.6f },
        };

        ChatAnswer answer = await chat.Ask([new ChatTurn("system", "Be brief."), new ChatTurn("user", "Ann: 2 + 2?")], default);

        Assert.Equal(new ChatAnswer("Four.", "2 + 2 is 4."), answer);
        var (sent, body) = server.Sent.Single(each => each.Request.RequestUri!.AbsolutePath == "/api/chat");
        using var json = JsonDocument.Parse(body);
        Assert.True(json.RootElement.GetProperty("think").GetBoolean());
        Assert.False(json.RootElement.GetProperty("stream").GetBoolean());
        Assert.Equal(["system", "user"], json.RootElement.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()));
    }

    [Theory]
    [InlineData("http://localhost:8080", "http://localhost:8080/v1/chat/completions")]
    [InlineData("http://localhost:8080/v1", "http://localhost:8080/v1/chat/completions")]
    [InlineData("http://localhost:8080/v1/completions", "http://localhost:8080/v1/chat/completions")]
    [InlineData("http://localhost:8080/v1/chat/completions", "http://localhost:8080/v1/chat/completions")]
    public void The_chat_URL_is_beside_the_completions_one(string written, string expected) =>
        Assert.Equal(expected, OpenAiChat.Url(written)!.ToString());

    [Fact]
    public async Task An_OpenAI_compatible_chat_keeps_llama_servers_reasoning_and_sends_only_what_was_tuned()
    {
        var server = FakeServer.Answering(HttpStatusCode.OK,
            "{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Four.\",\"reasoning_content\":\"2 + 2 = 4\"}}]}");
        var chat = new OpenAiChat(new HttpClient(server), OpenAiChat.Url("http://localhost:8080/v1/completions")!, "qwen3-coder-next", "secret")
        {
            Tuning = new PredictionTuning(0.7f, 0.95f, 40, 0, 0, "off"),
        };

        Assert.Equal(new ChatAnswer("Four.", "2 + 2 = 4"), await chat.Ask([new ChatTurn("user", "Ann: 2 + 2?")], default));
        var (sent, body) = Assert.Single(server.Sent);
        Assert.Equal("Bearer", sent.Headers.Authorization?.Scheme);
        using var json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;
        Assert.False(root.GetProperty("stream").GetBoolean());
        Assert.Equal(40, root.GetProperty("top_k").GetInt32());
        Assert.False(root.TryGetProperty("repeat_penalty", out _));
        Assert.False(root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());

        server = FakeServer.Answering(HttpStatusCode.OK, "{\"choices\":[{\"message\":{\"content\":\"ok\"}}]}");
        await new OpenAiChat(new HttpClient(server), OpenAiChat.Url("http://localhost:8080")!, "m", null).Ask([new ChatTurn("user", "q")], default);
        using var plain = JsonDocument.Parse(Assert.Single(server.Sent).Body);
        Assert.False(plain.RootElement.TryGetProperty("top_k", out _));
        Assert.False(plain.RootElement.TryGetProperty("chat_template_kwargs", out _));
    }

    [Fact]
    public void A_think_block_in_the_answer_becomes_the_thinking()
    {
        Assert.Equal(new ChatAnswer("Yes.", "Let me see."), OllamaChat.Answer("<think>\nLet me see.\n</think>\n\nYes."));
        Assert.Equal(new ChatAnswer("Yes.", "apart\n\ninline"), OllamaChat.Answer("<think>inline</think>Yes.", "apart"));
        Assert.Equal(new ChatAnswer("Plain."), OllamaChat.Answer("Plain."));
    }

    [Fact]
    public async Task A_level_a_model_refuses_is_asked_again_with_thinking_on()
    {
        int chats = 0;
        var server = new FakeServer(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/show", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"capabilities\":[\"thinking\"]}") });
            return Task.FromResult(++chats == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{\"error\":\"think value \\\"high\\\" is not supported for this model\"}") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"message\":{\"content\":\"ok\"}}") });
        });
        var chat = new OllamaChat(new HttpClient(server), OllamaChat.Url("http://localhost:11434")!, "m" + Guid.NewGuid())
        {
            Tuning = PredictionTuning.Usual with { Thinking = "high" },
        };

        Assert.Equal("ok", (await chat.Ask([new ChatTurn("user", "q")], default)).Text);
        string[] thinks = [.. server.Sent.Where(each => each.Request.RequestUri!.AbsolutePath == "/api/chat")
            .Select(each => JsonDocument.Parse(each.Body).RootElement.GetProperty("think").ToString())];
        Assert.Equal(["high", "True"], thinks);
    }
}
