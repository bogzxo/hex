using Hex.Core.Git;

namespace Hex.Dawn;

// The remotes. Fetch, pull, push and sync (a pull and then a push) from the Git menu and from the item next to the branch
// in the status bar, which says how far the branch is behind and ahead of what it follows, or offers to publish a branch
// that follows nothing. What the remotes have is fetched in the background every few minutes too, so behind is behind
// without anybody asking. All of it is git itself, with the user's credentials (Git Credential Manager asks in a window
// of its own when it has to), and what goes wrong comes up on a card with all of git's words a button away. How often
// it fetches, how a pull pulls and whether a sync asks first are the settings' Editor · Git.
internal sealed partial class HexGit
{
    private float fetchIn;
    private bool syncing;

    // How often the workspace's repositories are fetched in the background, in seconds, while the setting says so
    private float FetchEvery => context.Settings.Values.GitAutoFetchInterval * 60.0f;

    private void BuildSync()
    {
        fetchIn = FetchEvery;
        context.Commands.Add("Git/menus/remote:sync", context.Strings.Said("git.fetch"), () => Remote(ActiveRepository(), RemoteWork.Fetch), icon: "cloud-download");
        context.Commands.Add("Git/menus/remote:sync", context.Strings.Said("git.pull"), () => Remote(ActiveRepository(), RemoteWork.Pull), icon: "arrow-down");
        context.Commands.Add("Git/menus/remote:sync", context.Strings.Said("git.pull_rebase"), () => Remote(ActiveRepository(), RemoteWork.Pull, pullMode: "rebase"), icon: "arrow-down");
        context.Commands.Add("Git/menus/remote:sync", context.Strings.Said("git.push"), () => Remote(ActiveRepository(), RemoteWork.Push), icon: "arrow-up");
        context.Commands.Add("Git/menus/remote:sync", context.Strings.Said("git.sync"), () => AskSync(ActiveRepository()), icon: "sync");
        context.Commands.Add("Git/menus/remote:publish", context.Strings.Said("git.publish"), () => Remote(ActiveRepository(), RemoteWork.Push), icon: "cloud-upload").IsEnabled =
            () => ActiveRepository() is { HasRemote: true, Status: { Branch: not null, Upstream: null } };
        context.Commands.Add("Git/settings", context.Strings.Said("git.settings_command"), () => context.Windows.OpenSettings(EditorSettingsCatalog.GIT), icon: "settings-gear");
    }

    private enum RemoteWork { Fetch, Pull, Push, Sync }

    /// <summary>
    /// What the status bar's item next to the branch says for the active repository, behind and ahead for a branch that
    /// follows one, publish for one that doesn't, empty with no remote (or no repository).
    /// </summary>
    public string SyncText()
    {
        if (ActiveRepository() is not { HasRemote: true, Status: { Branch: not null } status })
            return string.Empty;
        if (syncing)
            return "$(sync) " + context.Strings.Said("git.syncing");
        if (status.Upstream is null)
            return "$(cloud-upload) " + context.Strings.Said("git.publish");
        return status.Ahead > 0 || status.Behind > 0 ? $"$(sync) {status.Behind}$(arrow-down) {status.Ahead}$(arrow-up)" : "$(sync)";
    }

    /// <summary>The status bar's sync item pressed, a sync of the active repository, or a publish for a branch that follows nothing.</summary>
    public void SyncPressed() => SyncRepository(ActiveRepository());

    // A sync, or a publish for a branch that follows nothing, the status bar's sync item and a heading's button
    private void SyncRepository(Repository? repository)
    {
        if (repository?.Status is not { } status)
            return;

        if (status.Upstream is null)
            Remote(repository, RemoteWork.Push);
        else
            AskSync(repository);
    }

    /// <summary>
    /// A sync, asked about first while the setting says so (VS Code's git.confirmSync), how many commits come and go and
    /// with where. Always says yes and stops asking.
    /// </summary>
    private void AskSync(Repository? repository)
    {
        if (repository?.Status is not { } status || syncing)
            return;

        if (!context.Settings.Values.GitConfirmSync || !repository.HasRemote || status.Upstream is null)
        {
            Remote(repository, RemoteWork.Sync);
            return;
        }

        string sync = context.Strings.Said("git.sync"), always = context.Strings.Said("git.sync_always");
        context.Windows.Ask(context.Strings.Said("git.sync_title"),
            context.Strings.Said("git.sync_text", ("behind", status.Behind.ToString()), ("ahead", status.Ahead.ToString()), ("upstream", status.Upstream)),
            [sync, always, context.Strings.Said("git.sync_cancel")], answer =>
            {
                if (answer == always)
                {
                    context.Settings.Values.GitConfirmSync = false;
                    context.Settings.Save();
                    context.Settings.Refresh("git_confirm_sync");

                    // Said where it went, so it can be found to have it ask again
                    context.Notices.Notify(context.Strings.Said("git_merge.sync_always_set"), false,
                        (context.Strings.Said("git_merge.git_settings"), () => context.Windows.OpenSettings(EditorSettingsCatalog.GIT)));
                }
                else if (answer != sync)
                {
                    return;
                }

                Remote(repository, RemoteWork.Sync);
            });
    }

