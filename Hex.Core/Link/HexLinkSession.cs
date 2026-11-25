using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

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

    // A chat completion this machine's model couldn't make, or doesn't serve (data.status says how, in a word)
    public const int AI_FAILED = -32008;

    // The authority paused shared editing for a file it holds. Peers can still edit their local file.
    public const int DOCUMENT_HELD = -32007;

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
        new(HexLinkErrors.NOT_ALLOWED, why ?? $"This device may not do that here, it needs \"{HexLinkData.Readable(needs)}\".", new JsonObject { ["needs"] = needs });

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
/// Notifications are dispatched in arrival order by a separate task. One writer prioritizes control between messages.
/// </para>
/// </summary>
internal sealed partial class HexLinkSession : IDisposable
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
    private readonly Channel<Notice> notices = Bounded<Notice>(32);
    private readonly SemaphoreSlim noticeSpace = new(0, 1);
    private readonly SemaphoreSlim handlers = new(MOST_REQUESTS);
    private int requestBytes;
    private int noticeBytes;
    private int started;
    private int receivingBackpressure;

    private readonly record struct Notice(string? Method, JsonObject Message, int Bytes, TaskCompletionSource<JsonNode?>? Answer = null);
    private readonly record struct PendingAsk(TaskCompletionSource<JsonNode?> Answer, bool Priority);

    private static Channel<T> Bounded<T>(int capacity) => Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
    {
        SingleReader = true
    });
    private readonly ConcurrentDictionary<long, PendingAsk> asked = new();
    private readonly CancellationTokenSource closing = new();
    private long lastId;
    private int ended;

    // The requests counted in the second going on, for the rate limit, and when it started
    private long rateSecond;
    private int rateCount;
    private int legacyNoticeCount;
    private int noticeCount;
    private int controlCount;

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

    /// <summary>What takes a notification from the other end, on a dispatch task, in the order they came.</summary>
    public Action<string, JsonObject>? Told { get; set; }

    /// <summary>A dispatch handler that can propagate its own consumer's backpressure through the session.</summary>
    internal Func<string, JsonObject, ValueTask>? ToldAsync { get; set; }

    /// <summary>Called once when the link goes, for whatever reason, with the reason.</summary>
    public event Action<HexLinkSession, string>? Closed;

    /// <summary>How often the other end is pinged, 20 seconds unless a test wants it sooner.</summary>
    public TimeSpan PingEvery { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Whether it's gone.</summary>
    public bool IsClosed => Volatile.Read(ref ended) != 0;

    /// <summary>Why it went, null while it's up. Set before <see cref="IsClosed"/> is, so whoever sees it closed can say why.</summary>
    public string? Why => Volatile.Read(ref closedWhy);

    private string? closedWhy;

    /// <summary>The token cancelled as it goes, for what runs as long as the link does.</summary>
    public CancellationToken Closing => closing.Token;

    internal bool ReceivingBackpressure => Volatile.Read(ref receivingBackpressure) != 0;

    /// <summary>Starts reading and pinging, once the handlers are set.</summary>
    public HexLinkSession Start()
    {
        if (Interlocked.Exchange(ref started, 1) != 0 || IsClosed)
            return this;

        pinger = new Timer(_ => Ping(), null, PingEvery, PingEvery);
        if (IsClosed)
            pinger.Dispose();
        _ = Task.Run(Write);
        _ = Task.Run(Dispatch);
        _ = Task.Run(Read);
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

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel, closing.Token);
        limit.CancelAfter(timeout ?? PATIENCE);
        long id = Interlocked.Increment(ref lastId);
        var answer = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool priority = IsControl(method);
        asked[id] = new PendingAsk(answer, priority);
        try
        {
            JsonObject message = Message(method, parameters);
            message["id"] = id;
            if (!await SendAsync(message, priority, limit.Token))
                throw new HexLinkException(HexLinkErrors.TOO_BIG, $"{method} is too much to send over a link.");
            return await answer.Task.WaitAsync(limit.Token);
        }
        catch (Exception e) when (IsClosed && !cancel.IsCancellationRequested && e is OperationCanceledException or ChannelClosedException)
        {
            throw new HexLinkException(HexLinkErrors.CLOSED, $"The link closed, {Why}");
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new HexLinkException(HexLinkErrors.TIMED_OUT, $"{method} had no answer in {(timeout ?? PATIENCE).TotalSeconds:0} seconds.");
        }
        finally
        {
            asked.TryRemove(id, out _);
        }
    }

    /// <summary>Waits for notification delivery. Use on a synchronous producer that owns the ordering of its stream.</summary>
    public bool Tell(string method, JsonObject? parameters = null) =>
        TellAsync(method, parameters).AsTask().GetAwaiter().GetResult();

    /// <summary>Waits for send capacity and writes a notification without losing it when the writer is busy.</summary>
    public async ValueTask<bool> TellAsync(string method, JsonObject? parameters = null, CancellationToken cancel = default)
    {
        if (IsClosed)
            return false;

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel, closing.Token);
        try
        {
            return await SendAsync(Message(method, parameters), IsControl(method), limit.Token);
        }
        catch (Exception) when (IsClosed && !cancel.IsCancellationRequested)
        {
            return false;
        }
    }

    private static JsonObject Message(string method, JsonObject? parameters)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null)
            message["params"] = parameters;
        return message;
    }

    private static bool IsLegacyStreamNotice(string method) =>
        method is "git/chunk" or "git/done" or "predict/chunk" or "predict/done" or "ai/chunk" or "ai/done";

    /// <summary>The notice a device sends when its person cut the link by hand: the other end doesn't dial it again until it connects.</summary>
    public const string BYE = "link/bye";

    private int byeHeard;

    /// <summary>Whether the other end said, before it closed, that its person cut this link and it is not to be dialled again.</summary>
    public bool PeerSaidBye => Volatile.Read(ref byeHeard) != 0;

    private static bool IsControl(string method) =>
        method is "link/ping" or BYE or "link/rights" or "predict/cancel" or "ai/cancel" or "git/cancel" or "link/bulk/open" or "link/bulk/complete";

    /// <summary>Closes the link, saying why. The asks still waiting fail, <see cref="Closed"/> is called once.</summary>
    public void Close(string why)
    {
        // The first to close says why, and the reason is there before anybody can see the link closed
        if (Interlocked.CompareExchange(ref closedWhy, why, null) is not null)
            return;
        Volatile.Write(ref ended, 1);
        try { pinger?.Dispose(); } catch (Exception) { }
        try { closing.Cancel(); } catch (Exception) { }
        control.Writer.TryComplete();
        replies.Writer.TryComplete();
        outgoing.Writer.TryComplete();
        bulkControl.Writer.TryComplete();
        bulkOutgoing.Writer.TryComplete();
        notices.Writer.TryComplete();
        Connection.Dispose();
        Closed?.Invoke(this, why);
    }

    public void Dispose() => Close("it was closed here");

    private async Task Dispatch()
    {
        try
        {
            await foreach (Notice notice in notices.Reader.ReadAllAsync(closing.Token))
            {
                try
                {
                    if (notice.Answer is { } answer)
                        CompleteAnswer(answer, notice.Message);
                    else if (ToldAsync is { } told)
                        await told(notice.Method!, notice.Message);
                    else
                        Told?.Invoke(notice.Method!, notice.Message);
                }
                catch (Exception e)
                {
                    if (notice.Answer is { } answer)
                        answer.TrySetException(new HexLinkException(HexLinkErrors.INVALID_REQUEST, "The Link reply could not be read."));
                    AuditFault(notice.Method ?? "reply", e);
                }
                finally
                {
                    Interlocked.Add(ref noticeBytes, -notice.Bytes);
                    if (noticeSpace.CurrentCount == 0)
                        noticeSpace.Release();
                }
            }
        }
        catch (OperationCanceledException) when (IsClosed)
        {
        }
        finally
        {
            while (notices.Reader.TryRead(out _))
            {
            }
        }
    }

    private void AuditFault(string operation, Exception error) => Connection.ReportFault(operation, error);

    // A line taken apart, nothing in it ever let throw out of here: it's the other machine's, whatever it says
    private async ValueTask Received(ReadOnlyMemory<byte> line)
    {
        if (line.Span.Trim((byte)' ').Trim((byte)'\r').IsEmpty)
            return;

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(line.Span);
        }
        catch (JsonException)
        {
            await Reply(Error(null, HexLinkErrors.PARSE_ERROR, "That isn't JSON.", null));
            return;
        }

        if (parsed is not JsonObject message)
        {
            await Reply(Error(null, HexLinkErrors.INVALID_REQUEST, "A link speaks a JSON-RPC message a line, one at a time.", null));
            return;
        }

        try
        {
            JsonNode? id = message["id"];
            string? method = HexLinkData.String(message, "method", 200);
            if (method is null)
            {
                if (id is not null)
                    await Answered(id, message, line.Length);
                return;
            }

            JsonObject parameters = message["params"] as JsonObject ?? [];
            if (id is null && method == BYE)
            {
                // Read here, not in the queue: the close that follows it may stop the queue before it is taken
                Volatile.Write(ref byeHeard, 1);
                return;
            }

            if (id is null)
            {
                await ReceiveNotice(method, parameters, line.Length);
                return;
            }

            // Only a number or a short string is an id, it's sent back as it came
            if (id.GetValueKind() is not (JsonValueKind.Number or JsonValueKind.String) || id.ToJsonString().Length > 100)
            {
                await Reply(Error(null, HexLinkErrors.INVALID_REQUEST, "A request's id is a number or a short string.", null));
                return;
            }

            if (method == "link/ping")
            {
                if (Admitted(priority: true))
                    await Reply(McpProtocol.Result(id, new JsonObject()), urgent: true);
                return;
            }

            if (!Admitted())
            {
                await Reply(Error(id, HexLinkErrors.BUSY, $"Too many requests, a link takes {MOST_REQUESTS} a second.", null));
                return;
            }

            if (!handlers.Wait(0))
            {
                await Reply(Error(id, HexLinkErrors.BUSY, "The link is already handling too many requests.", null));
                return;
            }

            int bytes = line.Length;
            if (Interlocked.Add(ref requestBytes, bytes) > 2 * MOST_LINE)
            {
                ReleaseRequest(bytes);
                await Reply(Error(id, HexLinkErrors.BUSY, "The link already has too much request data to process.", null));
                return;
            }

            IncomingBulk? reserved = null;
            try
            {
                if (method == "link/bulk/open")
                    reserved = ReserveIncoming(parameters, bytes);
            }
            catch (HexLinkException e)
            {
                ReleaseRequest(bytes);
                await Reply(Error(id, e.Code, e.Message, e.Detail));
                return;
            }
            catch
            {
                ReleaseRequest(bytes);
                throw;
            }

            _ = Task.Run(async () =>
            {
                try { await Answer(id, method, parameters, reserved); }
                finally { ReleaseRequest(bytes); }
            });
        }
        catch (Exception e) when (!IsClosed)
        {
            AuditFault("receive", e);
        }
    }

    private void ReleaseRequest(int bytes)
    {
        Interlocked.Add(ref requestBytes, -bytes);
        handlers.Release();
    }

    private ValueTask Reply(JsonObject message, bool urgent = false) =>
        QueueJson(urgent ? control : replies, new Outgoing(message), closing.Token);

    private async ValueTask ReceiveNotice(string method, JsonObject parameters, int bytes)
    {
        bool legacy = IsLegacyStreamNotice(method);
        while (!AdmitNotice(legacy))
        {
            int wait = (int)Math.Clamp((rateSecond + 1) * 1000 - Environment.TickCount64, 1, 1000);
            await Task.Delay(wait, closing.Token);
        }

        await DispatchInOrder(new Notice(method, parameters, bytes));
    }

    // Only the reader inserts into this FIFO. Waiting stops socket reads, so correctly framed bursts exert
    // backpressure rather than drop messages or close the link. One parsed frame may be waiting outside the queue.
    private async ValueTask DispatchInOrder(Notice notice)
    {
        int available = 2 * MOST_LINE - notice.Bytes;
        if (Volatile.Read(ref noticeBytes) <= available)
        {
            Interlocked.Add(ref noticeBytes, notice.Bytes);
            if (notices.Writer.TryWrite(notice))
                return;
            Interlocked.Add(ref noticeBytes, -notice.Bytes);
        }

        Volatile.Write(ref receivingBackpressure, 1);
        try
        {
            while (Volatile.Read(ref noticeBytes) > available)
                await noticeSpace.WaitAsync(closing.Token);

            Interlocked.Add(ref noticeBytes, notice.Bytes);
            try
            {
                await notices.Writer.WriteAsync(notice, closing.Token);
            }
            catch
            {
                Interlocked.Add(ref noticeBytes, -notice.Bytes);
                throw;
            }
        }
        finally
        {
            Volatile.Write(ref heard, Environment.TickCount64);
            Volatile.Write(ref receivingBackpressure, 0);
        }
    }

    // Only the reader uses these counters. Requests and notifications have independent quotas.
    private void RefreshAdmission()
    {
        long second = Environment.TickCount64 / 1000;
        if (second == rateSecond)
            return;
        rateSecond = second;
        rateCount = 0;
        noticeCount = 0;
        legacyNoticeCount = 0;
        controlCount = 0;
    }

    private bool Admitted(bool priority = false)
    {
        RefreshAdmission();
        return priority ? ++controlCount <= 32 : ++rateCount <= MOST_REQUESTS;
    }

    private bool AdmitNotice(bool legacy)
    {
        RefreshAdmission();
        return legacy ? ++legacyNoticeCount <= 1024 : ++noticeCount <= MOST_REQUESTS;
    }

    private async Task Answer(JsonNode id, string method, JsonObject parameters, IncomingBulk? reserved)
    {
        JsonObject answer;
        try
        {
            JsonNode? result;
            if (reserved is not null)
                result = await reserved.Open(parameters["metadata"] as JsonObject ?? []);
            else if (method == "link/bulk/complete")
                result = await FinishIncoming(parameters);
            else if (Requested is { } requested)
                result = await requested(method, parameters, closing.Token);
            else
                throw new HexLinkException(HexLinkErrors.METHOD_NOT_FOUND, $"This end answers no {method}.");
            answer = McpProtocol.Result(id, result);
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
            AuditFault(method, e);
            answer = Error(id, HexLinkErrors.INTERNAL_ERROR, e.Message, null);
        }

        try
        {
            if (!await SendAsync(answer, IsControl(method), closing.Token))
                await Reply(Error(id, HexLinkErrors.TOO_BIG, $"The answer to {method} is too big to send over a link.", null));
        }
        catch (Exception) when (IsClosed)
        {
        }
    }

    // An answer to one of ours, the asker let go with it, one to nothing we asked dropped
    private async ValueTask Answered(JsonNode id, JsonObject message, int bytes)
    {
        if (id.GetValueKind() != JsonValueKind.Number || !id.AsValue().TryGetValue(out long number) ||
            !asked.TryGetValue(number, out PendingAsk waiting))
            return;

        // RPCs such as sync/flush promise that earlier notifications have already been handled on return.
        // Control replies can proceed while a notification handler is waiting on disk or other work.
        if (!waiting.Priority && Volatile.Read(ref noticeBytes) != 0)
            await DispatchInOrder(new Notice(null, message, bytes, waiting.Answer));
        else
            CompleteAnswer(waiting.Answer, message);
    }

    private static void CompleteAnswer(TaskCompletionSource<JsonNode?> waiting, JsonObject message)
    {
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
        if (!ReceivingBackpressure && quiet > PingEvery.TotalMilliseconds * PINGS_MISSED)
        {
            Close("the other end stopped answering");
            return;
        }

        _ = Ask("link/ping", null, CancellationToken.None, PingEvery * PINGS_MISSED).ContinueWith(static done => _ = done.Exception, TaskScheduler.Default);
    }
}
