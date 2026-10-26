using Hex.Core.Git;

namespace Hex.Dawn;

// The self test, a folder of repositories as the workspace, the way a folder of projects side by side is. Each is under
// a heading of its own with its branch, a click on one makes it the active one that the status bar and the Git menu
// follow, a submodule is a row of its own kind that goes to its own heading, a repository made later turns up without a
// refresh, and a fetch in the terminal is seen.
internal sealed partial class DawnScene
{
    private void TestGitRepositories(HexPointerScript test, Func<IEnumerable<UITreeItem>, IEnumerable<UITreeItem>> sideRows)
    {
        string nest = Path.Combine(testFolder, "git_nest");
        string first = Path.Combine(nest, "first"), second = Path.Combine(nest, "second"), clone = Path.Combine(nest, "clone");
        string module = Path.Combine(first, "mod"), inner = Path.Combine(first, "inner"), third = Path.Combine(nest, "third");

        test.Run(() =>
        {
            chrome.Dialog?.Close();
            DeleteGitFolder(nest);
            foreach (string folder in new[] { first, second })
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, "readme.md"), "hi\n");
                Git(folder, "init", "-q", "-b", "main");
                Git(folder, "config", "user.name", "Hex self test");
                Git(folder, "config", "user.email", "selftest@example.com");
                Git(folder, "add", ".");
                Git(folder, "commit", "-q", "-m", "First go");
            }

            // second as a submodule of first, then a commit in it, so first has a submodule with new commits
            Git(first, "-c", "protocol.file.allow=always", "submodule", "add", "-q", second, "mod");
            Git(first, "commit", "-q", "-m", "The submodule");
            Git(module, "config", "user.name", "Hex self test");
            Git(module, "config", "user.email", "selftest@example.com");
            File.WriteAllText(Path.Combine(module, "more.md"), "more\n");
            Git(module, "add", ".");
            Git(module, "commit", "-q", "-m", "More in the submodule");

            // A repository inside first that nobody added, and a clone of second beside them to fetch into
            Directory.CreateDirectory(inner);
            Git(inner, "init", "-q", "-b", "main");
            Git(nest, "clone", "-q", second, clone);

