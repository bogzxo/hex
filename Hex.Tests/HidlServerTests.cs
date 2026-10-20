using System.IO.Pipes;
using System.Text.Json.Nodes;

using Hex.Core;

using Horizon.UI.Scripting;

namespace Hex.Tests;

/// <summary>
/// HIDL's language server talked to the way an editor talks to it, through the engine's JSON-RPC connection over a pair
/// of pipes: what it says it can do, the diagnostics it sends of an opened file, completion, hovers, where a name is
/// declared, the outline, and that it goes when it's told to.
/// </summary>
public sealed class HidlServerTests : IDisposable
{
    private const int WAIT_MS = 10_000;

    private readonly AnonymousPipeServerStream toServer = new(PipeDirection.Out);
    private readonly AnonymousPipeServerStream toClient = new(PipeDirection.Out);
    private readonly AnonymousPipeClientStream serverReads, clientReads;
    private readonly HidlServer server;
    private readonly JsonRpcConnection client;
    private readonly List<JsonNode?> published = [];
    private readonly SemaphoreSlim publishedOne = new(0);

    private readonly string folder = Path.Combine(Path.GetTempPath(), "hidl-server-" + Guid.NewGuid().ToString("N"));

    public HidlServerTests()
    {
        Directory.CreateDirectory(folder);
        serverReads = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        clientReads = new AnonymousPipeClientStream(PipeDirection.In, toClient.ClientSafePipeHandle);
        server = new HidlServer(serverReads, toClient);
        client = new JsonRpcConnection(clientReads, toServer);
        client.OnNotification("textDocument/publishDiagnostics", parameters =>
        {
            lock (published)
                published.Add(parameters?.DeepClone());
            publishedOne.Release();
        });
    }

