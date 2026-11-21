using Hex.Core.Git;

namespace Hex.Core;

/// <summary>
/// The workspace as a link describes it to the devices that may see it (docs/hex-link-plan.md, 4, link/hello): its
/// name, its folders each by a name of its own, and the repositories they're in with the branch each is on and the
/// commit at its head. A wire path starts with a folder's name, so two folders of the same name (two checkouts called
/// hex) are told apart by a number after the second, and a repository that holds several of the folders is said once.
/// Nothing here is for the editor's thread, asking git waits.
/// </summary>
internal static class HexLinkWorkspaces
{
    /// <summary>
    /// The workspace of <paramref name="folders"/> described, named <paramref name="name"/> (the first folder's name
    /// when that's empty), each folder's repository asked of <paramref name="repositoryOf"/>, null where there's none.
    /// </summary>
    public static HexLinkWorkspace Describe(string name, IReadOnlyList<string> folders, Func<string, HexLinkRepository?> repositoryOf)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var described = new List<HexLinkFolder>();
        foreach (string folder in folders)
        {
            string whole = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (described.Any(each => PlatformPaths.Same(each.Path, whole)))
                continue;
            described.Add(new HexLinkFolder(Unique(Path.GetFileName(whole), names), whole));
        }

        var repositoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var repositories = new List<HexLinkRepository>();
        foreach (HexLinkFolder folder in described)
        {
            if (repositoryOf(folder.Path) is not { } repository)
                continue;
            string top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository.Path));
            if (repositories.Any(each => PlatformPaths.Same(each.Path, top)))
                continue;
            repositories.Add(repository with { Name = Unique(repository.Name.Length > 0 ? repository.Name : Path.GetFileName(top), repositoryNames), Path = top });
        }

        string named = name.Length > 0 ? name : described.Count > 0 ? described[0].Name : string.Empty;
        return new HexLinkWorkspace(named, described, repositories);
    }

    // A name not taken yet, the name itself the first time, then name-2, name-3. A name that's nothing (a drive's root) is "folder"
    private static string Unique(string name, HashSet<string> taken)
    {
        string usual = name.Length > 0 ? name.Replace('/', '-').Replace('\\', '-') : "folder";
        string tried = usual;
        for (int next = 2; !taken.Add(tried); next++)
            tried = $"{usual}-{next}";
        return tried;
    }

    /// <summary>
    /// The repository a folder is in, its top folder, the branch checked out (HEAD when it's detached) and the commit at
    /// its head (empty in a repository with no commit yet). Null when the folder isn't in one or git can't be run.
    /// </summary>
    public static HexLinkRepository? RepositoryOf(string folder)
    {
        if (HexHost.NoProcesses("git") is not null || !GitProcess.Installed || !Directory.Exists(folder))
            return null;

        GitResult said = GitProcess.Run(folder, ["rev-parse", "--show-toplevel", "--abbrev-ref", "HEAD", "--verify", "-q", "HEAD"]);
        string[] lines = said.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 || !Directory.Exists(lines[0]))
        {
            // No commit yet, HEAD names nothing and git says so for the lot, the top folder asked alone
            GitResult top = GitProcess.Run(folder, ["rev-parse", "--show-toplevel"]);
            string found = top.Output.Trim();
            return top.Ok && Directory.Exists(found) ? new HexLinkRepository(Path.GetFileName(Path.TrimEndingDirectorySeparator(found)), string.Empty, string.Empty, found) : null;
        }

        string path = Path.GetFullPath(lines[0]);
        string branch = lines.Length > 1 ? lines[1] : string.Empty;
        string head = lines.Length > 2 ? lines[2] : string.Empty;
        return new HexLinkRepository(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), branch, head, path);
    }
}
