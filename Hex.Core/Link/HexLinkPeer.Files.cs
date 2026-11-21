using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Hex.Core;

/// <summary>An entry of a folder of the other machine's workspace.</summary>
internal sealed record HexLinkEntry(string Name, bool Folder, long Size);

/// <summary>
/// A file of the other machine's read: its bytes as they are there, its text when it's text (with the encoding it's in,
/// utf-8, utf-8-bom, utf-16le, utf-16be, or binary, and its line ends), the hash of its bytes, which a write back gives as
/// its base, and when it was last changed (Unix milliseconds).
/// </summary>
internal sealed record HexLinkFile(string Path, byte[] Bytes, string? Text, string Encoding, string Eol, string Hash, long Modified);

internal sealed partial class HexLinkPeer
{
    /// <summary>A watched file of the other machine's moved there, its wire path and new hash (null when it went). From the reading task.</summary>
    public Action<string, string?>? FileChanged { get; set; }

    /// <summary>A folder of the other machine's workspace listed, the empty path its folders.</summary>
    public async Task<IReadOnlyList<HexLinkEntry>> List(string path, CancellationToken cancel = default)
    {
        JsonObject said = await AskObject("fs/list", new JsonObject { ["path"] = path }, cancel);
        return [.. HexLinkData.Objects(said, "entries", HexLinkFiles.MOST_LISTED)
            .Where(entry => HexLinkData.String(entry, "name", 1000) is { Length: > 0 } name && !name.Contains('/') && name is not ("." or ".."))
            .Select(entry => new HexLinkEntry(HexLinkData.String(entry, "name", 1000)!, HexLinkData.String(entry, "kind", 10) == "folder", Math.Max(0, HexLinkData.Long(entry, "size") ?? 0)))];
    }

