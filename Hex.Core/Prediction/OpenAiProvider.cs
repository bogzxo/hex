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

    public async IAsyncEnumerable<string> Complete(PredictionWireRequest request, [EnumeratorCancellation] CancellationToken cancel)
    {
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
        using StreamReader reader = await PredictionHttp.Reader(response, deadline, cancel, api).ConfigureAwait(false);

        while (await PredictionHttp.Line(reader, deadline, wait, cancel, api).ConfigureAwait(false) is { } line)
        {
            wait = Gap;

            // Server-sent events: data lines matter, blank lines, comments and other fields don't
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            string data = line.AsSpan(5).Trim().ToString();
            if (data.Length == 0)
                continue;
            if (data == "[DONE]")
                yield break;

            string? piece = null;
            bool finished;
            using (JsonDocument document = PredictionHttp.Parse(data, api))
            {
                JsonElement root = document.RootElement;
                if (PredictionHttp.Error(root) is { } error)
                    throw PredictionHttp.Failed(error, api, key);

                finished = false;
                if (root.TryGetProperty("choices", out JsonElement choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
                {
                    JsonElement choice = choices[0];
                    if (choice.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
                        piece = text.GetString();
                    finished = choice.TryGetProperty("finish_reason", out JsonElement reason) && reason.ValueKind == JsonValueKind.String;
                }
            }

            if (!string.IsNullOrEmpty(piece))
                yield return piece;
            if (finished)
                yield break;
        }
    }

    // {model, prompt, max_tokens, temperature, stop, stream}, with the suffix when there is one
    private void Body(Utf8JsonWriter writer, PredictionWireRequest request)
    {
        writer.WriteStartObject();
        writer.WriteString("model", model);
        writer.WriteString("prompt", request.Prompt);
        if (request.Suffix is not null)
            writer.WriteString("suffix", request.Suffix);
        writer.WriteNumber("max_tokens", request.MaxTokens);
        writer.WriteNumber("temperature", 0);
        if (request.Stop.Length > 0)
        {
            writer.WriteStartArray("stop");
            foreach (string stop in request.Stop.Take(MOST_STOPS))
                writer.WriteStringValue(stop);
            writer.WriteEndArray();
        }
        writer.WriteBoolean("stream", true);
        writer.WriteEndObject();
    }
}
