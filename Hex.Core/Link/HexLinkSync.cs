namespace Hex.Core;

/// <summary>Where a folder's sync with another device is.</summary>
internal enum HexLinkSyncState
{
    /// <summary>Not started, or stopped (by the caller, or because the link went; <see cref="HexLinkSync.Why"/> says which).</summary>
    Off,

    /// <summary>The two trees being listed and compared before anything moves.</summary>
    Comparing,

    /// <summary>The two sides have different commits checked out, nothing is synced until the caller says go on anyway.</summary>
    NeedsGit,

    /// <summary>Files on their way one way or the other.</summary>
    Syncing,

    /// <summary>Both sides the same as far as is known, waiting for the next change.</summary>
    Idle,

    /// <summary>The other device doesn't let this one write there, so its changes come here and nothing goes back.</summary>
    PullOnly,

    /// <summary>It couldn't start or carry on, <see cref="HexLinkSync.Why"/> says why.</summary>
    Failed
}

/// <summary>Which side a conflict is settled for.</summary>
internal enum HexLinkSyncKeep
{
    /// <summary>This machine's version goes to the other.</summary>
    Mine,

    /// <summary>The other machine's version is written here.</summary>
    Theirs
}

/// <summary>
/// A file both sides changed since they last agreed on it, or one deleted on one side and changed on the other. Its
/// wire path, what happened in a few words, and where the other machine's version was put beside it (a wire path of
/// this side's, null when there was nothing to put, a delete). Nothing of it moves until it's resolved.
/// </summary>
internal sealed record HexLinkSyncConflict(string Path, string Kind, string? Copy, string? CopyHash)
{
    public bool HasUnsavedChanges => Kind.StartsWith("unsaved changes", StringComparison.Ordinal);
}

/// <summary>A file that doesn't travel and why: too big, a name this side can't take, a path the other side had no business sending.</summary>
internal sealed record HexLinkSyncSkipped(string Path, string Why);

/// <summary>
/// One workspace folder kept the same on this machine and another over a link, the working-tree sync of the plan's
/// section 5.5. One of these is made per device and folder by the side that turned sync on, and it's enough on its own:
/// the protocol is symmetric, so this side watches its own tree on the disk and the other's through
/// <see cref="HexLinkPeer.WatchTree"/>, sends with <see cref="HexLinkPeer.Write"/> and <see cref="HexLinkPeer.Delete"/>
/// and fetches with <see cref="HexLinkPeer.Read"/>. Both sides running one each comes to the same thing, because every
/// decision is made from hashes rather than from who said what last.
/// <para>
/// The idea it all hangs on is the base: for each file, the hash both sides last agreed it had, kept in a state file so
/// a restart picks up where it was. A file that differs is looked at with its base. Changed here and still at the base
/// there, it's sent with the base, and the other side refuses it as a conflict if that's no longer true. Changed there
/// and at the base here, it's fetched and written here. Changed on both, nothing is overwritten: the other machine's
/// version is put beside this one's as <c>name.theirs-device.ext</c> and the file waits in <see cref="Conflicts"/>
/// until somebody picks a side with <see cref="Resolve"/>. Because a file written here from the other side gets that
/// side's hash as its base before the watcher reports it, it isn't a change when it is reported, and what this side
/// sent comes back from the other side's watcher with the hash it already agreed: neither echoes.
/// </para>
/// <para>
/// A Windows checkout with core.autocrlf has CRLF where a Mac's of the same commit has LF, so the base is kept as each
/// side has it (<see cref="HexLinkSyncAgreed"/>): text that only differs in its line ends is agreed with nothing sent
/// (<see cref="EolOnly"/>), each side is compared with its own hash, what's sent is written in the receiver's line ends
/// (those of the file it replaces, or what most files there have for a new one), and a change of only line ends stays
/// on the side it was made. Binary files, and text with more than one kind of line end, go exactly as they are.
/// .gitattributes isn't read.
/// </para>
/// <para>
/// Everything the other side says is data. Its paths go through the same jail its own requests do
/// (<see cref="HexLinkFiles.Resolve"/>), plus what this side ignores, the conflict copies and the state file, so a
/// peer can never make this side write outside the folder, into .git, over something git ignores here or over the
/// state. Work goes through a queue a handful at a time and paced under the link's request limit, with each file's
/// changes coalesced and done in order, so a burst of a thousand files doesn't stall the link for everything else.
/// </para>
/// </summary>
internal sealed partial class HexLinkSync : IDisposable
{
    /// <summary>The most transfers in flight at once.</summary>
    public const int MOST_IN_FLIGHT = 4;

