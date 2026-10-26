using Hex.Core.Git;

namespace Hex.Dawn;

// Branches, the one checked out said in the status bar with how far it is from what it follows, and a click on it the
// list of every branch to switch to, a new one to make first. Switching is git's own switch, so a working tree with
// changes that would be lost is refused the way the terminal would refuse it, and what git said is put on a card.
internal sealed partial class HexGit
{
    /// <summary>
    /// What the status bar says for the repository a path is in, the branch (or the commit HEAD is on) and how many commits
    /// it's ahead and behind, empty for no repository. A star after it while anything's changed (VS Code's), and what git is
    /// in the middle of after that, (Merging) or (Rebasing).
    /// </summary>
    public string BranchText(string? path)
    {
        if (StatusAt(path) is not { } status)
            return string.Empty;

        string name = status.Branch ?? (status.Commit is { } commit ? commit[..Math.Min(8, commit.Length)] : "?");
        Repository? repository = RepositoryOf(status.Root);
        string star = repository is { HasChanges: true } ? "*" : string.Empty;
        string doing = repository is { Operation: not GitOperation.None and var operation } ? $" ({OperationWord(operation)})" : string.Empty;

        // How far it is from what it follows is the sync item's to say, next to it
        return $"$(git-branch) {name}{star}{doing}";
    }

    // The repository of a path, or of the workspace's first folder that has one when there's no path
    private GitStatus? StatusAt(string? path)
    {
        if (path is not null && StatusFor(path) is { } status)
            return status;

        foreach (string root in context.Workspace.Roots)
        {
            if (StatusFor(root) is { } rooted)
                return rooted;
        }

        return null;
    }

    /// <summary>The branches of the repository a path is in to pick one from, a new one to make at the top.</summary>
    public void ShowBranches(string? path)
    {
        if (StatusAt(path) is not { } status || RepositoryOf(status.Root) is not { } repository)
            return;

        context.Work.InBackground("git branches", repository.Git.Branches, branches =>
        {
            var choices = new List<UIChoice>
            {
                new(context.Strings.Said("git.new_branch"), () => AskNewBranch(repository)) { Icon = "add" }
            };

            // A remote branch that has a local one of its own isn't listed, picking it only ever said the local one exists
            var local = new HashSet<string>(branches.Where(branch => !branch.Remote).SelectMany(branch => new[] { branch.Name, branch.Upstream ?? string.Empty }), StringComparer.Ordinal);
            foreach (GitBranch branch in branches)
            {
                if (branch.Remote && (local.Contains(branch.Name) || local.Contains(branch.Name[(branch.Name.IndexOf('/') + 1)..])))
                    continue;

                string where = branch.Current ? context.Strings.Said("git.checked_out") : branch.Remote ? context.Strings.Said("git.remote") : branch.When;
                choices.Add(new UIChoice(branch.Name, () =>
                {
                    if (!branch.Current)
                        Switch(repository, branch);
                })
                {
                    Icon = branch.Current ? "check" : branch.Remote ? "cloud" : "git-branch",
                    Segments = BranchRow(branch, where),
                    Id = $"{where}: {branch.Name}   {branch.Subject}"
                });
            }

            context.Windows.Choose(context.Strings.Said("git.branches_title"), context.Strings.Said("git.branches_text", ("branch", status.Branch ?? "?")), choices);
        }, quiet: true);
    }

    // A branch's row, its name bright, the one checked out said so in the accent, what it follows dim, when it last
    // moved in a date's colour and its last commit's subject quiet at the end. Every list of branches has them, Merge
    // Branch and the rest of BranchOps too, so a branch looks the same wherever it's picked
    internal static List<(string Text, UIChoiceTone Tone)> BranchRow(GitBranch branch, string where)
    {
        var row = new List<(string Text, UIChoiceTone Tone)> { (branch.Name, UIChoiceTone.Primary) };
        if (branch.Current)
            row.Add((where, UIChoiceTone.Accent));
        else if (branch.Remote)
            row.Add((where, UIChoiceTone.Dim));
        if (branch.Upstream is { Length: > 0 } upstream)
            row.Add((upstream, UIChoiceTone.Dim));
        if (!branch.Remote && branch.When.Length > 0)
            row.Add((branch.When, UIChoiceTone.Date));
        row.Add((branch.Subject, UIChoiceTone.Dim));
        return row;
    }

    private void Switch(Repository repository, GitBranch branch)
    {
        context.Work.InBackground("git switch", () => repository.Git.Switch(branch), failed => Switched(repository, branch.Name, failed, () => repository.Git.Switch(branch)), quiet: true);
    }

    private void AskNewBranch(Repository repository)
    {
        context.Windows.Prompt(context.Strings.Said("git.new_branch_title"), context.Strings.Said("git.new_branch_text"), string.Empty,
            context.Strings.Said("git.new_branch_button"), "feature/name", typed =>
            {
                string name = typed.Trim();
                if (name.Length == 0)
                    return;
                context.Work.InBackground("git switch -c", () => repository.Git.CreateBranch(name), failed => Switched(repository, name, failed), quiet: true);
            });
    }

    // What came of a switch, said, and git asked again either way. A switch refused for the changes it would write over
    // offers to stash them first and switch again, which is what switching again is for
    private void Switched(Repository repository, string name, string? failed, Func<string?>? again = null)
    {
        repository.Dirty(now: true);
        if (failed is null)
        {
            ClearFailure();
            context.Notices.Say(context.Strings.Said("git.switched", ("branch", name)));
            return;
        }

        // The card has git's first line, which fits it, and the whole of it (the files in the way) a button away
        string first = failed.Split('\n')[0].Trim();
        var actions = new List<(string Label, Action Pressed)>();
        if (again is not null && LocalChangesInTheWay(failed))
            actions.Add((context.Strings.Said("git_stash.stash_and_switch"), () => StashAndSwitch(repository, name, again)));
        actions.Add((context.Strings.Said("git.details"), () => context.Windows.ShowText(context.Strings.Said("git.switch_details", ("branch", name)), $"```text\n{failed}\n```\n")));
        FailureCard(context.Strings.Said("git.switch_failed", ("branch", name), ("why", first)), error: true, [.. actions]);
    }

    // Whether git refused because changes in the working tree would be written over or are in the way
    private static bool LocalChangesInTheWay(string said) =>
        said.Contains("would be overwritten", StringComparison.Ordinal) || said.Contains("You have unstaged changes", StringComparison.Ordinal)
        || said.Contains("Your index contains uncommitted changes", StringComparison.Ordinal);

    // Everything changed put in a stash, the files nobody added too, then the switch again. A switch that still goes wrong
    // has the changes popped back, so nothing's left stashed for a switch that never happened
    private void StashAndSwitch(Repository repository, string name, Func<string?> again)
    {
        ClearFailure();
        string message = context.Strings.Said("git_stash.switch_message", ("branch", name));
        context.Work.InBackground("git stash, git switch", string? () =>
        {
            if (repository.Git.PushStash(message, includeUntracked: true) is { } stashFailed)
                return stashFailed;
            if (again() is { } switchFailed)
            {
                repository.Git.Pop(0);
                return switchFailed;
            }

            return null;
        }, failed =>
        {
            repository.Dirty(now: true);
            if (failed is not null)
            {
                Switched(repository, name, failed);
                return;
            }

            const string LATEST = "stash@{0}";
            failureCard = context.Notices.Notify(context.Strings.Said("git_stash.switched_stashed", ("branch", name), ("stash", LATEST)), false,
                (context.Strings.Said("git_stash.pop_button"), () => PopStash(repository, 0, LATEST)));
        }, quiet: true);
    }
}