            explorer.AddFolder(nest);
            ShowSide(HexSide.SourceControl);
        });

        UITreeItem? Heading(string name) => sideTree.Items.FirstOrDefault(row => row.Text == name && row.Icon == "repo" && row.HasChildren);
        UITreeItem? Under(string heading, string name) => sideRows(Heading(heading)?.Children ?? []).FirstOrDefault(row => row.Text == name);

        test.WaitFor(() => new[] { "first", "second", "clone", "mod", "inner" }.All(name => Heading(name) is not null) && Heading("first")?.Detail.StartsWith("main", StringComparison.Ordinal) == true, 10.0f);
        test.Check("repositories: a folder of repositories has every one of them, a submodule and one nobody added too, each under its name", () =>
            new[] { "first", "second", "clone", "mod", "inner" }.All(name => Heading(name) is not null));
        test.Check("repositories: a heading says its branch after its name and how many changes at its end", () =>
            Heading("first") is { Detail: var detail, Badge: var badge } && detail.StartsWith("main*", StringComparison.Ordinal) && badge == "1");

        test.WaitFor(() => Under("first", "mod") is not null, 5.0f);
        test.Check("submodules: a submodule is a row of its own kind, saying it has new commits", () =>
            Under("first", "mod") is { Icon: "file-submodule" } row && row.Detail.Contains("new commits", StringComparison.Ordinal));
        test.Check("submodules: a repository nobody added that has a heading isn't a row too, and no row is without a name", () =>
            Under("first", "inner") is null && sideRows(sideTree.Items).All(row => row.Text.Length > 0));

        // A click on a heading makes it the active one, the status bar says it by name and the Git menu works in it
        test.Click(TreeRow(() => Heading("second")));
        test.WaitFor(() => branchItem.Text.Contains("$(repo) second", StringComparison.Ordinal), 4.0f);
        test.Check("active repository: a click on a heading makes it active, the status bar names it with its branch", () =>
            branchItem.Text.Contains("$(repo) second", StringComparison.Ordinal) && branchItem.Text.Contains("$(git-branch) main", StringComparison.Ordinal)
            && Heading("second")?.Edge is not null && Heading("first")?.Edge is null);

        test.Run(() =>
        {
            File.WriteAllText(Path.Combine(second, "readme.md"), "changed\n");
            File.WriteAllText(Path.Combine(nest, "clone", "readme.md"), "changed too\n");
            git.Refresh(second);
            git.Refresh(clone);
        });
        test.WaitFor(() => Heading("second")?.Badge == "1" && Heading("clone")?.Badge == "1", 5.0f);
        test.Run(() => GitCommand(Said("git.stage_all"))?.OnPicked?.Invoke());
        test.WaitFor(() => GitProcess.Run(second, ["diff", "--cached", "--name-only"]).Output.Trim() == "readme.md", 5.0f);
        test.Check("active repository: Stage All from the Git menu stages in the active one and nowhere else", () =>
            GitProcess.Run(second, ["diff", "--cached", "--name-only"]).Output.Trim() == "readme.md"
            && GitProcess.Run(clone, ["diff", "--cached", "--name-only"]).Output.Trim().Length == 0);

        // The status bar's branch picks the repository first, then the branch
        test.Click(() => chrome.ToWorld(statusBar.ItemBounds(branchItem).Center));
        test.WaitFor(() => textDialog is { IsOpen: true }, 3.0f);
        test.Check("active repository: a click on the status bar's branch lists the repositories first", () =>
            textDialog is { IsOpen: true } && Descendants(textDialog!.Body).OfType<ListBox>().First().Items is var items
            && items.Any(item => item.StartsWith("first: main", StringComparison.Ordinal)) && items.Any(item => item.StartsWith("clone: main", StringComparison.Ordinal)));
        test.Run(() =>
        {
            ListBox list = Descendants(textDialog!.Body).OfType<ListBox>().First();
            int at = Array.FindIndex(list.Items, item => item.StartsWith("clone: main", StringComparison.Ordinal));
            list.Choose(at);
            list.OnActivated!(list.Items[at]);
        });
        test.WaitFor(() => textDialog is { IsOpen: true } && Descendants(textDialog!.Body).OfType<ListBox>().First().Items.Any(item => item.Contains("Create a new branch", StringComparison.Ordinal)), 4.0f);
        test.Check("active repository: and picking one makes it active and lists its branches", () =>
            branchItem.Text.Contains("$(repo) clone", StringComparison.Ordinal) && textDialog is { IsOpen: true });
        test.Run(() => chrome.Dialog?.Close());

        // A click on the submodule's row goes to its own heading, the active one now
        test.Click(TreeRow(() => Under("first", "mod")));
        test.WaitFor(() => branchItem.Text.Contains("$(repo) mod", StringComparison.Ordinal), 4.0f);
        test.Check("submodules: a click on a submodule's row makes its own repository the active one", () =>
            branchItem.Text.Contains("$(repo) mod", StringComparison.Ordinal) && Heading("mod") is { Expanded: true, Edge: not null });

        // A commit pushed to second and fetched into the clone in the terminal, which only moves the remote's ref
        test.Run(() =>
        {
            Git(second, "commit", "-q", "-m", "Changed in second");
            Git(clone, "fetch", "-q");
        });
        test.WaitFor(() => Heading("clone")?.Detail.Contains("1↓", StringComparison.Ordinal) == true, 5.0f);
        test.Check("repositories: a fetch in the terminal is seen, the clone's heading says it's one behind", () =>
            Heading("clone")?.Detail.Contains("1↓ 0↑", StringComparison.Ordinal) == true);

        // A repository made after the workspace was opened turns up by itself, no refresh
        test.Run(() =>
        {
            Directory.CreateDirectory(third);
            Git(third, "init", "-q", "-b", "main");
        });
        test.WaitFor(() => Heading("third") is not null, 12.0f);
        test.Check("repositories: a git init in the workspace turns up on the side without a refresh", () => Heading("third") is not null);

        test.Run(() =>
        {
            chrome.Dialog?.Close();
            explorer.RemoveFolder(nest);
            DeleteGitFolder(nest);
        });
        test.Wait();
    }
}