    /// <summary>A file of the other machine's read, up to 8 MB (too big past it).</summary>
    public async Task<HexLinkFile> Read(string path, CancellationToken cancel = default)
    {
        JsonObject said = await AskObject("fs/read", new JsonObject { ["path"] = path }, cancel);
        string encoding = HexLinkData.String(said, "encoding", 20) ?? "binary";
        string? text = HexLinkData.String(said, "text", HexLinkFiles.MOST_READ);
        byte[] bytes;
        if (text is not null && HexLinkFiles.Encode(text, encoding) is { } encoded)
        {
            bytes = encoded;
        }
        else
        {
            text = null;
            encoding = "binary";
            try
            {
                bytes = Convert.FromBase64String(HexLinkData.String(said, "base64", HexLinkFiles.MOST_READ / 3 * 4 + 4) ?? string.Empty);
            }
            catch (FormatException)
            {
                throw new HexLinkException(HexLinkErrors.INVALID_REQUEST, $"The other Hex sent {path} in a shape this one can't read.");
            }
        }

        string hash = HexLinkFiles.Hash(bytes);
        // What it says the hash is has to be what the bytes are, or a write back would conflict for ever
        if (HexLinkData.String(said, "hash", 64) is { } sent && !sent.Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new HexLinkException(HexLinkErrors.INVALID_REQUEST, $"{path} came over with a hash that isn't its bytes'.");
        return new HexLinkFile(path, bytes, text, encoding, HexLinkData.String(said, "eol", 8) ?? string.Empty, hash, HexLinkData.Long(said, "modified") ?? 0);
    }

    /// <summary>
    /// A file of the other machine's written, if it's still what was read (<paramref name="baseHash"/>, null for a new
    /// file), its new hash back. A conflict throws a <see cref="HexLinkException"/> of <see cref="HexLinkErrors.CONFLICT"/>,
    /// <see cref="ConflictHash"/> says the hash it has now. The bytes go as they are, so the file's encoding stays.
    /// </summary>
    public async Task<string> Write(string path, string? baseHash, byte[] bytes, CancellationToken cancel = default)
    {
        JsonObject said = await AskObject("fs/write", new JsonObject { ["path"] = path, ["base"] = baseHash, ["base64"] = Convert.ToBase64String(bytes) }, cancel);
        return HexLinkData.String(said, "hash", 64) ?? HexLinkFiles.Hash(bytes);
    }

    /// <summary>The hash a file has now on the other machine, from a conflict, null when it's gone or it isn't one.</summary>
    public static string? ConflictHash(HexLinkException conflict) =>
        conflict.Code == HexLinkErrors.CONFLICT ? HexLinkData.String(conflict.Detail as JsonObject, "hash", 64) : null;

    private readonly ConcurrentDictionary<string, string?> fileWatches = new(StringComparer.Ordinal);

    /// <summary>A file of the other machine's watched, <see cref="FileChanged"/> said when it moves; its hash now.</summary>
    public async Task<string?> Watch(string path, CancellationToken cancel = default)
    {
        string? hash = await WatchFile(path, cancel);
        fileWatches[path] = hash;
        return hash;
    }

    private async Task<string?> WatchFile(string path, CancellationToken cancel) =>
        HexLinkData.String(await AskObject("fs/watch", new JsonObject { ["path"] = path }, cancel), "hash", 64);

    /// <summary>A file no longer watched.</summary>
    public Task Unwatch(string path, CancellationToken cancel = default)
    {
        fileWatches.TryRemove(path, out _);
        return session.Ask("fs/unwatch", new JsonObject { ["path"] = path }, cancel);
    }

    private async Task RestoreFileWatches(Func<Func<Task>, Task> attempt, Func<bool> stillAllowed)
    {
        foreach (var (path, previous) in fileWatches)
        {
            await attempt(async () =>
            {
                string? hash = await WatchFile(path, session.Closing);
                if (stillAllowed() && fileWatches.TryUpdate(path, hash, previous) && previous != hash)
                    FileChanged?.Invoke(path, hash);
            });
        }
    }

    // The reader only enqueues messages. One asynchronous writer owns the temp file and validates both size reports.
    private sealed class Receiving
    {
        private readonly Channel<(long At, string Base64)> chunks = Channel.CreateBounded<(long, string)>(
            new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = true });
        private readonly TaskCompletionSource<long> advertised = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long ended = -1;

        public void Expect(long size) => advertised.TrySetResult(size);

        public void Queue(long at, string data)
        {
            if (!chunks.Writer.TryWrite((at, data)))
                Fail("the download could not keep up with its sender");
        }

        public void Finish(long size)
        {
            if (ended >= 0)
                return;
            if (size < 0 || size > MOST_BUNDLE)
            {
                Fail("the completion size was invalid");
                return;
            }
            ended = size;
            chunks.Writer.TryComplete();
        }

        public void Fail(string why) => chunks.Writer.TryComplete(BundleFailure(why));

        public async Task Receive(string file, Action<long, long>? progress, CancellationToken cancel)
        {
            await using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            long written = 0;
            try
            {
                await foreach (var (at, text) in chunks.Reader.ReadAllAsync(cancel).ConfigureAwait(false))
                {
                    byte[] data = Convert.FromBase64String(text);
                    long expected = advertised.Task.IsCompletedSuccessfully ? advertised.Task.Result : -1;
                    long next = written + data.Length;
                    if (at != written || data.Length is 0 or > HexLinkGit.CHUNK || next > MOST_BUNDLE || (expected >= 0 && next > expected))
                        throw BundleFailure("its chunks didn't come in order or came to more than it said");
                    await stream.WriteAsync(data, cancel).ConfigureAwait(false);
                    written = next;
                    progress?.Invoke(written, expected);
                }

                long size = await advertised.Task.WaitAsync(cancel).ConfigureAwait(false);
                if (size != written || ended != written)
                    throw BundleFailure($"it came to {written} bytes, the response said {size}, and completion said {ended}");
                progress?.Invoke(written, size);
            }
            catch (Exception e) when (e is FormatException or IOException)
            {
                throw BundleFailure(e.Message);
            }
        }
    }

