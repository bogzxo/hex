using Hex.Core.Git;

namespace Hex.Dawn;

// The branch work VS Code's Git menu has for every day, merging a branch in, rebasing onto one, deleting and renaming,
// tags made and deleted, a commit picked over from another branch or taken back. Each picks what it works on from a list
// whose branch rows look the same as the Switch Branch list's, and commits the same as a file's history. A merge, a
// rebase, a cherry-pick or a revert that stops on conflicts is the merge state the rest of Hex already knows, the
// Merging row on the side and Abort and Continue in the Git menu, and the card says how many there are to settle.
internal sealed partial class HexGit
{
    private void BuildBranchOps()
    {
        context.Commands.Add("Git/menus/branches:change", context.Strings.Said("git_branch.merge_command"), () => PickBranch(ActiveRepository(), "git_branch.merge_title", remotes: true, MergeBranch), icon: "git-merge");
        context.Commands.Add("Git/menus/branches:change", context.Strings.Said("git_branch.rebase_command"), () => PickBranch(ActiveRepository(), "git_branch.rebase_title", remotes: true, RebaseOnto), icon: "git-pull-request");
        context.Commands.Add("Git/menus/branches:switch", context.Strings.Said("git_branch.rename_command"), () => AskRename(ActiveRepository()), icon: "edit");
        context.Commands.Add("Git/menus/branches:switch", context.Strings.Said("git_branch.delete_command"), () => PickBranch(ActiveRepository(), "git_branch.delete_title", remotes: false, DeleteBranch), icon: "trash");
        context.Commands.Add("Git/menus/tags", context.Strings.Said("git_branch.tag_command"), () => AskTag(ActiveRepository()), icon: "tag");
        context.Commands.Add("Git/menus/tags", context.Strings.Said("git_branch.delete_tag_command"), () => PickTag(ActiveRepository()), icon: "trash");
        context.Commands.Add("Git/menus/branches:commits", context.Strings.Said("git_branch.cherry_pick_command"), () => PickBranch(ActiveRepository(), "git_branch.cherry_pick_title", remotes: true, PickToCherryPick), icon: "git-commit");
        context.Commands.Add("Git/menus/branches:commits", context.Strings.Said("git_branch.revert_command"), () => PickCommit(ActiveRepository(), "HEAD", "git_branch.revert_title", Revert), icon: "discard");
    }

    /// <summary>
    /// The branches to pick one from, all but the one checked out, local ones first then the remotes' when they're any use.
    /// Rows the same as the Switch Branch list's.
    /// </summary>
    private void PickBranch(Repository? repository, string title, bool remotes, Action<Repository, GitBranch> picked)
    {
        if (repository?.Status is not { } status)
            return;

        context.Work.InBackground("git branches", repository.Git.Branches, branches =>
        {
            var choices = new List<UIChoice>();
            foreach (GitBranch branch in branches)
            {
                if (branch.Current || branch.Remote && !remotes)
                    continue;

                string where = branch.Remote ? context.Strings.Said("git.remote") : branch.When;
                choices.Add(new UIChoice(branch.Name, () => picked(repository, branch))
                {
                    Icon = branch.Remote ? "cloud" : "git-branch",
                    Segments = BranchRow(branch, where),
                    Id = $"{where}: {branch.Name}   {branch.Subject}"
                });
            }

            if (choices.Count == 0)
            {
                context.Notices.Say(context.Strings.Said("git_branch.no_branches"));
                return;
            }

            context.Windows.Choose(context.Strings.Said(title), context.Strings.Said("git.branches_text", ("branch", status.Branch ?? "?")), choices);
        }, quiet: true);
    }

