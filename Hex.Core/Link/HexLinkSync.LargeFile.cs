using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>One immutable file version carried over a bulk stream, with a checked checkpoint for reconnects.</summary>
internal sealed record HexLinkFileTransfer(string Path, long Length, string Hash, string? Base = null, bool? Executable = null)
{
    public const long MaximumLength = 4L * 1024 * 1024 * 1024;
    private const long MaximumStaging = 8L * 1024 * 1024 * 1024;
    private static readonly Lock staging = new();
    private static readonly Dictionary<string, long> active = new(PlatformPaths.Comparer);

    public JsonObject Json() => new()
    {
        ["path"] = Path,
        ["length"] = Length,
        ["hash"] = Hash,
        ["base"] = Base,
        ["executable"] = Executable
    };

    public static HexLinkFileTransfer Of(JsonObject said)
    {
        if (HexLinkData.String(said, "path", HexLinkFiles.MOST_PATH + 1) is not { Length: > 0 and <= HexLinkFiles.MOST_PATH } path ||
            HexLinkData.Long(said, "length") is not (>= 0 and <= MaximumLength) ||
            HexLinkData.String(said, "hash", 65) is not { Length: 64 } hash || !hash.All(char.IsAsciiHexDigit))
            throw HexLinkException.Invalid("Invalid Link file transfer.");
        string? expected = HexLinkData.String(said, "base", 65);
        if (said["base"] is not null && (expected is not { Length: 64 } || !expected.All(char.IsAsciiHexDigit)))
            throw HexLinkException.Invalid("Invalid Link file transfer base hash.");
        return new HexLinkFileTransfer(path, HexLinkData.Long(said, "length")!.Value, hash.ToLowerInvariant(), expected,
            HexLinkData.Bool(said, "executable"));
    }

    public readonly record struct Checkpoint(long Offset, string? Prefix)
    {
        public void PutIn(JsonObject said)
        {
            said["offset"] = Offset;
            said["prefix"] = Prefix;
        }

        public static Checkpoint Of(JsonObject said, long length)
        {
            long offset = HexLinkData.Long(said, "offset") ?? -1;
            string? prefix = HexLinkData.String(said, "prefix", 65);
            if (offset < 0 || offset > length ||
                (offset != 0 && (prefix is not { Length: 64 } || !prefix.All(char.IsAsciiHexDigit))))
                throw HexLinkException.Invalid("Invalid Link file checkpoint.");
            return new Checkpoint(offset, offset == 0 ? null : prefix!.ToLowerInvariant());
        }
    }

    public string PartialPath(string folder, string owner)
    {
        string key = HexLinkFiles.Hash(Encoding.UTF8.GetBytes($"{owner}\n{Path}\n{Length}\n{Hash}"));
        return System.IO.Path.Combine(folder, key + ".hexpart");
    }

