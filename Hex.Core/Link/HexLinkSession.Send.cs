using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Hex.Core;

internal sealed partial class HexLinkSession
{
    private readonly Channel<Outgoing> control = Bounded<Outgoing>(32);
    private readonly Channel<Outgoing> replies = Bounded<Outgoing>(512);
    private readonly Channel<Outgoing> outgoing = Bounded<Outgoing>(8);
    private readonly Channel<BulkFrame> bulkControl = Bounded<BulkFrame>(32);
    private readonly Channel<BulkFrame> bulkOutgoing = Bounded<BulkFrame>(36);
    private readonly SemaphoreSlim sendReady = new(0);
    private readonly Lock snapshotsGate = new();
    private readonly Dictionary<(string Method, string? Source), JsonObject> snapshots = [];
    private bool sendingSnapshots;
    private readonly record struct Outgoing(JsonNode Message, CancellationToken Cancel = default, TaskCompletionSource<bool>? Sent = null);

    // Used by the writer task alone.
    private ArrayBufferWriter<byte> sendBuffer = new(4096);
    private readonly Utf8JsonWriter sendWriter = new(Stream.Null, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private async ValueTask<bool> SendAsync(JsonNode message, bool priority, CancellationToken cancel)
    {
        var sent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Channel<Outgoing> queue = priority ? control : outgoing;
        await QueueJson(queue, new Outgoing(message, cancel, sent), cancel);
        return await sent.Task.WaitAsync(cancel);
    }

    // The caller supplies replaceable state, such as the latest pointer or current grants. Ordered updates use TellAsync.
    internal void TellLatest(string method, JsonObject parameters, string? source = null)
    {
        lock (snapshotsGate)
        {
            if (IsClosed)
                return;

            var key = (method, source);
            // The hub supplies at most MOST active presence sources. Retire an old queued source after device churn.
            if (source is not null && !snapshots.ContainsKey(key) && snapshots.Count(each => each.Key.Source is not null) >= HexLink.MOST)
                snapshots.Remove(snapshots.Keys.First(each => each.Source is not null));
            snapshots[key] = parameters;
            if (sendingSnapshots)
                return;
            sendingSnapshots = true;
        }
        _ = SendSnapshots();
    }

    private async Task SendSnapshots()
    {
        while (true)
        {
            KeyValuePair<(string Method, string? Source), JsonObject> next;
            lock (snapshotsGate)
            {
                if (IsClosed || snapshots.Count == 0)
                {
                    snapshots.Clear();
                    sendingSnapshots = false;
                    return;
                }
                next = snapshots.First();
                snapshots.Remove(next.Key);
            }
            try { await TellAsync(next.Key.Method, next.Value); }
            catch (Exception error) when (!IsClosed) { AuditFault(next.Key.Method, error); }
        }
    }

    private async ValueTask QueueJson(Channel<Outgoing> queue, Outgoing message, CancellationToken cancel)
    {
        await queue.Writer.WriteAsync(message, cancel);
        sendReady.Release();
    }

    private async Task Write()
    {
        CancellationToken stop = closing.Token;
        var header = new byte[HexLinkFrameHeader.Size];
        try
        {
            while (true)
            {
                await sendReady.WaitAsync(stop);
                if (!control.Reader.TryRead(out Outgoing next))
                {
                    if (bulkControl.Reader.TryRead(out BulkFrame frame))
                    {
                        await WriteFrame(frame, header, stop);
                        continue;
                    }
                    if (!replies.Reader.TryRead(out next) && !outgoing.Reader.TryRead(out next))
                    {
                        if (bulkOutgoing.Reader.TryRead(out frame))
                            await WriteFrame(frame, header, stop);
                        continue;
                    }
                }
                if (next.Cancel.IsCancellationRequested)
                    continue;

                bool sent = await WriteJson(next.Message, stop);
                next.Sent?.TrySetResult(sent);
                if (!sent && next.Sent is null)
                    AuditFault("send", new InvalidDataException("A notification exceeded the Link message limit."));
            }
        }
        catch (OperationCanceledException) when (IsClosed)
        {
        }
        catch (Exception e)
        {
            if (!IsClosed)
                AuditFault("write", e);
            Close("it couldn't be written to");
        }
        finally
        {
            while (control.Reader.TryRead(out _))
            {
            }
            while (replies.Reader.TryRead(out _))
            {
            }
            while (outgoing.Reader.TryRead(out _))
            {
            }
            while (bulkControl.Reader.TryRead(out BulkFrame frame))
                frame.Release();
            while (bulkOutgoing.Reader.TryRead(out BulkFrame frame))
                frame.Release();
            sendWriter.Dispose();
        }
    }

    private async ValueTask<bool> WriteJson(JsonNode message, CancellationToken stop)
    {
        sendBuffer.ResetWrittenCount();
        sendWriter.Reset(sendBuffer);
        message.WriteTo(sendWriter);
        sendWriter.Flush();
        bool fits = sendBuffer.WrittenCount < MOST_LINE;
        if (fits)
        {
            sendBuffer.GetSpan(1)[0] = (byte)'\n';
            sendBuffer.Advance(1);
            // Only shutdown cancels IO after a message starts, so caller cancellation can't truncate it.
            await stream.WriteAsync(sendBuffer.WrittenMemory, stop);
            await stream.FlushAsync(stop);
        }

        if (sendBuffer.Capacity > 1024 * 1024)
        {
            sendBuffer = new ArrayBufferWriter<byte>(4096);
            sendWriter.Reset(sendBuffer);
        }

        return fits;
    }

    private async ValueTask QueueFrame(BulkFrame frame, CancellationToken cancel)
    {
        await bulkOutgoing.Writer.WriteAsync(frame, cancel);
        sendReady.Release();
    }

    private bool TryQueueFrame(BulkFrame frame, bool priority)
    {
        if (!(priority ? bulkControl : bulkOutgoing).Writer.TryWrite(frame))
            return false;
        sendReady.Release();
        return true;
    }

    private async ValueTask WriteFrame(BulkFrame frame, byte[] headerBuffer, CancellationToken cancel)
    {
        using var timing = HexLinkSyncTiming.Measure("wire.tls-frame-write", frame.Header.WireLength);
        try
        {
            if (frame.Bytes is { } packet)
            {
                await stream.WriteAsync(packet.AsMemory(0, HexLinkFrameHeader.Size + frame.Header.WireLength), cancel);
            }
            else
            {
                frame.Header.Write(headerBuffer);
                await stream.WriteAsync(headerBuffer, cancel);
            }
            await stream.FlushAsync(cancel);
        }
        finally
        {
            frame.Release();
        }
    }
}
