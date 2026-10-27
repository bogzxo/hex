using System.Numerics;

using Hex.Core.Git;

namespace Hex.Dawn;

// A repository's heading on the Source Control side, when the workspace has more than one. Its name, then its branch
// with a star while anything's changed and how far it is behind and ahead of what it follows, and how many changes it
// has at the right end. Its buttons commit, sync and switch branch in that repository whichever is active, and its right
// click has the rest. The active one is marked down its edge in the accent, and a click on it or on anything under it
// makes it the active one. Closing one is remembered, the side is put together again every time git says something.
// A submodule's row in the repository it's in, and a repository in it nobody added, are a row of their own kind here
// too, a click on one going to its own heading.
internal sealed partial class HexGit
{
    // The repositories whose headings were closed, by root
    private readonly HashSet<string> closedHeadings = new(PlatformPaths.Comparer);

    private UITreeItem Heading(Repository repository, bool active)
    {
        Vector4? accent = active ? AccentOf?.Invoke() : null;
        string root = repository.Git.Root;
        var heading = new UITreeItem(repository.Name, "repo")
        {
            Detail = HeadingDetail(repository),
            DetailMarkup = true,
            Expandable = true,
            Expanded = !closedHeadings.Contains(root),
            Children = [],
            Tooltip = active ? $"{root}\n{context.Strings.Said("git_repos.active_tip")}" : root,
            Edge = accent,
            IconColor = accent,
            Actions = HeadingActions(repository),
            Tag = new SideTag(Pressed: () => MakeActive(repository), Menu: _ => HeadingMenu(repository), Toggled: row =>
            {
                if (row.Expanded) closedHeadings.Remove(root);
                else closedHeadings.Add(root);
            })
        };

        int count = AddRepositoryRows(heading.Children, repository);
        heading.Badge = count > 0 ? count.ToString() : string.Empty;
        Follow(heading.Children, repository);
        return heading;
    }

    // What a heading says after the name, the branch, a star for changes, what git is in the middle of and how far it is
    // from what it follows
    private string HeadingDetail(Repository repository)
    {
        if (repository.Status is not { } status)
            return string.Empty;

        // A checkout of no branch (a submodule, mostly) is the commit it's on, said with git's commit icon so it isn't taken
        // for a branch called that. Behind and ahead in the arrows of the icons, the font has none of its own
        string detail = (status.Branch is null ? "$(git-commit) " : string.Empty) + BranchName(status) + (repository.HasChanges ? "*" : string.Empty);
        if (repository.Operation is not GitOperation.None and var operation)
            detail += $" ({OperationWord(operation)})";
        if (status.Behind > 0 || status.Ahead > 0)
            detail += $"  {status.Behind}$(arrow-down) {status.Ahead}$(arrow-up)";
        else if (repository.HasRemote && status.Branch is not null && status.Upstream is null)
            detail += "  " + context.Strings.Said("git_repos.unpublished");
        return detail;
    }

    // How many changes a repository has on the side, the same count its heading has at its right end
    private int ChangeCount(Repository repository)
    {
        if (repository.Status is not { } status)
            return 0;

        int count = 0;
        foreach (var (path, state) in status.Files)
        {
            if (Listed(path, state) && (state.Staged || state.Unstaged))
                count++;
        }

        return count;
    }

    // A heading's buttons, Commit, Sync (or Publish) while there's a remote, and Switch Branch, each in that repository
    private List<UITreeAction> HeadingActions(Repository repository)
    {
        var actions = new List<UITreeAction>
        {
            new("check", context.Strings.Said("git_repos.commit_here"), () => Active(repository, AskCommit))
        };
        if (repository.HasRemote)
            actions.Add(new UITreeAction("sync", context.Strings.Said("git_repos.sync_here"), () => Active(repository, SyncRepository)));
        actions.Add(new UITreeAction("git-branch", context.Strings.Said("git_repos.branch_here"), () => Active(repository, BranchesOf)));
        return actions;
    }

    // The right click on a heading, what VS Code's has for a repository
    private void HeadingMenu(Repository repository)
    {
        MakeActive(repository);
        var menu = new Menu("git_repository");
        menu.Add(context.Strings.Said("git.commit_button"), () => AskCommit(repository), icon: "check");
        menu.AddSeparator();
        menu.Add(context.Strings.Said("git.fetch"), () => Remote(repository, RemoteWork.Fetch), icon: "cloud-download");
        menu.Add(context.Strings.Said("git.pull"), () => Remote(repository, RemoteWork.Pull), icon: "arrow-down");
        menu.Add(context.Strings.Said("git.push"), () => Remote(repository, RemoteWork.Push), icon: "arrow-up");
        menu.Add(context.Strings.Said("git.sync"), () => SyncRepository(repository), icon: "sync");
        menu.AddSeparator();
        menu.Add(context.Strings.Said("git.branches_command"), () => BranchesOf(repository), icon: "git-branch");
        menu.Add(context.Strings.Said("git_stash.stash"), () => AskStash(repository, includeUntracked: false), icon: "git-stash");
        context.Windows.Menu(menu);
    }

