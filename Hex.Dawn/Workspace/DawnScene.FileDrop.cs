using System.Numerics;

using Hex.Shell;

using Horizon.Core;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Files and folders dragged onto the window from outside Hex (Explorer, Finder, a file manager). A file opens in a tab
// the way File > Open opens it (a picture as the picture, a notebook as the notebook), several in one drop each in its
// own and the last one shown. A folder is added to the workspace, or is the workspace when there isn't one, the way Open
// Folder makes it. Let go of over a folder's row in the explorer (or a file's, for the folder it's in) they're copied into
// that folder instead, the way VS Code's explorer takes a drop; the explorer's own drag of its rows is the tree's and
// isn't touched by any of this. While they're held over the window (where the platform says so, Windows), a hairline
// round where they'd land and a line under it say what letting go would do (HexDropOverlay).
internal sealed partial class DawnScene
{
    // What says what letting go would do, made the first time something is held over the window
    private HexDropOverlay? dropOverlay;

    /// <summary>What letting go of files would do, its words, its icon, the row it'd copy them into (null for none) and where to draw it.</summary>
    private readonly record struct DropPlan(string Text, string Icon, string? Into, UIRect? Place);

    /// <summary>Takes what was dropped since the last tick, and says what letting go of what is held would do.</summary>
    private void UpdateFileDrops()
    {
        // With Dawn and Dusk in the one window, what's dropped is for the one on screen
        if (HexModes.Together && HexModes.Current != HexMode.Dawn)
            return;

        var window = Engine.WindowManager;
        while (window.TryTakeFileDrop(out FileDrop drop))
            TakeFileDrop(drop.Paths, drop.HasPosition ? drop.Position : null);

        if (window.FileDrag is { Paths.Count: > 0 } held)
        {
            DropPlan plan = PlanFileDrop(held.Paths, held.HasPosition ? held.Position : null);
            dropOverlay ??= chrome.AddComponent(new HexDropOverlay());
            dropOverlay.Show(plan.Text, plan.Icon, plan.Place);
        }
        else if (dropOverlay is { Visible: true })
        {
            dropOverlay.Hide();
        }
    }

    /// <summary>
    /// Files and folders dropped on the window, at a pixel of it when the platform said where: copied into the explorer's
    /// folder under it, or the files opened and the folders put in the workspace.
    /// </summary>
    internal void TakeFileDrop(IReadOnlyList<string> paths, Vector2? at)
    {
        dropOverlay?.Hide();
        DropPlan plan = PlanFileDrop(paths, at);
        if (plan.Into is { } folder)
        {
            explorer.TransferAsync([.. paths.Where(path => File.Exists(path) || Directory.Exists(path))], folder, cut: false);
            return;
        }

        var files = new List<string>();
        var folders = new List<string>();
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
                folders.Add(Path.GetFullPath(path));
            else if (File.Exists(path))
                files.Add(Path.GetFullPath(path));
        }

        if (files.Count == 0 && folders.Count == 0)
        {
            Say(Said("file_drop.nothing"), error: true);
            return;
        }

        // The folders first, so the files open with the workspace they belong to already there
        foreach (string added in folders)
        {
            if (explorer.workspace.IsEmpty)
                explorer.OpenFolder(added);
            else
                explorer.AddFolder(added);
        }

        // Each in a tab of its own, in the order they came, the last one the one shown
        foreach (string file in files)
            Open(file);

        if (files.Count > 0 || folders.Count > 0)
            Engine.WindowManager.BringToFront();
    }

    /// <summary>What letting go of the paths at a pixel of the window would do, and where it'd land.</summary>
    private DropPlan PlanFileDrop(IReadOnlyList<string> paths, Vector2? at)
    {
        int count = paths.Count;
        if (at is { } pixel && ExplorerFolderAt(pixel) is ({ } row, { } into))
        {
            string items = HexText.Count("item", count);
            return new DropPlan(Said("file_drop.copy_into", ("items", items), ("name", WorkspacePaths.FolderName(into))), "copy", into, sideTree.RowBounds(row));
        }

        int files = 0, folders = 0;
        string? firstFolder = null;
        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                folders++;
                firstFolder ??= path;
            }
            else
            {
                files++;
            }
        }

        string filesSaid = HexText.Count("file", files);
        string folderName = firstFolder is null ? string.Empty : WorkspacePaths.FolderName(firstFolder);
        string foldersSaid = folders == 1 ? folderName : HexText.Count("folder", folders);
        bool asWorkspace = explorer.workspace.IsEmpty;
        string text = (files, folders) switch
        {
            (_, 0) => Said("file_drop.open_files", ("files", filesSaid)),
            (0, _) => Said(asWorkspace ? "file_drop.open_folder" : "file_drop.add_folders", ("folders", foldersSaid)),
            _ => Said(asWorkspace ? "file_drop.open_files_and_folder" : "file_drop.open_files_add_folders", ("files", filesSaid), ("folders", foldersSaid))
        };
        return new DropPlan(text, folders > 0 ? "root-folder-opened" : "go-to-file", null, null);
    }

    /// <summary>
    /// The explorer's folder under a pixel of the window, a file's row for the folder it's in, null when the explorer isn't
    /// showing, has no workspace or the pixel isn't on one of its rows.
    /// </summary>
    private (UITreeItem? Row, string? Folder) ExplorerFolderAt(Vector2 pixel)
    {
        if (Side != HexSide.Explorer || explorer.workspace.IsEmpty || !sideTree.Visible)
            return default;

        Vector2 local = chrome.ToLocal(camera.ScreenToWorld(pixel));
        if (!sideTree.Bounds.Contains(local))
            return default;

        // An empty folder has no chevron, so it's the explorer's own list that says what a row is
        for (UITreeItem? row = sideTree.RowAt(local); row is not null; row = row.Parent)
        {
            foreach (var (path, isFolder, shown) in explorer.explorerRows)
            {
                if (shown == row && isFolder && Directory.Exists(path))
                    return (row, path);
            }
        }

        return default;
    }
}
