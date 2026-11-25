using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hex.Core;

// Empty directories travel both ways beside the files, none of which are in them: made, removed and renamed on one side, the
// other follows. Kept apart from the file manifests: a directory listing is asked of the peer ("sync/dirs") once the file sync
// is settled, and the two lists, with what both had last time, say what to make and remove (HexLinkDirs.Plan).
internal sealed partial class HexLinkSync
{
    private const int MOST_VACATED = 20_000;

    private readonly Lock dirGate = new();

    // What both sides had empty at the last pass, and the directories files were taken out of (their deletes and moves seen by
    // either watcher), so a directory left empty when its files went is a delete to follow and not a new directory
    private HashSet<string> agreedDirs = new(StringComparer.Ordinal);
    private HashSet<string> vacatedDirs = new(StringComparer.Ordinal);
    private bool dirsLoaded, dirsDirty, dirsUnsupported;
    private int dirsMade, dirsRemoved;

    /// <summary>How often the empty directories of the two sides are compared while the file sync is settled, four seconds unless a test says.</summary>
    public TimeSpan DirectoryInterval { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>Empty directories made on either side by this sync so far.</summary>
    public int DirectoriesMade => Volatile.Read(ref dirsMade);

    /// <summary>Empty directories removed on either side by this sync so far.</summary>
    public int DirectoriesRemoved => Volatile.Read(ref dirsRemoved);

    /// <summary>
    /// How often they're compared once a few passes in a row found nothing to do, thirty seconds unless a test says: a listing of
    /// the whole tree on both machines and git's ignore rules is too much to spend every four seconds on a folder at rest. Files
    /// taken out of a directory wake the loop at once (see <see cref="NoteVacated"/>).
    /// </summary>
    public TimeSpan DirectoryIdleInterval { get; init; } = TimeSpan.FromSeconds(30);

    private const int IDLE_PASSES = 3;
    private readonly SemaphoreSlim dirsWake = new(0, 1);

    private string DirsFile => StateFile + ".dirs";

    private void StartDirectories(CancellationToken stop) => _ = Task.Run(() => DirectoryLoop(stop), CancellationToken.None);

    // A file went from a place (deleted or moved): the directories above it are ones files were taken out of
    private void NoteVacated(string? wire)
    {
        string prefix = folder.Name + "/";
        if (wire is null || !wire.StartsWith(prefix, StringComparison.Ordinal))
            return;
        lock (dirGate)
        {
            for (int slash = wire.LastIndexOf('/'); slash > prefix.Length - 1; slash = wire.LastIndexOf('/', slash - 1))
            {
                string directory = wire[prefix.Length..slash];
                if (directory.Length == 0 || vacatedDirs.Count >= MOST_VACATED)
                    break;
                dirsDirty |= vacatedDirs.Add(directory);
            }
        }

        if (dirsWake.CurrentCount == 0)
        {
            try { dirsWake.Release(); } catch (SemaphoreFullException) { }
        }
    }

    private void NoteVacatedBy(HexLinkTreeChange change)
    {
        if (change.Deleted)
            NoteVacated(change.Path);
        if (change.PreviousPath is not null)
            NoteVacated(change.PreviousPath);
    }

    private async Task DirectoryLoop(CancellationToken stop)
    {
        try
        {
            await Task.Run(LoadDirs, stop);
            int idle = 0;
            while (!stop.IsCancellationRequested && !dirsUnsupported)
            {
                try
                {
                    if (ready && State is HexLinkSyncState.Idle or HexLinkSyncState.PullOnly && !peer.IsClosed)
                        idle = (await ReconcileDirectories(stop)).Any ? 0 : idle + 1;
                }
                catch (HexLinkException e) when (e.Code == HexLinkErrors.METHOD_NOT_FOUND)
                {
                    dirsUnsupported = true;
                    return;
                }
                catch (Exception e) when (e is HexLinkException or IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    // Asked again at the next pass
                }

                if (await dirsWake.WaitAsync(idle >= IDLE_PASSES ? DirectoryIdleInterval : DirectoryInterval, stop))
                    idle = 0;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>One pass of the empty directories, waiting for nothing else. Returns what it did.</summary>
    internal async Task<HexLinkDirPlan> ReconcileDirectories(CancellationToken cancel)
    {
        bool canSend = !pullOnly && !BoundPullMirror, canReceive = CanReceive;
        List<string> here = await Task.Run(() => HexLinkDirs.Empty(tree, cancel), cancel);
        HexLinkRemoteDirs there = await peer.Directories(folder.Name, list: true, [], [], [], cancel);
        var theirs = new HashSet<string>(there.Empty, StringComparer.Ordinal);
        var mine = new HashSet<string>(here, StringComparer.Ordinal);

        // Which of what only one side lists the other has with files in it
        string[] unmatched = [.. here.Where(path => !theirs.Contains(path)).Concat(there.Empty.Where(path => !mine.Contains(path)))];
        HashSet<string> present = unmatched.Length == 0 ? [] : [.. (await peer.Directories(folder.Name, list: false, unmatched, [], [], cancel)).Present];
        string[] vacated, agreed;
        lock (dirGate)
        {
            vacated = [.. vacatedDirs];
            agreed = [.. agreedDirs];
        }

        HexLinkDirPlan plan = HexLinkDirs.Plan(here, there.Empty, agreed, vacated,
            path => HexLinkDirs.Exists(folder, path), present.Contains, canSend, canReceive);
        var now = new HashSet<string>(plan.Agreed, StringComparer.Ordinal);

        foreach (string path in plan.RemoveHere)
        {
            if (await Task.Run(() => HexLinkDirs.Remove(folder, tree, path, () => Excludes()), cancel))
                Interlocked.Increment(ref dirsRemoved);
        }

        foreach (string path in plan.MakeHere)
        {
            if (await Task.Run(() => HexLinkDirs.Make(folder, tree, path, () => Excludes()), cancel))
                Interlocked.Increment(ref dirsMade);
            else
                now.Remove(path);
        }

        if (plan.MakeThere.Count + plan.RemoveThere.Count > 0)
        {
            HexLinkRemoteDirs done = await peer.Directories(folder.Name, list: false, [], plan.MakeThere, plan.RemoveThere, cancel);
            var made = new HashSet<string>(done.Made, StringComparer.Ordinal);
            foreach (string path in plan.MakeThere.Where(path => !made.Contains(path)))
                now.Remove(path);
            Interlocked.Add(ref dirsMade, made.Count);
            Interlocked.Add(ref dirsRemoved, done.Removed.Count);
        }

        // A directory files were taken out of that neither side lists may only be waiting for the other side to take its files
        // out too (it lists nothing while it has them): it stays marked while either side still has it
        string[] unlisted = [.. vacated.Where(path => !mine.Contains(path) && !theirs.Contains(path) && !now.Contains(path))];
        HashSet<string> stillThere = [];
        if (unlisted.Length > 0)
        {
            stillThere = [.. (await peer.Directories(folder.Name, list: false, unlisted, [], [], cancel)).Present];
            stillThere.UnionWith(unlisted.Where(path => HexLinkDirs.Exists(folder, path)));
        }

        lock (dirGate)
        {
            if (!agreedDirs.SetEquals(now))
            {
                agreedDirs = now;
                dirsDirty = true;
            }

            // A directory neither side lists (or both now agree on) is no longer left over from anything
            dirsDirty |= vacatedDirs.RemoveWhere(path => now.Contains(path) || (!mine.Contains(path) && !theirs.Contains(path) && !stillThere.Contains(path))) > 0;
        }

        if (plan.Any)
            Notify();
        SaveDirs();
        return plan;
    }

    private void LoadDirs()
    {
        lock (dirGate)
        {
            if (dirsLoaded)
                return;
            dirsLoaded = true;
            try
            {
                if (!File.Exists(DirsFile) || JsonNode.Parse(File.ReadAllText(DirsFile)) is not JsonObject saved)
                    return;
                agreedDirs = [.. HexLinkData.Strings(saved, "agreed", HexLinkDirs.MOST, HexLinkFiles.MOST_PATH)];
                vacatedDirs = [.. HexLinkData.Strings(saved, "vacated", MOST_VACATED, HexLinkFiles.MOST_PATH)];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                // Nothing kept is what a first pass starts from: only a directory deleted while this was off comes back
            }
        }
    }

    private void SaveDirs()
    {
        string json;
        lock (dirGate)
        {
            if (!dirsDirty)
                return;
            dirsDirty = false;
            json = new JsonObject
            {
                ["agreed"] = new JsonArray([.. agreedDirs.Select(path => (JsonNode)path)]),
                ["vacated"] = new JsonArray([.. vacatedDirs.Select(path => (JsonNode)path)])
            }.ToJsonString();
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DirsFile)!);
            string temp = DirsFile + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, DirsFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            lock (dirGate)
                dirsDirty = true;
        }
    }
}
