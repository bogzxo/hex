using System.Numerics;

using Hex.Core.Git;

namespace Hex.Dawn;

// Source Control, VS Code's side of that name. The files of every repository of the workspace that aren't as committed,
// what's staged for the next commit apart from what isn't, and what a merge left in conflict above both. A file's row is
// its name in the colour of its change, its folder dim after it and the letter of the change at the right end. The
// pointer on a row brings up its buttons (open, discard, stage or unstage), Space stages or unstages the picked one and
// Delete throws its changes away. A click shows what changed in it, the right click has the same and more, and Commit
// at the top takes what's staged with a message (everything, when nothing's staged and that's what's asked for). Every
// one of them is git itself in the background, and the side is put together again from what git says after.
internal sealed partial class HexGit
{
    private void BuildSourceControl()
    {
        context.Panels.AddSide(HexSide.SourceControl, context.Strings.Said("git.side_title"), SourceControlSide);
        Changed += () => context.Panels.RefreshSide(HexSide.SourceControl);

        context.Commands.Add("View/sides", context.Strings.Said("git.side_command"), () => context.Panels.ShowSide(HexSide.SourceControl), "Ctrl+Shift+G", "source-control", UIKeyUse.BoundWhileTyping);
        context.Commands.Add("Git/commit", context.Strings.Said("git.commit_command"), () => AskCommit(FirstRepository()), "Ctrl+Alt+Enter", "check", UIKeyUse.BoundWhileTyping);
        BuildCommits();
        context.Commands.Add("Git/menus/changes:file", context.Strings.Said("git.stage_file"), () => StageCurrent(true), icon: "add");
        context.Commands.Add("Git/menus/changes:file", context.Strings.Said("git.unstage_file"), () => StageCurrent(false), icon: "remove");
        context.Commands.Add("Git/menus/changes:file", context.Strings.Said("git.discard_file"), DiscardCurrent, icon: "discard");
        context.Commands.Add("Git/menus/changes:all", context.Strings.Said("git.stage_all"), () => StageEverything(FirstRepository(), true), icon: "add");
        context.Commands.Add("Git/menus/changes:all", context.Strings.Said("git.unstage_all"), () => StageEverything(FirstRepository(), false), icon: "remove");
        context.Commands.Add("Git/menus/changes:all", context.Strings.Said("git.discard_all_command"), () => DiscardEverything(FirstRepository()), icon: "discard");
        context.Commands.Add("Git/menus/branches:switch", context.Strings.Said("git.branches_command"), () => ShowBranches(context.Documents.Current.Path), icon: "git-branch");
        BuildBranchOps();
    }

    // The repository of the file on screen, or the workspace's first
    private Repository? FirstRepository()
    {
        if (context.Documents.Current.Path is { } path && RepositoryOf(path) is { } here)
            return here;

        foreach (string root in context.Workspace.Roots)
        {
            if (RepositoryOf(root) is { } rooted)
                return rooted;
        }

        return null;
    }

    /// <summary>The rows of the side, a repository's changes under its name when there's more than one.</summary>
    private List<UITreeItem> SourceControlSide()
    {
        var items = new List<UITreeItem>();
        foreach (string root in context.Workspace.Roots)
            RepositoryOf(root);
        List<Repository> shown = WorkspaceRepositories();

        if (context.Documents.Current.Path is { } open && RepositoryOf(open) is { } openRepository && !shown.Contains(openRepository))
            shown.Add(openRepository);

        if (!Available)
            items.Add(Note(context.Strings.Said("git.not_installed")));
        else if (shown.Count == 0)
            items.Add(Note(context.Strings.Said("git.no_repository")));

        foreach (Repository repository in shown)
        {
            if (shown.Count == 1)
            {
                AddRepositoryRows(items, repository);
                continue;
            }

            var heading = new UITreeItem($"{Path.GetFileName(repository.Git.Root)}   {repository.Status?.Branch ?? string.Empty}", "repo")
            {
                Expandable = true,
                Expanded = true,
                Children = [],
                Tooltip = repository.Git.Root
            };
            AddRepositoryRows(heading.Children, repository);
            items.Add(heading);
        }

        return items;
    }

