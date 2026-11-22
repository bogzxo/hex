namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private sealed record LinkPublicationRuntime(LinkServices Services, HexLinkWorktreePublication Publication, HexLinkCoalesce Pump);
    private LinkPublicationRuntime? linkPublicationRuntime;

    private LinkPublicationRuntime? CurrentLinkPublication =>
        Volatile.Read(ref linkPublicationRuntime) is { } active && ReferenceEquals(active.Services, linkServices) ? active : null;

    // The live sync adapter calls this before starting any engine in the workspace. Leaving the
    // callbacks unbound keeps publication unavailable on peers that cannot persist paired baselines.
    internal async Task LinkActivateWorktreePublication(Func<string, IReadOnlyList<string>> recipients,
        Func<HexLinkFolder, HexLinkCommittedSet, IReadOnlyList<string>, CancellationToken, Task> deferLive,
        Func<HexLinkFolder, HexLinkCommittedSet, string, CancellationToken, Task> acknowledgeLive,
        Func<HexLinkFolder, HexLinkCommittedSet, CancellationToken, Task> installReplicaBaseline,
        Action<HexLinkFolder, HexLinkCommittedSet, string> releaseLive, CancellationToken cancel)
    {
        var services = linkServices ?? throw new InvalidOperationException("Link is off.");
        var publication = new HexLinkWorktreePublication(services, Path.Combine(LinkPrivateStorage, "pub"),
            recipients, deferLive, acknowledgeLive, installReplicaBaseline,
            folder =>
            {
                CurrentLinkPublication?.Pump.Poke();
                QueuePrivateWorktreeFollow(folder);
            }, releaseLive, ReadPrivateWorktreePresence, NoticePrivatePublication);
        await publication.Restore(cancel);
        if (!ReferenceEquals(services, linkServices))
            return;

        CancellationToken running = linkCancel?.Token ?? CancellationToken.None;
        var pump = new HexLinkCoalesce(TimeSpan.FromMilliseconds(250), async () =>
        {
            if (CurrentLinkPublication?.Publication != publication || running.IsCancellationRequested)
                return;
            foreach (HexLinkPeer peer in LinkPeers)
            {
                if (peer.IsClosed || !peer.Session.SupportsBulk || !HexLinkAllowed.Has(peer.Granted, HexLinkRights.Edit))
                    continue;
                try { await publication.Deliver(peer, running); }
                catch (OperationCanceledException) when (running.IsCancellationRequested) { return; }
                catch (Exception error) when (error is IOException or HexLinkException)
                {
                    Console.WriteLine($"[Hex] Publication delivery to {peer.Device.Name} is pending: {error.Message}");
                }
            }
        });
        LinkPublicationRuntime? old = Interlocked.Exchange(ref linkPublicationRuntime, new(services, publication, pump));
        old?.Pump.Dispose();
        pump.Poke();
        PostLink(() => { linkDirty = true; LinkMoved(); });
    }

    internal Task LinkActivateWorktreePublication(Func<string, IReadOnlyList<string>> recipients, CancellationToken cancel) =>
        LinkActivateWorktreePublication(recipients,
            (folder, set, peers, _) =>
            {
                foreach (string peer in peers)
                    PublicationPair(folder, peer)?.DeferPublication(set);
                return Task.CompletedTask;
            },
            (folder, set, peer, stop) =>
            {
                stop.ThrowIfCancellationRequested();
                HexLinkSync sync = PublicationPair(folder, peer)
                    ?? throw new HexLinkException(HexLinkErrors.BUSY, "Wait for the linked folder's sync before recording its receipt.");
                sync.InstallPublicationBaseline(set);
                return Task.CompletedTask;
            },
            (folder, set, stop) =>
            {
                stop.ThrowIfCancellationRequested();
                string? owner = linkServices?.FolderAuthorities.Of(folder)?.DeviceId;
                if (owner is not null && PublicationPair(folder, owner) is { PublicationPairReady: true } pair)
                    pair.InstallPublicationBaseline(set);
                return Task.CompletedTask;
            },
            (folder, set, peer) => PublicationPair(folder, peer)?.ReleasePublication(set), cancel);

    private HexLinkSync? PublicationPair(HexLinkFolder folder, string peer)
    {
        lock (linkSyncGate)
        {
            HexLinkSync? sync = linkSyncs.GetValueOrDefault(LinkSyncKey(peer, folder.Name))?.Sync;
            return sync is not null && PlatformPaths.Comparer.Equals(Path.GetFullPath(sync.LocalFolder.Path), Path.GetFullPath(folder.Path))
                ? sync : null;
        }
    }

    // Before Start loads or compares anything, restore the durable outbox's holds for this exact pair.
    internal void LinkRestorePublicationDeferrals(HexLinkSync sync)
    {
        if (CurrentLinkPublication is not { } active)
            return;
        foreach (var set in active.Publication.Pending(sync.Device.Id))
            if (set.Folder == sync.Folder && set.Epoch == active.Services.FolderAuthorities.Of(sync.LocalFolder)?.Epoch)
                sync.DeferPublication(set);
    }

    internal void LinkPublicationPairReady() => CurrentLinkPublication?.Pump.Poke();

    internal async Task LinkFollowPrivateWorkingChanges(HexLinkFolder folder, CancellationToken cancel)
    {
        HexLinkPrivateWorktree[] drafts = [.. linkPrivateDrafts.Values.Where(draft => !draft.HoldIncoming &&
            PlatformPaths.Comparer.Equals(Path.GetFullPath(draft.SharedFolder.Path), Path.GetFullPath(folder.Path)))];
        if (drafts.Length == 0 || linkServices is not { } services)
            return;
        HexLinkManifest snapshot;
        await using (var live = await HexLinkPublicationFence.ForRoot(folder.Path).EnterLive(cancel))
            snapshot = await Task.Run(new HexLinkTree(folder, () => services.Excludes).Manifest, live.Stop);
        foreach (var draft in drafts)
            await draft.Follow(snapshot, cancel);
    }

    private void NoticePrivatePublication(HexLinkCommittedSet set) => PostLink(() =>
    {
        Say(Said("link.publication_arrived", ("author", set.Author ?? "Link"), ("folder", set.Folder),
            ("count", set.Files.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        if (linkServices?.Workspace.Folders.FirstOrDefault(folder => folder.Name == set.Folder) is { } folder)
            QueuePrivateWorktreeFollow(folder);
    });

    private void QueuePrivateWorktreeFollow(HexLinkFolder folder)
    {
        if (linkPrivateDrafts.IsEmpty)
            return;
        CancellationToken stop = linkCancel?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            try { await LinkFollowPrivateWorkingChanges(folder, stop); }
            catch (Exception error) when (error is IOException or HexLinkException or OperationCanceledException)
            {
                if (!stop.IsCancellationRequested)
                    Console.WriteLine($"[Hex] Private worktree follow is pending: {error.Message}");
            }
        });
    }

    private HexLinkDraftRoute? LinkPrivatePublishRoute(HexLinkFolder folder)
    {
        if (CurrentLinkPublication is not { } active ||
            active.Services.FolderAuthorities.Of(folder) is not { } pin)
            return null;
        return active.Publication.Route(folder, link?.PeerOf(pin.DeviceId));
    }
}
