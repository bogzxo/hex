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

    private sealed record ProvisionFacts(string Head, string? Branch, string? Origin, Dictionary<string, string> Modules);

    /// <summary>Builds a checkout plan for the actual repositories inside the folders a peer may see.</summary>
    internal static HexLinkHostedProvision Provision(HexLinkWorkspace workspace, string? only, CancellationToken cancel, string? onlyRepository = null, bool deferWorktree = false)
    {
        if (HexLinkGit.Why() is { } why)
            throw new InvalidOperationException(why);

        HexLinkFolder[] selected = [.. workspace.Folders.Where(folder => only is null || folder.Name == only)];
        if (selected.Length is 0 or > HexLinkHost.MOST_TREES)
            throw HexLinkException.Invalid("The workspace has no such folder or too many folders to provision.");

        var real = selected.ToDictionary(folder => folder.Name, folder => HexLinkFiles.Resolve(selected, folder.Name), StringComparer.Ordinal);
        HexLinkFolder[] roots = [.. selected.Where(folder => !selected.Any(other => other != folder
            && PlatformPaths.IsUnder(real[folder.Name], real[other.Name])))];
        var folders = new List<HexLinkProvisionFolder>();
        foreach (HexLinkFolder folder in selected)
        {
            HexLinkFolder root = roots.First(root => PlatformPaths.IsUnder(real[folder.Name], real[root.Name]));
            string prefix = roots.Length == 1 ? string.Empty : root.Name;
            folders.Add(new HexLinkProvisionFolder(folder.Name, Under(prefix, real[root.Name], real[folder.Name]), folder.Authority));
        }

        if (onlyRepository is not null && (only is null || onlyRepository.Length == 0))
            throw HexLinkException.Invalid("A missing repository must be nested inside a named folder.");

        var repoScope = new HashSet<string>(StringComparer.Ordinal);
        var repos = new List<HexLinkProvisionRepository>();
        var wirePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var gitlinks = new Dictionary<string, (string Parent, string Head)>(StringComparer.Ordinal);
        var skipped = new List<HexLinkProvisionSkip>();
        var physical = new HashSet<string>(PlatformPaths.Comparer);
        var deleted = new List<string>();
        var largeChanges = new List<string>();
        string lease = Guid.NewGuid().ToString("N");
        bool partial = only is null;

        // Check every root before walking its nested checkouts. A bad root cannot produce a workspace.
        foreach (HexLinkFolder root in roots)
        {
            string rootPath = real[root.Name];
            if (HexLinkProvision.HasRepository(rootPath))
                Describe(root.Name, rootPath, roots.Length == 1 ? string.Empty : root.Name);
        }

        foreach (HexLinkFolder root in roots)
        {
            string rootPath = real[root.Name];
            string prefix = roots.Length == 1 ? string.Empty : root.Name;
            string[] found = [.. HexLinkTree.FindNestedRepositories(rootPath, cancel, partial
                ? (path, reason) => skipped.Add(new(prefix.Length == 0 ? path : prefix + "/" + path, reason)) : null,
                includeSystemFiles: root.IncludeSystemFiles).OrderBy(path => path.Length)];
            var facts = new ProvisionFacts?[found.Length];
            var errors = new Exception?[found.Length];
            if (deferWorktree)
            {
                Parallel.For(0, found.Length, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancel }, index =>
                {
                    string path = root.Name + "/" + found[index];
                    try
                    {
                        facts[index] = ReadFacts(path, HexLinkFiles.Resolve(selected, path));
                    }
                    catch (Exception e) when (MaySkip(e))
                    {
                        errors[index] = e;
                    }
                });
            }
            for (int index = 0; index < found.Length; index++)
            {
                string relative = found[index];
                cancel.ThrowIfCancellationRequested();
                string path = prefix.Length == 0 ? relative : prefix + "/" + relative;
                if (HexLinkProvisionPlan.Skips(skipped, path))
                    continue;
                string wire = root.Name + "/" + relative;
                try
                {
                    if (errors[index] is { } error)
                        throw error;
                    Describe(wire, HexLinkFiles.Resolve(selected, wire), path, facts[index]);
                }
                catch (Exception e) when (MaySkip(e))
                {
                    skipped.Add(new(path, e.Message));
                }
            }
        }

        var repoPaths = repos.Select(repo => repo.Path).ToHashSet(StringComparer.Ordinal);
        if (onlyRepository is not null && !repoScope.Contains(onlyRepository))
            throw HexLinkException.NotFound($"The workspace has no nested repository at {onlyRepository}.");

        foreach (var (path, link) in gitlinks)
        {
            if (repoPaths.Contains(path) || HexLinkProvisionPlan.Skips(skipped, path)
                || onlyRepository is not null && !repoScope.Contains(link.Parent))
                continue;
            if (!partial)
                throw new InvalidOperationException($"Submodule {path} is uninitialized on the host.");
            skipped.Add(new(path, "Submodule is uninitialized on the host."));
        }
        return new HexLinkHostedProvision(new HexLinkProvisionPlan(folders, repos, deleted, largeChanges, onlyRepository, skipped, deferWorktree), wirePaths);

        bool MaySkip(Exception error) => partial && !HexLinkProvision.DiskFull(error)
            && error is (HexLinkException or InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException);

        ProvisionFacts ReadFacts(string wire, string directory)
        {
            if (HexLinkProvision.Run(directory, ["rev-parse", "--show-prefix"], cancel).Trim().Length > 0)
                throw new InvalidOperationException($"{wire} does not identify its own Git checkout.");
            string head = HexLinkProvision.ReadHead(directory, cancel) ?? throw new InvalidOperationException($"Repository {wire} has no commit.");
            var origin = GitProcess.Run(directory, ["config", "--local", "--get", "remote.origin.url"], cancel: cancel);
            if (!origin.Ok && origin.Exit != 1)
                throw new InvalidOperationException($"Couldn't read the origin of {wire}: {HexLinkGit.ErrorOf(origin)}");
            return new(head, HexLinkProvision.ReadBranch(directory, cancel), origin.Ok ? origin.Output.TrimEnd('\r', '\n') : null,
                Submodules(directory, cancel));
        }

        void Describe(string wire, string directory, string path, ProvisionFacts? prepared = null)
        {
            if (!physical.Add(directory))
                return;
            if (repos.Count == HexLinkTree.MOST_REPOS)
                throw HexLinkException.Invalid("The workspace has too many repositories to provision.");
            bool inScope = path == onlyRepository || gitlinks.TryGetValue(path, out var moduleLink) && repoScope.Contains(moduleLink.Parent);
            if (onlyRepository is not null && !inScope && !HexLinkProvisionPlan.Inside(onlyRepository, path))
                return;

            ProvisionFacts facts = prepared ?? ReadFacts(wire, directory);
            string head = facts.Head;
            string? parent = null;
            if (gitlinks.TryGetValue(path, out var link))
            {
                parent = link.Parent;
                if (head != link.Head)
                    throw new InvalidOperationException($"Submodule {wire} is at {head}. Its parent records {link.Head}.");
            }
            string id = $"{HexLinkProvisionPlan.BUNDLE_PREFIX}{lease}-{repos.Count}";
            string? branch = parent is null ? facts.Branch : null;
            if (!deferWorktree && (only is null || inScope))
                ProvisionChanges(directory, path, deleted, largeChanges,
                    selected.First(folder => wire == folder.Name || wire.StartsWith(folder.Name + "/", StringComparison.Ordinal)).IncludeSystemFiles, cancel);

            repos.Add(new HexLinkProvisionRepository(id, path, head, branch, facts.Origin, parent));
            wirePaths.Add(id, wire);
            if (inScope)
                repoScope.Add(path);
            foreach (var (module, commit) in facts.Modules)
                gitlinks.Add(path.Length == 0 ? module : path + "/" + module, (path, commit));
        }
    }

    private static void ProvisionChanges(string directory, string repositoryPath, List<string> deleted, List<string> largeChanges, bool includeSystemFiles, CancellationToken cancel)
    {
        foreach (string status in HexLinkProvision.Status(directory, cancel))
        {
            string file = status[3..];
            if (!includeSystemFiles && HexLinkSystemFiles.Matches(file))
                continue;
            string path = repositoryPath.Length > 0 ? repositoryPath + "/" + file : file;
            if ((status[0] == 'D' || status[1] == 'D') && !File.Exists(Path.Combine(directory, file)))
                deleted.Add(path);
            else if (new FileInfo(Path.Combine(directory, file)) is { Exists: true, Length: > HexLinkFiles.MOST_READ })
                largeChanges.Add(path);
        }
    }

    private static string Under(string prefix, string root, string path)
    {
        string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        return relative == "." ? prefix : prefix.Length > 0 ? prefix + "/" + relative : relative;
    }

    private static Dictionary<string, string> Submodules(string directory, CancellationToken cancel)
    {
        string[] links = [.. HexLinkProvision.Run(directory, ["ls-tree", "-rz", "HEAD"], cancel)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries).Where(entry => entry.StartsWith("160000 commit ", StringComparison.Ordinal))];
        var modules = new Dictionary<string, string>(StringComparer.Ordinal);
        if (links.Length == 0)
            return modules;

        GitResult config = GitProcess.Run(directory, ["config", "--blob", "HEAD:.gitmodules", "-z", "--get-regexp", "^submodule\\..*\\.path$"], cancel: cancel);
        if (!config.Ok)
        {
            // Gitlinks without entries in .gitmodules are not registered submodules.
            if (config.Exit == 1 || config.Error.Contains("unable to resolve config blob", StringComparison.OrdinalIgnoreCase))
                return modules;
            throw new InvalidOperationException($"{directory}: {HexLinkGit.ErrorOf(config)}");
        }

        var configured = config.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry[(entry.IndexOf('\n') + 1)..]).ToHashSet(StringComparer.Ordinal);
        foreach (string entry in links)
        {
            int tab = entry.IndexOf('\t');
            if (tab < 0)
                continue;
            string module = entry[(tab + 1)..];
            if (configured.Contains(module))
                modules.Add(module, entry[14..tab]);
        }
        return modules;
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

        // The top folder and the commit at the head, then the branch's name (abbrev-ref holds for every name after it)
        GitResult said = GitProcess.Run(folder, ["rev-parse", "--show-toplevel", "HEAD"]);
        string[] lines = said.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!said.Ok || lines.Length < 2 || !Directory.Exists(lines[0]))
        {
            // No commit yet, HEAD names nothing and git says so for the lot, the top folder asked alone
            GitResult top = GitProcess.Run(folder, ["rev-parse", "--show-toplevel"]);
            string found = top.Output.Trim();
            if (!top.Ok || !Directory.Exists(found))
                return null;
            found = AsAsked(folder, found);
            return new HexLinkRepository(Path.GetFileName(Path.TrimEndingDirectorySeparator(found)), string.Empty, string.Empty, found);
        }

        string path = AsAsked(folder, lines[0]);
        string head = lines[1];
        GitResult named = GitProcess.Run(folder, ["rev-parse", "--abbrev-ref", "HEAD"]);
        string branch = named.Ok ? named.Output.Trim() : string.Empty;
        return new HexLinkRepository(Path.GetFileName(Path.TrimEndingDirectorySeparator(path)), branch, head, path);
    }

    /// <summary>
    /// The top folder git said, spelled as the folder it was asked about is. Git resolves links on the way (macOS's temp
    /// folder under /var is /private/var to it), so a folder inside a linked path would seem to be outside its own
    /// repository; then the top is found by going up from the asked folder as many levels as git says it's below the top.
    /// </summary>
    private static string AsAsked(string folder, string top)
    {
        string asked = Path.GetFullPath(folder), said = Path.TrimEndingDirectorySeparator(Path.GetFullPath(top));
        if (PlatformPaths.IsUnder(asked, said))
            return said;
        GitResult up = GitProcess.Run(folder, ["rev-parse", "--show-cdup"]);
        if (!up.Ok)
            return said;
        string spelled = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(asked, up.Output.Trim())));
        return Directory.Exists(spelled) ? spelled : said;
    }
}
