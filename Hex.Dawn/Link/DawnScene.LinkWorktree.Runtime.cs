namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private sealed record LinkPublicationRuntime(LinkServices Services, string Storage, HexLinkWorktreePublication Publication, HexLinkCoalesce Pump);
    private LinkPublicationRuntime? linkPublicationRuntime;
    private System.Collections.Concurrent.ConcurrentDictionary<string, HexLinkCoalesce> privateFollow { get => LazyInitializer.EnsureInitialized(ref field, static () => new(PlatformPaths.Comparer)); }

    private LinkPublicationRuntime? CurrentLinkPublication =>
        Volatile.Read(ref linkPublicationRuntime) is { } active && ReferenceEquals(active.Services, linkServices) &&
        LinkPrivateStoragePath is { } storage && PlatformPaths.Same(active.Storage, storage) ? active : null;

    private SemaphoreSlim publicationStarting { get => LazyInitializer.EnsureInitialized(ref field, static () => new(1, 1)); }

    internal async Task LinkEnsureWorktreePublication()
    {
        if (linkByTest || CurrentLinkPublication is not null || linkServices is not { } services)
            return;

        if (services.Workspace.Folders.Count == 0 || LinkPrivateStoragePath is null)
            return;

        CancellationToken stop = linkCancel?.Token ?? CancellationToken.None;
        await publicationStarting.WaitAsync(stop);
        try
        {
            if (ReferenceEquals(linkServices, services) && CurrentLinkPublication is null)
                await LinkActivateWorktreePublication(PublicationRecipients, stop);
        }
        finally
        {
            publicationStarting.Release();
        }
    }

    private IReadOnlyList<string> PublicationRecipients(string folder)
    {
        var recipients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (linkServices?.Workspace.Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, folder)) is { } shared)
        {
            lock (linkSyncGate)
            {
                foreach (LinkSyncEntry entry in linkSyncs.Values)
                    if (PlatformPaths.Comparer.Equals(entry.Folder, folder) && entry.Sync is { PublicationPairReady: true } sync &&
                        PlatformPaths.Same(sync.LocalFolder.Path, shared.Path))
                        recipients.Add(entry.DeviceId);
            }
        }
        foreach (var known in LinkKnown)
            if (LinkSyncChosen(known.Device.Id).Contains(folder, PlatformPaths.Comparer))
                recipients.Add(known.Device.Id);
        return [.. recipients];
    }

    private async Task LinkPreparePublicationAndShare(HexLinkPeer peer)
    {
        try
        {
            await LinkEnsureWorktreePublication();
            await LinkPrepareSharedWorkspace(peer);
        }
        catch (OperationCanceledException) when (peer.IsClosed || linkCancel?.IsCancellationRequested != false) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HexLinkException)
        {
            PostLink(() => Say(error.Message, error: true));
        }
    }

    // The live sync adapter calls this before starting any engine in the workspace. Leaving the
    // callbacks unbound keeps publication unavailable on peers that cannot persist paired baselines.
    internal async Task LinkActivateWorktreePublication(Func<string, IReadOnlyList<string>> recipients,
        Func<HexLinkFolder, HexLinkCommittedSet, IReadOnlyList<string>, CancellationToken, Task> deferLive,
        Func<HexLinkFolder, HexLinkCommittedSet, string, CancellationToken, Task> acknowledgeLive,
        Func<HexLinkFolder, HexLinkCommittedSet, CancellationToken, Task> installReplicaBaseline,
        Action<HexLinkFolder, HexLinkCommittedSet, string> releaseLive, CancellationToken cancel)
    {
        var services = linkServices ?? throw new InvalidOperationException("Link is off.");
        string storage = LinkPrivateStorage;
        await HexLinkPrivateWorktree.RecoverSetups(storage, cancel);
        var publication = new HexLinkWorktreePublication(services, Path.Combine(storage, "pub"),
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
        LinkPublicationRuntime? old = Interlocked.Exchange(ref linkPublicationRuntime, new(services, storage, publication, pump));
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

    private System.Collections.Concurrent.ConcurrentDictionary<Guid, SemaphoreSlim> privateDraftStarts { get => LazyInitializer.EnsureInitialized(ref field, static () => new()); }

    private async Task<HexLinkPrivateWorktree> LinkStartPrivateWorktree(LinkServices services, HexLinkFolder folder, Guid id,
        CancellationToken cancel, IReadOnlyList<string>? additional = null, Action<HexLinkWorktreeProgress>? progress = null)
    {
        string storage = LinkPrivateStorage;
        var serial = privateDraftStarts.GetOrAdd(id, static _ => new SemaphoreSlim(1));
        await serial.WaitAsync(cancel);
        try
        {
            HexLinkPrivateWorktree? existing = HexLinkPrivateWorktree.RecordedFolder(storage, id) is not null
                ? await LinkOpenStoredDraft(id, cancel) : null;
            var policy = LinkPrivateWorktreeGateForScope(services, folder);
            HashSet<string> selected = [];
            await using var live = await HexLinkPublicationFence.ForRoot(folder.Path).EnterLive(cancel);
            var tree = new HexLinkTree(folder, () => services.Excludes);
            HexLinkManifest snapshot = await Task.Run(() => tree.PrivateManifest(nested =>
            {
                string[] layout = HexLinkWorktreeScope.Layout(nested);
                selected = HexLinkWorktreeScope.Initial(layout, policy?.WorkingFolder, additional ?? []);
                if (existing is not null)
                    selected.UnionWith(existing.MaterializedRepositories);
                return path => selected.Contains(HexLinkWorktreeScope.Nearest(layout, path));
            }, live.Stop), live.Stop);
            if (existing is not null)
            {
                await existing.EnsureRepositories(selected, snapshot, progress, live.Stop);
                await existing.Follow(snapshot, live.Stop);
                return existing;
            }

            var draft = await HexLinkPrivateWorktree.Open(storage, id, folder, snapshot, () => services.Excludes, live.Stop, selected, progress);
            if (cancel.IsCancellationRequested)
            {
                await draft.Discard(CancellationToken.None);
                cancel.ThrowIfCancellationRequested();
            }
            linkPrivateDrafts[id] = draft;
            return draft;
        }
        finally
        {
            serial.Release();
        }
    }

    private HexLinkWorktreeGatePolicy? LinkPrivateWorktreeGateForScope(LinkServices services, HexLinkFolder folder)
    {
        if (services.FolderAuthorities.Of(folder) is not { } pin)
            return null;
        return pin.DeviceId == services.Device.Id
            ? LinkReadOwnedWorktreeGate(services, folder with { Authority = pin })
            : link?.PeerOf(pin.DeviceId)?.Workspace.Folders.FirstOrDefault(each =>
                PlatformPaths.Comparer.Equals(each.Name, folder.Name) && each.Authority == pin)?.WorktreeGate;
    }

    private static bool PrivateRepositoryPresent(HexLinkPrivateWorktree draft, string path) =>
        draft.MaterializedRepositories.Contains(HexLinkWorktreeScope.Nearest(draft.RepositoryPaths, path), PlatformPaths.Comparer);

    private async Task LinkMaterializePrivatePath(HexLinkPrivateWorktree draft, string path, CancellationToken cancel)
    {
        if (PrivateRepositoryPresent(draft, path))
            return;
        var folder = draft.SharedFolder;
        var services = linkServices ?? throw new IOException("Link is off.");
        string repo = HexLinkWorktreeScope.Nearest(draft.RepositoryPaths, path);
        var desired = new HashSet<string>(draft.RepositoryPaths.Where(parent => PlatformPaths.Comparer.Equals(parent, repo) ||
            repo.StartsWith(parent + "/", PlatformPaths.Comparison)), PlatformPaths.Comparer);
        desired.ExceptWith(draft.MaterializedRepositories);
        await using var live = await HexLinkPublicationFence.ForRoot(folder.Path).EnterLive(cancel);
        var tree = new HexLinkTree(folder, () => services.Excludes);
        var snapshot = await Task.Run(() => tree.PrivateManifest(nested =>
        {
            string[] layout = HexLinkWorktreeScope.Layout(nested);
            return file => desired.Contains(HexLinkWorktreeScope.Nearest(layout, file));
        }, live.Stop), live.Stop);
        await draft.EnsureRepositories(desired, snapshot, null, live.Stop);
    }
    internal async Task LinkFollowPrivateWorkingChanges(HexLinkFolder folder, CancellationToken cancel)
    {
        HexLinkPrivateWorktree[] drafts = [.. linkPrivateDrafts.Values.Where(draft => !draft.HoldIncoming &&
            PlatformPaths.Comparer.Equals(Path.GetFullPath(draft.SharedFolder.Path), Path.GetFullPath(folder.Path)))];
        if (drafts.Length == 0 || linkServices is not { } services)
            return;
        HexLinkManifest snapshot;
        await using (var live = await HexLinkPublicationFence.ForRoot(folder.Path).EnterLive(cancel))
        {
            var selected = new HashSet<string>(drafts.SelectMany(draft => draft.MaterializedRepositories), PlatformPaths.Comparer);
            var tree = new HexLinkTree(folder, () => services.Excludes);
            snapshot = await Task.Run(() => tree.PrivateManifest(nested =>
            {
                string[] layout = HexLinkWorktreeScope.Layout(nested);
                return path => selected.Contains(HexLinkWorktreeScope.Nearest(layout, path));
            }, live.Stop), live.Stop);
        }
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

    private void QueuePrivateFollowForPath(string full)
    {
        if (linkPrivateDrafts.IsEmpty)
            return;
        // Only the shared export is observed. The isolated checkout's own edits never feed this path.
        foreach (var draft in linkPrivateDrafts.Values)
        {
            if (PlatformPaths.IsUnder(full, draft.SharedFolder.Path) && !draft.HoldIncoming)
            {
                QueuePrivateWorktreeFollow(draft.SharedFolder);
                return;
            }
        }
    }

    private void QueuePrivateWorktreeFollow(HexLinkFolder folder)
    {
        if (linkPrivateDrafts.IsEmpty)
            return;
        string root = Path.GetFullPath(folder.Path);
        privateFollow.GetOrAdd(root, _ => new HexLinkCoalesce(TimeSpan.FromMilliseconds(500), async () =>
        {
            CancellationToken stop = linkCancel?.Token ?? CancellationToken.None;
            try { await LinkFollowPrivateWorkingChanges(folder, stop); }
            catch (Exception error) when (error is IOException or HexLinkException or OperationCanceledException)
            {
                if (!stop.IsCancellationRequested)
                    Console.WriteLine($"[Hex] Private worktree follow is pending: {error.Message}");
            }
        })).Poke();
    }

    private HexLinkDraftRoute? LinkPrivatePublishRoute(HexLinkFolder folder)
    {
        if (CurrentLinkPublication is not { } active || LinkPublishPin(active.Services, folder) is not { } pin)
            return null;
        return active.Publication.Route(folder with { Authority = pin }, link?.PeerOf(pin.DeviceId));
    }

    // The authority a private worktree's folder publishes to: the one pinned here or, for a folder kept in sync with its
    // authority that never pinned it here, the one the devices it's synced with say it is, as its claims are scoped by.
    // A replica Sync Folder alone made said there was no authority to publish to
    private HexLinkAuthority? LinkPublishPin(IHexLinkServices services, HexLinkFolder folder)
    {
        if ((services.FolderAuthorities.Of(folder) ?? folder.Authority) is { } pin)
            return pin;
        return LinkPeers.Where(peer => services.ChosenSyncFolders(peer.Device.Id).Contains(folder.Name, PlatformPaths.Comparer)
                || peer.ChosenSyncFolders.Contains(folder.Name, PlatformPaths.Comparer))
            .Select(peer => peer.Workspace.Folders.FirstOrDefault(other => PlatformPaths.Comparer.Equals(other.Name, folder.Name))?.Authority)
            .OfType<HexLinkAuthority>().Distinct().ToArray() is [var agreed] && LinkPeers.Any(peer => peer.Device.Id == agreed.DeviceId)
            ? agreed : null;
    }
}
