using Hex.Core.Git;

namespace Hex.Dawn;

// The repositories inside the workspace's folders, not only the ones the folders are in. A workspace of a folder of
// projects, each a repository of its own (this one, hex and horizon and the game side by side, with their submodules a
// folder or two further in), had none of them on the Source Control side. They're looked for in the background a few
// folders down when the workspace's folders change, and then only where the workspace's watching says something was made
// (a clone, a folder moved in, a git init), soon after, and every one found is followed like the rest. One whose .git
// went is let go of, looked for when something was deleted. Looking down the whole workspace again every few seconds
// walked a big one's every folder five deep, all day, with nothing happening.
internal sealed partial class HexGit
{
    // How far down a workspace folder a repository is looked for, deep enough for a submodule of a project in a folder of
    // projects (Fighter2D/external/horizon, horizon/Horizon.HIDL) with room to spare
    private const int NESTED_DEPTH = 5;

    // How long after something is made it's looked down from, in seconds, a clone or a build making many things one look
    private const float LOOK_DOWN_AFTER = 1.0f;

    // The most made things kept to look at, the whole workspace looked down again past that (an archive unpacked)
    private const int MOST_MADE = 512;

    // How long a folder that was made is looked at for a .git turning up in it (a git init after the mkdir), in seconds,
    // and the most looked at, git's own files being nothing the workspace's watching says
    private const double NEW_FOLDER_WATCHED = 120.0;
    private const int MOST_NEW_FOLDERS = 64;

    private string[] nestedFor = [];
    private bool lookingDown;
    private CancellationTokenSource? repositoryScan;

    private void StopLookingDown()
    {
        repositoryScan?.Cancel();
        repositoryScan = null;
        lookingDown = false;
        lookDownIn = 0;
        lookDownAll = deletedSince = false;
        nestedFor = [];
        madeSince.Clear();
        newFolders.Clear();
    }
    private float lookDownIn;

    // What was made since the last look and whether anything was deleted, nothing walked while both are empty. The whole
    // workspace is looked down again when too much was made to look at each, or a look was asked for during another
    private readonly HashSet<string> madeSince = new(PlatformPaths.Comparer);
    private bool deletedSince, lookDownAll;

