using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>Publish over authenticated bulk streams. Live sync supplies its durable baseline and deferral operations.</summary>
internal sealed class HexLinkWorktreePublication : IHexLinkPublicationService
{
    private readonly IHexLinkServices services;
    private readonly string storage;
    private readonly HexLinkCommittedSets receipts;
    private readonly HexLinkPublicationReplica replica;
    private readonly Action<HexLinkFolder>? queued;
    private readonly Func<HexLinkFolder, CancellationToken, Task<HexLinkDraftPresence>>? draftPresence;
    private readonly Action<HexLinkFolder, HexLinkCommittedSet, string>? releaseLive;
    private readonly Func<string, IReadOnlyList<string>> recipients;
    private readonly Func<HexLinkFolder, HexLinkCommittedSet, IReadOnlyList<string>, CancellationToken, Task> deferLive;
    private readonly Func<HexLinkFolder, HexLinkCommittedSet, string, CancellationToken, Task> acknowledgeLive;
    private readonly ConcurrentDictionary<string, Task<HexLinkPublishJournal>> owners = new(PlatformPaths.Comparer);

    public HexLinkWorktreePublication(IHexLinkServices services, string storage, Func<string, IReadOnlyList<string>> recipients,
        Func<HexLinkFolder, HexLinkCommittedSet, IReadOnlyList<string>, CancellationToken, Task> deferLive,
        Func<HexLinkFolder, HexLinkCommittedSet, string, CancellationToken, Task> acknowledgeLive,
        Func<HexLinkFolder, HexLinkCommittedSet, CancellationToken, Task> installReplicaBaseline, Action<HexLinkFolder>? queued = null, Action<HexLinkFolder, HexLinkCommittedSet, string>? releaseLive = null,
        Func<HexLinkFolder, CancellationToken, Task<HexLinkDraftPresence>>? draftPresence = null, Action<HexLinkCommittedSet>? received = null)
    {
        this.services = services;
        this.storage = Path.GetFullPath(storage);
        this.recipients = recipients;
        this.deferLive = deferLive;
        this.acknowledgeLive = acknowledgeLive;
        this.queued = queued;
        this.draftPresence = draftPresence;
        this.releaseLive = releaseLive;
        receipts = new HexLinkCommittedSets(Path.Combine(this.storage, "sets"));
        replica = new HexLinkPublicationReplica(receipts, _ => services.Excludes,
            (full, wire, hash, bytes, stop) => services.CompareAndReplace(full, wire, hash, bytes, stop), installReplicaBaseline, received);
    }

    public string PublicationSpool => Path.Combine(storage, "spool");

    // Call before starting the live engines. An interrupted transaction keeps its scope until replay.
    public async Task Restore(CancellationToken cancel)
    {
        replica.Restore(set => services.Workspace.Folders.FirstOrDefault(folder => folder.Name == set.Folder));
        foreach (var folder in services.Workspace.Folders)
        {
            if (Pinned(folder) is not { } pin || pin.DeviceId != services.Device.Id)
                continue;
            if (!HasOwnerStore(folder, pin.Epoch))
                continue;
            HexLinkPublishJournal owner = await Owner(folder, pin.Epoch, cancel);
            if (await owner.PendingProjection(cancel) is not { } id)
                continue;
            Fence(folder).HoldRecovery(id, HexLinkFenceKind.Publication);
            try { await owner.RecoverCurrent(cancel); }
            catch (HexLinkException error) when (error.Code is HexLinkErrors.BUSY or HexLinkErrors.CONFLICT)
            {
                services.Audit("publish-recovery", services.Device, $"{folder.Name} {id:N}: {error.Message}");
            }
        }
    }

    public Task<HexLinkDraftPresence> DraftPresence(string name, long epoch, CancellationToken cancel)
    {
        HexLinkFolder folder = Folder(name);
        RequireEpoch(folder, epoch);
        return draftPresence?.Invoke(folder, cancel) ?? Task.FromResult(new HexLinkDraftPresence(0, []));
    }

