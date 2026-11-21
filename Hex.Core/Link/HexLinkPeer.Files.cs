using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>An entry of a folder of the other machine's workspace.</summary>
internal sealed record HexLinkEntry(string Name, bool Folder, long Size);

/// <summary>
/// A file of the other machine's read: its bytes as they are there, its text when it's text (with the encoding it's in,
/// utf-8, utf-8-bom, utf-16le, utf-16be, or binary, and its line ends), the hash of its bytes, which a write back gives as
/// its base, and when it was last changed (Unix milliseconds).
/// </summary>
internal sealed record HexLinkFile(string Path, byte[] Bytes, string? Text, string Encoding, string Eol, string Hash, long Modified, bool? Executable = null);

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

    /// <summary>A file of the other machine's watched, <see cref="FileChanged"/> said when it moves; its hash now.</summary>
    public async Task<string?> Watch(string path, CancellationToken cancel = default) =>
        HexLinkData.String(await AskObject("fs/watch", new JsonObject { ["path"] = path }, cancel), "hash", 64);

    /// <summary>A file no longer watched.</summary>
    public Task Unwatch(string path, CancellationToken cancel = default) => session.Ask("fs/unwatch", new JsonObject { ["path"] = path }, cancel);

    // A bundle coming over, into its temp file a chunk at a time, in order
    private sealed class Receiving(string file, Action<long, long>? progress)
    {
        private readonly FileStream stream = new(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        public string File { get; } = file;
        public long Written { get; private set; }
        public long Expected { get; set; } = -1;
        public TaskCompletionSource<string?> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Write(long at, byte[] data)
        {
            if (Done.Task.IsCompleted)
                return;
            if (at != Written || (Expected >= 0 && Written + data.Length > Expected) || Written + data.Length > MOST_BUNDLE)
            {
                Fail("its chunks didn't come in order or came to more than it said");
                return;
            }

            stream.Write(data);
            Written += data.Length;
            progress?.Invoke(Written, Expected);
        }

        public void Finish(long size)
        {
            if (Done.Task.IsCompleted)
                return;
            stream.Dispose();
            if (size != Written)
            {
                Fail($"it came to {Written} bytes where it should have been {size}");
                return;
            }

            Done.TrySetResult(File);
        }

        public void Fail(string why)
        {
            try { stream.Dispose(); } catch (Exception) { }
            HexLinkGit.TryDelete(File);
            Done.TrySetException(new HexLinkException(HexLinkErrors.INTERNAL_ERROR, $"The bundle didn't come over, {why}."));
        }
    }

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
        var receiving = new Receiving(Path.Combine(Path.GetTempPath(), $"hexlink-{id}.bundle"), progress);
        bundles[id] = receiving;
        try
        {
            JsonObject said = await AskObject("git/bundle", new JsonObject
            {
                ["repository"] = repository,
                ["id"] = id,
                ["have"] = new JsonArray([.. have.Take(HexLinkGit.MOST_HAVE).Select(sha => (JsonNode)sha)])
            }, cancel, TimeSpan.FromMinutes(15));
            long size = HexLinkData.Long(said, "size") ?? -1;
            if (size < 0 || size > MOST_BUNDLE)
                throw new HexLinkException(HexLinkErrors.TOO_BIG, "The bundle is bigger than a link takes.");
            receiving.Expected = size;

            string? file = await receiving.Done.Task.WaitAsync(TimeSpan.FromHours(2), cancel);
            if (size == 0)
            {
                HexLinkGit.TryDelete(receiving.File);
                return null;
            }

            if (await Task.Run(() => HexLinkGit.Verify(file!, repo), cancel) is { } bad)
            {
                HexLinkGit.TryDelete(receiving.File);
                throw new HexLinkException(HexLinkErrors.INTERNAL_ERROR, $"The bundle didn't verify, {bad}");
            }

            return file;
        }
        catch (Exception)
        {
            receiving.Fail("it was given up on");
            throw;
        }
        finally
        {
            bundles.TryRemove(id, out _);
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

        try
        {
            receiving.Write(at, Convert.FromBase64String(data));
        }
        catch (Exception e) when (e is FormatException or IOException)
        {
            receiving.Fail(e.Message);
        }
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
