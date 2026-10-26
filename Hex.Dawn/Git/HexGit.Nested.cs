using Hex.Core.Git;

namespace Hex.Dawn;

// The repositories inside the workspace's folders, not only the ones the folders are in. A workspace of a folder of
// projects, each a repository of its own (this one, hex and horizon and the game side by side, with their submodules a
// folder or two further in), had none of them on the Source Control side. They're looked for in the background a few
// folders down, again every few seconds and soon after anything is made (a clone, a git init), and every one found is
// followed like the rest. One whose .git went is let go of.
internal sealed partial class HexGit
{
    // How far down a workspace folder a repository is looked for, deep enough for a submodule of a project in a folder of
    // projects (Fighter2D/external/horizon, horizon/Horizon.HIDL) with room to spare
    private const int NESTED_DEPTH = 5;

    // How often the workspace's folders are looked down again for a repository made since, in seconds
    private const float LOOK_DOWN_EVERY = 6.0f;

    private string[] nestedFor = [];
    private bool lookingDown;
    private float lookDownIn = LOOK_DOWN_EVERY;

    // The workspace's repositories in order, kept until one is found or let go of or the folders change, so the active
    // one's look for the first of them every second makes nothing
    private readonly List<Repository> listed = [];
    private string[] listedFor = [];
    private bool listedDirty = true;

    /// <summary>
    /// Every repository of the workspace, the ones its folders are in and the ones inside them, in order of where they are.
    /// The same list until something changes, don't hold on to it or change it.
    /// </summary>
    private List<Repository> WorkspaceRepositories()
    {
        IReadOnlyList<string> roots = context.Workspace.Roots;
        if (!listedDirty && Same(roots, listedFor))
            return listed;

        listedDirty = false;
        listedFor = [.. roots];
        listed.Clear();
        foreach (Repository repository in repositories.Values)
        {
            bool inside = roots.Any(root => PlatformPaths.IsUnder(repository.Git.Root, root) || PlatformPaths.Same(repository.Git.Root, root));
            bool around = roots.Any(root => PlatformPaths.IsUnder(root, repository.Git.Root));
            if (!repository.Gone && (inside || around))
                listed.Add(repository);
        }

        listed.Sort((a, b) => string.Compare(a.Git.Root, b.Git.Root, StringComparison.OrdinalIgnoreCase));
        return listed;
    }

    /// <summary>Every tick, the folders looked down again every few seconds for a repository made since.</summary>
    private void UpdateLookDown(float dt)
    {
        if ((lookDownIn -= dt) > 0.0f)
            return;

        lookDownIn = LOOK_DOWN_EVERY;
        LookDown(again: true);
    }

    // Something was made in the workspace, so the look down comes sooner, a clone shows up a moment after it's done
    private void LookDownSoon() => lookDownIn = Math.Min(lookDownIn, 1.0f);

    /// <summary>The workspace's folders looked down for repositories, when they changed since the last look or when asked again.</summary>
    private void LookDown(bool again = false)
    {
        // The folders compared one by one with the last look's, joining them into one string to compare was garbage every second
        IReadOnlyList<string> roots = context.Workspace.Roots;
        if (lookingDown || (!again && Same(roots, nestedFor)))
            return;

        lookingDown = true;
        bool moved = !Same(roots, nestedFor);
        nestedFor = [.. roots];
        FindDownInBackground(nestedFor, moved);
    }

    // Helper method to look down the folders off the editor's thread, a method of its own so its closure is made only
    // then. The side is put together again after a look for folders that changed, or when a repository came or went
    private void FindDownInBackground(string[] folders, bool moved)
    {
        context.Work.InBackground("git repositories", () => FindDown(folders), found =>
        {
            lookingDown = false;
            bool changed = moved;
            foreach (string folder in found)
                changed |= Adopt(folder);
            changed |= LetGoOfGone();
            if (!changed)
                return;

            listedDirty = true;
            Version++;
            context.Panels.RefreshSide(HexSide.SourceControl);
            context.Redraw();
        }, quiet: true);
    }

    /// <summary>
    /// Helper method to follow a repository found looking down, true when it's new. What was said of the paths in it
    /// before is forgotten, they were the repository around it's (a git init inside one) or nobody's.
    /// </summary>
    private bool Adopt(string folder)
    {
        if (repositories.ContainsKey(folder) || GitRepository.Find(folder) is not { } git || repositories.ContainsKey(git.Root))
            return false;

        var found = new Repository(this, git);
        repositories[git.Root] = found;
        found.LookForMoves();
        Forget(path => PlatformPaths.Same(path, git.Root) || PlatformPaths.IsUnder(path, git.Root));
        return true;
    }

    // Helper method to let go of the repositories whose .git is gone (a folder deleted), and what was said of paths in them
    private bool LetGoOfGone()
    {
        bool any = false;
        foreach (Repository repository in repositories.Values)
        {
            if (!Path.Exists(Path.Combine(repository.Git.Root, ".git")))
            {
                repository.Gone = true;
                any = true;
            }
        }

        if (!any)
            return false;

        foreach (var (root, repository) in repositories)
            if (repository.Gone) forgetting.Add(root);
        foreach (string root in forgetting)
            repositories.Remove(root);
        forgetting.Clear();

        Forget(foundFor, (_, repository) => repository is { Gone: true });
        Forget(foundForPath, (_, repository) => repository is { Gone: true });
        if (chosen is { Gone: true })
            chosen = null;
        return true;
    }

    // Helper method to forget where every path under a folder was found to be, for it to be looked for again
    private void Forget(Func<string, bool> under)
    {
        Forget(foundFor, (path, _) => under(path));
        Forget(foundForPath, (path, _) => under(path));
    }

    private void Forget(Dictionary<string, Repository?> found, Func<string, Repository?, bool> which)
    {
        foreach (var (path, repository) in found)
            if (which(path, repository)) forgetting.Add(path);
        foreach (string path in forgetting)
            found.Remove(path);
        forgetting.Clear();
    }

    // Whether the workspace's folders are the ones of some look before
    private static bool Same(IReadOnlyList<string> roots, string[] before)
    {
        if (roots.Count != before.Length)
            return false;

        for (int i = 0; i < roots.Count; i++)
        {
            if (roots[i] != before[i])
                return false;
        }

        return true;
    }

    // The folders under some folders with a .git in them, built and git's own folders left out
    private static List<string> FindDown(string[] roots)
    {
        var found = new List<string>();
        var pending = new Stack<(string Folder, int Depth)>();
        foreach (string root in roots)
            pending.Push((root, 0));

        while (pending.Count > 0)
        {
            var (folder, depth) = pending.Pop();
            string[] inside;
            try { inside = Directory.GetDirectories(folder); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            foreach (string child in inside)
            {
                string name = Path.GetFileName(child);
                if (name.StartsWith('.') || HexSearch.SkippedFolders.Contains(name))
                    continue;

                string dot = Path.Combine(child, ".git");
                if (Directory.Exists(dot) || File.Exists(dot))
                    found.Add(child);
                if (depth + 1 < NESTED_DEPTH)
                    pending.Push((child, depth + 1));
            }
        }

        return found;
    }
}
