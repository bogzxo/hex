using Hex.Core.Git;

namespace Hex.Dawn;

// Branches, the one checked out said in the status bar with how far it is from what it follows, and a click on it the
// list of every branch to switch to, a new one to make first. Switching is git's own switch, so a working tree with
// changes that would be lost is refused the way the terminal would refuse it, and what git said is put on a card.
internal sealed partial class HexGit
{
    /// <summary>
    /// What the status bar says for the repository a path is in, the branch (or the commit HEAD is on) and how many commits
    /// it's ahead and behind, empty for no repository.
    /// </summary>
    public string BranchText(string? path)
    {
        if (StatusAt(path) is not { } status)
            return string.Empty;

        string name = status.Branch ?? (status.Commit is { } commit ? commit[..Math.Min(8, commit.Length)] : "?");
        // How far it is from what it follows is the sync item's to say, next to it
        return $"$(git-branch) {name}";
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
            var choices = new List<(string Text, Action Picked)>
            {
                (context.Strings.Said("git.new_branch"), () => AskNewBranch(repository))
            };

            // A remote branch that has a local one of its own isn't listed, picking it only ever said the local one exists
            var local = new HashSet<string>(branches.Where(branch => !branch.Remote).SelectMany(branch => new[] { branch.Name, branch.Upstream ?? string.Empty }), StringComparer.Ordinal);
            foreach (GitBranch branch in branches)
            {
                if (branch.Remote && (local.Contains(branch.Name) || local.Contains(branch.Name[(branch.Name.IndexOf('/') + 1)..])))
                    continue;

                string where = branch.Current ? context.Strings.Said("git.checked_out") : branch.Remote ? context.Strings.Said("git.remote") : branch.When;
                choices.Add(($"{where}: {branch.Name}   {branch.Subject}", () =>
                {
                    if (!branch.Current)
                        Switch(repository, branch);
                }));
            }

            context.Windows.Choose(context.Strings.Said("git.branches_title"), context.Strings.Said("git.branches_text", ("branch", status.Branch ?? "?")), choices);
        }, quiet: true);
    }

    private void Switch(Repository repository, GitBranch branch)
    {
        context.Work.InBackground("git switch", () => repository.Git.Switch(branch), failed => Switched(repository, branch.Name, failed), quiet: true);
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

    // What came of a switch, said, and git asked again either way
    private void Switched(Repository repository, string name, string? failed)
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
        FailureCard(context.Strings.Said("git.switch_failed", ("branch", name), ("why", first)), error: true,
            (context.Strings.Said("git.details"), () => context.Windows.ShowText(context.Strings.Said("git.switch_details", ("branch", name)), $"```\n{failed}\n```\n")));
    }
}
