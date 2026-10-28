using Horizon.UI;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private bool ExplorerFileKey(char key)
    {
        var paths = sideTree.SelectedItems.Select(item => item.Tooltip)
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
            default: return false;
        }
    }
}
