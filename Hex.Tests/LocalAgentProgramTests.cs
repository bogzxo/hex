using System.Net;
using System.Text.Json.Nodes;

using Hex.Core;
using Hex.Dawn;

namespace Hex.Tests;

/// <summary>Hex --local-agent's parts that need no Hex, model or pipe: its arguments, the options file, the stdin router and the --models JSON.</summary>
public sealed class LocalAgentProgramTests
{
    [Fact]
    public void Arguments_are_read_by_name()
    {
        LocalAgentArgs args = LocalAgentProgram.ParseArgs(["--local-agent", "--folder", "C:/work", "--agent-chat", "7", "--options", "o.json", "--resume", "s1"]);

        Assert.Equal("C:/work", args.Folder);
        Assert.Equal(7, args.ChatId);
        Assert.Equal("o.json", args.OptionsFile);
        Assert.Equal("s1", args.Resume);
        Assert.False(args.Models);
    }

    [Fact]
    public void Models_alone_and_no_folder_means_the_current_one()
    {
        LocalAgentArgs args = LocalAgentProgram.ParseArgs(["--local-agent", "--models"]);

        Assert.True(args.Models);
        Assert.Equal(Environment.CurrentDirectory, args.Folder);
        Assert.Null(args.Resume);
        Assert.Null(args.OptionsFile);
        Assert.Equal(0, args.ChatId);
    }

    [Fact]
    public void The_options_file_gives_model_approvals_and_thinking()
    {
        LocalAgentFileOptions options = LocalAgentProgram.ParseOptions("""{"model":" qwen3.5:9b ","approvals":"ASK","think":"off"}""");

        Assert.Equal("qwen3.5:9b", options.Model);
        Assert.Equal("ask", options.Approvals);
        Assert.Equal("off", options.Think);
    }

    [Theory]
    [InlineData("""{"profile":"ASK"}""", "ask")]
    [InlineData("""{"profile":"minimal"}""", "minimal")]
    [InlineData("""{"profile":"write"}""", "write")]
    [InlineData("""{"profile":"nonsense"}""", "write")]
    [InlineData("{}", "write")]
    public void The_options_file_gives_the_profile_and_write_is_the_usual(string json, string expected) =>
        Assert.Equal(expected, LocalAgentProgram.ParseOptions(json).Profile);

