using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>A linked model as the loopback endpoint lists it: the id a request names it by and the device it runs on.</summary>
internal sealed record LinkAiLoopbackModel(string Id, string Device, string Name = "");

/// <summary>
/// An OpenAI-compatible endpoint on this machine's loopback that forwards chat completions through Link to the linked
/// device's model (docs/hex-link-plan.md, 5.7), so what can't hold a Link connection of its own (the Local agent, which runs
/// as a process of its own, or any OpenAI-compatible tool) can use a linked model. It listens on 127.0.0.1 only, answers a
/// request only with the bearer token it was made with, refuses a browser's (an Origin header) and a Host that isn't
/// loopback (DNS rebinding), takes a body of 1 MB at most and a handful of connections at a time. GET /v1/models lists the
/// linked models, POST /v1/chat/completions answers a chat, streamed as server-sent events when the request says stream, whole otherwise.
/// </summary>
internal sealed class LinkAiLoopback : IDisposable
{
    private const int MOST_HEAD = 16 * 1024, MOST_CONNECTIONS = 8;

    private readonly TcpListener listener;
    private readonly Func<IReadOnlyList<LinkAiLoopbackModel>> models;
    private readonly CancellationTokenSource stopped = new();
    private readonly SemaphoreSlim connections = new(MOST_CONNECTIONS, MOST_CONNECTIONS);
    private readonly byte[] token;

    public LinkAiLoopback(Func<IReadOnlyList<LinkAiLoopbackModel>> models, string? token = null, int port = 0)
    {
        this.models = models;
        Token = token ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        this.token = Encoding.UTF8.GetBytes(Token);
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start(16);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Accept();
    }

    /// <summary>The port it listens on, on 127.0.0.1.</summary>
    public int Port { get; }

    /// <summary>The bearer token a request has to carry.</summary>
    public string Token { get; }

    /// <summary>The address an OpenAI client is given, ending /v1.</summary>
    public string Url => $"http://127.0.0.1:{Port}/v1";

    public void Dispose()
    {
        try { stopped.Cancel(); } catch (ObjectDisposedException) { }
        try { listener.Stop(); } catch (SocketException) { }
    }

    private async Task Accept()
    {
        while (!stopped.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stopped.Token).ConfigureAwait(false);
            }
            catch (Exception) when (stopped.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                continue;
            }

            if (!await connections.WaitAsync(0).ConfigureAwait(false))
            {
                client.Dispose();
                continue;
            }

