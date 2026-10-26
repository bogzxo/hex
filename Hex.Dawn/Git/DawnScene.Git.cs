using System.Numerics;

using Hex.Core.Git;

namespace Hex.Dawn;

// What git thinks of the files, shown where they are. The explorer's rows and the tabs take the colour of their change
// (VS Code's, the skin's warning for changed, success for new, error for deleted or in conflict, dim for ignored) and a
// file's row has the letter of it at its right end, in the same colour.
internal sealed partial class DawnScene
{
    /// <summary>Has the explorer and the tabs follow git, once the parts are made.</summary>
    private void BuildGit()
    {
        git.ColourOf = GitColor;
        git.Changed += () =>
        {
            DecorateRows();
            tabsDirty = true;
        };
    }

    private StatusItem branchItem = null!, syncItem = null!;
    private string branchShown = string.Empty;
    private (HexDocument? Document, int Version) branchFor = (null, -1);

    /// <summary>The branch at the left end of the status bar, a click on it the list of branches. Made first so it's first.</summary>
    private void BuildBranchItem()
    {
        branchItem = statusBar.Add(string.Empty, pressed: () => git.ShowBranches(document.Path), tooltip: Said("git.branch_tip"));
        branchItem.IsVisible = () => branchItem.Text.Length > 0;
        syncItem = statusBar.Add(string.Empty, pressed: () => git.SyncPressed(document.Path), tooltip: Said("git.sync_tip"));
        syncItem.IsVisible = () => syncItem.Text.Length > 0;
    }

    /// <summary>Every tick, the branch said again when the repository or what git says of it changed.</summary>
    private void UpdateBranchItem()
    {
        if (branchFor == (document, git.Version))
            return;

        branchFor = (document, git.Version);
        string text = git.BranchText(document.Path);
        if (text != branchShown)
            branchItem.Text = branchShown = text;
        string sync = git.SyncText(document.Path);
        if (sync != syncItem.Text)
            syncItem.Text = sync;
    }

    /// <summary>The colour of a change in the skin's palette, null for none (as committed).</summary>
    internal Vector4? GitColor(GitChange change) => compositor.Skin?.Palette is not { } palette ? null : change switch
    {
        GitChange.Modified => palette.Warning,
        GitChange.Added or GitChange.Untracked or GitChange.Renamed => palette.Success,
        GitChange.Deleted or GitChange.Conflicted => palette.Error,
        GitChange.Ignored => DimColor,
        _ => null
    };

    /// <summary>The explorer's rows coloured again for what git thinks of them, after they're made or git said something new.</summary>
    private void DecorateRows()
    {
        // The tree can be made before git is
        if (git is null)
            return;

        foreach (var (path, isFolder, row) in explorer.explorerRows)
            Decorate(row, path, isFolder, isFolder && explorer.workspace.Folders.Any(root => PlatformPaths.Same(root, path)) ? null : isFolder ? FolderColor : null);

        // The files of a folder with no workspace, but not the dim rows that only say something
        foreach (var (path, isFolder, row) in fileRows)
        {
            if (row.Color != DimColor)
                Decorate(row, path, isFolder, null);
        }

        sideTree.Refresh();
    }

    /// <summary>A row coloured for git as it's made, a folder opened up has its files coloured straight away.</summary>
    private void DecorateRow(UITreeItem row, string path, bool isFolder, Vector4? usual)
    {
        if (git is not null)
            Decorate(row, path, isFolder, usual);
    }

    private void Decorate(UITreeItem row, string path, bool isFolder, Vector4? usual)
    {
        GitFileState state = isFolder ? new GitFileState(git.FolderChange(path), false, false) : git.StateOf(path);
        row.Color = GitColor(state.Change) ?? usual;

        // A file's letter at the right end in the colour of its change, the same as Source Control has it, a folder only
        // takes the colour
        bool lettered = !isFolder && state.Change is not (GitChange.None or GitChange.Ignored);
        row.Badge = lettered ? state.Letter : string.Empty;
        row.BadgeColor = lettered ? GitColor(state.Change) : null;
    }

    /// <summary>The colour a tab's title takes from its file's change, null for the skin's.</summary>
    private Vector4? TabGitColor(HexDocument shown) =>
        shown.Path is { } path && git.StateOf(path).Change is var change and not (GitChange.None or GitChange.Ignored) ? GitColor(change) : null;
}