    public void Dispose()
    {
        client.Dispose();
        server.Dispose();
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    private string Uri(string name) => new Uri(Path.Combine(folder, name)).AbsoluteUri;

    private async Task<JsonNode?> Ask(string method, JsonNode? parameters)
    {
        Task<JsonNode?> asked = client.Request(method, parameters);
        Assert.Same(asked, await Task.WhenAny(asked, Task.Delay(WAIT_MS)));
        return await asked;
    }

    private async Task Start()
    {
        JsonNode? answer = await Ask("initialize", new JsonObject { ["processId"] = null, ["rootUri"] = new Uri(folder).AbsoluteUri, ["capabilities"] = new JsonObject() });
        Assert.Equal("hidl", answer?["serverInfo"]?["name"]?.GetValue<string>());
        client.Notify("initialized", new JsonObject());
    }

    private void Open(string name, string text) => client.Notify("textDocument/didOpen", new JsonObject
    {
        ["textDocument"] = new JsonObject { ["uri"] = Uri(name), ["languageId"] = "hidl", ["version"] = 1, ["text"] = text }
    });

    private async Task<JsonArray> DiagnosticsOf(string name)
    {
        for (int tries = 0; tries < 10; tries++)
        {
            Assert.True(await publishedOne.WaitAsync(WAIT_MS));
            lock (published)
            {
                if (published.LastOrDefault(said => said?["uri"]?.GetValue<string>() == Uri(name)) is { } found)
                    return found["diagnostics"]!.AsArray();
            }
        }

        throw new TimeoutException("no diagnostics came");
    }

    private JsonObject At(string name, int line, int character) => new()
    {
        ["textDocument"] = new JsonObject { ["uri"] = Uri(name) },
        ["position"] = new JsonObject { ["line"] = line, ["character"] = character }
    };

    [Fact]
    public async Task It_says_what_it_can_do()
    {
        JsonNode? answer = await Ask("initialize", new JsonObject { ["processId"] = null, ["capabilities"] = new JsonObject() });
        JsonNode? capabilities = answer?["capabilities"];
        Assert.Equal(1, capabilities?["textDocumentSync"]?["change"]?.GetValue<int>());
        Assert.True(capabilities?["hoverProvider"]?.GetValue<bool>());
        Assert.True(capabilities?["definitionProvider"]?.GetValue<bool>());
        Assert.True(capabilities?["documentSymbolProvider"]?.GetValue<bool>());
        Assert.NotNull(capabilities?["completionProvider"]);
    }

    [Fact]
    public async Task An_opened_file_is_checked_and_a_fixed_one_has_nothing_said_about_it()
    {
        await Start();
        Open("speed.hor", "let speed = 4;\nlet go = sped * 2;");

        JsonArray diagnostics = await DiagnosticsOf("speed.hor");
        JsonNode wrong = Assert.Single(diagnostics)!;
        Assert.Equal(1, wrong["range"]?["start"]?["line"]?.GetValue<int>());
        Assert.Equal(1, wrong["severity"]?.GetValue<int>());
        Assert.Contains("'*'", wrong["message"]?.GetValue<string>());

        client.Notify("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri("speed.hor"), ["version"] = 2 },
            ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = "let speed = 4;\nlet go = speed * 2;" })
        });
        Assert.Empty(await DiagnosticsOf("speed.hor"));
    }

    [Fact]
    public async Task A_mistake_of_syntax_is_an_error_where_it_is()
    {
        await Start();
        Open("broken.hor", "let a = 1;\nlet b = ;");

        JsonArray diagnostics = await DiagnosticsOf("broken.hor");
        Assert.NotEmpty(diagnostics);
        Assert.Equal("hidl", diagnostics[0]?["source"]?.GetValue<string>());
    }

    [Fact]
    public async Task Completion_replaces_the_word_being_typed()
    {
        await Start();
        Open("complete.hor", "let health = 100;\nlet x = hea");

        JsonNode? answer = await Ask("textDocument/completion", At("complete.hor", 1, 11));
        JsonArray items = answer!["items"]!.AsArray();
        JsonNode health = items.First(item => item?["label"]?.GetValue<string>() == "health")!;
        Assert.Equal(8, health["textEdit"]?["range"]?["start"]?["character"]?.GetValue<int>());
        Assert.Equal(11, health["textEdit"]?["range"]?["end"]?["character"]?.GetValue<int>());
    }

    [Fact]
    public async Task A_hover_is_markdown_without_Hexs_icons()
    {
        await Start();
        Open("hover.hor", "// @doc How fast it goes\nlet speed = 4;\nprint(speed);");

        JsonNode? answer = await Ask("textDocument/hover", At("hover.hor", 2, 8));
        string said = answer!["contents"]!["value"]!.GetValue<string>();
        Assert.Equal("markdown", answer["contents"]!["kind"]!.GetValue<string>());
        Assert.Contains("How fast it goes", said);
        Assert.DoesNotContain("$(", said);
    }

    [Fact]
    public async Task A_name_is_declared_in_the_file_in_another_open_one_or_in_the_workspace()
    {
        File.WriteAllText(Path.Combine(folder, "shared.hor"), "let gravity = 9.8;\n");
        await Start();
        Open("other.hor", "let friction = 0.5;");
        Open("use.hor", "let speed = 4;\nprint(speed, friction, gravity);");

        JsonNode? here = await Ask("textDocument/definition", At("use.hor", 1, 7));
        Assert.Equal(Uri("use.hor"), here?["uri"]?.GetValue<string>());
        Assert.Equal(0, here?["range"]?["start"]?["line"]?.GetValue<int>());

        JsonNode? open = await Ask("textDocument/definition", At("use.hor", 1, 15));
        Assert.Equal(Uri("other.hor"), open?["uri"]?.GetValue<string>());

        JsonNode? disk = await Ask("textDocument/definition", At("use.hor", 1, 25));
        Assert.Equal(Uri("shared.hor"), disk?["uri"]?.GetValue<string>());
    }

    [Fact]
    public async Task The_outline_is_the_files_symbols()
    {
        await Start();
        Open("outline.hor", "let moves = {\n    jab: { damage: 3 }\n};\nconst speed = 4;");

        JsonNode? answer = await Ask("textDocument/documentSymbol", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = Uri("outline.hor") } });
        JsonArray symbols = answer!.AsArray();
        Assert.Contains(symbols, symbol => symbol?["name"]?.GetValue<string>() == "moves");
        Assert.Contains(symbols, symbol => symbol?["name"]?.GetValue<string>() == "speed");
    }

    [Fact]
    public async Task It_goes_when_it_is_told_to_after_a_shutdown()
    {
        await Start();
        await Ask("shutdown", null);
        client.Notify("exit", null);

        Assert.True(Task.Run(server.Wait).Wait(WAIT_MS));
        Assert.True(server.ShutDownCleanly);
    }
}
