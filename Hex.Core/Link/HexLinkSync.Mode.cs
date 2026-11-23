namespace Hex.Core;

internal sealed partial class HexLinkSync
{
    private async Task<bool> ReconcileMode(string wire, string full, HexLinkTreeFile mine, HexLinkTreeFile theirs,
        HexLinkSyncAgreed? agreed, byte[]? bytes, CancellationToken cancel)
    {
        bool? localMode = mine.Executable, remoteMode = theirs.Executable;
        if (!peer.Session.SupportsBulk || mine.Hash is null || mine.Hash != theirs.Hash || localMode == remoteMode)
            return false;
        if (localMode is null || remoteMode is null)
        {
            // Windows can learn the executable bit from a peer without replacing a file whose contents already agree.
            tree.RememberMode(full, remoteMode);
            return false;
        }

        bool push;
        if (agreed?.Executable == remoteMode)
            push = true;
        else if (agreed?.Executable == localMode)
            push = false;
        else
        {
            HoldStreamedConflict(wire, "executable mode changed on both devices");
            return true;
        }

        bool mode = (push ? localMode : remoteMode)!.Value;
        try
        {
            if (push)
            {
                if (!remoteCertified || pullOnly || !HexLinkAllowed.Has(peer.Granted, HexLinkRights.Edit))
                {
                    lock (gate)
                        deferredPushes.Add(wire);
                    return true;
                }

                if (bytes is not null && bytes.Length <= HexLinkFilePack.MaximumFile)
                    await PushPacked(wire, theirs.Hash, bytes, mode, cancel);
                else
                    await peer.WriteStreamedFile(new HexLinkFileTransfer(wire, mine.Size, mine.Hash, theirs.Hash, mode), full, cancel);
                Interlocked.Increment(ref sent);
            }
            else
            {
                if (!OperatingSystem.IsWindows())
                {
                    if (bytes is not null)
                        await ApplyLocal(full, wire, mine.Hash, bytes, cancel, mode);
                    else
                    {
                        await using FileStream source = HexLinkFileTransfer.Read(full);
                        if (!await ApplyFencedStream(full, wire, mine.Hash, source, mine.Size, mine.Hash, cancel, mode))
                            return true;
                    }
                }
                tree.RememberMode(full, mode);
                Interlocked.Increment(ref received);
            }
        }
        catch (HexLinkException error) when (EditorDirty(error))
        {
            HoldStreamedConflict(wire, "unsaved changes while setting executable mode");
            return true;
        }

        lock (gate)
            SetBase(wire, (agreed ?? Identical(mine.Hash)) with { Local = mine.Hash, Remote = theirs.Hash!, Executable = mode });
        Notify();
        return true;
    }
}
