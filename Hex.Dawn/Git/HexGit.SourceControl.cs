using System.Numerics;

using Hex.Core.Git;

namespace Hex.Dawn;

// Source Control, VS Code's side of that name. The files of every repository of the workspace that aren't as committed,
// what's staged for the next commit apart from what isn't, and what a merge left in conflict above both. A click on a
// file shows what changed in it, the right click stages, unstages, throws away or opens it, and Commit at the top takes
// what's staged with a message (everything, when nothing's staged and that's what's asked for). Every one of them is
// git itself in the background, and the side is put together again from what git says after.
internal sealed partial class HexGit
{
    private void BuildSourceControl()
    {
        context.Panels.AddSide(HexSide.SourceControl, context.Strings.Said("git.side_title"), SourceControlSide);
        Changed += () => context.Panels.RefreshSide(HexSide.SourceControl);

        context.Commands.Add("View/sides", context.Strings.Said("git.side_command"), () => context.Panels.ShowSide(HexSide.SourceControl), "Ctrl+Shift+G", "source-control", UIKeyUse.BoundWhileTyping);
        context.Commands.Add("Git/changes", context.Strings.Said("git.commit_command"), () => AskCommit(FirstRepository()), "Ctrl+Alt+Enter", "check", UIKeyUse.BoundWhileTyping);
        context.Commands.Add("Git/changes", context.Strings.Said("git.stage_file"), () => StageCurrent(true), icon: "add");
        context.Commands.Add("Git/changes", context.Strings.Said("git.unstage_file"), () => StageCurrent(false), icon: "remove");
        context.Commands.Add("Git/branches", context.Strings.Said("git.branches_command"), () => ShowBranches(context.Documents.Current.Path), icon: "git-branch");
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

        int count = conflicts.Count + staged.Count + changes.Count;
        items.Add(new UITreeItem(context.Strings.Said(staged.Count > 0 ? "git.commit_staged" : "git.commit_all", ("count", (staged.Count > 0 ? staged.Count : count).ToString())), "check")
        {
            Color = count > 0 ? null : context.Panels.Dim,
            Tooltip = context.Strings.Said("git.commit_tip"),
            Tag = new SideTag(Pressed: () => AskCommit(repository))
        });

        if (count == 0)
        {
            items.Add(Note(context.Strings.Said("git.no_changes")));
            return;
        }

        if (conflicts.Count > 0)
            items.Add(Section(repository, context.Strings.Said("git.merge_changes", ("count", conflicts.Count.ToString())), conflicts, stagedSide: false));
        if (staged.Count > 0)
            items.Add(Section(repository, context.Strings.Said("git.staged_changes", ("count", staged.Count.ToString())), staged, stagedSide: true));
        if (changes.Count > 0)
            items.Add(Section(repository, context.Strings.Said("git.changes", ("count", changes.Count.ToString())), changes, stagedSide: false));
    }

    // A section of files, staged or not, its right click every file of it at once
    private UITreeItem Section(Repository repository, string title, List<(string Path, GitFileState State)> files, bool stagedSide)
    {
        string[] paths = [.. files.Select(file => file.Path)];
        var section = new UITreeItem(title, stagedSide ? "pass" : "diff")
        {
            Expandable = true,
            Expanded = true,
            Children = [],
            Tag = new SideTag(Menu: _ => SectionMenu(repository, paths, stagedSide))
        };

        foreach (var (path, state) in files)
        {
            string folder = Path.GetRelativePath(repository.Git.Root, Path.GetDirectoryName(path) ?? repository.Git.Root);
            section.Children.Add(new UITreeItem(Path.GetFileName(path), HexExplorer.FileIcon(path))
            {
                // A renamed file says what it was called, or nobody can tell it from a new one
                Detail = $"{(state.From is { } from ? $"from {Path.GetFileName(from)}  " : string.Empty)}{(folder == "." ? string.Empty : folder.Replace('\\', '/') + "  ")}{state.Letter}",
                Color = ColourOf?.Invoke(state.Change),
                Tooltip = path,
                Tag = new SideTag(Pressed: () => OpenChange(repository, path, stagedSide), Menu: _ => FileMenu(repository, path, state, stagedSide))
            });
        }

        return section;
    }

    /// <summary>The colour of a change, the explorer's, given by the scene that knows the skin.</summary>
    public Func<GitChange, Vector4?>? ColourOf { get; set; }

    /// <summary>Shows a changed file, what changed in it when there's a way to (the diff), the file itself otherwise.</summary>
    public Action<GitRepository, string, bool>? ShowChange { get; set; }

    private void OpenChange(Repository repository, string path, bool staged)
    {
        if (ShowChange is { } show && File.Exists(path))
            show(repository.Git, path, staged);
        else if (File.Exists(path))
            context.Documents.Open(path);
    }

