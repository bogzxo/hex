namespace Hex.Core;

internal static partial class HexLinkProvision
{
    internal static async Task GetMissingRepository(HexLinkPeer peer, HexLinkFolder folder, string path, string journal,
        IProgress<HexLinkProvisionProgress>? progress, CancellationToken cancel)
    {
        if (path.Length == 0)
            throw HexLinkException.Invalid("Choose a nested repository.");
        Resolve(folder.Path, path);
        progress?.Report(new(HexLinkProvisionPhase.Planning, 0, 0));
        HexLinkProvisionPlan plan = await peer.GetProvisionPlan(folder.Name, cancel, path);
        if (plan.Folders is not [var remote] || remote.Name != folder.Name || remote.Path.Length != 0
            || plan.RepositoryPath != path || plan.Deleted is null || plan.LargeChanges is null)
            throw new InvalidOperationException("The other device did not return a plan for this repository. Update Hex on both devices.");
        if (plan.Repositories.FirstOrDefault(repo => repo.Path == path) is not { } requested)
            throw new InvalidOperationException($"The other device has no repository at {path}.");
        if (plan.LargeChanges.FirstOrDefault() is { } large)
            throw new InvalidOperationException($"{large} has uncommitted content larger than this link can transfer.");

        var paths = new HashSet<string>(StringComparer.Ordinal) { path };
        foreach (HexLinkProvisionRepository repository in plan.Repositories.OrderBy(repo => repo.Path.Length))
        {
            if (repository.ParentRepository is { } parent && paths.Contains(parent))
                paths.Add(repository.Path);
        }

        if (requested.ParentRepository is { } parentPath)
        {
            var parent = plan.Repositories.Single(repo => repo.Path == parentPath);
            string parentCheckout = Resolve(folder.Path, parent.Path);
            if (!HasRepository(parentCheckout) || ReadHead(parentCheckout, cancel) != parent.Head)
                throw new InvalidOperationException($"Align the parent repository at {parentPath} before getting {path}.");
            if (Run(parentCheckout, ["status", "--porcelain=v1", "--", ".gitmodules"], cancel).Length > 0)
                throw new InvalidOperationException("Save the local .gitmodules changes before getting this submodule.");
        }

        HexLinkProvisionPlan scoped = plan with { Repositories = [.. plan.Repositories.Where(repo => paths.Contains(repo.Path))] };
        foreach (HexLinkProvisionRepository repository in scoped.Repositories)
        {
            string checkout = Resolve(folder.Path, repository.Path);
            if (!HasRepository(checkout))
                continue;
            string? head = ReadHead(checkout, cancel);
            if (head == repository.Head && ReadBranch(checkout, cancel) == repository.Branch)
                continue;
            if (head is null && Directory.EnumerateFileSystemEntries(checkout).All(file => Path.GetFileName(file) == ".git"))
                continue;
            throw new InvalidOperationException($"{repository.Path} is already a checkout. Align or save its work before updating it.");
        }
        await ApplyRepositories(peer, scoped, folder.Path, align: true, progress, cancel);
        await CopyWorkingTrees(peer, scoped, [folder], folder.Path, journal, progress, cancel, paths);
        progress?.Report(new(HexLinkProvisionPhase.Complete, paths.Count, paths.Count));
    }

    private static string? CheckRepositoryHeads(HexLinkProvisionPlan plan, HexLinkManifest local, HexLinkManifest remote)
    {
        foreach (HexLinkProvisionRepository repository in plan.Repositories)
        {
            if (local.NestedHeads?.GetValueOrDefault(repository.Path) != repository.Head
                || remote.NestedHeads?.GetValueOrDefault(repository.Path) != repository.Head)
                return $"{repository.Path} changed commits while it was being transferred";
        }
        return null;
    }

    // Files belong to their nearest checkout, so an independent repository under the selected one is left alone.
    private static bool InRepositoryScope(string path, IReadOnlyDictionary<string, string?>? heads, IReadOnlySet<string>? scope)
    {
        if (scope is null)
            return true;
        if (heads is null)
            return false;

        for (int slash = path.LastIndexOf('/'); slash > 0; slash = path.LastIndexOf('/', slash - 1))
        {
            string repository = path[..slash];
            if (heads.ContainsKey(repository))
                return scope.Contains(repository);
        }
        return false;
    }
}
