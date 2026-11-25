using System.Security.Cryptography;
using System.Text;

using Horizon.Core.Threading;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private HashSet<HexLinkSync> linkRepositoryRefreshes { get => LazyInitializer.EnsureInitialized(ref field, static () => []); }

    private HexLinkFolder? LinkMissingRepositoryFolder(string deviceId, string folder) =>
        LinkProvisionedLocal(deviceId, folder) ?? linkServices?.Workspace.Folders.FirstOrDefault(each => each.Name == folder);

    internal async Task<string?> LinkGetMissingRepository(string deviceId, string folder, string path,
        IProgress<HexLinkProvisionProgress>? progress = null, CancellationToken cancel = default)
    {
        if (link?.PeerOf(deviceId) is not { IsClosed: false } peer)
            return Said("link.sync_not_linked", ("device", deviceId));
        if (!HexLinkAllowed.Has(peer.Granted, HexLinkRights.View))
            return Said("link.provision.needs_view", ("device", peer.Device.Name));
        if (LinkMissingRepositoryFolder(deviceId, folder) is not { } local)
            return Said("link.sync_no_folder_here", ("folder", folder));

        await linkProvisioning.WaitAsync(cancel);
        try
        {
            string target = HexLinkProvision.Resolve(local.Path, path);
            if (LinkSyncs.Any(sync => sync.DeviceId != deviceId && LinkSyncOf(sync.DeviceId, sync.Folder) is { } running
                && sync.State is HexLinkSyncState.Idle or HexLinkSyncState.Syncing or HexLinkSyncState.Comparing or HexLinkSyncState.PullOnly
                && PlatformPaths.IsUnder(target, running.LocalFolder.Path)))
                return Said("link.provision.other_sync", ("folder", folder));

            if (await OnEditorForLink(() => documents.Any(tab => tab.Path is { } file
                && PlatformPaths.IsUnder(file, target) && HasChanges(tab))).WaitAsync(cancel))
                return Said("link.provision.repository_unsaved", ("path", path));

            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId + "\n" + target)));
            string journal = Path.Combine(linkData, "provision", key + ".json");
            await Task.Run(() => HexLinkProvision.GetMissingRepository(peer, local, path, journal, progress, cancel), cancel);
            HexLinkGit.TryDelete(journal);

            await LinkRefreshRepositorySync(peer, folder, path, fromPeer: false);
            await peer.TellRepositoryReady(folder, path, cancel);
            return null;
        }
        catch (Exception e) when (e is HexLinkException or IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            return e.Message;
        }
        finally
        {
            linkProvisioning.Release();
        }
    }

    private void LinkRepositoryReady(HexLinkDevice device, string folder, string path)
    {
        if (link?.PeerOf(device.Id) is { IsClosed: false } peer)
            _ = LinkRefreshRepositorySync(peer, folder, path, fromPeer: true);
    }

    private async Task LinkRefreshRepositorySync(HexLinkPeer peer, string folder, string path, bool fromPeer)
    {
        string deviceId = peer.Device.Id;
        if (LinkSyncOf(deviceId, folder) is not { } sync
            || !(fromPeer ? sync.MissingRepositories.There : sync.MissingRepositories.Here).Contains(path, StringComparer.Ordinal))
            return;

        lock (linkSyncGate)
        {
            if (!linkRepositoryRefreshes.Add(sync))
                return;
        }

        try
        {
            if (fromPeer)
            {
                HexLinkManifest said = await peer.Manifest(folder);
                if (said.NestedHeads?.GetValueOrDefault(path) is null)
                    return;
            }

            Task stopped;
            lock (linkSyncGate)
            {
                if (LinkSyncOf(deviceId, folder) != sync || sync.State is not (HexLinkSyncState.Idle or HexLinkSyncState.Syncing or HexLinkSyncState.PullOnly))
                    return;
                stopped = sync.StopForRepositoryRefresh();
            }
            await stopped;
            Task? restarting;
            lock (linkSyncGate)
                restarting = LinkSyncOf(deviceId, folder) == sync ? LinkSyncStart(deviceId, folder, remember: false) : null;
            if (restarting is not null)
                await restarting;
        }
        catch (Exception e) when (e is HexLinkException or IOException or InvalidOperationException or OperationCanceledException)
        {
            PostLink(() => Say(Said("link.sync_not_resumed", ("folder", folder), ("device", peer.Device.Name), ("why", e.Message)), error: true));
        }
        finally
        {
            lock (linkSyncGate)
                linkRepositoryRefreshes.Remove(sync);
        }
    }

    private void LinkMissingRepositoryRow(StackPanel panel, HexLinkPeer peer, string folder, HexLinkSyncState state)
    {
        if (!HexLinkAllowed.Has(peer.Granted, HexLinkRights.View) || LinkMissingRepositoryFolder(peer.Device.Id, folder) is null)
            return;
        HexLinkMissingRepositories? missing = LinkSyncOf(peer.Device.Id, folder)?.MissingRepositories;
        if (missing is { There.Count: > 0 })
            panel.Add(new Label
            {
                Text = UIRichText.Literal(Said("link.provision.repositories_missing_there", ("device", peer.Device.Name), ("paths", string.Join(", ", missing.There.Take(8))))),
                TextScale = LINK_SMALL, Dim = true, Wrap = true
            });
        if (missing is { Here.Count: > 0 })
            panel.Add(LinkButton(Said("link.provision.get_repository"), "cloud-download", () => LinkPickMissingRepository(peer, folder)));
    }

    private void LinkPickMissingRepository(HexLinkPeer peer, string folder)
    {
        if (LinkMissingRepositoryFolder(peer.Device.Id, folder) is not { } local)
            return;
        IReadOnlyList<string>? paths = null;
        string title = Said("link.provision.get_repository"), id = peer.Device.Id;
        Say(Said("link.provision.get_repository_started", ("device", LinkDeviceLabel(peer.Device))));
        Context.Work.Start($"link-missing-check-{id}-{folder}", title, async work =>
        {
            HexLinkManifest remote = await peer.Manifest(folder, work.Token);
            HexLinkManifest here = await Task.Run(new HexLinkTree(local).Manifest, work.Token);
            paths = HexLinkMissingRepositories.Compare(here, remote).Here;
        }, done =>
        {
            if (done.State == WorkState.Failed)
                Say(Said("link.provision.failed", ("why", done.Error ?? string.Empty)), error: true);
            else if (paths is { Count: > 0 })
                ChooseFrom(title, Said("link.provision.get_repository_pick", ("device", peer.Device.Name)),
                    [.. paths.Select(path => (path, (Action)(() => LinkGetMissingRepositoryCommand(id, folder, path))))]);
            else if (done.State == WorkState.Succeeded)
                Say(Said("link.provision.no_missing_repositories"));

        });
    }

    private void LinkGetMissingRepositoryCommand(string deviceId, string folder, string path)
    {
        Say(Said("link.provision.get_repository_working", ("path", path), ("device", LinkNameOf(deviceId))));
        Context.Work.Start($"link-get-repository-{deviceId}-{folder}", Said("link.provision.get_repository_title", ("path", path)), async work =>
        {
            if (await LinkGetMissingRepository(deviceId, folder, path, new Progress<HexLinkProvisionProgress>(update => ReportProvision(work, update)), work.Token) is { } why)
                throw new InvalidOperationException(why);
        }, done =>
        {
            if (done.State == WorkState.Failed)
                Say(Said("link.provision.failed", ("why", done.Error ?? string.Empty)), error: true);
            else if (done.State == WorkState.Succeeded)
                Say(Said("link.provision.repository_ready", ("path", path)));
        });
    }
}
