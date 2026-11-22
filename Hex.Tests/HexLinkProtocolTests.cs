using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

/// <summary>
/// Hex Link's session and host over a connection in memory: asking and answering both ways at once, errors crossing
/// the link with their codes, the line limit, pings, the request rate, hello, and every method refused without the
/// right it takes and allowed with it, rights changed mid-link taking hold at the next request, the tool allow list,
/// and a peer sending garbage.
/// </summary>
public sealed class HexLinkProtocolTests
{
    [Fact]
    public async Task A_stalled_writer_backpressures_and_cancels_asks_and_picks_ping_next()
    {
        var (end, other) = Pair();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(WAIT_MS));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var methods = new ConcurrentQueue<string>();
        using var a = Session(new ObservedStream(end, async (bytes, cancel) =>
        {
            methods.Enqueue(JsonNode.Parse(bytes.Span)!["method"]!.GetValue<string>());
            entered.TrySetResult();
            await release.Task.WaitAsync(cancel);
        }), true).Start();
        using var b = Session(other, false);
        b.Requested = Echo("b");
        b.Start();

        Task<JsonNode?> first = a.Ask("echo", cancel: timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        using var cancelled = new CancellationTokenSource();
        Task<JsonNode?>[] queued = [.. Enumerable.Range(0, 12).Select(_ => a.Ask("echo", cancel: cancelled.Token))];
        Task<JsonNode?> ping = a.Ask("link/ping", cancel: timeout.Token);
        Assert.All(queued, ask => Assert.False(ask.IsCompleted));
        cancelled.Cancel();
        foreach (Task<JsonNode?> ask in queued)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ask.WaitAsync(timeout.Token));
        Assert.False(first.IsCompleted);
        Assert.False(ping.IsCompleted);