    private void AddRepositoryRows(List<UITreeItem> items, Repository repository)
    {
        GitStatus? status = repository.Status;
        if (status is null)
        {
            items.Add(Note(context.Strings.Said("git.reading")));
            return;
        }

        var conflicts = new List<(string Path, GitFileState State)>();
        var staged = new List<(string Path, GitFileState State)>();
        var changes = new List<(string Path, GitFileState State)>();
        foreach (var (path, state) in status.Files.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (state.Change == GitChange.Ignored)
                continue;
            if (state.Change == GitChange.Conflicted)
                conflicts.Add((path, state));
            else
            {
                if (state.Staged) staged.Add((path, state));
                if (state.Unstaged) changes.Add((path, state));
            }
        }

        // A merge going on says so above Commit, which finishes it once the conflicts are settled
        bool merge = MergeOf(repository) is not null;
        if (merge)
        {
            items.Add(new UITreeItem(context.Strings.Said("git.merging"), "git-merge")
            {
                Color = context.Panels.Warning,
                IconColor = context.Panels.Warning,
                Tooltip = context.Strings.Said("git.merging_tip")
            });
        }

        // With nothing staged and the setting saying never, Commit stages nothing for you and says so on its row. A merge's
        // Commit finishes it whatever is staged, so it says no more than that, and with nothing changed there's no count
        // of nothing to give, only a dim Commit
        int count = conflicts.Count + staged.Count + changes.Count;
        bool never = staged.Count == 0 && context.Settings.Values.GitSmartCommit == "never";
        string commit = merge ? context.Strings.Said("git.commit_merge")
            : count == 0 ? context.Strings.Said("git.commit_plain")
            : staged.Count > 0 ? context.Strings.Said("git.commit_staged", ("count", staged.Count.ToString()))
            : never ? context.Strings.Said("git.commit_nothing_staged")
            : context.Strings.Said("git.commit_all", ("count", count.ToString()));
        items.Add(new UITreeItem(commit, "check")
        {
            Color = merge || count > 0 && !never ? null : context.Panels.Dim,
            Tooltip = context.Strings.Said("git.commit_tip"),
            Actions = [new UITreeAction("ellipsis", context.Strings.Said("git.commit_more"), () => CommitMenu(repository))],
            Tag = new SideTag(Pressed: () => AskCommit(repository), Menu: _ => CommitMenu(repository))
        });

        if (count == 0)
        {
            // Short enough for the side as narrow as it comes, the whole of it on the pointer
            UITreeItem none = Note(context.Strings.Said("git.no_changes"));
            none.Tooltip = context.Strings.Said("git_branch.nothing_to_commit");
            items.Add(none);
            return;
        }

        if (conflicts.Count > 0)
            items.Add(Section(repository, context.Strings.Said("git.merge_changes"), conflicts, SectionKind.Conflicts));
        if (staged.Count > 0)
            items.Add(Section(repository, context.Strings.Said("git.staged_changes"), staged, SectionKind.Staged));
        if (changes.Count > 0)
            items.Add(Section(repository, context.Strings.Said("git.changes"), changes, SectionKind.Changes));
    }

    // Which of the three a section is, what a merge left in conflict, what's staged and what isn't
    private enum SectionKind { Conflicts, Staged, Changes }

    // A section of files, how many there are at its right end, its buttons and its right click every file of it at once
    private UITreeItem Section(Repository repository, string title, List<(string Path, GitFileState State)> files, SectionKind kind)
    {
        bool stagedSide = kind == SectionKind.Staged;
        string[] paths = [.. files.Select(file => file.Path)];
        var section = new UITreeItem(title, stagedSide ? "pass" : "diff")
        {
            Badge = files.Count.ToString(),
            Expandable = true,
            Expanded = true,
            Children = [],
            Actions = SectionActions(repository, paths, kind),
            Tag = new SideTag(Menu: _ => SectionMenu(repository, paths, stagedSide), Key: key => SectionKey(repository, paths, stagedSide, key))
        };

        foreach (var (path, state) in files)
        {
            string folder = Path.GetRelativePath(repository.Git.Root, Path.GetDirectoryName(path) ?? repository.Git.Root);

            // A renamed file says what it was called, or nobody can tell it from a new one
            string from = state.From is { } was ? $"from {Path.GetFileName(was)}" : string.Empty;
            string where = folder == "." ? string.Empty : folder.Replace('\\', '/');
            section.Children.Add(new UITreeItem(Path.GetFileName(path), HexExplorer.FileIcon(path))
            {
                Detail = from.Length > 0 && where.Length > 0 ? $"{from}  {where}" : from + where,
                Color = ColourOf?.Invoke(state.Change),
                Badge = state.Letter,
                BadgeColor = ColourOf?.Invoke(state.Change),
                Tooltip = path,
                Actions = FileActions(repository, path, state, kind),
                Tag = new SideTag(Pressed: () => OpenChange(repository, path, stagedSide), Menu: _ => FileMenu(repository, path, state, stagedSide),
                    Key: key => FileKey(repository, path, state, stagedSide, key))
            });
        }

        return section;
    }

