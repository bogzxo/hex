using Hex.Core.Git;

namespace Hex.Core;

internal sealed partial class HexLinkTree
{
    private sealed record DiskGitLayout(string[] Repositories, ILookup<string, string>? Candidates);

    private long reusedGitListings;
    internal long ReusedGitListings => Interlocked.Read(ref reusedGitListings);

    private DiskGitLayout DiscoverGitLayout(Dictionary<string, HexLinkGitIssue> issues, bool allowIndexReuse, Action<string>? omittedSystemFile)
    {
        List<string>? inventory = allowIndexReuse && !trackedIndexes.IsEmpty && PreviousManifest is not null ? [] : null;
        string[] repositories = [.. FindNestedRepositories(Root, unavailable: (path, why) =>
            issues[path] = new HexLinkGitIssue(OneLine(why), Unavailable: true), inventory: inventory, includeSystemFiles: Folder.IncludeSystemFiles,
                omittedSystemFile: omittedSystemFile)
            .Where(path => !Hidden(path, folder: true)).OrderByDescending(path => path.Length)];
        ILookup<string, string>? candidates = inventory is { Count: <= MOST_FILES } && issues.Count == 0
            ? inventory.ToLookup(path => RepositoryContaining(path, repositories) ?? string.Empty, StringComparer.Ordinal)
            : null;
        return new DiskGitLayout(repositories, candidates);
    }

    // Only a fully tracked scope can reuse names without re-evaluating Git ignore rules. The disk inventory exposes
    // any untracked candidate even if it appeared while Hex was closed, or the directory timestamp was restored.
    private GitResult ListGitWorkingFiles(string directory, string scope, DiskGitLayout layout, TimeSpan patience,
        out GitResult? headAlreadyRead)
    {
        headAlreadyRead = null;
        HexLinkManifest? prior = PreviousManifest;
        string? previousHead = scope.Length == 0 ? prior?.Head : prior?.NestedHeads?.GetValueOrDefault(scope);
        if (previousHead is not null && prior?.GitIssues?.ContainsKey(scope) != true &&
            layout.Candidates is { } candidates && TrackedNow(directory, cachedOnly: true) is { } tracked)
        {
            int prefix = scope.Length == 0 ? 0 : scope.Length + 1;
            if (candidates[scope].All(path => path.Length > prefix && tracked.Contains(path[prefix..])))
            {
                GitResult head = GitProcess.Run(directory, ["rev-parse", "--verify", "--quiet", "HEAD"], patience: patience);
                headAlreadyRead = head;
                if (head.Ok && head.Output.Trim() == previousHead)
                {
                    using var timing = HexLinkSyncTiming.Measure("tree.list-exact-index", tracked.Count);
                    Interlocked.Increment(ref reusedGitListings);
                    return new GitResult(0, string.Join('\0', tracked), string.Empty);
                }
            }
        }

        using var listing = HexLinkSyncTiming.Measure(scope.Length == 0 ? "tree.list-root-git" : "tree.list-nested-git");
        return GitProcess.Run(directory, ["ls-files", "-co", "--exclude-standard", "-z"], patience: patience);
    }
}
