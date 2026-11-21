using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkSync
{
    // How many paths the queue hands over at once, asking git which of them it ignores in one go
    private const int MOST_BATCH = 64;

    // How many times in a row a file is looked at again because it moved while it was being synced, before it's left
    // until it next changes
    private const int MOST_TRIES = 6;

    // The most skipped files listed
    private const int MOST_SKIPPED = 2000;

    // Per wire path: what both sides last agreed on (each side's hash of it, which differ when only their line ends do),
    // what the other side has now as far as it's said (a file whose hash is null is too big to hash), the conflicts
    // waiting and what doesn't travel
    private readonly Dictionary<string, HexLinkSyncAgreed> bases = new(StringComparer.Ordinal);
    private int eolOnly;
    private readonly Dictionary<string, HexLinkTreeFile> remote = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HexLinkSyncConflict> conflicts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HexLinkSyncSkipped> skipped = new(StringComparer.Ordinal);

    // The queue: paths waiting in the order they changed, those being worked on, those that changed again while they
    // were (looked at again once they're done, so a path is only ever worked on once at a time and in order), and how
    // many times each has been tried again in a row
    private readonly Queue<string> queue = new();
    private readonly HashSet<string> queued = new(StringComparer.Ordinal), running = new(StringComparer.Ordinal), again = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> tries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim wake = new(0), slots = new(MOST_IN_FLIGHT, MOST_IN_FLIGHT);
    private int taking, delayed;
    private TaskCompletionSource? drained;

    // The first comparison done: what differs queued, both trees watched from now on
    private void Begin(HexLinkManifest mine, HexLinkManifest theirs)
    {
        var here = new Dictionary<string, HexLinkTreeFile>(StringComparer.Ordinal);
        foreach (HexLinkTreeFile file in mine.Files)
        {
            if (!Ours(file.Path))
                here[file.Path] = file;
        }

        var watch = new HexLinkTreeWatch(tree, mine, Saw, Quiet, Rescan);
        var differ = new List<string>();
        CancellationToken stop;
        lock (gate)
        {
            stop = stopping.Token;
            remote.Clear();
            foreach (HexLinkTreeFile file in theirs.Files)
            {
                if (!Ours(file.Path))
                    remote[file.Path] = file;
            }

            // What the other side said changed while the two were being listed, on top of its manifest
            foreach (HexLinkTreeChange change in early ?? [])
            {
                if (!Ours(change.Path))
                    There(change);
            }

            early = null;
            var paths = new HashSet<string>(here.Keys, StringComparer.Ordinal);
            paths.UnionWith(remote.Keys);
            paths.UnionWith(bases.Keys);
            paths.UnionWith(conflicts.Keys);
            foreach (string path in paths)
            {
                HexLinkTreeFile? mineFile = here.GetValueOrDefault(path), theirsFile = remote.GetValueOrDefault(path);
                string? m = mineFile?.Hash, t = theirsFile?.Hash;
                bool mineThere = mineFile is not null, theirsThere = theirsFile is not null;
                if (m is not null && bases.TryGetValue(path, out HexLinkSyncAgreed? agreed) && agreed.Local == m && agreed.Remote == t)
                {
                    // Where both were left, line ends and all
                }
                else if (m is not null && m == t)
                {
                    // The same on both, agreed whatever was known before, a conflict settled by hand on both sides with it
                    if (mineFile!.Text is { } same && mineFile.Eol is not null)
                        SetBase(path, new HexLinkSyncAgreed(m, m, same, EolIn(mineFile.Eol), EolIn(mineFile.Eol)));
                    else
                        Agree(path, m);
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
                    RemoveBase(path);
                    conflicts.Remove(path);
                }
                else
                {
                    differ.Add(path);
                }
            }

            local = watch;
            ready = true;
            State = pullOnly ? HexLinkSyncState.PullOnly : HexLinkSyncState.Syncing;
            Why = pullOnly ? $"{peer.Device.Name} doesn't let this device write there (Edit)" : null;
        }

        Notify();
        _ = Task.Run(() => Work(stop));
        foreach (string path in differ)
            Dirty(path);
        CheckIdle();
        Save();
    }

    // This side's tree changed (its watcher's thread): what isn't settled already is queued
    private void Saw(IReadOnlyList<HexLinkTreeChange> changes)
    {
        var dirty = new List<string>();
        lock (gate)
        {
            if (!live || !ready)
                return;
            foreach (HexLinkTreeChange change in changes)
            {
                if (Ours(change.Path))
                    continue;
                bases.TryGetValue(change.Path, out HexLinkSyncAgreed? agreed);
                remote.TryGetValue(change.Path, out HexLinkTreeFile? theirs);
                // A file written here from the other side, reported back by the watcher with the hash it was agreed at
                bool settled = change.Deleted
                    ? agreed is null && theirs is null
                    : change.Hash is not null && agreed is not null && change.Hash == agreed.Local && theirs?.Hash == agreed.Remote;
                if (!settled)
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
        var dirty = new List<string>();
        lock (gate)
        {
            if (!live)
                return;
            if (!ready)
            {
                early?.AddRange(changes.Take(100_000 - early.Count));
                return;
            }

            foreach (HexLinkTreeChange change in changes)
            {
                if (Ours(change.Path))
                    continue;
                There(change);
                bases.TryGetValue(change.Path, out HexLinkSyncAgreed? agreed);
                bool settled = change.Deleted ? agreed is null : change.Hash is not null && change.Hash == agreed?.Remote;
                if (!settled)
                    dirty.Add(change.Path);
            }
        }

        foreach (string path in dirty)
            Dirty(path);
    }

    // What the other side has of a path now, noted (under the gate)
    private void There(HexLinkTreeChange change)
    {
        if (change.Deleted)
            remote.Remove(change.Path);
        else
            remote[change.Path] = new HexLinkTreeFile(change.Path, change.Hash, 0, change.Text, change.Eol);
    }

    private void There(string path, string? hash)
    {
        if (hash is null)
            remote.Remove(path);
        else
            remote[path] = new HexLinkTreeFile(path, hash, 0);
    }

    // Both sides have a path with the same bytes (or neither has it), the base from now on (under the gate)
    private void Agree(string path, string? hash)
    {
        if (hash is null)
        {
            RemoveBase(path);
            There(path, null);
            tries.Remove(path);
        }
        else if (!bases.TryGetValue(path, out HexLinkSyncAgreed? was) || was.Local != hash || was.Remote != hash)
        {
            SetBase(path, Identical(hash));
        }
    }

    // A path queued to be looked at, after whatever's already being done with it
    private void Dirty(string wire)
    {
        bool woke = false, busy = false;
        lock (gate)
        {
            if (!live || !ready)
                return;
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

    // A path looked at again in a moment, because it moved while it was being synced; left until it next changes when
    // that keeps happening
    private void Again(string wire)
    {
        int count;
        lock (gate)
        {
            count = tries[wire] = tries.GetValueOrDefault(wire) + 1;
            if (count > MOST_TRIES)
                tries.Remove(wire);
            else
                delayed++;
        }

        if (count > MOST_TRIES)
        {
            Skip(wire, "it kept changing while it was being synced, it's tried again when it next changes");
            return;
        }

        _ = Task.Delay(50 * count * count).ContinueWith(_ =>
        {
            Dirty(wire);
            lock (gate)
                delayed--;
            CheckIdle();
        }, TaskScheduler.Default);
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
                    while (batch.Count < MOST_BATCH && queue.TryDequeue(out string? wire))
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
                    HashSet<string> ignored = await Task.Run(() => IgnoredOf(batch), stop);
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
        var ignored = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (string each in tree.Ignored(relative))
                ignored.Add(start + each);
        }
        catch (Exception)
        {
            // Git not answering, the jail still keeps out what has to be kept out
        }

        return ignored;
    }

    // One path worked on, whatever happens the queue going on after it
    private async Task Run(string wire, bool ignored, CancellationToken stop)
    {
        try
        {
            await Reconcile(wire, ignored, stop);
        }
        catch (OperationCanceledException)
        {
        }
        catch (HexLinkException e) when (e.Code == HexLinkErrors.CLOSED)
        {
        }
        catch (HexLinkException e) when (e.Code == HexLinkErrors.TIMED_OUT)
        {
            Again(wire);
        }
        catch (Exception e)
        {
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

        // This side's file hashed from what's on the disk now, never a hash cached by its length and time written, which
        // two saves within one tick of the clock can share
        HexLinkTreeFile? mine = null;
        try
        {
            var info = new FileInfo(full);
            if (info.Exists && info.Length > HexLinkFiles.MOST_READ)
            {
                mine = new HexLinkTreeFile(wire, null, info.Length);
            }
            else if (info.Exists)
            {
                byte[] bytes = await File.ReadAllBytesAsync(full, stop);
                TextInfo text = TextOf(bytes);
                mine = new HexLinkTreeFile(wire, HexLinkFiles.Hash(bytes), info.Length, text.Norm, EolWord(text.Eol));
            }
        }
        catch (IOException)
        {
            // Being written, looked at again in a moment
            Again(wire);
            return;
        }
        HexLinkTreeFile? theirs;
        HexLinkSyncAgreed? agreed;
        bool held;
        lock (gate)
        {
            remote.TryGetValue(wire, out theirs);
            bases.TryGetValue(wire, out agreed);
            held = conflicts.ContainsKey(wire);
        }

        if (mine is { Big: true } || theirs is { Big: true })
        {
            Skip(wire, $"it's over {HexLinkFiles.MOST_READ / 1024 / 1024} MB, a link doesn't carry it");
            return;
        }

        Unskip(wire);
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
        if (held)
            return;

        // Each side is looked at against its own agreed hash: only one moved, that one goes to the other
        if (agreed is not null)
        {
            if (m == agreed.Local)
            {
                await Pull(wire, full, m, stop);
                return;
            }

            if (t == agreed.Remote)
            {
                await Push(wire, full, agreed, t, stop);
                return;
            }
        }
        else if (m is null)
        {
            await Pull(wire, full, null, stop);
            return;
        }
        else if (t is null)
        {
            await Push(wire, full, null, null, stop);
            return;
        }

        await Conflict(wire, full, m, t, stop);
    }

    // The other side's version written here (or its delete carried out), when this side's is still what it was
    // (expected) by the time it's written
    private async Task Pull(string wire, string full, string? expected, CancellationToken stop)
    {
        HexLinkTreeFile? theirs;
        lock (gate)
            remote.TryGetValue(wire, out theirs);

        if (theirs is null)
        {
            if (HexLinkFiles.HashOf(full) != expected)
            {
                Again(wire);
                return;
            }

            if (expected is not null)
                await DeleteLocal(full);
            lock (gate)
                Agree(wire, null);
            Interlocked.Increment(ref received);
            Notify();
            return;
        }

        HexLinkFile file;
        try
        {
            file = await Fetch(wire, stop);
        }
        catch (HexLinkException e) when (e.Code == HexLinkErrors.NOT_FOUND)
        {
            lock (gate)
                There(wire, null);
            Again(wire);
            return;
        }

        byte[]? local;
        try
        {
            local = File.Exists(full) ? await File.ReadAllBytesAsync(full, stop) : null;
        }
        catch (IOException)
        {
            Again(wire);
            return;
        }

        string? now = local is null ? null : HexLinkFiles.Hash(local);
        if (now != expected)
        {
            // Changed here while it was being fetched, looked at again with both as they are now
            lock (gate)
                There(wire, file.Hash);
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

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        // Agreed before it's written, so this side's watcher reporting the write finds it settled and doesn't send it back
        HexLinkSyncAgreed? before;
        lock (gate)
        {
            before = bases.GetValueOrDefault(wire);
            SetBase(wire, AgreedOf(written, file.Bytes));
        }

        try
        {
            await WriteLocal(full, written);
        }
        catch (Exception)
        {
            lock (gate)
            {
                if (before is null)
                    RemoveBase(wire);
                else
                    SetBase(wire, before);
                There(wire, file.Hash);
            }

            throw;
        }

        Interlocked.Increment(ref received);
        Notify();
    }

    // This side's version sent over the other's (or its delete), with what the other side had (theirs) as the base, so
    // the other side refuses it if it moved on meanwhile
    private async Task Push(string wire, string full, HexLinkSyncAgreed? agreed, string? theirs, CancellationToken stop)
    {
        if (pullOnly)
            return;
        if (!HexLinkAllowed.Has(peer.Granted, HexLinkRights.Edit))
        {
            GoPullOnly(null);
            return;
        }

        byte[]? bytes;
        try
        {
            bytes = File.Exists(full) ? await File.ReadAllBytesAsync(full, stop) : null;
        }
        catch (IOException)
        {
            // Still being written, most likely
            Again(wire);
            return;
        }

        try
        {
            if (bytes is null)
            {
                if (theirs is not null)
                {
                    try
                    {
                        await Asked(() => peer.Delete(wire, theirs, stop), stop);
                    }
                    catch (HexLinkException e) when (e.Code == HexLinkErrors.NOT_FOUND)
                    {
                    }
                }

                lock (gate)
                    Agree(wire, null);
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
                            SetBase(wire, agreed with { Local = HexLinkFiles.Hash(bytes), LocalEol = info.Eol });
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
                                SetBase(wire, new HexLinkSyncAgreed(HexLinkFiles.Hash(bytes), theirs, saidText, info.Eol, EolIn(said.Eol)));
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
                await Asked(() => peer.Write(wire, theirs, sending, stop), stop);
                lock (gate)
                    SetBase(wire, AgreedOf(bytes, sending));
            }

            Interlocked.Increment(ref sent);
            Notify();
        }
        catch (HexLinkException e) when (e.Code == HexLinkErrors.CONFLICT)
        {
            // It moved on over there, looked at again with what it has now
            lock (gate)
                There(wire, HexLinkPeer.ConflictHash(e));
            Again(wire);
        }
        catch (HexLinkException e) when (e.Code == HexLinkErrors.NOT_FOUND)
        {
            // Gone there while its line ends were being looked at
            lock (gate)
                There(wire, null);
            Again(wire);
        }
        catch (HexLinkException e) when (e.Code == HexLinkErrors.NOT_ALLOWED)
        {
            if (HexLinkData.String(e.Detail as JsonObject, "needs", 40) == HexLinkData.Word(HexLinkRights.Edit))
                GoPullOnly(null);
            else
                Skip(wire, e.Message);
        }
    }

    // Both changed since they agreed: nothing overwritten, the other side's version put beside this one's, the path
    // held until somebody picks a side
    private async Task Conflict(string wire, string full, string? mine, string? theirs, CancellationToken stop)
    {
        string kind = mine is null ? "deleted here, changed there" : theirs is null ? "changed here, deleted there" : "changed on both";
        string? copy = null, copyHash = null;
        if (mine is not null && theirs is not null)
        {
            HexLinkFile file;
            try
            {
                file = await Fetch(wire, stop);
            }
            catch (HexLinkException e) when (e.Code == HexLinkErrors.NOT_FOUND)
            {
                lock (gate)
                    There(wire, null);
                Again(wire);
                return;
            }

            byte[]? local;
            try
            {
                local = File.Exists(full) ? await File.ReadAllBytesAsync(full, stop) : null;
            }
            catch (IOException)
            {
                local = null;
            }

            if (local is null || HexLinkFiles.Hash(local) != mine)
            {
                lock (gate)
                    There(wire, file.Hash);
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
            HexLinkFiles.WriteAtomic(copyFull, file.Bytes);
            copyHash = file.Hash;
            lock (gate)
                There(wire, file.Hash);
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

    private void GoPullOnly(string? why)
    {
        pullOnly = true;
        Set(HexLinkSyncState.PullOnly, why ?? $"{peer.Device.Name} doesn't let this device write there (Edit)");
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
            if (queue.Count > 0 || running.Count > 0 || taking > 0 || delayed > 0)
                return;
            waiting = drained;
            drained = null;
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

    // Done when the queue is empty and nothing's being worked on
    private async Task Drained(CancellationToken cancel)
    {
        Task wait;
        lock (gate)
        {
            if (!live || (queue.Count == 0 && running.Count == 0 && taking == 0 && delayed == 0))
                return;
            drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            wait = drained.Task;
        }

        await wait.WaitAsync(cancel);
    }
}
