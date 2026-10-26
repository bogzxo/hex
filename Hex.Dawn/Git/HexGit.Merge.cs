using Hex.Core.Git;

namespace Hex.Dawn;

// A merge, a rebase, a cherry-pick or a revert git stopped in the middle of, waiting on conflicts. Said after the branch
// in the status bar, and called off or carried on from the Git menu, its two commands named for whichever it is, so a
// merge that went wrong never needs a trip to the terminal. Carrying on a merge is a commit, the commit box with the
// message git wrote for it, the others are git's --continue with that message as it is.
internal sealed partial class HexGit
{
    private MenuItem abortItem = null!, continueItem = null!;
    private GitOperation following;

    private void BuildMerge()
    {
        abortItem = context.Commands.Add("Git/changes", context.Strings.Said("git_merge.abort_merge"), () => AskAbort(FirstRepository()), icon: "discard");
        abortItem.IsEnabled = () => FirstRepository() is { Operation: not GitOperation.None };
        continueItem = context.Commands.Add("Git/changes", context.Strings.Said("git_merge.continue_merge"), () => ContinueOperation(FirstRepository()), icon: "debug-continue");
        continueItem.IsEnabled = () => FirstRepository() is { Operation: not GitOperation.None };
    }

    /// <summary>What git is in the middle of in the repository a path is in, None for nothing or no repository.</summary>
    public GitOperation OperationAt(string? path) => StatusAt(path) is { } status ? RepositoryOf(status.Root)?.Operation ?? GitOperation.None : GitOperation.None;

    /// <summary>The word the status bar puts after the branch for what's going on, Merging, Rebasing and the rest.</summary>
    private string OperationWord(GitOperation operation) => operation switch
    {
        GitOperation.Merging => context.Strings.Said("git_merge.merging"),
        GitOperation.Rebasing => context.Strings.Said("git_merge.rebasing"),
        GitOperation.CherryPicking => context.Strings.Said("git_merge.cherry_picking"),
        GitOperation.Reverting => context.Strings.Said("git_merge.reverting"),
        _ => string.Empty
    };

    // The commands named for what the repository of the file on screen is in the middle of, a merge's names for nothing
    private void FollowOperation()
    {
        // Nothing going on anywhere and the names already a merge's, so the file on screen isn't looked up again. That look
        // after the nones were forgotten went to the disk every second, garbage the pointer's sweep counted
        if (following == GitOperation.None && !AnyOperation())
            return;

        GitOperation operation = FirstRepository()?.Operation ?? GitOperation.None;
        if (operation == following || abortItem is null)
            return;

        following = operation;
        (string abort, string carryOn) = operation switch
        {
            GitOperation.Rebasing => ("git_merge.abort_rebase", "git_merge.continue_rebase"),
            GitOperation.CherryPicking => ("git_merge.abort_cherry_pick", "git_merge.continue_cherry_pick"),
            GitOperation.Reverting => ("git_merge.abort_revert", "git_merge.continue_revert"),
            _ => ("git_merge.abort_merge", "git_merge.continue_merge")
        };
        abortItem.Label = context.Strings.Said(abort);
        continueItem.Label = context.Strings.Said(carryOn);
    }

    // Whether any repository found so far is in the middle of something, as of its last look
    private bool AnyOperation()
    {
        foreach (Repository repository in repositories.Values)
        {
            if (repository.Operation != GitOperation.None)
                return true;
        }

        return false;
    }

    /// <summary>Calling it off asks first, what was settled of its conflicts goes with it.</summary>
    private void AskAbort(Repository? repository)
    {
        if (repository is not { Operation: not GitOperation.None and var operation })
        {
            context.Notices.Say(context.Strings.Said("git_merge.nothing"));
            return;
        }

        string yes = abortItem.Label;
        context.Windows.Ask(yes, context.Strings.Said("git_merge.abort_text"), [context.Strings.Said("git_merge.abort_no"), yes], answer =>
        {
            if (answer != yes)
                return;

            Run(repository, yes, () => repository.Git.Abort(operation), context.Strings.Said("git_merge.aborted", ("doing", yes)));
        });
    }

    /// <summary>
    /// Carries on once the conflicts are settled, a merge with the commit box (git's MERGE_MSG in it), a rebase, a
    /// cherry-pick or a revert with git's --continue.
    /// </summary>
    private void ContinueOperation(Repository? repository)
    {
        if (repository is not { Status: { } status, Operation: not GitOperation.None and var operation })
        {
            context.Notices.Say(context.Strings.Said("git_merge.nothing"));
            return;
        }

        if (operation == GitOperation.Merging)
        {
            AskCommit(repository);
            return;
        }

        int conflicts = status.Files.Values.Count(state => state.Change == GitChange.Conflicted);
        if (conflicts > 0)
        {
            context.Notices.Say(context.Strings.Said("git.resolve_first", ("count", HexText.Count("file", conflicts))), error: true);
            return;
        }

        string doing = continueItem.Label;
        Run(repository, doing, () => repository.Git.Continue(operation), context.Strings.Said("git.remote_done", ("doing", doing)));
    }
}