    /// <summary>The colour of a change, the explorer's, given by the scene that knows the skin.</summary>
    public Func<GitChange, Vector4?>? ColourOf { get; set; }

    /// <summary>Shows a changed file, what changed in it when there's a way to (the diff), the file itself otherwise.</summary>
    public Action<GitRepository, string, bool>? ShowChange { get; set; }

    // A deleted file has a diff too, all of it red, what was taken out
    private void OpenChange(Repository repository, string path, bool staged)
    {
        if (ShowChange is { } show)
            show(repository.Git, path, staged);
        else if (File.Exists(path))
            context.Documents.Open(path);
    }

    // The buttons of a file's row, left to right the way VS Code has them. Stage on a file in conflict marks it settled
    private List<UITreeAction> FileActions(Repository repository, string path, GitFileState state, SectionKind kind)
    {
        var actions = new List<UITreeAction>();
        if (File.Exists(path))
            actions.Add(new UITreeAction("go-to-file", context.Strings.Said("git.open_file"), () => context.Documents.Open(path)));

        switch (kind)
        {
            case SectionKind.Staged:
                actions.Add(new UITreeAction("remove", context.Strings.Said("git.unstage_changes"), () => StageFiles(repository, [path], false)));
                break;
            case SectionKind.Conflicts:
                actions.Add(new UITreeAction("add", context.Strings.Said("git.stage_resolved"), () => StageFiles(repository, [path], true)));
                break;
            default:
                actions.Add(new UITreeAction("discard", context.Strings.Said("git.discard"), () => DiscardFile(repository, path, state)));
                actions.Add(new UITreeAction("add", context.Strings.Said("git.stage_changes"), () => StageFiles(repository, [path], true)));
                break;
        }

        return actions;
    }

    // The buttons of a section's heading, every file of it at once
    private List<UITreeAction> SectionActions(Repository repository, string[] paths, SectionKind kind)
    {
        if (kind == SectionKind.Staged)
            return [new UITreeAction("remove", context.Strings.Said("git.unstage_all"), () => StageFiles(repository, paths, false))];
        if (kind == SectionKind.Conflicts)
            return [new UITreeAction("add", context.Strings.Said("git.stage_all_resolved"), () => StageFiles(repository, paths, true))];

        return
        [
            new UITreeAction("discard", context.Strings.Said("git.discard_all"), () => DiscardAll(repository, paths)),
            new UITreeAction("add", context.Strings.Said("git.stage_all"), () => StageFiles(repository, paths, true))
        ];
    }

    // Space stages or unstages the picked file, Delete throws its changes away (asking first)
    private bool FileKey(Repository repository, string path, GitFileState state, bool stagedSide, char key)
    {
        switch (key)
        {
            case ' ':
                StageFiles(repository, [path], !stagedSide);
                return true;
            case UIKeyboard.DELETE when !stagedSide && state.Change != GitChange.Conflicted:
                DiscardFile(repository, path, state);
                return true;
            default:
                return false;
        }
    }

    // The same on a section's heading, every file of it
    private bool SectionKey(Repository repository, string[] paths, bool stagedSide, char key)
    {
        if (key != ' ')
            return false;

        StageFiles(repository, paths, !stagedSide);
        return true;
    }

    private void StageFiles(Repository repository, string[] paths, bool stage)
    {
        if (stage)
            Run(repository, "git add", () => repository.Git.Stage(paths));
        else
            Run(repository, "git restore --staged", () => repository.Git.Unstage(paths));
    }

    private void DiscardFile(Repository repository, string path, GitFileState state) =>
        AskDiscard(repository, [path], state.Change == GitChange.Untracked ? [path] : []);

    private void DiscardAll(Repository repository, string[] paths)
    {
        string[] fresh = [.. paths.Where(path => repository.Status?.StateOf(path).Change == GitChange.Untracked)];
        AskDiscard(repository, paths, fresh);
    }

    private void FileMenu(Repository repository, string path, GitFileState state, bool stagedSide)
    {
        var menu = new Menu("git_file");
        if (stagedSide)
            menu.Add(context.Strings.Said("git.unstage"), () => StageFiles(repository, [path], false), icon: "remove");
        else
            menu.Add(context.Strings.Said("git.stage"), () => StageFiles(repository, [path], true), icon: "add");
        if (!stagedSide && state.Change != GitChange.Conflicted)
            menu.Add(context.Strings.Said("git.discard"), () => DiscardFile(repository, path, state), icon: "discard");
        menu.AddSeparator();
        if (File.Exists(path))
            menu.Add(context.Strings.Said("git.open_file"), () => context.Documents.Open(path), icon: "go-to-file");
        context.Windows.Menu(menu);
    }

