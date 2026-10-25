namespace Hex.Dawn;

// Refresh, for what changed on the disk without Hex hearing of it (a network drive, a watcher that dropped events, a
// tool that writes in a way Windows doesn't say). The folders are read again, git is asked again, and every open file
// is looked at against the disk as if the watcher had said it changed. The button at the end of the side bar's title,
// and a command.
internal sealed partial class DawnScene
{
    private Button refreshButton = null!;

    private void WireRefresh()
    {
        refreshButton = layout.Get<Button>("tree_refresh");
        refreshButton.OnPressed = RefreshFromDisk;
        commands.Add("File/workspace", Said("side.refresh"), RefreshFromDisk, icon: "refresh");
    }

    /// <summary>The explorer's files, git and the open files read again from the disk.</summary>
    private void RefreshFromDisk()
    {
        folders.Forget();
        WatchFolders();
        treeDirty = true;
        git.RefreshAll();

        foreach (HexDocument open in documents.ToList())
        {
            if (open.Path is { } path)
                DiskChanged(Path.GetFullPath(path));
        }

        Say(Said("side.refreshed"));
    }
}