    private void FileMenu(Repository repository, string path, GitFileState state, bool stagedSide)
    {
        var menu = new Menu("git_file");
        if (stagedSide)
            menu.Add(context.Strings.Said("git.unstage"), () => Run(repository, "git restore --staged", () => repository.Git.Unstage([path])), icon: "remove");
        else
            menu.Add(context.Strings.Said("git.stage"), () => Run(repository, "git add", () => repository.Git.Stage([path])), icon: "add");
        if (!stagedSide && state.Change != GitChange.Conflicted)
            menu.Add(context.Strings.Said("git.discard"), () => AskDiscard(repository, [path], state.Change == GitChange.Untracked ? [path] : []), icon: "discard");
        menu.AddSeparator();
        if (File.Exists(path))
            menu.Add(context.Strings.Said("git.open_file"), () => context.Documents.Open(path), icon: "go-to-file");
        context.Windows.Menu(menu);
    }

    private void SectionMenu(Repository repository, string[] paths, bool stagedSide)
    {
        var menu = new Menu("git_section");
        if (stagedSide)
            menu.Add(context.Strings.Said("git.unstage_all"), () => Run(repository, "git restore --staged", () => repository.Git.Unstage(paths)), icon: "remove");
        else
        {
            menu.Add(context.Strings.Said("git.stage_all"), () => Run(repository, "git add", () => repository.Git.Stage(paths)), icon: "add");
            string[] fresh = [.. paths.Where(path => repository.Status?.StateOf(path).Change == GitChange.Untracked)];
            menu.Add(context.Strings.Said("git.discard_all"), () => AskDiscard(repository, paths, fresh), icon: "discard");
        }
        context.Windows.Menu(menu);
    }

    /// <summary>The file on screen staged or taken out of the stage, from the command palette.</summary>
    private void StageCurrent(bool stage)
    {
        if (context.Documents.Current.Path is not { } path || RepositoryOf(path) is not { } repository)
            return;

        if (stage)
            Run(repository, "git add", () => repository.Git.Stage([path]));
        else
            Run(repository, "git restore --staged", () => repository.Git.Unstage([path]));
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

    /// <summary>Asks for a commit's message and commits, everything when nothing's staged and that's what's wanted.</summary>
    private void AskCommit(Repository? repository)
    {
        if (repository?.Status is not { } status)
            return;

        int conflicts = status.Files.Values.Count(state => state.Change == GitChange.Conflicted);
        if (conflicts > 0)
        {
            context.Notices.Say(context.Strings.Said("git.resolve_first", ("count", HexText.Count("file", conflicts))), error: true);
            return;
        }

        bool anyStaged = status.Files.Values.Any(state => state.Staged);
        string[] all = [.. status.Files.Where(pair => pair.Value.Change is not (GitChange.Ignored or GitChange.Conflicted)).Select(pair => pair.Key)];
        if (all.Length == 0 && !anyStaged)
        {
            context.Notices.Say(context.Strings.Said("git.no_changes"));
            return;
        }

        context.Windows.PromptLines(context.Strings.Said("git.commit_title", ("branch", status.Branch ?? "HEAD")),
            anyStaged ? context.Strings.Said("git.commit_text_staged") : context.Strings.Said("git.commit_text_all", ("count", all.Length.ToString())),
            string.Empty, context.Strings.Said("git.commit_button"), "COMMIT_EDITMSG.md", SummaryNote, typed =>
            {
                string message = typed.Trim();
                if (message.Length == 0)
                    return;

                Run(repository, "git commit", () =>
                {
                    if (!anyStaged && repository.Git.Stage(all) is { } failed)
                        return failed;
                    return repository.Git.Commit(message);
                }, context.Strings.Said("git.committed", ("message", message.Split('\n')[0])));
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

    // Past this many characters a commit's summary is cut short by git log --oneline and GitHub, 50 is what's asked for
    private const int SUMMARY_SHORT = 50, SUMMARY_LONG = 72;

    /// <summary>What's said under the commit's box, how long its first line is, a warning once it's too long to show whole.</summary>
    private (string Text, bool Warning) SummaryNote(string message)
    {
        string summary = message.Replace("\r\n", "\n").Split('\n')[0];
        int length = summary.Trim().Length;
        if (length == 0)
            return (context.Strings.Said("git.commit_hint"), false);
        if (length > SUMMARY_LONG)
            return (context.Strings.Said("git.summary_too_long", ("length", length.ToString()), ("most", SUMMARY_LONG.ToString())), true);
        return (context.Strings.Said("git.summary_length", ("length", length.ToString()), ("best", SUMMARY_SHORT.ToString())), length > SUMMARY_SHORT);
    }

    /// <summary>Runs something of git's in the background, then has git asked again, what went wrong on a card with all of it a button away.</summary>
    private void Run(Repository repository, string doing, Func<string?> work, string? done = null)
    {
        context.Work.InBackground(doing, work, failed =>
        {
            repository.Dirty(now: true);
            if (failed is null)
            {
                ClearFailure();
                if (done is not null)
                    context.Notices.Say(done);
                return;
            }

            string first = failed.Split('\n')[0].Trim();
            FailureCard(context.Strings.Said("git.failed", ("doing", doing), ("why", first)), error: true,
                (context.Strings.Said("git.details"), () => context.Windows.ShowText(doing, $"```\n{failed}\n```\n")));
        }, quiet: true);
    }

    private UITreeItem Note(string text) => new(text, "info") { Color = context.Panels.Dim };
}
