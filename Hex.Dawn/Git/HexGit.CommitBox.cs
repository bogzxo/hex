using System.Numerics;

using Hex.Core.Git;

namespace Hex.Dawn;

// What the box over the Source Control side asks of git, VS Code's message box and the Commit button under it. The box
// is the active repository's, it says which when there's more than one, and its button says what Commit would take.
// With no repository at all the side says so with the two ways of having one under it, making one of the workspace's
// folder and cloning one.
internal sealed partial class HexGit
{
    /// <summary>The text of the box over the side by the root of the repository it's for, null when it's for another or empty.</summary>
    public Func<string, string?>? BoxMessage { get; set; }

    /// <summary>A commit went in, by the root of its repository, so the box it was typed in is emptied.</summary>
    public event Action<string>? Committed;

    /// <summary>A folder opened in the editor in place of the workspace, a repository just cloned.</summary>
    public Action<string>? OpenFolder { get; set; }

    /// <summary>
    /// What the box is for right now, the active repository's root, what its Commit says, whether that would do anything
    /// and what the box says while it's empty. Null when there's no repository to commit to.
    /// </summary>
    internal (string Root, string Button, bool Ready, string Placeholder)? CommitBox()
    {
        if (!Available || ActiveRepository() is not { Status: { } status } repository)
            return null;

        var (button, ready) = CommitLabel(repository, status);
        string branch = BranchName(status);
        string placeholder = Several
            ? context.Strings.Said("git.box_placeholder_in", ("repository", repository.Name), ("branch", branch))
            : context.Strings.Said("git.box_placeholder", ("branch", branch));
        return (repository.Git.Root, button, ready, placeholder);
    }

    /// <summary>The box's Commit, the active repository's, with what's typed or the dialog asking when nothing is.</summary>
    internal void CommitFromBox() => AskCommit(ActiveRepository());

    /// <summary>The box's menu of the other ways of committing, Commit and Push and the rest.</summary>
    internal void BoxMenu()
    {
        if (ActiveRepository() is { } repository)
            CommitMenu(repository);
    }

    /// <summary>
    /// What Commit says for a repository and whether it would do anything. With nothing staged and the setting saying
    /// never, Commit stages nothing for you and says so. A merge's Commit finishes it whatever is staged, so it says no
    /// more than that, and with nothing changed there's no count of nothing to give, only a dim Commit.
    /// </summary>
    private (string Text, bool Ready) CommitLabel(Repository repository, GitStatus status)
    {
        int staged = 0, count = 0;
        foreach (var (path, state) in status.Files)
        {
            if (!Listed(path, state))
                continue;
            if (state.Change == GitChange.Conflicted || state.Staged || state.Unstaged)
                count++;
            if (state.Staged && state.Change != GitChange.Conflicted)
                staged++;
        }

        bool merge = MergeOf(repository) is not null;
        bool never = staged == 0 && context.Settings.Values.GitSmartCommit == "never";
        string text = merge ? context.Strings.Said("git.commit_merge")
            : count == 0 ? context.Strings.Said("git.commit_plain")
            : staged > 0 ? context.Strings.Said("git.commit_staged", ("count", staged.ToString()))
            : never ? context.Strings.Said("git.commit_nothing_staged")
            : context.Strings.Said("git.commit_all", ("count", count.ToString()));
        return (text, merge || count > 0 && !never);
    }

    /* No repository yet */

    // The note and the two buttons under it, VS Code's Initialize Repository and Clone Repository
    private void AddNoRepository(List<UITreeItem> items)
    {
        items.Add(Note(context.Strings.Said("git.no_repository")));
        Vector4? accent = AccentOf?.Invoke();
        if (context.Workspace.Roots.Count > 0)
            items.Add(HexNotes.Link(context.Strings.Said("git.init_button"), "add", accent, InitRepository));
        items.Add(HexNotes.Link(context.Strings.Said("git.clone_button"), "repo-clone", accent, AskClone));
    }

    /// <summary>A repository made of the workspace's first folder, git init in it.</summary>
    private void InitRepository()
    {
        if (context.Workspace.Roots.Count == 0)
            return;

        string folder = context.Workspace.Roots[0];
        context.Work.InBackground("git init", () => GitRepository.Init(folder), failed =>
        {
            if (failed is not null)
            {
                FailureCard(context.Strings.Said("git.failed", ("doing", "git init"), ("why", failed.Split('\n')[0].Trim())), error: true);
                return;
            }

            ClearFailure();
            RepositoryOf(folder);
            context.Notices.Say(context.Strings.Said("git.initialised", ("folder", Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)))));
            context.Panels.RefreshSide(HexSide.SourceControl);
        }, quiet: true);
    }

    /// <summary>A repository cloned, its address asked for, then the folder to put it in, and opened once it's there.</summary>
    private void AskClone()
    {
        context.Windows.Prompt(context.Strings.Said("git.clone_title"), context.Strings.Said("git.clone_text"), string.Empty,
            context.Strings.Said("git.clone_go"), context.Strings.Said("git.clone_placeholder"), typed =>
            {
                string url = typed.Trim();
                if (url.Length == 0)
                    return;

                context.Windows.ChooseFolder(parent =>
                {
                    string into = Path.Combine(parent, GitRepository.CloneName(url));
                    context.Work.InBackground(context.Strings.Said("git.cloning", ("url", url)), () => GitRepository.Clone(url, into), failed =>
                    {
                        if (failed is not null)
                        {
                            FailureCard(context.Strings.Said("git.failed", ("doing", "git clone"), ("why", failed.Split('\n')[0].Trim())), error: true,
                                (context.Strings.Said("git.details"), () => context.Windows.ShowText("git clone", $"```\n{failed}\n```\n")));
                            return;
                        }

                        ClearFailure();
                        context.Notices.Notify(context.Strings.Said("git.cloned", ("folder", into)), false,
                            (context.Strings.Said("git.clone_open"), () => OpenFolder?.Invoke(into)));
                    });
                });
            });
    }
}
