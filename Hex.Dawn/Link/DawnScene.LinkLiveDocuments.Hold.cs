namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    // The editor thread owns both sets. Keep a join that predates a hold from attaching its old snapshot.
    private HashSet<string> linkDocumentsTakingHold { get => LazyInitializer.EnsureInitialized(ref field, static () => new(PlatformPaths.Comparer)); }
    private HashSet<string> linkDocumentStaleJoins { get => LazyInitializer.EnsureInitialized(ref field, static () => new(PlatformPaths.Comparer)); }
    private HashSet<string> linkDocumentRejoinAfterHold { get => LazyInitializer.EnsureInitialized(ref field, static () => new(PlatformPaths.Comparer)); }

    private bool LinkHoldBlocksDocument(string wire, string? full)
    {
        if (linkServices is not { } services)
            return false;
        full ??= LinkLocalOf(wire);
        if (full is not null && linkDocumentsTakingHold.Contains(full))
            return true;
        foreach (var folder in services.Workspace.Folders)
        {
            var store = services.Holds(folder);
            if (store.Count != 0 && (store.IsHeld(wire) ||
                full is not null && store.WireFromFullPath(full) is { } alias && store.IsHeld(alias)))
                return true;
        }
        return false;
    }

    private async Task<HexLinkHold> LinkHoldDocument(HexLinkHoldService service, string wire, CancellationToken cancel = default)
    {
        var (services, full, active) = await OnEditorForLink(() =>
        {
            cancel.ThrowIfCancellationRequested();
            if (linkServices is not { } current || LinkLocalOf(wire) is not { } path)
                throw HexLinkException.NotFound("The shared folder is no longer open.");
            if (!linkDocumentsTakingHold.Add(path))
                throw new HexLinkException(HexLinkErrors.BUSY, "A hold is already starting for this file.");

            linkDocumentRejoinAfterHold.Add(wire);
            var steps = new List<Task>();
            foreach (var shared in linkLiveDocuments.Values.ToArray())
            {
                if (!PlatformPaths.Same(shared.Wire, wire) &&
                    !(shared.Tab.Path is { } tabPath && PlatformPaths.Same(path, tabPath)))
                    continue;
                steps.Add(shared.ActiveStep);
                LinkLiveDocumentClose(shared, explicitly: false);
            }
            foreach (string joining in linkLiveJoining)
                if (LinkLocalOf(joining) is { } local && PlatformPaths.Same(local, path))
                    linkDocumentStaleJoins.Add(joining);
            CloseHeldDocumentRooms(current, path);
            return (current, path, Task.WhenAll(steps));
        }).ConfigureAwait(false);
        try
        {
            // A save captured before detaching finishes before the hold samples its basis.
            await active.WaitAsync(cancel).ConfigureAwait(false);
            return await service.Hold(wire, cancel).ConfigureAwait(false);
        }
        finally
        {
            await OnEditorForLink(() =>
            {
                try { CloseHeldDocumentRooms(services, full); }
                finally { linkDocumentsTakingHold.Remove(full); }
                return true;
            }).ConfigureAwait(false);
        }
    }

    private async Task<string?> LinkApplyHeldSubmission(LinkServices services, string full, string wire, string? basis,
        byte[]? bytes, CancellationToken cancel, bool? executable, HexLinkFiles.PackDirectories? directories)
    {
        bool suspended = await OnEditorForLink(() =>
        {
            cancel.ThrowIfCancellationRequested();
            if (!ReferenceEquals(linkServices, services))
                throw new HexLinkException(HexLinkErrors.CLOSED, "Link changed before the submission arrived.");
            if (linkDocumentsTakingHold.Contains(full))
                throw new HexLinkException(HexLinkErrors.BUSY, "A hold transition is already changing this document.");

            LinkTabStamp tab = LinkTabBeforeWrite(full);
            LinkLiveDocument? shared = LinkLiveOf(tab.Document);
            HexLinkDocumentRoom? room = shared?.Room ?? services.Documents.Rooms.TryFind(wire);
            if (shared is null && room is null)
                return false;

            if (shared is { SaveRequested: true } or { DiskSaveInFlight: > 0 } || room?.SavingFile == true)
                throw new HexLinkException(HexLinkErrors.BUSY, "A shared document save is still being applied.");
            if (tab.Dirty || shared is { Draft: not null } or { Replica.HasPending: true } ||
                shared is not null && (shared.AgentEdits.Count > 0 || shared.UndoRequests.Count > 0))
                throw LinkLiveSaveConflict(full, wire);

            string? disk = ReadNow(full);
            if (HexLinkFiles.HashOf(full) != basis)
                throw new HexLinkException(HexLinkErrors.CONFLICT, "The shared file changed before the submitted version arrived.");
            if (room is not null && (disk is null || !room.TryRetireForSubmission(HexDocSaved.NormalText(disk))))
                throw LinkLiveSaveConflict(full, wire);

            linkDocumentsTakingHold.Add(full);
            if (shared is not null)
            {
                linkDocumentRejoinAfterHold.Add(shared.Wire);
                LinkLiveDocumentClose(shared, explicitly: false);
            }
            if (room is not null)
                services.Documents.Rooms.Close(room.Path);
            return true;
        }).ConfigureAwait(false);

        // This copy can also be a source for another peer's file sync. Carry the exact submission forward.
        int slash = wire.IndexOf('/');
        HexLinkSync[] forwarding;
        lock (linkSyncGate)
            forwarding = [.. linkSyncs.Values.Where(entry => slash > 0 && PlatformPaths.Comparer.Equals(entry.Folder, wire[..slash]))
                .Select(entry => entry.Sync).OfType<HexLinkSync>()];
        string? submitted = bytes is null ? null : HexLinkFiles.Hash(bytes);
        foreach (HexLinkSync sync in forwarding)
            sync.RememberHeldSubmission(wire, submitted);
        try
        {
            return await LinkCompareAndReplace(full, wire, basis, bytes, cancel, executable, directories).ConfigureAwait(false);
        }
        catch
        {
            foreach (HexLinkSync sync in forwarding)
                sync.ForgetHeldSubmission(wire, submitted);
            throw;
        }
        finally
        {
            if (suspended)
                await OnEditorForLink(() => linkDocumentsTakingHold.Remove(full)).ConfigureAwait(false);
        }
    }

    private static void CloseHeldDocumentRooms(LinkServices services, string full)
    {
        // A closed editor may still host a headless room, including under a second workspace root.
        foreach (var folder in services.Workspace.Folders)
        {
            if (PlatformPaths.IsUnder(full, folder.Path) && !PlatformPaths.Same(full, folder.Path))
            {
                string wire = folder.Name + "/" + Path.GetRelativePath(folder.Path, full).Replace('\\', '/');
                services.Documents.Rooms.Close(wire);
            }
        }
    }
}
