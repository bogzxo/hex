using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Hex.Core;

namespace Hex.Tests;

/// <summary>Ollama's API and OpenAI's against a server in the test: what is sent, what is read, and every way it fails.</summary>
public sealed class PredictionProviderTests
{
    private const string KEY = "sk-test-0123456789-secret";
    private static readonly PredictionWireRequest request = new("PROMPT", null, 77, ["\n", "<|endoftext|>"], "qwen");

    private static async Task<List<string>> Read(IPredictionProvider provider, CancellationToken cancel = default)
    {
        var pieces = new List<string>();
        await foreach (string piece in provider.Complete(request, cancel))
            pieces.Add(piece);
        return pieces;
    }

    private static async Task<PredictionException> Failing(IPredictionProvider provider) =>
        await Assert.ThrowsAsync<PredictionException>(() => Read(provider));

    private static OllamaProvider Ollama(FakeServer server, string model = "zeta") =>
        new(new HttpClient(server), PredictionProviders.OllamaUrl("http://localhost:11434")!, model + Guid.NewGuid())
        {
            FirstPiece = TimeSpan.FromSeconds(5),
            Loading = TimeSpan.FromSeconds(5),
        };

    private static OpenAiProvider OpenAi(FakeServer server, string? key = KEY) =>
        new(new HttpClient(server), PredictionProviders.OpenAiUrl("https://example.com/v1")!, "model", key);

