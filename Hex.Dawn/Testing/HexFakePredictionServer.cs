using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Hex.Dawn;

/// <summary>
/// A model server for the self test, on a loopback port: enough HTTP/1.1 to answer Ollama's POST /api/generate (lines of
/// JSON) and the OpenAI POST /v1/completions (server-sent events) from a script. It can answer in pieces with a pause
/// between, stall until the client goes away (and notes that it went), refuse the key, have no such model, or answer
/// with something that isn't JSON. It counts what it's asked and keeps the last body and Authorization header.
/// </summary>
internal sealed class HexFakePredictionServer : IDisposable
{
    /// <summary>What the server does with the next requests.</summary>
    public enum Kind { Answer, Stall, Unauthorized, NoSuchModel, Garbage }

    /// <summary>How the server answers, and with what text for a prompt, in so many pieces with a pause between.</summary>
    public sealed record Reply(Kind Kind, Func<string, string>? Text = null, int Pieces = 3, int PauseMs = 10);

    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    private int requests, stalled, gone, accepted;

    public HexFakePredictionServer()
    {
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(Accept);
    }

    /// <summary>The port it listens on.</summary>
    public int Port { get; }

    /// <summary>Its address, as the settings take it.</summary>
    public string Url => $"http://127.0.0.1:{Port}";

    /// <summary>How the next requests are answered.</summary>
    public Reply Next { get => Volatile.Read(ref field); set => Volatile.Write(ref field, value); } = new(Kind.Answer, _ => string.Empty);

    /// <summary>The requests it was sent.</summary>
    public int Requests => Volatile.Read(ref requests);

    /// <summary>The requests stalled, and how many of those the client went away from.</summary>
    public int Stalled => Volatile.Read(ref stalled);

    public int Gone => Volatile.Read(ref gone);

    /// <summary>The connections it took.</summary>
    public int Accepted => Volatile.Read(ref accepted);

    /// <summary>The last request's path, body, prompt and Authorization header.</summary>
    public string LastPath { get; private set; } = string.Empty;

    public string LastBody { get; private set; } = string.Empty;

    public string LastPrompt { get; private set; } = string.Empty;

    public string LastAuthorization { get; private set; } = string.Empty;

    /// <summary>A port nothing listens on, for a server that can't be reached.</summary>
    public static int DeadPort()
    {
        var taken = new TcpListener(IPAddress.Loopback, 0);
        taken.Start();
        int port = ((IPEndPoint)taken.LocalEndpoint).Port;
        taken.Stop();
        return port;
    }

    /// <summary>
    /// An answer in Zeta 2's shape: the editable region of the prompt, between &lt;&lt;&lt;&lt;&lt;&lt;&lt; CURRENT and =======, without the
    /// cursor's marker, changed, then &gt;&gt;&gt;&gt;&gt;&gt;&gt; UPDATED.
    /// </summary>
    public static Func<string, string> Zeta2(Func<string, string> change) => prompt =>
    {
        const string CURRENT = "<<<<<<< CURRENT\n", SEPARATOR = "=======\n";
        int start = prompt.LastIndexOf(CURRENT, StringComparison.Ordinal);
        int end = start < 0 ? -1 : prompt.IndexOf(SEPARATOR, start, StringComparison.Ordinal);
        if (end < 0)
            return string.Empty;
        string region = prompt[(start + CURRENT.Length)..end].Replace("<|user_cursor|>", string.Empty, StringComparison.Ordinal);
        string changed = change(region);
        return (changed.EndsWith('\n') ? changed : changed + "\n") + ">>>>>>> UPDATED\n";
    };

    public void Dispose()
    {
        stop.Cancel();
        listener.Stop();
    }

