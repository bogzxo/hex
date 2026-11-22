using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>A bounded pack of small files. Each entry is a little-endian JSON header length, header and raw bytes.</summary>
internal static class HexLinkFilePack
{
    public const int MaximumFiles = 128;
    public const int MaximumFile = 512 * 1024;
    public const int MaximumSingle = HexLinkFiles.MOST_READ;
    public const int MaximumLength = 18 * 1024 * 1024;
    private const int MaximumHeader = 48 * 1024;

    // Allow for UTF-8 JSON escapes, the SHA-256 and the length prefix before constructing the headers.
    public static int WireCost(string path, int bytes) => checked(256 + 6 * path.Length + bytes);

    internal sealed record Entry(string Path, byte[] Bytes, string? Base = null, bool? Executable = null);

    internal sealed record Outcome(string Path, string? Hash, HexLinkException? Error = null, bool Held = false)
    {
        public JsonObject Json() => new()
        {
            ["path"] = Path,
            ["hash"] = Hash,
            ["error"] = Error?.Code,
            ["message"] = Error?.Message,
            ["detail"] = Error?.Detail?.DeepClone(),
            ["held"] = Held ? true : null
        };

        public static Outcome Of(JsonObject said)
        {
            string path = HexLinkData.Need(said, "path", HexLinkFiles.MOST_PATH);
            string? hash = HexLinkData.String(said, "hash", 64);
            HexLinkException? error = HexLinkData.Int(said, "error") is int code
                ? new HexLinkException(code, HexLinkData.String(said, "message", 2000) ?? "A Link pack entry failed.", said["detail"]?.DeepClone())
                : null;
            return new Outcome(path, hash, error, HexLinkData.Bool(said, "held") == true);
        }
    }

