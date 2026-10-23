namespace Hex.Dawn;

// What isn't saved, kept every couple of seconds in a folder of Hex's own (HexBackups) and brought back the next time Hex
// starts if it never got to say what to do with it, a crash or a power cut. Closing a file or Hex and answering the
// question about its changes either way takes the backup away, so only what was lost comes back.
internal sealed partial class DawnScene
{
    // How often what isn't saved is looked over
    private const float BACKUP_EVERY = 2.0f;

    private HexBackups? backups;
    private float backupWait = BACKUP_EVERY;

    // What every document was the last time it was looked over (its layout as last noted, the version of its code), so
    // one nobody touched since costs a compare and not its whole code written out. The garbage sweep of the self test
    // found that, every changed layout written out every two seconds
    private readonly Dictionary<HexDocument, (string Layout, int Code)> backupSeen = [];

    private HexBackups Backups => backups ??= new HexBackups(BackupFolder);

    // The self test keeps its backups apart, or a run of it would leave things to bring back for the next real start.
    // The data folder next to Hex.exe, or the user's state folder for a Hex that can't write where it is
    private string BackupFolder => options.SelfTest
        ? Path.Combine(Path.GetTempPath(), "Hex", "self test backups")
        : Path.Combine(Horizon.Core.Platform.PlatformPaths.State("Hex"), "backups");

    /// <summary>Every couple of seconds, what has changes kept and what has none any more let go of.</summary>
    private void UpdateBackups(float dt)
    {
        backupWait -= dt;
        if (backupWait > 0.0f)
            return;
        backupWait = BACKUP_EVERY;

        foreach (var open in documents)
        {
            // what the disk had for a file, opened to compare, isn't anybody's work
            if (open.Path is { } path && path.StartsWith(CompareFolder, PlatformPaths.Comparison))
                continue;

            // A layout's code is written out of it again whenever it's shown, which changes the code and not the layout, so
            // its code only counts when it was typed into
            var now = (open.Recorded, open.IsText || open.CodeEdited ? open.Editor.Document.Version : 0);
            if (backupSeen.TryGetValue(open, out var seen) && ReferenceEquals(seen.Layout, now.Recorded) && seen.Code == now.Item2)
                continue;
            backupSeen[open] = now;

            // Modified is kept up for a layout by the tick it's on screen and nothing changes one that isn't, which saves
            // writing its whole code out to ask
            bool changed = open.IsText ? HasChanges(open) : open.Modified || open.CodeEdited;
            if (changed)
                Backups.Keep(open.Path, open.Name, open.IsText || open.CodeEdited ? open.Editor.Text : open.GenerateCode());
            else if (Backups.Has(open.Path, open.Name))
                Backups.Drop(open.Path, open.Name);
        }
    }

    /// <summary>
    /// A document was saved, its backup goes at once. It waited for the next look two seconds on, and Hex closed in
    /// between brought back the text from before the save the next time it started, over whatever the file had by then.
    /// </summary>
    private void DropSavedBackup(HexDocument saved)
    {
        backupSeen.Remove(saved);
        Backups.Drop(saved.Path, saved.Name);
    }

    /// <summary>A document's backup let go of, it was closed and what it had was saved or thrown away.</summary>
    private void DropBackup(HexDocument closing)
    {
        backupSeen.Remove(closing);
        Backups.Drop(closing.Path, closing.Name);
    }

    /// <summary>
    /// What wasn't saved the last time Hex was closed without being asked (it crashed, the power went), opened and put
    /// back as a change to each file, one step of its undo, so Ctrl+Z goes back to what's on the disk.
    /// </summary>
    private void RestoreBackups()
    {
        List<HexBackups.Found> found = Backups.All();
        if (found.Count == 0)
            return;

        // A file Hex was started on stays on screen, what's brought back opens behind it. It came up under a tab of another
        // folder brought back on top of it
        HexDocument? was = document;
        var brought = new List<string>();
        foreach (var backup in found)
        {
            if (BringBack(backup))
                brought.Add(backup.Name);
            Backups.Remove(backup);
        }

        if (options.Open is not null && was is { Path: not null } && documents.Contains(was) && document != was)
            Show(was);

        if (brought.Count > 0)
            Notify(Said("backups.restored", ("files", string.Join(", ", brought))));
    }

    private bool BringBack(HexBackups.Found backup)
    {
        // A file that's gone since is brought back as an untitled layout or a copy of the text, never written for it
        string? path = backup.Path;
        if (path is null || !File.Exists(path))
        {
            if (path is null)
            {
                HexDocument made = NewDocument();
                made.Name = backup.Name;
                var (success, _) = made.Load(backup.Text);
                Show(made);
                return success;
            }

            Directory.CreateDirectory(CompareFolder);
            string copy = Path.Combine(CompareFolder, Path.GetFileName(path));
            File.WriteAllText(copy, backup.Text);
            return OpenText(copy, backup.Text);
        }

        // Saved after all, there's nothing to bring back
        if (ReadNow(path) is { } onDisk && onDisk.Replace("\r\n", "\n") == backup.Text)
            return false;

        // The file was written after the backup was (a checkout, another editor), what it has now is newer than what was
        // lost and is never written over by it
        try
        {
            if (File.GetLastWriteTimeUtc(path) > File.GetLastWriteTimeUtc(backup.File))
                return false;
        }
        catch (Exception)
        {
            return false;
        }

        HexDocument? open = documents.Find(other => other.Path is not null && Path.GetFullPath(other.Path).Equals(Path.GetFullPath(path), PlatformPaths.Comparison));
        if (open is null)
        {
            if (!Open(path))
                return false;
            open = document;
        }
        else
        {
            Show(open);
        }

        if (open.IsText)
        {
            SetCode(open, backup.Text, fresh: false);
        }
        else
        {
            open.Load(backup.Text, path, keepHistory: true);
            treeDirty = inspectorDirty = true;
        }

        codeDirty = tabsDirty = true;
        return true;
    }
}
