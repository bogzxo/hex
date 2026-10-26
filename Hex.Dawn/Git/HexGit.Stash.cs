using Hex.Core.Git;

namespace Hex.Dawn;

// The stash, VS Code's Git > Stash. What's changed put away with a message (the files nobody added too, when asked),
// the working tree back to the last commit, and brought back later, the newest straight away or any of them picked
// from a list, applied and kept, popped, dropped after asking, or shown as a patch. All of it git stash itself, so a
// stash made here is the terminal's too and the other way round.
internal sealed partial class HexGit
{
    private void BuildStash()
    {
        context.Commands.Add("Git/stash", context.Strings.Said("git_stash.stash"), () => AskStash(FirstRepository(), includeUntracked: false), icon: "git-stash");
        context.Commands.Add("Git/stash", context.Strings.Said("git_stash.stash_untracked"), () => AskStash(FirstRepository(), includeUntracked: true), icon: "git-stash");
        context.Commands.Add("Git/stash", context.Strings.Said("git_stash.pop_latest"), () => PopLatest(FirstRepository()), icon: "git-stash-pop");
        context.Commands.Add("Git/stash", context.Strings.Said("git_stash.apply"), () => PickStash(FirstRepository(), "git_stash.apply_title", ApplyStash), icon: "git-stash-apply");
        context.Commands.Add("Git/stash", context.Strings.Said("git_stash.pop"), () => PickStash(FirstRepository(), "git_stash.pop_title", (repository, entry) => PopStash(repository, entry.Index, entry.Name)), icon: "git-stash-pop");
        context.Commands.Add("Git/stash", context.Strings.Said("git_stash.drop"), () => PickStash(FirstRepository(), "git_stash.drop_title", AskDrop), icon: "trash");
        context.Commands.Add("Git/stash", context.Strings.Said("git_stash.show"), () => PickStash(FirstRepository(), "git_stash.show_title", ShowStash), icon: "eye");
    }

    /// <summary>Asks for the stash's message, which can be left empty for git's own, then stashes.</summary>
    private void AskStash(Repository? repository, bool includeUntracked)
    {
        if (repository is null)
            return;

        context.Windows.Prompt(context.Strings.Said("git_stash.message_title"),
            context.Strings.Said(includeUntracked ? "git_stash.message_text_untracked" : "git_stash.message_text"), string.Empty,
            context.Strings.Said("git_stash.message_button"), context.Strings.Said("git_stash.message_placeholder"),
            typed => Stash(repository, typed, includeUntracked));
    }

    private void Stash(Repository repository, string message, bool includeUntracked)
    {
        // Nothing to stash is git's exit 0 with a line saying so, a notice and not a card of something gone wrong
        context.Work.InBackground("git stash", () => repository.Git.PushStash(message, includeUntracked), failed =>
        {
            repository.Dirty(now: true);
            if (failed is not null && failed.Contains("No local changes to save", StringComparison.Ordinal))
                context.Notices.Say(context.Strings.Said("git_stash.nothing"));
            else
                StashDone(repository, "git stash", failed, context.Strings.Said("git_stash.stashed"));
        }, quiet: true);
    }

    /// <summary>The newest stash brought back and dropped, a notice when there's none.</summary>
    private void PopLatest(Repository? repository)
    {
        if (repository is null)
            return;

        context.Work.InBackground("git stash list", repository.Git.List, stashes =>
        {
            if (stashes.Count == 0)
                context.Notices.Say(context.Strings.Said("git_stash.none"));
            else
                PopStash(repository, stashes[0].Index, stashes[0].Name);
        }, quiet: true);
    }

    /// <summary>The stashes to pick one from, the newest first, each with its message and how long ago, and what's done to the one picked.</summary>
    private void PickStash(Repository? repository, string title, Action<Repository, GitStashEntry> picked)
    {
        if (repository is null)
            return;

        context.Work.InBackground("git stash list", repository.Git.List, stashes =>
        {
            if (stashes.Count == 0)
            {
                context.Notices.Say(context.Strings.Said("git_stash.none"));
                return;
            }

            // Each its name in a hash's colour, the message bright and how long ago in a date's
            var choices = new List<UIChoice>();
            foreach (GitStashEntry entry in stashes)
            {
                string ago = Ago(entry.When);
                choices.Add(new UIChoice(entry.Short, () => picked(repository, entry))
                {
                    Segments = [(entry.Name, UIChoiceTone.Hash), (entry.Short, UIChoiceTone.Primary), (ago, UIChoiceTone.Date)],
                    Id = $"{entry.Name}  {entry.Short}  {ago}"
                });
            }
            context.Windows.Choose(context.Strings.Said(title), context.Strings.Said("git_stash.pick_text"), choices);
        }, quiet: true);
    }

    private void ApplyStash(Repository repository, GitStashEntry entry) =>
        Stashing(repository, $"git stash apply {entry.Name}", () => repository.Git.Apply(entry.Index), context.Strings.Said("git_stash.applied", ("name", entry.Name)));

    /// <summary>A stash brought back and dropped, kept by git when it didn't come back cleanly.</summary>
    private void PopStash(Repository repository, int index, string name) =>
        Stashing(repository, $"git stash pop {name}", () => repository.Git.Pop(index), context.Strings.Said("git_stash.popped", ("name", name)));

    // Dropping can't be undone in Hex, so it asks first
    private void AskDrop(Repository repository, GitStashEntry entry)
    {
        string yes = context.Strings.Said("git_stash.drop_yes");
        context.Windows.Ask(context.Strings.Said("git_stash.drop_confirm_title", ("name", entry.Name)),
            context.Strings.Said("git_stash.drop_confirm_text", ("message", entry.Short)),
            [context.Strings.Said("git_stash.drop_no"), yes], answer =>
            {
                if (answer == yes)
                    Stashing(repository, $"git stash drop {entry.Name}", () => repository.Git.Drop(entry.Index), context.Strings.Said("git_stash.dropped", ("name", entry.Name)));
            });
    }

    // What a stash changed, as a patch in a window of its own
    private void ShowStash(Repository repository, GitStashEntry entry)
    {
        context.Work.InBackground("git stash show", () => repository.Git.Show(entry.Index), shown =>
        {
            if (shown.Failed is { } failed)
            {
                StashDone(repository, $"git stash show {entry.Name}", failed, null);
                return;
            }

            string patch = shown.Patch.Trim().Length > 0 ? shown.Patch.TrimEnd() : context.Strings.Said("git_stash.empty_patch");
            context.Windows.ShowText(context.Strings.Said("git_stash.show_heading", ("name", entry.Name), ("message", entry.Short)), $"```diff\n{patch}\n```\n");
        }, quiet: true);
    }

    // Something of the stash's run in the background, then the repository asked again and what came of it said
    private void Stashing(Repository repository, string doing, Func<string?> work, string done) =>
        context.Work.InBackground(doing, work, failed => StashDone(repository, doing, failed, done), quiet: true);

    private void StashDone(Repository repository, string doing, string? failed, string? done)
    {
        repository.Dirty(now: true);
        if (failed is null)
        {
            ClearFailure();
            if (done is not null)
                context.Notices.Say(done);
            return;
        }

        FailureCard(context.Strings.Said("git.failed", ("doing", doing), ("why", failed.Split('\n')[0].Trim())), error: true,
            (context.Strings.Said("git.details"), () => context.Windows.ShowText(doing, $"```text\n{failed}\n```\n")));
    }
}
