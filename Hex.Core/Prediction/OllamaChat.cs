using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hex.Core;

/// <summary>One message of a conversation sent to a chat model: system, user or assistant, and what it says.</summary>
public sealed record ChatTurn(string Role, string Content);

/// <summary>A chat model's answer, and what it thought on the way when it thought, null when it didn't.</summary>
internal sealed record ChatAnswer(string Text, string? Thinking = null);

/// <summary>
/// Ollama's chat API, POST /api/chat with the conversation, the model's tuning and, when the model can think and the
/// settings say so, think. The answer comes whole (stream false), with what the model thought beside it: Ollama's own
/// thinking field, or a &lt;think&gt; block the model wrote into the answer. Whether a model thinks at all is asked of
/// /api/show once per server and model.
/// </summary>
internal sealed partial class OllamaChat(HttpClient http, Uri api, string model) : IChatStreamer
{
    // What /api/show said each model can do, by its chat URL and name. Null when the server didn't say
    private static readonly ConcurrentDictionary<(string Api, string Model), bool?> thinks = new();

    /// <summary>The address requests go to, /api/chat on the server.</summary>
    public Uri Api => api;

    /// <summary>The model's parameters. Thinking is sent only when the model can think, or when the server doesn't say.</summary>
    public PredictionTuning Tuning { get; init; } = PredictionTuning.Usual;

    /// <summary>The most tokens one answer may have, what it thought counted in them.</summary>
    public int MaxTokens { get; init; } = 1536;

