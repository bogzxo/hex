using System.Numerics;

using Horizon.UI.Components;

namespace Hex.Dawn;

// The explorer's own buttons at the end of the side bar's title, VS Code's New File, New Folder, Collapse Folders and
// Refresh, and the same on a right click under its rows. A new file or folder goes in the folder that's selected, the
// folder of the file that is, or the workspace's first. An empty folder of the workspace says so with the two buttons
// under it rather than showing only its own row.
internal sealed partial class DawnScene
{
    private Button newFileButton = null!, newFolderButton = null!, collapseButton = null!;

    private void WireExplorerHead()
    {
        newFileButton = layout.Get<Button>("tree_new_file");
        newFolderButton = layout.Get<Button>("tree_new_folder");
        collapseButton = layout.Get<Button>("tree_collapse");
        newFileButton.OnPressed = () => explorer.NewFileIn(ExplorerTarget());
        newFolderButton.OnPressed = () => explorer.NewFolderIn(ExplorerTarget());
        collapseButton.OnPressed = CollapseExplorer;
        sideTree.OnEmptyContextMenu = point =>
        {
            if (Side == HexSide.Explorer && !explorer.workspace.IsEmpty)
                contextMenu = ContextMenu.ShowAt(chrome, compositor.Pointer.Position, ExplorerSpaceMenu());
        };
    }

    /// <summary>Whether the explorer's buttons show, only on the explorer of a workspace, the refresh on any explorer.</summary>
    private void ShowExplorerHead(HexSide side)
    {
        bool workspace = side == HexSide.Explorer && !explorer.workspace.IsEmpty;
        newFileButton.Visible = newFolderButton.Visible = collapseButton.Visible = workspace;
    }

    /// <summary>The menu of the explorer's empty space, what its buttons do.</summary>
    private Menu ExplorerSpaceMenu()
    {
        var menu = new Menu("explorer_space");
        menu.Add(Said("explorer_files.new_file"), () => explorer.NewFileIn(ExplorerTarget()), icon: "new-file");
        menu.Add(Said("explorer_files.new_folder"), () => explorer.NewFolderIn(ExplorerTarget()), icon: "new-folder");
        menu.AddSeparator();
        menu.Add(Said("side.refresh"), RefreshFromDisk, icon: "refresh");
        menu.Add(Said("side.collapse"), CollapseExplorer, icon: "collapse-all");
        return menu;
    }

    /// <summary>The folder a new file or folder goes in, the selected folder, the selected file's, or the workspace's first.</summary>
    private string ExplorerTarget()
    {
        if (sideTree.Selected is { } picked)
        {
            foreach (var (path, isFolder, row) in explorer.explorerRows)
            {
                if (row == picked)
                    return isFolder ? path : Path.GetDirectoryName(path) ?? path;
            }
        }

        return explorer.workspace.Folders.Count > 0 ? explorer.workspace.Folders[0] : browseDirectory;
    }

    /// <summary>Every folder of the explorer closed, the workspace's own folders left open the way VS Code leaves them.</summary>
    private void CollapseExplorer()
    {
        var roots = new HashSet<string>(explorer.workspace.Folders, PlatformPaths.Comparer);
        explorer.workspace.Expanded.RemoveWhere(path => !roots.Contains(path));
        explorer.KeepWorkspace();
        treeDirty = true;
    }

    /// <summary>The note an empty folder of the workspace has under its row, with New File and New Folder.</summary>
    private void AddEmptyFolderNote(UITreeItem root, string folder)
    {
        Vector4? accent = compositor.Skin?.Palette.Accent;
        root.Add(NoteItem(Said("side_tree.empty_folder"), DimColor));
        root.Add(HexNotes.Link(Said("side_tree.new_file"), "new-file", accent, () => explorer.NewFileIn(folder)));
        root.Add(HexNotes.Link(Said("side_tree.new_folder"), "new-folder", accent, () => explorer.NewFolderIn(folder)));
    }
}
