using System.Numerics;

using Hex.Core.Git;

namespace Hex.Dawn;

// The self test, the rows of Source Control. Their buttons and keys, the commit's menu, amending and undoing the last
// commit, a deleted file's diff, and a merge's message waiting in the commit box. Part of the git suite, in the
// repository it made.
internal sealed partial class DawnScene
{
    // The box of the commit (or amend) window, null while it isn't up
    private CodeEditor? CommitBox() =>
        textDialog is { IsOpen: true } dialog ? Descendants(dialog.Body).OfType<CodeEditor>().FirstOrDefault() : null;

    private MenuItem? GitMenuItem(string label) =>
        menu.Menus.FirstOrDefault(bar => bar.Title == "Git")?.Items.FirstOrDefault(item => item.Label == label);

    // The middle of one of a row's buttons, found by what it says over it, the row scrolled to first
    private Func<Vector2> TreeAction(Func<UITreeItem?> find, string tooltip) => () =>
    {
        if (find() is { Actions: { } actions } item && actions.ToList().FindIndex(action => action.Tooltip == tooltip) is >= 0 and var index)
        {
            sideTree.Reveal(item);
            if (sideTree.ActionBounds(item, index) is { } bounds && sideTree.Module is { } module)
                last = module.ToWorld(bounds.Center);
        }

        return last;
    };

    private static string LastSubject(string repo) => GitProcess.Run(repo, ["log", "-1", "--format=%s"]).Output.Trim();

