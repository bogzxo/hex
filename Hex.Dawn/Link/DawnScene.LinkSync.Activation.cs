using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> linkSyncActivation = new(StringComparer.OrdinalIgnoreCase);

    private bool LinkSyncOptedOut(string device, string folder) =>
        LinkSyncChoices().Any(choice => HexLinkData.String(choice, "device", 200) == device &&
            PlatformPaths.Comparer.Equals(HexLinkData.String(choice, "folder", 200), folder) &&
            HexLinkData.Bool(choice, "enabled") == false);

    private void RequireSyncEnabled(HexLinkPeer peer, string folder, bool resume)
    {
        if (LinkSyncPaused(peer.Device.Id).FirstOrDefault(pause => PlatformPaths.Comparer.Equals(pause.Folder, folder)) is not { } paused)
            return;
        if (!resume || !HexLinkAllowed.Has(peer.Session.Connection.Rights, HexLinkRights.Steer))
            throw new HexLinkException(HexLinkErrors.NOT_ALLOWED,
                LinkStoppedOn(paused, LinkDevice?.Name ?? "this device") + " " + Said("link.sync_start_there"));
        LinkSyncRemember(peer.Device.Id, folder, on: true);
    }

    private async Task LinkResumeLegacyPair(HexLinkPeer peer, HexLinkFolder folder, CancellationToken cancel)
    {
        await peer.RefreshWorkspace(cancel).ConfigureAwait(false);
        if (peer.IsClosed || LinkPeerOf(peer.Device.Id) != peer ||
            !HexLinkAllowed.Has(peer.Session.Connection.Rights, HexLinkRights.Steer) ||
            peer.Workspace.Folders.FirstOrDefault(remote => PlatformPaths.Comparer.Equals(remote.Name, folder.Name)) is not { } counterpart ||
            linkServices?.Workspace.Folders.FirstOrDefault(local => PlatformPaths.Same(local.Path, folder.Path) &&
                PlatformPaths.Comparer.Equals(local.Name, folder.Name)) is not { } current ||
            (current.Authority is not null && counterpart.Authority is not null))
            throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "This sync is no longer an active legacy pair.");
        if (LinkSyncOptedOut(peer.Device.Id, current.Name))
            LinkSyncRemember(peer.Device.Id, current.Name, on: false, keepDisabled: false);
    }

    private void LinkAcceptRemotePauses(HexLinkPeer peer)
    {
        if (peer.IsClosed || LinkPeerOf(peer.Device.Id) != peer)
            return;
        foreach (HexLinkSyncPause pause in peer.PausedSyncFolders.Where(pause => pause.By is not null))
        {
            if (LinkSyncOf(peer.Device.Id, pause.Folder) is { } sync && sync.UsesPeer(peer))
                sync.Stop();
        }
    }

    private bool LinkBoundSyncPeer(HexLinkPeer peer, HexLinkFolder folder, HexLinkAuthority pin) =>
        !peer.IsClosed && LinkPeerOf(peer.Device.Id) == peer &&
        linkServices?.Workspace.Folders.Any(each => PlatformPaths.Comparer.Equals(each.Name, folder.Name)
            && each.Authority == pin && PlatformPaths.Same(each.Path, folder.Path)) == true;

    private async Task<HexLinkSyncReport?> LinkReceiveSyncActivation(HexLinkPeer peer, HexLinkFolder folder,
        string action, bool resume, CancellationToken cancel)
    {
        var pin = folder.Authority!;
        if (!LinkBoundSyncPeer(peer, folder, pin))
            throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "The bound workspace is no longer shared on this link.");
        if (action == "recheck")
        {
            RequireSyncEnabled(peer, folder.Name, resume);
            if (await LinkStartBoundReplica(peer, folder.Name, pin, cancel, resume) is { } why)
                throw new HexLinkException(HexLinkErrors.CONFLICT, why);
            return null;
        }

        var gate = linkSyncActivation.GetOrAdd(LinkSyncKey(peer.Device.Id, folder.Name), _ => new(1, 1));
        await gate.WaitAsync(cancel);
        try
        {
            if (!LinkBoundSyncPeer(peer, folder, pin) ||
                !HexLinkAllowed.Has(peer.Session.Connection.Rights, HexLinkRights.View))
                throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "The bound workspace or access changed.");
            // A genuinely one-way reader cannot expose its workspace back to the owner.
            // The owner can acknowledge the absence of its engine without reading the replica.
            if (action == "pull")
            {
                if (HexLinkAllowed.Has(peer.Granted, HexLinkRights.Edit))
                    throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "Sync can run on the pinned authority. Retry activation there.");
                RequireSyncEnabled(peer, folder.Name, resume);
                await LinkDrainBoundEngine(peer.Device.Id, folder.Name, cancel);
                return null;
            }

            if (action == "stop")
            {
                await LinkDrainBoundEngine(peer.Device.Id, folder.Name, cancel);
                return null;
            }

            if (!HexLinkAllowed.Has(peer.Granted, HexLinkRights.Edit))
                throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "The authority cannot write to the replica without Edit.");
            await peer.RefreshWorkspace(cancel);
            if (!LinkBoundSyncPeer(peer, folder, pin) ||
                !HexLinkAllowed.Has(peer.Session.Connection.Rights, HexLinkRights.View) ||
                !peer.Workspace.Folders.Any(each => PlatformPaths.Comparer.Equals(each.Name, folder.Name) &&
                    each.Authority == pin))
                throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "The replica or pinned authority changed.");

            RequireSyncEnabled(peer, folder.Name, resume);
            if (action == "ready")
            {
                if (LinkSyncOf(peer.Device.Id, folder.Name) is { } previous &&
                    (!previous.BoundAuthorityEngine || !LinkSyncSamePeer(previous, peer)))
                    await LinkDrainBoundEngine(peer.Device.Id, folder.Name, cancel);
                if (await LinkSyncStart(peer.Device.Id, folder.Name, remember: false, boundEngine: true, cancel: cancel) is { } why)
                    throw new InvalidOperationException(why);
            }
            else if (LinkSyncOf(peer.Device.Id, folder.Name) is { } sync)
            {
                await sync.Flush(cancel);
            }
            return LinkMakeSyncReport(peer.Device.Id)
                .FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Folder, folder.Name));
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string?> LinkStartBoundReplica(HexLinkPeer peer, string folder, HexLinkAuthority pin,
        CancellationToken cancel, bool resume = false)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel, peer.Session.Closing);
        limit.CancelAfter(HexLinkPeer.SyncActivationPatience);
        var gate = linkSyncActivation.GetOrAdd(LinkSyncKey(peer.Device.Id, folder), _ => new(1, 1));
        try
        {
            await gate.WaitAsync(limit.Token);
            try
            {
                await peer.RefreshWorkspace(limit.Token);
                if (LinkSyncOptedOut(peer.Device.Id, folder))
                    return Said("link.sync_paused_on", ("device", LinkDevice?.Name ?? "this device"));
                if (!peer.Workspace.Folders.Any(each => PlatformPaths.Comparer.Equals(each.Name, folder) && each.Authority == pin))
                    return Said("link.sync_authority_required", ("folder", folder));

                if (HexLinkAllowed.Has(peer.Session.Connection.Rights, HexLinkRights.Edit))
                {
                    if (LinkSyncOf(peer.Device.Id, folder) is { BoundPullMirror: true, State: HexLinkSyncState.PullOnly } mirror &&
                        mirror.UsesPeer(peer))
                        await mirror.Flush(limit.Token);
                    await LinkDrainBoundEngine(peer.Device.Id, folder, limit.Token);
                    HexLinkSyncReport? status = await peer.ActivateSync(folder, pin, "ready", limit.Token, resume);
                    return status is { State: HexLinkSyncState.Syncing or HexLinkSyncState.Idle or HexLinkSyncState.PullOnly }
                        ? null : status?.Why ?? "The pinned authority did not start sync.";
                }

                await peer.ActivateSync(folder, pin, "pull", limit.Token, resume);
                if (HexLinkAllowed.Has(peer.Session.Connection.Rights, HexLinkRights.Edit))
                    return "Link rights changed during sync activation. Sync will retry with the current grants.";
                if (LinkSyncOf(peer.Device.Id, folder) is { } previous && !LinkSyncSamePeer(previous, peer))
                    await LinkDrainBoundEngine(peer.Device.Id, folder, limit.Token);
                string? start = await LinkSyncStart(peer.Device.Id, folder, remember: false, boundEngine: true, cancel: limit.Token);
                return start ?? (LinkSyncOf(peer.Device.Id, folder) is
                    { State: HexLinkSyncState.Idle or HexLinkSyncState.Syncing or HexLinkSyncState.PullOnly }
                    ? null : LinkSyncOf(peer.Device.Id, folder)?.Why ?? "The pull-only sync did not start.");
            }
            finally
            {
                gate.Release();
            }
        }
        catch (Exception error) when (error is HexLinkException or IOException or InvalidOperationException ||
            error is OperationCanceledException && !cancel.IsCancellationRequested)
        {
            return error switch
            {
                HexLinkException { Code: HexLinkErrors.METHOD_NOT_FOUND } =>
                    "This Hex cannot activate authority-managed sync with the other device's version.",
                OperationCanceledException => "Sync activation timed out or the link closed.",
                _ => error.Message
            };
        }
    }

    private async Task LinkDrainBoundEngine(string deviceId, string folder, CancellationToken cancel)
    {
        HexLinkSync? sync;
        Task? starting;
        lock (linkSyncGate)
        {
            if (!linkSyncs.TryGetValue(LinkSyncKey(deviceId, folder), out var entry) || entry.Sync is null)
                return;
            sync = entry.Sync;
            starting = entry.Starting;
        }

        await sync.StopForRepositoryRefresh().WaitAsync(cancel);
        if (starting is not null)
            await starting.WaitAsync(cancel);
        await sync.DrainStoppedEngine(cancel);
        LinkReportSync(deviceId);
    }

    private void LinkSyncRightsChangedLocally(HexLinkPeer peer)
    {
        lock (linkSyncGate)
            foreach (var entry in linkSyncs.Values.Where(each => each.DeviceId == peer.Device.Id))
                entry.Sync?.RefreshReceiveRights();
        if (HexLinkAllowed.Has(peer.Granted, HexLinkRights.View))
            _ = LinkRecheckBoundSync(peer);
    }

    private async Task LinkRecheckBoundSync(HexLinkPeer peer)
    {
        if (linkServices?.Workspace.Folders.Any(folder => folder.FolderAuthorityId == peer.Device.Id) != true ||
            !await TryRefreshLinkWorkspace(peer).ConfigureAwait(false))
            return;
        PostLink(() =>
        {
            if (!peer.IsClosed && LinkPeerOf(peer.Device.Id) == peer && HexLinkAllowed.Has(peer.Granted, HexLinkRights.View))
                LinkSyncStartSharedFolders(peer, boundOnly: true);
        });
    }

    private bool LinkStopBoundReplica(string deviceId, string folder)
    {
        if (LinkPeerOf(deviceId) is not { IsClosed: false } peer ||
            linkServices?.Workspace.Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, folder))
                ?.Authority is not { } pin || pin.DeviceId != peer.Device.Id ||
            !peer.Workspace.Folders.Any(each => PlatformPaths.Comparer.Equals(each.Name, folder) && each.Authority == pin))
            return false;
        _ = TellAuthorityStopped();
        return true;

        async Task TellAuthorityStopped()
        {
            var gate = linkSyncActivation.GetOrAdd(LinkSyncKey(deviceId, folder), _ => new(1, 1));
            try
            {
                await gate.WaitAsync(peer.Session.Closing);
                try { await peer.ActivateSync(folder, pin, "stop", peer.Session.Closing); }
                finally { gate.Release(); }
            }
            catch (Exception error) when (error is HexLinkException or OperationCanceledException)
            {
                // The local opt-out is already saved. A closed link has no active pair to stop.
            }
        }
    }

    private async Task<bool> LinkFlushBoundReplica(HexLinkPeer peer, string folder, CancellationToken cancel)
    {
        if (linkServices?.Workspace.Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, folder))
                ?.Authority is not { } pin || pin.DeviceId != peer.Device.Id ||
            !HexLinkAllowed.Has(peer.Session.Connection.Rights, HexLinkRights.Edit) ||
            !peer.Workspace.Folders.Any(each => PlatformPaths.Comparer.Equals(each.Name, folder) && each.Authority == pin))
            return false;
        if (await peer.ActivateSync(folder, pin, "flush", cancel) is not
            { State: HexLinkSyncState.Syncing or HexLinkSyncState.Idle or HexLinkSyncState.PullOnly })
            throw new InvalidOperationException($"The authority is not syncing {folder}.");
        return true;
    }
}