    public async Task<Checkpoint> Inspect(string partial, CancellationToken cancel)
    {
        try
        {
            await using FileStream file = Read(partial);
            long length = file.Length;
            if (length <= 0 || length > Length)
                return default;

            string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancel));
            return file.Length == length ? new Checkpoint(length, hash) : default;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return default;
        }
    }

    public static FileStream Read(string full) =>
        new(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);

    public async Task<JsonNode?> Send(HexLinkSession session, string purpose, JsonObject metadata, FileStream file,
        Checkpoint checkpoint, CancellationToken cancel)
    {
        if (file.Length != Length)
            throw new HexLinkException(HexLinkErrors.CONFLICT, $"The streamed file {Path} changed size.");
        byte[] buffer = ArrayPool<byte>.Shared.Rent(HexLinkFrameHeader.MaximumPayload);
        try
        {
            if (checkpoint.Offset > 0)
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long remaining = checkpoint.Offset;
                while (remaining > 0)
                {
                    int read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(remaining, buffer.Length)), cancel);
                    if (read == 0)
                        throw new HexLinkException(HexLinkErrors.CONFLICT, $"The streamed file {Path} changed size.");
                    hash.AppendData(buffer, 0, read);
                    remaining -= read;
                }
                if (!string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), checkpoint.Prefix, StringComparison.Ordinal))
                {
                    file.Position = 0;
                    checkpoint = default;
                }
            }

            checkpoint.PutIn(metadata);
            await using HexLinkSession.BulkUpload upload = await session.OpenBulkAsync(purpose, Length - checkpoint.Offset, metadata, cancel);
            long left = Length - checkpoint.Offset;
            while (left > 0)
            {
                int need = (int)Math.Min(left, HexLinkFrameHeader.MaximumPayload);
                int read = await file.ReadAtLeastAsync(buffer.AsMemory(0, need), need, throwOnEndOfStream: false, cancel);
                if (read != need)
                    throw new HexLinkException(HexLinkErrors.CONFLICT, $"The streamed file {Path} changed size.");
                await upload.WriteAsync(buffer.AsMemory(0, read), cancel);
                left -= read;
            }
            if (file.Length != Length)
                throw new HexLinkException(HexLinkErrors.CONFLICT, $"The streamed file {Path} changed size.");
            return await upload.CompleteAsync(cancel);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public Sink Receive(string partial, long remaining, Checkpoint checkpoint,
        Func<string, CancellationToken, Task<JsonNode?>> commit, bool discardOnSuccess)
    {
        if (checkpoint.Offset > Length || remaining != Length - checkpoint.Offset)
            throw HexLinkException.Invalid("The Link file stream length does not match its checkpoint.");
        string directory = System.IO.Path.GetDirectoryName(partial)!;
        Directory.CreateDirectory(directory);
        lock (staging)
        {
            if (active.ContainsKey(partial))
                throw new HexLinkException(HexLinkErrors.BUSY, "This Link file already has an active transfer.");

            long reserved = 0;
            int count = 0;
            foreach (var (name, size) in active)
            {
                if (PlatformPaths.Comparer.Equals(System.IO.Path.GetDirectoryName(name), directory))
                {
                    reserved += size;
                    count++;
                }
            }

            foreach (string existing in Directory.EnumerateFiles(directory, "*.hexpart"))
            {
                if (PlatformPaths.Comparer.Equals(existing, partial) || active.ContainsKey(existing))
                    continue;
                var info = new FileInfo(existing);
                if (info.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-2))
                    HexLinkGit.TryDelete(existing);
                else
                {
                    reserved += info.Length;
                    count++;
                }
            }

            if (reserved > MaximumStaging - Length || count >= 128)
                throw new HexLinkException(HexLinkErrors.TOO_BIG, "Link's incomplete file transfers exceed the staging limit.");
            active.Add(partial, Length);
        }
        try
        {
            return new Sink(this, partial, checkpoint, commit, discardOnSuccess);
        }
        catch
        {
            Release(partial);
            throw;
        }
    }

    private static void Release(string partial)
    {
        lock (staging)
            active.Remove(partial);
    }

    public sealed class Sink : IHexLinkBulkSink
    {
        private readonly HexLinkFileTransfer transfer;
        private readonly string partial;
        private readonly Func<string, CancellationToken, Task<JsonNode?>> commit;
        private readonly bool discardOnSuccess;
        private FileStream? file;
        private bool discard;

        internal Sink(HexLinkFileTransfer transfer, string partial, Checkpoint checkpoint,
            Func<string, CancellationToken, Task<JsonNode?>> commit, bool discardOnSuccess)
        {
            this.transfer = transfer;
            this.partial = partial;
            this.commit = commit;
            this.discardOnSuccess = discardOnSuccess;
            var open = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 1, FileOptions.Asynchronous);
            try
            {
                if (checkpoint.Offset == 0)
                    open.SetLength(0);
                else if (open.Length != checkpoint.Offset)
                    throw new HexLinkException(HexLinkErrors.CONFLICT, "The Link file checkpoint changed.");
                open.Position = checkpoint.Offset;
                file = open;
            }
            catch
            {
                open.Dispose();
                throw;
            }
        }

        public JsonNode? Result { get; private set; }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancel) => file!.WriteAsync(data, cancel);

        public async ValueTask CommitAsync(CancellationToken cancel)
        {
            if (file!.Length != transfer.Length)
                throw HexLinkException.Invalid("The staged Link file has the wrong length.");
            await file.DisposeAsync();
            file = null;
            try
            {
                Result = await commit(partial, cancel);
                discard = discardOnSuccess;
            }
            catch (HexLinkException e) when (e.Code == HexLinkErrors.INVALID_PARAMS)
            {
                discard = true;
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (file is not null)
                    await file.DisposeAsync();
                if (discard)
                    HexLinkGit.TryDelete(partial);
            }
            finally
            {
                Release(partial);
            }
        }
    }
}
