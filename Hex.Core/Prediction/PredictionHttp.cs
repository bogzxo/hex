using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;

namespace Hex.Core;

/// <summary>
/// What the providers that speak HTTP share: the request body written as JSON, the request sent with its deadline, the
/// answer read a line at a time, and every way it can fail turned into a status and a sentence. The sentence names the
/// server's host and never the key or the prompt.
/// </summary>
internal static class PredictionHttp
{
    /// <summary>How long between pieces of an answer once it has started.</summary>
    public static readonly TimeSpan FirstPiece = TimeSpan.FromSeconds(15);

    /// <summary>How long the first piece may take, the server perhaps loading the model first.</summary>
    public static readonly TimeSpan Loading = TimeSpan.FromSeconds(60);

    // The most of a server's own error message put in a status sentence
    private const int MOST_MESSAGE = 200;

    /// <summary>A JSON body written by <paramref name="write"/>.</summary>
    public static HttpContent Json(Action<Utf8JsonWriter> write) => PooledJsonContent.Of(write);

    /// <summary>
    /// Sends the request and hands back the response once its headers are in, or throws what went wrong. The deadline
    /// is <paramref name="deadline"/>'s; <paramref name="cancel"/> is the caller's, whose cancelling isn't an error.
    /// </summary>
    public static async Task<HttpResponseMessage> Send(
        HttpClient http, HttpRequestMessage request, CancellationTokenSource deadline, CancellationToken cancel, Uri api, string? key)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!deadline.IsCancellationRequested && exception.InnerException is TimeoutException)
        {
            // The handler's connection timeout can fire before the request deadline.
            throw Unreachable(api, exception);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw TimedOut(api);
        }
        catch (HttpRequestException exception) when (!cancel.IsCancellationRequested)
        {
            throw Unreachable(api, exception);
        }

        if (response.IsSuccessStatusCode)
            return response;

        using (response)
        {
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or OperationCanceledException && !cancel.IsCancellationRequested)
            {
                body = "";
            }

            throw Failure(response.StatusCode, body, api, key);
        }
    }

    /// <summary>The response's body to read a line at a time, as bytes.</summary>
    public static async Task<WireLines> Reader(HttpResponseMessage response, CancellationTokenSource deadline, CancellationToken cancel, Uri api)
    {
        try
        {
            Stream stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            return new WireLines(stream);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw TimedOut(api);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException && !cancel.IsCancellationRequested)
        {
            throw Unreachable(api, exception);
        }
    }

    /// <summary>
    /// The next line of the answer, in <see cref="WireLines.Current"/>; false at its end. A line already read waits for
    /// nothing, one still to come is given <paramref name="wait"/>.
    /// </summary>
    public static async ValueTask<bool> Line(WireLines reader, CancellationTokenSource deadline, TimeSpan wait, CancellationToken cancel, Uri api)
    {
        if (reader.TryNext())
            return true;
        try
        {
            deadline.CancelAfter(wait);
            return await reader.More(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw TimedOut(api);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException && !cancel.IsCancellationRequested)
        {
            throw new PredictionException(PredictionStatus.Unreachable, $"The connection to {Host(api)} was lost.");
        }
    }

    /// <summary>A line of the answer as JSON, or a bad answer when it isn't.</summary>
    public static JsonDocument Parse(string line, Uri api)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            throw new PredictionException(PredictionStatus.BadAnswer, $"{Host(api)} answered with something that isn't JSON.");
        }
    }

    /// <summary>A line of the answer, as bytes, as JSON, or a bad answer when it isn't.</summary>
    public static JsonDocument Parse(ReadOnlyMemory<byte> line, Uri api)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            throw new PredictionException(PredictionStatus.BadAnswer, $"{Host(api)} answered with something that isn't JSON.");
        }
    }

    /// <summary>
    /// The error a server put in its answer: Ollama's {"error": "..."} or OpenAI's {"error": {"message": "..."}}, null
    /// when there's none.
    /// </summary>
    public static string? Error(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out JsonElement error))
            return null;

        return error.ValueKind switch
        {
            JsonValueKind.String => error.GetString() ?? "",
            JsonValueKind.Object when error.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String
                => message.GetString() ?? "",
            JsonValueKind.Null => null,
            _ => error.ToString(),
        };
    }

    /// <summary>An error the server sent in the middle of an answer, as a status and a sentence.</summary>
    public static PredictionException Failed(string message, Uri api, string? key) =>
        Failure(HttpStatusCode.OK, message, api, key, json: false);

    /// <summary>A response that wasn't a success, as a status and a sentence.</summary>
    public static PredictionException Failure(HttpStatusCode code, string body, Uri api, string? key, bool json = true)
    {
        string message = body;
        if (json && body.Length > 0)
        {
            try
            {
                using var document = JsonDocument.Parse(body);
                message = Error(document.RootElement) ?? "";
            }
            catch (JsonException)
            {
                message = "";
            }
        }

        message = Clean(message, key);
        string host = Host(api);
        bool missingModel = message.Contains("model", StringComparison.OrdinalIgnoreCase)
            && message.Contains("not found", StringComparison.OrdinalIgnoreCase);

        if (code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return new PredictionException(PredictionStatus.Unauthorized, $"{host} refused the key ({(int)code}).");
        if (missingModel || code == HttpStatusCode.NotFound)
            return new PredictionException(PredictionStatus.NoSuchModel,
                message.Length > 0 ? $"{host} has no such model: {message}" : $"{host} has no such model, or no such address ({(int)code}).");
        if (code == HttpStatusCode.OK)
            // The address of a loopback (a link's AI is served from one) says nothing to the person, the message does
            return new PredictionException(PredictionStatus.BadAnswer,
                api.IsLoopback && message.Length > 0 ? message : $"{host} stopped with an error: {message}");
        return new PredictionException(PredictionStatus.BadAnswer,
            message.Length > 0 ? $"{host} answered {(int)code}: {message}" : $"{host} answered {(int)code}.");
    }

    /// <summary>The server couldn't be reached: refused, no such name, the TLS handshake failed.</summary>
    public static PredictionException Unreachable(Uri api, Exception exception)
    {
        string why = exception.InnerException is SocketException socket
            ? socket.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "nothing is listening there",
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "there's no such host",
                SocketError.TimedOut => "it didn't answer",
                _ => socket.SocketErrorCode.ToString(),
            }
            : exception.InnerException is System.Security.Authentication.AuthenticationException ? "its certificate wasn't trusted"
            : "the connection failed";
        return new PredictionException(PredictionStatus.Unreachable, $"Can't reach {Host(api)}: {why}.");
    }

    /// <summary>The server took longer than its deadline.</summary>
    public static PredictionException TimedOut(Uri api) =>
        new(PredictionStatus.TimedOut, $"{Host(api)} took too long to answer.");

    /// <summary>The host and port of a URL, the part of it that is safe to show: no path, no query, no user.</summary>
    public static string Host(Uri api) => api.IsDefaultPort ? api.Host : $"{api.Host}:{api.Port}";

    // A server's message cut short, on one line, and with the key taken out should the server have repeated it
    private static string Clean(string message, string? key)
    {
        if (!string.IsNullOrEmpty(key))
            message = message.Replace(key, "***", StringComparison.Ordinal);
        message = message.ReplaceLineEndings(" ").Trim();
        return message.Length > MOST_MESSAGE ? message[..MOST_MESSAGE] + "…" : message;
    }
}