            _ = Serve(client);
        }
    }

    private async Task Serve(TcpClient client)
    {
        try
        {
            using (client)
            {
                client.NoDelay = true;
                using NetworkStream stream = client.GetStream();
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);
                limit.CancelAfter(TimeSpan.FromMinutes(30));
                await Handle(stream, limit.Token).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // A client that went, or sent nonsense: its connection is closed, nothing else is touched
        }
        finally
        {
            connections.Release();
        }
    }

    private async Task Handle(NetworkStream stream, CancellationToken cancel)
    {
        (string? method, string? path, Dictionary<string, string> headers, byte[] rest) = await ReadHead(stream, cancel).ConfigureAwait(false);
        if (method is null || path is null)
        {
            await Reply(stream, 400, "Bad request.", cancel).ConfigureAwait(false);
            return;
        }

        // A browser page can't be told apart from a tool by anything but these, and none is let in
        if (headers.ContainsKey("origin") || !HostIsLoopback(headers.GetValueOrDefault("host")))
        {
            await Reply(stream, 403, "Not for browsers.", cancel).ConfigureAwait(false);
            return;
        }

        if (!TokenMatches(headers.GetValueOrDefault("authorization")))
        {
            await Reply(stream, 401, "Needs the bearer token Hex gave.", cancel).ConfigureAwait(false);
            return;
        }

        int query = path.IndexOf('?', StringComparison.Ordinal);
        string route = (query >= 0 ? path[..query] : path).TrimEnd('/');
        if (method == "GET" && route == "/v1/models")
        {
            var data = new JsonArray([.. models().Select(model => (JsonNode)new JsonObject { ["id"] = model.Id, ["object"] = "model", ["owned_by"] = model.Device })]);
            await Reply(stream, 200, new JsonObject { ["object"] = "list", ["data"] = data }.ToJsonString(), cancel, json: true).ConfigureAwait(false);
            return;
        }

        if (method != "POST" || route != "/v1/chat/completions")
        {
            await Reply(stream, 404, "No such address.", cancel).ConfigureAwait(false);
            return;
        }

        int length = int.TryParse(headers.GetValueOrDefault("content-length"), out int said) ? said : -1;
        if (length < 0 || length > HexLinkAiRequest.MOST_BODY)
        {
            await Reply(stream, length < 0 ? 411 : 413, "A chat request has a body of 1 MB at most.", cancel).ConfigureAwait(false);
            return;
        }

        byte[] body = new byte[length];
        int have = Math.Min(rest.Length, length);
        Array.Copy(rest, body, have);
        while (have < length)
        {
            int read = await stream.ReadAsync(body.AsMemory(have, length - have), cancel).ConfigureAwait(false);
            if (read == 0)
                return;
            have += read;
        }

        await Chat(stream, Encoding.UTF8.GetString(body), cancel).ConfigureAwait(false);
    }

    // The chat sent over Link; the answer's events relayed, or joined into one answer when the request wasn't streaming
    private static async Task Chat(NetworkStream stream, string body, CancellationToken cancel)
    {
        bool streaming;
        try
        {
            streaming = JsonNode.Parse(body) is JsonObject asked && asked["stream"] is JsonValue flag && flag.TryGetValue(out bool on) && on;
        }
        catch (JsonException)
        {
            await Reply(stream, 400, "A chat request is JSON.", cancel).ConfigureAwait(false);
            return;
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, LinkAiRouter.Api) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        HttpResponseMessage response;
        StreamReader reader;
        try
        {
            response = await LinkAiRouter.Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false), Encoding.UTF8);
        }
        catch (PredictionException e)
        {
            await Reply(stream, StatusOf(e), e.Message, cancel).ConfigureAwait(false);
            return;
        }

        using (response)
        using (reader)
        {
            if (streaming)
                await Relay(stream, reader, cancel).ConfigureAwait(false);
            else
                await Reply(stream, 200, await reader.ReadToEndAsync(cancel).ConfigureAwait(false), cancel, json: true).ConfigureAwait(false);
        }
    }

    // The events written as they come; an error in the middle as an error event, which the client reads as the chat failing
    private static async Task Relay(NetworkStream stream, StreamReader reader, CancellationToken cancel)
    {
        await Write(stream, "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n", cancel).ConfigureAwait(false);
        try
        {
            while (await reader.ReadLineAsync(cancel).ConfigureAwait(false) is { } line)
                await Write(stream, line + "\n", cancel).ConfigureAwait(false);
        }
        catch (PredictionException e)
        {
            await Write(stream, "data: " + new JsonObject { ["error"] = new JsonObject { ["message"] = e.Message } }.ToJsonString() + "\n\n", cancel).ConfigureAwait(false);
        }
    }

    private static int StatusOf(PredictionException error) => error.Status switch
    {
        PredictionStatus.Unauthorized or PredictionStatus.Refused => 403,
        PredictionStatus.NoSuchModel => 404,
        PredictionStatus.Unreachable => 503,
        PredictionStatus.TimedOut => 504,
        _ => 502,
    };

    private bool TokenMatches(string? authorization)
    {
        const string BEARER = "Bearer ";
        if (authorization is null || !authorization.StartsWith(BEARER, StringComparison.OrdinalIgnoreCase))
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(authorization[BEARER.Length..].Trim()), token);
    }

    private bool HostIsLoopback(string? host)
    {
        if (host is null)
            return false;
        return host.Equals($"127.0.0.1:{Port}", StringComparison.Ordinal) || host.Equals($"localhost:{Port}", StringComparison.OrdinalIgnoreCase);
    }

    // The request line and headers, and what of the body came with them
    private static async Task<(string? Method, string? Path, Dictionary<string, string> Headers, byte[] Leftover)> ReadHead(NetworkStream stream, CancellationToken cancel)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        byte[] buffer = new byte[MOST_HEAD];
        int used = 0, end = -1;
        while (end < 0)
        {
            if (used == buffer.Length)
                return (null, null, headers, []);
            int read = await stream.ReadAsync(buffer.AsMemory(used), cancel).ConfigureAwait(false);
            if (read == 0)
                return (null, null, headers, []);
            used += read;
            end = buffer.AsSpan(0, used).IndexOf("\r\n\r\n"u8);
        }

        string[] lines = Encoding.ASCII.GetString(buffer, 0, end).Split("\r\n");
        string[] first = lines[0].Split(' ');
        if (first.Length < 3)
            return (null, null, headers, []);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        return (first[0], first[1], headers, buffer.AsSpan(end + 4, used - end - 4).ToArray());
    }

    private static Task Reply(NetworkStream stream, int status, string text, CancellationToken cancel, bool json = false)
    {
        string body = json ? text : new JsonObject { ["error"] = new JsonObject { ["message"] = text } }.ToJsonString();
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        string head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        return Write(stream, head, cancel, bytes);
    }

    private static async Task Write(NetworkStream stream, string text, CancellationToken cancel, byte[]? after = null)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), cancel).ConfigureAwait(false);
        if (after is not null)
            await stream.WriteAsync(after, cancel).ConfigureAwait(false);
        await stream.FlushAsync(cancel).ConfigureAwait(false);
    }
}
