using Hex.Core.Git;

namespace Hex.Dawn;

// Committing. The box for the message and the commit after it, and what VS Code has round that, Commit and Push, Commit
// and Sync, amending the last commit and taking it back again. They're in the Git menu, on the right click of the
// Commit row and behind its ... button. A commit taken back leaves what it changed staged and its message waiting in the
// next commit's box, and a merge going on fills the box with the message git wrote for it.
internal sealed partial class HexGit
{
    // What happens once a commit went in
    private enum CommitThen { Nothing, Push, Sync }

    // What the commit is told besides its message, the hooks skipped or a Signed-off-by line added
    [Flags]
    private enum CommitFlags { None = 0, NoVerify = 1, SignOff = 2 }

    // The message of a commit that was taken back, by its repository's root, what the next commit's box starts with
    private readonly Dictionary<string, string> undone = new(PlatformPaths.Comparer);

    // The message a merge left, by repository root, with the generation of what git said it was read for. Read in the
    // background whenever git says something new, the side asks for it every time it's put together
    private readonly Dictionary<string, (int Generation, string? Message)> merging = new(PlatformPaths.Comparer);
    private readonly HashSet<string> readingMerge = new(PlatformPaths.Comparer);

    // The other ways of committing, the Git menu's Commit menu and the Commit row's right click and ... button both, in
    // their groups (fix is amending and taking back, flags the hooks skipped or signed off)
    private (string Group, string Key, string Icon, Action<Repository?> Do)[] CommitChoices() =>
    [
        ("commit", "git.commit_push", "arrow-up", repository => AskCommit(repository, CommitThen.Push)),
        ("commit", "git.commit_sync", "sync", repository => AskCommit(repository, CommitThen.Sync)),
        ("fix", "git.amend_command", "edit", AskAmend),
        ("fix", "git.undo_command", "discard", UndoLastCommit),
        ("flags", "git.commit_no_verify", "check", repository => AskCommit(repository, CommitThen.Nothing, CommitFlags.NoVerify)),
        ("flags", "git.commit_signoff", "check", repository => AskCommit(repository, CommitThen.Nothing, CommitFlags.SignOff))
    ];

    private void BuildCommits()
    {
        foreach (var (group, key, icon, act) in CommitChoices())
            context.Commands.Add($"Git/menus/commit:{group}", context.Strings.Said(key), () => act(ActiveRepository()), icon: icon);
    }

    // The Commit row's right click and its ... button, Commit itself and the same things as the menu's
    private void CommitMenu(Repository repository)
    {
        var menu = new Menu("git_commit");
        menu.Add(context.Strings.Said("git.commit_button"), () => AskCommit(repository), icon: "check");
        string? last = null;
        foreach (var (group, key, icon, act) in CommitChoices())
        {
            if (last is not null && group != last)
                menu.AddSeparator();

            last = group;
            menu.Add(context.Strings.Said(key), () => act(repository), icon: icon);
        }

        context.Windows.Menu(menu);
    }

    /// <summary>
    /// The message a merge going on left for the commit that finishes it, null for no merge. What was read last, and read
    /// again in the background when git has said something new since, the side put together again if it changed.
    /// </summary>
    private string? MergeOf(Repository repository)
    {
        string root = repository.Git.Root;
        merging.TryGetValue(root, out var known);
        if (known.Generation != repository.Generation && readingMerge.Add(root))
        {
            int generation = repository.Generation;
            context.Work.InBackground("git rev-parse", repository.Git.MergeMessage, message =>
            {
                readingMerge.Remove(root);
                bool changed = !merging.TryGetValue(root, out var before) || before.Message != message;
                merging[root] = (generation, message);
                if (changed)
                    context.Panels.RefreshSide(HexSide.SourceControl);
            }, quiet: true);
        }

        return known.Message;
    }

    /// <summary>
    /// Asks for a commit's message and commits. With nothing staged the setting says what happens (VS Code's smart
    /// commit), ask whether to stage everything first, always stage everything, or never and say so.
    /// </summary>
    private void AskCommit(Repository? repository) => AskCommit(repository, CommitThen.Nothing);

