namespace Hex.Core;

// A repository known to only one side is neither a new directory nor a deletion.
internal sealed record HexLinkMissingRepositories(IReadOnlyList<string> Here, IReadOnlyList<string> There, IReadOnlyList<string>? Unavailable = null)
{
    internal static readonly HexLinkMissingRepositories None = new([], []);

    internal static HexLinkMissingRepositories Compare(HexLinkManifest here, HexLinkManifest there)
    {
        var mine = here.NestedHeads?.Keys ?? [];
        var theirs = there.NestedHeads?.Keys ?? [];
        string[] unavailable = [.. UnavailableIn(here).Concat(UnavailableIn(there)).Distinct(StringComparer.OrdinalIgnoreCase)];
        return new(Missing(theirs, mine), Missing(mine, theirs), unavailable);

        static IEnumerable<string> UnavailableIn(HexLinkManifest manifest) =>
            manifest.GitIssues is { } issues ? issues.Where(each => each.Value.Unavailable).Select(each => each.Key) : [];

        string[] Missing(IEnumerable<string> host, IEnumerable<string> guest) =>
            [.. host.Except(guest, StringComparer.Ordinal).Where(path => !unavailable.Any(repo => Under(path, repo))).Order(StringComparer.Ordinal)];
    }

    internal bool Blocks(string path) => Matches(path, ancestors: true);

    internal bool Covers(string path) => Matches(path, ancestors: false);

    private bool Matches(string path, bool ancestors)
    {
        return Matches(Here) || Matches(There) || Unavailable is { } unavailable && Matches(unavailable);

        bool Matches(IReadOnlyList<string> repositories)
        {
            foreach (string repository in repositories)
            {
                if (Under(path, repository) || ancestors && Under(repository, path))
                    return true;
            }
            return false;
        }
    }

    internal static bool Under(string path, string repository) =>
        path.Equals(repository, StringComparison.OrdinalIgnoreCase)
        || path.Length > repository.Length && path[repository.Length] == '/' && path.StartsWith(repository, StringComparison.OrdinalIgnoreCase);

}

internal sealed partial class HexLinkSync
{
    private volatile HexLinkMissingRepositories missingRepositories = HexLinkMissingRepositories.None;

    internal HexLinkMissingRepositories MissingRepositories => missingRepositories;

    private Task stoppedWatching = Task.CompletedTask;

    internal Task StopForRepositoryRefresh()
    {
        Stop();
        return stoppedWatching;
    }

    private bool BlockedRepository(string wire)
    {
        HexLinkMissingRepositories missing = missingRepositories;
        if (missing is { Here.Count: 0, There.Count: 0, Unavailable: null or { Count: 0 } })
            return false;
        int slash = wire.IndexOf('/');
        return slash >= 0 && missing.Blocks(wire[(slash + 1)..]);
    }
}
