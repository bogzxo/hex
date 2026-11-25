using System.Collections.Concurrent;

namespace Hex.Core;

// Most tip checks read only HEAD and its loose ref. Packed refs and alternate ref backends use Git to resolve the tip.
internal sealed class HexLinkGitFollowScan(string root)
{
    private static readonly ConcurrentDictionary<string, HexLinkGitFollowScan> scans = new(PlatformPaths.Comparer);
    private readonly Lock gate = new();
    private (string GitDirectory, string CommonDirectory)? locations;
    private HexLinkHeadTip? cached;
    private Stamp? previous;
    private sealed record Stamp(string Head, string? Loose, DateTime Packed, bool Reftable);

    internal static HexLinkGitFollowScan For(string root) =>
        scans.TryGetValue(root, out var found) ? found : scans.Count < 4096
            ? scans.GetOrAdd(root, static path => new(path)) : new(root);

    internal HexLinkHeadTip? Read(CancellationToken cancel)
    {
        lock (gate)
        {
            cancel.ThrowIfCancellationRequested();
            var (gitDirectory, common) = locations ??= HexLinkGitFollow.LocateCheckout(root, cancel);
            Stamp? stamp = Capture(gitDirectory, common);
            if (stamp is null)
                return null;
            if (stamp != previous || cached is null || stamp.Reftable)
            {
                const string branchPrefix = "ref: refs/heads/";
                if (HexLinkGit.IsCommitId(stamp.Head))
                    cached = new(stamp.Head, null);
                else if (stamp.Head.StartsWith(branchPrefix, StringComparison.Ordinal)
                    && stamp.Loose is { } loose && HexLinkGit.IsCommitId(loose))
                    cached = new(loose, stamp.Head[branchPrefix.Length..]);
                else
                    cached = HexLinkGitFollow.Read(root, cancel);
                if (Capture(gitDirectory, common) != stamp)
                {
                    cached = null;
                    return null;
                }
                previous = stamp;
            }
            return cached;
        }
    }

    /// <summary>The checkout's git directory and the common one a worktree shares (the same when it has none), once a read has located them.</summary>
    internal string[] GitDirectories()
    {
        lock (gate)
            return locations is { } found
                ? found.GitDirectory.Equals(found.CommonDirectory, PlatformPaths.Comparison) ? [found.GitDirectory] : [found.GitDirectory, found.CommonDirectory]
                : [];
    }

    internal async Task<HexLinkHeadTip?> ReadStable(CancellationToken cancel)
    {
        if (Read(cancel) is { } tip)
            return tip;
        string directory;
        lock (gate)
            directory = locations!.Value.GitDirectory;
        return await HexLinkGitFollow.RecoverPending(root, directory, cancel) ? Read(cancel) : null;
    }

    private static Stamp? Capture(string gitDirectory, string common)
    {
        if (File.Exists(Path.Combine(gitDirectory, "index.lock")) || File.Exists(Path.Combine(gitDirectory, HexLinkGitFollow.JOURNAL)))
            return null;

        string head = SmallRef(Path.Combine(gitDirectory, "HEAD")) ?? throw new IOException("Git's HEAD is unavailable.");
        string? loose = null;
        if (head.StartsWith("ref: refs/heads/", StringComparison.Ordinal))
        {
            string candidate = Path.GetFullPath(Path.Combine(common, head[5..]));
            if (PlatformPaths.IsUnder(candidate, common))
                loose = SmallRef(candidate);
        }

        return File.Exists(Path.Combine(gitDirectory, "index.lock")) ? null
            : new(head, loose, File.GetLastWriteTimeUtc(Path.Combine(common, "packed-refs")),
                Directory.Exists(Path.Combine(common, "reftable")));
    }

    private static string? SmallRef(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > 4096)
                throw new IOException("A Git ref is too long.");
            using var text = new StreamReader(file);
            return text.ReadToEnd().Trim();
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
