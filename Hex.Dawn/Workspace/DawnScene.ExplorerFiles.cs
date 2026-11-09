using Horizon.UI;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private bool ExplorerFileKey(char key)
    {
        var paths = sideTree.SelectedItems.Where(item => !solutionHeads.ContainsKey(item)).Select(item => item.Tooltip)
            .Where(path => (File.Exists(path) || Directory.Exists(path)) && !explorer.workspace.Folders.Contains(path, PlatformPaths.Comparer)).ToList();
        switch (key)
        {
            case UIKeyboard.COPY when paths.Count > 0:
                explorer.CopyPaths(paths); treeDirty = true; return true;
            case UIKeyboard.CUT when paths.Count > 0:
                explorer.CutPaths(paths); treeDirty = true; return true;
            case UIKeyboard.PASTE when explorer.CanPaste:
                explorer.PasteIntoAsync(ExplorerTarget()); return true;
            case UIKeyboard.ESCAPE when explorer.ClippedCut:
                explorer.CancelClip(); treeDirty = true; return true;
            case UIKeyboard.DELETE when sideTree.Selected?.Tooltip is { } path && paths.Contains(path, PlatformPaths.Comparer):
                explorer.DeletePath(path); return true;
            default: return false;
        }
    }

    // F2 with the explorer's row picked, the rename its menu has. Not a folder of the workspace, that's taken out instead
    private void RenameInExplorer()
    {
        if (sideTree.Selected is { } row && !solutionHeads.ContainsKey(row) && row.Tooltip is { } path && (File.Exists(path) || Directory.Exists(path))
            && !explorer.workspace.Folders.Contains(path, PlatformPaths.Comparer))
            explorer.RenamePath(path);
    }
}