    private readonly HexLinkPeer peer;
    private readonly HexLinkFolder folder;
    private readonly HexLinkTree tree;
    private readonly string slug;

    // Everything below that more than one thread touches is kept under this
    private readonly Lock gate = new();

    private HexLinkTreeWatch? local;
    private CancellationTokenSource stopping = new();
    private bool live, remoteWatched, ready;
    private volatile bool pullOnly;
    private List<HexLinkTreeChange>? early;
    private bool earlyOverflow, remoteCertified, insensitivePaths;
    private int sent, received;

    public Func<HexExcludes> Excludes { get; init; } = () => HexExcludes.Usual;

    /// <summary>
    /// A folder of this machine's workspace to keep the same as the folder of the same name on <paramref name="peer"/>'s,
    /// the state kept in a file in <paramref name="stateFolder"/> (made when it isn't there) named by the device and the
    /// folder, so a sync with the same device started again later, over a new link, picks up from it.
    /// </summary>
    public HexLinkSync(HexLinkPeer peer, HexLinkFolder folder, string stateFolder)
    {
        this.peer = peer;
        this.folder = folder;
        tree = new HexLinkTree(folder, () => Excludes());
        slug = HexLinkGit.Slug(peer.Device.Name);
        StateFile = Path.GetFullPath(Path.Combine(stateFolder, StateName(peer.Device.Id, folder.Name)));
        ApplyLocal = HexLinkFiles.CompareAndReplace;
        RenameLocal = (from, to, expected, cancel) => Task.Run(() => HexLinkTree.RenameCase(from, to, expected), cancel);
    }

    /// <summary>The device synced with.</summary>
    public HexLinkDevice Device => peer.Device;

    /// <summary>The folder synced, by the name both sides know it by.</summary>
    public string Folder => folder.Name;

    internal HexLinkFolder LocalFolder => folder;

    /// <summary>Where the state (the bases and the open conflicts) is kept.</summary>
    public string StateFile { get; }

    /// <summary>Apply a checked incoming change, optionally through the editor's dirty-tab guard.</summary>
    public Func<string, string, string?, byte[]?, CancellationToken, bool?, Task<string?>> ApplyLocal { get; init; }

    public Func<string, string, string, CancellationToken, Task> RenameLocal { get; init; }

