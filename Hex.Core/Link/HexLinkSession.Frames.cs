using System.Buffers;

namespace Hex.Core;

internal sealed partial class HexLinkSession
{
    private async Task Read()
    {
        var input = new WireInput(stream, closing.Token);
        var header = new byte[HexLinkFrameHeader.Size];
        var line = new ArrayBufferWriter<byte>(4096);
        string why = "the other end hung up";
        try
        {
            while (!IsClosed && await input.Ensure())
            {
                Volatile.Write(ref heard, Environment.TickCount64);
                if (SupportsBulk && line.WrittenCount == 0 && input.Buffered[0] == HexLinkFrameHeader.Marker)
                {
                    await ReadFrame(input, header);
                    continue;
                }

                int end = input.Buffered.IndexOf((byte)'\n');
                int count = end < 0 ? input.Buffered.Length : end;
                if (line.WrittenCount + count > MOST_LINE)
                {
                    why = "it sent a line longer than a link takes";
                    break;
                }

                if (end >= 0 && line.WrittenCount == 0)
                {
                    await Received(input.BufferedMemory[..count]);
                }
                else
                {
                    line.Write(input.Buffered[..count]);
                    if (end >= 0)
                    {
                        await Received(line.WrittenMemory);
                        // A big line's buffer is released rather than kept for the link's life.
                        if (line.Capacity > 1024 * 1024)
                            line = new ArrayBufferWriter<byte>(4096);
                        else
                            line.ResetWrittenCount();
                    }
                }

                input.Consume(count + (end < 0 ? 0 : 1));
            }
        }
        catch (InvalidDataException e)
        {
            why = e.Message;
            AuditFault("frame", e);
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

    private async ValueTask ReadFrame(WireInput input, byte[] headerBuffer)
    {
        await input.Exact(headerBuffer);
        HexLinkFrameHeader header = HexLinkFrameHeader.Read(headerBuffer, Connection.Bulk);
        if (header.Kind == HexLinkFrameKind.Credit)
        {
            if (uploads.TryGetValue(header.StreamId, out BulkUpload? upload))
                upload.Acknowledge(header.Sequence);
            return;
        }

        if (header.Kind == HexLinkFrameKind.Abort)
        {
            if (downloads.TryGetValue(header.StreamId, out IncomingBulk? aborted))
                aborted.Abort();
            return;
        }

        if (!downloads.TryGetValue(header.StreamId, out IncomingBulk? download))
            throw new InvalidDataException("A Link bulk frame arrived for an unopened stream.");
        download.CheckFrame(header);
        byte[] bytes = ArrayPool<byte>.Shared.Rent(header.WireLength);
        try
        {
            using (HexLinkSyncTiming.Measure("wire.tls-payload-read", header.WireLength))
                await input.Exact(bytes.AsMemory(0, header.WireLength));
            if (download.Enqueue(new BulkFrame(header, bytes)))
                return;
            throw new InvalidDataException("The Link bulk receive window closed.");
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(bytes);
            throw;
        }
    }

    // Only this reader consumes the authenticated stream. A binary frame can start in the unread part of a TLS record.
    private sealed class WireInput(Stream stream, CancellationToken stop)
    {
        private readonly byte[] buffer = new byte[64 * 1024];
        private int from;
        private int end;

        public ReadOnlyMemory<byte> BufferedMemory => buffer.AsMemory(from, end - from);
        public ReadOnlySpan<byte> Buffered => BufferedMemory.Span;

        public void Consume(int count) => from += count;

        public async ValueTask<bool> Ensure()
        {
            if (from < end)
                return true;
            from = 0;
            end = await stream.ReadAsync(buffer, stop);
            return end != 0;
        }

        public async ValueTask Exact(Memory<byte> destination)
        {
            int copied = Math.Min(destination.Length, end - from);
            buffer.AsMemory(from, copied).CopyTo(destination);
            from += copied;
            if (copied < destination.Length)
                await stream.ReadExactlyAsync(destination[copied..], stop);
        }
    }
}
