namespace Hex.Core;

internal static class HexLinkWorktreeScope
{
    public static string[] Layout(IReadOnlyDictionary<string, string?>? nested) =>
        ["", .. (nested?.Keys ?? []).OrderBy(path => path.Count(ch => ch == '/')).ThenBy(path => path, StringComparer.Ordinal)];

    public static string Nearest(IEnumerable<string> repositories, string path)
    {
        string closest = "";
        foreach (string repo in repositories)
            if (repo.Length > closest.Length && path.Length >= repo.Length &&
                (path.Length == repo.Length || path[repo.Length] == '/') &&
                path.AsSpan(0, repo.Length).Equals(repo.AsSpan(), PlatformPaths.Comparison))
                closest = repo;
        return closest;
    }

    public static HashSet<string> Initial(string[] layout, string? gateDirectory, IReadOnlyList<string> extra)
    {
        var selected = new HashSet<string>(PlatformPaths.Comparer) { "" };
        if (gateDirectory is null)
            selected.UnionWith(layout);
        else
        {
            string gateRepo = Nearest(layout, gateDirectory);
            selected.Add(gateRepo);
            if (PlatformPaths.Comparer.Equals(gateRepo, "hex"))
                selected.UnionWith(layout.Where(path =>
                    PlatformPaths.Comparer.Equals(path, "horizon") || PlatformPaths.Comparer.Equals(path, "horizon/Horizon.HIDL")));
        }
        foreach (string repo in extra)
        {
            string requested = layout.FirstOrDefault(path => PlatformPaths.Comparer.Equals(path, repo))
                ?? throw new IOException($"No repository has the Link path '{repo}'.");
            selected.Add(requested);
        }
        foreach (string parent in layout)
            if (selected.Any(repo => repo.StartsWith(parent + "/", PlatformPaths.Comparison)))
                selected.Add(parent);
        return selected;
    }
}

internal sealed record HexLinkWorktreeProgress(string Repository, int Number, int Total, int Files, long Bytes)
{
    public string Describe() => $"preparing private worktree, repo {Number} of {Total} ({(Repository.Length == 0 ? "root" : Repository)}), {Files:N0} files, {Bytes:N0} bytes";
}
