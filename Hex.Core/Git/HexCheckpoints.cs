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
    // How long the add of the whole working copy may take, and the refs each checkpoint is kept alive by
    private static readonly TimeSpan TAKE_PATIENCE = TimeSpan.FromMinutes(3);
    private const string PIN = "refs/hex/checkpoints/";

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
            // A first add of a big working copy with untracked build output takes longer than git's usual patience, and
            // killed halfway the turn had no checkpoint at all
            if (!GitProcess.Run(top, ["add", "-A", "--", "."], patience: TAKE_PATIENCE, environment: own).Ok)
                return null;
            GitResult tree = GitProcess.Run(top, ["write-tree"], patience: TAKE_PATIENCE, environment: own);
            if (!tree.Ok || tree.Output.Trim() is not { Length: 40 or 64 } hash)
                return null;

            // Pinned by a ref of its own, nothing else points at the tree and git's tidying would let it go after a couple
            // of weeks, leaving a chat read back then with nothing to put back from
            GitProcess.Run(top, ["update-ref", PIN + hash, hash]);
            return hash;
        }
        catch (Exception)
        {
            // Whatever went wrong, no checkpoint rather than a throw, which left the turn waiting on one for ever
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

    /// <summary>
    /// The top folder of the repository a path is in, the nearest folder up from it with a .git of its own (a folder, or a
    /// file for a worktree or a submodule), null when it's in none. Found by walking up rather than asking git, which says
    /// the path with its links resolved (/private/var for /var on macOS) and then matched none of the workspace's own.
    /// </summary>
    public static string? TopOf(string path)
    {
        string? folder = Path.GetFullPath(Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path);
        for (; folder is not null; folder = Path.GetDirectoryName(folder))
        {
            string git = Path.Combine(folder, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return Path.TrimEndingDirectorySeparator(folder);
        }

        return null;
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
        // The checkpoint itself still there first, gone (git let it go, or git failed) nothing is touched
        GitResult kind = GitProcess.Run(top, ["cat-file", "-t", tree]);
        if (!kind.Ok || kind.Output.Trim() != "tree")
            return kind.Ok ? "the checkpoint isn't there any more" : kind.Why;

        if (!GitProcess.Run(top, ["cat-file", "-e", $"{tree}:{inTree}"]).Ok)
        {
            // Not in the checkpoint is only "made in the turn" for a file git would have taken in. One it ignores (a .env,
            // local settings) or one in a repository of its own below this one was never in it, and was deleted as new
            if (GitProcess.Run(top, ["check-ignore", "-q", "--", inTree]).Ok)
                return "git ignores it, so the checkpoint never had it";
            if (TopOf(path) is { } own && !PlatformPaths.Comparer.Equals(own, Path.GetFullPath(top)))
                return "it's in a repository of its own, which the checkpoint doesn't have";
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
