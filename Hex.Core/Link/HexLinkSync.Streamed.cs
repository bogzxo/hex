namespace Hex.Core;

internal sealed partial class HexLinkSync
{
    private async Task<HexLinkTreeFile> ReadStreamedMetadata(string wire, string full, FileInfo info, CancellationToken cancel)
    {
        long length = info.Length;
        if (!peer.Session.SupportsBulk || length > HexLinkFileTransfer.MaximumLength)
            return new HexLinkTreeFile(wire, null, length);

        await using FileStream read = HexLinkFileTransfer.Read(full);
        string hash = Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(read, cancel));
        if (read.Length != length)
            throw new IOException($"{wire} changed while Link read its hash.");
        return new HexLinkTreeFile(wire, hash, length, Executable: tree.Mode(full));
    }

    private async Task ReconcileStreamed(string wire, string full, HexLinkTreeFile? mine, HexLinkTreeFile? theirs,
        HexLinkSyncAgreed? agreed, HexLinkSyncConflict? held, CancellationToken cancel)
    {
        string? m = mine?.Hash, t = theirs?.Hash;
        if (held?.HasUnsavedChanges == true || (agreed is not null && m == agreed.Local && t == agreed.Remote))
            return;
        if (m == t)
        {
            lock (gate)
            {
                Agree(wire, m);
                conflicts.Remove(wire);
            }
            return;
        }
        if (held is not null)
            return;

        if (m == agreed?.Local)
        {
            await PullStreamed(wire, full, m, theirs, cancel);
        }
        else if (t == agreed?.Remote)
        {
            await PushStreamed(wire, full, mine, t, cancel);
        }
        else
        {
            HoldStreamedConflict(wire, "large file changed on both devices");
        }
    }

    private async Task PullStreamed(string wire, string full, string? expected, HexLinkTreeFile? theirs, CancellationToken cancel)
    {
        try
        {
            if (theirs is null)
            {
                if (!remoteCertified || !await Asked(() => peer.ConfirmAbsent(folder.Name, wire, cancel), cancel))
                {
                    Skip(wire, "The peer cannot confirm a working-tree deletion.");
                    return;
                }
                if (expected is not null)
                    await ApplyStream(full, wire, expected, null, 0, null, cancel, null);
            }
            else
            {
                if (!peer.CanReadFilePacks)
                {
                    Skip(wire, "The peer cannot send a streamed file on this link.");
                    return;
                }
                Interlocked.Increment(ref reads);
                var transfer = new HexLinkFileTransfer(wire, theirs.Size, theirs.Hash!, Executable: theirs.Executable);
                string partial = transfer.PartialPath(Path.Combine(Path.GetDirectoryName(StateFile)!, "transfers"), tree.Root + "\n" + peer.Device.Id);
                await peer.ReadStreamedFile(transfer, partial, cancel);
                try
                {
                    await using FileStream staged = HexLinkFileTransfer.Read(partial);
                    await ApplyStream(full, wire, expected, staged, transfer.Length, transfer.Hash, cancel, transfer.Executable);
                    tree.RememberMode(full, transfer.Executable);
                }
                catch (HexLinkException e) when (e.Code == HexLinkErrors.INVALID_PARAMS)
                {
                    HexLinkGit.TryDelete(partial);
                    Again(wire, e.Message);
                    return;
                }
                HexLinkGit.TryDelete(partial);
            }
        }
        catch (HexLinkException e) when (EditorDirty(e))
        {
            HoldStreamedConflict(wire, "unsaved changes in a local tab");
            return;
        }

        lock (gate)
        {
            if (theirs is null)
            {
                Agree(wire, null);
                RemoveRemote(wire);
            }
            else
            {
                SetBase(wire, Identical(theirs.Hash!) with { Executable = theirs.Executable });
                remote[wire] = theirs;
            }
        }
        Interlocked.Increment(ref received);
        Notify();
    }

    private async Task PushStreamed(string wire, string full, HexLinkTreeFile? mine, string? expected, CancellationToken cancel)
    {
        if (!remoteCertified)
        {
            Skip(wire, "The peer does not support checked working-tree writes.");
            return;
        }
        lock (gate)
        {
            if (!live || !ready)
                return;
            if (pullOnly || !HexLinkAllowed.Has(peer.Granted, HexLinkRights.Edit))
            {
                deferredPushes.Add(wire);
                return;
            }
        }

        try
        {
            if (mine is null)
            {
                if (HexLinkTree.RegularFile(full, tree.Root) is not null)
                {
                    Again(wire);
                    return;
                }
                if (expected is not null)
                    await peer.DeleteStreamedFile(wire, expected, cancel);
            }
            else
            {
                await peer.WriteStreamedFile(new HexLinkFileTransfer(wire, mine.Size, mine.Hash!, expected, mine.Executable), full, cancel);
            }
        }
        catch (HexLinkException e) when (EditorDirty(e))
        {
            HoldStreamedConflict(wire, "unsaved changes in a remote tab");
            return;
        }

        lock (gate)
        {
            if (mine is null)
            {
                Agree(wire, null);
                RemoveRemote(wire);
            }
            else
            {
                SetBase(wire, Identical(mine.Hash!) with { Executable = mine.Executable });
                remote[wire] = mine;
            }
        }
        Interlocked.Increment(ref sent);
        Notify();
    }

    private void HoldStreamedConflict(string wire, string kind)
    {
        lock (gate)
            conflicts[wire] = new HexLinkSyncConflict(wire, kind, null, null);
        Save();
        Notify();
    }

}
