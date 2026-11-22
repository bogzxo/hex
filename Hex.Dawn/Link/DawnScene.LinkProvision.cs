using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Horizon.Core.Threading;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private const string LINK_PROVISION_BINDINGS = "provisioned.json";

    private sealed record ProvisionedFolder(string DeviceId, HexLinkFolder Folder);

    internal async Task<string?> LinkGetWorkspace(string deviceId, string destination, IProgress<HexLinkProvisionProgress>? progress = null,
        CancellationToken cancel = default, Action<string>? notes = null)
    {
        if (link?.PeerOf(deviceId) is not { } peer || peer.IsClosed)
            return Said("link.sync_not_linked", ("device", deviceId));
        if (!HexLinkAllowed.Has(peer.Granted, HexLinkRights.View))
            return Said("link.provision.needs_view", ("device", peer.Device.Name));

        await linkProvisioning.WaitAsync(cancel);
        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
            if (LinkSyncs.Any(sync => LinkSyncOf(sync.DeviceId, sync.Folder) is { } live
                && sync.State is HexLinkSyncState.Idle or HexLinkSyncState.Syncing or HexLinkSyncState.Comparing or HexLinkSyncState.PullOnly
                && (PlatformPaths.IsUnder(root, live.LocalFolder.Path) || PlatformPaths.IsUnder(live.LocalFolder.Path, root))))
                return Said("link.provision.other_sync", ("folder", root));

            string transferKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId + "\n" + root)));
            string journal = Path.Combine(linkData, "provision", transferKey + ".json");
            HexLinkProvisionResult got = await Task.Run(() => HexLinkProvision.Get(peer, root, journal, progress, cancel), cancel);
            IReadOnlyList<HexLinkFolder> folders = got.Folders;
            cancel.ThrowIfCancellationRequested();
            if (got.Skipped.Count > 0)
            {
                string skipped = ProvisionSkipped(got.Skipped);
                if (notes is not null)
                    notes(skipped);
                else
                    PostLink(() => Say(skipped));
            }

            LinkBindProvisionedFolders(peer, folders);
            LinkKeepProvisionedFolders(deviceId, folders);
            await OnEditorForLink(() =>
            {
                foreach (HexLinkFolder folder in folders)
                {
                    if (!explorer.workspace.Folders.Contains(folder.Path, PlatformPaths.Comparer))
                        explorer.AddFolder(folder.Path);
                }
                LinkLookAtWorkspace(force: true);
                return true;
            });

            await LinkEnsureWorktreePublication();
            progress?.Report(new HexLinkProvisionProgress(HexLinkProvisionPhase.Sync, 0, 0, Skipped: got.Skipped));
            foreach (HexLinkFolder folder in folders)
                LinkSyncRemember(deviceId, folder.Name, on: true);
            foreach (HexLinkFolder folder in folders)
            {
                cancel.ThrowIfCancellationRequested();
                if (await LinkStartProvisionedSync(peer, folder.Name, cancel) is { } why)
                    return why;
            }

            HexLinkGit.TryDelete(journal);
            progress?.Report(new HexLinkProvisionProgress(HexLinkProvisionPhase.Complete, 0, 0, Skipped: got.Skipped));
            return null;
        }
        catch (Exception e) when (e is HexLinkException or IOException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or JsonException)
        {
            return e.Message;
        }
        finally
        {
            linkProvisioning.Release();
        }
    }

    private static string ProvisionSkipped(IReadOnlyList<HexLinkProvisionSkip> skipped) =>
        string.Join('\n', skipped.Select(entry => Said("link.provision.skipped_repository", ("path", entry.Path), ("why", entry.Reason))));

    internal async Task<string?> LinkShareWorkspace(string deviceId, CancellationToken cancel = default)
    {
        if (link?.PeerOf(deviceId) is not { } peer || peer.IsClosed)
            return Said("link.sync_not_linked", ("device", deviceId));
        if (linkServices is not { } services || services.Workspace is not { Folders.Count: > 0 } workspace)
            return Said("link.provision.nothing_to_share");
        if (!HexLinkAllowed.Has(peer.Session.Connection.Rights, HexLinkRights.View))
            return Said("link.provision.grant_view", ("device", peer.Device.Name));

        try
        {
            HexLinkWorkspace exporting = LinkProvisionExport(null);
            await LinkEnsureWorktreePublication();
            await peer.ShareProvision(exporting.Name, cancel);
            return null;
        }
        catch (HexLinkException e)
        {
            return e.Message;
        }
    }

    internal void LinkGetWorkspaceCommand(string deviceId)
    {
        if (link?.PeerOf(deviceId) is not { } peer)
            return;
        string draft = LinkDefaultProvisionDestination(peer);
        AskText(Said("link.provision.get_title", ("device", peer.Device.Name)), Said("link.provision.destination"), draft,
            Said("link.provision.get_button"), selected => LinkGetWorkspaceInto(deviceId, peer.Device.Name, selected.Trim()));
    }

    internal void LinkShareWorkspaceCommand(string deviceId)
    {
        string device = link?.PeerOf(deviceId)?.Device.Name ?? deviceId;
        Context.Work.Start($"link-offer-workspace-{deviceId}", Said("link.provision.share_title", ("device", device)),
            async work =>
            {
                if (await LinkShareWorkspace(deviceId, work.Token) is { } why)
                    throw new InvalidOperationException(why);
            },
            done =>
            {
                if (done.State == WorkState.Failed)
                    Say(Said("link.provision.failed", ("why", done.Error ?? string.Empty)), error: true);
                else if (done.State == WorkState.Succeeded)
                    Say(Said("link.provision.offer_sent", ("device", device)));
            });
    }

    private void LinkWorkspaceOffered(HexLinkDevice device, string workspace) => QueueLinkWorkspaceOffer(device, workspace);

    private List<(string Text, Action Picked)> LinkProvisionPaletteCommands()
    {
        var commands = new List<(string Text, Action Picked)>();
        foreach (HexLinkPeer peer in LinkPeers)
        {
            if (HexLinkAllowed.Has(peer.Granted, HexLinkRights.View) && peer.Workspace.Folders.Count > 0)
                commands.Add((Said("link.provision.get_title", ("device", peer.Device.Name)), () => LinkGetWorkspaceCommand(peer.Device.Id)));
            if (HexLinkAllowed.Has(peer.Session.Connection.Rights, HexLinkRights.View) && linkServices?.Workspace.Folders.Count > 0)
                commands.Add((Said("link.provision.share_title", ("device", peer.Device.Name)), () => LinkShareWorkspaceCommand(peer.Device.Id)));
        }
        return commands;
    }

    private static string LinkDefaultProvisionDestination(HexLinkPeer peer)
    {
        string name = peer.Workspace.Name.Length > 0 ? peer.Workspace.Name : peer.Workspace.Folders.FirstOrDefault()?.Name ?? "workspace";
        string folder = new string([.. name.Take(64).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-')]).Trim('-');
        if (folder.Length == 0)
            folder = "workspace";

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string root = Path.Combine(home, "source", "repos");
        try { return HexLinkProvision.Resolve(root, folder); }
        catch (HexLinkException) { return Path.Combine(root, "workspace-" + folder); }
    }

    private HexLinkFolder? LinkProvisionedLocal(string deviceId, string name) =>
        LinkReadProvisionedFolders().FirstOrDefault(binding => binding.DeviceId == deviceId
            && PlatformPaths.Comparer.Equals(binding.Folder.Name, name) && Directory.Exists(binding.Folder.Path))?.Folder;

    // Older Get saved the authenticated source and local alias before it saved any folder authority.
    private async Task LinkRecoverProvisionedAuthority(HexLinkPeer peer)
    {
        if (linkServices is not { } services)
            return;
        var saved = LinkReadProvisionedFolders();
        var cancel = peer.Session.Closing;
        foreach (HexLinkFolder folder in services.Workspace.Folders)
        {
            if (folder.Authority is not null || !saved.Any(binding => binding.DeviceId == peer.Device.Id &&
                    PlatformPaths.Same(binding.Folder.Path, folder.Path) && PlatformPaths.Comparer.Equals(binding.Folder.Name, folder.Name)) ||
                saved.Any(binding => binding.DeviceId != peer.Device.Id && PlatformPaths.Same(binding.Folder.Path, folder.Path)))
                continue;
            if (peer.Workspace.Folders.FirstOrDefault(remote => PlatformPaths.Comparer.Equals(remote.Name, folder.Name)) is not { } exported ||
                exported.FolderAuthorityId is { } owner && owner != peer.Device.Id)
                continue;
            try
            {
                HexLinkAuthority? pin = exported.Authority ?? await peer.ProvisionSyncAuthority(folder.Name, cancel).ConfigureAwait(false);
                if (pin?.DeviceId != peer.Device.Id)
                    continue;
                if (exported.Authority is null)
                    await peer.RefreshWorkspace(cancel).ConfigureAwait(false);
                if (services != linkServices || peer.IsClosed || !HexLinkAllowed.Has(peer.Granted, HexLinkRights.View) ||
                    !peer.Workspace.Folders.Any(remote => PlatformPaths.Comparer.Equals(remote.Name, folder.Name) && remote.Authority == pin))
                    return;
                if (LinkSyncOf(peer.Device.Id, folder.Name) is { } legacy && !legacy.BoundPullMirror)
                    await LinkDrainBoundEngine(peer.Device.Id, folder.Name, cancel).ConfigureAwait(false);
                services.FolderAuthorities.Bind(folder, pin, HexLinkAuthorityProvenance.Provisioned);
                link?.WorkspaceMoved();
                services.Audit("authority migration", peer.Device, $"Recovered the Get binding for {folder.Name}, epoch {pin.Epoch}.");
            }
            catch (Exception error) when (error is HexLinkException or IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                services.Audit("authority migration", peer.Device, $"{folder.Name}: {error.Message}");
            }
        }
    }

    private List<ProvisionedFolder> LinkReadProvisionedFolders()
    {
        if (linkKeys.Length == 0)
            return [];
        string file = Path.Combine(linkKeys, LINK_PROVISION_BINDINGS);
        try
        {
            if (!File.Exists(file) || JsonNode.Parse(File.ReadAllBytes(file)) is not JsonObject saved)
                return [];
            var found = new List<ProvisionedFolder>();
            foreach (JsonObject each in HexLinkData.Objects(saved, "folders", 500))
            {
                string? device = HexLinkData.String(each, "device", 200), name = HexLinkData.String(each, "name", 200);
                string? path = HexLinkData.String(each, "path", 32768);
                if (device is { Length: > 0 } && name is { Length: > 0 } && path is { Length: > 0 } && Path.IsPathFullyQualified(path))
                    found.Add(new ProvisionedFolder(device, new HexLinkFolder(name, path)));
            }
            return found;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.WriteLine($"[Hex] Link couldn't read provisioned folders: {e.Message}");
            return [];
        }
    }

    private void LinkKeepProvisionedFolders(string deviceId, IReadOnlyList<HexLinkFolder> folders)
    {
        List<ProvisionedFolder> bindings = LinkReadProvisionedFolders();
        bindings.RemoveAll(binding => binding.DeviceId == deviceId
            && folders.Any(folder => PlatformPaths.Comparer.Equals(folder.Name, binding.Folder.Name)));
        bindings.AddRange(folders.Select(folder => new ProvisionedFolder(deviceId, folder)));
        var saved = new JsonObject
        {
            ["folders"] = new JsonArray([.. bindings.Select(binding => (JsonNode)new JsonObject
            {
                ["device"] = binding.DeviceId, ["name"] = binding.Folder.Name, ["path"] = binding.Folder.Path
            })])
        };
        Directory.CreateDirectory(linkKeys);
        HexLinkFiles.WriteAtomic(Path.Combine(linkKeys, LINK_PROVISION_BINDINGS), Encoding.UTF8.GetBytes(saved.ToJsonString()));
    }

    // Keep the host's wire names when the person picked a different name for the destination directory.
    private HexLinkWorkspace LinkDescribeWorkspace(string name, string[] folders, Func<string, HexLinkRepository?> repositoryOf)
    {
        HexLinkWorkspace workspace = HexLinkWorkspaces.Describe(name, folders, repositoryOf);
        List<ProvisionedFolder> bindings = LinkReadProvisionedFolders();
        if (bindings.Count == 0)
            return workspace;

        var aliases = workspace.Folders.Select(folder => bindings.LastOrDefault(binding => PlatformPaths.Same(binding.Folder.Path, folder.Path))?.Folder).ToArray();
        var reserved = aliases.Where(alias => alias is not null).Select(alias => alias!.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renamed = new List<HexLinkFolder>();
        for (int i = 0; i < workspace.Folders.Count; i++)
        {
            HexLinkFolder folder = workspace.Folders[i];
            string basis = aliases[i]?.Name ?? folder.Name;
            string candidate = basis;
            for (int suffix = 2; used.Contains(candidate) || (aliases[i]?.Name != candidate && reserved.Contains(candidate)); suffix++)
                candidate = $"{basis}-{suffix}";
            used.Add(candidate);
            renamed.Add(folder with { Name = candidate });
        }
        return workspace with { Folders = renamed };
    }
}
