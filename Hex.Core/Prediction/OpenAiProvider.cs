using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Hex.Core;

/// <summary>
/// The OpenAI completions API, POST /v1/completions streamed back as server-sent events,
/// data: {"choices": [{"text": "..."}]} and data: [DONE] at the end. Ollama's /v1, llama.cpp's server, LM Studio, vLLM,
/// OpenRouter and Codestral's endpoint all speak it. The key goes as a bearer token on each request.
/// </summary>
internal sealed class OpenAiProvider(HttpClient http, Uri api, string model, string? key) : IPredictionProvider
{
    // The most stop sequences the API takes
    private const int MOST_STOPS = 4;

    /// <summary>The address requests go to, ending /completions.</summary>
    public Uri Api => api;

    /// <summary>How long the first piece may take, long enough for a server to load the model; nothing here says whether it is.</summary>
    internal TimeSpan FirstPiece { get; init; } = PredictionHttp.Loading;

    /// <summary>How long between pieces once the first has come.</summary>
    internal TimeSpan Gap { get; init; } = PredictionHttp.FirstPiece;

    /// <summary>The model's parameters the settings tune. The completions API takes temperature and top_p of them.</summary>
    internal PredictionTuning Tuning { get; init; } = PredictionTuning.Usual;

    /// <summary>
    /// Asks llama-server to keep the prompt's start in its cache (cache_prompt) so that the next request, which shares
    /// most of it, only reads what changed. Sent only to a llama.cpp server: others may refuse a field they don't know.
    /// </summary>
    internal bool CachePrompt { get; init; }

    /// <summary>The llama-server slot (id_slot) to use, -1 for any; sent only with <see cref="CachePrompt"/>.</summary>
    internal int Slot { get; init; } = -1;

    public async IAsyncEnumerable<string> Complete(PredictionWireRequest request, [EnumeratorCancellation] CancellationToken cancel)
    {
        if (request.Turns is { Count: > 0 } turns)
        {
            await foreach (string piece in Chat(turns, request.MaxTokens, cancel).WithCancellation(cancel).ConfigureAwait(false))
                yield return piece;
            yield break;
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, api)
        {
            Content = PredictionHttp.Json(writer => Body(writer, request)),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (!string.IsNullOrEmpty(key))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(FirstPiece);
        TimeSpan wait = FirstPiece;

        using HttpResponseMessage response = await PredictionHttp.Send(http, message, deadline, cancel, api, key).ConfigureAwait(false);
        using WireLines reader = await PredictionHttp.Reader(response, deadline, cancel, api).ConfigureAwait(false);

        // A thinking model's <think> block, which some servers leave in the text, is never the answer
        var filter = new ThinkFilter();
        while (await PredictionHttp.Line(reader, deadline, wait, cancel, api).ConfigureAwait(false))
        {
            wait = Gap;

            // Server-sent events: data lines matter, blank lines, comments and other fields don't
            if (!WireLines.Data(reader.Current, out ReadOnlyMemory<byte> data))
                continue;
            if (WireLines.IsDone(data))
            {
                if (filter.End() is { Length: > 0 } rest)
                    yield return rest;
                yield break;
            }

            WireChunk chunk = WireChunk.Read(data.Span, WireShape.OpenAiText, api);
            if (chunk.Error is { } error)
                throw PredictionHttp.Failed(error, api, key);

            string answer = filter.Feed(chunk.Text ?? string.Empty);
            if (chunk.Finished && filter.End() is { Length: > 0 } tail)
                answer += tail;
            if (answer.Length > 0)
                yield return answer;
            if (chunk.Finished)
                yield break;
        }
    }

    // A chat format's request: /v1/chat/completions, streamed, thinking off, the answer's pieces without what the model
    // thinks. Leaving the loop early disposes the stream, which closes the connection
    private async IAsyncEnumerable<string> Chat(IReadOnlyList<ChatTurn> turns, int maxTokens, [EnumeratorCancellation] CancellationToken cancel)
    {
        var chat = new OpenAiChat(http, OpenAiChat.Url(api.ToString()) ?? api, model, key)
        {
            Tuning = Tuning with { Thinking = PredictionTuning.THINK_OFF },
            MaxTokens = maxTokens,
            CachePrompt = CachePrompt,
            Slot = Slot,
        };
        await foreach (ChatPiece piece in chat.Stream(turns, cancel).WithCancellation(cancel).ConfigureAwait(false))
        {
            if (!piece.Thinking)
                yield return piece.Text;
        }
    }

    // {model, prompt, max_tokens, temperature, top_p, stop, stream}, with the suffix when there is one
    private void Body(Utf8JsonWriter writer, PredictionWireRequest request)
    {
        writer.WriteStartObject();
        writer.WriteString("model", model);
        writer.WriteString("prompt", request.Prompt);
        if (request.Suffix is not null)
            writer.WriteString("suffix", request.Suffix);
        writer.WriteNumber("max_tokens", request.MaxTokens);
        writer.WriteNumber("temperature", Tuning.Temperature);
        if (Tuning.TopP > 0)
            writer.WriteNumber("top_p", Tuning.TopP);
        if (request.Stop.Length > 0)
        {
            writer.WriteStartArray("stop");
            foreach (string stop in request.Stop.Take(MOST_STOPS))
                writer.WriteStringValue(stop);
            writer.WriteEndArray();
        }
        writer.WriteBoolean("stream", true);
        if (CachePrompt)
        {
            writer.WriteBoolean("cache_prompt", true);
            if (Slot >= 0)
                writer.WriteNumber("id_slot", Slot);
        }
        writer.WriteEndObject();
    }
}
