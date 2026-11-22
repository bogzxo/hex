namespace Hex.Core;

internal sealed partial class HexLinkSync
{
    public int Held => Holds?.Count ?? 0;
    private HashSet<string> holdPaths = new(PlatformPaths.Comparer);
    private readonly HashSet<string> remoteHeld = new(PlatformPaths.Comparer);
    private readonly Dictionary<string, string?> offeredWhileHeld = new(PlatformPaths.Comparer);
    private readonly Dictionary<string, string?> remoteSeenWhileHeld = new(PlatformPaths.Comparer);

    // Called under the sync gate.
    private void NoteRemoteHold(string wire, bool held)
    {
        if (held)
            remoteHeld.Add(wire);
        else
        {
            remoteHeld.Remove(wire);
            offeredWhileHeld.Remove(wire);
        }
    }

    private async Task<bool> ReconcilePeerHold(string wire, string full, HexLinkTreeFile? mine, HexLinkTreeFile? theirs,
        HexLinkSyncAgreed? agreed, byte[]? localBytes, CancellationToken stop)
    {
        lock (gate)
        {
            if (!remoteHeld.Contains(wire))
                return false;
            if (pullOnly || !HexLinkAllowed.Has(peer.Granted, HexLinkRights.Edit) || !remoteCertified)
                return true;
            if (offeredWhileHeld.TryGetValue(wire, out string? offered))
            {
                if (mine?.Hash == offered)
                    return true;
            }
            else if (mine?.Hash == theirs?.Hash || agreed is not null && mine?.Hash == agreed.Local
                || mine?.Text is { } text && text == theirs?.Text)
                return true;
        }

        try
        {
            if (mine is null)
                await peer.Delete(wire, theirs?.Hash ?? HexLinkFiles.Hash([]), stop);
            else if (mine.Big)
                return true;
            else if (localBytes is null)
                await peer.WriteStreamedFile(new(wire, mine.Size, mine.Hash!, theirs?.Hash, mine.Executable), full, stop);
            else
                await PushPacked(wire, theirs?.Hash, localBytes, mine.Executable, stop);
        }
        catch (HexLinkException error) when (HexLinkData.Bool(error.Detail as System.Text.Json.Nodes.JsonObject, "held") == true)
        {
            RecordHeldOffer(wire, mine?.Hash);
            return true;
        }

        // The remote released its hold before this write arrived. Reconcile using its ordinary tree again.
        lock (gate)
            NoteRemoteHold(wire, false);
        Dirty(wire);
        return true;
    }

    private void RecordHeldOffer(string wire, string? hash)
    {
        lock (gate)
        {
            NoteRemoteHold(wire, true);
            offeredWhileHeld[wire] = hash;
        }
        Notify();
    }

    private void WatchHolds()
    {
        if (Holds is null)
            return;
        Holds.Changed += HoldsMoved;
        holdPaths = new HashSet<string>(Holds.List().Select(entry => entry.Path), PlatformPaths.Comparer);
    }

    private void HoldsMoved()
    {
        if (Holds is null)
            return;
        string[] released;
        lock (gate)
        {
            var current = new HashSet<string>(Holds.List().Select(entry => entry.Path), PlatformPaths.Comparer);
            released = [.. holdPaths.Except(current, PlatformPaths.Comparer)];
            foreach (string wire in released)
                remoteSeenWhileHeld.Remove(wire);
            holdPaths = current;
        }
        foreach (string wire in released)
            Dirty(wire);
        WakeGitFollow();
        Notify();
    }

    private IEnumerable<HexLinkSyncSkipped> HoldSkips()
    {
        if (Holds is not null)
            foreach (HexLinkHold entry in Holds.List())
                yield return new(entry.Path, entry.HasIncoming ? "Held on this device, others changed it" : "Held on this device");
        foreach (string wire in remoteHeld)
            yield return new(wire, $"Held on {Device.Name}");
    }

    private async Task<bool> ReconcileHold(string wire, CancellationToken stop)
    {
        if (Holds is not { } store || store.Get(wire) is not { } hold)
            return false;

        HexLinkTreeFile? theirs;
        HexLinkSyncAgreed? agreed;
        bool seen;
        lock (gate)
        {
            theirs = remote.GetValueOrDefault(wire);
            agreed = bases.GetValueOrDefault(wire);
            if (remoteHeld.Contains(wire))
                return true;
            seen = remoteSeenWhileHeld.TryGetValue(wire, out string? last);
            if (seen && theirs?.Hash == last && theirs is not ({ Unavailable: true } or { Big: true }))
                return true;
        }
        if (!CanReceive || theirs is { Unavailable: true } or { Big: true })
            return true;

        string? baseline = agreed?.Remote ?? (hold.BaseDeleted ? null : hold.BaseHash);
        string? hash = theirs?.Hash;
        bool changed = hash != baseline || seen;
        if (!changed || (hash == hold.IncomingHash && (hash is not null || hold.IncomingDeleted)))
        {
            RememberHeldRemote(wire, hash);
            return true;
        }

        if (theirs is null)
        {
            if (remoteCertified && await Asked(() => peer.ConfirmAbsent(folder.Name, wire, stop), stop))
            {
                store.SetIncomingDeleted(wire, Device.Name);
                RememberHeldRemote(wire, hash);
            }
            return true;
        }

        if (theirs.Size > HexLinkFiles.MOST_READ)
        {
            var transfer = new HexLinkFileTransfer(wire, theirs.Size, theirs.Hash!, Executable: theirs.Executable);
            string partial = transfer.PartialPath(Path.Combine(Path.GetDirectoryName(StateFile)!, "transfers"), tree.Root + "\n" + peer.Device.Id);
            await peer.ReadStreamedFile(transfer, partial, stop);
            try
            {
                store.SetIncomingStreamed(wire, partial, transfer.Hash, Device.Name);
                RememberHeldRemote(wire, hash);
            }
            finally { HexLinkGit.TryDelete(partial); }
            return true;
        }

        HexLinkFile incoming = await Fetch(wire, stop);
        if (incoming.Hash == theirs.Hash)
        {
            store.SetIncoming(wire, incoming.Bytes, Device.Name);
            RememberHeldRemote(wire, hash);
        }
        else
            Again(wire);
        return true;
    }

    private void RememberHeldRemote(string wire, string? hash)
    {
        lock (gate)
            if (holdPaths.Contains(wire))
                remoteSeenWhileHeld[wire] = hash;
    }
}
