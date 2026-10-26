namespace Hex.Dawn;

// The repositories inside the workspace's folders, not only the ones the folders are in. A workspace of a folder of
// projects, each a repository of its own (this one, hex and horizon and the game side by side), had none of them on the
// Source Control side. They're looked for in the background a few folders down, again when the folders change or the
// explorer is refreshed, and every one found is followed like the rest.
internal sealed partial class HexGit
{
    // How far down a workspace folder a repository is looked for
    private const int NESTED_DEPTH = 3;

    private string[] nestedFor = [];
    private bool lookingDown;

    /// <summary>Every repository of the workspace, the ones its folders are in and the ones inside them, in order of where they are.</summary>
    private List<Repository> WorkspaceRepositories()
    {
        IReadOnlyList<string> roots = context.Workspace.Roots;
        var found = new List<Repository>();
        foreach (Repository repository in repositories.Values)
        {
            bool inside = roots.Any(root => PlatformPaths.IsUnder(repository.Git.Root, root) || PlatformPaths.Same(repository.Git.Root, root));
            bool around = roots.Any(root => PlatformPaths.IsUnder(root, repository.Git.Root));
            if (inside || around)
                found.Add(repository);
        }

        found.Sort((a, b) => string.Compare(a.Git.Root, b.Git.Root, StringComparison.OrdinalIgnoreCase));
        return found;
    }

    /// <summary>Every so often, the workspace's folders looked down for repositories when they changed since the last look.</summary>
    private void LookDown(bool again = false)
    {
        // The folders compared one by one with the last look's, joining them into one string to compare was garbage every second
        IReadOnlyList<string> roots = context.Workspace.Roots;
        if (lookingDown || (!again && SameRoots(roots)))
            return;

        lookingDown = true;
        nestedFor = [.. roots];
        FindDownInBackground(nestedFor);
    }

    // Helper method to look down the folders off the editor's thread, a method of its own so its closure is made only then
    private void FindDownInBackground(string[] folders)
    {
        context.Work.InBackground("git repositories", () => FindDown(folders), found =>
        {
            lookingDown = false;
            foreach (string folder in found)
                RepositoryOf(folder);
            context.Panels.RefreshSide(HexSide.SourceControl);
        }, quiet: true);
    }

    // Whether the workspace's folders are the ones looked down last time
    private bool SameRoots(IReadOnlyList<string> roots)
    {
        if (roots.Count != nestedFor.Length)
            return false;

        for (int i = 0; i < roots.Count; i++)
        {
            if (roots[i] != nestedFor[i])
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