    private void AskCommit(Repository? repository, CommitThen then, CommitFlags flags = CommitFlags.None, string? message = null)
    {
        if (repository?.Status is not { } status)
            return;

        // What's typed in the box over the side is the message, whichever way the commit was asked for
        message ??= BoxMessage?.Invoke(repository.Git.Root);

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
            context.Notices.Say(context.Strings.Said("git_branch.nothing_to_commit"));
            return;
        }

        string branch = status.Branch ?? "HEAD";
        if (anyStaged || context.Settings.Values.GitSmartCommit == "always")
        {
            PromptCommit(repository, branch, all, anyStaged, then, flags, message);
            return;
        }

        if (context.Settings.Values.GitSmartCommit == "never")
        {
            context.Notices.Say(context.Strings.Said("git.nothing_staged"));
            return;
        }

        int untracked = status.Files.Values.Count(state => state.Change == GitChange.Untracked);
        string stage = context.Strings.Said("git.smart_stage"), always = context.Strings.Said("git.smart_always");
        context.Windows.Ask(context.Strings.Said("git.smart_title"),
            context.Strings.Said("git.smart_text", ("count", all.Length.ToString()), ("untracked", untracked.ToString())),
            [stage, always, context.Strings.Said("git.smart_cancel")], answer =>
            {
                if (answer == always)
                {
                    context.Settings.Values.GitSmartCommit = "always";
                    context.Settings.Save();
                    context.Settings.Refresh("git_smart_commit");

                    // Said where it went, so it can be found again to be put back
                    context.Notices.Notify(context.Strings.Said("git.smart_always_set"), false,
                        (context.Strings.Said("git.smart_always_settings"), () => context.Windows.OpenSettings(EditorSettingsCatalog.GIT)));
                }
                else if (answer != stage)
                {
                    return;
                }

                PromptCommit(repository, branch, all, anyStaged: false, then, flags, message);
            });
    }

    // The box for the commit's message, and the commit once it's typed, everything staged first when nothing was. It starts
    // with the message of a commit just taken back, or what git wrote for a merge going on. A message typed in the box
    // over the side already is committed with as it is, nothing asked
    private void PromptCommit(Repository repository, string branch, string[] all, bool anyStaged, CommitThen then, CommitFlags flags, string? typed)
    {
        if (typed is { } given && given.Trim().Length > 0)
        {
            CommitNow(repository, given.Trim(), all, anyStaged, then, flags);
            return;
        }

        string start = WaitingMessage(repository) ?? string.Empty;
        context.Windows.PromptLines(context.Strings.Said("git.commit_title", ("branch", branch)),
            anyStaged ? context.Strings.Said("git.commit_text_staged") : context.Strings.Said("git.commit_text_all", ("count", all.Length.ToString())),
            start, context.Strings.Said("git.commit_button"), "COMMIT_EDITMSG.md", SummaryNote, typed =>
            {
                string message = typed.Trim();
                if (message.Length > 0)
                    CommitNow(repository, message, all, anyStaged, then, flags);
            });
    }

    /// <summary>The message a repository's next commit starts with, a commit's that was taken back or a merge's, null for none.</summary>
    private string? WaitingMessage(Repository repository) => undone.GetValueOrDefault(repository.Git.Root) ?? MergeOf(repository);

    // The commit itself in the background, everything staged first when nothing was, and what's after it once it went in
    private void CommitNow(Repository repository, string message, string[] all, bool anyStaged, CommitThen then, CommitFlags flags)
    {
        // Said the way the terminal would have it, so a card of a hook that said no names what was run
        string root = repository.Git.Root;
        bool noVerify = flags.HasFlag(CommitFlags.NoVerify), signOff = flags.HasFlag(CommitFlags.SignOff);
        string doing = "git commit" + (noVerify ? " --no-verify" : string.Empty) + (signOff ? " --signoff" : string.Empty);
        Run(repository, doing, () =>
        {
            if (!anyStaged && repository.Git.Stage(all) is { } failed)
                return failed;
            return flags == CommitFlags.None ? repository.Git.Commit(message) : repository.Git.Commit(message, noVerify, signOff);
        }, context.Strings.Said("git.committed", ("message", message.Split('\n')[0])), () =>
        {
            undone.Remove(root);
            Committed?.Invoke(repository.Git.Root);
            if (then == CommitThen.Push)
                Remote(repository, RemoteWork.Push);
            else if (then == CommitThen.Sync)
                AskSync(repository);
        });
    }

    /// <summary>
    /// The last commit made again, its message in the box to change. Whatever is staged goes in with it, with nothing
    /// staged it's the message alone.
    /// </summary>
    private void AskAmend(Repository? repository)
    {
        if (repository?.Status is not { } status)
            return;

        int conflicts = status.Files.Values.Count(state => state.Change == GitChange.Conflicted);
        if (conflicts > 0)
        {
            context.Notices.Say(context.Strings.Said("git.resolve_first", ("count", HexText.Count("file", conflicts))), error: true);
            return;
        }

        string branch = status.Branch ?? "HEAD";
        context.Work.InBackground("git log", repository.Git.LastMessage, last =>
        {
            if (last is null)
            {
                context.Notices.Say(context.Strings.Said("git.nothing_to_amend"));
                return;
            }

            bool anyStaged = repository.Status?.Files.Values.Any(state => state.Staged) == true;
            context.Windows.PromptLines(context.Strings.Said("git.amend_title", ("branch", branch)),
                anyStaged ? context.Strings.Said("git.amend_text_staged") : context.Strings.Said("git.amend_text_message"),
                last, context.Strings.Said("git.amend_button"), "COMMIT_EDITMSG.md", SummaryNote, typed =>
                {
                    string message = typed.Trim();
                    if (message.Length == 0)
                        return;

                    Run(repository, "git commit --amend", () => repository.Git.Amend(message), context.Strings.Said("git.amended", ("message", message.Split('\n')[0])));
                });
        }, quiet: true);
    }

    /// <summary>The last commit taken back, what it changed left staged and its message kept for the next commit's box.</summary>
    private void UndoLastCommit(Repository? repository)
    {
        if (repository is null)
            return;

        string root = repository.Git.Root;
        const string DOING = "git reset --soft HEAD~1";
        context.Work.InBackground(DOING, () =>
        {
            string? message = repository.Git.LastMessage();
            return (Message: message, Failed: repository.Git.UndoLastCommit());
        }, result =>
        {
            repository.Dirty(now: true);
            if (result.Failed is { } failed)
            {
                FailureCard(context.Strings.Said("git.failed", ("doing", DOING), ("why", failed.Split('\n')[0].Trim())), error: true,
                    (context.Strings.Said("git.details"), () => context.Windows.ShowText(DOING, $"```\n{failed}\n```\n")));
                return;
            }

            ClearFailure();
            string message = result.Message ?? string.Empty;
            if (message.Length > 0)
                undone[root] = message;
            context.Notices.Say(context.Strings.Said("git.undid", ("subject", message.Split('\n')[0])));
        }, quiet: true);
    }

    /// <summary>
    /// What's said under the commit's box, how long its first line is, a warning once it's too long to show whole. How long
    /// is best and past what it's cut short (git log --oneline and GitHub) are the settings, 50 and 72 as they come.
    /// </summary>
    internal (string Text, bool Warning) SummaryNote(string message)
    {
        int best = context.Settings.Values.GitSubjectLength, most = Math.Max(best, context.Settings.Values.GitLineLength);
        string summary = message.Replace("\r\n", "\n").Split('\n')[0];
        int length = summary.Trim().Length;
        if (length == 0)
            return (context.Strings.Said("git.commit_hint"), false);
        if (length > most)
            return (context.Strings.Said("git.summary_too_long", ("length", length.ToString()), ("most", most.ToString())), true);
        return (context.Strings.Said("git.summary_length", ("length", length.ToString()), ("best", best.ToString())), length > best);
    }
}
