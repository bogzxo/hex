using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// The explorer worked in place, VS Code's way. A name is typed over on its row (F2, the menu's Rename) and a new file or
// folder is named on a row of its own in the folder it goes in, what's wrong with the name said under it as it's typed;
// the dialog is only for when the explorer isn't showing the row. Typing finds a row by its name. Files and folders are
// dragged onto a folder to move them there, Ctrl to copy. A file open in a tab has a dot where a folder has its chevron,
// and the file shown has its folders opened and its row picked unless explorer_auto_reveal is off.
internal sealed partial class DawnScene
{
    private void WireExplorerInPlace()
    {
        explorer.RenameInPlace = RenameRowInPlace;
        explorer.NewInPlace = NewRowInPlace;
        sideTree.TypeToFind = true;
        sideTree.FindNothingText = Said("side_tree.find_nothing");
        sideTree.DragManyText = Said("explorer_files.drag_many", ("count", "{0}"));
        sideTree.DropCopies = () => explorerDropCopies || TreeView.CopyHeld(Horizon.Engine.GameEngine.Instance?.Input.Keyboard);
    }

    // The explorer of a workspace on screen, its rows there to type over
    private bool ExplorerShowing => Side == HexSide.Explorer && sideTree.IsShownInHierarchy && !explorer.workspace.IsEmpty;

    private UITreeItem? ExplorerRowOf(string path, bool folder)
    {
        foreach (var (rowPath, isFolder, row) in explorer.explorerRows)
        {
            if (isFolder == folder && PlatformPaths.Same(rowPath, path))
                return row;
        }

        return null;
    }

    /// <summary>A file's or a folder's name typed over on its row, false when the explorer doesn't show it on a row of its own.</summary>
    private bool RenameRowInPlace(string path)
    {
        bool folder = Directory.Exists(path);
        if (!ExplorerShowing || explorer.workspace.Folders.Contains(path, PlatformPaths.Comparer) || ExplorerRowOf(path, folder) is not { } row)
            return false;

        // A row of a chain of folders, assets/ui/flat, is more than the one name
        string old = Path.GetFileName(path.TrimEnd('\\', '/'));
        if (row.Text != old)
            return false;

        // Just the name picked, not its extension, the way VS Code does. A folder or a dotfile (.gitignore) has all of it
        int named = !folder && old.LastIndexOf('.') is > 0 and int dot ? dot : old.Length;
        string parent = Path.GetDirectoryName(Path.GetFullPath(path))!;
        sideTree.Edit(row, old, 0, named,
            typed => typed.Trim() == old ? null : explorer.NameProblem(parent, typed, path),
            typed => typed.Trim() == old || explorer.RenameTo(path, typed));
        return true;
    }

    /// <summary>
    /// A new file or folder named on a row of its own in a folder, the folder opened for it. Folders go first, a file after
    /// the last folder. False when the explorer doesn't show the folder.
    /// </summary>
    private bool NewRowInPlace(string folder, bool isFolder)
    {
        string full = Path.GetFullPath(folder);
        if (!ExplorerShowing || ExplorerRowOf(full, folder: true) is not { } row)
            return false;

        if (!row.Expanded)
            sideTree.Toggle(row);
        row.Children ??= [];

        string extension = isFolder ? string.Empty : explorer.GuessExtension(full);
        string sample = "new" + extension;
        var made = new UITreeItem(string.Empty, isFolder ? "folder" : HexExplorer.FileIcon(sample))
        {
            IconColor = isFolder ? FolderColor : explorer.FileIconColor(sample)
        };

        int at = 0;
        if (!isFolder)
        {
            while (at < row.Children.Count && row.Children[at].Expandable)
                at++;
        }

        // The row picked before, picked again once the new one goes. The edit picks the new row, and left picked once it
        // was gone the next New File went by a row that wasn't there and into the workspace's first folder
        UITreeItem picked = sideTree.Selected ?? row;
        row.Children.Insert(at, made);
        sideTree.Refresh();

        void Gone()
        {
            row.Children?.Remove(made);
            sideTree.Refresh();
            if (sideTree.Selected is null || sideTree.Selected == made)
                sideTree.Selected = explorer.explorerRows.Any(shown => shown.Row == picked) ? picked : row;
        }

        sideTree.Edit(made, string.Empty, 0, 0,
            typed => explorer.NameProblem(full, typed, null, extension),
            typed =>
            {
                bool done = isFolder ? explorer.MakeNamedFolder(full, typed) : explorer.MakeNamedFile(full, typed, extension);
                if (done)
                    Gone();
                return done;
            },
            Gone);
        if (sideTree.EditBox is { } box)
            box.Placeholder = Said(isFolder ? "explorer_files.new_folder_hint" : "explorer_files.new_file_hint");
        return true;
    }