    /// <summary>The commits of a revision to pick one from, rows the same as a file's history.</summary>
    private void PickCommit(Repository? repository, string revision, string title, Action<Repository, GitLogCommit> picked)
    {
        if (repository is null)
            return;

        context.Work.InBackground("git log", () => repository.Git.Log(revision), log =>
        {
            if (log.Count == 0)
            {
                context.Notices.Say(context.Strings.Said("git_branch.no_commits"));
                return;
            }

            var choices = new List<UIChoice>();
            foreach (GitLogCommit commit in log)
            {
                string ago = Ago(commit.When);
                choices.Add(new UIChoice(commit.Subject, () => picked(repository, commit))
                {
                    Icon = "git-commit",
                    Segments = [(commit.Short, UIChoiceTone.Hash), (commit.Subject, UIChoiceTone.Primary), (commit.Author, UIChoiceTone.Author), (ago, UIChoiceTone.Date)],
                    Id = $"{commit.Short} {commit.Author}, {ago}: {commit.Subject}"
                });
            }

            context.Windows.Choose(context.Strings.Said(title), context.Strings.Said("git_branch.commit_text"), choices);
        }, quiet: true);
    }

    private void MergeBranch(Repository repository, GitBranch branch) =>
        BranchWork(repository, $"git merge {branch.Name}", () => repository.Git.MergeBranch(branch.Name), context.Strings.Said("git_branch.merged", ("branch", branch.Name)));

    private void RebaseOnto(Repository repository, GitBranch branch) =>
        BranchWork(repository, $"git rebase {branch.Name}", () => repository.Git.RebaseOnto(branch.Name), context.Strings.Said("git_branch.rebased", ("branch", branch.Name)));

    // A branch git won't delete for not being merged anywhere asks whether to delete it anyway, its commits lost with it
    private void DeleteBranch(Repository repository, GitBranch branch)
    {
        string name = branch.Name;
        BranchWork(repository, $"git branch -d {name}", () => repository.Git.DeleteBranch(name, force: false), context.Strings.Said("git_branch.deleted", ("branch", name)), failed =>
        {
            if (!failed.Contains("not fully merged", StringComparison.Ordinal))
                return false;

            string force = context.Strings.Said("git_branch.force_delete");
            context.Windows.Ask(context.Strings.Said("git_branch.unmerged_title", ("branch", name)), context.Strings.Said("git_branch.unmerged_text", ("branch", name)),
                [context.Strings.Said("git_branch.cancel"), force], risky: force, answered: answer =>
                {
                    if (answer == force)
                        BranchWork(repository, $"git branch -D {name}", () => repository.Git.DeleteBranch(name, force: true), context.Strings.Said("git_branch.deleted", ("branch", name)));
                });
            return true;
        });
    }

    /// <summary>The branch checked out renamed, the box starting with the name it has.</summary>
    private void AskRename(Repository? repository)
    {
        if (repository?.Status is not { } status)
            return;
        if (status.Branch is not { } current)
        {
            context.Notices.Say(context.Strings.Said("git_branch.no_branch"));
            return;
        }

        context.Windows.Prompt(context.Strings.Said("git_branch.rename_title", ("branch", current)), context.Strings.Said("git_branch.rename_text"), current,
            context.Strings.Said("git_branch.rename_button"), "feature/name", typed =>
            {
                string name = typed.Trim();
                if (name.Length == 0 || name == current)
                    return;
                BranchWork(repository, $"git branch -m {name}", () => repository.Git.RenameBranch(current, name), context.Strings.Said("git_branch.renamed", ("branch", name)));
            });
    }

    /// <summary>A tag where HEAD is, its name asked for and then a message, none for a lightweight tag.</summary>
    private void AskTag(Repository? repository)
    {
        if (repository is null)
            return;

        context.Windows.Prompt(context.Strings.Said("git_branch.tag_title"), context.Strings.Said("git_branch.tag_text"), string.Empty,
            context.Strings.Said("git_branch.tag_next"), "v1.0.0", typed =>
            {
                string name = typed.Trim();
                if (name.Length == 0)
                    return;

                context.Windows.Prompt(context.Strings.Said("git_branch.tag_message_title", ("tag", name)), context.Strings.Said("git_branch.tag_message_text"), string.Empty,
                    context.Strings.Said("git_branch.tag_button"), context.Strings.Said("git_branch.tag_message_placeholder"), message =>
                        BranchWork(repository, $"git tag {name}", () => repository.Git.CreateTag(name, message.Trim()), context.Strings.Said("git_branch.tagged", ("tag", name))));
            });
    }

