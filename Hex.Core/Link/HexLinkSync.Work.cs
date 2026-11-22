using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkSync
{
    // How many paths the queue hands over at once, asking git which of them it ignores in one go
    private const int MOST_BATCH = 64;

    // Cap retries at a ten-second delay without dropping a pending path.
    private const int MAX_RETRY_DELAY = 10_000;

    // The most skipped files listed
    private const int MOST_SKIPPED = 2000;

    // Per wire path: what both sides last agreed on (each side's hash of it, which differ when only their line ends do),
    // what the other side has now as far as it's said (a file whose hash is null is too big to hash), the conflicts
    // waiting and what doesn't travel
    private readonly Dictionary<string, HexLinkSyncAgreed> bases = new(StringComparer.Ordinal);
    private int eolOnly;
    private readonly CaseNames hereNames = new(), thereNames = new();
    private readonly Dictionary<string, HexLinkTreeFile> remote = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HexLinkSyncConflict> conflicts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HexLinkSyncSkipped> skipped = new(StringComparer.Ordinal);

    // The queue: paths waiting in the order they changed, those being worked on, those that changed again while they
    // were (looked at again once they're done, so a path is only ever worked on once at a time and in order), and how
    // many times each has been tried again in a row
    private readonly Queue<string> queue = new();
    private readonly HashSet<string> queued = new(StringComparer.Ordinal), running = new(StringComparer.Ordinal), again = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> tries = new(StringComparer.Ordinal);
    private readonly HashSet<string> deferredPushes = new(StringComparer.Ordinal);
    private int rightsVersion;
    private readonly SemaphoreSlim wake = new(0), slots;
    private readonly HashSet<string> retrying = new(StringComparer.Ordinal);
    private int taking, filesDone, filesTotal;
    private long bytesDone, bytesTotal;
    private string? detail, retryDetail;
    private Dictionary<string, long>? passSizes;
    private TaskCompletionSource? drained;

    // The first comparison done: what differs queued, both trees watched from now on
    private void Begin(HexLinkManifest mine, HexLinkManifest theirs, HexLinkTreeSubscription watch, HexLinkManifest actual)
    {
        var here = new Dictionary<string, HexLinkTreeFile>(StringComparer.Ordinal);
        foreach (HexLinkTreeFile file in mine.Files)
        {
            if (!Ours(file.Path))
                here[file.Path] = file;
        }

        var differ = new Dictionary<string, long>(StringComparer.Ordinal);
        CancellationToken stop;
        lock (gate)
        {
            if (!live)
                throw new OperationCanceledException("Sync stopped before comparison finished.");
            if (earlyOverflow)
                throw new HexLinkException(HexLinkErrors.TOO_BIG, "Too many paths changed during the manifest read. Retry the sync.");
            stop = stopping.Token;
            remote.Clear();
            remoteHeld.Clear();
            remoteHeld.UnionWith(theirs.HeldPaths ?? []);
            offeredWhileHeld.Clear();
            remoteSeenWhileHeld.Clear();
            releasedPeerOffers.Clear();
            remoteCertified = theirs.Certified;
            CompareHeads(mine, theirs);
            insensitivePaths = mine.CaseSensitive != true || theirs.CaseSensitive != true;
            hereNames.Clear();
            thereNames.Clear();
            foreach (string path in here.Keys)
                hereNames.Add(path);
            foreach (HexLinkTreeFile file in theirs.Files)
            {
                if (!Ours(file.Path))
                {
                    remote[file.Path] = file;
                    thereNames.Add(file.Path);
                }
            }

            // What the other side said changed while the two were being listed, on top of its manifest
            foreach (HexLinkTreeChange change in early ?? [])
            {
                if (!Ours(change.Path) && (!change.Deleted || remoteCertified))
                    There(change);
            }

            early = null;
            var paths = new HashSet<string>(here.Keys, StringComparer.Ordinal);
            paths.UnionWith(remote.Keys);
            paths.UnionWith(bases.Keys);
            paths.UnionWith(conflicts.Keys);
            paths.UnionWith(holdPaths);
            paths.UnionWith(remoteHeld);
            foreach (string path in paths)
            {
                if (BlockedRepository(path) || conflicts.GetValueOrDefault(path)?.HasUnsavedChanges == true)
                    continue;
                if (holdPaths.Contains(path) || remoteHeld.Contains(path) || CaseDiffers(path))
                {
                    differ.Add(path, TransferSize(here.GetValueOrDefault(path), remote.GetValueOrDefault(path)));
                    continue;
                }

                HexLinkTreeFile? mineFile = here.GetValueOrDefault(path), theirsFile = remote.GetValueOrDefault(path);
                string? m = mineFile?.Hash, t = theirsFile?.Hash;
                bool mineThere = mineFile is not null, theirsThere = theirsFile is not null;
                if (peer.Session.SupportsBulk && m is not null && m == t && mineFile!.Executable != theirsFile!.Executable)
                {
                    differ.Add(path, TransferSize(mineFile, theirsFile));
                }
                else if (m is not null && bases.TryGetValue(path, out HexLinkSyncAgreed? agreed) && agreed.Local == m && agreed.Remote == t)
                {
                    // Where both were left, line ends and all. Older state did not record executable mode.
                    if (agreed.Executable is null && mineFile!.Executable is { } mode && mode == theirsFile?.Executable)
                        SetBase(path, agreed with { Executable = mode });
                }
                else if (m is not null && m == t)
                {
                    // The same on both, agreed whatever was known before, a conflict settled by hand on both sides with it
                    if (mineFile!.Text is { } same && mineFile.Eol is not null)
                        SetBase(path, new HexLinkSyncAgreed(m, m, same, EolIn(mineFile.Eol), EolIn(mineFile.Eol), mineFile.Executable));
                    else
                        SetBase(path, Identical(m) with { Executable = mineFile!.Executable });
                    conflicts.Remove(path);
                }
                else if (m is not null && t is not null && mineFile!.Text is { } text && text == theirsFile!.Text)
                {
                    // The same text with other line ends, a CRLF checkout against an LF one, told by the two manifests
                    // alone: agreed as each side has it, nothing read
                    SetBase(path, new HexLinkSyncAgreed(m, t, text, EolIn(mineFile.Eol), EolIn(theirsFile.Eol)));
                    conflicts.Remove(path);
                    eolOnly++;
                }
                else if (!mineThere && !theirsThere)
                {
                    if (remoteCertified)
                    {
                        RemoveBase(path);
                        conflicts.Remove(path);
                    }
                }
                else
                {
                    differ.Add(path, TransferSize(mineFile, theirsFile));
                }
            }

            ResetProgress();
            passSizes = differ;
            filesTotal = differ.Count;
            bytesTotal = differ.Values.Sum();
            foreach (string path in differ.Keys)
            {
                queued.Add(path);
                queue.Enqueue(path);
            }
            local = watch;
            ready = true;
            State = pullOnly ? HexLinkSyncState.PullOnly : HexLinkSyncState.Syncing;
            Why = pullOnly ? $"{peer.Device.Name} doesn't let this device write there (Edit)" : null;
            followPoll = followRepositories.Length != 0 ? Task.Run(() => PollGitFollow(stop)) : Task.CompletedTask;
        }

        watch.Seed(actual);
        Notify();
        _ = Task.Run(() => Work(stop));
        if (differ.Count > 0)
            wake.Release((differ.Count + MOST_BATCH - 1) / MOST_BATCH);
        CheckIdle();
        Save();
    }

    private static long TransferSize(HexLinkTreeFile? here, HexLinkTreeFile? there) =>
        here is { Big: true } || there is { Big: true } ? 0 : Math.Max(here?.Size ?? 0, there?.Size ?? 0);

    // This side's tree changed (its watcher's thread): what isn't settled already is queued
    private void Saw(IReadOnlyList<HexLinkTreeChange> changes)
    {
        using var timing = HexLinkSyncTiming.Measure("watch.local-echo", changes.Count);
        var dirty = new List<string>();
        lock (gate)
        {
            if (!live || !ready)
                return;
            foreach (HexLinkTreeChange change in changes)
            {
                if (Ours(change.Path))
                    continue;
                if (change.PreviousPath is { } previous)
                    hereNames.Remove(previous);
                if (change.Deleted)
                    hereNames.Remove(change.Path);
                else
                    hereNames.Add(change.Path);
                if (hereNames.Name(change.Path) is { } remaining && remaining != change.Path)
                    dirty.Add(remaining);
                bases.TryGetValue(change.Path, out HexLinkSyncAgreed? agreed);
                remote.TryGetValue(change.Path, out HexLinkTreeFile? theirs);
                // A file written here from the other side, reported back by the watcher with the hash it was agreed at
                bool settled = change.Deleted
                    ? agreed is null && theirs is null
                    : change.Hash is not null && agreed is not null && change.Hash == agreed.Local && theirs?.Hash == agreed.Remote &&
                        (change.Executable is null || change.Executable == agreed.Executable);
                if (!settled || remoteHeld.Contains(change.Path) || change.PreviousPath is not null || CaseDiffers(change.Path))
                    dirty.Add(change.Path);
            }
        }

        foreach (string path in dirty)
            Dirty(path);
    }

    // The other side's tree changed (the link's reading task, so nothing slow): what it has now noted, and what isn't
    // at the base queued. What this side sent comes back here with the hash it was agreed at and is dropped.
    private void Heard(IReadOnlyList<HexLinkTreeChange> changes)
    {
        using var timing = HexLinkSyncTiming.Measure("watch.remote-echo", changes.Count);
        var dirty = new List<string>();
        lock (gate)
        {
            if (!live)
                return;
            if (!ready)
            {
                if (early is not null)
                {
                    foreach (HexLinkTreeChange change in changes)
                    {
                        if (early.Count >= HexLinkTree.MOST_FILES)
                        {
                            earlyOverflow = true;
                            break;
                        }

                        early.Add(change);
                    }
                }
                return;
            }

            foreach (HexLinkTreeChange change in changes)
            {
                if (Ours(change.Path) || (change.Deleted && !remoteCertified))
                    continue;
                There(change);
                if (change.HoldNotice is not null)
                {
                    dirty.Add(change.Path);
                    continue;
                }
                bases.TryGetValue(change.Path, out HexLinkSyncAgreed? agreed);
                bool settled = change.Deleted ? agreed is null : change.Hash is not null && change.Hash == agreed?.Remote &&
                    (change.Executable is null || change.Executable == agreed.Executable);
                if (!settled || holdPaths.Contains(change.Path) || remoteHeld.Contains(change.Path))
                    dirty.Add(change.Path);
                if (change.PreviousPath is { } previous && holdPaths.Contains(previous))
                    dirty.Add(previous);
            }
        }

        foreach (string path in dirty)
            Dirty(path);
    }

    // What the other side has of a path now, noted (under the gate)
    private void There(HexLinkTreeChange change)
    {
        if (change.HoldNotice is { } held)
        {
            NoteRemoteHold(change.Path, held, fromNotice: true);
            return;
        }
        ObserveDuringPublication(change.Path);
        if (change.PreviousPath is { } previous && remoteCertified)
        {
            ObserveDuringPublication(previous);
            RemoveRemote(previous);
        }
        if (change.Deleted)
            RemoveRemote(change.Path);
        else
        {
            remote[change.Path] = new HexLinkTreeFile(change.Path, change.Hash, change.Size, change.Text, change.Eol, change.Executable, change.Unavailable);
            thereNames.Add(change.Path);
        }
    }

    private void RemoveRemote(string path)
    {
        remote.Remove(path);
        thereNames.Remove(path);
    }

    private void There(string path, string? hash)
    {
        if (hash is null)
            RemoveRemote(path);
        else if (remote.GetValueOrDefault(path)?.Hash != hash)
        {
            remote[path] = new HexLinkTreeFile(path, hash, 0);
            thereNames.Add(path);
        }
    }

    // Accessed under the sync gate. One spelling per path is common, so only allocate a set for actual collisions.
    private sealed class CaseNames
    {
        private readonly Dictionary<string, (string First, HashSet<string>? Others)> names = new(StringComparer.OrdinalIgnoreCase);

        public void Clear() => names.Clear();
        public string? Name(string path) => names.GetValueOrDefault(path).First;
        public bool Collides(string path) => names.GetValueOrDefault(path).Others is not null;

        public void Add(string path)
        {
            if (!names.TryGetValue(path, out var group))
                names.Add(path, (path, null));
            else if (path != group.First)
            {
                if (group.Others is null)
                    names[path] = (group.First, new HashSet<string>(StringComparer.Ordinal) { path });
                else
                    group.Others.Add(path);
            }
        }

        public void Remove(string path)
        {
            if (!names.TryGetValue(path, out var group))
                return;
            if (group.First == path)
            {
                if (group.Others is null)
                {
                    names.Remove(path);
                    return;
                }
                string next = group.Others.First();
                group.Others.Remove(next);
                group.First = next;
            }
            else
            {
                group.Others?.Remove(path);
            }

            if (group.Others is { Count: 0 })
                group.Others = null;
            names[path] = group;
        }
    }

    private bool CaseDiffers(string wire) =>
        (insensitivePaths && (hereNames.Collides(wire) || thereNames.Collides(wire))) ||
        (hereNames.Name(wire) is { } here && thereNames.Name(wire) is { } there && here != there);

    // A pair of spellings can be a rename only when one spelling has the previous shared base.
    private async Task<bool> ReconcileCase(string wire, CancellationToken stop)
    {
        string? before = null, after = null, expected = null;
        bool push = false, collision;
        lock (gate)
        {
            if (!CaseDiffers(wire))
                return false;

            string? here = hereNames.Name(wire), there = thereNames.Name(wire);
            collision = hereNames.Collides(wire) || thereNames.Collides(wire);
            if (!collision && remoteCertified && here is not null && there is not null)
            {
                HexLinkSyncAgreed? hereBase = bases.GetValueOrDefault(here), thereBase = bases.GetValueOrDefault(there);
                if (hereBase is null && thereBase is not null && !conflicts.ContainsKey(there))
                {
                    (before, after, expected, push) = (there, here, thereBase.Remote, true);
                }
                else if (thereBase is null && hereBase is not null && !conflicts.ContainsKey(here))
                {
                    (before, after, expected) = (here, there, hereBase.Local);
                }
            }
        }

        if (before is null || after is null || expected is null)
        {
            if (!insensitivePaths)
                return false;
            Skip(wire, "Its name collides with another path that differs only in case.");
            return true;
        }
        if (Holds?.IsHeld(before) == true || Holds?.IsHeld(after) == true)
            return false;
        if (wire != after)
            return true;

        string from = Jailed(before), to = Jailed(after);
        if (Path.GetDirectoryName(from) != Path.GetDirectoryName(to))
        {
            Skip(wire, "The directory name differs only in case and requires an explicit rename.");
            return true;
        }
        string prefix = folder.Name + "/";
        if (tree.Ignored([before[prefix.Length..], after[prefix.Length..]]).Count != 0)
        {
            Skip(wire, "The previous or new name is ignored.");
            return true;
        }

        if (push)
        {
            if (pullOnly)
            {
                lock (gate)
                    deferredPushes.Add(wire);
                return true;
            }
            await Asked(() => peer.RenameCase(folder.Name, before, after, expected, stop), stop);
        }
        else
        {
            if (!CanReceive)
            {
                Skip(wire, VIEW_ONLY_REPLICA);
                return true;
            }
            await RenameLocal(from, to, expected, stop);
            tree.NoteCase(after[prefix.Length..]);
        }

        lock (gate)
        {
            if (bases.TryGetValue(before, out HexLinkSyncAgreed? agreed))
            {
                RemoveBase(before);
                SetBase(after, agreed);
            }
            if (push)
            {
                if (remote.GetValueOrDefault(before) is { Hash: var hash } old && hash == expected)
                {
                    RemoveRemote(before);
                    remote[after] = old with { Path = after };
                    thereNames.Add(after);
                }
                Interlocked.Increment(ref sent);
            }
            else
            {
                hereNames.Remove(before);
                hereNames.Add(after);
                Interlocked.Increment(ref received);
            }
        }

        Unskip(before);
        Dirty(after);
        Notify();
        return true;
    }

    // Both sides have a path with the same bytes (or neither has it), the base from now on (under the gate)
    private void Agree(string path, string? hash)
    {
        if (hash is null)
        {
            RemoveBase(path);
            tries.Remove(path);
        }
        else if (!bases.TryGetValue(path, out HexLinkSyncAgreed? was) || was.Local != hash || was.Remote != hash)
        {
            SetBase(path, Identical(hash));
        }
    }

    // A path queued to be looked at, after whatever's already being done with it
    private void Dirty(string wire, bool retry = false)
    {
        bool woke = false, busy = false;
        lock (gate)
        {
            if (!live || !ready)
                return;
            if (retry)
            {
                retrying.Remove(wire);
                if (retrying.Count == 0)
                    Volatile.Write(ref retryDetail, null);
            }
            else if (!retrying.Contains(wire))
                tries.Remove(wire);
            if (running.Contains(wire))
            {
                again.Add(wire);
            }
            else if (queued.Add(wire))
            {
                queue.Enqueue(wire);
                woke = true;
            }

            if (State == HexLinkSyncState.Idle)
            {
                State = HexLinkSyncState.Syncing;
                busy = true;
            }
        }

        if (woke)
            wake.Release();
        if (busy)
            Notify();
    }

    private void Again(string wire, string? why = null, bool reportAsSkipped = true)
    {
        int count;
        CancellationToken stop;
        lock (gate)
        {
            if (!live || !ready || !retrying.Add(wire))
                return;
            count = tries[wire] = Math.Min(tries.GetValueOrDefault(wire) + 1, 15);
            stop = stopping.Token;
            Volatile.Write(ref retryDetail, why ?? wire);
        }

        if (reportAsSkipped && (why is not null || count > 6))
            Skip(wire, why ?? "The file kept changing during sync. Link will retry.");
        _ = Retry(wire, Math.Min(MAX_RETRY_DELAY, 50 * count * count), stop);
    }

    private async Task Retry(string wire, int wait, CancellationToken stop)
    {
        try
        {
            await Task.Delay(wait, stop);
            stop.ThrowIfCancellationRequested();
            Dirty(wire, retry: true);
            CheckIdle();
        }
        catch (OperationCanceledException)
        {
        }
    }

    // The queue's worker: a batch at a time, git asked once which of it this side ignores, then each path worked on
    // with at most MOST_IN_FLIGHT at once
    private async Task Work(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await wake.WaitAsync(stop);
                var batch = new List<string>();
                lock (gate)
                {
                    taking++;
                    while (batch.Count < (peer.Session.SupportsBulk ? 2 * HexLinkFilePack.MaximumFiles : MOST_BATCH) && queue.TryDequeue(out string? wire))
                    {
                        queued.Remove(wire);
                        running.Add(wire);
                        batch.Add(wire);
                    }
                }

                try
                {
                    if (batch.Count == 0)
                        continue;
                    HashSet<string> ignored;
                    try
                    {
                        ignored = await Task.Run(() => IgnoredOf(batch), stop);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        foreach (string wire in batch)
                            Again(wire, e.Message);
                        lock (gate)
                        {
                            foreach (string wire in batch)
                                running.Remove(wire);
                        }
                        continue;
                    }
                    for (int i = 0; i < batch.Count; i++)
                    {
                        try
                        {
                            await slots.WaitAsync(stop);
                        }
                        catch (OperationCanceledException)
                        {
                            lock (gate)
                            {
                                foreach (string left in batch.Skip(i))
                                    running.Remove(left);
                            }

                            throw;
                        }

                        string wire = batch[i];
                        _ = Task.Run(() => Run(wire, ignored.Contains(wire), stop));
                    }
                }
                finally
                {
                    lock (gate)
                        taking--;
                    CheckIdle();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Which of a batch's paths git ignores here (or which are under bin, obj and the like outside a repository)
    private HashSet<string> IgnoredOf(List<string> batch)
    {
        string start = folder.Name + "/";
        var relative = batch.Where(wire => wire.StartsWith(start, StringComparison.Ordinal)).Select(wire => wire[start.Length..]).ToList();
        return new HashSet<string>(tree.Ignored(relative).Select(each => start + each), StringComparer.Ordinal);
    }

    private async Task<byte[]?> ReadLocal(string full, CancellationToken stop)
    {
        await using var lease = await HexLinkPublicationFence.ForRoot(full).EnterLive(stop);
        await EnsureLocalGitFollowFile(full, stop);
        return HexLinkTree.RegularFile(full, tree.Root) is null ? null : await HexLinkFiles.ReadSharedAsync(full, stop);
    }

    // One path worked on, whatever happens the queue going on after it
    private async Task Run(string wire, bool ignored, CancellationToken stop)
    {
        Volatile.Write(ref detail, wire);
        try
        {
            using var follow = AdmitGitFollowPath(wire);
            await Reconcile(wire, ignored, stop);
        }
        catch (OperationCanceledException)
        {
        }
        catch (HexLinkException e) when (e.Code == HexLinkErrors.CLOSED)
        {
        }
        catch (HexLinkException e) when (e.Code == HexLinkErrors.BUSY)
        {
            using var timing = HexLinkSyncTiming.Measure("retry.bulk-capacity");
            Again(wire, e.Message, reportAsSkipped: false);
        }
        catch (HexLinkException e) when (e.Code is HexLinkErrors.CONFLICT or HexLinkErrors.TIMED_OUT or HexLinkErrors.INTERNAL_ERROR or HexLinkErrors.NOT_FOUND)
        {
            using var timing = HexLinkSyncTiming.Measure($"retry.rpc-{e.Code}");
            Again(wire, e.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            using var timing = HexLinkSyncTiming.Measure("retry.io-unavailable");
            // Open here a moment (an editor saving it, a virus scanner, an indexer, on Windows any reader stops a file
            // being moved over), tried again in a moment rather than left until it next changes
            Again(wire, $"The file is unavailable. Link will retry. {e.Message}");
        }
        catch (Exception e)
        {
            using var timing = HexLinkSyncTiming.Measure(e is HexLinkException error ? $"skip.rpc-{error.Code}" : "skip.other");
            Skip(wire, e.Message);
        }
        finally
        {
            slots.Release();
            bool requeue;
            lock (gate)
            {
                running.Remove(wire);
                requeue = again.Remove(wire) && live && queued.Add(wire);
                if (requeue)
                    queue.Enqueue(wire);
                else if (!retrying.Contains(wire))
                {
                    tries.Remove(wire);
                    if (passSizes?.Remove(wire, out long size) == true)
                    {
                        Interlocked.Increment(ref filesDone);
                        Interlocked.Add(ref bytesDone, size);
                    }
                }
            }

            if (requeue)
                wake.Release();
            CheckIdle();
            MaybeSave();
        }
    }

    // A path looked at as it is on both sides now against its base, and whatever that calls for done
    private async Task Reconcile(string wire, bool ignored, CancellationToken stop)
    {
        if (BlockedRepository(wire))
            return;
        string full;
        try
        {
            full = Jailed(wire);
        }
        catch (HexLinkException e)
        {
            Skip(wire, e.Message);
            return;
        }

        // What git ignores here never travels, either way: the other side sending one is never written over this side's
        if (ignored)
            return;

        bool unavailable;
        lock (gate)
            unavailable = remote.GetValueOrDefault(wire)?.Unavailable == true;
        if (unavailable)
        {
            Again(wire, "The other device cannot read this file. Link will retry.");
            return;
        }

        if (DefersPublication(wire) || await ReconcileHold(wire, stop))
            return;
        await EnsureLocalGitFollowFile(full, stop);
        if (await ReconcileCase(wire, stop))
            return;

        // This side's file hashed from what's on the disk now, never a hash cached by its length and time written, which
        // two saves within one tick of the clock can share
        HexLinkTreeFile? mine = null;
        byte[]? localBytes = null;
        HexLinkTreeFile? theirs;
        HexLinkSyncAgreed? agreed;
        HexLinkSyncConflict? held;
        bool firstComparison;
        await using (await HexLinkPublicationFence.ForRoot(full).EnterLive(stop))
        {
            if (DefersPublication(wire))
                return;
            if (!PlatformPaths.Comparer.Equals(full, Jailed(wire)))
                throw new HexLinkException(HexLinkErrors.CONFLICT, "The file path changed before it was compared.");
            if (HexLinkTree.RegularFile(full, tree.Root) is { } info)
            {
                if (info.Length > HexLinkFiles.MOST_READ)
                    mine = await ReadStreamedMetadata(wire, full, info, stop);
                else
                {
                    using (HexLinkSyncTiming.Measure("reconcile.read-source"))
                        localBytes = peer.Session.SupportsBulk && info.Length <= 64 * 1024
                            ? HexLinkFiles.ReadShared(full)
                            : await HexLinkFiles.ReadSharedAsync(full, stop);
                    TextInfo text = TextOf(localBytes);
                    mine = new HexLinkTreeFile(wire, HexLinkFiles.Hash(localBytes), localBytes.LongLength, text.Norm, EolWord(text.Eol), tree.Mode(full));
                }
            }
            lock (gate)
            {
                remote.TryGetValue(wire, out theirs);
                bases.TryGetValue(wire, out agreed);
                conflicts.TryGetValue(wire, out held);
                firstComparison = passSizes?.ContainsKey(wire) == true;
            }
        }

        if (theirs?.Unavailable == true)
        {
            Again(wire, "The other device cannot read this file. Link will retry.");
            return;
        }

        if (mine is { Big: true } || theirs is { Big: true })
        {
            Skip(wire, $"it's over {HexLinkFiles.MOST_READ / 1024 / 1024} MB, a link doesn't carry it");
            return;
        }

        Unskip(wire);
        if (await ReconcilePeerHold(wire, full, mine, theirs, agreed, localBytes, stop)
            || await TakeReleasedPeerChange(wire, full, mine, theirs, stop))
            return;
        if (!CanReceive && await ReconcileWithoutReplicaEdits(wire, full, mine, theirs, agreed, localBytes, stop))
            return;
        if (held?.HasUnsavedChanges != true && mine is not null && theirs is not null &&
            await ReconcileMode(wire, full, mine, theirs, agreed, localBytes, stop))
            return;
        if ((mine?.Size ?? 0) > HexLinkFiles.MOST_READ || (theirs?.Size ?? 0) > HexLinkFiles.MOST_READ)
        {
            if (firstComparison && agreed is null && held is null && (mine is null || theirs is null) &&
                (staleTracked.Contains(wire) || await StreamedPathHadSharedBase(wire, stop)))
            {
                HoldStreamedConflict(wire, "tracked large file has no agreed working-tree hash");
                return;
            }
            await ReconcileStreamed(wire, full, mine, theirs, agreed, held, stop);
            return;
        }
        if (held?.HasUnsavedChanges == true)
            return;
        string? m = mine?.Hash, t = theirs?.Hash;
        // Each side still at what it agreed, which may be the same file with different line ends
        if (agreed is not null && m == agreed.Local && t == agreed.Remote)
            return;

        if (m == t)
        {
            bool settled;
            lock (gate)
            {
                Agree(wire, m);
                settled = conflicts.Remove(wire);
            }

            if (settled)
            {
                Save();
                Notify();
            }

            return;
        }

        // The same text but for line ends, by what the other side said of its file: agreed as each has it, nothing read.
        // A change there of only its line ends lands here.
        if (m is not null && t is not null && mine!.Text is { } same && same == theirs!.Text)
        {
            bool settled;
            lock (gate)
            {
                SetBase(wire, new HexLinkSyncAgreed(m, t, same, EolIn(mine.Eol), EolIn(theirs.Eol)));
                settled = conflicts.Remove(wire);
            }

            Interlocked.Increment(ref eolOnly);
            if (settled)
                Save();
            Notify();
            return;
        }

        // A conflict waits for somebody to pick a side, or for both sides to come to the same thing
        if (held is not null)
            return;

        // Each side is looked at against its own agreed hash: only one moved, that one goes to the other
        if (agreed is not null)
        {
            if (m == agreed.Local)
            {
                await Pull(wire, full, m, theirs, stop);
                return;
            }

            if (t == agreed.Remote)
            {
                await Push(wire, full, agreed, localBytes, m, t, stop);
                return;
            }
        }
        else if ((firstComparison || HasFollowBase(wire)) && await ReconcileFromHead(wire, full, mine, theirs, localBytes, stop))
        {
            return;
        }
        else if (firstComparison && staleTracked.Contains(wire))
        {
            await Conflict(wire, full, m, t, stop);
            return;
        }
        else if (m is null)
        {
            await Pull(wire, full, null, theirs, stop);
            return;
        }
        else if (t is null)
        {
            await Push(wire, full, null, localBytes, m, null, stop);
            return;
        }

        if (await TryTextMerge(wire, full, agreed, localBytes, m, t, stop)) return;
        await Conflict(wire, full, m, t, stop);
    }

    // The other side's version written here (or its delete carried out), when this side's is still what it was
    // (expected) by the time it's written
    private async Task Pull(string wire, string full, string? expected, HexLinkTreeFile? theirs, CancellationToken stop)
    {
        if (await ReconcileHold(wire, stop))
            return;
        if (theirs is null)
        {
            if (!remoteCertified || !await Asked(() => peer.ConfirmAbsent(folder.Name, wire, stop), stop))
            {
                Skip(wire, "The peer cannot confirm a working-tree deletion.");
                return;
            }

            if (expected is not null)
            {
                try
                {
                    await ApplyLocal(full, wire, expected, null, stop, null);
                }
                catch (HexLinkException e) when (EditorDirty(e))
                {
                    await HoldEditorConflict(wire, full, null, "unsaved changes here, deleted there", stop);
                    return;
                }
            }
            lock (gate)
                Agree(wire, null);
            Interlocked.Increment(ref received);
            Notify();
            return;
        }

        HexLinkFile file = await Fetch(wire, stop);
        if (file.Hash != theirs.Hash)
        {
            Again(wire);
            return;
        }
        byte[]? local = await ReadLocal(full, stop);

        string? now = local is null ? null : HexLinkFiles.Hash(local);
        if (now != expected)
        {
            // Changed here while it was being fetched, looked at again with both as they are now
            Again(wire);
            return;
        }

        // The same here already, or the same but for line ends: what changed there was only its line ends, nothing's
        // written and each side keeps its own
        if (local is not null && SameText(local, file.Bytes))
        {
            lock (gate)
                SetBase(wire, AgreedOf(local, file.Bytes));
            if (now != file.Hash)
                Interlocked.Increment(ref eolOnly);
            Notify();
            return;
        }

        // Written in this side's line ends: those of the file it replaces, or for a new one what most files here use
        TextInfo info = TextOf(file.Bytes);
        HexLinkEol here = local is null ? HexLinkEol.Unknown : TextOf(local).Eol;
        if (here is not (HexLinkEol.Lf or HexLinkEol.Crlf))
        {
            lock (gate)
                here = bases.GetValueOrDefault(wire)?.LocalEol ?? HexLinkEol.Unknown;
        }

        if (here is not (HexLinkEol.Lf or HexLinkEol.Crlf) && local is null)
            here = Most(here: true);
        byte[] written = Converted(file.Bytes, info, here);

        try
        {
            await ApplyLocal(full, wire, expected, written, stop, file.Executable ?? theirs.Executable);
            tree.RememberMode(full, file.Executable ?? theirs.Executable);
        }
        catch (HexLinkException e) when (EditorDirty(e))
        {
            await HoldEditorConflict(wire, full, file.Bytes, "unsaved changes in the editor", stop);
            return;
        }

        lock (gate)
            SetBase(wire, AgreedOf(written, file.Bytes) with { Executable = file.Executable ?? theirs.Executable });

        Interlocked.Increment(ref received);
        Notify();
    }

    // This side's version sent over the other's (or its delete), with what the other side had (theirs) as the base, so
    // the other side refuses it if it moved on meanwhile
    private async Task Push(string wire, string full, HexLinkSyncAgreed? agreed, byte[]? bytes, string? expectedLocal, string? theirs, CancellationToken stop)
    {
        if (Holds?.IsHeld(wire) == true)
            return;
        if (!remoteCertified)
        {
            Skip(wire, "The peer does not support checked working-tree writes.");
            return;
        }
        int grantedAt;
        lock (gate)
        {
            if (!live || !ready)
                return;
            if (pullOnly || !HexLinkAllowed.Has(peer.Granted, HexLinkRights.Edit))
            {
                deferredPushes.Add(wire);
                return;
            }

            grantedAt = rightsVersion;
        }

        if (bytes is null && HexLinkTree.RegularFile(full, tree.Root) is not null)
        {
            Again(wire);
            return;
        }

        bool heldSubmission = IsSubmittedHold(wire, expectedLocal);
        try
        {
            if (bytes is null)
            {
                if (theirs is not null)
                {
                    try
                    {
                        await Asked(() => peer.Delete(wire, theirs, stop, heldSubmission), stop);
                    }
                    catch (HexLinkException e) when (e.Code == HexLinkErrors.NOT_FOUND)
                    {
                    }
                }

                lock (gate)
                {
                    if (remote.GetValueOrDefault(wire)?.Hash == theirs)
                        RemoveRemote(wire);
                    Agree(wire, null);
                }
            }
            else
            {
                if (bytes.Length > HexLinkFiles.MOST_READ)
                {
                    Skip(wire, $"it's over {HexLinkFiles.MOST_READ / 1024 / 1024} MB, a link doesn't carry it");
                    return;
                }

                TextInfo info = TextOf(bytes);
                HexLinkEol there = HexLinkEol.Unknown;
                if (info.Norm is not null && theirs is not null)
                {
                    // Only its line ends changed here since both agreed: nothing goes, this side's hash moves on
                    if (agreed?.Text is { } text && text == info.Norm)
                    {
                        lock (gate)
                            SetBase(wire, agreed with { Local = expectedLocal!, LocalEol = info.Eol });
                        Interlocked.Increment(ref eolOnly);
                        Notify();
                        return;
                    }

                    there = agreed?.RemoteEol ?? HexLinkEol.Unknown;
                    bool known = there is HexLinkEol.Lf or HexLinkEol.Crlf;
                    HexLinkTreeFile? said;
                    lock (gate)
                        said = remote.GetValueOrDefault(wire);
                    if (!known && said is { Text: { } saidText } && said.Hash == theirs)
                    {
                        known = true;
                        // What the other side said of its file (a peer that says its text and line ends): the same text
                        // is nothing to send, otherwise it goes in its line ends, or what most files there have
                        if (saidText == info.Norm)
                        {
                            lock (gate)
                                SetBase(wire, new HexLinkSyncAgreed(expectedLocal!, theirs, saidText, info.Eol, EolIn(said.Eol)));
                            Interlocked.Increment(ref eolOnly);
                            Notify();
                            return;
                        }

                        there = EolIn(said.Eol) is HexLinkEol.Lf or HexLinkEol.Crlf ? EolIn(said.Eol) : Most(here: false);
                    }

                    if (!known)
                    {
                        // What the other side has isn't known by its line ends (a base from before they were kept, or
                        // one agreed by hash alone), so it's read: the same but for line ends is nothing to send
                        HexLinkFile now = await Fetch(wire, stop);
                        if (now.Hash != theirs)
                        {
                            lock (gate)
                                There(wire, now.Hash);
                            Again(wire);
                            return;
                        }

                        if (SameText(bytes, now.Bytes))
                        {
                            lock (gate)
                                SetBase(wire, AgreedOf(bytes, now.Bytes));
                            Interlocked.Increment(ref eolOnly);
                            Notify();
                            return;
                        }

                        there = TextOf(now.Bytes).Eol;
                    }
                }
                else if (theirs is null && info.Norm is not null)
                {
                    // A new file there takes what most files there use
                    there = Most(here: false);
                }

                byte[] sending = Converted(bytes, info, there);
                await PushPacked(wire, theirs, sending, tree.Mode(full), stop, heldSubmission);
                lock (gate)
                    SetBase(wire, AgreedOf(bytes, sending) with { Executable = tree.Mode(full) });
            }

            if (heldSubmission)
                ForgetHeldSubmission(wire, expectedLocal);
            Interlocked.Increment(ref sent);
            Notify();
        }
        catch (HexLinkException e) when (EditorDirty(e))
        {
            await HoldEditorConflict(wire, full, null, "unsaved changes on the other device", stop);
        }
        catch (HexLinkException e) when (e.Code is HexLinkErrors.CONFLICT or HexLinkErrors.NOT_FOUND)
        {
            // A failed read or compare can be a lock or loss of access. Only the tree watcher can confirm deletion.
            Again(wire, e.Message);
        }
        catch (HexLinkException e) when (e.Code == HexLinkErrors.NOT_ALLOWED)
        {
            if (HexLinkData.Bool(e.Detail as JsonObject, "held") == true)
                RecordHeldOffer(wire, expectedLocal);
            else if (HexLinkData.String(e.Detail as JsonObject, "needs", 40) != HexLinkData.Word(HexLinkRights.Edit))
                Skip(wire, e.Message);
            else
                HoldDeniedPush(wire, grantedAt);
        }
    }

    private static bool EditorDirty(HexLinkException e) =>
        e.Code == HexLinkErrors.CONFLICT && e.Detail is JsonObject detail && HexLinkData.Bool(detail, "editor_dirty") == true;

    private async Task HoldEditorConflict(string wire, string full, byte[]? bytes, string kind, CancellationToken stop)
    {
        string? copy = null, hash = null;
        if (bytes is not null)
        {
            (string copyFull, copy) = Beside(full, wire);
            using var allowed = ReceiveLease(stop);
            hash = await HexLinkFiles.CompareAndReplace(copyFull, copy, null, bytes, allowed?.Token ?? stop);
        }
        lock (gate)
        {
            conflicts[wire] = new HexLinkSyncConflict(wire, kind, copy, hash);
            RemoveBase(wire);
        }
        Save();
        Notify();
    }

    // Both changed since they agreed: nothing overwritten, the other side's version put beside this one's, the path
    // held until somebody picks a side
    private async Task Conflict(string wire, string full, string? mine, string? theirs, CancellationToken stop)
    {
        string kind = mine is null ? "deleted here, changed there" : theirs is null ? "changed here, deleted there" : "changed on both";
        string? copy = null, copyHash = null;
        if (mine is not null && theirs is not null)
        {
            HexLinkFile file = await Fetch(wire, stop);
            byte[]? local = await ReadLocal(full, stop);
            if (file.Hash != theirs)
            {
                Again(wire);
                return;
            }

            if (local is null || HexLinkFiles.Hash(local) != mine)
            {
                Again(wire);
                return;
            }

            // The same, or the same but for line ends (a CRLF checkout against an LF one): agreed as each side has it
            if (SameText(local, file.Bytes))
            {
                lock (gate)
                    SetBase(wire, AgreedOf(local, file.Bytes));
                if (mine != file.Hash)
                    Interlocked.Increment(ref eolOnly);
                Notify();
                return;
            }

            (string copyFull, copy) = Beside(full, wire);
            using var allowed = ReceiveLease(stop);
            copyHash = await HexLinkFiles.CompareAndReplace(copyFull, copy, null, file.Bytes, allowed?.Token ?? stop);
        }

        lock (gate)
        {
            conflicts[wire] = new HexLinkSyncConflict(wire, kind, copy, copyHash);
            RemoveBase(wire);
        }

        Save();
        Notify();
    }

    // Where the other side's version of a file in conflict goes: name.theirs-device.ext beside it, numbered past any
    // copy already there, never over one
    private (string Full, string Wire) Beside(string full, string wire)
    {
        string directory = Path.GetDirectoryName(full)!, name = Path.GetFileName(full);
        string extension = Path.GetExtension(name), stem = Path.GetFileNameWithoutExtension(name);
        if (stem.Length == 0)
        {
            // .gitignore and the like, the whole name is the stem
            stem = name;
            extension = string.Empty;
        }

        string parent = wire[..wire.LastIndexOf('/')];
        for (int n = 1; ; n++)
        {
            string made = $"{stem}{THEIRS}{slug}{(n == 1 ? string.Empty : "-" + n)}{extension}";
            string at = Path.Combine(directory, made);
            if (!File.Exists(at) && !Directory.Exists(at))
                return (at, parent + "/" + made);
        }
    }

    private void HoldDeniedPush(string path, int grantedAt)
    {
        bool retry, changed = false;
        lock (gate)
        {
            if (!live || !ready)
                return;

            // A newer grant can arrive while the refused request is in flight.
            retry = grantedAt != rightsVersion && !pullOnly;
            if (!retry)
            {
                deferredPushes.Add(path);
                changed = !pullOnly;
                pullOnly = true;
                State = HexLinkSyncState.PullOnly;
                Why = $"{peer.Device.Name} doesn't let this device write there (Edit)";
            }
        }

        if (retry)
            Again(path);
        if (changed)
            Notify();
    }

    private void UpdateRights()
    {
        if ((BoundAuthorityEngine && !HexLinkAllowed.Has(peer.Granted, HexLinkRights.Edit)) ||
            (BoundPullMirror && !HexLinkAllowed.Has(peer.Granted, HexLinkRights.View)))
        {
            Stop();
            return;
        }
        lock (gate)
        {
            rightsVersion++;
            bool nowPullOnly = BoundPullMirror || !HexLinkAllowed.Has(peer.Granted, HexLinkRights.Edit);
            if (!live || pullOnly == nowPullOnly)
                return;
            pullOnly = nowPullOnly;
            if (!ready)
                return;

            State = pullOnly ? HexLinkSyncState.PullOnly : HexLinkSyncState.Syncing;
            Why = pullOnly ? $"{peer.Device.Name} doesn't let this device write there (Edit)" : null;
            if (!pullOnly)
            {
                // Recompare held paths against their bases before sending anything.
                foreach (string path in deferredPushes)
                    Dirty(path);
                deferredPushes.Clear();
            }
        }

        Notify();
        CheckIdle();
    }

    private void Skip(string wire, string why)
    {
        lock (gate)
        {
            if (skipped.Count >= MOST_SKIPPED && !skipped.ContainsKey(wire))
                return;
            skipped[wire] = new HexLinkSyncSkipped(wire, why);
        }

        Notify();
    }

    private void Unskip(string wire)
    {
        bool was;
        lock (gate)
            was = skipped.Remove(wire);
        if (was)
            Notify();
    }

    // When nothing's queued, being worked on or waiting to be tried again: whoever waits for that let go, and Syncing
    // becomes Idle
    private void CheckIdle()
    {
        TaskCompletionSource? waiting = null;
        bool idled = false;
        lock (gate)
        {
            if (queue.Count > 0 || running.Count > 0 || taking > 0 || retrying.Count > 0)
                return;
            waiting = drained;
            drained = null;
            stoppingWork?.TrySetResult();
            stoppingWork = null;
            ResetProgress();
            if (State == HexLinkSyncState.Syncing)
            {
                State = HexLinkSyncState.Idle;
                idled = true;
            }
        }

        waiting?.TrySetResult();
        if (idled)
        {
            Save();
            Notify();
        }
    }

    // Under the gate, once a comparison has drained or been stopped.
    private void ResetProgress()
    {
        passSizes = null;
        Volatile.Write(ref filesDone, 0);
        Volatile.Write(ref filesTotal, 0);
        Volatile.Write(ref bytesDone, 0);
        Volatile.Write(ref bytesTotal, 0);
        Volatile.Write(ref detail, null);
        Volatile.Write(ref retryDetail, null);
    }

    // Done when the queue is empty and nothing's being worked on
    private async Task Drained(CancellationToken cancel)
    {
        Task wait;
        lock (gate)
        {
            if (!live || (queue.Count == 0 && running.Count == 0 && taking == 0 && retrying.Count == 0))
                return;
            drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            wait = drained.Task;
        }

        await wait.WaitAsync(cancel);
    }
}