    /// <summary>How long this side's tree is quiet before a batch of its changes is looked at, 150 ms unless a test says.</summary>
    public TimeSpan Quiet { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>How often this side's tree is looked over whole in case its watcher dropped something, 15 seconds unless a test says.</summary>
    public TimeSpan Rescan { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Where it is.</summary>
    public HexLinkSyncState State { get; private set; } = HexLinkSyncState.Off;

    /// <summary>Why it's off, failed, pull-only or waiting on git, in a sentence; null when there's nothing to say.</summary>
    public string? Why { get; private set; }

    /// <summary>The commit checked out here and there, as of the last comparison (null outside a repository).</summary>
    public string? LocalHead { get; private set; }

    /// <summary>The commit checked out on the other side, as of the last comparison.</summary>
    public string? RemoteHead { get; private set; }

    /// <summary>Whether changes here stay here because the other side doesn't grant Edit.</summary>
    public bool PullOnly => pullOnly;

    /// <summary>Files written or deleted on the other side by this one so far.</summary>
    public int Sent => Volatile.Read(ref sent);

    /// <summary>Files written or deleted here from the other side so far.</summary>
    public int Received => Volatile.Read(ref received);

    /// <summary>The conflicts waiting for somebody to pick a side, by path.</summary>
    public IReadOnlyList<HexLinkSyncConflict> Conflicts
    {
        get
        {
            lock (gate)
                return [.. conflicts.Values.OrderBy(each => each.Path, StringComparer.Ordinal)];
        }
    }

    /// <summary>
    /// Files found the same on both sides but for their line ends (a CRLF checkout against an LF one), or changed on one
    /// side only in their line ends, agreed with nothing sent, each side keeping its own.
    /// </summary>
    public int EolOnly => Volatile.Read(ref eolOnly);

    /// <summary>Files read from the other side so far, to write here, to keep beside a conflict or to compare by their text.</summary>
    public int Reads => Volatile.Read(ref reads);

    private int reads;

    // A file of the other side's read, counted
    private Task<HexLinkFile> Fetch(string wire, CancellationToken cancel)
    {
        Interlocked.Increment(ref reads);
        return Asked(() => peer.Read(wire, cancel), cancel);
    }

    /// <summary>The files that don't travel and why, by path.</summary>
    public IReadOnlyList<HexLinkSyncSkipped> Skipped
    {
        get
        {
            lock (gate)
                return [.. skipped.Values.OrderBy(each => each.Path, StringComparer.Ordinal)];
        }
    }

    /// <summary>Called when anything shown of it moved (its state, a count, a conflict), on whatever thread it moved on.</summary>
    public Action? Changed { get; set; }

    public int FilesDone => Volatile.Read(ref filesDone);
    public int FilesTotal => Volatile.Read(ref filesTotal);
    public long BytesDone => Volatile.Read(ref bytesDone);
    public long BytesTotal => Volatile.Read(ref bytesTotal);
    public int Queued
    {
        get
        {
            lock (gate)
                return retrying.Count;
        }
    }
    public string? Detail => Volatile.Read(ref retryDetail) ?? Volatile.Read(ref detail);

    /// <summary>Where it is in a line, for a status item or a tool's answer.</summary>
    public string Summary
    {
        get
        {
            string with = $"{folder.Name} with {peer.Device.Name}";
            int conflicted, skips;
            lock (gate)
            {
                conflicted = conflicts.Count;
                skips = Math.Max(0, skipped.Count - retrying.Count);
            }

            string counts = $"{Sent} sent, {Received} received, {conflicted} {(conflicted == 1 ? "conflict" : "conflicts")}, {skips} skipped"
                + (EolOnly > 0 ? $", {EolOnly} only differing in line ends" : string.Empty);
            if (Queued is > 0 and var retryCount)
                counts += $", {retryCount} retrying";
            return State switch
            {
                HexLinkSyncState.Comparing => $"Comparing {with}",
                HexLinkSyncState.NeedsGit => $"{with} isn't synced, {Why}",
                HexLinkSyncState.Syncing => $"Syncing {with}: {counts}",
                HexLinkSyncState.Idle => $"{with} in sync: {counts}",
                HexLinkSyncState.PullOnly => $"{with} pull-only ({Why}): {counts}",
                HexLinkSyncState.Failed => $"Sync of {with} failed, {Why}",
                _ => Why is null ? $"Sync of {with} is off" : $"Sync of {with} is off, {Why}"
            };
        }
    }

    /// <summary>
    /// Starts it: the two trees listed and matched (the same folder name, and the same repository by its first commit
    /// where both are one), then compared with the bases kept from last time, what differs sorted out, and both trees
    /// watched from then on. When the two sides have different commits checked out it stops at
    /// <see cref="HexLinkSyncState.NeedsGit"/> and syncs nothing, unless <paramref name="force"/> says go on anyway (the
    /// person brought them together, or chose not to). Without Edit granted there it runs pull-only and says so. Never
    /// throws for what the other side or the disk does, <see cref="State"/> and <see cref="Why"/> say.
    /// </summary>
    public async Task Start(bool force = false, CancellationToken cancel = default, bool automatic = false)
    {
        lock (gate)
        {
            if (live)
                return;
            live = true;
            ready = false;
            peer.RightsChanged += UpdateRights;
            pullOnly = !HexLinkAllowed.Has(peer.Granted, HexLinkRights.Edit);
            early = [];
            earlyOverflow = false;
            stopping = new CancellationTokenSource();
        }

        Set(HexLinkSyncState.Comparing, null);
        HexLinkTreeWatch? watch = null;
        try
        {
            if (peer.IsClosed)
            {
                Fail("the link is closed");
                return;
            }

            if (!HexLinkAllowed.Has(peer.Granted, HexLinkRights.View))
            {
                Fail($"{peer.Device.Name} doesn't let this device see its workspace (View)");
                return;
            }

            await peer.RefreshWorkspace(cancel);
            if (!peer.Workspace.Folders.Any(each => PlatformPaths.Comparer.Equals(each.Name, folder.Name)))
            {
                Fail($"{peer.Device.Name} has no folder called {folder.Name} open");
                return;
            }

            peer.Session.Closed += LinkClosed;

            HexLinkManifest theirs = await peer.WatchTree(folder.Name, Heard, cancel);
            remoteWatched = true;
            watch = new HexLinkTreeWatch(tree, Saw, Quiet, Rescan);
            HexLinkManifest mine = await Task.Run(tree.Manifest, cancel);
            LocalHead = mine.Head;
            RemoteHead = theirs.Head;

            if (Mismatch(mine, theirs) is { } why)
            {
                await Unwatch();
                Fail(why);
                return;
            }

            if (automatic && mine.Root is null && mine.NestedHeads is not { Count: > 0 })
            {
                await Unwatch();
                Dispose();
                return;
            }

            missingRepositories = HexLinkMissingRepositories.Compare(mine, theirs);
            if (!force && HexLinkTree.CommitsDiffer(mine, theirs, missingRepositories) is { } difference)
            {
                await Unwatch();
                lock (gate)
                {
                    live = false;
                    peer.RightsChanged -= UpdateRights;
                }
                peer.Session.Closed -= LinkClosed;
                Set(HexLinkSyncState.NeedsGit,
                    $"{folder.Name} differs from {peer.Device.Name}: {difference}. Bring them to the same commits with git first or go on anyway.");
                return;
            }

            await Task.Run(() => Load(mine, theirs, cancel), cancel);
            Begin(mine, theirs, watch);
            watch = null;
        }
        catch (Exception e) when (e is HexLinkException or IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException)
        {
            await Unwatch();
            Fail(e is HexLinkException { Code: HexLinkErrors.CLOSED } ? "the link closed" : e.Message);
        }
        finally
        {
            watch?.Dispose();
        }
    }

    // Two trees that aren't the same folder of the same repository, said why; null when they match
    private string? Mismatch(HexLinkManifest mine, HexLinkManifest theirs)
    {
        if (mine.Root is not null && theirs.Root is not null && !mine.Root.Equals(theirs.Root, StringComparison.OrdinalIgnoreCase))
            return $"{folder.Name} here and on {peer.Device.Name} are different repositories, their first commits differ";
        if ((mine.Root is null) != (theirs.Root is null))
            return $"{folder.Name} is a git repository with commits on {(mine.Root is null ? peer.Device.Name : "this device")} and isn't on {(mine.Root is null ? "this device" : peer.Device.Name)}";
        return null;
    }

    /// <summary>
    /// Done when everything that changed here before it was called is on the other side: this side's tree looked over
    /// whole, the other side's changes said, and the queue drained. What "now build it over there" waits for. Throws
    /// <see cref="InvalidOperationException"/> when it isn't running.
    /// </summary>
    public async Task Flush(CancellationToken cancel = default)
    {
        HexLinkTreeWatch? watch;
        lock (gate)
        {
            if (!live || !ready)
                throw new InvalidOperationException(Summary);
            watch = local;
        }

        if (watch is not null)
            await watch.Flush().WaitAsync(cancel);
        await peer.Flush(folder.Name, cancel);

        await Drained(cancel);
        if (State is HexLinkSyncState.Off or HexLinkSyncState.Failed)
            throw new InvalidOperationException(Summary);
    }

    /// <summary>
    /// A conflict settled: <see cref="HexLinkSyncKeep.Mine"/> sends this side's version (or its delete) over the other's,
    /// <see cref="HexLinkSyncKeep.Theirs"/> writes the other's (or its delete) here. The copy put beside it goes when
    /// it's still what was put there. Null when it's done, why when it isn't (not a conflict, pull-only, the other side
    /// moved again).
    /// </summary>
    public async Task<string?> Resolve(string path, HexLinkSyncKeep keep, CancellationToken cancel = default)
    {
        HexLinkSyncConflict? conflict;
        HexLinkTreeFile? theirs;
        lock (gate)
        {
            if (!live || !ready)
                return Summary;
            if (insensitivePaths && (hereNames.Collides(path) || thereNames.Collides(path) ||
                (hereNames.Name(path) is { } here && here != path) || (thereNames.Name(path) is { } there && there != path)))
                return "The path collides with another spelling on this link.";
            conflicts.TryGetValue(path, out conflict);
            remote.TryGetValue(path, out theirs);
        }

        if (theirs?.Unavailable == true)
            return "The other device cannot read this file yet.";
        if (conflict is null)
            return $"{path} isn't in conflict.";
        if (keep == HexLinkSyncKeep.Mine && !remoteCertified)
            return "The peer does not support checked working-tree writes.";
        if (keep == HexLinkSyncKeep.Mine && pullOnly)
            return $"{peer.Device.Name} doesn't let this device write there, only theirs can be kept.";

        string full;
        try
        {
            full = Jailed(path);
        }
        catch (HexLinkException e)
        {
            return e.Message;
        }

        try
        {
            // What both sides have once it's done, null when neither has it; whichever way it goes it's written in the
            // receiver's line ends, those of the file it replaces there or what most files there use
            HexLinkSyncAgreed? agreed;
            byte[]? mine = await ReadLocal(full, cancel);
            if (keep == HexLinkSyncKeep.Mine)
            {
                if (mine is null)
                {
                    if (theirs?.Hash is { } there)
                        await Asked(() => peer.Delete(path, there, cancel), cancel);
                    agreed = null;
                }
                else
                {
                    if (mine.Length > HexLinkFiles.MOST_READ)
                        return $"{path} is over {HexLinkFiles.MOST_READ / 1024 / 1024} MB, a link doesn't carry it.";
                    TextInfo info = TextOf(mine);
                    HexLinkEol eol = HexLinkEol.Unknown;
                    if (info.Norm is not null)
                        eol = theirs?.Hash is not null ? TextOf((await Fetch(path, cancel)).Bytes).Eol : Most(here: false);
                    byte[] sending = Converted(mine, info, eol);
                    await Asked(() => peer.Write(path, theirs?.Hash, sending, cancel), cancel);
                    agreed = AgreedOf(mine, sending);
                }

                Interlocked.Increment(ref sent);
            }
            else
            {
                if (theirs is null)
                {
                    if (!remoteCertified || !await Asked(() => peer.ConfirmAbsent(folder.Name, path, cancel), cancel))
                        return "The peer cannot confirm a working-tree deletion.";
                    if (mine is not null)
                        await ApplyLocal(full, path, HexLinkFiles.Hash(mine), null, cancel, null);
                    agreed = null;
                }
                else
                {
                    HexLinkFile file = await Fetch(path, cancel);
                    HexLinkEol eol = mine is null ? Most(here: true) : TextOf(mine).Eol;
                    byte[] written = Converted(file.Bytes, TextOf(file.Bytes), eol);
                    await ApplyLocal(full, path, mine is null ? null : HexLinkFiles.Hash(mine), written, cancel, null);
                    agreed = AgreedOf(written, file.Bytes);
                }

                Interlocked.Increment(ref received);
            }

            lock (gate)
            {
                conflicts.Remove(path);
                if (agreed is null)
                    Agree(path, null);
                else
                    SetBase(path, agreed);
            }

            await DropCopy(conflict, cancel);
            Save();
            Notify();
            return null;
        }
        catch (HexLinkException e) when (e.Code == HexLinkErrors.CONFLICT)
        {
            return e.Message;
        }
        catch (HexLinkException e)
        {
            return e.Message;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }

    // The copy put beside a file in conflict deleted, when it's still what was put there
    private async Task DropCopy(HexLinkSyncConflict conflict, CancellationToken cancel)
    {
        if (conflict.Copy is null || conflict.CopyHash is null)
            return;
        try
        {
            // A copy is the sync's own name, which Jailed refuses, so only the jail itself and the name are checked
            string full = HexLinkFiles.Resolve([folder], conflict.Copy);
            if (Path.GetFileName(full).Contains(THEIRS, StringComparison.OrdinalIgnoreCase))
                await ApplyLocal(full, conflict.Copy, conflict.CopyHash, null, cancel, null);
        }
        catch (Exception e) when (e is HexLinkException or IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Stops it: both trees no longer watched, the state kept, nothing more moves. It can be started again.</summary>
    public void Stop() => Halt(HexLinkSyncState.Off, null);

    private void LinkClosed(HexLinkSession session, string why) => Halt(HexLinkSyncState.Off, $"the link to {peer.Device.Name} closed, {why}");

    private void Fail(string why) => Halt(HexLinkSyncState.Failed, why);

    // Everything let go of and the state saved, said as it ended
    private void Halt(HexLinkSyncState state, string? why)
    {
        HexLinkTreeWatch? watch;
        TaskCompletionSource? waiting;
        bool was;
        lock (gate)
        {
            was = live && ready;
            live = false;
            ready = false;
            watch = local;
            local = null;
            waiting = drained;
            drained = null;
            peer.RightsChanged -= UpdateRights;
            queue.Clear();
            queued.Clear();
            again.Clear();
            retrying.Clear();
            ResetProgress();
            deferredPushes.Clear();
        }

        try { stopping.Cancel(); } catch (ObjectDisposedException) { }
        peer.Session.Closed -= LinkClosed;
        watch?.Dispose();
        stoppedWatching = Unwatch();
        waiting?.TrySetResult();
        if (was)
            Save();
        Set(state, why);
    }

    // The other side's tree no longer watched, when it was and the link's still there
    private async Task Unwatch()
    {
        if (!remoteWatched)
            return;
        remoteWatched = false;
        if (peer.IsClosed)
            return;
        try
        {
            await peer.UnwatchTree(folder.Name);
        }
        catch (HexLinkException)
        {
        }
    }

    private void Set(HexLinkSyncState state, string? why)
    {
        lock (gate)
        {
            State = state;
            Why = why;
        }

        Notify();
    }

    private void Notify()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception)
        {
            // Whoever's drawing it gets it right next time
        }
    }

    public void Dispose() => Stop();
}
