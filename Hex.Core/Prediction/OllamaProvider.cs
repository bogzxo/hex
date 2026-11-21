using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Hex.Core;

/// <summary>
/// Ollama's own API, POST /api/generate with the prompt sent raw (no template round it) and streamed back as lines of
/// JSON, {"response": "...", "done": false}. The model is kept loaded for half an hour between requests.
/// </summary>
internal sealed class OllamaProvider(HttpClient http, Uri api, string model) : IPredictionProvider
{
    /// <summary>The address requests go to, /api/generate on the server.</summary>
    public Uri Api => api;

    /// <summary>How long between pieces once the first has come.</summary>
    internal TimeSpan FirstPiece { get; init; } = PredictionHttp.FirstPiece;

    /// <summary>
    /// How long the first piece may take. Long enough for Ollama to load the model, which it does again whenever another
    /// model took its memory, and nothing says beforehand whether it will; a request typing has left behind is cancelled
    /// long before this.
    /// </summary>
    internal TimeSpan Loading { get; init; } = PredictionHttp.Loading;

    public async IAsyncEnumerable<string> Complete(PredictionWireRequest request, [EnumeratorCancellation] CancellationToken cancel)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, api)
        {
            Content = PredictionHttp.Json(writer => Body(writer, request)),
        };

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        TimeSpan wait = Loading;
        deadline.CancelAfter(wait);

        using HttpResponseMessage response = await PredictionHttp.Send(http, message, deadline, cancel, api, null).ConfigureAwait(false);
        using StreamReader reader = await PredictionHttp.Reader(response, deadline, cancel, api).ConfigureAwait(false);

        while (await PredictionHttp.Line(reader, deadline, wait, cancel, api).ConfigureAwait(false) is { } line)
        {
            wait = FirstPiece;
            if (line.Length == 0)
                continue;

            string? piece;
            bool done;
            using (JsonDocument document = PredictionHttp.Parse(line, api))
            {
                JsonElement root = document.RootElement;
                if (PredictionHttp.Error(root) is { } error)
                    throw PredictionHttp.Failed(error, api, null);
                piece = root.TryGetProperty("response", out JsonElement text) && text.ValueKind == JsonValueKind.String
                    ? text.GetString()
                    : null;
                done = root.TryGetProperty("done", out JsonElement doneElement) && doneElement.ValueKind == JsonValueKind.True;
            }

            if (!string.IsNullOrEmpty(piece))
                yield return piece;
            if (done)
                yield break;
        }
    }

    // {model, prompt, raw, stream, keep_alive, options: {num_predict, temperature, stop}}, with the suffix when there is one
    private void Body(Utf8JsonWriter writer, PredictionWireRequest request)
    {
        writer.WriteStartObject();
        writer.WriteString("model", model);
        writer.WriteString("prompt", request.Prompt);
        if (request.Suffix is not null)
            writer.WriteString("suffix", request.Suffix);
        writer.WriteBoolean("raw", request.Suffix is null);
        writer.WriteBoolean("stream", true);
        writer.WriteString("keep_alive", "30m");
        writer.WriteStartObject("options");
        writer.WriteNumber("num_predict", request.MaxTokens);
        writer.WriteNumber("temperature", 0);
        if (request.Stop.Length > 0)
        {
            writer.WriteStartArray("stop");
            foreach (string stop in request.Stop)
                writer.WriteStringValue(stop);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
        writer.WriteEndObject();
    }
}
