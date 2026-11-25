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

    private static readonly string[] OperationArguments =
    [
        "rev-parse",
        "--git-path", "rebase-merge",
        "--git-path", "rebase-apply",
        "--git-path", "MERGE_HEAD",
        "--git-path", "CHERRY_PICK_HEAD",
        "--git-path", "REVERT_HEAD"
    ];

    /// <summary>What git is in the middle of, None for nothing (or git not saying).</summary>
    public static GitOperation Operation(this GitRepository repository)
    {
        return GitProcess.Run(repository.Root, OperationArguments, (exit, output, _) =>
        {
            if (exit != 0)
                return GitOperation.None;

            // One line each in the order asked, relative to the root unless git keeps them somewhere else altogether
            int index = 0;
            ReadOnlySpan<char> remaining = output;
            while (!remaining.IsEmpty && index < Markers.Length)
            {
                int nl = remaining.IndexOf('\n');
                ReadOnlySpan<char> line;
                if (nl < 0)
                {
                    line = remaining;
                    remaining = default;
                }
                else
                {
                    line = remaining[..nl];
                    remaining = remaining[(nl + 1)..];
                }

                line = line.Trim();
                if (line.IsEmpty)
                    continue;

                string at = Path.GetFullPath(Path.Combine(repository.Root, line.ToString()));
                if (File.Exists(at) || Directory.Exists(at))
                    return Markers[index].Operation;
                index++;
            }

            return GitOperation.None;
        });
    }

    /// <summary>Calls it off, the working tree and the branch back to how they were before it. What git said when it went wrong, null when it didn't.</summary>
    public static string? Abort(this GitRepository repository, GitOperation operation)
    {
        if (Verb(operation) is not { } verb)
            return null;

        GitRepository.InvalidateInFlight(repository.Root);
        try
        {
            GitResult result = GitProcess.Run(repository.Root, [verb, "--abort"]);
            return result.Ok ? null : GitStash.Said(result);
        }
        finally
        {
            GitRepository.InvalidateInFlight(repository.Root);
        }
    }

    // GIT_EDITOR takes precedence over -c core.editor in git, so override it too to accept the message git already has
    // even when GitProcess or the parent environment sets GIT_EDITOR=false or an interactive editor
    private static readonly IReadOnlyDictionary<string, string> ContinueEnvironment = new Dictionary<string, string>
    {
        ["GIT_EDITOR"] = "true",
        ["GIT_SEQUENCE_EDITOR"] = "true"
    };

    /// <summary>
    /// Carries on once the conflicts are settled and staged, with the message git already has (no editor to stop in, there's
    /// nobody at a terminal). What git said when it went wrong, null when it didn't.
    /// </summary>
    public static string? Continue(this GitRepository repository, GitOperation operation)
    {
        if (Verb(operation) is not { } verb)
            return null;

        GitRepository.InvalidateInFlight(repository.Root);
        try
        {
            GitResult result = GitProcess.Run(repository.Root, ["-c", "core.editor=true", verb, "--continue"], environment: ContinueEnvironment);
            return result.Ok ? null : GitStash.Said(result);
        }
        finally
        {
            GitRepository.InvalidateInFlight(repository.Root);
        }
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
