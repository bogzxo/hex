using System.Buffers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// What a link answers with when a request went wrong, JSON-RPC's own codes and the link's. A handler throws a
/// <see cref="HexLinkException"/> with one of these and the asker gets the same one thrown back out of its Ask.
/// </summary>
internal static class HexLinkErrors
{
    // JSON-RPC's own
    public const int PARSE_ERROR = McpProtocol.PARSE_ERROR, INVALID_REQUEST = McpProtocol.INVALID_REQUEST, METHOD_NOT_FOUND = McpProtocol.METHOD_NOT_FOUND,
        INVALID_PARAMS = McpProtocol.INVALID_PARAMS, INTERNAL_ERROR = McpProtocol.INTERNAL_ERROR;

    // The link's: a right the device hasn't got (data.needs says which), a file that moved on since it was read
    // (data.hash says what it is now), something that isn't there, something too big to send, and a link asking faster
    // than it may, which is told to wait rather than cut off
    public const int NOT_ALLOWED = -32001, CONFLICT = -32002, NOT_FOUND = -32003, TOO_BIG = -32004, BUSY = -32005;

    // A prediction this machine's model couldn't make, or doesn't share (data.status says how, in a word)
    public const int PREDICTION_FAILED = -32006;

    // What a link's own end says when the link went before an answer came, never on the wire
    public const int CLOSED = -32099, TIMED_OUT = -32098;
}

/// <summary>
/// A request over a link that went wrong, thrown by a handler to answer with it and thrown out of
/// <see cref="HexLinkSession.Ask"/> on the other side with what was said. <see cref="Detail"/> is the error's data,
/// what right it needed or the hash a file has now.
/// </summary>
internal sealed class HexLinkException(int code, string message, JsonNode? detail = null) : Exception(message)
{
    public int Code { get; } = code;

    public JsonNode? Detail { get; } = detail;

    /// <summary>A right the asker hasn't got, which its data names so the other side can say what's missing.</summary>
    public static HexLinkException NotAllowed(string needs, string? why = null) =>
        new(HexLinkErrors.NOT_ALLOWED, why ?? $"This device may not do that here, it needs {needs}.", new JsonObject { ["needs"] = needs });

    public static HexLinkException Invalid(string why) => new(HexLinkErrors.INVALID_PARAMS, why);

    public static HexLinkException NotFound(string why) => new(HexLinkErrors.NOT_FOUND, why);
}

/// <summary>
/// JSON-RPC 2.0 over one link's stream, a message a line of UTF-8 JSON, in both directions at once: either end asks
/// (<see cref="Ask"/>), tells (<see cref="Tell"/>) and answers. Built the way <see cref="McpSession"/> is, with what a
/// connection to another machine needs on top of what a pipe to this one does: the other end is somebody else's, so a
/// line it sends is read with a limit (one past <see cref="MOST_LINE"/> cuts the link), requests come at most
/// <see cref="MOST_REQUESTS"/> a second (the rest are told busy), and a link that goes quiet is noticed by pinging it.
/// <para>
/// Each request is answered on a task of its own, so a slow one (a file read, a tool that waits) never holds up a ping.
/// Notifications are handed over on the reading task in the order they came, the chunks of a git bundle have to be
/// written in order, so whoever takes them does it quickly. Answers go out under a lock in whatever order they're done.
/// </para>
/// </summary>
internal sealed class HexLinkSession : IDisposable
{
    /// <summary>
    /// The longest line either end may send, in bytes. A file read is up to 8 MB (HexLinkFiles.MOST_READ), which as
    /// base64 is nearly 11, so the line is a little more than that; anything longer is somebody trying it on.
    /// </summary>
    public const int MOST_LINE = 16 * 1024 * 1024;

    /// <summary>How many requests a second the other end may make, past that they're answered busy.</summary>
    public const int MOST_REQUESTS = 60;

    /// <summary>How many pings may go unanswered before the link is taken as dead.</summary>
    public const int PINGS_MISSED = 3;