    /// <summary>The tags to pick one to delete from, the newest first.</summary>
    private void PickTag(Repository? repository)
    {
        if (repository is null)
            return;

        context.Work.InBackground("git tags", repository.Git.Tags, tags =>
        {
            if (tags.Count == 0)
            {
                context.Notices.Say(context.Strings.Said("git_branch.no_tags"));
                return;
            }

            var choices = new List<UIChoice>();
            foreach (GitTag tag in tags)
            {
                string name = tag.Name;
                choices.Add(new UIChoice(name, () =>
                    BranchWork(repository, $"git tag -d {name}", () => repository.Git.DeleteTag(name), context.Strings.Said("git_branch.tag_deleted", ("tag", name))))
                {
                    Icon = "tag",
                    Segments = [(name, UIChoiceTone.Primary), (tag.When, UIChoiceTone.Date), (tag.Subject, UIChoiceTone.Dim)],
                    Id = $"{tag.When}: {name}   {tag.Subject}"
                });
            }

            context.Windows.Choose(context.Strings.Said("git_branch.delete_tag_title"), context.Strings.Said("git_branch.tag_list_text"), choices);
        }, quiet: true);
    }

    // A branch picked to cherry-pick from lists what it has that the branch checked out hasn't
    private void PickToCherryPick(Repository repository, GitBranch branch) =>
        PickCommit(repository, $"HEAD..{branch.Name}", "git_branch.cherry_pick_commit_title", CherryPick);

    private void CherryPick(Repository repository, GitLogCommit commit) =>
        BranchWork(repository, $"git cherry-pick {commit.Short}", () => repository.Git.CherryPick(commit.Hash), context.Strings.Said("git_branch.cherry_picked", ("subject", commit.Subject)));

    private void Revert(Repository repository, GitLogCommit commit) =>
        BranchWork(repository, $"git revert {commit.Short}", () => repository.Git.Revert(commit.Hash), context.Strings.Said("git_branch.reverted", ("subject", commit.Subject)));

    /// <summary>
    /// Runs one of them in the background and says what came of it. Stopped on conflicts it's the merge state, and the card
    /// says how many there are. Otherwise what went wrong goes on the card with all of git's words a button away, unless
    /// <paramref name="refused"/> takes it (a branch not fully merged asking to be forced).
    /// </summary>
    private void BranchWork(Repository repository, string doing, Func<string?> work, string done, Func<string, bool>? refused = null)
    {
        context.Work.InBackground(doing, () =>
        {
            string? failed = work();
            GitOperation operation = failed is null ? GitOperation.None : repository.Git.Operation();
            return (Failed: failed, Operation: operation, Conflicts: operation == GitOperation.None ? 0 : repository.Git.ConflictCount());
        }, result =>
        {
            repository.Dirty(now: true);
            if (result.Failed is not { } failed)
            {
                ClearFailure();
                context.Notices.Say(done);
                return;
            }

            if (result.Operation != GitOperation.None)
            {
                FailureCard(context.Strings.Said("git_branch.conflicts", ("doing", doing), ("count", HexText.Count("conflict", result.Conflicts))), error: false,
                    (context.Strings.Said("git.details"), () => context.Windows.ShowText(doing, $"```text\n{failed}\n```\n")));
                return;
            }

            if (refused?.Invoke(failed) == true)
            {
                ClearFailure();
                return;
            }

            FailureCard(context.Strings.Said("git.failed", ("doing", doing), ("why", failed.Split('\n')[0].Trim())), error: true,
                (context.Strings.Said("git.details"), () => context.Windows.ShowText(doing, $"```text\n{failed}\n```\n")));
        }, quiet: true);
    }
}
