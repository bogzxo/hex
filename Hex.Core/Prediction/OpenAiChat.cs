using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Hex.Core;

/// <summary>
/// The OpenAI chat API, POST /v1/chat/completions with the conversation and the answer whole (stream false): llama.cpp's
/// llama-server, LM Studio, vLLM, OpenRouter. What the model thought comes back as llama-server's reasoning_content (or
/// a &lt;think&gt; block in the answer), kept beside it. The sampling values OpenAI's API doesn't have (top_k,
/// repeat_penalty) and thinking (chat_template_kwargs.enable_thinking, llama-server's) are sent only when the settings
/// change them, so a server that doesn't know them is never sent them unasked. The key goes as a bearer token.
/// </summary>
internal sealed class OpenAiChat(HttpClient http, Uri api, string model, string? key) : IChatStreamer
{
    /// <summary>The address requests go to, ending /chat/completions.</summary>
    public Uri Api => api;

    /// <summary>The model's parameters.</summary>
    public PredictionTuning Tuning { get; init; } = PredictionTuning.Usual;

    /// <summary>The most tokens one answer may have.</summary>
    public int MaxTokens { get; init; } = 1536;

    /// <summary>Asks llama-server to reuse its cached prompt (cache_prompt); false for a server that may refuse the field.</summary>
    public bool CachePrompt { get; init; }

    /// <summary>The llama-server slot (id_slot) to use, -1 for any; sent only with <see cref="CachePrompt"/>.</summary>
    public int Slot { get; init; } = -1;

    /// <summary>How long the whole answer may take.</summary>
    public TimeSpan Deadline { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The chat API's address from the URL the user gave for completions: one ending /chat/completions stays, one ending
    /// /completions becomes /chat/completions, one ending /v1 has /chat/completions added, a bare server
    /// /v1/chat/completions. Null when it isn't a http or https URL.
    /// </summary>
    public static Uri? Url(string written)
    {
        if (PredictionProviders.OpenAiUrl(written) is not { } completions)
            return null;
        string path = completions.AbsolutePath;
        if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return completions;
        return new UriBuilder(completions) { Path = path[..^"completions".Length] + "chat/completions" }.Uri;
    }

    /// <summary>The model's answer and what it thought. Throws <see cref="PredictionException"/> for what went wrong.</summary>
    public async Task<ChatAnswer> Ask(IReadOnlyList<ChatTurn> conversation, CancellationToken cancel)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, api)
        {
            Content = PredictionHttp.Json(writer => Body(writer, conversation)),
        };
        if (!string.IsNullOrEmpty(key))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(Deadline);
        using HttpResponseMessage response = await PredictionHttp.Send(http, message, deadline, cancel, api, key).ConfigureAwait(false);

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
            throw PredictionHttp.Failed(error, api, key);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("choices", out JsonElement choices) || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0 || !choices[0].TryGetProperty("message", out JsonElement said) || said.ValueKind != JsonValueKind.Object)
            throw new PredictionException(PredictionStatus.BadAnswer, $"{api.Host} answered without a message.");

        string content = said.TryGetProperty("content", out JsonElement text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" : "";
        string? thought = said.TryGetProperty("reasoning_content", out JsonElement reasoning) && reasoning.ValueKind == JsonValueKind.String
            ? reasoning.GetString()
            : null;
        return OllamaChat.Answer(content, thought);
    }

    /// <summary>
    /// The answer as it is written, server-sent events read until [DONE] or the choice's finish reason: llama-server's
    /// reasoning_content as thinking pieces, the content as answer pieces with a &lt;think&gt; block written into it split
    /// out as thinking. The model may take <see cref="PredictionHttp.Loading"/> to load and to say anything, then
    /// <see cref="PredictionHttp.FirstPiece"/> between events, what it thinks counting; there is no limit on the whole
    /// answer. Throws <see cref="PredictionException"/> for what went wrong.
    /// </summary>
    public async IAsyncEnumerable<ChatPiece> Stream(IReadOnlyList<ChatTurn> conversation, [EnumeratorCancellation] CancellationToken cancel)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, api)
        {
            Content = PredictionHttp.Json(writer => Body(writer, conversation, stream: true)),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrEmpty(key))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        TimeSpan wait = PredictionHttp.Loading;
        deadline.CancelAfter(wait);
        using HttpResponseMessage response = await PredictionHttp.Send(http, message, deadline, cancel, api, key).ConfigureAwait(false);
        using WireLines reader = await PredictionHttp.Reader(response, deadline, cancel, api).ConfigureAwait(false);

        var splitter = new ThinkSplitter();
        var pieces = new List<ChatPiece>(2);
        while (await PredictionHttp.Line(reader, deadline, wait, cancel, api).ConfigureAwait(false))
        {
            wait = PredictionHttp.FirstPiece;

            // Server-sent events: data lines matter, blank lines, comments and other fields don't
            if (!WireLines.Data(reader.Current, out ReadOnlyMemory<byte> data))
                continue;
            if (WireLines.IsDone(data))
                break;

            WireChunk chunk = WireChunk.Read(data.Span, WireShape.OpenAiChat, api);
            if (chunk.Error is { } error)
                throw PredictionHttp.Failed(error, api, key);

            if (!string.IsNullOrEmpty(chunk.Thought))
                yield return new ChatPiece(chunk.Thought, true);
            pieces.Clear();
            splitter.Feed(chunk.Text ?? string.Empty, pieces);
            foreach (ChatPiece piece in pieces)
                yield return piece;
            if (chunk.Finished)
                break;
        }

        pieces.Clear();
        splitter.End(pieces);
        foreach (ChatPiece piece in pieces)
            yield return piece;
    }

    // {model, messages, stream, max_tokens, temperature, top_p?, top_k?, repeat_penalty?, chat_template_kwargs?}
    private void Body(Utf8JsonWriter writer, IReadOnlyList<ChatTurn> conversation, bool stream = false)
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
        writer.WriteNumber("max_tokens", MaxTokens);
        writer.WriteNumber("temperature", Tuning.Temperature);
        if (CachePrompt)
        {
            writer.WriteBoolean("cache_prompt", true);
            if (Slot >= 0)
                writer.WriteNumber("id_slot", Slot);
        }
        if (Tuning.TopP > 0)
            writer.WriteNumber("top_p", Tuning.TopP);
        if (Tuning.TopK > 0)
            writer.WriteNumber("top_k", Tuning.TopK);
        if (Tuning.RepeatPenalty > 0)
            writer.WriteNumber("repeat_penalty", Tuning.RepeatPenalty);
        if (Tuning.Thinking != PredictionTuning.THINK_MODEL)
        {
            writer.WriteStartObject("chat_template_kwargs");
            writer.WriteBoolean("enable_thinking", Tuning.Thinking != PredictionTuning.THINK_OFF);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }
}