    // How long an ask waits for its answer unless the asker says otherwise, long enough for a tool that waits
    private static readonly TimeSpan PATIENCE = TimeSpan.FromMinutes(2);

    private readonly Stream stream;
    private readonly Lock writing = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> asked = new();
    private readonly CancellationTokenSource closing = new();
    private long lastId;
    private int ended;

    // What a message is written into, the session's for its life and only used under writing
    private readonly ArrayBufferWriter<byte> sendBuffer = new();
    private readonly Utf8JsonWriter sendWriter = new(Stream.Null, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    // The requests counted in the second going on, for the rate limit, and when it started
    private long rateSecond;
    private int rateCount;

    // When anything was last heard from the other end, Environment.TickCount64
    private long heard = Environment.TickCount64;
    private Timer? pinger;

    public HexLinkSession(HexLinkConnection connection)
    {
        Connection = connection;
        stream = connection.Stream;
    }

    /// <summary>The connection it talks over, who's at the other end and what they may do here.</summary>
    public HexLinkConnection Connection { get; }

    /// <summary>
    /// What answers a request from the other end, by its method, its params (an empty object when it sent none) and a
    /// token cancelled when the link goes. Its result is the answer; a <see cref="HexLinkException"/> it throws is the
    /// error. Called on a task of its own. Null answers every request with method not found.
    /// </summary>
    public Func<string, JsonObject, CancellationToken, Task<JsonNode?>>? Requested { get; set; }

    /// <summary>What takes a notification from the other end, on the reading task, in the order they came.</summary>
    public Action<string, JsonObject>? Told { get; set; }

    /// <summary>Called once when the link goes, for whatever reason, with the reason.</summary>
    public event Action<HexLinkSession, string>? Closed;

    /// <summary>How often the other end is pinged, 20 seconds unless a test wants it sooner.</summary>
    public TimeSpan PingEvery { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Whether it's gone.</summary>
    public bool IsClosed => Volatile.Read(ref ended) != 0;

    /// <summary>Why it went, null while it's up.</summary>
    public string? Why { get; private set; }

    /// <summary>The token cancelled as it goes, for what runs as long as the link does.</summary>
    public CancellationToken Closing => closing.Token;

    /// <summary>Starts reading and pinging, once the handlers are set.</summary>
    public HexLinkSession Start()
    {
        _ = Task.Run(Read);
        pinger = new Timer(_ => Ping(), null, PingEvery, PingEvery);
        return this;
    }

    /// <summary>
    /// Asks the other end something and waits for its answer, the result as it was sent. An error it answered with is
    /// thrown as a <see cref="HexLinkException"/> with its code and data, as is the link going before the answer came
    /// (<see cref="HexLinkErrors.CLOSED"/>) or no answer in time (<see cref="HexLinkErrors.TIMED_OUT"/>).
    /// </summary>
    public async Task<JsonNode?> Ask(string method, JsonObject? parameters = null, CancellationToken cancel = default, TimeSpan? timeout = null)
    {
        if (IsClosed)
            throw new HexLinkException(HexLinkErrors.CLOSED, $"The link is closed, {Why}");

        long id = Interlocked.Increment(ref lastId);
        var answer = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        asked[id] = answer;
        try
        {
            var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters is not null)
                message["params"] = parameters;
            if (!Send(message))
                throw new HexLinkException(HexLinkErrors.TOO_BIG, $"{method} is too much to send over a link.");

            // Gone between the check and the send, the close may have failed the asks before this one was in
            if (IsClosed)
                answer.TrySetException(new HexLinkException(HexLinkErrors.CLOSED, $"The link is closed, {Why}"));

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(timeout ?? PATIENCE);
            try
            {
                return await answer.Task.WaitAsync(limit.Token);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
            {
                throw new HexLinkException(HexLinkErrors.TIMED_OUT, $"{method} had no answer in {(timeout ?? PATIENCE).TotalSeconds:0} seconds.");
            }
        }
        finally
        {
            asked.TryRemove(id, out _);
        }
    }

    /// <summary>Tells the other end something it doesn't answer, false when it couldn't go (the link's gone, or it's too big).</summary>
    public bool Tell(string method, JsonObject? parameters = null)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null)
            message["params"] = parameters;
        return Send(message);
    }