    public void CheckPublication(HexLinkDevice device, HexLinkPublicationRequest request)
    {
        HexLinkFolder folder = Folder(request.Folder);
        HexLinkAuthority pin = RequireEpoch(folder, request.Epoch);
        string authority = request.Replica ? device.Id : services.Device.Id;
        if (!string.Equals(pin.DeviceId, authority, StringComparison.OrdinalIgnoreCase))
            throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "This connection is not the pinned folder authority.");
    }

    public async Task ReceivePublication(HexLinkDevice device, HexLinkPublicationRequest request, HexLinkCommittedSet set,
        IReadOnlyList<HexLinkPublishEdit> edits, CancellationToken cancel)
    {
        CheckPublication(device, request);
        HexLinkFolder folder = Folder(request.Folder);
        if (request.Replica)
        {
            await replica.Receive(folder, set, edits, cancel);
            return;
        }

        if (set.Scope.Length != 0 || set.Files.Any(entry => entry.Executable is not null))
            throw HexLinkException.Invalid("Private publication submits whole-folder content edits.");
        await Submit(folder, request.Epoch, request.Id, edits, request.Bypass, device, cancel);
    }

    public async Task<JsonObject?> PublicationReceipt(HexLinkDevice device, HexLinkPublicationRequest request, CancellationToken cancel)
    {
        CheckPublication(device, request);
        if (request.Replica)
            return receipts.Receipt(request.Id, request.Folder, request.Epoch) is { } ack
                ? new JsonObject { ["id"] = ack.Id.ToString("N"), ["digest"] = ack.Digest } : null;

        HexLinkPublishJournal owner = await Owner(Folder(request.Folder), request.Epoch, cancel);
        return await owner.Recover(request.Id, cancel) is { } receipt ? ReceiptJson(receipt) : null;
    }

    public HexLinkDraftRoute? Route(HexLinkFolder folder, HexLinkPeer? peer)
    {
        if (Pinned(folder) is not { } pin)
            return null;
        if (pin.DeviceId == services.Device.Id)
            return new HexLinkDraftRoute(stop => Snapshot(folder, stop),
                async (id, stop) => await (await Owner(folder, pin.Epoch, stop)).Recover(id, stop),
                (id, edits, bypass, stop) => Submit(folder, pin.Epoch, id, edits, bypass, services.Device, stop));
        if (peer?.Device.Id != pin.DeviceId || peer.IsClosed || !peer.Session.SupportsBulk || !HexLinkAllowed.Has(peer.Granted, HexLinkRights.Edit))
            return null;

        async Task<HexLinkPublishReceipt?> Status(Guid id, CancellationToken stop) =>
            ReceiptOf(await peer.PublicationStatus(new(id, folder.Name, pin.Epoch, false, false), stop), id);
        return new HexLinkDraftRoute(stop => peer.Manifest(folder.Name, stop), Status, async (id, edits, bypass, stop) =>
        {
            await peer.SendPublication(Descriptor(folder, pin.Epoch, id, edits), edits, false, bypass, stop);
            return await Status(id, stop) ?? throw new IOException("The authority did not return the publication receipt.");
        }, async (path, stop) => (await peer.Read(path, stop)).Bytes);
    }

    public IReadOnlyList<HexLinkCommittedSet> Pending(string peer) => receipts.Pending(peer);

    public Task Deliver(HexLinkPeer peer, CancellationToken cancel) =>
        receipts.Deliver(peer.Device.Id, async (set, stop) =>
        {
            HexLinkFolder folder = Folder(set.Folder);
            if (RequireEpoch(folder, set.Epoch).DeviceId != services.Device.Id)
                throw new HexLinkException(HexLinkErrors.CONFLICT, "The authority changed before delivery.");
            var request = new HexLinkPublicationRequest(set.Id, set.Folder, set.Epoch, true, false);
            JsonObject? answer = await peer.PublicationStatus(request, stop);
            if (answer is null)
            {
                HexLinkPublishJournal owner = await Owner(folder, set.Epoch, stop);
                var edits = await owner.CommittedEdits(set.Id, stop);
                await peer.SendPublication(set, edits, true, false, stop);
                answer = await peer.PublicationStatus(request, stop);
            }
            string? digest = HexLinkData.String(answer, "digest", 65);
            if (HexLinkData.String(answer, "id", 33) != set.Id.ToString("N") || digest is not { Length: 64 } ||
                !digest.All(char.IsAsciiHexDigit) || !receipts.MatchesReceipt(set, new(set.Id, digest)))
                throw HexLinkException.Invalid("The replica did not acknowledge this publication.");
            await using var lease = await Fence(folder).EnterExclusive(HexLinkFenceKind.Publication, stop);
            RequireEpoch(folder, set.Epoch);
            await acknowledgeLive(folder, set, peer.Device.Id, lease.Stop);
            return new HexLinkCommittedAck(set.Id, digest);
        }, cancel, set => releaseLive?.Invoke(Folder(set.Folder), set, peer.Device.Id), CanDeliver);

    private bool CanDeliver(HexLinkCommittedSet set) =>
        services.Workspace.Folders.FirstOrDefault(folder => folder.Name == set.Folder) is { } folder &&
        Pinned(folder) is { } pin && pin.Epoch == set.Epoch && pin.DeviceId == services.Device.Id;

    private async Task<HexLinkPublishReceipt> Submit(HexLinkFolder folder, long epoch, Guid id,
        IReadOnlyList<HexLinkPublishEdit> edits, bool bypass, HexLinkDevice device, CancellationToken cancel)
    {
        if (RequireEpoch(folder, epoch).DeviceId != services.Device.Id)
            throw new HexLinkException(HexLinkErrors.CONFLICT, "This device is no longer the folder authority.");
        if (bypass)
            services.Audit("publish-anyway", device, $"{folder.Name} {id:N}");
        var receipt = await (await Owner(folder, epoch, cancel)).Publish(id, edits, cancel, device.Name);
        services.Audit("publish", device, $"{folder.Name} {id:N}, {receipt.Files} files");
        return receipt;
    }

    private async Task<HexLinkPublishJournal> Owner(HexLinkFolder folder, long epoch, CancellationToken cancel)
    {
        string data = OwnerDirectory(folder, epoch);
        Task<HexLinkPublishJournal> opening = owners.GetOrAdd(data, _ => OpenOwner(folder, epoch, data));
        try { return await opening.WaitAsync(cancel); }
        catch when (opening.IsFaulted) { owners.TryRemove(new KeyValuePair<string, Task<HexLinkPublishJournal>>(data, opening)); throw; }
    }

    private string OwnerDirectory(HexLinkFolder folder, long epoch)
    {
        string root = Path.GetFullPath(folder.Path);
        if (PlatformPaths.Comparison != StringComparison.Ordinal)
            root = root.ToUpperInvariant();
        string key = HexLinkFiles.Hash(Encoding.UTF8.GetBytes(root + "\n" + epoch.ToString(System.Globalization.CultureInfo.InvariantCulture)))[..24];
        return Path.Combine(storage, "owner", key);
    }

    private bool HasOwnerStore(HexLinkFolder folder, long epoch)
    {
        try { File.GetAttributes(Path.Combine(OwnerDirectory(folder, epoch), "git", "HEAD")); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private async Task<HexLinkPublishJournal> OpenOwner(HexLinkFolder folder, long epoch, string data)
    {
        var fence = Fence(folder);
        var objects = await HexLinkWorktreeGit.Open(Path.Combine(data, "git"), CancellationToken.None);
        string Resolve(string wire) => HexLinkFiles.Resolve([folder], wire, services.Excludes);
        var target = new HexLinkPublishTarget(async (id, stop) =>
            {
                var lease = await fence.EnterExclusive(HexLinkFenceKind.Publication, stop, recovery: id);
                try
                {
                    if (RequireEpoch(folder, epoch).DeviceId != services.Device.Id)
                        throw new HexLinkException(HexLinkErrors.CONFLICT, "The authority changed before projection.");
                    return lease;
                }
                catch
                {
                    await lease.DisposeAsync();
                    throw;
                }
            },
            (path, stop) => HexLinkPublicationReplica.CurrentHash(Resolve(path), stop),
            async (edit, stop) => await services.CompareAndReplace(Resolve(edit.Path), edit.Path, edit.Base, edit.Bytes, stop),
            async (receipt, edits, stop) =>
            {
                HexLinkCommittedSet set = Descriptor(folder, epoch, receipt.Id, edits) with { Author = receipt.Author };
                string[] peers = [.. recipients(folder.Name).Where(id => id != services.Device.Id).Distinct(StringComparer.OrdinalIgnoreCase)];
                var pending = receipts.Queue(set, peers);
                await deferLive(folder, set, pending, stop);
                queued?.Invoke(folder);
            },
            (id, pending) =>
            {
                if (pending)
                    fence.HoldRecovery(id, HexLinkFenceKind.Publication);
                else
                    fence.ReleaseRecovery(id);
            });
        return new HexLinkPublishJournal(data, objects, target);
    }

    private async Task<HexLinkManifest> Snapshot(HexLinkFolder folder, CancellationToken cancel)
    {
        await using var lease = await Fence(folder).EnterLive(cancel);
        return await Task.Run(new HexLinkTree(folder, () => services.Excludes).Manifest, lease.Stop);
    }

    private HexLinkPublicationFence Fence(HexLinkFolder folder) =>
        HexLinkPublicationFence.ForRoot(HexLinkFiles.Resolve([folder], folder.Name, services.Excludes));

    private HexLinkFolder Folder(string name) =>
        services.Workspace.Folders.FirstOrDefault(folder => folder.Name == name)
        ?? throw HexLinkException.NotFound("No local checkout is bound to the publication folder.");

    private HexLinkAuthority? Pinned(HexLinkFolder folder) => services.FolderAuthorities?.Of(folder) ?? folder.Authority;

    private HexLinkAuthority RequireEpoch(HexLinkFolder folder, long epoch) =>
        Pinned(folder) is { } pin && pin.Epoch == epoch ? pin
        : throw new HexLinkException(HexLinkErrors.CONFLICT, "The folder authority epoch changed.");

    private static HexLinkCommittedSet Descriptor(HexLinkFolder folder, long epoch, Guid id, IReadOnlyList<HexLinkPublishEdit> edits) =>
        new(id, folder.Name, string.Empty, epoch, HexLinkFenceKind.Publication,
            [.. edits.Select(edit => new HexLinkCommittedFile(edit.Path, edit.Base, edit.Bytes is null ? null : HexLinkFiles.Hash(edit.Bytes)))]);

    private static JsonObject ReceiptJson(HexLinkPublishReceipt receipt) =>
        new() { ["id"] = receipt.Id.ToString("N"), ["commit"] = receipt.Commit, ["files"] = receipt.Files, ["author"] = receipt.Author };

    private static HexLinkPublishReceipt? ReceiptOf(JsonObject? answer, Guid id)
    {
        if (answer is null)
            return null;
        if (HexLinkData.String(answer, "id", 33) != id.ToString("N") ||
            HexLinkData.String(answer, "commit", 65) is not { } commit || !HexLinkGit.IsCommitId(commit) ||
            HexLinkData.Int(answer, "files") is not int files || files <= 0)
            throw HexLinkException.Invalid("The authority returned an invalid publication receipt.");
        return new(id, commit, files, HexLinkNames.Clean(HexLinkData.String(answer, "author", 80)));
    }
}
