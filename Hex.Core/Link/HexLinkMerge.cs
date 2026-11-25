using System.Text;
using Hex.Core.Git;
using Hex.Core.Merge;

namespace Hex.Core;

internal sealed record HexLinkMergeResult(byte[] Bytes, bool Clean);

/// <summary>Three-way merge of checked text bytes. Conflict markers exist only in the returned draft.</summary>
internal static class HexLinkMerge
{
    internal const int CACHE_FILE_BYTES = 256 * 1024;
    private const int MAX_MERGE_BYTES = 1024 * 1024, CACHE_BYTES = 8 * 1024 * 1024; // the disk store (HexLinkMergeBases) is the fallback past it
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
            || HexLinkFiles.Decode(remote) is not { } theirs)
            return null;

        // Git's merge-file is the first choice, but a machine without git, or a git that fails, still has to merge region
        // by region, so the same merge is then worked out in Hex.
        return (HexLinkGit.Why() is null ? ViaGit(basis, ours, theirs, cancel) : null)
            ?? InProcess(basis.Text, ours.Text, theirs.Text, ours.Encoding);
    }

    /// <summary>The merge worked out in Hex, with the markers and labels git's merge-file would have given.</summary>
    internal static HexLinkMergeResult? InProcess(string basis, string mine, string theirs, string encoding)
    {
        IReadOnlyList<MergeRegion> regions = MergeThreeWay.Merge(MergeModel.Lines(basis), MergeModel.Lines(mine),
            MergeModel.Lines(theirs), "Current", "Base", "Incoming");
        string text = MergeThreeWay.Marked(regions);
        if (text.Length > 0 && EndsWithNewline(mine))
            text += "\n";
        byte[]? bytes = EncodeLike(text, mine, encoding);
        return bytes is { Length: <= MAX_MERGE_BYTES } ? new HexLinkMergeResult(bytes, !regions.Any(region => region is MergeRegion.Conflict)) : null;
    }

    private static bool EndsWithNewline(string text) => text.EndsWith('\n') || text.EndsWith('\r');

    private static HexLinkMergeResult? ViaGit((string Text, string Encoding) basis, (string Text, string Encoding) ours,
        (string Text, string Encoding) theirs, CancellationToken cancel)
    {
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
        // Any text that decodes keeps its own encoding and line ends, one with mixed line ends too: it was refused here
        // for having no single kind, which made a 35-byte file "too large or its encoding changed"
        if (HexLinkFiles.Decode(original) is not { } decoded)
            return null;
        return EncodeLike(text, decoded.Text, decoded.Encoding);
    }

    /// <summary>
    /// A merged text written for one side with every line that came unchanged from either copy keeping the line end it had
    /// there, byte for byte (each repeated line takes the ends of its earlier twins in turn), and the side's dominant line end
    /// only on lines that are new or edited. The side's own copy is looked in first, then the other's.
    /// </summary>
    internal static byte[]? EncodeKeeping(string text, byte[] own, byte[] other)
    {
        if (HexLinkFiles.Decode(own) is not { } decoded)
            return null;
        // A side whose lines all end the same way keeps that style for everything it receives, a line that came from the
        // other copy included (an LF file's resolved line must not land in a CRLF file as LF). Only a side with mixed line
        // ends takes the end a line had in the other copy.
        string dominant = DominantEol(decoded.Text);
        Dictionary<string, Queue<string>> mine = EndsOf(decoded.Text),
            theirs = !IsUniform(decoded.Text) && HexLinkFiles.Decode(other) is { } far ? EndsOf(far.Text) : [];
        var made = new System.Text.StringBuilder(text.Length + 16);
        foreach ((string content, string end) in Split(text))
        {
            made.Append(content);
            if (end.Length == 0)
                continue;
            made.Append(Take(mine, content) ?? Take(theirs, content) ?? dominant);
        }

        return HexLinkFiles.Encode(made.ToString(), decoded.Encoding);

        static string? Take(Dictionary<string, Queue<string>> ends, string content)
        {
            if (!ends.TryGetValue(content, out Queue<string>? queue) || queue.Count == 0)
                return null;
            // The last of a repeated line's ends stays for any more of it
            return queue.Count > 1 ? queue.Dequeue() : queue.Peek();
        }
    }

    /// <summary>Whether every line end of a text is of one kind (a text with none is).</summary>
    internal static bool IsUniform(string text)
    {
        string? kind = null;
        foreach ((_, string end) in Split(text))
        {
            if (end.Length == 0)
                continue;
            if (kind is not null && kind != end)
                return false;
            kind = end;
        }

        return true;
    }

    // A text's lines as their content and the line end that followed them, empty for the last line when the text ends without one
    private static List<(string Content, string End)> Split(string text)
    {
        var lines = new List<(string, string)>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\r' && text[i] != '\n')
                continue;
            int length = text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
            lines.Add((text[start..i], text.Substring(i, length)));
            i += length - 1;
            start = i + 1;
        }

        if (start < text.Length)
            lines.Add((text[start..], string.Empty));
        return lines;
    }

    private static Dictionary<string, Queue<string>> EndsOf(string text)
    {
        var ends = new Dictionary<string, Queue<string>>(StringComparer.Ordinal);
        foreach ((string content, string end) in Split(text))
        {
            if (end.Length == 0)
                continue;
            if (!ends.TryGetValue(content, out Queue<string>? queue))
                ends[content] = queue = new Queue<string>();
            queue.Enqueue(end);
        }

        return ends;
    }

    private static byte[]? EncodeLike(string text, string original, string encoding)
    {
        string eol = DominantEol(original);
        return HexLinkFiles.Encode(text.ReplaceLineEndings(eol), encoding);
    }

    /// <summary>The line end most of a text's lines have, the first one's when they are level, LF for none.</summary>
    internal static string DominantEol(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        string first = string.Empty;
        for (int i = 0; i < text.Length; i++)
        {
            string kind;
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                crlf++;
                i++;
                kind = "\r\n";
            }
            else if (text[i] == '\n')
            {
                lf++;
                kind = "\n";
            }
            else if (text[i] == '\r')
            {
                cr++;
                kind = "\r";
            }
            else
                continue;
            if (first.Length == 0)
                first = kind;
        }

        int most = Math.Max(crlf, Math.Max(lf, cr));
        if (most == 0)
            return "\n";
        string[] kinds = ["\r\n", "\n", "\r"];
        int[] counts = [crlf, lf, cr];
        for (int i = 0; i < 3; i++)
        {
            if (counts[i] == most && (kinds[i] == first || counts.Count(each => each == most) == 1))
                return kinds[i];
        }

        return first;
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