    [Theory]
    [InlineData("""{"model":"","approvals":"nonsense","think":"maybe"}""")]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("{}")]
    public void Options_that_say_nothing_usable_are_the_usual(string json)
    {
        LocalAgentFileOptions options = LocalAgentProgram.ParseOptions(json);

        Assert.Null(options.Model);
        Assert.Equal("edits", options.Approvals);
        Assert.Equal("model", options.Think);
    }

    [Fact]
    public void A_missing_options_file_is_the_usual_options()
    {
        Assert.Equal(LocalAgentFileOptions.Usual, LocalAgentProgram.ReadOptionsFile(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".json")));
        Assert.Equal(LocalAgentFileOptions.Usual, LocalAgentProgram.ReadOptionsFile(null));
    }

    [Fact]
    public async Task The_router_queues_messages_answers_approvals_and_ends_on_stop()
    {
        var router = new LocalAgentInputRouter();
        string input = string.Join('\n',
            """{"type":"message","text":"first"}""",
            """{"type":"approval","id":"c1","allow":true}""",
            "garbage",
            """{"type":"message","text":"second"}""",
            """{"type":"stop"}""",
            """{"type":"message","text":"after stop"}""");

        await router.ReadAsync(new StringReader(input));

        Assert.True(router.Stopped.IsCancellationRequested);
        var messages = new List<string>();
        await foreach (string message in router.Messages.ReadAllAsync())
            messages.Add(message);
        Assert.Equal(["first", "second"], messages);

        // The approval came before anyone waited for it, and is there when they do
        Assert.True(await router.AskAsync("c1", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task An_approval_is_waited_for_until_it_comes_and_a_stop_says_no()
    {
        var router = new LocalAgentInputRouter();
        Task<bool> allowed = router.AskAsync("a", CancellationToken.None);
        Task<bool> stopped = router.AskAsync("b", CancellationToken.None);
        Assert.False(allowed.IsCompleted);

        router.Route("""{"type":"approval","id":"a","allow":false}""");
        Assert.False(await allowed.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(stopped.IsCompleted);

        router.Route("""{"type":"stop"}""");
        Assert.False(await stopped.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task The_end_of_the_input_ends_the_agent()
    {
        var router = new LocalAgentInputRouter();

        await router.ReadAsync(new StringReader("""{"type":"message","text":"hi"}""" + "\n"));

        Assert.True(router.Stopped.IsCancellationRequested);
        Assert.True(router.Messages.TryRead(out string? message));
        Assert.Equal("hi", message);
        Assert.False(router.Messages.TryRead(out _));
    }

    [Fact]
    public void Lines_go_out_one_at_a_time_and_whole()
    {
        var output = new StringWriter();
        var lines = new LocalAgentLines(output);

        Parallel.For(0, 50, i => lines.Write(LocalAgentProtocol.Note("line " + i)));

        string[] written = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(50, written.Length);
        Assert.All(written, line => Assert.Equal("note", JsonNode.Parse(line)!["type"]!.ToString()));
    }

    [Fact]
    public void The_models_json_has_model_title_provider_and_swaps()
    {
        string json = LocalAgentProgram.ModelsJson([("mellum", "Mellum2.1", "openai", false), ("qwen3.5:9b", "qwen3.5:9b", "ollama", true)]);

        JsonArray array = JsonNode.Parse(json)!.AsArray();
        Assert.Equal(2, array.Count);
        Assert.Equal("mellum", array[0]!["model"]!.ToString());
        Assert.Equal("Mellum2.1", array[0]!["title"]!.ToString());
        Assert.Equal("openai", array[0]!["provider"]!.ToString());
        Assert.False(array[0]!["swaps"]!.GetValue<bool>());
        Assert.True(array[1]!["swaps"]!.GetValue<bool>());
        Assert.Equal("[]", LocalAgentProgram.ModelsJson([]));
    }

    [Fact]
    public async Task Ollama_models_without_tools_and_those_that_fail_are_left_out()
    {
        var calls = new List<string>();
        var http = new HttpClient(new FakeOllama(calls));

        List<string> found = await LocalAgentProgram.OllamaToolModelsAsync(http, "http://localhost:11434/v1", CancellationToken.None);

        Assert.Equal(["qwen3.5:9b"], found);
        Assert.Contains("GET http://localhost:11434/api/tags", calls);
        Assert.Contains("POST http://localhost:11434/api/show", calls);
    }

    [Fact]
    public async Task An_ollama_server_that_isnt_there_gives_none()
    {
        var http = new HttpClient(new FakeOllama([], down: true));

        Assert.Empty(await LocalAgentProgram.OllamaToolModelsAsync(http, "http://localhost:11434", CancellationToken.None));
        Assert.Empty(await LocalAgentProgram.OllamaToolModelsAsync(http, "not a url", CancellationToken.None));
    }

    private sealed class FakeOllama(List<string> calls, bool down = false) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            calls.Add($"{request.Method} {request.RequestUri}");
            if (down)
                throw new HttpRequestException("refused");
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/api/tags")
                return Json("""{"models":[{"name":"qwen3.5:9b"},{"name":"embed:1"},{"name":"broken:1"}]}""");

            string body = await request.Content!.ReadAsStringAsync(cancel);
            string name = JsonNode.Parse(body)!["model"]!.ToString();
            return name switch
            {
                "qwen3.5:9b" => Json("""{"capabilities":["completion","tools"]}"""),
                "embed:1" => Json("""{"capabilities":["embedding"]}"""),
                _ => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            };
        }

        private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    }
}
