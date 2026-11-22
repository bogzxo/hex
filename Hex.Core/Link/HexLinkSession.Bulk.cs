using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Hex.Core;

/// <summary>A staging sink for an authenticated upload. Write memory is valid until the call completes.</summary>
internal interface IHexLinkBulkSink : IAsyncDisposable
{
    /// <summary>Stages ordered bytes. Disposal discards staged data if the upload failed or was aborted.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancel);

    /// <summary>Publishes staged data after the session verifies length and SHA-256.</summary>
    ValueTask CommitAsync(CancellationToken cancel);

    /// <summary>An optional result returned by the verified commit.</summary>
    JsonNode? Result => null;
}

internal sealed partial class HexLinkSession
{
    private readonly ConcurrentDictionary<string, BulkReceiver> bulkReceivers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, BulkUpload> uploads = new();
    private readonly ConcurrentDictionary<long, IncomingBulk> downloads = new();
    private readonly SemaphoreSlim uploadSlots = new(4);
    private readonly SemaphoreSlim downloadSlots = new(4);
    private long lastBulkId;
    /// <summary>Whether this authenticated connection negotiated bulk frames. Use the existing JSON methods otherwise.</summary>
    public bool SupportsBulk => Connection.Bulk != HexLinkBulkSupport.None;

    /// <summary>
    /// Registers a bulk purpose and the receiver's required rights. The factory validates metadata and the declared
    /// length, then returns a sink. The session checks the rights at open, before each frame, and at commit.
    /// </summary>
    public void RegisterBulkReceiver(string purpose, HexLinkRights rights,
        Func<long, JsonObject, CancellationToken, ValueTask<IHexLinkBulkSink>> open)
    {
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        ArgumentNullException.ThrowIfNull(open);
        if (purpose.Length > 80 || rights == HexLinkRights.None)
            throw new ArgumentException("A bulk receiver needs a short purpose and a Link right.");
        if (!bulkReceivers.TryAdd(purpose, new BulkReceiver(rights, open)))
            throw new InvalidOperationException($"A Link bulk receiver for {purpose} is already registered.");
    }

    /// <summary>
    /// Opens an ordered upload of exactly <paramref name="length"/> bytes to a registered purpose. Up to four uploads
    /// in each direction share the connection. Each upload has eight credits for frames of up to 256 KiB, so at most
    /// 2 MiB per upload is outstanding. The open is subject to RPC admission. Data and credits use the byte window
    /// independently of RPC admission. A caller waits for capacity before allocating a stream ID. The token also
    /// cancels subsequent writes and completion. Dispose an unfinished upload to abort it and release its slot.
    /// </summary>
    public async Task<BulkUpload> OpenBulkAsync(string purpose, long length, JsonObject? metadata = null, CancellationToken cancel = default)
    {
        if (!SupportsBulk)
            throw new HexLinkException(HexLinkErrors.METHOD_NOT_FOUND, "This peer did not negotiate Link bulk transfers.");
        ArgumentException.ThrowIfNullOrEmpty(purpose);
        if (purpose.Length > 80)
            throw new ArgumentException("A bulk purpose takes at most 80 characters.", nameof(purpose));
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancel, closing.Token);
        try
        {
            await uploadSlots.WaitAsync(waiting.Token);
        }
        catch (OperationCanceledException) when (IsClosed && !cancel.IsCancellationRequested)
        {
            throw new HexLinkException(HexLinkErrors.CLOSED, $"The link closed, {Why}");
        }

