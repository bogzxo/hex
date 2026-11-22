using System.Text;
using Hex.Core.Git;

namespace Hex.Core;

internal sealed record HexLinkMergeResult(byte[] Bytes, bool Clean);

/// <summary>Three-way merge of checked text bytes. Conflict markers exist only in the returned draft.</summary>
internal static class HexLinkMerge
{
    internal const int CACHE_FILE_BYTES = 256 * 1024;
    private const int MAX_MERGE_BYTES = 1024 * 1024, CACHE_BYTES = 32 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Lock cacheGate = new();
    private static readonly LinkedList<(string Hash, byte[] Bytes)> recent = [];
    private static readonly Dictionary<string, LinkedListNode<(string Hash, byte[] Bytes)>> cached = new(StringComparer.Ordinal);
    private static int heldBytes;

    public static Task<HexLinkMergeResult?> TryTextAsync(byte[] @base, byte[] local, byte[] remote, CancellationToken cancel = default) =>
        Task.Run(() => TryText(@base, local, remote, cancel), cancel);

    private static HexLinkMergeResult? TryText(byte[] @base, byte[] local, byte[] remote, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (@base.Length > MAX_MERGE_BYTES || local.Length > MAX_MERGE_BYTES || remote.Length > MAX_MERGE_BYTES
            || HexLinkFiles.Decode(@base) is not { } basis || HexLinkFiles.Decode(local) is not { } ours
            || HexLinkFiles.Decode(remote) is not { } theirs || HexLinkSync.TextAndEol(local).Text is null
            || HexLinkGit.Why() is not null)
            return null;

        string temp = Path.GetTempPath();
        string key = Path.Combine(temp, $"hexlink-merge-{Guid.NewGuid():N}");
        string mineFile = key + "-current", baseFile = key + "-base", theirFile = key + "-incoming";
        try
        {
            File.WriteAllText(mineFile, ours.Text.ReplaceLineEndings("\n"), Utf8);
            File.WriteAllText(baseFile, basis.Text.ReplaceLineEndings("\n"), Utf8);
            File.WriteAllText(theirFile, theirs.Text.ReplaceLineEndings("\n"), Utf8);
            GitResult git = GitProcess.Run(temp, ["merge-file", "-p", "--diff3", "-L", "Current", "-L", "Base", "-L", "Incoming",
                "--", mineFile, baseFile, theirFile], patience: TimeSpan.FromSeconds(15), unattended: true, cancel: cancel);
            if (git.Exit is < 0 or > 127 || git.Exit > 0 && !git.Output.Contains("<<<<<<< Current", StringComparison.Ordinal))
                return null;
            byte[]? bytes = EncodeLike(git.Output, ours.Text, ours.Encoding);
            return bytes is { Length: <= MAX_MERGE_BYTES } ? new HexLinkMergeResult(bytes, git.Ok) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or EncoderFallbackException)
        {
            return null;
        }
        finally
        {
            HexLinkGit.TryDelete(mineFile);
            HexLinkGit.TryDelete(baseFile);
            HexLinkGit.TryDelete(theirFile);
        }
    }

    internal static byte[]? EncodeLike(string text, byte[] original)
    {
        if (HexLinkFiles.Decode(original) is not { } decoded || HexLinkSync.TextAndEol(original).Text is null)
            return null;
        return EncodeLike(text, decoded.Text, decoded.Encoding);
    }

    private static byte[]? EncodeLike(string text, string original, string encoding)
    {
        string eol = HexLinkFiles.Eol(original) switch { "crlf" => "\r\n", "cr" => "\r", _ => "\n" };
        return HexLinkFiles.Encode(text.ReplaceLineEndings(eol), encoding);
    }

    internal static void RememberBase(string hash, byte[] bytes)
    {
        if (bytes.Length > CACHE_FILE_BYTES)
            return;
        lock (cacheGate)
        {
            if (cached.TryGetValue(hash, out var used))
            {
                recent.Remove(used);
                recent.AddFirst(used);
                return;
            }
            cached.Add(hash, recent.AddFirst((hash, bytes)));
            heldBytes += bytes.Length + 128;
            while (heldBytes > CACHE_BYTES && recent.Last is { } last)
            {
                recent.RemoveLast();
                cached.Remove(last.Value.Hash);
                heldBytes -= last.Value.Bytes.Length + 128;
            }
        }
    }

    internal static byte[]? CachedBase(string hash)
    {
        lock (cacheGate)
        {
            if (!cached.TryGetValue(hash, out var found))
                return null;
            recent.Remove(found);
            recent.AddFirst(found);
            return found.Value.Bytes;
        }
    }
}
