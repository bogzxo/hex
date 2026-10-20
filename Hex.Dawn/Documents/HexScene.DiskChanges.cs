using Horizon.UI.Components;

namespace Hex.Dawn;

// A file that's open being written by something else. The explorer's watcher only heard about files made and deleted,
// so a file changed under Hex (the game writing its options, git, Syncthing bringing the other machine's edit over) kept
// what Hex had read, and saving it put the old text back over the new without anybody seeing it go. Now a file with
// nothing changed in Hex is read again quietly, one step of its undo, and one with changes asks with a card. Saving
// over a file that changed on the disk since Hex last read it asks first, whether or not the watcher heard.
internal sealed partial class HexScene
{
    private readonly HexDiskWatch disk = new();

    // Where the disk's side of a file is put for comparing it with Hex's
    private static string CompareFolder => Path.Combine(Path.GetTempPath(), "Hex", "on the disk");

    /// <summary>The open files with a file of their own watched, after a file is opened, closed or saved somewhere.</summary>
    private void WatchOpenFiles() => disk.Watch(documents.Where(open => open.Path is not null).Select(open => open.Path!));

    /// <summary>What the watch says changed on the disk, acted on. Nothing most ticks.</summary>
    private void UpdateDiskChanges()
    {
        IReadOnlyList<string> changed = disk.Settled();
        for (int i = 0; i < changed.Count; i++)
            DiskChanged(changed[i]);
    }

    private void DiskChanged(string path)
    {
        HexDocument? open = documents.Find(other => other.Path is not null && Path.GetFullPath(other.Path).Equals(path, PlatformPaths.Comparison));
        if (open is null)
            return;

        if (!File.Exists(path))
        {
            if (open.DiskText is not null)
            {
                open.DiskText = null;
                Notify($"{Path.GetFileName(path)} was deleted on the disk, saving it puts it back");
            }
            return;
        }

        string? text = ReadNow(path);
        if (text is null)
        {
            disk.Again(path);
            return;
        }

        // Hex's own save, or a write that left it as it was
        if (text == open.DiskText)
            return;

        if (!HasChanges(open))
        {
            TakeFromDisk(open, text);
            Say($"{open.Name} changed on the disk and was read again, Ctrl+Z has what it was");
            return;
        }

        AskAboutDisk(open, text);
    }

    /// <summary>A file's text off the disk as it is, null when it can't be read just now (somebody is writing it).</summary>
    private static string? ReadNow(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Whether a document has anything in it that isn't in its file.</summary>
    private static bool HasChanges(HexDocument open) => open.IsText
        ? open.Editor.Text != open.SavedCode || open.CrLf != open.SavedCrLf
        : open.Modified || open.CodeEdited || open.GenerateCode() != open.SavedCode;

    /// <summary>A document made what its file says on the disk, as a step of its undo, the caret kept where it can be.</summary>
    private void TakeFromDisk(HexDocument open, string text)
    {
        if (open.IsText)
        {
            SetCode(open, text, fresh: false);
            open.SavedCode = open.Editor.Text;
        }
        else
        {
            var (success, message) = open.Load(text, open.Path, keepHistory: true);
            if (!success)
            {
                Say($"{open.Name} changed on the disk into something that doesn't build, {message}", error: true);
                open.DiskText = text;
                return;
            }

            open.SavedCode = open.GenerateCode();
            open.CodeEdited = false;
            SetCode(open, open.SavedCode, fresh: false);
        }

        open.CrLf = open.SavedCrLf = text.Contains("\r\n", StringComparison.Ordinal);
        open.DiskText = text;
        open.Modified = false;
        tabsDirty = true;
        if (open == document)
            treeDirty = inspectorDirty = codeDirty = true;
    }

    /// <summary>A file with changes in Hex that changed on the disk too, a card that stays until it's answered.</summary>
    private void AskAboutDisk(HexDocument open, string text)
    {
        string name = Path.GetFileName(open.Path!);
        string message = $"{name} changed on the disk, and it has changes here that aren't saved";
        Note(message, error: false);

        UINotification? card = null;
        card = toasts.Show(message, "warning", null, 0.0f,
            ("Take the disk's", () =>
            {
                card?.Close();
                if (documents.Contains(open) && open.Path is { } path && ReadNow(path) is { } now)
                    TakeFromDisk(open, now);
            }),
            ("Keep mine", () =>
            {
                // Saving then writes over the disk's without asking again, that's what was said
                card?.Close();
                if (open.Path is { } path)
                    open.DiskText = ReadNow(path) ?? text;
            }),
            ("Compare", () => CompareWithDisk(open)));
    }

    /// <summary>What the disk has for a file opened next to it, to look at the two.</summary>
    private void CompareWithDisk(HexDocument open)
    {
        if (open.Path is not { } path || ReadNow(path) is not { } text)
            return;

        Directory.CreateDirectory(CompareFolder);
        string copy = Path.Combine(CompareFolder, $"{Path.GetFileNameWithoutExtension(path)}.on-disk{Path.GetExtension(path)}");
        File.WriteAllText(copy, text);

        // A copy opened before is read again rather than opened twice, and goes next to the file, mine on the left
        HexDocument? copied = documents.Find(other => other.Path is not null && Path.GetFullPath(other.Path).Equals(Path.GetFullPath(copy), PlatformPaths.Comparison));
        if (copied is not null)
        {
            TakeFromDisk(copied, text);
        }
        else
        {
            OpenText(copy, text, show: false);
            copied = documents.Find(other => other.Path is not null && Path.GetFullPath(other.Path).Equals(Path.GetFullPath(copy), PlatformPaths.Comparison));
        }

        if (documents.Contains(open))
            Show(open);
        if (copied is not null)
            OpenToSide(copied);
        Say($"what the disk has for {open.Name} on the right, as {Path.GetFileName(copy)}");
    }

    /// <summary>
    /// Whether saving to a path would write over something written there since Hex last read the file, and if so the
    /// question asked. True when it's fine to go on.
    /// </summary>
    private bool SafeToSave(string path)
    {
        if (document.DiskText is not { } known || document.Path is not { } own
            || !Path.GetFullPath(own).Equals(Path.GetFullPath(path), PlatformPaths.Comparison)
            || !File.Exists(path) || ReadNow(path) is not { } now || now == known)
            return true;

        HexDocument saving = document;
        UIDialog.Show(chrome, "Changed on the disk", $"{Path.GetFileName(path)} was changed on the disk since Hex read it. Saving writes over that.",
            new DialogChoice("Save over it", () =>
            {
                if (!documents.Contains(saving))
                    return;
                saving.DiskText = ReadNow(path) ?? now;
                Show(saving);
                SaveTo(path);
            }),
            new DialogChoice("Take the disk's", () =>
            {
                if (documents.Contains(saving) && ReadNow(path) is { } latest)
                    TakeFromDisk(saving, latest);
            }),
            new DialogChoice("Cancel"));
        return false;
    }
}