    private void SectionMenu(Repository repository, string[] paths, bool stagedSide)
    {
        var menu = new Menu("git_section");
        if (stagedSide)
            menu.Add(context.Strings.Said("git.unstage_all"), () => StageFiles(repository, paths, false), icon: "remove");
        else
        {
            menu.Add(context.Strings.Said("git.stage_all"), () => StageFiles(repository, paths, true), icon: "add");
            menu.Add(context.Strings.Said("git.discard_all"), () => DiscardAll(repository, paths), icon: "discard");
        }
        context.Windows.Menu(menu);
    }

    /// <summary>The file on screen staged or taken out of the stage, from the command palette.</summary>
    private void StageCurrent(bool stage)
    {
        if (context.Documents.Current.Path is not { } path || RepositoryOf(path) is not { } repository)
            return;

        StageFiles(repository, [path], stage);
    }

    /// <summary>The changes to the file on screen thrown away, asking first, from the Git menu.</summary>
    private void DiscardCurrent()
    {
        if (context.Documents.Current.Path is not { } path || RepositoryOf(path) is not { Status: { } status } repository)
            return;

        GitFileState state = status.StateOf(path);
        if (state.Unstaged && state.Change != GitChange.Conflicted)
            DiscardFile(repository, path, state);
    }

    /// <summary>Every change of a repository staged, or everything staged taken out of the stage, the Changes sections' buttons from the Git menu.</summary>
    private void StageEverything(Repository? repository, bool stage)
    {
        if (repository?.Status is not { } status)
            return;

        string[] paths = [.. status.Files.Where(pair => pair.Value.Change != GitChange.Ignored && (stage ? pair.Value.Unstaged || pair.Value.Change == GitChange.Conflicted : pair.Value.Staged)).Select(pair => pair.Key)];
        if (paths.Length > 0)
            StageFiles(repository, paths, stage);
    }

    /// <summary>Every change not staged thrown away, asking first, the Changes section's button from the Git menu.</summary>
    private void DiscardEverything(Repository? repository)
    {
        if (repository?.Status is not { } status)
            return;

        string[] paths = [.. status.Files.Where(pair => pair.Value.Unstaged && pair.Value.Change is not GitChange.Ignored and not GitChange.Conflicted).Select(pair => pair.Key)];
        if (paths.Length > 0)
            DiscardAll(repository, paths);
    }

    /// <summary>
    /// Throwing changes away asks first, it can't be undone in Hex. Files git doesn't know go to the trash, the rest go back
    /// to what the index has.
    /// </summary>
    private void AskDiscard(Repository repository, string[] paths, string[] fresh)
    {
        string what = paths.Length == 1 ? Path.GetFileName(paths[0]) : HexText.Count("file", paths.Length);
        string yes = context.Strings.Said("git.discard_yes");
        context.Windows.Ask(context.Strings.Said("git.discard_title"), context.Strings.Said("git.discard_text", ("what", what)), [context.Strings.Said("git.discard_no"), yes], answer =>
        {
            if (answer != yes)
                return;

            string[] known = [.. paths.Except(fresh)];
            Run(repository, "git restore", () =>
            {
                foreach (string path in fresh)
                    HexTrash.Discard(path);
                return known.Length > 0 ? repository.Git.Discard(known) : null;
            });
        });
    }

    // The card of the last thing of git's that went wrong, taken down by the next one that goes right or wrong, so cards
    // of the same failure don't stack up and one that's been put right doesn't stay
    private UINotification? failureCard;

    private void FailureCard(string message, bool error, params (string Label, Action Pressed)[] actions)
    {
        failureCard?.Close();
        failureCard = context.Notices.Notify(message, error, actions);
    }

    private void ClearFailure()
    {
        failureCard?.Close();
        failureCard = null;
    }

    /// <summary>
    /// Runs something of git's in the background, then has git asked again, what went wrong on a card with all of it a
    /// button away. What comes after it (a push after a commit) only once it went right.
    /// </summary>
    private void Run(Repository repository, string doing, Func<string?> work, string? done = null, Action? then = null)
    {
        context.Work.InBackground(doing, work, failed =>
        {
            repository.Dirty(now: true);
            if (failed is null)
            {
                ClearFailure();
                if (done is not null)
                    context.Notices.Say(done);
                then?.Invoke();
                return;
            }

            string first = failed.Split('\n')[0].Trim();
            FailureCard(context.Strings.Said("git.failed", ("doing", doing), ("why", first)), error: true,
                (context.Strings.Said("git.details"), () => context.Windows.ShowText(doing, $"```\n{failed}\n```\n")));
        }, quiet: true);
    }

    private UITreeItem Note(string text) => new(text, "info") { Color = context.Panels.Dim };
}
