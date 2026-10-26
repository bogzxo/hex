namespace Hex.Core.Git;

/// <summary>Something of git's left half done, waiting on conflicts to be settled or to be called off.</summary>
internal enum GitOperation
{
    None,
    Merging,
    Rebasing,
    CherryPicking,
    Reverting
}

/// <summary>
/// A merge, a rebase, a cherry-pick or a revert git stopped in the middle of, which one it is, and it called off or carried
/// on. Found by the files git leaves behind while it waits (MERGE_HEAD, rebase-merge and the rest), asked of git where
/// they are so a worktree or a submodule with a .git file is read the same. All of it runs git and waits, so it's for
/// the background.
/// </summary>
internal static class GitMerge
{
    // What git leaves behind for each, a rebase first since a rebase that stopped on a pick can leave CHERRY_PICK_HEAD too
    private static readonly (string Path, GitOperation Operation)[] Markers =
    [
        ("rebase-merge", GitOperation.Rebasing),
        ("rebase-apply", GitOperation.Rebasing),
        ("MERGE_HEAD", GitOperation.Merging),
        ("CHERRY_PICK_HEAD", GitOperation.CherryPicking),
        ("REVERT_HEAD", GitOperation.Reverting)
    ];

    /// <summary>What git is in the middle of, None for nothing (or git not saying).</summary>
    public static GitOperation Operation(this GitRepository repository)
    {
        var arguments = new List<string> { "rev-parse" };
        foreach (var (path, _) in Markers)
        {
            arguments.Add("--git-path");
            arguments.Add(path);
        }

        GitResult result = GitProcess.Run(repository.Root, arguments);
        if (!result.Ok)
            return GitOperation.None;

        // One line each in the order asked, relative to the root unless git keeps them somewhere else altogether
        string[] lines = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < Math.Min(lines.Length, Markers.Length); i++)
        {
            string at = Path.GetFullPath(Path.Combine(repository.Root, lines[i].Trim()));
            if (File.Exists(at) || Directory.Exists(at))
                return Markers[i].Operation;
        }

        return GitOperation.None;
    }

    /// <summary>Calls it off, the working tree and the branch back to how they were before it. What git said when it went wrong, null when it didn't.</summary>
    public static string? Abort(this GitRepository repository, GitOperation operation)
    {
        if (Verb(operation) is not { } verb)
            return null;

        GitResult result = GitProcess.Run(repository.Root, [verb, "--abort"]);
        return result.Ok ? null : GitStash.Said(result);
    }

    /// <summary>
    /// Carries on once the conflicts are settled and staged, with the message git already has (no editor to stop in, there's
    /// nobody at a terminal). What git said when it went wrong, null when it didn't.
    /// </summary>
    public static string? Continue(this GitRepository repository, GitOperation operation)
    {
        if (Verb(operation) is not { } verb)
            return null;

        GitResult result = GitProcess.Run(repository.Root, ["-c", "core.editor=true", verb, "--continue"]);
        return result.Ok ? null : GitStash.Said(result);
    }

    // The git command each one is
    private static string? Verb(GitOperation operation) => operation switch
    {
        GitOperation.Merging => "merge",
        GitOperation.Rebasing => "rebase",
        GitOperation.CherryPicking => "cherry-pick",
        GitOperation.Reverting => "revert",
        _ => null
    };
}