    // The folders made lately with no .git in them yet, and when they stop being looked at (Environment.TickCount64)
    private readonly List<(string Folder, long Until)> newFolders = [];

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
            if (!repository.Gone && BelongsToWorkspace(roots, repository.Git.Root))
                listed.Add(repository);
        }

        listed.Sort((a, b) => string.Compare(a.Git.Root, b.Git.Root, StringComparison.OrdinalIgnoreCase));
        return listed;
    }

    private static bool BelongsToWorkspace(IReadOnlyList<string> roots, string repositoryRoot)
    {
        for (int i = 0; i < roots.Count; i++)
        {
            string root = roots[i];
            if (PlatformPaths.IsUnder(repositoryRoot, root) || PlatformPaths.Same(repositoryRoot, root) || PlatformPaths.IsUnder(root, repositoryRoot))
                return true;
        }

        return false;
    }

    /// <summary>Every tick, what was made since the last look looked down from a moment after, nothing while nothing was.</summary>
    private void UpdateLookDown(float dt)
    {
        if ((madeSince.Count == 0 && !deletedSince && !lookDownAll) || lookingDown || (lookDownIn -= dt) > 0.0f)
            return;

        if (lookDownAll)
        {
            madeSince.Clear();
            deletedSince = false;
            LookDown(again: true);
            return;
        }

        lookingDown = true;
        string[] made = [.. madeSince];
        madeSince.Clear();
        deletedSince = false;
        FindDownInBackground([.. context.Workspace.Roots], made, moved: false);
    }

    /// <summary>
    /// Something the workspace's watching says was made or deleted, looked down from a moment after. Made in the workspace
    /// is a clone, a folder moved in or a git init's folder; deleted can be a repository's folder, its .git looked at.
    /// </summary>
    private void LookDownAfter(string path, LanguageServerFileChange change)
    {
        bool waiting = madeSince.Count > 0 || deletedSince || lookDownAll;
        if (change == LanguageServerFileChange.Deleted)
            deletedSince = true;
        else if (change != LanguageServerFileChange.Created)
            return;
        else if (madeSince.Count < MOST_MADE)
            madeSince.Add(path);
        else
            lookDownAll = true;

        // A moment after the first of them, not put off by every one after, so a clone shows up a moment after it's made
        if (!waiting)
            lookDownIn = LOOK_DOWN_AFTER;
    }

    /// <summary>
    /// Every second, the folders made lately looked at for a .git turning up, a stat each. A git init in a folder that was
    /// made a while before says nothing the workspace's watching passes on, git's own folder being left out of it.
    /// </summary>
    private bool LookAtNewFolders()
    {
        long now = Environment.TickCount64;
        bool changed = false;
        for (int i = newFolders.Count - 1; i >= 0; i--)
        {
            var (folder, until) = newFolders[i];
            if (now > until)
            {
                newFolders.RemoveAt(i);
                continue;
            }

            if (!Path.Exists(Path.Combine(folder, ".git")))
                continue;

            newFolders.RemoveAt(i);
            changed |= Adopt(folder);
        }

        if (changed)
            Adopted();
        return changed;
    }

    /// <summary>The workspace's folders looked down for repositories, when they changed since the last look or when asked again.</summary>
    private void LookDown(bool again = false)
    {
        // The folders compared one by one with the last look's, joining them into one string to compare was garbage every second
        IReadOnlyList<string> roots = context.Workspace.Roots;
        if (!again && Same(roots, nestedFor))
            return;

        // Asked again during a look, it's looked again once that one's done
        if (lookingDown)
        {
            if (again)
                lookDownAll = true;
            return;
        }

        lookingDown = true;
        lookDownAll = false;
        bool moved = !Same(roots, nestedFor);
        nestedFor = [.. roots];
        FindDownInBackground(nestedFor, null, moved);

        // A folder added is watched now rather than once the explorer is next put together, what's made in it is what
        // says where to look down from next
        if (moved)
            context.Workspace.Rewatch();
    }

    // Helper method to look down the folders off the editor's thread, a method of its own so its closure is made only
    // then, all of them or only from what was made in them. The side is put together again after a look for folders that
    // changed, or when a repository came or went
    private void FindDownInBackground(string[] folders, string[]? made, bool moved)
    {
        var scan = repositoryScan = new CancellationTokenSource();
        CancellationToken cancel = scan.Token;
        context.Work.InBackground("git repositories", () => made is null ? (FindDown(folders, cancel), new List<string>()) : FindDownFrom(folders, made, cancel), result =>
        {
            scan.Dispose();
            if (repositoryScan != scan)
                return;
            repositoryScan = null;
            lookingDown = false;
            var (found, newOnes) = result;
            bool changed = moved;
            foreach (string folder in found)
                changed |= Adopt(folder);
            changed |= LetGoOfGone();

            long until = Environment.TickCount64 + (long)(NEW_FOLDER_WATCHED * 1000);
            foreach (string folder in newOnes)
            {
                if (newFolders.Count < MOST_NEW_FOLDERS && !repositories.ContainsKey(folder))
                    newFolders.Add((folder, until));
            }

            if (changed)
                Adopted();
        }, quiet: true);
    }

    // Helper method to put the side together again after a repository came or went
    private void Adopted()
    {
        listedDirty = true;
        Version++;
        context.Panels.RefreshSide(HexSide.SourceControl);
        context.Redraw();
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
    internal static List<string> FindDown(string[] roots, CancellationToken cancel = default)
    {
        var found = new List<string>();
        var pending = new Stack<(string Folder, int Depth)>();
        foreach (string root in roots)
            pending.Push((root, 0));
        FindDown(pending, found, cancel);
        return found;
    }

    /// <summary>
    /// The repositories in or under what was made in some workspace folders, as far down as the whole look goes and leaving
    /// out what it does: a folder made looked down from, a file made its folder looked at. With the folders made that have
    /// no .git, to be looked at a while for one turning up.
    /// </summary>
    internal static (List<string> Found, List<string> Made) FindDownFrom(string[] roots, string[] made, CancellationToken cancel = default)
    {
        var found = new List<string>();
        var folders = new List<string>();
        var pending = new Stack<(string Folder, int Depth)>();
        var looked = new HashSet<string>(PlatformPaths.Comparer);
        foreach (string path in made)
        {
            if (cancel.IsCancellationRequested)
                break;
            bool folder = Directory.Exists(path);
            string? at = folder ? path : Path.GetDirectoryName(path);
            if (at is null || !looked.Add(at) || DepthUnder(roots, at) is not { } depth || depth == 0 || depth > NESTED_DEPTH)
                continue;

            if (Path.Exists(Path.Combine(at, ".git")))
                found.Add(at);
            else if (folder)
                folders.Add(at);
            if (folder && depth < NESTED_DEPTH)
                pending.Push((at, depth));
        }

        FindDown(pending, found, cancel);
        return (found, folders);
    }

    // How far down a workspace folder a folder is, null when it's in none of them or in a folder the look leaves out
    private static int? DepthUnder(string[] roots, string folder)
    {
        foreach (string root in roots)
        {
            if (PlatformPaths.Same(folder, root))
                return 0;
            if (!PlatformPaths.IsUnder(folder, root))
                continue;

            int depth = 0;
            foreach (string name in Path.GetRelativePath(root, folder).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (name.Length == 0)
                    continue;
                if (name.StartsWith('.') || HexSearch.SkippedFolders.Contains(name))
                    return null;
                depth++;
            }

            return depth;
        }

        return null;
    }

    // Helper method to look down from the folders waiting, every one with a .git in it found
    private static void FindDown(Stack<(string Folder, int Depth)> pending, List<string> found, CancellationToken cancel)
    {
        while (pending.Count > 0 && !cancel.IsCancellationRequested)
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
                if (Path.Exists(dot))
                    found.Add(child);
                if (depth + 1 < NESTED_DEPTH)
                    pending.Push((child, depth + 1));
            }
        }
    }
}
