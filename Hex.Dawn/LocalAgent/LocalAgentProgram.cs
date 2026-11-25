using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using Hex.Core;

namespace Hex.Dawn;

/// <summary>What <c>Hex.exe --local-agent</c> was started with.</summary>
internal sealed record LocalAgentArgs(string Folder, int ChatId, string? OptionsFile, string? Resume, bool Models);

/// <summary>What the extension wrote in the options file for a chat: the model, the approvals mode and whether it thinks.</summary>
internal sealed record LocalAgentFileOptions(string? Model, string Approvals, string Think, string Profile = LocalAgentOptions.WRITE)
{
    public static readonly LocalAgentFileOptions Usual = new(null, LocalAgentOptions.EDITS, "model");
}

/// <summary>Standard output as the agent's one way of speaking: a line at a time through one lock, flushed per line.</summary>
internal sealed class LocalAgentLines(TextWriter output)
{
    private readonly Lock gate = new();

    public void Write(string line)
    {
        lock (gate)
        {
            output.Write(line);
            output.Write('\n');
            output.Flush();
        }
    }
}

/// <summary>
/// What the agent reads on its stdin, on a task of its own: messages queue up for the turns, an approval's answer finds
/// the turn waiting for it, a stop (or the end of the input) cancels <see cref="Stopped"/> and ends the queue.
/// </summary>
internal sealed class LocalAgentInputRouter
{
    private readonly Channel<string> messages = Channel.CreateUnbounded<string>();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> approvals = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource stop = new();

    /// <summary>Cancelled when the person said stop or the input ended.</summary>
    public CancellationToken Stopped => stop.Token;

    /// <summary>The messages said, in order, ending when the input does.</summary>
    public ChannelReader<string> Messages => messages.Reader;

    /// <summary>Reads lines until the input ends or a stop comes, routing each.</summary>
    public async Task ReadAsync(TextReader input)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                string? line = await input.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                Route(line);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
        finally
        {
            End();
        }
    }

    /// <summary>One line of stdin routed: a message queued, an approval answered, a stop ended it all.</summary>
    public void Route(string line)
    {
        switch (LocalAgentProtocol.Read(line))
        {
            case LocalAgentInput.Message message:
                messages.Writer.TryWrite(message.Text);
                break;
            case LocalAgentInput.Approval approval:
                Waiting(approval.Id).TrySetResult(approval.Allow);
                break;
            case LocalAgentInput.Stop:
                End();
                break;
        }
    }

    /// <summary>The answer to the approval of that id, from now or from before it was waited for; no when the agent is stopped.</summary>
    public async Task<bool> AskAsync(string id, CancellationToken cancel)
    {
        TaskCompletionSource<bool> waiting = Waiting(id);
        using CancellationTokenRegistration registration = cancel.Register(() => waiting.TrySetCanceled(cancel));
        try
        {
            return await waiting.Task.ConfigureAwait(false);
        }
        finally
        {
            approvals.TryRemove(id, out TaskCompletionSource<bool>? _);
        }
    }

    private TaskCompletionSource<bool> Waiting(string id) =>
        approvals.GetOrAdd(id, _ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));

    private void End()
    {
        messages.Writer.TryComplete();
        try { stop.Cancel(); } catch (ObjectDisposedException) { }
        foreach (TaskCompletionSource<bool> waiting in approvals.Values)
            waiting.TrySetResult(false);
    }
}

/// <summary>Hex's tools over MCP as the loop's <see cref="IToolHost"/>.</summary>
internal sealed class LocalAgentMcpHost(McpClient client, IReadOnlyList<ToolSpec> tools) : IToolHost
{
    public IReadOnlyList<ToolSpec> Tools => tools;

    public Task<AgentToolResult> CallAsync(string name, string argumentsJson, CancellationToken cancel) =>
        client.CallAsync(name, argumentsJson, cancel);
}

/// <summary>
/// <c>Hex.exe --local-agent</c>: the local model as an agent (docs/local-agent-plan.md). JSON lines out on stdout, in on
/// stdin; <c>--models</c> prints the models a chat may pick instead and leaves.
/// </summary>
internal static class LocalAgentProgram
{
    public const string ARGUMENT = "--local-agent";

    private const string NO_MODEL = "No local model that can chat is set up. Run llama.cpp: Set Up… or pick one in Settings → AI.";

    public static bool IsLocalAgentMode(string[] args) => args.Contains(ARGUMENT, StringComparer.Ordinal);