    // Helper method to make a repository the active one before doing something in it
    private void Active(Repository repository, Action<Repository> then)
    {
        MakeActive(repository);
        then(repository);
    }

    /// <summary>
    /// Helper method to have every row under a heading make its repository the active one when it's clicked, right
    /// clicked or one of its buttons is pressed, before doing what it does.
    /// </summary>
    private void Follow(List<UITreeItem> rows, Repository repository)
    {
        foreach (UITreeItem row in rows)
        {
            SideTag tag = row.Tag as SideTag ?? new SideTag();
            Action? pressed = tag.Pressed;
            Action<Vector2>? menu = tag.Menu;
            row.Tag = tag with
            {
                Pressed = () =>
                {
                    MakeActive(repository);
                    pressed?.Invoke();
                },
                Menu = menu is null ? null : point =>
                {
                    MakeActive(repository);
                    menu(point);
                }
            };

            if (row.Actions is { Count: > 0 } actions)
                row.Actions = [.. actions.Select(action => action with { Pressed = () => Active(repository, _ => action.Pressed()) })];
            if (row.Children is { } children)
                Follow(children, repository);
        }
    }

    /* A submodule's row, and a repository's nobody added */

    // A submodule says what moved in it, new commits or changed files, and a repository nobody added says so. Clicking
    // either goes to its own heading, where its own changes are
    private UITreeItem RepositoryRow(Repository repository, string path, GitFileState state, SectionKind kind)
    {
        bool stagedSide = kind == SectionKind.Staged;
        bool module = state.Submodule.HasFlag(GitSubmodule.Module);
        string folder = Path.GetRelativePath(repository.Git.Root, Path.GetDirectoryName(path) ?? repository.Git.Root);
        string where = folder == "." ? string.Empty : folder.Replace('\\', '/');
        string what = SubmoduleWords(state.Submodule);

        var actions = new List<UITreeAction> { new("repo", context.Strings.Said("git_repos.open_repository"), () => FocusRepository(path)) };
        if (module && stagedSide)
            actions.Add(new UITreeAction("remove", context.Strings.Said("git.unstage_changes"), () => StageFiles(repository, [path], false)));
        else if (module)
            actions.Add(new UITreeAction("add", context.Strings.Said("git.stage_changes"), () => StageFiles(repository, [path], true)));

        return new UITreeItem(Path.GetFileName(path), module ? "file-submodule" : "repo")
        {
            Detail = where.Length > 0 ? $"{what}  {where}" : what,
            Color = ColourOf?.Invoke(state.Change),
            Badge = state.Letter,
            BadgeColor = ColourOf?.Invoke(state.Change),
            Tooltip = path,
            Actions = actions,
            Tag = new SideTag(Pressed: () => FocusRepository(path), Menu: _ => RepositoryRowMenu(repository, path, module, stagedSide),
                Key: key => module && key == ' ' && StagedBySpace(repository, path, stagedSide))
        };
    }

    private bool StagedBySpace(Repository repository, string path, bool stagedSide)
    {
        StageFiles(repository, [path], !stagedSide);
        return true;
    }

    // What moved in a submodule in words, the way git status says it, or that a repository is one nobody added
    private string SubmoduleWords(GitSubmodule sub)
    {
        if (sub.HasFlag(GitSubmodule.Nested))
            return context.Strings.Said("git_repos.not_added");

        var words = new List<string>(3);
        if (sub.HasFlag(GitSubmodule.Commits))
            words.Add(context.Strings.Said("git_repos.new_commits"));
        if (sub.HasFlag(GitSubmodule.Modified))
            words.Add(context.Strings.Said("git_repos.modified_content"));
        if (sub.HasFlag(GitSubmodule.Untracked))
            words.Add(context.Strings.Said("git_repos.untracked_content"));
        return words.Count > 0 ? string.Join(", ", words) : context.Strings.Said("git_repos.submodule");
    }

    private void RepositoryRowMenu(Repository repository, string path, bool module, bool stagedSide)
    {
        var menu = new Menu("git_submodule");
        menu.Add(context.Strings.Said("git_repos.open_repository"), () => FocusRepository(path), icon: "repo");
        if (module)
        {
            menu.AddSeparator();
            if (stagedSide)
                menu.Add(context.Strings.Said("git.unstage"), () => StageFiles(repository, [path], false), icon: "remove");
            else
                menu.Add(context.Strings.Said("git.stage"), () => StageFiles(repository, [path], true), icon: "add");
        }
        context.Windows.Menu(menu);
    }
}
