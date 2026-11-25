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

    /// <summary>The model's parameters the settings tune, sent in options. A prediction never thinks, whatever they say.</summary>
    internal PredictionTuning Tuning { get; init; } = PredictionTuning.Usual;

    /// <summary>The system prompt, sent only when the prompt goes through the model's template (with a suffix), empty for none.</summary>
    internal string SystemPrompt { get; init; } = string.Empty;

    public async IAsyncEnumerable<string> Complete(PredictionWireRequest request, [EnumeratorCancellation] CancellationToken cancel)
    {
        IAsyncEnumerable<string> source = request.Turns is { Count: > 0 } turns ? Chat(turns, request.MaxTokens, cancel) : Stream(request, cancel);
        await using IAsyncEnumerator<string> pieces = source.GetAsyncEnumerator(cancel);
        while (true)
        {
            try
            {
                if (!await pieces.MoveNextAsync().ConfigureAwait(false))
                    yield break;
            }
            catch (PredictionException e) when (e.Status == PredictionStatus.NoSuchModel)
            {
                throw await Explained(e, cancel).ConfigureAwait(false);
            }
            yield return pieces.Current;
        }
    }

    /// <summary>
    /// Asks the server to load the model now, a request with no prompt, so the load (13 s for Zeta 2 here) isn't paid on
    /// the first keystroke, and keep it for half an hour. Null when it's loaded or when nothing is worth saying, else
    /// what is wrong with the server or the model.
    /// </summary>
    public async Task<PredictionException?> Preload(CancellationToken cancel)
    {
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, api)
            {
                Content = PredictionHttp.Json(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("model", model);
                    writer.WriteString("keep_alive", "30m");
                    writer.WriteEndObject();
                }),
            };
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            deadline.CancelAfter(Loading);
            using HttpResponseMessage response = await PredictionHttp.Send(http, message, deadline, cancel, api, null).ConfigureAwait(false);
            return null;
        }
        catch (PredictionException e) when (e.Status == PredictionStatus.NoSuchModel)
        {
            return await Explained(e, cancel).ConfigureAwait(false);
        }
        catch (PredictionException e) when (e.Status == PredictionStatus.Unreachable)
        {
            return e;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// A "no such model" with what the user needs to put it right: the model and the server by name, what the server does
    /// have (asked of /api/tags, a few seconds at most) and where Ollama reads its models from.
    /// </summary>
    private async Task<PredictionException> Explained(PredictionException e, CancellationToken cancel)
    {
        string has;
        try
        {
            string[] names = await InstalledModels(http, api, cancel).ConfigureAwait(false);
            has = names.Length == 0 ? "The server has no models at all." : "The server has: " + string.Join(", ", names.Take(6)) + ".";
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            has = "";
        }

        return new PredictionException(PredictionStatus.NoSuchModel,
            $"{e.Message} (asked for {model}). {has} If it should have it, Ollama may be reading another model folder: set OLLAMA_MODELS and restart Ollama.".Replace(". .", ".").Replace("  ", " "));
    }

    internal static async Task<string[]> InstalledModels(HttpClient client, Uri generate, CancellationToken cancel)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        wait.CancelAfter(TimeSpan.FromSeconds(3));
        var tags = new UriBuilder(generate) { Path = generate.AbsolutePath[..^"generate".Length] + "tags" }.Uri;
        using HttpResponseMessage response = await client.GetAsync(tags, wait.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(wait.Token).ConfigureAwait(false));
        if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            throw new JsonException("Expected a list of Ollama models.");
        return [.. models.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
            .Select(item => item.GetProperty("name").GetString()!)
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The tokens a prediction may think with on top of its most, when the settings ask it to think.</summary>
    internal const int THINKING_BUDGET = 1024;

    // Whether the settings ask the model to think, on or a level: its stop sequences are then kept here, not sent, so
    // that one met while it thinks doesn't end the answer before it starts
    private bool ThinkingAsked => Tuning.Thinking is not (PredictionTuning.THINK_MODEL or PredictionTuning.THINK_OFF);

    // A chat format's request: /api/chat, streamed, thinking off, the answer's pieces without what the model thinks.
    // Leaving the loop early disposes the stream, which closes the connection
    private async IAsyncEnumerable<string> Chat(IReadOnlyList<ChatTurn> turns, int maxTokens, [EnumeratorCancellation] CancellationToken cancel)
    {
        var chat = new OllamaChat(http, OllamaChat.Url(api.ToString()) ?? api, model)
        {
            Tuning = Tuning with { Thinking = PredictionTuning.THINK_OFF },
            MaxTokens = maxTokens,
        };
        await foreach (ChatPiece piece in chat.Stream(turns, cancel).WithCancellation(cancel).ConfigureAwait(false))
        {
            if (!piece.Thinking)
                yield return piece.Text;
        }
    }

    private async IAsyncEnumerable<string> Stream(PredictionWireRequest request, [EnumeratorCancellation] CancellationToken cancel)
    {
        // Ollama's think needs the model's template, which a raw prompt doesn't go through
        object? think = request.Suffix is null ? null : await OllamaChat.Think(http, api, model, Tuning.Thinking, cancel).ConfigureAwait(false);
        bool stopsHere = ThinkingAsked && request.Stop.Length > 0;
        using var message = new HttpRequestMessage(HttpMethod.Post, api)
        {
            Content = PredictionHttp.Json(writer => Body(writer, request, think, stopsHere)),
        };
        var filter = new ThinkFilter();
        var shown = new System.Text.StringBuilder();
        int given = 0, longestStop = stopsHere ? request.Stop.Max(stop => stop.Length) : 0;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        TimeSpan wait = Loading;
        deadline.CancelAfter(wait);

        using HttpResponseMessage response = await PredictionHttp.Send(http, message, deadline, cancel, api, null).ConfigureAwait(false);
        using WireLines reader = await PredictionHttp.Reader(response, deadline, cancel, api).ConfigureAwait(false);

        while (await PredictionHttp.Line(reader, deadline, wait, cancel, api).ConfigureAwait(false))
        {
            wait = FirstPiece;
            if (reader.Current.Length == 0)
                continue;

            WireChunk chunk = WireChunk.Read(reader.Current.Span, WireShape.OllamaGenerate, api);
            if (chunk.Error is { } error)
                throw PredictionHttp.Failed(error, api, null);
            bool done = chunk.Finished;

            // What it thinks comes in Ollama's thinking field, which is never the answer, or between <think> tags in the
            // answer, which the filter takes out. Either way each line keeps the wait going
            string answer = filter.Feed(chunk.Text ?? string.Empty);
            if (done && filter.End() is { Length: > 0 } tail)
                answer += tail;
            if (!stopsHere)
            {
                if (answer.Length > 0)
                    yield return answer;
            }
            else
            {
                // The stop sequences met in the answer here, what could be the start of one held back till it's known
                shown.Append(answer);
                string sofar = shown.ToString();
                int stopAt = request.Stop.Select(stop => sofar.IndexOf(stop, StringComparison.Ordinal)).Where(at => at >= 0).DefaultIfEmpty(-1).Min();
                int safe = stopAt >= 0 ? stopAt : done ? sofar.Length : Math.Max(given, sofar.Length - (longestStop - 1));
                if (safe > given)
                {
                    yield return sofar[given..safe];
                    given = safe;
                }
                if (stopAt >= 0)
                    yield break;
            }
            if (done)
                yield break;
        }
    }

    // {model, prompt, raw, stream, keep_alive, think?, options: {num_predict, temperature, top_p, top_k, num_ctx,
    // repeat_penalty, stop}}, with the suffix and the system prompt when there is a suffix. A raw prompt has no template to
    // put a system prompt or think in. Asked to think, it has a budget to think with besides its most tokens
    private void Body(Utf8JsonWriter writer, PredictionWireRequest request, object? think, bool stopsHere)
    {
        writer.WriteStartObject();
        writer.WriteString("model", model);
        writer.WriteString("prompt", request.Prompt);
        if (request.Suffix is not null)
        {
            writer.WriteString("suffix", request.Suffix);
            if (!string.IsNullOrWhiteSpace(SystemPrompt))
                writer.WriteString("system", SystemPrompt);
        }
        writer.WriteBoolean("raw", request.Suffix is null);
        writer.WriteBoolean("stream", true);
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
        writer.WriteNumber("num_predict", request.MaxTokens + (ThinkingAsked ? THINKING_BUDGET : 0));
        Tuning.WriteOllamaOptions(writer);
        if (request.Stop.Length > 0 && !stopsHere)
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