        long id = Interlocked.Increment(ref lastBulkId);
        BulkUpload upload;
        try
        {
            upload = new BulkUpload(this, id, length, cancel);
        }
        catch
        {
            uploadSlots.Release();
            throw;
        }
        uploads[id] = upload;
        try
        {
            await Ask("link/bulk/open", new JsonObject
            {
                ["stream"] = id,
                ["purpose"] = purpose,
                ["length"] = length,
                ["metadata"] = metadata
            }, cancel);
            return upload;
        }
        catch
        {
            await upload.DisposeAsync();
            throw;
        }
    }

    private IncomingBulk ReserveIncoming(JsonObject parameters, int bytes)
    {
        if (!SupportsBulk)
            throw new HexLinkException(HexLinkErrors.METHOD_NOT_FOUND, "Link bulk transfers were not negotiated.");
        if (bytes > 16 * 1024 || HexLinkData.Long(parameters, "stream") is not > 0 ||
            HexLinkData.Long(parameters, "length") is not >= 0 ||
            HexLinkData.String(parameters, "purpose", 81) is not { Length: > 0 and <= 80 } purpose)
            throw HexLinkException.Invalid("Invalid Link bulk open.");

        if (!bulkReceivers.TryGetValue(purpose, out BulkReceiver receiver))
            throw HexLinkException.NotFound("There is no receiver for this Link bulk purpose.");
        RequireBulkRights(receiver.Rights);
        if (!downloadSlots.Wait(0))
            throw new HexLinkException(HexLinkErrors.BUSY, "Too many Link bulk downloads are already open.");

        long id = HexLinkData.Long(parameters, "stream")!.Value;
        long length = HexLinkData.Long(parameters, "length")!.Value;
        var download = new IncomingBulk(this, id, length, receiver);
        if (!downloads.TryAdd(id, download))
        {
            download.DisposeReservation();
            throw HexLinkException.Invalid("This Link bulk stream is already open.");
        }
        return download;
    }

    private void RequireBulkRights(HexLinkRights required)
    {
        if (!HexLinkAllowed.Has(Connection.Rights, required))
            throw HexLinkException.NotAllowed(string.Join(", ", HexLinkTrust.RightsNames(required)));
    }

    private async Task<JsonNode?> FinishIncoming(JsonObject parameters)
    {
        if (HexLinkData.Long(parameters, "stream") is not long id ||
            !downloads.TryGetValue(id, out IncomingBulk? download) ||
            HexLinkData.String(parameters, "sha256", 65) is not { Length: 64 } hash ||
            !hash.All(Uri.IsHexDigit))
            throw HexLinkException.Invalid("Invalid Link bulk completion.");
        return await download.Complete(hash);
    }

    /// <summary>An ordered bulk upload. Call WriteAsync, CompleteAsync and DisposeAsync serially.</summary>
    internal sealed class BulkUpload : IAsyncDisposable
    {
        private readonly HexLinkSession session;
        private readonly long id;
        private readonly long length;
        private readonly SemaphoreSlim credit = new(HexLinkFrameHeader.Window, HexLinkFrameHeader.Window);
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly ZstandardEncoder? encoder;
        private readonly CancellationToken caller;
        private readonly CancellationTokenSource lifetime;
        private uint sequence;
        private uint acknowledged;
        private long written;
        private bool finished;
        private bool disposed;

        internal BulkUpload(HexLinkSession session, long id, long length, CancellationToken caller)
        {
            this.session = session;
            this.id = id;
            this.length = length;
            if (session.Connection.Bulk == HexLinkBulkSupport.Zstandard)
                encoder = new ZstandardEncoder(1, 18);
            this.caller = caller;
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(caller, session.Closing);
        }

        /// <summary>Copies bytes into pooled frames, waiting asynchronously for credit. A cancelled write requires disposal.</summary>
        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancel = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (finished || data.Length > length - written)
                throw new InvalidOperationException("The write exceeds the declared Link bulk length.");

            // Reuse the upload token on the steady state path. Only an additional per-call token needs linking.
            using var linked = LinkExtraCancellation(cancel);
            CancellationToken stop = linked?.Token ?? lifetime.Token;
            while (!data.IsEmpty)
            {
                // A peer that keeps answering pings while refusing credit must still time out.
                if (!await credit.WaitAsync(PATIENCE, stop))
                    throw new HexLinkException(HexLinkErrors.TIMED_OUT, "The Link bulk receiver stopped returning credit.");
                int count = Math.Min(data.Length, HexLinkFrameHeader.MaximumPayload);
                await QueueData(data[..count], stop);
                written += count;
                data = data[count..];
            }
        }

        private async ValueTask QueueData(ReadOnlyMemory<byte> data, CancellationToken stop)
        {
            byte[] packet = ArrayPool<byte>.Shared.Rent(data.Length + HexLinkFrameHeader.Size);
            try
            {
                var kind = HexLinkFrameKind.Data;
                int size = data.Length;
                Span<byte> body = packet.AsSpan(HexLinkFrameHeader.Size, size);
                if (encoder is not null && CanCompress(data.Span) &&
                    TryCompress(encoder, data.Span, body[..(size - size / 20)], out int compressed))
                {
                    kind = HexLinkFrameKind.Compressed;
                    size = compressed;
                }
                else
                {
                    data.Span.CopyTo(body);
                }

                var header = new HexLinkFrameHeader(kind, id, sequence, size, data.Length);
                uint next = checked(sequence + 1);
                header.Write(packet);
                hash.AppendData(data.Span);
                Volatile.Write(ref sequence, next);
                await session.QueueFrame(new BulkFrame(header, packet), stop);
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(packet);
                throw;
            }
        }

        // Sample the full frame so a source archive or media file doesn't repeatedly invoke the codec.
        private static bool CanCompress(ReadOnlySpan<byte> input)
        {
            if (input.Length < 512)
                return false;
            Span<int> counts = stackalloc int[256];
            counts.Clear();
            int hits = 0;
            int stride = Math.Max(1, input.Length / 512);
            for (int at = 0; at < input.Length; at += stride)
                hits += counts[input[at]]++;
            return hits >= 850;
        }

        private static bool TryCompress(ZstandardEncoder encoder, ReadOnlySpan<byte> input, Span<byte> output, out int size)
        {
            encoder.Reset();
            encoder.SetSourceLength(input.Length);
            return encoder.Compress(input, output, out int used, out size, isFinalBlock: true) == OperationStatus.Done &&
                used == input.Length && size < input.Length;
        }

        internal void Acknowledge(uint number)
        {
            if (number != acknowledged || number >= Volatile.Read(ref sequence))
                throw new InvalidDataException("Invalid Link bulk credit sequence.");
            acknowledged++;
            credit.Release();
        }

        /// <summary>Finishes the upload and waits until the receiver verifies SHA-256 and commits its sink.</summary>
        public async Task<JsonNode?> CompleteAsync(CancellationToken cancel = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (finished || written != length)
                throw new InvalidOperationException("The Link bulk upload has not written its declared length or is already complete.");

            using var linked = LinkExtraCancellation(cancel);
            CancellationToken stop = linked?.Token ?? lifetime.Token;
            for (int remaining = HexLinkFrameHeader.Window; remaining > 0; remaining--)
            {
                if (!await credit.WaitAsync(PATIENCE, stop))
                    throw new HexLinkException(HexLinkErrors.TIMED_OUT, "The Link bulk receiver stopped returning credit.");
            }
            string digest = Convert.ToHexString(hash.GetHashAndReset());
            JsonNode? result = await session.Ask("link/bulk/complete", new JsonObject { ["stream"] = id, ["sha256"] = digest }, stop);
            finished = true;
            return result;
        }

        private CancellationTokenSource? LinkExtraCancellation(CancellationToken cancel) =>
            cancel.CanBeCanceled && cancel != caller ? CancellationTokenSource.CreateLinkedTokenSource(cancel, lifetime.Token) : null;

        public ValueTask DisposeAsync()
        {
            if (disposed)
                return ValueTask.CompletedTask;
            disposed = true;
            session.uploads.TryRemove(id, out _);
            try
            {
                if (!finished && !session.IsClosed &&
                    !session.TryQueueFrame(new BulkFrame(new(HexLinkFrameKind.Abort, id, 0, 0, 0)), priority: false))
                    session.Close("its bulk abort queue filled");
            }
            finally
            {
                session.uploadSlots.Release();
                encoder?.Dispose();
                hash.Dispose();
                lifetime.Dispose();
            }
            return ValueTask.CompletedTask;
        }
    }

    private readonly record struct BulkReceiver(HexLinkRights Rights, Func<long, JsonObject, CancellationToken, ValueTask<IHexLinkBulkSink>> Open);
    private readonly record struct BulkFrame(HexLinkFrameHeader Header, byte[]? Bytes = null)
    {
        public void Release()
        {
            if (Bytes is not null)
                ArrayPool<byte>.Shared.Return(Bytes);
        }
    }

    private sealed class IncomingBulk
    {
        private readonly HexLinkSession session;
        private readonly long id;
        private readonly long length;
        private readonly BulkReceiver receiver;
        private readonly CancellationTokenSource stop;
        private readonly Channel<BulkFrame> frames = Bounded<BulkFrame>(HexLinkFrameHeader.Window);
        private readonly TaskCompletionSource<(JsonNode? Result, Exception? Error)> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long received;
        private uint sequence;
        private int outstanding;
        private int ready;
        private string? expectedHash;

        public IncomingBulk(HexLinkSession session, long id, long length, BulkReceiver receiver)
        {
            this.session = session;
            this.id = id;
            this.length = length;
            this.receiver = receiver;
            stop = CancellationTokenSource.CreateLinkedTokenSource(session.Closing);
            stop.CancelAfter(PATIENCE);
        }

        public async Task<JsonNode?> Open(JsonObject metadata)
        {
            IHexLinkBulkSink sink;
            try
            {
                stop.Token.ThrowIfCancellationRequested();
                sink = await receiver.Open(length, metadata, stop.Token);
            }
            catch
            {
                Release();
                throw;
            }

            Volatile.Write(ref ready, 1);
            _ = Consume(sink);
            return null;
        }

        public void CheckFrame(HexLinkFrameHeader header)
        {
            if (Volatile.Read(ref ready) == 0 || !HexLinkAllowed.Has(session.Connection.Rights, receiver.Rights) ||
                header.Sequence != sequence || sequence == uint.MaxValue ||
                header.Length > length - received || Volatile.Read(ref outstanding) >= HexLinkFrameHeader.Window)
                throw new InvalidDataException("Link bulk frame violates stream rights, sequence, length or credit.");

            sequence++;
            Volatile.Write(ref received, received + header.Length);
            Interlocked.Increment(ref outstanding);
        }

        public bool Enqueue(BulkFrame frame) => frames.Writer.TryWrite(frame);

        public async Task<JsonNode?> Complete(string hash)
        {
            if (Volatile.Read(ref received) != length || Volatile.Read(ref outstanding) != 0 ||
                Interlocked.CompareExchange(ref expectedHash, hash, null) is not null)
                throw HexLinkException.Invalid("Link bulk completion arrived before the declared bytes were consumed.");
            frames.Writer.TryComplete();
            var (result, error) = await completed.Task.WaitAsync(session.Closing);
            if (error is not null)
                throw error;
            return result;
        }

        public void Abort()
        {
            try
            {
                _ = stop.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // The sink finished on its own while this frame was being read.
            }
        }

        private async Task Consume(IHexLinkBulkSink sink)
        {
            CancellationToken cancel = stop.Token;
            Exception? failure = null;
            JsonNode? result = null;
            try
            {
                await using (sink)
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    using ZstandardDecoder? decoder = session.Connection.Bulk == HexLinkBulkSupport.Zstandard ? new(new ZstandardDecompressionOptions { MaxWindowLog2 = 18 }) : null;
                    await foreach (BulkFrame frame in frames.Reader.ReadAllAsync(cancel))
                    {
                        try
                        {
                            if (failure is null)
                                await WriteFrame(frame, sink, decoder, hash, cancel);
                        }
                        catch (Exception e) when (!cancel.IsCancellationRequested)
                        {
                            // Drain the remaining window in order. Completion returns the sink error to the sender.
                            failure = e;
                            if (e is not HexLinkException)
                                session.AuditFault("bulk receive", e);
                        }
                        finally
                        {
                            frame.Release();
                            Interlocked.Decrement(ref outstanding);
                        }

                        var credit = new HexLinkFrameHeader(HexLinkFrameKind.Credit, id, frame.Header.Sequence, 0, 0);
                        if (!session.TryQueueFrame(new BulkFrame(credit), priority: true))
                            throw new IOException("The Link bulk credit queue filled.");
                        stop.CancelAfter(PATIENCE);
                    }

                    if (failure is null)
                    {
                        cancel.ThrowIfCancellationRequested();
                        session.RequireBulkRights(receiver.Rights);
                        if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), expectedHash, StringComparison.OrdinalIgnoreCase))
                            throw HexLinkException.Invalid("The Link bulk hash did not match.");
                        await sink.CommitAsync(cancel);
                        result = sink.Result;
                    }
                }
            }
            catch (Exception e)
            {
                if (e is not HexLinkException && !cancel.IsCancellationRequested && failure is null)
                    session.AuditFault("bulk close", e);
                failure ??= e;
            }
            finally
            {
                frames.Writer.TryComplete();
                while (frames.Reader.TryRead(out BulkFrame frame))
                    frame.Release();
                Release();
                completed.TrySetResult((result, failure));
            }
        }

        private async ValueTask WriteFrame(BulkFrame frame, IHexLinkBulkSink sink, ZstandardDecoder? decoder,
            IncrementalHash hash, CancellationToken cancel)
        {
            session.RequireBulkRights(receiver.Rights);
            byte[]? decoded = null;
            try
            {
                ReadOnlyMemory<byte> bytes = frame.Bytes!.AsMemory(0, frame.Header.WireLength);
                if (frame.Header.Kind == HexLinkFrameKind.Compressed)
                {
                    decoded = ArrayPool<byte>.Shared.Rent(frame.Header.Length);
                    Span<byte> destination = decoded.AsSpan(0, frame.Header.Length);
                    decoder!.Reset();
                    if (decoder.Decompress(bytes.Span, destination, out int consumed, out int written) != OperationStatus.Done ||
                        consumed != bytes.Length || written != destination.Length)
                        throw HexLinkException.Invalid("Invalid Link Zstandard frame.");
                    bytes = decoded.AsMemory(0, frame.Header.Length);
                }

                hash.AppendData(bytes.Span);
                await sink.WriteAsync(bytes, cancel);
            }
            finally
            {
                if (decoded is not null)
                    ArrayPool<byte>.Shared.Return(decoded);
            }
        }

        public void DisposeReservation()
        {
            stop.Dispose();
            session.downloadSlots.Release();
        }

        private void Release()
        {
            session.downloads.TryRemove(id, out _);
            DisposeReservation();
        }
    }
}