    public static async Task<JsonNode?> Send(HexLinkSession session, string purpose, JsonObject metadata, IReadOnlyList<Entry> entries, CancellationToken cancel)
    {
        if (entries.Count > MaximumFiles)
            throw new ArgumentException("A Link file pack contains too many files.", nameof(entries));

        var prepare = HexLinkSyncTiming.Measure("pack.build-headers", entries.Count);
        var headers = new byte[entries.Count][];
        int maximum = entries.Count == 1 ? MaximumSingle : MaximumFile;
        int length = 0;
        for (int i = 0; i < entries.Count; i++)
        {
            Entry entry = entries[i];
            var json = new JsonObject { ["path"] = entry.Path, ["length"] = entry.Bytes.Length, ["base"] = entry.Base, ["executable"] = entry.Executable };
            byte[] header = Encoding.UTF8.GetBytes(json.ToJsonString());
            if (header.Length > MaximumHeader || entry.Bytes.Length > maximum)
                throw new HexLinkException(HexLinkErrors.TOO_BIG, "An entry is too large for a Link file pack.");
            headers[i] = header;
            length += sizeof(int) + header.Length + entry.Bytes.Length;
        }

        if (length > MaximumLength)
            throw new HexLinkException(HexLinkErrors.TOO_BIG, "The Link file pack is too large.");

        prepare.Dispose();
        var opening = HexLinkSyncTiming.Measure("pack.open");
        await using HexLinkSession.BulkUpload upload = await session.OpenBulkAsync(purpose, length, metadata, cancel);
        opening.Dispose();
        var frames = HexLinkSyncTiming.Measure("pack.copy-and-send", length);
        // A full frame carries many small files. Never allocate a contiguous buffer the size of the pack.
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(HexLinkFrameHeader.MaximumPayload);
        try
        {
            int used = 0;
            byte[] size = new byte[sizeof(int)];
            for (int i = 0; i < entries.Count; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(size, headers[i].Length);
                await Add(size);
                await Add(headers[i]);
                await Add(entries[i].Bytes);
            }
            if (used != 0)
                await upload.WriteAsync(buffer.AsMemory(0, used), cancel);

            async ValueTask Add(ReadOnlyMemory<byte> bytes)
            {
                while (!bytes.IsEmpty)
                {
                    int amount = Math.Min(bytes.Length, HexLinkFrameHeader.MaximumPayload - used);
                    bytes[..amount].CopyTo(buffer.AsMemory(used, amount));
                    bytes = bytes[amount..];
                    used += amount;
                    if (used == HexLinkFrameHeader.MaximumPayload)
                    {
                        await upload.WriteAsync(buffer.AsMemory(0, used), cancel);
                        used = 0;
                    }
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        frames.Dispose();
        using var completing = HexLinkSyncTiming.Measure("pack.commit-round-trip", entries.Count);
        return await upload.CompleteAsync(cancel);
    }

    private static List<Entry> Parse(ReadOnlySpan<byte> body)
    {
        var entries = new List<Entry>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        while (!body.IsEmpty)
        {
            if (entries.Count == MaximumFiles || body.Length < sizeof(int))
                throw HexLinkException.Invalid("Invalid Link file pack.");

            int length = BinaryPrimitives.ReadInt32LittleEndian(body);
            body = body[sizeof(int)..];
            if (length <= 0 || length > MaximumHeader || length > body.Length)
                throw HexLinkException.Invalid("Invalid Link pack header length.");
            if (JsonNode.Parse(body[..length]) is not JsonObject header ||
                HexLinkData.String(header, "path", HexLinkFiles.MOST_PATH + 1) is not { Length: > 0 and <= HexLinkFiles.MOST_PATH } path ||
                !paths.Add(path) || HexLinkData.Int(header, "length") is not int bytes || bytes < 0 || bytes > MaximumSingle ||
                bytes > body.Length - length || (bytes > MaximumFile && (entries.Count != 0 || bytes != body.Length - length)))
                throw HexLinkException.Invalid("Invalid or duplicate Link file pack entry.");
            body = body[length..];

            string? expected = HexLinkData.String(header, "base", 65);
            if (header["base"] is not null && (expected is not { Length: 64 } || !expected.All(char.IsAsciiHexDigit)))
                throw HexLinkException.Invalid("Invalid Link file pack base hash.");
            entries.Add(new Entry(path, body[..bytes].ToArray(), expected, HexLinkData.Bool(header, "executable")));
            body = body[bytes..];
        }

        return entries;
    }

    /// <summary>Stages a bounded pack and hands all entries over after the session verifies its length and hash.</summary>
    public sealed class Sink : IHexLinkBulkSink
    {
        private readonly MemoryStream staging;
        private readonly Func<IReadOnlyList<Entry>, CancellationToken, Task<JsonNode?>> commit;

        public Sink(long length, Func<IReadOnlyList<Entry>, CancellationToken, Task<JsonNode?>> commit)
        {
            if (length < 0 || length > MaximumLength)
                throw new HexLinkException(HexLinkErrors.TOO_BIG, "The Link file pack is too large.");
            staging = new MemoryStream((int)length);
            this.commit = commit;
        }

        public JsonNode? Result { get; private set; }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancel)
        {
            using var timing = HexLinkSyncTiming.Measure("pack.receiver-stage", data.Length);
            await staging.WriteAsync(data, cancel);
        }

        public async ValueTask CommitAsync(CancellationToken cancel)
        {
            List<Entry> entries;
            using (HexLinkSyncTiming.Measure("pack.receiver-parse", (int)staging.Length))
                entries = Parse(staging.GetBuffer().AsSpan(0, (int)staging.Length));
            Result = await commit(entries, cancel);
        }

        public ValueTask DisposeAsync() => staging.DisposeAsync();
    }
}

internal sealed partial class HexLinkSync
{
    private sealed record PendingRead(string Path, int Size, CancellationToken Cancel, TaskCompletionSource<HexLinkFile> Answer);
    private sealed record PendingWrite(HexLinkFilePack.Entry Entry, CancellationToken Cancel, TaskCompletionSource<string> Answer);
    private PackBatcher<PendingRead>? packedReads;
    private PackBatcher<PendingWrite>? packedWrites;
    private volatile bool noPackReads, noPackWrites;

    private async Task<HexLinkFile> FetchPacked(string path, CancellationToken cancel)
    {
        long size;
        lock (gate)
            size = remote.GetValueOrDefault(path)?.Size ?? 0;
        if (packedReads is not null && !noPackReads)
        {
            try
            {
                if (size <= HexLinkFilePack.MaximumFile)
                {
                    var answer = new TaskCompletionSource<HexLinkFile>(TaskCreationOptions.RunContinuationsAsynchronously);
                    packedReads.Add(new PendingRead(path, (int)size, cancel, answer));
                    try
                    {
                        return await answer.Task.WaitAsync(cancel);
                    }
                    catch (HexLinkException e) when (e.Code == HexLinkErrors.TOO_BIG)
                    {
                        // The file grew since the manifest. Retry it outside the small-file pack.
                    }
                }

                var (files, errors) = await peer.ReadFilePack([path], cancel);
                if (files.TryGetValue(path, out HexLinkFile? read))
                    return read;
                throw errors[path];
            }
            catch (HexLinkException e) when (e.Code == HexLinkErrors.METHOD_NOT_FOUND)
            {
                noPackReads = true;
            }
        }
        return await Asked(() => peer.Read(path, cancel), cancel);
    }

    private async Task<string> PushPacked(string path, string? baseHash, byte[] bytes, bool? executable, CancellationToken cancel)
    {
        if (packedWrites is not null && !noPackWrites)
        {
            var entry = new HexLinkFilePack.Entry(path, bytes, baseHash, executable);
            try
            {
                if (bytes.Length <= HexLinkFilePack.MaximumFile)
                {
                    var answer = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    packedWrites.Add(new PendingWrite(entry, cancel, answer));
                    return await answer.Task.WaitAsync(cancel);
                }

                var outcomes = await peer.WriteFilePack(folder.Name, [entry], cancel);
                HexLinkFilePack.Outcome outcome = outcomes[path];
                if (outcome.Held)
                    throw HexLinkHoldReply.HeldOn(Device.Name);
                if (outcome.Error is { } error)
                    throw error;
                return outcome.Hash ?? HexLinkFiles.Hash(bytes);
            }
            catch (HexLinkException e) when (e.Code is HexLinkErrors.METHOD_NOT_FOUND or HexLinkErrors.NOT_FOUND)
            {
                noPackWrites = true;
            }
        }
        return await Asked(() => peer.Write(path, baseHash, bytes, cancel), cancel);
    }

    private async Task ReadPacked(PendingRead[] batch)
    {
        PendingRead[] active = [.. batch.Where(each => !each.Cancel.IsCancellationRequested)];
        try
        {
            if (active.Length == 0)
                return;
            string[] paths = [.. active.Select(each => each.Path).Distinct(StringComparer.Ordinal)];
            var (files, errors) = await peer.ReadFilePack(paths, active[0].Cancel);
            foreach (PendingRead pending in active)
            {
                if (files.TryGetValue(pending.Path, out HexLinkFile? file))
                    pending.Answer.TrySetResult(file);
                else
                    pending.Answer.TrySetException(errors[pending.Path]);
            }
        }
        catch (Exception e)
        {
            foreach (PendingRead pending in active)
                pending.Answer.TrySetException(e);
        }
    }

    private async Task WritePacked(PendingWrite[] batch)
    {
        foreach (PendingWrite pending in batch)
            if (Holds?.IsHeld(pending.Entry.Path) == true)
                pending.Answer.TrySetCanceled();
        PendingWrite[] active = [.. batch.Where(each => !each.Cancel.IsCancellationRequested && !each.Answer.Task.IsCompleted)];
        try
        {
            if (active.Length == 0)
                return;
            var outcome = await peer.WriteFilePack(folder.Name, [.. active.Select(each => each.Entry)], active[0].Cancel);
            foreach (PendingWrite pending in active)
            {
                HexLinkFilePack.Outcome result = outcome[pending.Entry.Path];
                if (result.Held)
                    pending.Answer.TrySetException(HexLinkHoldReply.HeldOn(Device.Name));
                else if (result.Error is { } error)
                    pending.Answer.TrySetException(error);
                else
                    pending.Answer.TrySetResult(result.Hash ?? HexLinkFiles.Hash(pending.Entry.Bytes));
            }
        }
        catch (Exception e)
        {
            foreach (PendingWrite pending in active)
                pending.Answer.TrySetException(e);
        }
    }

    // One reader and writer per folder pair form packs from the paths already admitted by the work loop.
    private sealed class PackBatcher<T>(Func<T[], Task> send, Func<T, int> cost)
    {
        private readonly Queue<T> queued = new();
        private bool draining;

        public void Add(T item)
        {
            lock (queued)
            {
                queued.Enqueue(item);
                if (draining)
                    return;
                draining = true;
                _ = Drain();
            }
        }

        private async Task Drain()
        {
            while (true)
            {
                await Task.Delay(6).ConfigureAwait(false);
                T[] batch;
                lock (queued)
                {
                    if (queued.Count == 0)
                    {
                        draining = false;
                        return;
                    }

                    var next = new List<T>(Math.Min(queued.Count, HexLinkFilePack.MaximumFiles));
                    int bytes = 0;
                    while (next.Count < HexLinkFilePack.MaximumFiles && queued.TryPeek(out T? item))
                    {
                        int entry = cost(item);
                        if (next.Count > 0 && bytes + entry > HexLinkFilePack.MaximumLength)
                            break;
                        bytes += entry;
                        next.Add(queued.Dequeue());
                    }
                    batch = [.. next];
                }
                await send(batch).ConfigureAwait(false);
            }
        }
    }
}