        release.SetResult();
        await Task.WhenAll(first, ping).WaitAsync(timeout.Token);
        Assert.Equal(new[] { "echo", "link/ping" }, methods.ToArray());
    }

    [Fact]
    public async Task Ordered_notifications_run_off_the_reader_and_handler_faults_are_audited()
    {
        var (end, other) = Pair();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(WAIT_MS));
        string auditPath = Path.GetTempFileName();
        try
        {
            var audit = new HexLinkAudit(auditPath);
            using var a = Session(end, true).Start();
            using var b = new HexLinkSession(new HexLinkConnection(other, DeviceA, "memory", false, () => HexLinkRights.View, audit: audit));
            using var release = new ManualResetEventSlim();
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var last = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handled = new List<int>();
            b.Told = (_, notice) =>
            {
                int number = notice["number"]!.GetValue<int>();
                handled.Add(number);
                if (number == 1)
                {
                    first.SetResult();
                    release.Wait(timeout.Token);
                }
                else if (number == 2)
                    throw new InvalidOperationException("notice handler failed");
                else
                    last.SetResult();
            };
            b.Start();

            try
            {
                for (int i = 1; i <= 3; i++)
                    Assert.True(a.Tell("test/notice", new JsonObject { ["number"] = i }));
                await first.Task.WaitAsync(timeout.Token);
                Assert.NotNull(await a.Ask("link/ping", cancel: timeout.Token));
                Assert.False(last.Task.IsCompleted);
            }
            finally
            {
                release.Set();
            }
            await last.Task.WaitAsync(timeout.Token);
            Assert.Equal(new[] { 1, 2, 3 }, handled);
            HexLinkAuditEntry fault = Assert.Single(audit.Latest(10));
            Assert.Equal("session fault", fault.Event);
            Assert.Contains("notice handler failed", fault.Detail);
        }
        finally
        {
            File.Delete(auditPath);
        }
    }

    [Fact]
    public async Task Bulk_frames_round_trip_raw_and_compressed_bytes_in_order()
    {
        var (end, other) = Pair();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(WAIT_MS));
        var kinds = new ConcurrentQueue<HexLinkFrameKind>();
        using var a = Session(new ObservedStream(end, (bytes, _) =>
        {
            if (bytes.Span[0] == HexLinkFrameHeader.Marker)
                kinds.Enqueue((HexLinkFrameKind)bytes.Span[1]);
            return ValueTask.CompletedTask;
        }), true, HexLinkBulkSupport.Zstandard).Start();
        using var b = Session(other, false, HexLinkBulkSupport.Zstandard);
        var sink = new MemorySink();
        b.RegisterBulkReceiver("test/upload", HexLinkRights.Edit, (length, metadata, _) =>
        {
            Assert.Equal(3 * HexLinkFrameHeader.MaximumPayload, length);
            Assert.Equal("ordered", metadata["name"]!.GetValue<string>());
            return ValueTask.FromResult<IHexLinkBulkSink>(sink);
        });
        b.Start();

        int block = HexLinkFrameHeader.MaximumPayload;
        byte[] bytes = new byte[3 * block];
        bytes.AsSpan(0, block).Fill((byte)'a');
        new Random(0).NextBytes(bytes.AsSpan(block, block));
        bytes.AsSpan(2 * block, block).Fill((byte)'z');
        await using HexLinkSession.BulkUpload upload = await a.OpenBulkAsync("test/upload", bytes.Length,
            new JsonObject { ["name"] = "ordered" }, timeout.Token);
        await upload.WriteAsync(bytes, timeout.Token);
        await upload.CompleteAsync(timeout.Token);

        Assert.True(sink.Committed);
        Assert.Equal(bytes, sink.Data.ToArray());
        Assert.Equal(new[] { HexLinkFrameKind.Compressed, HexLinkFrameKind.Data, HexLinkFrameKind.Compressed }, kinds.ToArray());
    }

    [Fact]
    public async Task Bulk_credit_tracks_sink_consumption_and_cancelling_releases_the_sink()
    {
        var (end, other) = Pair();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(WAIT_MS));
        using var a = Session(end, true, HexLinkBulkSupport.Raw).Start();
        using var b = Session(other, false, HexLinkBulkSupport.Raw);
        var sink = new StalledSink();
        b.RegisterBulkReceiver("test/stalled", HexLinkRights.Edit, (_, _, _) => ValueTask.FromResult<IHexLinkBulkSink>(sink));
        b.Start();

        byte[] data = new byte[(HexLinkFrameHeader.Window + 1) * HexLinkFrameHeader.MaximumPayload];
        using var stopWrite = new CancellationTokenSource();
        await using (HexLinkSession.BulkUpload upload = await a.OpenBulkAsync("test/stalled", data.Length, cancel: timeout.Token))
        {
            Task writing = upload.WriteAsync(data, stopWrite.Token).AsTask();
            await sink.Entered.Task.WaitAsync(timeout.Token);
            Assert.NotNull(await a.Ask("link/ping", cancel: timeout.Token));
            Assert.False(writing.IsCompleted);
            stopWrite.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writing.WaitAsync(timeout.Token));
        }
        await sink.Disposed.Task.WaitAsync(timeout.Token);
        Assert.False(a.IsClosed);
        Assert.False(b.IsClosed);
    }

    [Fact]
    public async Task Bulk_upload_slots_wait_and_cancel_without_skipping_the_upload()
    {
        var (end, other) = Pair();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(WAIT_MS));
        using var a = Session(end, true, HexLinkBulkSupport.Raw).Start();
        using var b = Session(other, false, HexLinkBulkSupport.Raw);
        b.RegisterBulkReceiver("test/window", HexLinkRights.Edit, (_, _, _) =>
            ValueTask.FromResult<IHexLinkBulkSink>(new MemorySink()));
        b.Start();

        var occupied = new List<HexLinkSession.BulkUpload>();
        try
        {
            for (int i = 0; i < 4; i++)
                occupied.Add(await a.OpenBulkAsync("test/window", 0, cancel: timeout.Token));

            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            Task<HexLinkSession.BulkUpload> waiting = a.OpenBulkAsync("test/window", 0, cancel: cancel.Token);
            Assert.False(waiting.IsCompleted);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(timeout.Token));

            Task<HexLinkSession.BulkUpload> next = a.OpenBulkAsync("test/window", 0, cancel: timeout.Token);
            Assert.False(next.IsCompleted);
            Assert.NotNull(await a.Ask("link/ping", cancel: timeout.Token));
            await occupied[0].CompleteAsync(timeout.Token);
            await occupied[0].DisposeAsync();
            occupied.RemoveAt(0);
            await using HexLinkSession.BulkUpload admitted = await next.WaitAsync(timeout.Token);
            await admitted.CompleteAsync(timeout.Token);
        }
        finally
        {
            foreach (HexLinkSession.BulkUpload upload in occupied)
                await upload.DisposeAsync();
        }
    }

    [Fact]
    public async Task Bulk_open_requires_the_registered_right_and_frames_recheck_revocation()
    {
        var (end, other) = Pair();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(WAIT_MS));
        int rights = (int)HexLinkRights.View;
        using var a = Session(end, true, HexLinkBulkSupport.Raw).Start();
        using var b = Session(other, false, HexLinkBulkSupport.Raw, () => (HexLinkRights)Volatile.Read(ref rights));
        var sink = new MemorySink();
        b.RegisterBulkReceiver("test/rights", HexLinkRights.Edit, (_, _, _) => ValueTask.FromResult<IHexLinkBulkSink>(sink));
        b.Start();

        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(a.OpenBulkAsync("test/rights", 1, cancel: timeout.Token)));
        Volatile.Write(ref rights, (int)(HexLinkRights.View | HexLinkRights.Edit));
        await using HexLinkSession.BulkUpload upload = await a.OpenBulkAsync("test/rights", 1, cancel: timeout.Token);
        Volatile.Write(ref rights, (int)HexLinkRights.View);
        await upload.WriteAsync(new byte[] { 42 }, timeout.Token);
        Assert.True(Eventually(() => b.IsClosed));
        Assert.Contains("stream rights", b.Why);
        Assert.False(sink.Committed);
        Assert.Empty(sink.Data.ToArray());
    }

    [Theory]
    [InlineData((int)HexLinkFrameKind.Data, 1, 1, "unopened stream")]
    [InlineData((int)HexLinkFrameKind.Data, HexLinkFrameHeader.MaximumPayload + 1, HexLinkFrameHeader.MaximumPayload + 1, "frame header")]
    [InlineData((int)HexLinkFrameKind.Compressed, 1, 100, "frame header")]
    public void Bulk_rejects_invalid_lengths_unnegotiated_compression_and_unopened_streams(
        int kind, int wireLength, int length, string reason)
    {
        var (end, other) = Pair();
        using (end)
        using (var receiver = Session(other, false, HexLinkBulkSupport.Raw).Start())
        {
            var header = new HexLinkFrameHeader((HexLinkFrameKind)kind, 1, 0, wireLength, length);
            byte[] packet = new byte[HexLinkFrameHeader.Size];
            header.Write(packet);
            end.Write(packet.AsSpan(0, 7));
            end.Write(packet.AsSpan(7));
            Assert.True(Eventually(() => receiver.IsClosed));
            Assert.Contains(reason, receiver.Why);
        }
    }

    [Fact]
    public async Task Bulk_commit_refuses_an_incorrect_hash()
    {
        var (end, other) = Pair();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(WAIT_MS));
        using var a = Session(end, true, HexLinkBulkSupport.Raw).Start();
        using var b = Session(other, false, HexLinkBulkSupport.Raw);
        var sink = new MemorySink();
        b.RegisterBulkReceiver("test/hash", HexLinkRights.Edit, (_, _, _) => ValueTask.FromResult<IHexLinkBulkSink>(sink));
        b.Start();

        await using HexLinkSession.BulkUpload upload = await a.OpenBulkAsync("test/hash", 0, cancel: timeout.Token);
        HexLinkException mismatch = await Assert.ThrowsAsync<HexLinkException>(() => a.Ask("link/bulk/complete",
            new JsonObject { ["stream"] = 1, ["sha256"] = new string('0', 64) }, timeout.Token));
        Assert.Equal(HexLinkErrors.INVALID_PARAMS, mismatch.Code);
        Assert.Contains("hash did not match", mismatch.Message);
        Assert.False(sink.Committed);
        Assert.NotNull(await a.Ask("link/ping", cancel: timeout.Token));
    }

    private static HexLinkSession Session(Stream stream, bool dialled, HexLinkBulkSupport bulk = HexLinkBulkSupport.None,
        Func<HexLinkRights>? rights = null) =>
        new(new HexLinkConnection(stream, dialled ? DeviceB : DeviceA, "memory", dialled,
            rights ?? (() => HexLinkRights.View | HexLinkRights.Edit), bulk: bulk));

    private sealed class MemorySink : IHexLinkBulkSink
    {
        public MemoryStream Data { get; } = new();
        public bool Committed { get; private set; }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancel) => Data.WriteAsync(data, cancel);

        public ValueTask CommitAsync(CancellationToken cancel)
        {
            Committed = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => Data.DisposeAsync();
    }

    private sealed class StalledSink : IHexLinkBulkSink
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancel)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancel);
        }

        public ValueTask CommitAsync(CancellationToken cancel) => throw new InvalidOperationException();

        public ValueTask DisposeAsync()
        {
            Disposed.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class ObservedStream(Stream inner, Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> beforeWrite) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancel) => inner.FlushAsync(cancel);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default) =>
            inner.ReadAsync(buffer, cancel);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancel = default)
        {
            await beforeWrite(buffer, cancel);
            await inner.WriteAsync(buffer, cancel);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private static Func<string, JsonObject, CancellationToken, Task<JsonNode?>> Echo(string who) =>
        (method, parameters, _) => method switch
        {
            "echo" => Task.FromResult<JsonNode?>(new JsonObject { ["who"] = who, ["said"] = parameters["text"]?.DeepClone() }),
            "slow" => Task.Delay(400).ContinueWith(_ => (JsonNode?)new JsonObject { ["slow"] = true }),
            "conflict" => throw new HexLinkException(HexLinkErrors.CONFLICT, "it moved", new JsonObject { ["hash"] = "abc" }),
            "boom" => throw new InvalidOperationException("bang"),
            _ => throw new HexLinkException(HexLinkErrors.METHOD_NOT_FOUND, "no")
        };

    [Fact]
    public async Task Both_ends_ask_and_answer_at_once()
    {
        var (a, b) = Sessions();
        a.Requested = Echo("a");
        b.Requested = Echo("b");
        a.Start();
        b.Start();

        Task<JsonNode?>[] asked = [.. Enumerable.Range(0, 20).SelectMany(i => new[]
        {
            a.Ask("echo", new JsonObject { ["text"] = $"from a {i}" }),
            b.Ask("echo", new JsonObject { ["text"] = $"from b {i}" })
        })];
        JsonNode?[] answers = await Task.WhenAll(asked).WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS));
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal("b", answers[i * 2]!["who"]!.GetValue<string>());
            Assert.Equal($"from a {i}", answers[i * 2]!["said"]!.GetValue<string>());
            Assert.Equal("a", answers[i * 2 + 1]!["who"]!.GetValue<string>());
        }

        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public async Task A_slow_request_never_holds_up_a_quick_one()
    {
        var (a, b) = Sessions();
        b.Requested = Echo("b");
        a.Start();
        b.Start();
        Task<JsonNode?> slow = a.Ask("slow");
        JsonNode? quick = await a.Ask("echo", new JsonObject { ["text"] = "hi" }).WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS));
        Assert.False(slow.IsCompleted);
        Assert.Equal("hi", quick!["said"]!.GetValue<string>());
        Assert.True((await slow)!["slow"]!.GetValue<bool>());
        a.Dispose();
    }

    [Fact]
    public async Task Errors_cross_the_link_with_their_code_and_data()
    {
        var (a, b) = Sessions();
        b.Requested = Echo("b");
        a.Start();
        b.Start();

        var conflict = await Assert.ThrowsAsync<HexLinkException>(() => a.Ask("conflict"));
        Assert.Equal(HexLinkErrors.CONFLICT, conflict.Code);
        Assert.Equal("abc", conflict.Detail!["hash"]!.GetValue<string>());
        Assert.Equal("it moved", conflict.Message);

        Assert.Equal(HexLinkErrors.INTERNAL_ERROR, await CodeOf(a.Ask("boom")));
        Assert.Equal(HexLinkErrors.METHOD_NOT_FOUND, await CodeOf(a.Ask("what")));
        a.Dispose();
    }

    [Fact]
    public async Task A_line_too_long_closes_the_link_and_fails_what_was_waiting()
    {
        var (a, b) = Sessions();
        a.Requested = (_, _, _) => new TaskCompletionSource<JsonNode?>().Task;
        string? why = null;
        b.Closed += (_, said) => why = said;
        a.Start();
        b.Start();
        Task<JsonNode?> waiting = b.Ask("never answered");

        // Past the limit with no new line, in pieces as a network would bring it
        var end = (End)a.Connection.Stream;
        byte[] piece = new byte[1024 * 1024];
        Array.Fill(piece, (byte)'x');
        for (int i = 0; i <= HexLinkSession.MOST_LINE / piece.Length; i++)
            end.Write(piece);

        // Closed says why from the moment it's closed; the Closed event comes a moment after, on the reading task
        Assert.True(Eventually(() => b.IsClosed));
        Assert.Contains("longer", b.Why);
        Assert.True(Eventually(() => Volatile.Read(ref why) is not null));
        Assert.Equal(b.Why, why);
        Assert.Equal(HexLinkErrors.CLOSED, await CodeOf(waiting));
        Assert.Equal(HexLinkErrors.CLOSED, await CodeOf(b.Ask("after")));
        Assert.True(Eventually(() => a.IsClosed));
    }

    [Fact]
    public async Task What_would_be_too_long_isnt_sent_and_the_asker_hears_so()
    {
        var (a, b) = Sessions();
        b.Requested = (_, _, _) => Task.FromResult<JsonNode?>(new JsonObject { ["big"] = new string('y', HexLinkSession.MOST_LINE) });
        a.Start();
        b.Start();
        Assert.Equal(HexLinkErrors.TOO_BIG, await CodeOf(a.Ask("big")));
        Assert.Equal(HexLinkErrors.TOO_BIG, await CodeOf(a.Ask("echo", new JsonObject { ["text"] = new string('z', HexLinkSession.MOST_LINE) })));
        Assert.False(a.IsClosed);
        Assert.False(b.IsClosed);
        a.Dispose();
    }

    [Fact]
    public void Pings_keep_a_link_up_and_a_dead_end_is_noticed()
    {
        var (a, b) = HexLinkTestKit.Pair();
        // Pings every 400 ms, so the link goes after 1.2 s of quiet: short enough for a test, long enough that a loaded
        // machine running other suites beside this one doesn't starve a ping's answer past it
        TimeSpan every = TimeSpan.FromMilliseconds(400);
        var live = new HexLinkSession(new HexLinkConnection(a, DeviceB, "memory", true, () => HexLinkRights.View)) { PingEvery = every };
        var other = new HexLinkSession(new HexLinkConnection(b, DeviceA, "memory", false, () => HexLinkRights.View)) { PingEvery = every };
        live.Start();
        other.Start();

        // An end that's there but says nothing at all (a frozen machine): its stream is open, nothing comes back. Started
        // beside the live pair, so both are watched over the same stretch of time
        var (c, d) = HexLinkTestKit.Pair();
        var waiting = new HexLinkSession(new HexLinkConnection(c, DeviceB, "memory", true, () => HexLinkRights.View)) { PingEvery = every };
        long closedAt = 0;
        waiting.Closed += (_, _) => Volatile.Write(ref closedAt, Environment.TickCount64);
        long started = Environment.TickCount64;
        waiting.Start();

        // Well past three pings both live ends are still up, each answering the other's (with nothing else said, only
        // the pings keep them from the 1.2 s of quiet that closes a link)
        Thread.Sleep((int)(every.TotalMilliseconds * (HexLinkSession.PINGS_MISSED + 2)));
        Assert.False(live.IsClosed, live.Why);
        Assert.False(other.IsClosed, other.Why);

        // The silent one went for that, and not before three pings' worth of quiet
        Assert.True(Eventually(() => waiting.IsClosed, 10_000));
        Assert.Contains("stopped answering", waiting.Why);
        Assert.True(Eventually(() => Volatile.Read(ref closedAt) != 0));
        Assert.True(closedAt - started >= every.TotalMilliseconds * HexLinkSession.PINGS_MISSED - 16, $"closed after {closedAt - started} ms");
        d.Dispose();
        live.Dispose();
        other.Dispose();
    }

    [Fact]
    public async Task Past_the_rate_requests_are_told_busy_and_the_link_stays()
    {
        var (a, b) = Pair();
        using (a)
        using (var receiver = Session(b, false))
        using (var reader = new StreamReader(a, Encoding.UTF8, leaveOpen: true))
        using (var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(WAIT_MS)))
        {
            receiver.Requested = Echo("b");
            receiver.Start();

            // Deliver one wire burst. A cooperative session's bounded send queue can spread concurrent asks
            // across several admission windows on a busy host, and would not exercise receiver overload.
            const int count = 300;
            string burst = string.Concat(Enumerable.Range(1, count).Select(id => Request(id, "burst")));
            a.Write(Encoding.UTF8.GetBytes(burst));

            int admitted = 0, busy = 0;
            for (int i = 0; i < count; i++)
            {
                JsonNode reply = JsonNode.Parse((await reader.ReadLineAsync(timeout.Token))!)!;
                if (reply["error"] is JsonObject error)
                {
                    Assert.Equal(HexLinkErrors.BUSY, error["code"]!.GetValue<int>());
                    busy++;
                }
                else
                {
                    Assert.Equal("burst", reply["result"]!["said"]!.GetValue<string>());
                    admitted++;
                }
            }
            Assert.InRange(admitted, 1, HexLinkSession.MOST_REQUESTS * 2);
            Assert.Equal(count - admitted, busy);
            Assert.False(receiver.IsClosed);

            await Task.Delay(1100, timeout.Token);
            a.Write(Encoding.UTF8.GetBytes(Request(count + 1, "after")));
            JsonNode after = JsonNode.Parse((await reader.ReadLineAsync(timeout.Token))!)!;
            Assert.Equal("after", after["result"]!["said"]!.GetValue<string>());
        }

        static string Request(int id, string text) =>
            $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"echo\",\"params\":{{\"text\":\"{text}\"}}}}\n";
    }

    [Fact]
    public async Task Garbage_from_a_peer_never_throws_out_of_the_reader()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit, HexLinkRights.View);
        var end = (End)pair.PeerB.Session.Connection.Stream;

        string[] garbage =
        [
            "not json at all",
            "[1,2,3]",
            "{\"jsonrpc\":\"2.0\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":{\"a\":1},\"method\":\"fleet/get\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":999999,\"result\":{}}",
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":\"nope\"}",
            "{\"jsonrpc\":\"2.0\",\"method\":42}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"fleet/changed\",\"params\":{\"rows\":[{\"id\":\"x\"},{\"id\":1,\"name\":5,\"depth\":1e300,\"cost\":\"lots\"},7,null]}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"fleet/changed\",\"params\":{\"rows\":" + "[" + string.Join(',', Enumerable.Repeat("{}", 5000)) + "]}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"chat/changed\",\"params\":{\"id\":1,\"seq\":\"x\",\"rows\":{\"a\":1}}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"link/rights\",\"params\":{\"granted\":[\"view\",7,\"root\",null]}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"git/chunk\",\"params\":{\"id\":\"nobody\",\"at\":0,\"data\":\"!!\"}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"presence/set\",\"params\":{\"file\":12,\"line\":\"a\",\"selection\":[1]}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"sync/changed\",\"params\":{\"folder\":[],\"changes\":5}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"fs/changed\",\"params\":{\"path\":{},\"hash\":1}}",
            "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":[\"x\"],\"arguments\":7}}",
            "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"fs/read\",\"params\":{\"path\":null}}",
            "﻿{\"jsonrpc\":\"2.0\",\"method\":\"x\"}",
            new string('[', 5000) + new string(']', 5000)
        ];
        foreach (string line in garbage)
            end.Inject(Encoding.UTF8.GetBytes(line + "\n"));

        // Still up, and still answering
        JsonNode? still = await pair.PeerA.Session.Ask("link/ping").WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS));
        Assert.NotNull(still);
        Assert.False(pair.PeerA.IsClosed);
        Assert.False(pair.PeerB.IsClosed);
        Assert.True(pair.PeerB.Fleet.Count <= 2000);
    }

    [Fact]
    public async Task Hello_carries_the_device_what_it_grants_and_the_workspace()
    {
        using var pair = new LinkedPair(HexLinkRights.View | HexLinkRights.Steer, HexLinkRights.None);
        pair.A.Workspace = pair.A.Workspace with { Repositories = [new HexLinkRepository("hex", "development", new string('1', 40), pair.A.Work)] };
        var (a, b) = HexLinkTestKit.Pair();
        Task<HexLinkPeer> peerB = pair.LinkA.Add(new HexLinkConnection(a, DeviceB, "memory", true, () => pair.BOnA));
        HexLinkPeer peerA = await pair.LinkB.Add(new HexLinkConnection(b, DeviceA, "memory", false, () => pair.AOnB));
        HexLinkPeer fromA = await peerB;

        // B sees A as A said it, what A lets it do, A's folders by name with no path, and its repository
        Assert.Equal(DeviceA.Id, peerA.Device.Id);
        Assert.Equal("Desk", peerA.Device.Name);
        Assert.Equal("windows", peerA.Device.Os);
        Assert.Equal(HexLinkRights.View | HexLinkRights.Steer, peerA.Granted);
        Assert.Equal("Test", peerA.Workspace.Name);
        Assert.Equal("work", Assert.Single(peerA.Workspace.Folders).Name);
        Assert.Equal(string.Empty, peerA.Workspace.Folders[0].Path);
        HexLinkRepository repository = Assert.Single(peerA.Workspace.Repositories);
        Assert.Equal(("hex", "development", new string('1', 40)), (repository.Name, repository.Branch, repository.Head));

        // A was granted nothing on B, so it hears nothing of B's workspace
        Assert.Equal(HexLinkRights.None, fromA.Granted);
        Assert.Empty(fromA.Workspace.Folders);
        Assert.Single(pair.LinkA.Peers);
    }

    [Fact]
    public async Task A_device_claiming_another_id_is_still_the_one_its_connection_proved()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        Assert.Equal(DeviceB.Id, pair.PeerB.Device.Id);
        Assert.Equal(DeviceA.Id, pair.PeerA.Device.Id);
        Assert.Same(pair.PeerB, pair.LinkA.PeerOf(DeviceB.Id));
    }

    // Every method a device can ask, what it takes and what to ask it with
    private static IEnumerable<(string Method, HexLinkRights Needs, JsonObject Params)> Methods() =>
    [
        ("fleet/get", HexLinkRights.View, []),
        ("chat/get", HexLinkRights.View, new JsonObject { ["id"] = 1 }),
        ("chat/watch", HexLinkRights.View, new JsonObject { ["id"] = 1 }),
        ("chat/answer", HexLinkRights.Steer, new JsonObject { ["id"] = 1, ["answer"] = "yes" }),
        ("tools/list", HexLinkRights.View, []),
        ("tools/call", HexLinkRights.View, new JsonObject { ["name"] = "hex_read", ["arguments"] = new JsonObject { ["path"] = "a" } }),
        ("tools/call", HexLinkRights.Steer, new JsonObject { ["name"] = "hex_agent_send", ["arguments"] = new JsonObject { ["id"] = 1, ["text"] = "go" } }),
        ("tools/call", HexLinkRights.Edit, new JsonObject { ["name"] = "hex_write", ["arguments"] = new JsonObject { ["path"] = "a" } }),
        ("fs/list", HexLinkRights.View, new JsonObject { ["path"] = "work" }),
        ("fs/read", HexLinkRights.View, new JsonObject { ["path"] = "work/a.txt" }),
        ("fs/write", HexLinkRights.Edit, new JsonObject { ["path"] = "work/new.txt", ["base"] = null, ["text"] = "new" }),
        ("fs/delete", HexLinkRights.Edit, new JsonObject { ["path"] = "work/gone.txt", ["base"] = "00" }),
        ("fs/watch", HexLinkRights.View, new JsonObject { ["path"] = "work/a.txt" }),
        ("sync/manifest", HexLinkRights.View, new JsonObject { ["folder"] = "work" }),
        ("sync/watch", HexLinkRights.View, new JsonObject { ["folder"] = "work" }),
        ("sync/flush", HexLinkRights.View, new JsonObject { ["folder"] = "work" }),
        ("git/bundle", HexLinkRights.View, new JsonObject { ["repository"] = "none", ["id"] = "x1" }),
        ("patch/offer", HexLinkRights.View, new JsonObject { ["title"] = "t", ["diff"] = "d" })
    ];

    [Fact]
    public async Task Every_method_is_refused_without_its_right_and_allowed_with_it()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.None);
        File.WriteAllText(Path.Combine(pair.A.Work, "a.txt"), "hello");
        pair.A.OnEditor(() => pair.A.Hub.Add(new HexAgentChat(1, "claude_code")));

        foreach (var (method, needs, parameters) in Methods())
        {
            // Nothing granted, and everything but the right it takes
            foreach (HexLinkRights rights in new[] { HexLinkRights.None, (HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit) & ~needs })
            {
                pair.BOnA = rights;
                var refused = await Assert.ThrowsAsync<HexLinkException>(() => pair.PeerA.Session.Ask(method, (JsonObject)parameters.DeepClone()));
                Assert.True(refused.Code == HexLinkErrors.NOT_ALLOWED, $"{method} with {rights} said {refused.Code} {refused.Message}");
                Assert.Equal(HexLinkData.Word(needs), refused.Detail!["needs"]!.GetValue<string>());
            }

            // The right it takes and View (Steer and Edit take View too): not refused, whatever else it says
            pair.BOnA = needs | HexLinkRights.View;
            int? code = await CodeOf(pair.PeerA.Session.Ask(method, (JsonObject)parameters.DeepClone()));
            Assert.True(code != HexLinkErrors.NOT_ALLOWED, $"{method} with {pair.BOnA} was refused");
        }

        // Steer or Edit without View is a mistake in somebody's ticks, refused
        pair.BOnA = HexLinkRights.Steer | HexLinkRights.Edit;
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(pair.PeerA.Answer(1, "yes")));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(pair.PeerA.Write("work/x.txt", null, [1])));
        Assert.Contains(pair.A.Audited, line => line.What == "refused" && line.Who == DeviceB);
    }

    [Fact]
    public async Task Rights_changed_mid_link_hold_from_the_next_request_and_the_device_is_told()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        Assert.NotNull(await pair.PeerA.WatchFleet());

        pair.BOnA = HexLinkRights.None;
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(pair.PeerA.WatchFleet()));
        pair.LinkA.RightsMoved(DeviceB.Id);
        Assert.True(Eventually(() => pair.PeerA.Granted == HexLinkRights.None));

        pair.BOnA = HexLinkRights.View | HexLinkRights.Steer;
        pair.LinkA.RightsMoved(DeviceB.Id);
        Assert.True(Eventually(() => pair.PeerA.Granted == (HexLinkRights.View | HexLinkRights.Steer)));
        Assert.NotNull(await pair.PeerA.WatchFleet());
        Assert.False((await pair.PeerA.Send(1, "go")).IsError);
    }

    [Fact]
    public async Task A_tool_off_the_allow_list_is_refused_whatever_was_granted()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit);
        foreach (string tool in new[] { "hex_terminal_run", "hex_screenshot", "hex_settings_set", "hex_dev_restart", "hex_nothing" })
        {
            var refused = await Assert.ThrowsAsync<HexLinkException>(() => pair.PeerA.Call(tool, new JsonObject { ["command"] = "rm -rf /" }));
            Assert.Equal(HexLinkErrors.NOT_ALLOWED, refused.Code);
        }

        Assert.DoesNotContain(pair.A.LinkTools.Called, call => call.Name is "hex_terminal_run" or "hex_screenshot");

        // tools/list lists only what may be called, nothing off the list
        IReadOnlyList<McpTool> listed = await pair.PeerA.ListTools();
        Assert.Contains(listed, tool => tool.Name == "hex_edit");
        Assert.DoesNotContain(listed, tool => tool.Name is "hex_terminal_run" or "hex_screenshot");
        pair.BOnA = HexLinkRights.View;
        listed = await pair.PeerA.ListTools();
        Assert.Contains(listed, tool => tool.Name == "hex_agent_wait");
        Assert.DoesNotContain(listed, tool => tool.Name is "hex_edit" or "hex_agent_send");
    }

    [Fact]
    public async Task The_calling_chat_a_peer_says_is_stripped_and_arguments_are_checked()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View | HexLinkRights.Steer);
        McpToolResult sent = await pair.PeerA.Call("hex_agent_send", new JsonObject { ["id"] = 3, ["text"] = "go", [HexLinkAllowed.CALLER] = 7 });
        Assert.False(sent.IsError);
        var (name, arguments) = Assert.Single(pair.A.LinkTools.Called);
        Assert.Equal("hex_agent_send", name);
        Assert.Null(arguments[HexLinkAllowed.CALLER]);
        Assert.Equal(3, arguments["id"]!.GetValue<int>());
        Assert.Contains(pair.A.Audited, line => line.What == "steer" && line.Detail.Contains("hex_agent_send", StringComparison.Ordinal));

        // A required argument missing, the tool fails the way MCP's does and never runs
        McpToolResult wrong = await pair.PeerA.Call("hex_read", new JsonObject { ["nonsense"] = 1 });
        Assert.True(wrong.IsError);
        Assert.Contains("can't take those arguments", wrong.AllText());
        Assert.Single(pair.A.LinkTools.Called);
    }

    [Fact]
    public async Task The_steering_helpers_call_the_tools_they_stand_for()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View | HexLinkRights.Steer);
        await pair.PeerA.Send(2, "carry on");
        await pair.PeerA.Stop(2);
        await pair.PeerA.Restart(2, "again");
        await pair.PeerA.Approve(2, 1, true);
        Assert.True(await pair.PeerA.Answer(2, "the second"));
        Assert.Equal(["hex_agent_send", "hex_agent_stop", "hex_agent_restart", "hex_agent_approve"], pair.A.LinkTools.Called.Select(call => call.Name));
        Assert.Equal((2, "the second"), Assert.Single(pair.A.Answers));
        Assert.True(pair.A.LinkTools.Called.Last().Arguments["allow"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_patch_offered_reaches_the_person_and_their_answer_comes_back()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        pair.A.PatchAnswer = "applied";
        Assert.Equal("applied", await pair.PeerA.OfferPatch("Fix the build", "diff --git a/x b/x\n"));
        Assert.Equal(("Fix the build", "diff --git a/x b/x\n"), Assert.Single(pair.A.Offered));
        pair.A.PatchAnswer = "whatever";
        Assert.Equal("discarded", await pair.PeerA.OfferPatch("Again", "d"));
    }

    [Fact]
    public async Task Presence_goes_to_the_peer_and_on_to_a_third_device_that_may_see_but_not_to_one_that_may_not()
    {
        var hub = new LinkServices(DeviceA);
        var link = new HexLink(hub);
        var rights = new Dictionary<string, HexLinkRights> { [DeviceB.Id] = HexLinkRights.View, [DeviceC.Id] = HexLinkRights.View };
        var others = new List<(LinkServices Services, HexLink Link)>();
        HexLinkPeer? hubFromB = null;
        foreach (HexLinkDevice device in new[] { DeviceB, DeviceC })
        {
            var services = new LinkServices(device);
            var theirs = new HexLink(services);
            var (a, b) = HexLinkTestKit.Pair();
            Task<HexLinkPeer> mine = link.Add(new HexLinkConnection(a, device, "memory", false, () => rights[device.Id]));
            HexLinkPeer peer = await theirs.Add(new HexLinkConnection(b, DeviceA, "memory", true, () => HexLinkRights.View));
            await mine;
            others.Add((services, theirs));
            if (device == DeviceB)
                hubFromB = peer;
        }

        // B cannot replace the presence of C, who has a direct authenticated connection to A.
        var relayed = HexLinkHost.PresenceJson(new HexLinkPresence("work/forged.cs", 0, 0));
        relayed["device"] = HexLinkData.DeviceJson(DeviceC);
        hubFromB!.Session.Tell("presence/set", relayed);

        // B says where it is: A shows it and passes it on to C
        hubFromB!.SetPresence(new HexLinkPresence("work/a.cs", 4, 2, 4, 9, 8, 13));
        Assert.True(Eventually(() => hub.Presences.Any(each => each.Who == DeviceB && each.Where?.File == "work/a.cs")));
        Assert.True(Eventually(() => others[1].Services.Presences.Any(each => each.Who.Id == DeviceB.Id && each.Where is { File: "work/a.cs", Line: 4, Column: 2, ToColumn: 9, PointerLine: 8, PointerColumn: 13 })));
        Assert.Equal("work/a.cs", link.PeerOf(DeviceB.Id)!.Presence?.File);
        Assert.DoesNotContain(hub.Presences, each => each.Where?.File == "work/forged.cs");

        // C may no longer see A: it isn't told where B is any more
        rights[DeviceC.Id] = HexLinkRights.None;
        int before = others[1].Services.Presences.Count;
        hubFromB.SetPresence(new HexLinkPresence("work/b.cs", 1, 1));
        Assert.True(Eventually(() => hub.Presences.Any(each => each.Where?.File == "work/b.cs")));
        Thread.Sleep(300);
        Assert.Equal(before, others[1].Services.Presences.Count);

        link.Dispose();
        foreach (var (services, theirs) in others)
        {
            theirs.Dispose();
            services.Dispose();
        }

        hub.Dispose();
    }

    [Fact]
    public async Task A_link_holds_at_most_eight_and_one_per_device()
    {
        using var services = new LinkServices(DeviceA);
        using var link = new HexLink(services);
        var (a, _) = HexLinkTestKit.Pair();
        var (c, _) = HexLinkTestKit.Pair();
        _ = link.Add(new HexLinkConnection(a, DeviceB, "memory", false, () => HexLinkRights.View));
        Assert.Equal(HexLinkErrors.BUSY, await CodeOf(link.Add(new HexLinkConnection(c, DeviceB, "memory", false, () => HexLinkRights.View))));
        for (int i = 0; i < HexLink.MOST - 1; i++)
        {
            var (e, _) = HexLinkTestKit.Pair();
            _ = link.Add(new HexLinkConnection(e, new HexLinkDevice(i.ToString("X64"), $"d{i}"), "memory", false, () => HexLinkRights.View));
        }

        var (f, _) = HexLinkTestKit.Pair();
        Assert.Equal(HexLinkErrors.BUSY, await CodeOf(link.Add(new HexLinkConnection(f, DeviceC, "memory", false, () => HexLinkRights.View))));
    }

    [Fact]
    public void The_allow_list_says_what_each_tool_takes()
    {
        Assert.Equal(HexLinkRights.View, HexLinkAllowed.Needs("hex_read"));
        Assert.Equal(HexLinkRights.View, HexLinkAllowed.Needs("hex_agent_wait"));
        Assert.Equal(HexLinkRights.View, HexLinkAllowed.Needs("hex_agent_inbox"));
        Assert.Equal(HexLinkRights.Steer, HexLinkAllowed.Needs("hex_agent_spawn"));
        Assert.Equal(HexLinkRights.Edit, HexLinkAllowed.Needs("hex_rename"));
        Assert.Null(HexLinkAllowed.Needs("hex_terminal_run"));
        Assert.Null(HexLinkAllowed.Needs("HEX_READ"));
        Assert.False(HexLinkAllowed.May(HexLinkRights.Steer, "hex_agent_send"));
        Assert.True(HexLinkAllowed.May(HexLinkRights.View | HexLinkRights.Steer, "hex_agent_send"));
        Assert.False(HexLinkAllowed.May(HexLinkRights.View | HexLinkRights.Steer, "hex_edit"));
        Assert.False(HexLinkAllowed.May(HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit, "hex_debug_start"));
    }
}
