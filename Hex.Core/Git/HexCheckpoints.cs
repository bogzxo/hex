namespace Hex.Core.Git;

/// <summary>
/// The working copy as it was before an agent's turn, for its changes to be seen against it and put back. A checkpoint is
/// a git tree of every file, tracked and untracked (the ignored left out), written through a copy of the repository's own
/// index, so neither the index, HEAD nor any branch moves and nothing shows in the person's history. Copying the index
/// keeps what git knows of each file, so only what changed since is hashed again. A folder that isn't a repository has
/// none. Everything here runs git, so it's for the background.
/// </summary>
internal static class HexCheckpoints
{
    /// <summary>The tree of a folder's repository as it is now, null when it isn't one or git couldn't.</summary>
    public static string? Take(string folder)
    {
        GitResult inside = GitProcess.Run(folder, ["rev-parse", "--show-toplevel", "--git-path", "index"]);
        if (!inside.Ok)
            return null;
        string[] said = inside.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (said.Length < 2)
            return null;
        string top = said[0], index = Path.IsPathRooted(said[1]) ? said[1] : Path.Combine(folder, said[1]);

        string copy = Path.Combine(Path.GetTempPath(), $"hex-checkpoint-{Guid.NewGuid():N}.index");
        try
        {
            if (File.Exists(index))
                File.Copy(index, copy);
            var own = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = copy };
            if (!GitProcess.Run(top, ["add", "-A", "--", "."], environment: own).Ok)
                return null;
            GitResult tree = GitProcess.Run(top, ["write-tree"], environment: own);
            return tree.Ok && tree.Output.Trim() is { Length: 40 or 64 } hash ? hash : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            try
            {
                File.Delete(copy);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The top folder of the repository a path is in, null when it's in none.</summary>
    public static string? TopOf(string path)
    {
        string? folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (folder is null || !Directory.Exists(folder))
            return null;
        GitResult top = GitProcess.Run(folder, ["rev-parse", "--show-toplevel"]);
        return top.Ok ? Path.GetFullPath(top.Output.Trim()) : null;
    }

    // How far below a folder that isn't a repository its repositories are looked for, a folder of a few projects side by side
    private const int NESTED_DEPTH = 2;

    /// <summary>
    /// The repositories a workspace folder holds, its own when it's in one, otherwise those a level or two below it. A folder
    /// of several projects each with its own repository had no checkpoint at all.
    /// </summary>
    public static List<string> ReposOf(string folder)
    {
        var found = new List<string>();
        if (TopOf(folder) is { } top)
            found.Add(top);
        else
            Below(folder, NESTED_DEPTH, found);
        return found;
    }

    // The folders under one that have a .git of their own, not looking inside a repository once one is found
    private static void Below(string folder, int depth, List<string> found)
    {
        if (depth <= 0)
            return;
        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (string child in children)
        {
            string name = Path.GetFileName(child);
            if (name.StartsWith('.') || name is "node_modules" or "bin" or "obj")
                continue;
            if (Directory.Exists(Path.Combine(child, ".git")) || File.Exists(Path.Combine(child, ".git")))
                found.Add(Path.GetFullPath(child));
            else
                Below(child, depth - 1, found);
        }
    }

    // A path as git names it in a tree, from the repository's top, with forward slashes
    private static string InTree(string top, string path) => Path.GetRelativePath(top, Path.GetFullPath(path)).Replace('\\', '/');

    /// <summary>A file's text as it was at a checkpoint, null when it wasn't there then.</summary>
    public static string? TextAt(string top, string tree, string path)
    {
        GitResult shown = GitProcess.Run(top, ["show", $"{tree}:{InTree(top, path)}"]);
        return shown.Ok ? shown.Output : null;
    }

    /// <summary>
    /// A file put back as it was at a checkpoint, from the tree itself so a picture comes back whole, or deleted when it
    /// wasn't there then. The index is left as it is. The reason it couldn't, or null.
    /// </summary>
    public static string? Restore(string top, string tree, string path)
    {
        string inTree = InTree(top, path);
        if (!GitProcess.Run(top, ["cat-file", "-e", $"{tree}:{inTree}"]).Ok)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return e.Message;
            }
        }

        GitResult put = GitProcess.Run(top, ["restore", $"--source={tree}", "--worktree", "--", inTree]);
        return put.Ok ? null : put.Why;
    }
}
