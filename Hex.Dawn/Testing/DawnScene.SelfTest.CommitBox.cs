using Hex.Core.Git;

namespace Hex.Dawn;

// The self test of the box over Source Control, the message typed straight in and committed with Ctrl+Enter or its
// button, no dialog, and the box emptied after. Part of the git suite, in a repository of its own.
internal sealed partial class DawnScene
{
    /// <summary>
    /// What Commit says on the side and whether it's dim. Under a repository's heading it's a row drawn as a button, with
    /// one repository it's the button under the box. Null when neither is there.
    /// </summary>
    private (string Text, bool Dim)? CommitSaid(IEnumerable<UITreeItem> rows)
    {
        foreach (UITreeItem row in rows)
        {
            if (row.Icon == "check" && row.Text.StartsWith("Commit", StringComparison.Ordinal))
                return (row.Text, row.Color == DimColor);
        }

        return commitBox is { Visible: true } ? (commitButton.Label, !commitButton.Primary) : null;
    }

    private void TestCommitBox(HexPointerScript test)
    {
        string repo = Path.Combine(testFolder, "git_box");
        string file = Path.Combine(repo, "a.txt");
        bool wasEmpty = false;

        test.Run(() =>
        {
            chrome.Dialog?.Close();
            DeleteGitFolder(repo);
            Directory.CreateDirectory(repo);
            File.WriteAllText(file, "base\n");
            Git(repo, "init", "-q", "-b", "main");
            Git(repo, "config", "user.name", "Hex self test");
            Git(repo, "config", "user.email", "selftest@example.com");
            Git(repo, "add", ".");
            Git(repo, "commit", "-q", "-m", "Base");
            File.WriteAllText(file, "changed\n");
            Open(file);
            git.Refresh(repo);
            ShowSide(HexSide.SourceControl);
        });
        test.WaitFor(() => commitBox is { Visible: true } && commitBoxFor is { } root && PlatformPaths.Same(root, repo) && commitButton.Primary, 6.0f);
        test.Check("commit box: Source Control has a box for the message over the changes, the active repository's, its Commit a button that's lit", () =>
            commitBox is { Visible: true } && commitBoxFor is { } root && PlatformPaths.Same(root, repo) && commitButton.Primary
            && commitButton.Label.StartsWith("Commit", StringComparison.Ordinal) && commitPlaceholder.Visible);

        // The side bar keeps its width with the box in it, and the box is over the tree
        test.Check("commit box: the side bar is the width it was dragged to with the box in it, the box over the tree", () =>
            MathF.Abs(left.Bounds.Width - (treeScroll.Size.X + left.Padding.Total.X) * left.SlideAmount) < 2.0f
            && commitBox!.Bounds.Min.Y > sideTree.Bounds.Max.Y - 1.0f);

        // Typed in, Ctrl+Enter commits with it, nothing asked, and the box is emptied for the next. Smart commit always, or
        // the commit of changes nothing staged asks first whichever way it was asked for
        string smart = settings.GitSmartCommit;
        test.Run(() =>
        {
            settings.GitSmartCommit = "always";
            commitMessage.Focus();
            commitMessage.Text = "Typed in the box";
            commitMessage.OnControlEnter!();
        });
        test.WaitFor(() => LastSubject(repo) == "Typed in the box" && commitMessage.Text.Length == 0, 6.0f);
        test.Run(() => wasEmpty = commitMessage.Text.Length == 0);
        test.Check("commit box: Ctrl+Enter in the box commits what's typed with no dialog, every change staged first, and empties the box", () =>
            LastSubject(repo) == "Typed in the box" && wasEmpty && textDialog is not { IsOpen: true }
            && GitProcess.Run(repo, ["status", "--porcelain"]).Output.Trim().Length == 0);

        // Nothing typed, Commit asks for the message the way the Git menu's always has
        test.Run(() =>
        {
            File.WriteAllText(file, "again\n");
            git.Refresh(repo);
        });
        test.WaitFor(() => commitButton.Primary, 5.0f);
        test.Run(() => commitButton.OnPressed?.Invoke());
        test.WaitFor(() => textDialog is { IsOpen: true } && CommitBox() is not null, 3.0f);
        test.Check("commit box: Commit with the box empty asks for the message in the commit window", () =>
            textDialog is { IsOpen: true } && CommitBox() is not null);
        test.Run(() =>
        {
            chrome.Dialog?.Close();
            settings.GitSmartCommit = smart;
            Git(repo, "checkout", "--", "a.txt");
            git.Refresh(repo);
        });
    }
}