    public static int Run(string[] args)
    {
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var lines = new LocalAgentLines(output);
        try
        {
            return RunAsync(args, input, lines, LoadSettings(), CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            lines.Write(LocalAgentProtocol.Error(e.Message));
            lines.Write(LocalAgentProtocol.Done("error"));
            return 1;
        }
    }

    // The user's settings as the editor reads them (the data folder the person moved goes with them)
    private static HexSettings LoadSettings()
    {
        var settings = new HexSettings(Horizon.UI.Components.TerminalFiles.Settings(), Path.Combine(HexHost.ProgramFolder, HexSettings.FILE));
        HexData.UseAgentFolder(settings.AgentDataFolder);
        // This process has not scanned the model folders: without it the library's chat models (any GGUF that can chat) are missing from the list
        LocalModelLibrary.Shared.Settings = settings;
        LocalModelLibrary.Shared.OllamaEnabled = false;
        LocalModelLibrary.Shared.RefreshNow();
        return settings;
    }

    /// <summary>The whole run over its parts, for tests to give in-memory ones.</summary>
    public static async Task<int> RunAsync(string[] args, TextReader input, LocalAgentLines say, HexSettings settings, CancellationToken cancel)
    {
        LocalAgentArgs parsed = ParseArgs(args);
        if (parsed.Models)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            say.Write(await ModelsJsonAsync(settings, http, cancel).ConfigureAwait(false));
            return 0;
        }

        LocalAgentFileOptions file = ReadOptionsFile(parsed.OptionsFile);
        AiEditModel? model = PickModel(settings, file.Model, out string? instead);
        if (model is null)
        {
            say.Write(LocalAgentProtocol.Error(NO_MODEL));
            say.Write(LocalAgentProtocol.Done("error"));
            return 1;
        }
        if (instead is not null)
            say.Write(LocalAgentProtocol.Note(instead));

        // A model on a linked device is asked through the loopback endpoint Hex keeps, nothing is started here for it
        bool hexLlama = PredictionSettings.Provider(settings) == HexSettings.LLAMACPP && settings.LlamaCppConfig is not null && !LinkAiModelId.Is(model.Model);
        using var server = new LlamaCppServer();
        if (hexLlama && !await EnsureServerAsync(server, settings.LlamaCppConfig!, say, cancel).ConfigureAwait(false))
            return 1;

        // The router reads stdin from here on, so a stop is heard even while the tools are being connected
        var router = new LocalAgentInputRouter();
        Task reading = Task.Run(() => router.ReadAsync(input), CancellationToken.None);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel, router.Stopped);

