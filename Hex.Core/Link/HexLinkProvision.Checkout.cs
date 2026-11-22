using Hex.Core.Git;

namespace Hex.Core;

internal static partial class HexLinkProvision
{
    private sealed record TrackedPath(string Record, string Path, bool Gitlink);

    // A bad tracked name or symlink must not make an otherwise usable checkout fail.
    private static bool CheckoutSafeSubset(Checkout checkout, List<HexLinkProvisionSkip> skipped, CancellationToken cancel)
    {
        string repo = checkout.Directory, head = checkout.Remote.Head;
        var entries = new List<TrackedPath>();
        var spellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var collisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool partial = false;
        foreach (string record in Run(repo, ["ls-tree", "-rz", head], cancel).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int tab = record.IndexOf('\t');
            if (tab < 0)
                throw new IOException("Git sent an invalid checkout index entry.");
            string path = record[(tab + 1)..];
            try
            {
                if (record.StartsWith("120000 ", StringComparison.Ordinal))
                    throw new IOException("Symbolic links are left in the source checkout.");
                if (path.Length is 0 or > HexLinkFiles.MOST_PATH || path.Contains(':') || path.Contains('\\') || path.Any(char.IsControl))
                    throw new IOException("The file name cannot be used by Link.");
                foreach (string part in path.Split('/'))
                    HexLinkFiles.Check(path, part);
            }
            catch (Exception error) when (error is HexLinkException or IOException)
            {
                Note(path, error.Message);
                continue;
            }
            for (int at = path.IndexOf('/'); ; at = path.IndexOf('/', at + 1))
            {
                string name = at < 0 ? path : path[..at];
                if (spellings.TryGetValue(name, out string? previous) && previous != name)
                    collisions.Add(name);
                else
                    spellings.TryAdd(name, name);
                if (at < 0)
                    break;
            }
            entries.Add(new(record, path, record.StartsWith("160000 ", StringComparison.Ordinal)));
        }

        entries.RemoveAll(entry =>
        {
            if (!collisions.Any(prefix => HexLinkMissingRepositories.Under(entry.Path, prefix)))
                return false;
            Note(entry.Path, "Paths collide on a case-insensitive checkout.");
            return true;
        });
        if (!partial)
            return false;

        // Keep the full index when Git can represent it. Otherwise add just the safe entries, including valid gitlinks.
        GitResult index = GitProcess.Run(repo, ["read-tree", head], cancel: cancel);
        if (!index.Ok)
        {
            if (GitDiskFull(index))
                throw new IOException(index.Why, 112);
            Run(repo, ["read-tree", "--empty"], cancel);
            Run(repo, ["update-index", "-z", "--index-info"], cancel, string.Concat(entries.Select(entry => entry.Record + "\0")));
        }
        GitResult checkoutFiles = GitProcess.Run(repo, ["checkout-index", "-z", "--stdin"],
            string.Concat(entries.Where(entry => !entry.Gitlink).Select(entry => entry.Path + "\0")), cancel: cancel);
        if (!checkoutFiles.Ok && GitDiskFull(checkoutFiles))
            throw new IOException(checkoutFiles.Why, 112);
        if (!checkoutFiles.Ok)
        {
            foreach (TrackedPath entry in entries.Where(entry => !entry.Gitlink && !File.Exists(Path.Combine(repo, entry.Path))))
                Note(entry.Path, "Git could not check out the file. " + checkoutFiles.Why);
        }
        if (checkout.Remote.Branch is { } branch)
        {
            Run(repo, ["update-ref", "refs/heads/" + branch, head], cancel);
            Run(repo, ["symbolic-ref", "HEAD", "refs/heads/" + branch], cancel);
        }
        else
            Run(repo, ["update-ref", "--no-deref", "HEAD", head], cancel);
        return true;

        void Note(string path, string why)
        {
            skipped.Add(new(Relative(checkout.Remote.Path, path), why, File: true));
            partial = true;
        }
    }

    private static bool GitDiskFull(GitResult error) =>
        error.Error.Contains("No space left on device", StringComparison.OrdinalIgnoreCase)
        || error.Error.Contains("not enough space on the disk", StringComparison.OrdinalIgnoreCase);
}
