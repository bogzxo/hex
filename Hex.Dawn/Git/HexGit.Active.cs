using System.Numerics;

using Hex.Core.Git;

namespace Hex.Dawn;

// The active repository, VS Code's idea of it. With more than one repository in the workspace the Git menu, Commit's
// keys, the branch and the sync in the status bar all work on one of them. It's the one last picked on the Source
// Control side (its heading or anything under it) or the one the file last brought up is in, whichever came last, and
// the workspace's first before either. The status bar says which by name, and a click on it picks the repository first
// and its branch after.
internal sealed partial class HexGit
{
    // The repository last picked on the side or followed from the file on screen, null for none yet
    private Repository? chosen;

    // The path of the file on screen when it was last looked at, compared by reference so a tick with the same file makes nothing
    private string? followed;

    /// <summary>The colour the active repository's heading is marked in, the skin's accent, given by the scene that knows the skin.</summary>
    public Func<Vector4?>? AccentOf { get; set; }

    private void BuildActive()
    {
        context.Commands.Add("Git/commit", context.Strings.Said("git_repos.pick_command"), () => PickRepository(context.Strings.Said("git_repos.pick_text"), _ => { }), icon: "repo")
            .IsEnabled = () => WorkspaceRepositories().Count > 1;
    }

    /// <summary>The repository the Git menu and the status bar work on, null with none in the workspace.</summary>
    private Repository? ActiveRepository()
    {
        Repository? open = OpenRepository();
        if (chosen is { Gone: false } && (chosen == open || WorkspaceRepositories().Contains(chosen)))
            return chosen;
        if (open is not null)
            return open;

        List<Repository> all = WorkspaceRepositories();
        return all.Count > 0 ? all[0] : null;
    }

    // The repository of the file on screen, null for none
    private Repository? OpenRepository() => context.Documents.Current.Path is { } path ? RepositoryOf(path) : null;

    /// <summary>Whether the workspace has more than one repository, so what's said of the active one says which it is.</summary>
    private bool Several => WorkspaceRepositories().Count > 1;

    /// <summary>A repository made the active one, the side, the status bar and the Git menu's names following it.</summary>
    private void MakeActive(Repository repository)
    {
        if (chosen == repository)
            return;

        chosen = repository;
        ActiveMoved();
    }

    private void ActiveMoved()
    {
        Version++;
        FollowOperation();
        context.Panels.RefreshSide(HexSide.SourceControl);
        context.Redraw();
    }

    /// <summary>Every tick, a file brought up in another repository makes that one the active one.</summary>
    private void FollowOpenFile()
    {
        string? path = context.Documents.Current.Path;
        if (ReferenceEquals(path, followed))
            return;

        followed = path;
        if (path is not null && RepositoryOf(path) is { } here && here != chosen)
        {
            chosen = here;
            ActiveMoved();
        }
    }

    /// <summary>
    /// The status bar's branch pressed, the active repository's branches to switch to. With more than one repository the
    /// repository is picked first, the way VS Code's Source Control Repositories has it.
    /// </summary>
    public void BranchPressed()
    {
        if (Several)
            PickRepository(context.Strings.Said("git_repos.pick_branch_text"), BranchesOf);
        else if (ActiveRepository() is { } only)
            BranchesOf(only);
    }

    /// <summary>
    /// The workspace's repositories to pick one from, each with its branch, its changes and where it is, the active one
    /// checked. The one picked is made active, then handed on.
    /// </summary>
    private void PickRepository(string text, Action<Repository> picked)
    {
        Repository? active = ActiveRepository();
        var choices = new List<UIChoice>();
        foreach (Repository repository in WorkspaceRepositories())
        {
            string branch = BranchName(repository.Status) + (repository.HasChanges ? "*" : string.Empty);
            string where = WhereIs(repository);
            int count = ChangeCount(repository);
            var row = new List<(string Text, UIChoiceTone Tone)> { (repository.Name, UIChoiceTone.Primary), (branch, UIChoiceTone.Accent) };
            if (count > 0)
                row.Add((HexText.Count("change", count), UIChoiceTone.Dim));
            row.Add((where, UIChoiceTone.Dim));

            choices.Add(new UIChoice(repository.Name, () =>
            {
                MakeActive(repository);
                picked(repository);
            })
            {
                Icon = repository == active ? "check" : "repo",
                Segments = row,
                Id = $"{repository.Name}: {branch}   {where}"
            });
        }

        if (choices.Count > 0)
            context.Windows.Choose(context.Strings.Said("git_repos.pick_title"), text, choices);
    }

    // The branch a status is on, or the start of the commit HEAD is on when it's on none
    private static string BranchName(GitStatus? status) =>
        status is null ? "?" : status.Branch ?? (status.Commit is { } commit ? commit[..Math.Min(8, commit.Length)] : "?");

    // Where a repository is, from the workspace's folder it's in, the full path when it's in none
    private string WhereIs(Repository repository)
    {
        foreach (string root in context.Workspace.Roots)
        {
            if (PlatformPaths.IsUnder(repository.Git.Root, root) || PlatformPaths.Same(repository.Git.Root, root))
                return Path.GetRelativePath(Path.GetDirectoryName(root) ?? root, repository.Git.Root).Replace('\\', '/');
        }

        return repository.Git.Root;
    }

    /// <summary>
    /// The repository a submodule's or a nested repository's row is, followed when it wasn't yet, made active and its
    /// heading opened, so a click on it on the side goes to its own section.
    /// </summary>
    private void FocusRepository(string folder)
    {
        Adopt(folder);
        if (!repositories.TryGetValue(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), out Repository? repository))
            return;

        // Its heading opened, and the headings of the repositories it's inside of, it's under theirs
        listedDirty = true;
        closedHeadings.RemoveWhere(root => PlatformPaths.Same(root, repository.Git.Root) || PlatformPaths.IsUnder(repository.Git.Root, root));
        if (chosen == repository)
            context.Panels.RefreshSide(HexSide.SourceControl);
        MakeActive(repository);
    }
}