        McpClient client;
        IReadOnlyList<ToolSpec> tools;
        try
        {
            Stream pipe = await LocalAgentPipe.ConnectAsync(parsed.Folder, parsed.ChatId, linked.Token).ConfigureAwait(false);
            client = new McpClient(pipe, pipe);
            await client.InitializeAsync("hex-local", linked.Token).ConfigureAwait(false);
            tools = LocalAgentTools.Choose(await client.ListToolsAsync(linked.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            say.Write(LocalAgentProtocol.Error("Hex isn't reachable for this folder: " + e.Message.TrimEnd('.') + ". Open a folder first (File → Open Folder…), then start the agent again."));
            say.Write(LocalAgentProtocol.Done("error"));
            return 1;
        }

        await using (client.ConfigureAwait(false))
        {
            PredictionTuning chatTuning = settings.TuningFor(LocalAiRole.Chat);
            PredictionTuning tuning = chatTuning with { Thinking = ThinkingOf(file.Think, model.Thinks, chatTuning.Thinking) };
            int maxTokens = settings.AiEditMaxTokens > 0 ? settings.AiEditMaxTokens : 4096;
            int context = hexLlama ? settings.LlamaCppContext : settings.EditPredictionsContext;
            IAgentChat? chat = AgentChats.Make(model.Provider, model.Url, model.Model, model.ApiKey, tuning, maxTokens, context);
            if (chat is null)
            {
                say.Write(LocalAgentProtocol.Error($"The model's server ({model.Provider} at {model.Url}) can't be talked to as an agent."));
                say.Write(LocalAgentProtocol.Done("error"));
                return 1;
            }

            var store = new LocalAgentStore(Path.Combine(HexData.AgentTemp, "local-sessions"));
            string session = parsed.Resume ?? store.NewSession();
            var options = new LocalAgentOptions(model.Provider, model.Url, model.Model, model.ApiKey, tuning, context, maxTokens,
                file.Approvals, parsed.Folder, parsed.ChatId, session, file.Profile);
            say.Write(LocalAgentProtocol.Session(session));

            var loop = new LocalAgentLoop(chat, new LocalAgentMcpHost(client, tools), options, say.Write,
                // The loop says the approval line itself (call id, tool, arguments); the answer comes in on stdin
                (id, _, _, token) => router.AskAsync(id, token), store);

            await TurnsAsync(router, loop, say, linked.Token).ConfigureAwait(false);
        }

        await Task.WhenAny(reading, Task.Delay(200, CancellationToken.None)).ConfigureAwait(false);
        return 0;
    }

    /// <summary>The turns, one a message, until a stop or the end of the input.</summary>
    public static async Task TurnsAsync(LocalAgentInputRouter router, LocalAgentLoop loop, LocalAgentLines say, CancellationToken cancel)
    {
        try
        {
            await foreach (string message in router.Messages.ReadAllAsync(cancel).ConfigureAwait(false))
            {
                try
                {
                    await loop.RunTurnAsync(message, cancel).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    say.Write(LocalAgentProtocol.Error(e.Message));
                    say.Write(LocalAgentProtocol.Done("error"));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// The model a chat asked for: its name among the agent models, or for Ollama any name (the server's models with tools
    /// are listed for the picker beyond what AgentModels has). An unknown name elsewhere gives the first model, said in
    /// <paramref name="instead"/>. Null when no model can chat.
    /// </summary>
    internal static AiEditModel? PickModel(HexSettings settings, string? asked, out string? instead)
    {
        instead = null;
        string name = asked?.Trim() ?? string.Empty;
        IReadOnlyList<AiEditModel> known = AiEditModels.AgentModels(settings);
        if (name.Length > 0 && PredictionSettings.Provider(settings) == PredictionProviders.OLLAMA
            && !known.Any(model => string.Equals(model.Model, name, StringComparison.Ordinal)))
            return new AiEditModel(PredictionProviders.OLLAMA, PredictionSettings.ApiUrl(settings), name, null, name, true, settings.AiEditThinking);

        AiEditModel? found = AiEditModels.ForAgent(settings, name.Length > 0 ? name : null);
        if (found is not null && name.Length > 0 && !string.Equals(found.Model, name, StringComparison.Ordinal))
            instead = $"The model \"{name}\" isn't set up here, {found.Title} is used instead.";
        return found;
    }

    private static string ThinkingOf(string think, bool modelThinks, string tuned) => think switch
    {
        "on" => PredictionTuning.THINK_ON,
        "off" => PredictionTuning.THINK_OFF,
        _ => modelThinks ? PredictionTuning.THINK_ON : tuned,
    };

    // Hex's llama.cpp server up before the first turn: answering already, else started (or adopted) and waited for
    private static async Task<bool> EnsureServerAsync(LlamaCppServer server, LlamaCppServerConfig config, LocalAgentLines say, CancellationToken cancel)
    {
        string root = LlamaCppServerArgs.Root(config.Port);
        if (await AnswersAsync(root, cancel).ConfigureAwait(false))
            return true;

        say.Write(LocalAgentProtocol.Note("Starting llama.cpp…"));
        if (await server.Start(config, TimeSpan.FromMinutes(10), cancel).ConfigureAwait(false))
            return true;

        say.Write(LocalAgentProtocol.Error(server.Problem ?? "llama.cpp didn't start."));
        say.Write(LocalAgentProtocol.Done("error"));
        return false;
    }

    private static async Task<bool> AnswersAsync(string root, CancellationToken cancel)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using HttpResponseMessage answer = await http.GetAsync(root + "/health", cancel).ConfigureAwait(false);
            return answer.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancel.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>The arguments, the folder this run is for (where it was started otherwise), the Agents panel's chat and the rest.</summary>
    public static LocalAgentArgs ParseArgs(string[] args)
    {
        string? folder = null, options = null, resume = null;
        int chat = 0;
        bool models = false;
        for (int i = 0; i < args.Length; i++)
        {
            string value() => i + 1 < args.Length ? args[++i] : string.Empty;
            switch (args[i])
            {
                case "--folder": folder = value(); break;
                case "--agent-chat": _ = int.TryParse(value(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out chat); break;
                case "--options": options = value(); break;
                case "--resume": resume = value(); break;
                case "--models": models = true; break;
            }
        }

        return new LocalAgentArgs(string.IsNullOrWhiteSpace(folder) ? Environment.CurrentDirectory : folder, chat,
            string.IsNullOrWhiteSpace(options) ? null : options, string.IsNullOrWhiteSpace(resume) ? null : resume, models);
    }

    /// <summary>The options file the extension wrote, the usual options when it isn't there or isn't JSON.</summary>
    public static LocalAgentFileOptions ReadOptionsFile(string? path)
    {
        if (path is null || !File.Exists(path))
            return LocalAgentFileOptions.Usual;
        try
        {
            return ParseOptions(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return LocalAgentFileOptions.Usual;
        }
    }

    /// <summary>The options as JSON text: model (empty for the first), approvals (ask, edits, all), think (model, on, off).</summary>
    public static LocalAgentFileOptions ParseOptions(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
                return LocalAgentFileOptions.Usual;
            string? model = Text(root, "model");
            string? a = Text(root, "approvals")?.ToLowerInvariant(), t = Text(root, "think")?.ToLowerInvariant();
            string approvals = a is LocalAgentOptions.ASK or LocalAgentOptions.EDITS or LocalAgentOptions.ALL ? a : LocalAgentOptions.EDITS;
            string think = t is "on" or "off" ? t : "model";
            string? p = Text(root, "profile")?.ToLowerInvariant();
            string profile = p is LocalAgentOptions.READ_ONLY or LocalAgentOptions.MINIMAL ? p : LocalAgentOptions.WRITE;
            return new LocalAgentFileOptions(string.IsNullOrWhiteSpace(model) ? null : model, approvals, think, profile);
        }
        catch (JsonException)
        {
            return LocalAgentFileOptions.Usual;
        }

        static string? Text(JsonObject root, string key) =>
            root[key] is JsonValue value && value.TryGetValue(out string? text) ? text?.Trim() : null;
    }

    /// <summary>The models a chat may pick as the JSON array --models prints: Hex's agent models, then Ollama's with tools.</summary>
    public static async Task<string> ModelsJsonAsync(HexSettings settings, HttpClient http, CancellationToken cancel)
    {
        var all = new List<(string Model, string Title, string Provider, bool Swaps)>();
        foreach (AiEditModel model in AiEditModels.AgentModels(settings))
            all.Add((model.Model, model.Title, model.Provider, model.Swaps));

        if (PredictionSettings.Provider(settings) == PredictionProviders.OLLAMA)
        {
            foreach (string name in await OllamaToolModelsAsync(http, PredictionSettings.ApiUrl(settings), cancel).ConfigureAwait(false))
            {
                if (!all.Exists(known => string.Equals(known.Model, name, StringComparison.Ordinal)))
                    all.Add((name, name, PredictionProviders.OLLAMA, true));
            }
        }

        return ModelsJson(all);
    }

    /// <summary>The JSON array of models: [{"model","title","provider","swaps"}].</summary>
    public static string ModelsJson(IEnumerable<(string Model, string Title, string Provider, bool Swaps)> models)
    {
        var array = new JsonArray();
        foreach (var (model, title, provider, swaps) in models)
            array.Add((JsonNode)new JsonObject { ["model"] = model, ["title"] = title, ["provider"] = provider, ["swaps"] = swaps });
        return array.ToJsonString();
    }

    /// <summary>The names of the models an Ollama server has that can call tools (/api/tags, then /api/show of each); those that fail are left out.</summary>
    public static async Task<List<string>> OllamaToolModelsAsync(HttpClient http, string url, CancellationToken cancel)
    {
        var found = new List<string>();
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? address))
            return found;
        string root = address.GetLeftPart(UriPartial.Authority);
        try
        {
            using var tagsTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            tagsTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            string tags = await http.GetStringAsync(root + "/api/tags", tagsTimeout.Token).ConfigureAwait(false);
            if (JsonNode.Parse(tags)?["models"] is not JsonArray listed)
                return found;
            foreach (JsonNode? entry in listed)
            {
                string? name = entry?["name"]?.GetValue<string>() ?? entry?["model"]?.GetValue<string>();
                if (string.IsNullOrEmpty(name))
                    continue;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    using var body = new StringContent(new JsonObject { ["model"] = name }.ToJsonString(), Encoding.UTF8, "application/json");
                    using HttpResponseMessage response = await http.PostAsync(root + "/api/show", body, timeout.Token).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                        continue;
                    JsonNode? shown = JsonNode.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
                    if (shown?["capabilities"] is JsonArray capabilities && capabilities.Any(each => each?.ToString() == "tools"))
                        found.Add(name);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException && !cancel.IsCancellationRequested)
                {
                }
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException && !cancel.IsCancellationRequested)
        {
        }

        return found;
    }
}