    private void TestSourceControlRows(HexPointerScript test, string repo, string outside, string added, string same,
        Func<string, string, UITreeItem?> under, Func<IEnumerable<UITreeItem>, IEnumerable<UITreeItem>> sideRows)
    {
        string stage = Said("git.stage_changes"), unstage = Said("git.unstage_changes");
        UITreeItem? Changed() => under("Changes", outside);
        UITreeItem? Staged() => under("Staged Changes", outside);
        UITreeItem? Heading(string title) => sideRows(sideTree.Items).FirstOrDefault(row => row.Text == title && row.HasChildren);

        // The pointer on a row brings up its buttons, and one click on Stage stages it
        test.Run(() => ShowSide(HexSide.SourceControl));
        test.WaitFor(() => Changed() is not null, 4.0f);
        test.Hover(TreeAction(Changed, stage));
        test.Settle(0.2f);

        // The rows with their letters and the buttons of the one under the pointer, for a look with HEX_SELFTEST_SHOTS set
        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, "source_control_rows.png"));
        });
        test.Settle(0.2f);
        test.Check("source control: the pointer on a row brings up Open File, Discard and Stage, what they do said over them", () =>
            Changed() is { Actions: { } actions } && actions.Select(action => action.Icon).SequenceEqual(["go-to-file", "discard", "add"]) && sideTree.Tooltip == stage);
        test.Click(TreeAction(Changed, stage));
        test.WaitFor(() => Staged() is not null, 4.0f);
        test.Check("source control: one click on Stage moves the file to the staged changes, and doesn't open it", () =>
            Staged() is not null && Changed() is null && !(document.DiffOf is { } of && PlatformPaths.Same(of, outside)));

        // Space on the tree stages and unstages the picked row
        test.Run(() =>
        {
            sideTree.Selected = Staged();
            sideTree.PressKey(' ');
        });
        test.WaitFor(() => Changed() is not null, 4.0f);
        test.Check("source control: Space on a staged file unstages it", () => Changed() is not null && Staged() is null);
        test.Run(() =>
        {
            sideTree.Selected = Changed();
            sideTree.PressKey(' ');
        });
        test.WaitFor(() => Staged() is not null, 4.0f);
        test.Check("source control: and Space on a changed one stages it", () => Staged() is not null && Changed() is null);

        // The headings' buttons, every file of the section at once
        test.Click(TreeAction(() => Heading("Staged Changes"), Said("git.unstage_all")));
        test.WaitFor(() => Changed() is not null, 4.0f);
        test.Check("source control: Unstage All on the staged heading unstages them all", () => Changed() is not null && Staged() is null);
        test.Click(TreeAction(() => Heading("Changes"), Said("git.stage_all")));
        test.WaitFor(() => Staged() is not null, 4.0f);
        test.Check("source control: and Stage All on the changes' heading stages them all", () => Staged() is not null && Changed() is null);
        test.Click(TreeAction(Staged, unstage));
        test.WaitFor(() => Changed() is not null, 4.0f);
        test.Check("source control: Unstage on a staged row takes it out again", () => Changed() is not null && Staged() is null);

        // Delete asks first, Enter shows the change
        test.Run(() =>
        {
            sideTree.Selected = Changed();
            sideTree.PressKey(UIKeyboard.DELETE);
        });
        test.WaitFor(() => textDialog is { IsOpen: true }, 3.0f);
        test.Check("source control: Delete on a changed file asks before throwing it away", () =>
            textDialog is { IsOpen: true } dialog && dialog.Buttons.Any(button => button.Label == Said("git.discard_yes")) && File.Exists(outside));
        test.Run(() => textDialog?.Buttons.First(button => button.Label == Said("git.discard_no")).OnPressed?.Invoke());
        test.Wait();
        test.Run(() =>
        {
            sideTree.Selected = Changed();
            sideTree.PressKey(UIKeyboard.ENTER);
        });
        test.WaitFor(() => document.DiffOf is { } of && PlatformPaths.Same(of, outside), 4.0f);
        test.Check("source control: Enter on a file shows what changed in it", () =>
            File.Exists(outside) && document.DiffOf is { } of && PlatformPaths.Same(of, outside));
        test.Run(() =>
        {
            if (document.DiffOf is not null) CloseNow();
            ShowSide(HexSide.SourceControl);
        });

        // A deleted file's row, and its diff, all of what it had taken out
        test.Run(() => File.Delete(same));
        test.WaitFor(() => under("Changes", same) is { Badge: "D" }, 5.0f);
        test.Check("source control: a deleted file has its D in the error colour, and no Open File", () =>
            under("Changes", same) is { Badge: "D" } row && row.BadgeColor == compositor.Skin!.Palette.Error
            && row.Actions is { } actions && actions.All(action => action.Icon != "go-to-file"));
        test.Click(TreeRow(() => under("Changes", same)));
        test.WaitFor(() => document.DiffOf is { } of && PlatformPaths.Same(of, same), 4.0f);
        test.Check("source control: a click on a deleted file shows what went, all of it", () =>
            document.DiffOf is { } of && PlatformPaths.Same(of, same) && code.Text.Contains("never touched", StringComparison.Ordinal) && document.DiffChanges.Count == 1);
        test.Run(() =>
        {
            if (document.DiffOf is not null) CloseNow();
            Git(repo, "checkout", "--", "same.txt");
            ShowSide(HexSide.SourceControl);
        });
        test.WaitFor(() => under("Changes", same) is null && sideRows(sideTree.Items).Any(row => row.Text.StartsWith("Commit", StringComparison.Ordinal)), 5.0f);

        // The Commit row's ... button, the menu of what else a commit can be
        Func<UITreeItem?> commitRow = () => sideRows(sideTree.Items).FirstOrDefault(row => row.Text.StartsWith("Commit", StringComparison.Ordinal));
        test.Click(TreeAction(commitRow, Said("git.commit_more")));
        test.Wait();
        test.Check("commit: the Commit row's ... has Commit and Push, Commit and Sync, Amend and Undo", () =>
            contextMenu?.Showing is { } open && new[] { "git.commit_push", "git.commit_sync", "git.amend_command", "git.undo_command" }.All(key => open.Items.Any(item => item.Label == Said(key))));
        test.Run(() => contextMenu?.Close());
        test.Wait();

        // Amend, the last message in the box, and with nothing staged only the message changes
        test.Run(() => GitMenuItem(Said("git.amend_command"))?.OnPicked?.Invoke());
        test.WaitFor(() => CommitBox() is { } box && box.Text.StartsWith("Add the new one", StringComparison.Ordinal), 4.0f);
        test.Check("amend: Amend Last Commit fills the box with the last commit's message", () =>
            CommitBox() is { } box && box.Text.StartsWith("Add the new one\n\n# Why", StringComparison.Ordinal));
        test.Run(() =>
        {
            CodeEditor box = CommitBox()!;
            box.Text = "Add the new one, amended";
            box.OnControlEnter!();
        });
        test.WaitFor(() => LastSubject(repo) == "Add the new one, amended", 10.0f);
        test.Check("amend: and amending rewrites the commit HEAD is on, leaving what isn't staged alone", () =>
            LastSubject(repo) == "Add the new one, amended" && GitProcess.Run(repo, ["rev-list", "--count", "HEAD"]).Output.Trim() == "3"
            && !GitProcess.Run(repo, ["show", "--name-only", "--format=", "HEAD"]).Output.Contains("made_outside", StringComparison.Ordinal));
        test.Run(() => chrome.Dialog?.Close());

        // Undo takes it back, what it changed staged and its message waiting for the next commit
        test.Run(() => GitMenuItem(Said("git.undo_command"))?.OnPicked?.Invoke());
        test.WaitFor(() => LastSubject(repo) == "Second go" && under("Staged Changes", added) is not null, 6.0f);
        test.Check("undo: Undo Last Commit takes it back with its changes left staged, and says what it took back", () =>
            LastSubject(repo) == "Second go" && GitProcess.Run(repo, ["diff", "--cached", "--name-only"]).Output.Contains("src/added.txt", StringComparison.Ordinal)
            && under("Staged Changes", added) is not null && said.Any(line => line.Text.Contains("Add the new one, amended", StringComparison.Ordinal)));
        test.WaitFor(() => commitRow()?.Text.StartsWith("Commit 1 staged", StringComparison.Ordinal) == true, 4.0f);
        test.Click(TreeRow(commitRow));
        test.WaitFor(() => CommitBox() is not null, 3.0f);
        test.Check("undo: and the next commit's box starts with the message taken back", () => CommitBox()?.Text == "Add the new one, amended");
        test.Run(() => CommitBox()?.OnControlEnter!());
        test.WaitFor(() => LastSubject(repo) == "Add the new one, amended" && under("Staged Changes", added) is null, 10.0f);
        test.Run(() => chrome.Dialog?.Close());
    }

    private void TestMergeMessage(HexPointerScript test, Func<IEnumerable<UITreeItem>, IEnumerable<UITreeItem>> sideRows)
    {
        string merge = Path.Combine(testFolder, "git_merge");
        string file = Path.Combine(merge, "a.txt");
        UITreeItem? Heading() => sideTree.Items.FirstOrDefault(row => row.Text.StartsWith("git_merge", StringComparison.Ordinal));
        UITreeItem? Merging() => Heading()?.Children?.FirstOrDefault(row => row.Text == Said("git.merging"));
        UITreeItem? CommitRow() => Heading()?.Children?.FirstOrDefault(row => row.Text.StartsWith("Commit", StringComparison.Ordinal));

        test.Run(() =>
        {
            chrome.Dialog?.Close();
            DeleteGitFolder(merge);
            Directory.CreateDirectory(merge);
            File.WriteAllText(file, "base\n");
            Git(merge, "init", "-q", "-b", "main");
            Git(merge, "config", "user.name", "Hex self test");
            Git(merge, "config", "user.email", "selftest@example.com");
            Git(merge, "add", ".");
            Git(merge, "commit", "-q", "-m", "Base");
            Git(merge, "switch", "-q", "-c", "other");
            File.WriteAllText(file, "theirs\n");
            Git(merge, "commit", "-q", "-am", "Theirs");
            Git(merge, "switch", "-q", "main");
            File.WriteAllText(file, "ours\n");
            Git(merge, "commit", "-q", "-am", "Ours");

            // Stops at the conflict, which git says with an exit of 1
            GitProcess.Run(merge, ["merge", "other"]);
            explorer.AddFolder(merge);
            ShowSide(HexSide.SourceControl);
        });
        test.WaitFor(() => Merging() is not null, 6.0f);
        test.Check("merge: a merge going on says so just above Commit, in the warning colour, naming Abort Merge", () =>
            Merging() is { } row && row.Color == WarningColor && row.Tooltip.Contains("Abort Merge", StringComparison.Ordinal)
            && Heading()!.Children!.IndexOf(row) == Heading()!.Children!.IndexOf(CommitRow()!) - 1);

        test.Run(() =>
        {
            File.WriteAllText(file, "settled\n");
            Git(merge, "add", "a.txt");
            git.Refresh(merge);
        });
        test.WaitFor(() => CommitRow()?.Text.StartsWith("Commit 1 staged", StringComparison.Ordinal) == true, 5.0f);
        test.Click(TreeRow(CommitRow));
        test.WaitFor(() => CommitBox() is not null, 3.0f);
        test.Check("merge: the commit box starts with git's message for the merge, its # lines left out", () =>
            CommitBox() is { } box && box.Text.StartsWith("Merge branch 'other'", StringComparison.Ordinal) && !box.Text.Contains('#'));
        test.Run(() => CommitBox()?.OnControlEnter!());
        test.WaitFor(() => GitProcess.Run(merge, ["rev-list", "--parents", "-n", "1", "HEAD"]).Output.Trim().Split(' ').Length == 3 && Merging() is null, 10.0f);
        test.Check("merge: and committing finishes the merge, a commit of two parents, and the Merging row goes", () =>
            GitProcess.Run(merge, ["rev-list", "--parents", "-n", "1", "HEAD"]).Output.Trim().Split(' ').Length == 3 && Merging() is null
            && sideRows(sideTree.Items).Any(row => row.Text.StartsWith("git_merge", StringComparison.Ordinal)));

        test.Run(() =>
        {
            chrome.Dialog?.Close();
            explorer.RemoveFolder(merge);
            DeleteGitFolder(merge);
        });
    }
}