    private static HexLinkException BundleFailure(string why) =>
        new(HexLinkErrors.INTERNAL_ERROR, $"The bundle didn't come over, {why}.");

    /// <summary>The biggest bundle taken, 8 GB.</summary>
    public const long MOST_BUNDLE = 8L * 1024 * 1024 * 1024;

    private readonly ConcurrentDictionary<string, Receiving> bundles = new(StringComparer.Ordinal);

    /// <summary>
    /// A repository of the other machine's brought over as a bundle in a temp file (the caller's to delete), less the
    /// commits of <paramref name="have"/> (HexLinkGit.Haves of a clone here), checked by git before it's handed back.
    /// Null when there's nothing this machine hasn't got. <paramref name="progress"/> hears bytes so far and in all.
    /// </summary>
    public async Task<string?> Bundle(string repository, IReadOnlyList<string> have, Action<long, long>? progress = null, string? repo = null, CancellationToken cancel = default)
    {
        string id = Guid.NewGuid().ToString("N");
        string file = Path.Combine(Path.GetTempPath(), $"hexlink-{id}.bundle");
        var receiving = new Receiving();
        bundles[id] = receiving;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel, session.Closing);
        Task receive = Task.Run(() => receiving.Receive(file, progress, stop.Token));
        Task<JsonObject> request = AskObject("git/bundle", new JsonObject
        {
            ["repository"] = repository,
            ["id"] = id,
            ["have"] = new JsonArray([.. have.Take(HexLinkGit.MOST_HAVE).Select(sha => (JsonNode)sha)])
        }, stop.Token, TimeSpan.FromMinutes(15));
        bool finished = false;
        bool keep = false;
        try
        {
            if (await Task.WhenAny(receive, request) == receive)
                await receive;
            JsonObject said = await request;
            if (HexLinkData.String(said, "id", 64) != id)
                throw BundleFailure("the response had a different id");
            long size = HexLinkData.Long(said, "size") ?? -1;
            if (size < 0 || size > MOST_BUNDLE)
                throw new HexLinkException(HexLinkErrors.TOO_BIG, "The bundle is bigger than a link takes.");
            receiving.Expect(size);
            await receive.WaitAsync(TimeSpan.FromHours(2), stop.Token);
            finished = true;
            if (size == 0)
                return null;

            if (await Task.Run(() => HexLinkGit.Verify(file, repo, stop.Token), stop.Token) is { } bad)
                throw new HexLinkException(HexLinkErrors.INTERNAL_ERROR, $"The bundle didn't verify, {bad}");
            keep = true;
            return file;
        }
        finally
        {
            bundles.TryRemove(id, out _);
            if (!finished && !session.IsClosed)
                session.Tell("git/cancel", new JsonObject { ["id"] = id });
            stop.Cancel();
            try { await receive; } catch (Exception) { }
            if (!request.IsCompletedSuccessfully)
                _ = request.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
            if (!keep)
                HexLinkGit.TryDelete(file);
        }
    }

    private void BundleChunk(JsonObject said)
    {
        if (HexLinkData.String(said, "id", 64) is not { } id || !bundles.TryGetValue(id, out Receiving? receiving))
            return;
        if (HexLinkData.Long(said, "at") is not { } at || HexLinkData.String(said, "data", HexLinkGit.CHUNK / 3 * 4 + 8) is not { } data)
        {
            receiving.Fail("a chunk wasn't one");
            return;
        }

        receiving.Queue(at, data);
    }

    private void BundleDone(JsonObject said)
    {
        if (HexLinkData.String(said, "id", 64) is not { } id || !bundles.TryGetValue(id, out Receiving? receiving))
            return;
        if (HexLinkData.Bool(said, "ok") != true)
            receiving.Fail(HexLinkData.String(said, "error", 500) ?? "the other Hex couldn't send it");
        else
            receiving.Finish(HexLinkData.Long(said, "size") ?? -1);
    }
}
