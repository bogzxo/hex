using Hex.Core.Git;

namespace Hex.Dawn;

// The remotes. Fetch, pull, push and sync (a pull and then a push) from the Git menu and from the item next to the branch
// in the status bar, which says how far the branch is behind and ahead of what it follows, or offers to publish a branch
// that follows nothing. What the remotes have is fetched in the background every few minutes too, so behind is behind
// without anybody asking. All of it is git itself, with the user's credentials (Git Credential Manager asks in a window
// of its own when it has to), and what goes wrong comes up on a card with all of git's words a button away.
internal sealed partial class HexGit
{
    // How often the workspace's repositories are fetched in the background, while the setting says so
    private const float AUTO_FETCH_EVERY = 180.0f;

    private float fetchIn = AUTO_FETCH_EVERY;
    private bool syncing;

    private void BuildSync()
    {
        context.Commands.Add("Git/sync", context.Strings.Said("git.fetch"), () => Remote(FirstRepository(), RemoteWork.Fetch), icon: "cloud-download");
        context.Commands.Add("Git/sync", context.Strings.Said("git.pull"), () => Remote(FirstRepository(), RemoteWork.Pull), icon: "arrow-down");
        context.Commands.Add("Git/sync", context.Strings.Said("git.push"), () => Remote(FirstRepository(), RemoteWork.Push), icon: "arrow-up");
        context.Commands.Add("Git/sync", context.Strings.Said("git.sync"), () => Remote(FirstRepository(), RemoteWork.Sync), icon: "sync");
    }

    private enum RemoteWork { Fetch, Pull, Push, Sync }

    /// <summary>
    /// What the status bar's item next to the branch says, behind and ahead for a branch that follows one, publish for one
    /// that doesn't, empty with no remote (or no repository).
    /// </summary>
    public string SyncText(string? path)
    {
        if (StatusAt(path) is not { Branch: not null } status || RepositoryOf(status.Root) is not { HasRemote: true })
            return string.Empty;
        if (syncing)
            return "$(sync) " + context.Strings.Said("git.syncing");
        if (status.Upstream is null)
            return "$(cloud-upload) " + context.Strings.Said("git.publish");
        return status.Ahead > 0 || status.Behind > 0 ? $"$(sync) {status.Behind}$(arrow-down) {status.Ahead}$(arrow-up)" : "$(sync)";
    }

    /// <summary>The status bar's sync item pressed, a sync, or a publish for a branch that follows nothing.</summary>
    public void SyncPressed(string? path)
    {
        if (StatusAt(path) is { } status && RepositoryOf(status.Root) is { } repository)
            Remote(repository, status.Upstream is null ? RemoteWork.Push : RemoteWork.Sync);
    }

    /// <summary>Every tick, the workspace's repositories fetched in the background now and again.</summary>
    private void UpdateSync(float dt)
    {
        if ((fetchIn -= dt) > 0.0f)
            return;

        fetchIn = AUTO_FETCH_EVERY;
        if (!context.Settings.Values.GitAutoFetch || syncing || context.Options.SelfTest)
            return;

        foreach (Repository repository in WorkspaceRepositories())
        {
            if (repository.HasRemote)
                context.Work.InBackground("git fetch", repository.Git.Fetch, _ => repository.Dirty(now: true), quiet: true);
        }
    }

    private void Remote(Repository? repository, RemoteWork work)
    {
        if (repository?.Status is not { } status || syncing)
            return;

        if (!repository.HasRemote)
        {
            context.Notices.Say(context.Strings.Said("git.no_remote"), error: true);
            return;
        }

        string branch = status.Branch ?? string.Empty;
        bool upstream = status.Upstream is not null;
        string doing = work switch
        {
            RemoteWork.Fetch => "git fetch",
            RemoteWork.Pull => "git pull",
            RemoteWork.Push => upstream ? "git push" : $"git push --set-upstream {branch}",
            _ => "git pull, git push"
        };

        syncing = true;
        Version++;
        context.Redraw();
        context.Work.InBackground(doing, () => work switch
        {
            RemoteWork.Fetch => repository.Git.Fetch(),
            RemoteWork.Pull => repository.Git.Pull(),
            RemoteWork.Push => repository.Git.Push(branch, upstream),
            _ => repository.Git.Pull() ?? repository.Git.Push(branch, upstream)
        }, failed =>
        {
            syncing = false;
            repository.Dirty(now: true);
            Version++;
            if (failed is null)
            {
                ClearFailure();
                context.Notices.Say(context.Strings.Said("git.remote_done", ("doing", doing)));
                return;
            }

            FailureCard(context.Strings.Said("git.failed", ("doing", doing), ("why", failed.Split('\n')[0].Trim())), error: true,
                (context.Strings.Said("git.details"), () => context.Windows.ShowText(doing, $"```\n{failed}\n```\n")));
        });
    }
}