    [Fact]
    public async Task Ollama_is_sent_the_prompt_raw_streamed_with_its_options()
    {
        var server = FakeServer.Streaming(new PieceStream(["{\"response\":\"a\",\"done\":false}\n", "{\"response\":\"\",\"done\":true}\n"]));

        await Read(Ollama(server));

        var (sent, body) = Assert.Single(server.Sent);
        Assert.Equal("http://localhost:11434/api/generate", sent.RequestUri!.ToString());
        using var json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;
        Assert.Equal("PROMPT", root.GetProperty("prompt").GetString());
        Assert.True(root.GetProperty("raw").GetBoolean());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal("30m", root.GetProperty("keep_alive").GetString());
        Assert.Equal(77, root.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal(0, root.GetProperty("options").GetProperty("temperature").GetInt32());
        Assert.Equal(["\n", "<|endoftext|>"], root.GetProperty("options").GetProperty("stop").EnumerateArray().Select(e => e.GetString()));
        Assert.Null(sent.Headers.Authorization);
    }

    [Fact]
    public async Task Ollamas_lines_of_json_are_read_even_split_across_reads()
    {
        var server = FakeServer.Streaming(new PieceStream([
            "{\"respon", "se\":\"Hel\",\"done\":false}\n{\"response\":\"lo", "\",\"done\":false}\n",
            "{\"response\":\"\",\"done\":true,\"done_reason\":\"stop\",\"eval_count\":2}\n",
        ]));

        Assert.Equal(["Hel", "lo"], await Read(Ollama(server)));
    }

    [Fact]
    public async Task OpenAi_is_sent_its_body_and_the_key_as_a_bearer_token()
    {
        var server = FakeServer.Streaming(new PieceStream(["data: [DONE]\n\n"]));

        await Read(OpenAi(server));

        var (sent, body) = Assert.Single(server.Sent);
        Assert.Equal("https://example.com/v1/completions", sent.RequestUri!.ToString());
        Assert.Equal("Bearer", sent.Headers.Authorization!.Scheme);
        Assert.Equal(KEY, sent.Headers.Authorization.Parameter);
        using var json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;
        Assert.Equal("model", root.GetProperty("model").GetString());
        Assert.Equal("PROMPT", root.GetProperty("prompt").GetString());
        Assert.Equal(77, root.GetProperty("max_tokens").GetInt32());
        Assert.True(root.GetProperty("stream").GetBoolean());
        Assert.Equal(2, root.GetProperty("stop").GetArrayLength());
        Assert.DoesNotContain(KEY, body);
    }

    [Fact]
    public async Task OpenAi_sends_no_authorization_without_a_key()
    {
        var server = FakeServer.Streaming(new PieceStream(["data: [DONE]\n"]));

        await Read(OpenAi(server, null));

        Assert.Null(server.Sent[0].Request.Headers.Authorization);
    }

    [Fact]
    public async Task Server_sent_events_are_read_to_done_even_split_across_reads()
    {
        var server = FakeServer.Streaming(new PieceStream([
            ": a comment\n\ndata: {\"choices\":[{\"text\":\"fo", "o\",\"finish_reason\":null}]}\n\n",
            "data: {\"choices\":[{\"text\":\"bar\",\"finish_reason\":null}]}\n\ndata: [DONE]\n\n",
            "data: {\"choices\":[{\"text\":\"after the end\",\"finish_reason\":null}]}\n\n",
        ]));

        Assert.Equal(["foo", "bar"], await Read(OpenAi(server)));
    }

    [Fact]
    public async Task A_model_ollama_hasnt_got_is_no_such_model()
    {
        var failure = await Failing(Ollama(FakeServer.Answering(HttpStatusCode.NotFound, "{\"error\":\"model 'zeta' not found\"}")));

        Assert.Equal(PredictionStatus.NoSuchModel, failure.Status);
        Assert.Contains("model 'zeta' not found", failure.Message);
    }

    [Fact]
    public async Task A_missing_model_names_the_model_what_the_server_has_and_the_models_folder()
    {
        var server = new FakeServer(request => Task.FromResult(request.RequestUri!.AbsolutePath == "/api/tags"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"models\":[{\"name\":\"qwen:7b\"},{\"name\":\"llama3\"}]}") }
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"model 'zeta' not found\"}") }));
        var failure = await Failing(Ollama(server));

        Assert.Equal(PredictionStatus.NoSuchModel, failure.Status);
        Assert.Contains("zeta", failure.Message);
        Assert.Contains("qwen:7b, llama3", failure.Message);
        Assert.Contains("OLLAMA_MODELS", failure.Message);
    }

    [Fact]
    public async Task A_server_with_no_models_at_all_says_so()
    {
        var server = new FakeServer(request => Task.FromResult(request.RequestUri!.AbsolutePath == "/api/tags"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"models\":[]}") }
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"model 'zeta' not found\"}") }));
        var failure = await Failing(Ollama(server));

        Assert.Contains("no models at all", failure.Message);
        Assert.Contains("OLLAMA_MODELS", failure.Message);
    }

    [Fact]
    public async Task Preloading_sends_the_model_and_a_keep_alive_and_no_prompt()
    {
        var server = FakeServer.Answering(HttpStatusCode.OK, "{\"done\":true}");
        var provider = Ollama(server);

        Assert.Null(await provider.Preload(default));

        string body = Assert.Single(server.Sent).Body;
        Assert.Contains("\"keep_alive\":\"30m\"", body);
        Assert.DoesNotContain("prompt", body);
    }

    [Fact]
    public async Task Preloading_a_model_the_server_hasnt_got_says_so()
    {
        var provider = Ollama(FakeServer.Answering(HttpStatusCode.NotFound, "{\"error\":\"model 'zeta' not found\"}"));

        PredictionException? problem = await provider.Preload(default);

        Assert.Equal(PredictionStatus.NoSuchModel, problem?.Status);
        Assert.Contains("OLLAMA_MODELS", problem?.Message);
    }
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_refused_key_is_unauthorized_and_never_in_the_message(HttpStatusCode code)
    {
        var failure = await Failing(OpenAi(FakeServer.Answering(code, "{\"error\":{\"message\":\"Incorrect API key provided: " + KEY + "\",\"type\":\"invalid_request_error\"}}")));

        Assert.Equal(PredictionStatus.Unauthorized, failure.Status);
        Assert.DoesNotContain(KEY, failure.Message);
        Assert.DoesNotContain("PROMPT", failure.Message);
    }

    [Fact]
    public async Task A_server_error_is_a_bad_answer_with_the_servers_message_less_the_key()
    {
        var failure = await Failing(OpenAi(FakeServer.Answering(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"broke near " + KEY + "\"}}")));

        Assert.Equal(PredictionStatus.BadAnswer, failure.Status);
        Assert.Contains("500", failure.Message);
        Assert.Contains("broke near", failure.Message);
        Assert.DoesNotContain(KEY, failure.Message);
    }

    [Fact]
    public async Task An_error_in_the_middle_of_the_stream_ends_it()
    {
        var server = FakeServer.Streaming(new PieceStream(["{\"response\":\"a\",\"done\":false}\n", "{\"error\":\"out of memory\"}\n"]));

        var failure = await Failing(Ollama(server));

        Assert.Equal(PredictionStatus.BadAnswer, failure.Status);
        Assert.Contains("out of memory", failure.Message);
    }

    [Fact]
    public async Task An_answer_that_isnt_json_is_a_bad_answer()
    {
        var failure = await Failing(Ollama(FakeServer.Streaming(new PieceStream(["<html>proxy</html>\n"]))));

        Assert.Equal(PredictionStatus.BadAnswer, failure.Status);
    }

    [Fact]
    public async Task A_server_that_isnt_listening_is_unreachable()
    {
        var server = new FakeServer(_ => throw new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused)));

        var failure = await Failing(Ollama(server));

        Assert.Equal(PredictionStatus.Unreachable, failure.Status);
        Assert.Contains("localhost:11434", failure.Message);
    }

    [Fact]
    public async Task A_connection_timeout_before_the_request_deadline_is_unreachable()
    {
        var server = new FakeServer(_ => throw new TaskCanceledException("Connection timed out.", new TimeoutException()));

        var failure = await Failing(Ollama(server));

        Assert.Equal(PredictionStatus.Unreachable, failure.Status);
        Assert.Contains("localhost:11434", failure.Message);
    }

    [Fact]
    public async Task A_server_that_stalls_past_the_deadline_has_timed_out()
    {
        var stream = new PieceStream(["{\"response\":\"a\",\"done\":false}\n"], stall: true);
        var provider = new OllamaProvider(new HttpClient(FakeServer.Streaming(stream)), new Uri("http://localhost:1/api/generate"), "m" + Guid.NewGuid())
        {
            FirstPiece = TimeSpan.FromMilliseconds(100),
            Loading = TimeSpan.FromMilliseconds(100),
        };

        var failure = await Failing(provider);

        Assert.Equal(PredictionStatus.TimedOut, failure.Status);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Cancelling_closes_the_response()
    {
        var stream = new PieceStream(["data: {\"choices\":[{\"text\":\"a\",\"finish_reason\":null}]}\n"], stall: true);
        using var cancel = new CancellationTokenSource();
        var pieces = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (string piece in OpenAi(FakeServer.Streaming(stream)).Complete(request, cancel.Token))
            {
                pieces.Add(piece);
                cancel.Cancel();
            }
        });

        Assert.Equal(["a"], pieces);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Leaving_the_stream_early_closes_the_response()
    {
        var stream = new PieceStream(["{\"response\":\"a\",\"done\":false}\n"], stall: true);

        await foreach (string _ in Ollama(FakeServer.Streaming(stream)).Complete(request, default))
            break;

        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData("http://localhost:11434", "http://localhost:11434/api/generate")]
    [InlineData("http://localhost:11434/", "http://localhost:11434/api/generate")]
    [InlineData("localhost:11434", "http://localhost:11434/api/generate")]
    [InlineData("http://box:11434/api/generate", "http://box:11434/api/generate")]
    public void Ollamas_url_has_its_api_added(string written, string expected)
    {
        Assert.Equal(expected, PredictionProviders.OllamaUrl(written)!.ToString());
    }

    [Theory]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1/completions")]
    [InlineData("http://localhost:11434/v1/completions", "http://localhost:11434/v1/completions")]
    [InlineData("http://localhost:8080", "http://localhost:8080/v1/completions")]
    [InlineData("https://openrouter.ai/api/v1/", "https://openrouter.ai/api/v1/completions")]
    [InlineData("https://codestral.mistral.ai/v1/fim/completions", "https://codestral.mistral.ai/v1/fim/completions")]
    public void OpenAis_url_has_completions_added(string written, string expected)
    {
        Assert.Equal(expected, PredictionProviders.OpenAiUrl(written)!.ToString());
    }

    [Fact]
    public void Urls_that_arent_http_make_no_provider()
    {
        Assert.Null(PredictionProviders.OllamaUrl("ftp://x"));
        Assert.Null(PredictionProviders.OpenAiUrl(""));
        Assert.Null(PredictionProviders.Make(PredictionTestKit.Options() with { ApiUrl = "file:///c:/x" }));
        Assert.Null(PredictionProviders.Make(PredictionTestKit.Options() with { Model = " " }));
    }

    [Fact]
    public void The_settings_make_the_provider_they_name()
    {
        Assert.IsType<OllamaProvider>(PredictionProviders.Make(PredictionTestKit.Options()));
        var openAi = Assert.IsType<OpenAiProvider>(PredictionProviders.Make(PredictionTestKit.Options() with { Provider = "openai", ApiUrl = "http://localhost:11434/v1" }));
        Assert.Equal("http://localhost:11434/v1/completions", openAi.Api.ToString());
        Assert.IsType<LinkProvider>(PredictionProviders.Make(PredictionTestKit.Options() with { Provider = "link", Model = "" }));
        Assert.Null(PredictionProviders.Make(PredictionTestKit.Options() with { Provider = "copilot" }));
    }

    [Theory]
    [InlineData("http://localhost:11434", true)]
    [InlineData("http://127.0.0.2:1", true)]
    [InlineData("http://[::1]:1", true)]
    [InlineData("http://192.168.1.5:11434", false)]
    [InlineData("https://api.openai.com/v1", false)]
    public void Only_this_machine_is_local(string url, bool local)
    {
        Assert.Equal(local, PredictionProviders.IsLocal(new Uri(url)));
    }

    [Fact]
    public void The_options_never_write_the_key()
    {
        Assert.DoesNotContain(KEY, (PredictionTestKit.Options() with { ApiKey = KEY }).ToString());
    }
}