    /// <summary>Closes the link, saying why. The asks still waiting fail, <see cref="Closed"/> is called once.</summary>
    public void Close(string why)
    {
        if (Interlocked.Exchange(ref ended, 1) != 0)
            return;

        Why = why;
        try { pinger?.Dispose(); } catch (Exception) { }
        try { closing.Cancel(); } catch (Exception) { }
        Connection.Dispose();
        foreach (var (_, waiting) in asked)
            waiting.TrySetException(new HexLinkException(HexLinkErrors.CLOSED, $"The link closed, {why}"));
        Closed?.Invoke(this, why);
    }

    public void Dispose() => Close("it was closed here");

    // A message written as a line, false when it can't be. Not indented, so there's never a new line inside a message
    private bool Send(JsonNode message)
    {
        if (IsClosed)
            return false;

        lock (writing)
        {
            try
            {
                sendBuffer.ResetWrittenCount();
                sendWriter.Reset(sendBuffer);
                message.WriteTo(sendWriter);
                sendWriter.Flush();
                // The other end would cut the link for it, so it doesn't go, and whoever sent it hears so
                if (sendBuffer.WrittenCount + 1 > MOST_LINE)
                    return false;
                stream.Write(sendBuffer.WrittenSpan);
                stream.WriteByte((byte)'\n');
                stream.Flush();
                return true;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or NotSupportedException or InvalidOperationException)
            {
                // Closed off the lock, closing disposes the stream another write may be waiting on
            }
        }

        Close("it couldn't be written to");
        return false;
    }