    private async Task Accept()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }
            Interlocked.Increment(ref accepted);
            _ = Task.Run(() => Serve(client));
        }
    }

    // One request on a connection, answered and closed
    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                if (await ReadRequest(stream).ConfigureAwait(false) is not { } request)
                    return;

                Interlocked.Increment(ref requests);
                Reply reply = Next;
                bool openAi = request.Path.Contains("/v1/", StringComparison.Ordinal);
                switch (reply.Kind)
                {
                    case Kind.Unauthorized:
                        await Write(stream, "401 Unauthorized", "application/json", openAi
                            ? "{\"error\":{\"message\":\"Incorrect API key provided.\",\"type\":\"invalid_request_error\"}}"
                            : "{\"error\":\"unauthorized\"}").ConfigureAwait(false);
                        return;

                    case Kind.NoSuchModel:
                        await Write(stream, "404 Not Found", "application/json", openAi
                            ? "{\"error\":{\"message\":\"model 'fake' not found\",\"type\":\"not_found_error\"}}"
                            : "{\"error\":\"model 'fake' not found\"}").ConfigureAwait(false);
                        return;

                    case Kind.Garbage:
                        await Write(stream, "200 OK", "application/json", "this is not json at all\n").ConfigureAwait(false);
                        return;

                    case Kind.Stall:
                        await Head(stream, openAi).ConfigureAwait(false);
                        Interlocked.Increment(ref stalled);
                        await WaitForGoing(stream).ConfigureAwait(false);
                        return;
                }

                await Head(stream, openAi).ConfigureAwait(false);
                string text = reply.Text?.Invoke(request.Prompt) ?? string.Empty;
                int pieces = Math.Max(1, Math.Min(reply.Pieces, Math.Max(1, text.Length)));
                int size = (text.Length + pieces - 1) / Math.Max(1, pieces);
                for (int at = 0; at < text.Length; at += Math.Max(1, size))
                {
                    await Piece(stream, openAi, text.Substring(at, Math.Min(size, text.Length - at)), done: false).ConfigureAwait(false);
                    if (reply.PauseMs > 0)
                        await Task.Delay(reply.PauseMs, stop.Token).ConfigureAwait(false);
                }
                await Piece(stream, openAi, string.Empty, done: true).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
                // The client went away first, what a cancelled prediction does
            }
        }
    }

    // A stalled answer held until the client closes the connection, which a cancelled request does
    private async Task WaitForGoing(NetworkStream stream)
    {
        var buffer = new byte[256];
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            while (await stream.ReadAsync(buffer, limit.Token).ConfigureAwait(false) > 0)
            {
            }
            Interlocked.Increment(ref gone);
        }
        catch (Exception exception) when (exception is IOException or SocketException)
        {
            Interlocked.Increment(ref gone);
        }
    }

    // The request line, the headers and the body; the prompt and the key out of them
    private async Task<(string Path, string Prompt)?> ReadRequest(NetworkStream stream)
    {
        var read = new List<byte>(4096);
        var buffer = new byte[4096];
        int headEnd = -1;
        while (headEnd < 0)
        {
            int count = await stream.ReadAsync(buffer, stop.Token).ConfigureAwait(false);
            if (count == 0)
                return null;
            read.AddRange(buffer.AsSpan(0, count));
            headEnd = IndexOf(read, "\r\n\r\n"u8);
        }

        string head = Encoding.ASCII.GetString(read.GetRange(0, headEnd).ToArray());
        string[] lines = head.Split("\r\n");
        string[] first = lines[0].Split(' ');
        string path = first.Length > 1 ? first[1] : string.Empty;
        int length = 0;
        string authorization = string.Empty;
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon < 0)
                continue;
            string name = line[..colon].Trim(), value = line[(colon + 1)..].Trim();
            if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            else if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
                authorization = value;
        }

        int bodyStart = headEnd + 4;
        while (read.Count - bodyStart < length)
        {
            int count = await stream.ReadAsync(buffer, stop.Token).ConfigureAwait(false);
            if (count == 0)
                return null;
            read.AddRange(buffer.AsSpan(0, count));
        }

        string body = Encoding.UTF8.GetString(read.GetRange(bodyStart, length).ToArray());
        string prompt = string.Empty;
        try
        {
            using JsonDocument json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("prompt", out JsonElement written) && written.ValueKind == JsonValueKind.String)
                prompt = written.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
        }

        LastPath = path;
        LastBody = body;
        LastPrompt = prompt;
        LastAuthorization = authorization;
        return (path, prompt);
    }

    // A streamed answer's head: no length, the connection closed at its end
    private static Task Head(NetworkStream stream, bool openAi) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: {(openAi ? "text/event-stream" : "application/x-ndjson")}\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n")).AsTask();

    // One piece of a streamed answer, Ollama's line of JSON or an OpenAI event, the last saying it's done
    private static async Task Piece(NetworkStream stream, bool openAi, string text, bool done)
    {
        var written = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(written))
        {
            json.WriteStartObject();
            if (openAi)
            {
                json.WriteStartArray("choices");
                json.WriteStartObject();
                json.WriteString("text", text);
                json.WriteNumber("index", 0);
                if (done)
                    json.WriteString("finish_reason", "stop");
                else
                    json.WriteNull("finish_reason");
                json.WriteEndObject();
                json.WriteEndArray();
            }
            else
            {
                json.WriteString("model", "fake");
                json.WriteString("response", text);
                json.WriteBoolean("done", done);
                if (done)
                {
                    json.WriteString("done_reason", "stop");
                    json.WriteNumber("eval_count", 3);
                    json.WriteNumber("prompt_eval_count", 10);
                }
            }
            json.WriteEndObject();
        }

        var line = new List<byte>(written.WrittenCount + 16);
        if (openAi)
            line.AddRange("data: "u8);
        line.AddRange(written.WrittenSpan);
        line.AddRange(openAi ? "\n\n"u8 : "\n"u8);
        if (openAi && done)
            line.AddRange("data: [DONE]\n\n"u8);
        await stream.WriteAsync(line.ToArray()).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);
    }

    // A whole answer with its length, the connection closed after it
    private static Task Write(NetworkStream stream, string status, string type, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        byte[] head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
        byte[] whole = [.. head, .. bytes];
        return stream.WriteAsync(whole).AsTask();
    }

    private static int IndexOf(List<byte> bytes, ReadOnlySpan<byte> wanted)
    {
        for (int i = 0; i + wanted.Length <= bytes.Count; i++)
        {
            int j = 0;
            while (j < wanted.Length && bytes[i + j] == wanted[j])
                j++;
            if (j == wanted.Length)
                return i;
        }
        return -1;
    }
}
