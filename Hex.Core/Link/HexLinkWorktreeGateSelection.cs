namespace Hex.Core;

internal static class HexLinkWorktreeGateSelection
{
    public static IReadOnlyList<HexLinkWorktreeGateOptions> Defaults(HexLinkFolder folder, IReadOnlyList<string> repositories,
        IReadOnlyList<string> paths, Func<string, HexLinkWorktreeGateOptions?> taskAt, string? projectRoot = null)
    {
        string root = Path.GetFullPath(folder.Path);
        string[] repoRoots = [.. repositories.Select(repo => Path.GetFullPath(Path.Combine(root, repo))).OrderByDescending(repo => repo.Length)];
        var result = new List<HexLinkWorktreeGateOptions>();
        var taskCache = new Dictionary<string, HexLinkWorktreeGateOptions?>(PlatformPaths.Comparer);
        var projectCache = new Dictionary<string, string?>(PlatformPaths.Comparer);
        foreach (string wire in paths.Order(StringComparer.Ordinal))
        {
            string prefix = folder.Name + "/";
            if (!wire.StartsWith(prefix, StringComparison.Ordinal))
                throw HexLinkException.Invalid("A worktree gate path is outside its shared folder.");
            string full = HexLinkFiles.Resolve([folder], wire);
            string repo = repoRoots.FirstOrDefault(path => PlatformPaths.IsUnder(full, path)) ?? root;
            string[] ancestors = [.. Ancestors(Path.GetDirectoryName(full)!, repo)
                .Where(path => repoRoots.Length <= 1 || !PlatformPaths.Same(path, root))];
            HexLinkWorktreeGateOptions? task = null;
            foreach (string directory in ancestors)
            {
                if (!taskCache.TryGetValue(directory, out task))
                    taskCache[directory] = task = taskAt(directory);
                if (task is not null)
                    break;
            }
            if (task is not null)
            {
                if (!result.Contains(task))
                    result.Add(task);
                continue;
            }

            string? project = null;
            foreach (string directory in ancestors)
            {
                if (!projectCache.TryGetValue(directory, out project))
                    projectCache[directory] = project = ProjectIn(projectRoot is null
                        ? directory : Path.Combine(projectRoot, Path.GetRelativePath(root, directory)));
                if (project is not null)
                {
                    var options = new HexLinkWorktreeGateOptions(WorkingFolder: Relative(root, directory), Target: project);
                    if (!result.Contains(options))
                        result.Add(options);
                    break;
                }
            }
            if (project is null)
                throw new IOException($"No scoped build task or .NET project covers {wire}. Ask the folder authority to set a publish gate.");
        }
        return result;
    }

    internal static string? Relative(string root, string directory)
    {
        string relative = Path.GetRelativePath(root, directory).Replace('\\', '/');
        return relative == "." ? null : relative;
    }

    private static IEnumerable<string> Ancestors(string directory, string root)
    {
        for (string? path = directory; path is not null && PlatformPaths.IsUnder(path, root); path = Path.GetDirectoryName(path))
            yield return path;
    }

    private static string? ProjectIn(string directory)
    {
        if (!Directory.Exists(directory))
            return null;
        string[] files = [.. Directory.EnumerateFiles(directory).Select(Path.GetFileName).OfType<string>()];
        string[] solutions = [.. files.Where(path => Path.GetExtension(path) is ".sln" or ".slnx")];
        string[] candidates = solutions.Length > 0 ? solutions : [.. files.Where(path => Path.GetExtension(path) == ".csproj")];
        if (candidates.Length <= 1)
            return candidates.SingleOrDefault();
        string name = Path.GetFileName(directory);
        return candidates.FirstOrDefault(candidate => PlatformPaths.Comparer.Equals(Path.GetFileNameWithoutExtension(candidate), name))
            ?? throw new IOException($"There are multiple build targets in {directory}. Ask the folder authority to set a publish gate.");
    }
}