    /* Dragged onto a folder */

    private void WireExplorerDrops()
    {
        sideTree.DropAt = ExplorerDropAt;
        sideTree.OnDrop = ExplorerDropped;
    }

    /// <summary>Whether a drop copies rather than moves, Ctrl, or a self test saying so.</summary>
    private bool explorerDropCopies;

    /// <summary>
    /// Where files and folders dragged would go with the pointer over a row: the folder of the row (a file's own folder),
    /// the workspace's first folder under the rows, and before the first row there that comes after them by name. Nowhere
    /// for a folder of the workspace dragged, a folder into itself, or nothing that would move.
    /// </summary>
    private UITreeDrop? ExplorerDropAt(IReadOnlyList<UITreeItem> dragged, UITreeItem? over)
    {
        if (Side != HexSide.Explorer || explorer.workspace.IsEmpty || dragged.Count == 0)
            return null;

        UITreeItem? into = over;
        while (into is not null && !(into.Expandable && Directory.Exists(into.Tooltip)))
            into = into.Parent;
        into ??= ExplorerRowOf(explorer.workspace.Folders[0], folder: true);
        if (into is null || into.Tooltip is not { Length: > 0 } folder)
            return null;

        bool moves = false;
        foreach (UITreeItem item in dragged)
        {
            string path = item.Tooltip;
            if (path.Length == 0 || explorer.workspace.Folders.Contains(path, PlatformPaths.Comparer))
                return null;

            bool isFolder = Directory.Exists(path);
            if (!isFolder && !File.Exists(path))
                return null;
            if (isFolder && (PlatformPaths.Same(folder, path) || PlatformPaths.IsUnder(folder, path)))
                return null;
            moves |= !PlatformPaths.Same(Path.GetDirectoryName(Path.GetFullPath(path)) ?? path, folder);
        }

        if (!moves)
            return null;

        // Where among the folder's rows the first of them lands, folders before files and each by name
        string first = dragged[0].Tooltip;
        bool firstFolder = Directory.Exists(first);
        string name = Path.GetFileName(first.TrimEnd('\\', '/'));
        UITreeItem? before = null;
        if (into.Expanded && into.Children is { } children)
        {
            foreach (UITreeItem child in children)
            {
                if (child.Tooltip.Length == 0 || dragged.Contains(child))
                    continue;
                bool childFolder = child.Expandable;
                if ((firstFolder && !childFolder) || (firstFolder == childFolder && StringComparer.OrdinalIgnoreCase.Compare(child.Text, name) > 0))
                {
                    before = child;
                    break;
                }
            }
        }

        return new UITreeDrop(into, before);
    }

    private void ExplorerDropped(IReadOnlyList<UITreeItem> dragged, UITreeDrop drop, bool copy)
    {
        string folder = drop.Into.Tooltip;
        var paths = new List<string>(dragged.Count);
        foreach (UITreeItem item in dragged)
        {
            // What's already in the folder stays where it is
            if (item.Tooltip.Length > 0 && (copy || !PlatformPaths.Same(Path.GetDirectoryName(Path.GetFullPath(item.Tooltip)) ?? item.Tooltip, folder)))
                paths.Add(item.Tooltip);
        }

        explorer.TransferAsync(paths, folder, cut: !copy);
    }

    /* Open files */

    // The paths of the files open in tabs, filled again each time the rows are made
    private readonly HashSet<string> openInTabs = new(PlatformPaths.Comparer);

    // The dot a file open in a tab has, the text's colour turned well down
    private Vector4 openInTabMark;

    /// <summary>The files open in tabs gathered as the explorer's rows are about to be made, each file's row has its dot as it's made.</summary>
    private void GatherOpenInTabs()
    {
        openInTabs.Clear();
        foreach (HexDocument open in documents)
        {
            if (open.Path is { } path)
                openInTabs.Add(Path.GetFullPath(path));
        }

        Vector4 text = compositor.Skin?.TextColor ?? Vector4.One;
        openInTabMark = text with { W = text.W * 0.5f };
    }

    /// <summary>The folders between the workspace's folder and the file shown opened, so its row is made to be picked.</summary>
    private void OpenFoldersOfShown()
    {
        if (document?.Path is not { } path)
            return;

        string full = Path.GetFullPath(path);
        foreach (string root in explorer.workspace.Folders)
        {
            if (!PlatformPaths.IsUnder(full, root) || PlatformPaths.Same(full, root))
                continue;

            bool opened = false;
            for (string? folder = Path.GetDirectoryName(full); folder is not null && (PlatformPaths.IsUnder(folder, root) || PlatformPaths.Same(folder, root)); folder = Path.GetDirectoryName(folder))
                opened |= explorer.workspace.Expanded.Add(folder);
            if (opened)
                explorer.KeepWorkspace();
            return;
        }
    }
}
