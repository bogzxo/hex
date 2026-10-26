using Hex.Core.Git;

namespace Hex.Dawn;

// The self test, the stash and git stopped in the middle of something. A repository of its own beside the git suite's,
// a file of it open so the Git menu and the status bar follow it, stashed and popped, applied, shown and dropped from
// the pickers, a merge left in conflict and called off, a branch published, and a switch refused for changes in the
// way stashed around. The suite's own repository is left as it was.
internal sealed partial class DawnScene
{
    private void TestGitStash(HexPointerScript test)
    {
        string repo = Path.Combine(testFolder, "git_stash_repo"), origin = Path.Combine(testFolder, "git_stash_origin.git");
        string tracked = Path.Combine(repo, "a.txt"), fresh = Path.Combine(repo, "fresh.txt"), kept = Path.Combine(repo, "keep.txt");
        HexDocument? before = null;

        int Stashes() => GitProcess.Run(repo, ["stash", "list"]).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
        string Read(string file) => File.Exists(file) ? File.ReadAllText(file).Replace("\r\n", "\n") : string.Empty;
        string Branch(string suffix = "") => "$(git-branch) main" + suffix;
        IEnumerable<UITreeItem> SideRows(IEnumerable<UITreeItem> rows) => rows.SelectMany(row => new[] { row }.Concat(SideRows(row.Children ?? [])));
        ListBox Chooser() => Descendants(textDialog!.Body).OfType<ListBox>().First();
        void Submit(string typed) => (textDialog is { IsOpen: true } asked ? Descendants(asked.Body).OfType<TextBox>().FirstOrDefault() : null)?.OnSubmitted?.Invoke(typed);
        void Pick(string containing)
        {
            ListBox list = Chooser();
            int at = Array.FindIndex(list.Items, item => item.Contains(containing, StringComparison.Ordinal));
            list.Choose(at);
            list.OnActivated!(list.Items[at]);
        }
        void Press(string label) => textDialog?.Buttons.FirstOrDefault(button => button.Label == label)?.OnPressed?.Invoke();
        UINotification? Card(string action) => toasts.Cards.LastOrDefault(card => !card.Closed && card.Actions.Any(pair => pair.Label == action));

        test.Run(() =>
        {
            chrome.Dialog?.Close();
            before = document;
            DeleteGitFolder(repo);
            DeleteGitFolder(origin);
            Directory.CreateDirectory(repo);
            File.WriteAllText(tracked, "one\n");
            File.WriteAllText(kept, "never touched\n");
            Git(repo, "init", "-q", "-b", "main");
            Git(repo, "config", "user.name", "Hex self test");
            Git(repo, "config", "user.email", "selftest@example.com");
            Git(repo, "config", "core.autocrlf", "false");
            Git(repo, "add", ".");
            Git(repo, "commit", "-q", "-m", "First go");
            File.WriteAllText(tracked, "two\n");
            File.WriteAllText(fresh, "new\n");
            Open(kept);
            git.Refresh(repo);
        });

        // A star after the branch while anything's changed, VS Code's
        test.WaitFor(() => branchItem.Text == Branch("*"), 5.0f);
        test.Check("stash: the branch has a star while the tree has changes", () => branchItem.Text == Branch("*"));

        // Stash (Include Untracked)... takes the new file too, and the side has nothing changed after it
        test.Run(() => GitCommand(Said("git_stash.stash_untracked"))?.OnPicked?.Invoke());
        test.WaitFor(() => textDialog is { IsOpen: true }, 3.0f);
        test.Run(() => Submit("half done"));
        test.WaitFor(() => Stashes() == 1 && branchItem.Text == Branch(), 5.0f);
        test.Run(() => ShowSide(HexSide.SourceControl));
        test.WaitFor(() => SideRows(sideTree.Items).Any(row => row.Text == Said("git.no_changes")), 4.0f);
        test.Check("stash: Stash (Include Untracked) puts it all away, the star goes and the side has no changes", () =>
            Stashes() == 1 && Read(tracked) == "one\n" && !File.Exists(fresh) && branchItem.Text == Branch()
            && SideRows(sideTree.Items).Any(row => row.Text == Said("git.no_changes"))
            && GitProcess.Run(repo, ["stash", "list", "--format=%s"]).Output.Contains("half done", StringComparison.Ordinal));

        // Pop Latest Stash has it all back
        test.Run(() => GitCommand(Said("git_stash.pop_latest"))?.OnPicked?.Invoke());
        test.WaitFor(() => Stashes() == 0 && branchItem.Text == Branch("*"), 5.0f);
        test.Check("stash: Pop Latest Stash brings it back, the new file too", () =>
            Stashes() == 0 && Read(tracked) == "two\n" && Read(fresh) == "new\n" && branchItem.Text == Branch("*"));

        // Stash... leaves the file nobody added where it is
        test.Run(() => GitCommand(Said("git_stash.stash"))?.OnPicked?.Invoke());
        test.WaitFor(() => textDialog is { IsOpen: true }, 3.0f);
        test.Run(() => Submit("tracked only"));
        test.WaitFor(() => Stashes() == 1, 5.0f);
        test.Check("stash: Stash... takes what git knows and leaves the new file", () => Read(tracked) == "one\n" && File.Exists(fresh));

        // Apply Stash... from the picker brings it back and keeps it
        test.Run(() => GitCommand(Said("git_stash.apply"))?.OnPicked?.Invoke());
        test.WaitFor(() => textDialog is { IsOpen: true } && Chooser().Items.Length == 1, 3.0f);
        test.Check("stash: the picker lists stash@{0} with its message", () =>
            Chooser().Items[0].StartsWith("stash@{0}  tracked only", StringComparison.Ordinal));
        test.Run(() => Pick("stash@{0}"));
        test.WaitFor(() => Read(tracked) == "two\n", 5.0f);
        test.Check("stash: Apply Stash... brings it back and it stays in the stash", () => Read(tracked) == "two\n" && Stashes() == 1);

        // Show Stash... is the patch in a window of its own
        WindowPanel? Shown() => Descendants(chrome.Root).OfType<WindowPanel>().FirstOrDefault(window => window.Title.StartsWith("stash@{0}", StringComparison.Ordinal));
        test.Run(() => GitCommand(Said("git_stash.show"))?.OnPicked?.Invoke());
        test.WaitFor(() => textDialog is { IsOpen: true } && Chooser().Items.Length == 1, 3.0f);
        test.Run(() => Pick("stash@{0}"));
        test.WaitFor(() => Shown() is not null, 4.0f);
        test.Check("stash: Show Stash... shows its patch as a diff", () =>
            Shown() is { } window && Descendants(window).OfType<Label>().Any(label => label.Text.StartsWith("```diff", StringComparison.Ordinal) && label.Text.Contains("+two", StringComparison.Ordinal)));
        test.Run(() => Shown()?.Close());

        // Drop Stash... asks first, then it's gone
        test.Run(() => GitCommand(Said("git_stash.drop"))?.OnPicked?.Invoke());
        test.WaitFor(() => textDialog is { IsOpen: true } && Chooser().Items.Length == 1, 3.0f);
        test.Run(() => Pick("stash@{0}"));
        test.WaitFor(() => textDialog is { IsOpen: true } dialog && dialog.Buttons.Any(button => button.Label == Said("git_stash.drop_yes")), 3.0f);
        test.Check("stash: Drop Stash... asks first", () => Stashes() == 1);
        test.Run(() => Press(Said("git_stash.drop_yes")));
        test.WaitFor(() => Stashes() == 0, 4.0f);
        test.Check("stash: and Drop throws it away", () => Stashes() == 0 && Read(tracked) == "two\n");

        // Outside a merge Abort is greyed, in one with conflicts the branch says so and Abort calls it off
        MenuItem? Abort() => GitCommand(Said("git_merge.abort_merge"));
        test.Run(() =>
        {
            Git(repo, "add", "-A");
            Git(repo, "commit", "-q", "-m", "Second go");
            Git(repo, "switch", "-q", "-c", "other");
            File.WriteAllText(tracked, "theirs\n");
            Git(repo, "commit", "-q", "-am", "Theirs");
            Git(repo, "switch", "-q", "main");
            File.WriteAllText(tracked, "ours\n");
            Git(repo, "commit", "-q", "-am", "Ours");
            git.Refresh(repo);
        });
        test.WaitFor(() => branchItem.Text == Branch(), 5.0f);
        test.Check("merge: outside a merge Abort Merge is greyed out", () => Abort() is { IsEnabled: { } enabled } && !enabled());
        test.Run(() =>
        {
            GitProcess.Run(repo, ["merge", "other"]);
            git.Refresh(repo);
        });
        test.WaitFor(() => branchItem.Text == Branch("* (Merging)"), 5.0f);
        test.Check("merge: a merge with conflicts says main* (Merging) and Abort Merge can be picked", () =>
            branchItem.Text == Branch("* (Merging)") && Abort() is { IsEnabled: { } enabled } && enabled());
        test.Run(() => Abort()?.OnPicked?.Invoke());
        test.WaitFor(() => textDialog is { IsOpen: true } dialog && dialog.Buttons.Any(button => button.Label == Said("git_merge.abort_merge")), 3.0f);
        test.Check("merge: Abort Merge asks first", () => GitRepository.Find(repo)!.Operation() == GitOperation.Merging);
        test.Run(() => Press(Said("git_merge.abort_merge")));
        test.WaitFor(() => branchItem.Text == Branch(), 5.0f);
        test.Check("merge: and calls it off, the files as they were and the suffix gone", () =>
            branchItem.Text == Branch() && Read(tracked) == "ours\n" && GitRepository.Find(repo)!.Operation() == GitOperation.None);

        // The same merge again, settled and staged, and Continue Merge is the commit box
        test.Run(() =>
        {
            chrome.Dialog?.Close();
            GitProcess.Run(repo, ["merge", "other"]);
            File.WriteAllText(tracked, "both\n");
            Git(repo, "add", "a.txt");
            git.Refresh(repo);
        });
        test.WaitFor(() => branchItem.Text == Branch("* (Merging)"), 5.0f);
        test.Run(() => GitCommand(Said("git_merge.continue_merge"))?.OnPicked?.Invoke());
        test.WaitFor(() => textDialog is { IsOpen: true } dialog && dialog.Buttons.Any(button => button.Label == Said("git.commit_button")), 3.0f);
        test.Check("merge: Continue Merge once it's settled opens the commit box", () =>
            textDialog is { IsOpen: true } dialog && dialog.Buttons.Any(button => button.Label == Said("git.commit_button")));
        test.Run(() =>
        {
            chrome.Dialog?.Close();
            Git(repo, "merge", "--abort");
            git.Refresh(repo);
        });
        test.WaitFor(() => branchItem.Text == Branch(), 5.0f);

        // Publish Branch can be picked once there's a remote and the branch isn't on it, and pushes it with --set-upstream
        MenuItem? Publish() => GitCommand(Said("git.publish"));
        bool PublishEnabled() => Publish() is { IsEnabled: { } enabled } && enabled();
        test.Check("sync: Publish Branch is in the Git menu, greyed with no remote", () => Publish() is not null && !PublishEnabled());
        test.Run(() =>
        {
            Git(testFolder, "init", "-q", "--bare", "-b", "main", origin);
            Git(repo, "remote", "add", "origin", origin);
            git.Refresh(repo);
        });
        test.WaitFor(PublishEnabled, 5.0f);
        test.Check("sync: with a remote and no upstream Publish Branch can be picked", PublishEnabled);
        test.Run(() => Publish()?.OnPicked?.Invoke());
        test.WaitFor(() => GitProcess.Run(repo, ["rev-parse", "--abbrev-ref", "main@{upstream}"]).Output.Trim() == "origin/main" && !PublishEnabled(), 10.0f);
        test.Check("sync: Publish Branch pushes it set to follow the remote's, and is greyed after", () =>
            GitProcess.Run(repo, ["rev-parse", "--abbrev-ref", "main@{upstream}"]).Output.Trim() == "origin/main" && !PublishEnabled());

        // A switch refused for changes in the way offers Stash and Switch, which switches and leaves them in stash@{0}
        string stashAndSwitch = Said("git_stash.stash_and_switch");
        test.Run(() =>
        {
            File.WriteAllText(tracked, "in the way\n");
            git.Refresh(repo);
            git.ShowBranches(kept);
        });
        test.WaitFor(() => textDialog is { IsOpen: true } && Chooser().Items.Any(item => item.Contains(": other", StringComparison.Ordinal)), 4.0f);
        test.Run(() => Pick(": other"));
        test.WaitFor(() => Card(stashAndSwitch) is not null, 5.0f);
        test.Check("branch: a switch refused for local changes offers Stash and Switch", () => Card(stashAndSwitch) is not null);
        test.Run(() => Card(stashAndSwitch)?.Actions.First(pair => pair.Label == stashAndSwitch).Pressed());
        test.WaitFor(() => GitProcess.Run(repo, ["branch", "--show-current"]).Output.Trim() == "other" && Stashes() == 1, 5.0f);
        test.WaitFor(() => Card(Said("git_stash.pop_button")) is not null, 3.0f);
        test.Check("branch: Stash and Switch switches and leaves the changes in stash@{0}, with Pop on its notice", () =>
            GitProcess.Run(repo, ["branch", "--show-current"]).Output.Trim() == "other" && Stashes() == 1 && Read(tracked) == "theirs\n"
            && GitProcess.Run(repo, ["stash", "show", "-p", "stash@{0}"]).Output.Contains("+in the way", StringComparison.Ordinal)
            && Card(Said("git_stash.pop_button")) is not null);

        test.Run(() =>
        {
            chrome.Dialog?.Close();
            foreach (UINotification card in toasts.Cards)
                card.Close();
            if (document.Path is { } open && PlatformPaths.IsUnder(open, repo))
                CloseNow();
            if (before is not null && documents.Contains(before))
                Show(before);
        });
        test.Wait();
        test.Run(() =>
        {
            DeleteGitFolder(repo);
            DeleteGitFolder(origin);
        });
    }
}