    /// <summary>Every tick, the workspace's repositories fetched in the background now and again.</summary>
    private void UpdateSync(float dt)
    {
        // Brought in at once when the setting is turned down, rather than waiting out the longer time it was
        float every = FetchEvery;
        fetchIn = Math.Min(fetchIn, every);
        if ((fetchIn -= dt) > 0.0f)
            return;

        fetchIn = every;
        if (!context.Settings.Values.GitAutoFetch || syncing || context.Options.SelfTest)
            return;

        foreach (Repository repository in WorkspaceRepositories())
        {
            if (repository.HasRemote)
                context.Work.InBackground("git fetch", repository.Git.FetchUnattended, _ => repository.Dirty(now: true), quiet: true);
        }
    }

    // A pull is the way the setting says, unless it was asked for another way this once (Pull (Rebase))
    private void Remote(Repository? repository, RemoteWork work, string? pullMode = null)
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
        string mode = pullMode ?? context.Settings.Values.GitPullMode;
        string pull = GitRepository.PullFlag(mode) is { } flag ? $"git pull {flag}" : "git pull";
        string doing = work switch
        {
            RemoteWork.Fetch => "git fetch",
            RemoteWork.Pull => pull,
            RemoteWork.Push => upstream ? "git push" : $"git push --set-upstream {branch}",
            _ => $"{pull}, git push"
        };

        syncing = true;
        Version++;
        context.Redraw();
        context.Work.InBackground(doing, () => work switch
        {
            RemoteWork.Fetch => repository.Git.Fetch(),
            RemoteWork.Pull => repository.Git.Pull(mode),
            RemoteWork.Push => repository.Git.Push(branch, upstream),
            _ => repository.Git.Pull(mode) ?? repository.Git.Push(branch, upstream)
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

            // A pull refused for changes in the way can stash them around it, a push the remote turned away needs a pull first
            var actions = new List<(string Label, Action Pressed)>();
            if (work is RemoteWork.Pull or RemoteWork.Sync && LocalChangesInTheWay(failed))
                actions.Add((context.Strings.Said("git_stash.stash_and_pull"), () => StashAndPull(repository)));
            if (work is RemoteWork.Push or RemoteWork.Sync && PushRejected(failed))
                actions.Add((context.Strings.Said("git.pull"), () => Remote(repository, RemoteWork.Pull)));
            RemoteFailed(doing, context.Strings.Said("git.failed", ("doing", doing), ("why", failed.Split('\n')[0].Trim())), failed, actions);
        });
    }

    // Whether the remote turned a push away for having commits the branch hasn't
    private static bool PushRejected(string said) =>
        said.Contains("[rejected]", StringComparison.Ordinal) || said.Contains("non-fast-forward", StringComparison.Ordinal) || said.Contains("fetch first", StringComparison.Ordinal);

    // The card of a remote's failure, its own buttons first and all of git's words a button away
    private void RemoteFailed(string doing, string message, string failed, List<(string Label, Action Pressed)> actions)
    {
        actions.Add((context.Strings.Said("git.details"), () => context.Windows.ShowText(doing, $"```\n{failed}\n```\n")));
        FailureCard(message, error: true, [.. actions]);
    }

    /// <summary>
    /// What's changed stashed, the files nobody added among it, the branch pulled, and the stash popped back. Changes that
    /// don't come back cleanly stay in the stash, and the card says so.
    /// </summary>
    private void StashAndPull(Repository repository)
    {
        if (repository.Status is null || syncing)
            return;

        ClearFailure();
        string mode = context.Settings.Values.GitPullMode;
        string doing = GitRepository.PullFlag(mode) is { } flag ? $"git stash, git pull {flag}, git stash pop" : "git stash, git pull, git stash pop";
        string message = context.Strings.Said("git_stash.pull_message");

        syncing = true;
        Version++;
        context.Redraw();
        context.Work.InBackground(doing, (string? Failed, string? NotBack) () =>
        {
            if (repository.Git.PushStash(message, includeUntracked: true) is { } stashFailed)
                return (stashFailed, null);

            string? pulled = repository.Git.Pull(mode);
            return (pulled, repository.Git.Pop(0));
        }, result =>
        {
            syncing = false;
            repository.Dirty(now: true);
            Version++;
            if (result is { Failed: null, NotBack: null })
            {
                ClearFailure();
                context.Notices.Say(context.Strings.Said("git_stash.pulled_back"));
                return;
            }

            string all = string.Join("\n\n", new[] { result.Failed, result.NotBack }.Where(said => said is not null));
            string why = (result.Failed ?? result.NotBack)!.Split('\n')[0].Trim();
            string card = result.NotBack is not null
                ? context.Strings.Said("git_stash.not_back", ("doing", doing), ("stash", "stash@{0}"), ("why", why))
                : context.Strings.Said("git.failed", ("doing", doing), ("why", why));
            RemoteFailed(doing, card, all, []);
        });
    }
}
