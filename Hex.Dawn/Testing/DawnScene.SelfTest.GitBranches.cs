using Hex.Core.Git;

namespace Hex.Dawn;

// The self test, branch work from the Git menu. A repository of its own with a file of it open, so the commands work on
// it, a branch merged in fast forward, renamed, tags made and deleted, a commit reverted, a branch that isn't merged
// deleted only once it's forced, and a merge that stops on a conflict left in the merge state. The Commit row is
// checked on the way, a merge's plain Commit and a clean repository's dim one with no count of nothing.
internal sealed partial class DawnScene
{
    private void TestGitBranches(HexPointerScript test)
    {
        string repo = Path.Combine(testFolder, "git_branches");
        string file = Path.Combine(repo, "a.txt");
        HexDocument? before = null;

        string Head(string what = "HEAD") => GitProcess.Run(repo, ["rev-parse", what]).Output.Trim();
        string Subject() => GitProcess.Run(repo, ["log", "-1", "--format=%s"]).Output.Trim();
        string[] Local() => GitProcess.Run(repo, ["branch", "--format=%(refname:short)"]).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        IReadOnlyList<GitTag> Tags() => GitRepository.Find(repo)?.Tags() ?? [];
        ListBox? Chooser() => textDialog is { IsOpen: true } open ? Descendants(open.Body).OfType<ListBox>().FirstOrDefault() : null;
        bool Lists(string containing) => Chooser()?.Items.Any(item => item.Contains(containing, StringComparison.Ordinal)) == true;
        void Pick(string containing)
        {
            ListBox list = Chooser()!;
            int at = Array.FindIndex(list.Items, item => item.Contains(containing, StringComparison.Ordinal));
            list.Choose(at);
            list.OnActivated!(list.Items[at]);
        }
        void Submit(string typed) => (textDialog is { IsOpen: true } asked ? Descendants(asked.Body).OfType<TextBox>().FirstOrDefault() : null)?.OnSubmitted?.Invoke(typed);
        void Press(string label) => textDialog?.Buttons.FirstOrDefault(button => button.Label == label)?.OnPressed?.Invoke();
        void Command(string key) => GitCommand(Said(key))?.OnPicked?.Invoke();

        // The rows of this repository on the side, under its name when the workspace has another
        IEnumerable<UITreeItem> Rows() => sideTree.Items.FirstOrDefault(row => row.Text.StartsWith("git_branches", StringComparison.Ordinal))?.Children ?? sideTree.Items;
        UITreeItem? CommitRow() => Rows().FirstOrDefault(row => row.Icon == "check" && row.Text.StartsWith("Commit", StringComparison.Ordinal));

        string[] commands =
        [
            "git_branch.merge_command", "git_branch.rebase_command", "git_branch.delete_command", "git_branch.rename_command",
            "git_branch.tag_command", "git_branch.delete_tag_command", "git_branch.cherry_pick_command", "git_branch.revert_command",
            "git.commit_no_verify", "git.commit_signoff", "git.pull_rebase"
        ];
        test.Check("branch ops: the Git menu has Merge, Rebase, Delete and Rename Branch, the tags, Cherry-pick, Revert, the two commits and Pull (Rebase)", () =>
            commands.All(key => GitCommand(Said(key)) is not null));

        test.Run(() =>
        {
            chrome.Dialog?.Close();
            before = document;
            DeleteGitFolder(repo);
            Directory.CreateDirectory(repo);
            File.WriteAllText(file, "base\n");
            Git(repo, "init", "-q", "-b", "main");
            Git(repo, "config", "user.name", "Hex self test");
            Git(repo, "config", "user.email", "selftest@example.com");
            Git(repo, "config", "core.autocrlf", "false");
            Git(repo, "add", ".");
            Git(repo, "commit", "-q", "-m", "Base");
            Git(repo, "switch", "-q", "-c", "b");
            File.WriteAllText(Path.Combine(repo, "b.txt"), "from b\n");
            Git(repo, "add", ".");
            Git(repo, "commit", "-q", "-m", "On b");
            Git(repo, "switch", "-q", "main");
            Open(file);
            git.Refresh(repo);
            ShowSide(HexSide.SourceControl);
        });

        // A clean repository's Commit row is a dim Commit, no 0 in it, and the note under it short with the rest on the pointer
        test.WaitFor(() => CommitRow() is not null && git.StatusFor(file)?.Branch == "main", 5.0f);
        test.Check("branch ops: with nothing changed the Commit row is a dim Commit with no count, the note short and the whole of it its tooltip", () =>
            CommitRow() is { } row && row.Text == Said("git.commit_plain") && row.Color == DimColor && !row.Text.Contains('0')
            && Rows().Any(note => note.Text == Said("git.no_changes") && note.Tooltip == Said("git_branch.nothing_to_commit")));

        // Merge Branch... lists b with the branch rows' look, and merging it is a fast forward, HEAD moved to b's commit
        test.Run(() => Command("git_branch.merge_command"));
        test.WaitFor(() => Lists(": b   "), 3.0f);
        test.Check("branch ops: Merge Branch lists the other branches, not the one checked out", () => Lists(": b   ") && !Lists(": main   "));
        test.Run(() => Pick(": b   "));
        test.WaitFor(() => Head() == Head("b"), 5.0f);
        test.Check("branch ops: merging b fast forwards, HEAD on b's commit and no merge commit", () =>
            Head() == Head("b") && GitProcess.Run(repo, ["rev-list", "--parents", "-n", "1", "HEAD"]).Output.Trim().Split(' ').Length == 2);

        // Rename Branch... renames the one checked out, the box starting with its name
        test.Run(() =>
        {
            Git(repo, "switch", "-q", "b");
            git.Refresh(repo);
        });
        test.WaitFor(() => git.StatusFor(file)?.Branch == "b", 4.0f);
        test.Run(() => Command("git_branch.rename_command"));
        test.WaitFor(() => textDialog is { IsOpen: true }, 3.0f);
        test.Check("branch ops: Rename Branch starts with the name of the branch checked out", () =>
            textDialog is { IsOpen: true } open && Descendants(open.Body).OfType<TextBox>().FirstOrDefault()?.Text == "b");
        test.Run(() => Submit("c"));
        test.WaitFor(() => Local().Contains("c"), 5.0f);
        test.Check("branch ops: renaming b to c lists c and not b", () => Local().Contains("c") && !Local().Contains("b"));
        test.Run(() =>
        {
            Git(repo, "switch", "-q", "main");
            git.Refresh(repo);
        });
        test.WaitFor(() => git.StatusFor(file)?.Branch == "main", 4.0f);

        // Create Tag... asks a name then a message, none for a lightweight tag, and Delete Tag... lists it to delete
        test.Run(() => Command("git_branch.tag_command"));
        test.WaitFor(() => textDialog is { IsOpen: true }, 3.0f);
        test.Run(() => Submit("v1"));
        test.WaitFor(() => textDialog is { IsOpen: true } open && open.Buttons.Any(button => button.Label == Said("git_branch.tag_button")), 3.0f);
        test.Run(() => Submit(string.Empty));
        test.WaitFor(() => Tags().Any(tag => tag.Name == "v1"), 5.0f);
        test.Check("branch ops: Create Tag v1 makes a tag Tags() has", () => Tags().Any(tag => tag.Name == "v1"));
        test.Run(() => Command("git_branch.delete_tag_command"));
        test.WaitFor(() => Lists(": v1   "), 3.0f);
        test.Run(() => Pick(": v1   "));
        test.WaitFor(() => Tags().Count == 0, 5.0f);
        test.Check("branch ops: Delete Tag takes it away again", () => Tags().Count == 0);

        // Revert Commit... lists HEAD's commits, and the one picked is taken back by a commit of its own
        test.Run(() => Command("git_branch.revert_command"));
        test.WaitFor(() => Lists(": On b"), 3.0f);
        test.Run(() => Pick(": On b"));
        test.WaitFor(() => Subject().StartsWith("Revert", StringComparison.Ordinal), 5.0f);
        test.Check("branch ops: Revert Commit adds a commit that takes it back", () =>
            Subject() == "Revert \"On b\"" && !File.Exists(Path.Combine(repo, "b.txt")));

        // A branch merged already deletes straight away, one with a commit nowhere else asks to be forced first
        test.Run(() => Command("git_branch.delete_command"));
        test.WaitFor(() => Lists(": c   "), 3.0f);
        test.Run(() => Pick(": c   "));
        test.WaitFor(() => !Local().Contains("c"), 5.0f);
        test.Check("branch ops: Delete Branch deletes a merged branch without asking", () => !Local().Contains("c") && textDialog is not { IsOpen: true });
        test.Run(() =>
        {
            Git(repo, "switch", "-q", "-c", "d");
            File.WriteAllText(Path.Combine(repo, "d.txt"), "only on d\n");
            Git(repo, "add", ".");
            Git(repo, "commit", "-q", "-m", "On d");
            Git(repo, "switch", "-q", "main");
            git.Refresh(repo);
        });
        test.WaitFor(() => git.StatusFor(file)?.Branch == "main", 4.0f);
        test.Run(() => Command("git_branch.delete_command"));
        test.WaitFor(() => Lists(": d   "), 3.0f);
        test.Run(() => Pick(": d   "));
        test.WaitFor(() => textDialog is { IsOpen: true } open && open.Buttons.Any(button => button.Label == Said("git_branch.force_delete")), 5.0f);
        test.Check("branch ops: deleting a branch that isn't merged asks to Force Delete, and it's still there", () =>
            textDialog is { IsOpen: true } open && open.Buttons.Any(button => button.Label == Said("git_branch.force_delete")) && Local().Contains("d"));
        test.Run(() => Press(Said("git_branch.force_delete")));
        test.WaitFor(() => !Local().Contains("d"), 5.0f);
        test.Check("branch ops: and Force Delete deletes it", () => !Local().Contains("d"));

        // A merge that stops on a conflict is the merge state, Merging on the side, the C row and a Commit that finishes it
        test.Run(() =>
        {
            Git(repo, "switch", "-q", "-c", "x");
            File.WriteAllText(file, "theirs\n");
            Git(repo, "commit", "-q", "-am", "Theirs");
            Git(repo, "switch", "-q", "main");
            File.WriteAllText(file, "ours\n");
            Git(repo, "commit", "-q", "-am", "Ours");
            git.Refresh(repo);
        });
        test.WaitFor(() => git.StatusFor(file)?.Branch == "main" && GitProcess.Run(repo, ["status", "--porcelain"]).Output.Trim().Length == 0, 4.0f);
        test.Run(() => Command("git_branch.merge_command"));
        test.WaitFor(() => Lists(": x   "), 3.0f);
        test.Run(() => Pick(": x   "));
        test.WaitFor(() => git.OperationAt(file) == GitOperation.Merging && Rows().Any(row => row.Text == Said("git.merging")) && CommitRow()?.Text == Said("git.commit_merge"), 6.0f);
        test.Check("branch ops: a merge that conflicts leaves git merging, the Merging row and the file a C under Merge Changes", () =>
            git.OperationAt(file) == GitOperation.Merging && Rows().Any(row => row.Text == Said("git.merging"))
            && Rows().FirstOrDefault(row => row.Text == Said("git.merge_changes"))?.Children?.Any(row => row.Badge == "C") == true);
        test.Check("branch ops: mid merge the Commit row says Commit, no count, and the card says 1 conflict to settle", () =>
            CommitRow() is { } row && row.Text == Said("git.commit_merge") && row.Color is null
            && toasts.Cards.Any(card => !card.Closed && card.Text.Contains(HexText.Count("conflict", 1), StringComparison.Ordinal)));

        test.Run(() =>
        {
            Git(repo, "merge", "--abort");
            chrome.Dialog?.Close();
            foreach (UINotification card in toasts.Cards)
                card.Close();
            if (document.Path is { } open && PlatformPaths.IsUnder(open, repo))
                CloseNow();
            if (before is not null && documents.Contains(before))
                Show(before);
        });
        test.Wait();
        test.Run(() => DeleteGitFolder(repo));
    }
}
