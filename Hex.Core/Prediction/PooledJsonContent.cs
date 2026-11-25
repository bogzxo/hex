using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Hex.Core;

/// <summary>
/// A request body of JSON written into a pooled buffer and sent from it: no array of the body's size per request, no
/// copy of what the writer made. The buffer goes back to the pool when the content is disposed, which the request does
/// when it is.
/// </summary>
internal sealed class PooledJsonContent : HttpContent
{
    private static readonly MediaTypeHeaderValue JsonType = new("application/json");

    // One writer per thread, pointed at each body's buffer in turn
    [ThreadStatic]
    private static Utf8JsonWriter? writer;

    private byte[]? bytes;
    private readonly int length;

    private PooledJsonContent(byte[] bytes, int length)
    {
        this.bytes = bytes;
        this.length = length;
        Headers.ContentType = JsonType;
    }

    /// <summary>The body <paramref name="write"/> writes.</summary>
    public static PooledJsonContent Of(Action<Utf8JsonWriter> write)
    {
        var buffer = new Pooled(1024);
        try
        {
            Utf8JsonWriter json = writer ??= new Utf8JsonWriter(buffer);
            json.Reset(buffer);
            write(json);
            json.Flush();
            (byte[] taken, int count) = buffer.Take();
            return new PooledJsonContent(taken, count);
        }
        catch
        {
            buffer.Give();
            throw;
        }
    }

    /// <summary>The bytes of the body, for the tests.</summary>
    internal ReadOnlySpan<byte> Written => bytes.AsSpan(0, length);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, default);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
        stream.WriteAsync(bytes!.AsMemory(0, length), cancellationToken).AsTask();

    protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
        stream.Write(bytes!, 0, length);

    protected override bool TryComputeLength(out long size)
    {
        size = length;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref bytes, null) is { } rented)
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        base.Dispose(disposing);
    }

    // The writer's buffer: rented, grown by renting a larger one
    private sealed class Pooled(int first) : IBufferWriter<byte>
    {
        private byte[]? array = ArrayPool<byte>.Shared.Rent(first);
        private int written;

        public void Advance(int count) => written += count;

        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            Grow(sizeHint);
            return array.AsMemory(written);
        }

        public Span<byte> GetSpan(int sizeHint = 0)
        {
            Grow(sizeHint);
            return array.AsSpan(written);
        }

        public (byte[], int) Take()
        {
            byte[] taken = array!;
            array = null;
            return (taken, written);
        }

        public void Give()
        {
            if (Interlocked.Exchange(ref array, null) is { } rented)
                ArrayPool<byte>.Shared.Return(rented);
        }

        private void Grow(int sizeHint)
        {
            int need = Math.Max(sizeHint, 1);
            if (array!.Length - written >= need)
                return;
            byte[] larger = ArrayPool<byte>.Shared.Rent(Math.Max(array.Length * 2, written + need));
            Buffer.BlockCopy(array, 0, larger, 0, written);
            ArrayPool<byte>.Shared.Return(array);
            array = larger;
        }
    }
}
