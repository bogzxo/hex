using Hex.Core.Git;

namespace Hex.Dawn;

// The self test, git. A repository made for it with a commit in it, opened as the workspace, then changed the way
// somebody working in it would, and what Hex shows of that checked. Skipped where there's no git to make one with.
internal sealed partial class DawnScene
{
    // The repository the git suite works in, made again every run
    private string GitTestFolder => Path.Combine(testFolder, "git_repo");

    private void TestGit(HexPointerScript test)
    {
        if (!GitProcess.Installed)
        {
            test.Check("git: there's no git on this machine, so nothing of it is tested", () => true);
            return;
        }

        string repo = GitTestFolder;
        string changed = Path.Combine(repo, "changed.txt");
        string added = Path.Combine(repo, "src", "added.txt");
        string same = Path.Combine(repo, "same.txt");

        test.Run(() =>
        {
            DeleteGitFolder(repo);
            Directory.CreateDirectory(Path.Combine(repo, "src"));
            File.WriteAllText(changed, "one\ntwo\nthree\n");
            File.WriteAllText(same, "never touched\n");
            File.WriteAllText(Path.Combine(repo, ".gitignore"), "built/\n");
            Git(repo, "init", "-q", "-b", "main");
            Git(repo, "config", "user.name", "Hex self test");
            Git(repo, "config", "user.email", "selftest@example.com");
            Git(repo, "add", ".");
            Git(repo, "commit", "-q", "-m", "First go");

            // Then worked on, a line changed, a file nobody added yet and a build that's ignored
            File.WriteAllText(changed, "one\n2\nthree\n");
            File.WriteAllText(added, "new\n");
            Directory.CreateDirectory(Path.Combine(repo, "built"));
            File.WriteAllText(Path.Combine(repo, "built", "out.dll"), "built");

            explorer.workspace.Expanded.Add(Path.Combine(repo, "src"));
            explorer.AddFolder(repo);
        });

        UITreeItem? Row(string path) => explorer.explorerRows.FirstOrDefault(row => PlatformPaths.Same(row.Path, path)).Row;

        test.Run(explorer.ShowExplorer);
        test.WaitFor(() => Row(changed)?.Badge == "M", 5.0f);
        test.Check("git: a file changed since the commit is the warning colour with an M at its end in the same colour", () =>
            Row(changed) is { Badge: "M" } row && row.Color == compositor.Skin!.Palette.Warning && row.BadgeColor == row.Color && row.Detail == "");
        test.Check("git: one nobody added is new, success with a U, and so is its folder", () =>
            Row(added) is { Badge: "U" } row && row.Color == compositor.Skin!.Palette.Success
            && Row(Path.Combine(repo, "src")) is { } folder && folder.Color == compositor.Skin!.Palette.Success);
        test.Check("git: one as committed is as it always was", () => Row(same) is { Badge: "" } row && row.Color is null);
        test.Check("git: an ignored folder is dim", () => Row(Path.Combine(repo, "built")) is { } row && row.Color == DimColor);

        // Refresh, the button at the end of the side bar's title, reads a file made outside of Hex straight away
        string outside = Path.Combine(repo, "made_outside.txt");
        test.Run(() => File.WriteAllText(outside, "hello\n"));
        test.Click(At(() => refreshButton));
        test.WaitFor(() => Row(outside) is { Badge: "U" }, 3.0f);
        test.Check("refresh: the button is there on the explorer and a file made outside is read with its colour", () =>
            refreshButton.Visible && Row(outside) is { Badge: "U" });

        test.Run(() => Open(changed));
        test.Wait();
        test.Check("git: the tab of a changed file is the colour of its change", () =>
            docTabs.Tabs.FirstOrDefault(tab => tab.Tag == document) is { TitleColor: { } colour } && colour == compositor.Skin!.Palette.Warning);

        // Blame, the caret's line says who changed it last, a line changed since the commit that nobody's committed it
        test.Run(() => code.GoTo(0, 0));
        test.WaitFor(() => code.LineNote?.Text.Contains("Hex self test", StringComparison.Ordinal) == true, 5.0f);
        test.Check("blame: a committed line says who, when and the commit's message", () =>
            code.LineNote is { Line: 0 } note && note.Text.Contains("Hex self test", StringComparison.Ordinal) && note.Text.Contains("First go", StringComparison.Ordinal));
        test.Run(() => code.GoTo(1, 0));
        test.Wait();
        test.Check("blame: a line changed since says it's not committed yet", () =>
            code.LineNote is { Line: 1 } note && note.Text.Contains("not committed", StringComparison.Ordinal));
        test.Run(() => settings.GitBlame = false);
        test.Wait();
        test.Check("blame: and the setting turns it off", () => code.LineNote is null);
        test.Run(() => settings.GitBlame = true);

        // The gutter, line 2 was changed on the disk, then a line typed at the end and the first one taken out
        bool Marks(params UICodeLineChange[] wanted) => code.LineChanges.SequenceEqual(wanted);
        test.WaitFor(() => Marks(new UICodeLineChange(1, 1, UILineChangeKind.Modified)), 5.0f);
        test.Check("gutter: a line changed since the commit has a bar beside it", () => Marks(new UICodeLineChange(1, 1, UILineChangeKind.Modified)));

        test.Run(() => code.Document.Replace("one\n2\nthree\nfour\n"));
        test.WaitFor(() => code.LineChanges.Count == 2, 3.0f);
        test.Check("gutter: a line typed in is added, worked out as it's typed without a save", () =>
            Marks(new UICodeLineChange(1, 1, UILineChangeKind.Modified), new UICodeLineChange(3, 1, UILineChangeKind.Added)));

        // The first line taken out and the rest as committed, a line taken out next to a changed one is one change of both
        test.Run(() => code.Document.Replace("two\nthree\n"));
        test.WaitFor(() => code.LineChanges.Any(change => change.Kind == UILineChangeKind.Deleted), 3.0f);
        test.Check("gutter: a line taken out is a wedge where it was", () => Marks(new UICodeLineChange(0, 0, UILineChangeKind.Deleted)));

        // A long changed line wrapped onto a few rows, and a fold over changed lines, for a look with HEX_SELFTEST_SHOTS set
        bool wrapped = false;
        test.Run(() =>
        {
            wrapped = settings.WrapCode;
            if (!wrapped) ToggleWrap();
            code.Document.Replace("one\n" + string.Concat(Enumerable.Repeat("a long changed line that wraps ", 12)) + "\nthree\nfour\n");
        });
        test.WaitFor(() => code.LineChanges.Count == 2, 3.0f);
        test.Settle(0.3f);
        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, "gutter_wrapped.png"));
        });
        test.Settle(0.3f);
        test.Check("gutter: a wrapped changed line is one change however many rows it takes", () =>
            Marks(new UICodeLineChange(1, 1, UILineChangeKind.Modified), new UICodeLineChange(3, 1, UILineChangeKind.Added)));
        test.Run(() => { if (!wrapped) ToggleWrap(); });

        // Back to what's on the disk, so the commit below takes every mark off
        test.Run(() =>
        {
            code.Document.Replace("one\n2\nthree\n");
            Save();
        });
        test.Wait();

        // Committed in the terminal, which Hex sees by the index and HEAD moving, not by any file of the workspace
        test.Run(() =>
        {
            Git(repo, "add", "changed.txt");
            Git(repo, "commit", "-q", "-m", "Second go");
        });
        test.WaitFor(() => Row(changed)?.Badge == "", 5.0f);
        test.Check("git: a commit made outside of Hex takes the colour off again", () =>
            Row(changed) is { Badge: "" } row && row.Color is null && docTabs.Tabs.First(tab => tab.Tag == document).TitleColor is null);
        test.WaitFor(() => code.LineChanges.Count == 0, 3.0f);
        test.Check("gutter: and the bars go with it", () => code.LineChanges.Count == 0);

        // Source Control, the side of what's changed, a file staged from its right click and committed with a message
        IEnumerable<UITreeItem> SideRows(IEnumerable<UITreeItem> rows) => rows.SelectMany(row => new[] { row }.Concat(SideRows(row.Children ?? [])));
        UITreeItem? Under(string section, string path) => SideRows(sideTree.Items).FirstOrDefault(row =>
            row.Tooltip is { } tip && PlatformPaths.Same(tip, path) && row.Parent?.Text.StartsWith(section, StringComparison.Ordinal) == true);

        test.Run(() => ShowSide(HexSide.SourceControl));
        test.WaitFor(() => Under("Changes", added) is not null, 4.0f);
        test.Check("source control: the side lists what's changed, a new file with its folder dim and its U at the end in the success colour", () =>
            Side == HexSide.SourceControl && Under("Changes", added) is { Detail: "src", Badge: "U" } row && row.BadgeColor == compositor.Skin!.Palette.Success
            && row.Color == row.BadgeColor && row.Parent is { Text: "Changes", Badge: "2" });
        test.RightClick(TreeRow(() => Under("Changes", added)));
        test.Wait();
        PickFromMenu("Stage");
        test.WaitFor(() => Under("Staged Changes", added) is not null, 4.0f);
        test.Check("source control: Stage from its right click moves it to the staged changes", () =>
            Under("Staged Changes", added) is not null && Under("Changes", added) is null);

        // The side is put together again as git answers, so the Commit row is waited for before it's clicked
        test.WaitFor(() => SideRows(sideTree.Items).Any(row => row.Text.StartsWith("Commit 1 staged", StringComparison.Ordinal)), 4.0f);
        test.Wait();
        test.Click(TreeRow(() => SideRows(sideTree.Items).FirstOrDefault(row => row.Text.StartsWith("Commit", StringComparison.Ordinal))));
        test.WaitFor(() => textDialog is { IsOpen: true }, 3.0f);
        test.Check("source control: the commit's box takes lines and colours Markdown, with the summary's length under it", () =>
            Descendants(textDialog!.Body).OfType<CodeEditor>().FirstOrDefault() is { WordWrap: true } box && box.Highlighter is not null
            && Descendants(textDialog!.Body).OfType<Label>().Any(label => label.Text.Contains("Ctrl+Enter", StringComparison.Ordinal)));
        test.Run(() =>
        {
            CodeEditor box = Descendants(textDialog!.Body).OfType<CodeEditor>().First();
            box.Text = "Add the new one\n\n# Why\nSo there's **something** in src";
            box.OnControlEnter!();
        });
        test.WaitFor(() => Under("Staged Changes", added) is null && GitProcess.Run(repo, ["log", "-1", "--format=%s"]).Output.Trim() == "Add the new one", 10.0f);
        test.Check("source control: Commit with a message commits what was staged, and it's gone from the side", () =>
            GitProcess.Run(repo, ["log", "-1", "--format=%s"]).Output.Trim() == "Add the new one" && Under("Staged Changes", added) is null
            && GitProcess.Run(repo, ["log", "-1", "--format=%B"]).Output.Contains("# Why\nSo there's **something** in src", StringComparison.Ordinal));
        test.Run(() => chrome.Dialog?.Close());

        // The buttons on a row, the keys of the tree, amending and undoing, and a deleted file's diff
        TestSourceControlRows(test, repo, outside, added, same, Under, SideRows);

        // Editor · Git's settings and what they change, with nothing staged now and one file nobody added
        TestGitSettings(test, repo);

        // A diff of what's changed, and one change staged and another reverted from the gutter's menu
        HexDocument? edited = null;
        test.Run(() =>
        {
            Open(changed);
            edited = document;
            code.Document.Replace("zero\n2\nthree\nfour\n");
        });
        test.WaitFor(() => code.LineChanges.Count == 2, 4.0f);
        test.Run(OpenChangesHere);
        test.WaitFor(() => document.DiffOf is not null, 4.0f);
        test.Settle(0.3f);
        test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, "diff.png"));
        });
        test.Settle(0.3f);
        test.Check("diff: Open Changes shows the file with what went above what came, red and green", () =>
            document.DiffOf is { } of && PlatformPaths.Same(of, changed) && code.ReadOnly && code.Text.StartsWith("one\nzero\n2\nthree\nfour", StringComparison.Ordinal)
            && code.LineHighlights.Count == 3 && document.DiffChanges.SequenceEqual([0, 4]));
        test.Run(() =>
        {
            code.GoTo(0, 0);
            GoToChange(1);
        });
        test.Check("diff: Alt+F5 goes to the next change", () => code.Document.Caret.Line == 4);
        test.Run(() =>
        {
            if (document.DiffOf is not null)
                CloseNow();
            Show(edited!);
        });
        test.Wait();

        test.Run(() => code.OnChangePressed!(new UICodeLineChange(3, 1, UILineChangeKind.Added)));
        test.Wait();
        PickFromMenu("Stage Change");
        test.WaitFor(() => GitProcess.Run(repo, ["show", ":changed.txt"]).Output == "one\n2\nthree\nfour\n", 5.0f);
        test.Check("hunks: Stage Change from the gutter stages that change and nothing else", () =>
            GitProcess.Run(repo, ["show", ":changed.txt"]).Output == "one\n2\nthree\nfour\n" && File.ReadAllText(changed) == "one\n2\nthree\n");

        test.Run(() => code.OnChangePressed!(new UICodeLineChange(0, 1, UILineChangeKind.Modified)));
        test.Wait();
        PickFromMenu("Revert Change");
        test.Wait();
        test.Check("hunks: Revert Change puts the line back to git's, one step to undo", () => code.Text.StartsWith("one\n2\n", StringComparison.Ordinal));
        test.Run(() => code.Document.Undo());
        test.Wait();
        test.Check("hunks: and Ctrl+Z has it again", () => code.Text.StartsWith("zero\n", StringComparison.Ordinal));
        test.Run(() =>
        {
            Git(repo, "reset", "-q");
            code.Document.Replace(File.ReadAllText(changed));
        });
        test.Wait();

        // Local history, a copy every save, one compared, and git's history of the file, a commit of it opened as a diff
        test.Run(() =>
        {
            Show(edited!);
            code.Document.Replace("kept once\n");
            Save();
            code.Document.Replace("kept twice\n");
            Save();
        });
        test.WaitFor(() => HexLocalHistory.Versions(LocalHistoryRoot, changed).Count >= 2, 4.0f);
        test.Run(ShowLocalHistory);
        test.Wait();
        test.Check("history: every save is kept, the newest first", () =>
            textDialog is { IsOpen: true } && Chooser().Items.Length >= 2 && HexLocalHistory.Versions(LocalHistoryRoot, changed) is { Count: >= 2 } kept
            && File.ReadAllText(kept[0].Copy) == "kept twice\n");
        test.Run(() =>
        {
            // The one before the newest by when it was kept, the list can be in another order than the copies
            ListBox list = Chooser();
            string older = HexLocalHistory.Versions(LocalHistoryRoot, changed)[1].When.ToString("yyyy-MM-dd HH:mm:ss.fff");
            int at = Array.FindIndex(list.Items, item => item.Contains(older, StringComparison.Ordinal));
            list.Choose(at);
            list.OnActivated!(list.Items[at]);
        });
        test.Wait();
        test.Run(() => textDialog?.Buttons.First(button => button.Label == "Compare").OnPressed?.Invoke());
        test.Wait();
        test.WaitFor(() => document.DiffKind == "history", 3.0f);
        test.Check("history: an older copy compared with the file is a diff of the two", () =>
            document.DiffKind == "history" && code.Text.Contains("kept once", StringComparison.Ordinal) && code.Text.Contains("kept twice", StringComparison.Ordinal));
        test.Run(() =>
        {
            if (document.DiffOf is not null) CloseNow();
            Show(edited!);
            ShowFileHistory();
        });
        test.WaitFor(() => textDialog is { IsOpen: true } && Chooser().Items.Length >= 2, 4.0f);
        test.Check("history: git's history of the file lists the commits that changed it", () =>
            Chooser().Items.Any(item => item.Contains("Second go", StringComparison.Ordinal)) && Chooser().Items.Any(item => item.Contains("First go", StringComparison.Ordinal)));
        test.Run(() =>
        {
            ListBox list = Chooser();
            int second = Array.FindIndex(list.Items, item => item.Contains("Second go", StringComparison.Ordinal));
            list.Choose(second);
            list.OnActivated!(list.Items[second]);
        });
        test.WaitFor(() => document.DiffKind.StartsWith("commit ", StringComparison.Ordinal), 4.0f);
        test.Check("history: and a commit picked shows what it did to the file", () =>
            document.DiffKind.StartsWith("commit ", StringComparison.Ordinal) && code.Text.Contains("two", StringComparison.Ordinal) && code.Text.Contains("2", StringComparison.Ordinal));
        test.Run(() =>
        {
            if (document.DiffOf is not null) CloseNow();
            Show(edited!);
            Git(repo, "checkout", "--", "changed.txt");
        });

        // Read again from the disk by itself, nothing of Hex's in it, a save now would ask about writing over the checkout
        test.WaitFor(() => code.Text == File.ReadAllText(changed), 4.0f);

        // The branch at the left of the status bar, a switch made in the terminal said there, and one picked from its list
        test.WaitFor(() => branchItem.Text.Contains("main", StringComparison.Ordinal), 3.0f);
        test.Check("branch: the status bar says the branch checked out", () => branchItem.Text.Contains("$(git-branch) main", StringComparison.Ordinal));
        test.Run(() => Git(repo, "switch", "-q", "-c", "feature"));
        test.WaitFor(() => branchItem.Text.Contains("feature", StringComparison.Ordinal), 4.0f);
        test.Check("branch: a branch made and checked out in the terminal is said there", () => branchItem.Text.Contains("$(git-branch) feature", StringComparison.Ordinal));

        ListBox Chooser() => Descendants(textDialog!.Body).OfType<ListBox>().First();
        test.Click(() => chrome.ToWorld(statusBar.ItemBounds(branchItem).Center));
        test.WaitFor(() => textDialog is { IsOpen: true }, 3.0f);
        test.Check("branch: a click on it lists the branches, a new one to make first", () =>
            textDialog is { IsOpen: true } && Chooser().Items is { Length: >= 3 } items && items[0].Contains("Create a new branch", StringComparison.Ordinal)
            && items.Any(item => item.Contains(": main", StringComparison.Ordinal)));
        test.Run(() =>
        {
            ListBox list = Chooser();
            int main = Array.FindIndex(list.Items, item => item.Contains(": main", StringComparison.Ordinal));
            list.Choose(main);
            list.OnActivated!(list.Items[main]);
        });
        test.WaitFor(() => branchItem.Text.Contains("$(git-branch) main", StringComparison.Ordinal), 4.0f);
        test.Check("branch: and picking one switches to it", () =>
            branchItem.Text.Contains("$(git-branch) main", StringComparison.Ordinal) && GitProcess.Run(repo, ["branch", "--show-current"]).Output.Trim() == "main");

        // A conflict a merge left, lit a side each, settled with the current side, Ctrl+Z, and settled with both
        string conflicted = Path.Combine(repo, "conflict.txt");
        test.Run(() =>
        {
            chrome.Dialog?.Close();
            File.WriteAllText(conflicted, "top\n<<<<<<< HEAD\nours\n=======\ntheirs\n>>>>>>> other\nbottom\n");
            Open(conflicted);
        });
        test.WaitFor(() => conflicts.Count == 1, 3.0f);
        test.Check("conflicts: a conflict is found and each side lit", () => conflicts.Count == 1 && code.LineHighlights.Count >= 4);
        test.Run(() =>
        {
            code.GoTo(2, 0);
            SettleConflict(GitConflictPick.Current);
        });
        test.Wait();
        test.Check("conflicts: Alt+1 keeps the current side, the markers gone", () => code.Text.StartsWith("top\nours\nbottom", StringComparison.Ordinal) && conflicts.Count == 0);
        test.Run(() => code.Document.Undo());
        test.Wait();
        test.Run(() =>
        {
            code.GoTo(0, 0);
            SettleConflict(GitConflictPick.Both);
        });
        test.Wait();
        test.Check("conflicts: Ctrl+Z has it back, and Alt+3 from above it keeps both, current first", () => code.Text.StartsWith("top\nours\ntheirs\nbottom", StringComparison.Ordinal));
        test.Run(() =>
        {
            if (document.Path is { } open && PlatformPaths.Same(open, conflicted))
                CloseNow();
            File.Delete(conflicted);
        });
        test.Wait();

        // A remote, a bare repository beside it, the branch published from the status bar and a commit made elsewhere pulled in
        string origin = Path.Combine(testFolder, "git_origin.git"), other = Path.Combine(testFolder, "git_other");
        test.Run(() =>
        {
            chrome.Dialog?.Close();
            DeleteGitFolder(origin);
            DeleteGitFolder(other);
            Git(testFolder, "init", "-q", "--bare", "-b", "main", origin);
            Git(repo, "remote", "add", "origin", origin);
            git.Refresh(repo);
        });
        test.WaitFor(() => syncItem.Text.Contains("Publish", StringComparison.Ordinal), 5.0f);
        test.Check("sync: a branch that isn't on the remote offers to publish it", () => syncItem.Text.Contains("Publish", StringComparison.Ordinal));
        test.Click(() => chrome.ToWorld(statusBar.ItemBounds(syncItem).Center));
        test.WaitFor(() => GitProcess.Run(origin, ["log", "-1", "--format=%s"]).Output.Trim().Length > 0 && syncItem.Text == "$(sync)", 10.0f);
        test.Check("sync: and publishing pushes it and has it follow the remote's", () =>
            GitProcess.Run(repo, ["rev-parse", "--abbrev-ref", "main@{upstream}"]).Output.Trim() == "origin/main" && syncItem.Text == "$(sync)");

        test.Run(() =>
        {
            Git(testFolder, "clone", "-q", origin, other);
            Git(other, "config", "user.name", "Somebody else");
            Git(other, "config", "user.email", "else@example.com");
            File.WriteAllText(Path.Combine(other, "theirs.txt"), "from elsewhere\n");
            Git(other, "add", ".");
            Git(other, "commit", "-q", "-m", "Their commit");
            Git(other, "push", "-q");
            Git(repo, "fetch", "-q");
            git.Refresh(repo);
            settings.GitPullMode = "rebase";
        });

        // Sync asks first, how many come and go and with where, the way VS Code's git.confirmSync does
        string syncButton = Said("git.sync");
        test.WaitFor(() => syncItem.Text.Contains("1$(arrow-down)", StringComparison.Ordinal), 5.0f);
        test.Click(() => chrome.ToWorld(statusBar.ItemBounds(syncItem).Center));
        test.WaitFor(() => textDialog is { IsOpen: true } && textDialog.Buttons.Any(button => button.Label == syncButton), 3.0f);
        test.Check("sync: Sync asks first, pulling 1 and pushing 0 with origin/main", () =>
            textDialog is { IsOpen: true } dialog && dialog.Buttons.Any(button => button.Label == syncButton) && !File.Exists(Path.Combine(repo, "theirs.txt"))
            && Descendants(dialog).OfType<Label>().Any(label => label.Text.Contains("Pull 1 and push 0 commits with origin/main", StringComparison.Ordinal)));
        test.Run(() => textDialog?.Buttons.First(button => button.Label == syncButton).OnPressed?.Invoke());
        test.WaitFor(() => said.Any(line => line.Text.Contains("git push done", StringComparison.Ordinal)), 10.0f);
        test.Check("sync: Sync pulls in what somebody else pushed", () =>
            File.Exists(Path.Combine(repo, "theirs.txt")) && GitProcess.Run(repo, ["log", "-1", "--format=%s"]).Output.Trim() == "Their commit");
        test.Check("settings: pull mode rebase reaches git pull", () => said.Any(line => line.Text == "git pull --rebase, git push done"));
        test.Run(() => settings.GitPullMode = "default");

        // A folder of repositories as the workspace, each of them on the Source Control side under its name
        string nest = Path.Combine(testFolder, "git_nest");
        test.Run(() =>
        {
            chrome.Dialog?.Close();
            DeleteGitFolder(nest);
            foreach (string name in new[] { "first", "second" })
            {
                string inner = Path.Combine(nest, name);
                Directory.CreateDirectory(inner);
                File.WriteAllText(Path.Combine(inner, "readme.md"), "hi\n");
                Git(inner, "init", "-q", "-b", "main");
            }
            explorer.AddFolder(nest);
            ShowSide(HexSide.SourceControl);
        });
        test.WaitFor(() => SideRows(sideTree.Items).Count(row => row.Text.StartsWith("first", StringComparison.Ordinal) || row.Text.StartsWith("second", StringComparison.Ordinal)) == 2, 5.0f);
        test.Check("source control: a folder of repositories has every one of them, each under its name", () =>
            SideRows(sideTree.Items).Count(row => row.Text.StartsWith("first", StringComparison.Ordinal) || row.Text.StartsWith("second", StringComparison.Ordinal)) == 2);
        test.Run(() =>
        {
            explorer.RemoveFolder(nest);
            DeleteGitFolder(nest);
        });

        // A merge with a conflict in a repository of its own, the box for the commit that finishes it filled in from git's
        TestMergeMessage(test, SideRows);

        test.Run(() =>
        {
            chrome.Dialog?.Close();
            if (documents.Contains(document) && document.Path is { } open && PlatformPaths.IsUnder(open, repo))
                CloseNow();
            explorer.RemoveFolder(repo);
        });
        test.Wait();
        test.Run(() => DeleteGitFolder(repo));
    }

    private static void Git(string folder, params string[] arguments)
    {
        GitResult result = GitProcess.Run(folder, arguments);
        if (!result.Ok)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} said {result.Why}");
    }

    // git makes its objects read only, which Directory.Delete won't touch. A git still asking about it in the background
    // can have it open for a moment, and then it's left for the next run to clear
    private static void DeleteGitFolder(string folder)
    {
        if (!Directory.Exists(folder))
            return;

        try
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"[Hex self-test] {folder} was in use, it's left for the next run");
        }
    }
}