    /// <summary>How long the whole answer may take, the model perhaps loaded first and thinking.</summary>
    public TimeSpan Deadline { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>The address of Ollama's chat API from the server's URL the user gave, null when it isn't a http or https URL.</summary>
    public static Uri? Url(string written) => PredictionProviders.OllamaUrl(written) is { } generate ? Beside(generate, "chat") : null;

    /// <summary>
    /// The model's answer to the conversation and what it thought, trimmed, a &lt;think&gt; block it wrote into the answer
    /// itself taken out into the thinking. Throws <see cref="PredictionException"/> for what went wrong, the server's host
    /// named and never what was asked.
    /// </summary>
    public async Task<ChatAnswer> Ask(IReadOnlyList<ChatTurn> conversation, CancellationToken cancel)
    {
        object? think = await Think(cancel).ConfigureAwait(false);
        try
        {
            return await Asked(conversation, think, cancel).ConfigureAwait(false);
        }
        catch (PredictionException e) when (think is string && e.Message.Contains("think", StringComparison.OrdinalIgnoreCase))
        {
            // A level only some models take (gpt-oss's low, medium, high): asked again with thinking simply on
            return await Asked(conversation, true, cancel).ConfigureAwait(false);
        }
        catch (PredictionException e) when (think is not null && e.Message.Contains("think", StringComparison.OrdinalIgnoreCase))
        {
            // A model the server didn't say could not think, and it can't: asked again without
            thinks[(Beside(api, "show").ToString(), model)] = false;
            return await Asked(conversation, null, cancel).ConfigureAwait(false);
        }
    }

    // What think is sent as for this model
    private Task<object?> Think(CancellationToken cancel) => Think(http, api, model, Tuning.Thinking, cancel);

    /// <summary>
    /// What Ollama's think is sent as for what the settings want: nothing for the model's own, false, true or a level.
    /// Nothing at all for a model /api/show says can't think, which a think would make the server refuse.
    /// </summary>
    public static async Task<object?> Think(HttpClient http, Uri endpoint, string model, string wanted, CancellationToken cancel)
    {
        if (wanted == PredictionTuning.THINK_MODEL)
            return null;
        if (await CanThink(http, endpoint, model, cancel).ConfigureAwait(false) == false)
            return null;
        return wanted switch
        {
            PredictionTuning.THINK_OFF => false,
            PredictionTuning.THINK_ON => true,
            _ => wanted,
        };
    }

    /// <summary>Whether the model can think, as /api/show's capabilities say; null when the server doesn't say or can't be asked.</summary>
    public Task<bool?> CanThink(CancellationToken cancel) => CanThink(http, api, model, cancel);

    /// <summary>
    /// Whether a model can think, asked of /api/show beside any endpoint of Ollama's API (generate or chat) once per server
    /// and model; null when the server doesn't say or can't be asked, which isn't remembered.
    /// </summary>
    public static async Task<bool?> CanThink(HttpClient http, Uri endpoint, string model, CancellationToken cancel)
    {
        Uri show = Beside(endpoint, "show");
        var key = (show.ToString(), model);
        if (thinks.TryGetValue(key, out bool? known))
            return known;

        bool? can = null;
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            wait.CancelAfter(TimeSpan.FromSeconds(5));
            using var message = new HttpRequestMessage(HttpMethod.Post, show)
            {
                Content = PredictionHttp.Json(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("model", model);
                    writer.WriteEndObject();
                }),
            };
            using HttpResponseMessage response = await http.SendAsync(message, wait.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(wait.Token).ConfigureAwait(false));
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("capabilities", out JsonElement capabilities)
                    && capabilities.ValueKind == JsonValueKind.Array)
                    can = capabilities.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == "thinking");
            }
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or IOException or OperationCanceledException && !cancel.IsCancellationRequested)
        {
            return null;
        }

        thinks[key] = can;
        return can;
    }

    /// <summary>
    /// The answer as it is written, one piece per line the server sends: Ollama's thinking field as thinking pieces, the
    /// content as answer pieces with a &lt;think&gt; block the model wrote into it split out as thinking. The model may
    /// take <see cref="PredictionHttp.Loading"/> to load and to say anything, then <see cref="PredictionHttp.FirstPiece"/>
    /// between lines, what it thinks counting; there is no limit on the whole answer. Throws
    /// <see cref="PredictionException"/> for what went wrong, the server's host named and never what was asked.
    /// </summary>
    public async IAsyncEnumerable<ChatPiece> Stream(IReadOnlyList<ChatTurn> conversation, [EnumeratorCancellation] CancellationToken cancel)
    {
        object? think = await Think(cancel).ConfigureAwait(false);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        HttpResponseMessage response;
        try
        {
            response = await Opened(conversation, think, deadline, cancel).ConfigureAwait(false);
        }
        catch (PredictionException e) when (think is string && e.Message.Contains("think", StringComparison.OrdinalIgnoreCase))
        {
            // A level only some models take: asked again with thinking simply on
            response = await Opened(conversation, true, deadline, cancel).ConfigureAwait(false);
        }
        catch (PredictionException e) when (think is not null && e.Message.Contains("think", StringComparison.OrdinalIgnoreCase))
        {
            // A model the server didn't say could not think, and it can't: asked again without
            thinks[(Beside(api, "show").ToString(), model)] = false;
            response = await Opened(conversation, null, deadline, cancel).ConfigureAwait(false);
        }

        using (response)
        {
            using WireLines reader = await PredictionHttp.Reader(response, deadline, cancel, api).ConfigureAwait(false);
            var splitter = new ThinkSplitter();
            var pieces = new List<ChatPiece>(2);
            TimeSpan wait = PredictionHttp.Loading;
            while (await PredictionHttp.Line(reader, deadline, wait, cancel, api).ConfigureAwait(false))
            {
                wait = PredictionHttp.FirstPiece;
                if (reader.Current.Length == 0)
                    continue;

                WireChunk chunk = WireChunk.Read(reader.Current.Span, WireShape.OllamaChat, api);
                if (chunk.Error is { } error)
                    throw PredictionHttp.Failed(error, api, null);

                if (!string.IsNullOrEmpty(chunk.Thought))
                    yield return new ChatPiece(chunk.Thought, true);
                pieces.Clear();
                splitter.Feed(chunk.Text ?? string.Empty, pieces);
                if (chunk.Finished)
                    splitter.End(pieces);
                foreach (ChatPiece piece in pieces)
                    yield return piece;
                if (chunk.Finished)
                    yield break;
            }

            pieces.Clear();
            splitter.End(pieces);
            foreach (ChatPiece piece in pieces)
                yield return piece;
        }
    }

    // The request sent and its response once the headers are in, the deadline the model's loading time
    private async Task<HttpResponseMessage> Opened(IReadOnlyList<ChatTurn> conversation, object? think, CancellationTokenSource deadline, CancellationToken cancel)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, api)
        {
            Content = PredictionHttp.Json(writer => Body(writer, conversation, think, stream: true)),
        };
        deadline.CancelAfter(PredictionHttp.Loading);
        return await PredictionHttp.Send(http, message, deadline, cancel, api, null).ConfigureAwait(false);
    }

    private async Task<ChatAnswer> Asked(IReadOnlyList<ChatTurn> conversation, object? think, CancellationToken cancel)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, api)
        {
            Content = PredictionHttp.Json(writer => Body(writer, conversation, think)),
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(Deadline);
        using HttpResponseMessage response = await PredictionHttp.Send(http, message, deadline, cancel, api, null).ConfigureAwait(false);

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new PredictionException(PredictionStatus.TimedOut, $"{api.Host} took too long to answer.");
        }
        catch (Exception e) when (e is HttpRequestException or IOException && !cancel.IsCancellationRequested)
        {
            throw new PredictionException(PredictionStatus.Unreachable, $"The connection to {api.Host} was lost.");
        }

        using JsonDocument document = PredictionHttp.Parse(body, api);
        JsonElement root = document.RootElement;
        if (PredictionHttp.Error(root) is { } error)
            throw PredictionHttp.Failed(error, api, null);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("message", out JsonElement said) || said.ValueKind != JsonValueKind.Object
            || !said.TryGetProperty("content", out JsonElement content) || content.ValueKind != JsonValueKind.String)
            throw new PredictionException(PredictionStatus.BadAnswer, $"{api.Host} answered without a message.");

        string? thought = said.TryGetProperty("thinking", out JsonElement thinking) && thinking.ValueKind == JsonValueKind.String ? thinking.GetString() : null;
        return Answer(content.GetString() ?? string.Empty, thought);
    }

    /// <summary>
    /// The answer as it is shown: a &lt;think&gt; block the model wrote into it taken out and put with the thinking the
    /// server gave apart, both trimmed, no thinking as null.
    /// </summary>
    public static ChatAnswer Answer(string content, string? thinking = null)
    {
        var thought = new List<string>();
        if (!string.IsNullOrWhiteSpace(thinking))
            thought.Add(thinking.Trim());
        string text = ThoughtBlock().Replace(content, block =>
        {
            if (block.Groups[1].Value.Trim() is { Length: > 0 } inner)
                thought.Add(inner);
            return string.Empty;
        }).Trim();
        return new ChatAnswer(text, thought.Count > 0 ? string.Join("\n\n", thought) : null);
    }

    // {model, messages, stream, keep_alive, think?, options: {num_predict, temperature, top_p, top_k, num_ctx, repeat_penalty}}
    private void Body(Utf8JsonWriter writer, IReadOnlyList<ChatTurn> conversation, object? think, bool stream = false)
    {
        writer.WriteStartObject();
        writer.WriteString("model", model);
        writer.WriteStartArray("messages");
        foreach (ChatTurn turn in conversation)
        {
            writer.WriteStartObject();
            writer.WriteString("role", turn.Role);
            writer.WriteString("content", turn.Content);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteBoolean("stream", stream);
        writer.WriteString("keep_alive", "30m");
        switch (think)
        {
            case bool on:
                writer.WriteBoolean("think", on);
                break;
            case string level:
                writer.WriteString("think", level);
                break;
        }
        writer.WriteStartObject("options");
        writer.WriteNumber("num_predict", MaxTokens);
        Tuning.WriteOllamaOptions(writer);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    // Another endpoint of the same API beside /api/generate or /api/chat, the query kept
    private static Uri Beside(Uri endpoint, string name)
    {
        string path = endpoint.AbsolutePath;
        int slash = path.LastIndexOf('/');
        return new UriBuilder(endpoint) { Path = path[..(slash + 1)] + name }.Uri;
    }

    [GeneratedRegex(@"<think>(.*?)(?:</think>|$)", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThoughtBlock();
}