    // Lines read until the other end goes, a line too long or anything the stream throws closing the link
    private async Task Read()
    {
        byte[] chunk = new byte[64 * 1024];
        var line = new ArrayBufferWriter<byte>(4096);
        string why = "the other end hung up";
        try
        {
            while (!IsClosed)
            {
                int read = await stream.ReadAsync(chunk, closing.Token);
                if (read == 0)
                    break;
                Volatile.Write(ref heard, Environment.TickCount64);

                int from = 0;
                while (from < read)
                {
                    int end = Array.IndexOf(chunk, (byte)'\n', from, read - from);
                    int upTo = end < 0 ? read : end;
                    if (line.WrittenCount + (upTo - from) > MOST_LINE)
                    {
                        why = "it sent a line longer than a link takes";
                        return;
                    }

                    line.Write(chunk.AsSpan(from, upTo - from));
                    if (end < 0)
                        break;

                    Received(line.WrittenSpan);
                    // A big line's buffer let go of rather than kept for the link's life
                    if (line.Capacity > 1024 * 1024)
                        line = new ArrayBufferWriter<byte>(4096);
                    else
                        line.ResetWrittenCount();
                    from = end + 1;
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or NotSupportedException or InvalidOperationException)
        {
            why = "the connection broke";
        }
        finally
        {
            Close(why);
        }
    }

    // A line taken apart, nothing in it ever let throw out of here: it's the other machine's, whatever it says
    private void Received(ReadOnlySpan<byte> line)
    {
        if (line.Trim((byte)' ').Trim((byte)'\r').IsEmpty)
            return;

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            Send(Error(null, HexLinkErrors.PARSE_ERROR, "That isn't JSON.", null));
            return;
        }

        if (parsed is not JsonObject message)
        {
            Send(Error(null, HexLinkErrors.INVALID_REQUEST, "A link speaks a JSON-RPC message a line, one at a time.", null));
            return;
        }

        try
        {
            JsonNode? id = message["id"];
            string? method = HexLinkData.String(message, "method", 200);
            if (method is null)
            {
                if (id is not null)
                    Answered(id, message);
                return;
            }

            JsonObject parameters = message["params"] as JsonObject ?? [];
            if (id is null)
            {
                Told?.Invoke(method, parameters);
                return;
            }

            // Only a number or a short string is an id, it's sent back as it came
            if (id.GetValueKind() is not (JsonValueKind.Number or JsonValueKind.String) || id.ToJsonString().Length > 100)
            {
                Send(Error(null, HexLinkErrors.INVALID_REQUEST, "A request's id is a number or a short string.", null));
                return;
            }

            if (!Admitted())
            {
                Send(Error(id, HexLinkErrors.BUSY, $"Too many requests, a link takes {MOST_REQUESTS} a second.", null));
                return;
            }

            if (method == "link/ping")
            {
                Send(McpProtocol.Result(id, new JsonObject()));
                return;
            }

            id = id.DeepClone();
            _ = Task.Run(() => Answer(id, method, parameters));
        }
        catch (Exception)
        {
            // Something in it of a kind nobody expected, the link goes on without it
        }
    }

    // Counted against the second going on, false past the most
    private bool Admitted()
    {
        long second = Environment.TickCount64 / 1000;
        lock (asked)
        {
            if (second != rateSecond)
            {
                rateSecond = second;
                rateCount = 0;
            }

            return ++rateCount <= MOST_REQUESTS;
        }
    }

    private async Task Answer(JsonNode id, string method, JsonObject parameters)
    {
        JsonObject answer;
        try
        {
            if (Requested is not { } requested)
                throw new HexLinkException(HexLinkErrors.METHOD_NOT_FOUND, $"This end answers no {method}.");
            answer = McpProtocol.Result(id, await requested(method, parameters, closing.Token));
        }
        catch (HexLinkException e)
        {
            answer = Error(id, e.Code, e.Message, e.Detail);
        }
        catch (OperationCanceledException) when (IsClosed)
        {
            return;
        }
        catch (Exception e)
        {
            answer = Error(id, HexLinkErrors.INTERNAL_ERROR, e.Message, null);
        }

        // An answer too big for the link said as that instead, so the asker isn't left waiting
        if (!Send(answer) && !IsClosed)
            Send(Error(id, HexLinkErrors.TOO_BIG, $"The answer to {method} is too big to send over a link.", null));
    }

    // An answer to one of ours, the asker let go with it, one to nothing we asked dropped
    private void Answered(JsonNode id, JsonObject message)
    {
        if (id.GetValueKind() != JsonValueKind.Number || !id.AsValue().TryGetValue(out long number) || !asked.TryGetValue(number, out var waiting))
            return;

        if (message["error"] is JsonObject error)
        {
            int code = error["code"] is JsonValue said && said.TryGetValue(out int got) ? got : HexLinkErrors.INTERNAL_ERROR;
            waiting.TrySetException(new HexLinkException(code, HexLinkData.String(error, "message", 2000) ?? "It went wrong over there.", error["data"]?.DeepClone()));
            return;
        }

        // Taken out of the message, so it belongs to whoever has it now
        JsonNode? result = message["result"];
        message.Remove("result");
        waiting.TrySetResult(result);
    }

    private static JsonObject Error(JsonNode? id, int code, string message, JsonNode? data)
    {
        JsonObject error = McpProtocol.Error(id, code, message);
        if (data is not null)
            ((JsonObject)error["error"]!)["data"] = data.DeepClone();
        return error;
    }

    // Every while, a ping, and the link closed when nothing at all has been heard for the pings it may miss
    private void Ping()
    {
        if (IsClosed)
            return;

        long quiet = Environment.TickCount64 - Volatile.Read(ref heard);
        if (quiet > PingEvery.TotalMilliseconds * PINGS_MISSED)
        {
            Close("the other end stopped answering");
            return;
        }

        _ = Ask("link/ping", null, CancellationToken.None, PingEvery * PINGS_MISSED).ContinueWith(static done => _ = done.Exception, TaskScheduler.Default);
    }
}
