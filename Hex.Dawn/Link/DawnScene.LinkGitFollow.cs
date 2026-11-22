using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private Lock linkGitFollowGate { get => LazyInitializer.EnsureInitialized(ref field, static () => new()); }
    private volatile IReadOnlyDictionary<string, bool>? linkGitFollowChoices;

    private bool LinkGitFollowPolicy(HexLinkFolder folder, HexLinkDevice device) =>
        link?.PeerOf(device.Id) is { } peer && LinkGitFollowChoice(folder, peer, manual: false)();

    private Func<bool> LinkGitFollowChoice(HexLinkFolder folder, HexLinkPeer peer, bool manual)
    {
        string path = Path.GetFullPath(folder.Path);
        string? authority = linkServices?.FolderAuthorities.Of(folder)?.DeviceId;
        if (authority is not null && authority != linkServices?.Device.Id && authority != peer.Device.Id)
            return static () => false;
        var selected = LinkGitFollowSettings();
        bool defaultOn = authority is not null || manual
            || LinkProvisionedLocal(peer.Device.Id, folder.Name) is not null
            || LinkSyncChosen(peer.Device.Id).Contains(folder.Name, PlatformPaths.Comparer);
        return () => (linkGitFollowChoices ?? selected).GetValueOrDefault(path, defaultOn);
    }

    private IReadOnlyDictionary<string, bool> LinkGitFollowSettings()
    {
        lock (linkGitFollowGate)
        {
            if (linkGitFollowChoices is { } found)
                return found;
            var settings = new Dictionary<string, bool>(PlatformPaths.Comparer);
            try
            {
                string file = Path.Combine(linkData, "git-follow-folders.json");
                if (File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonObject json)
                    foreach (var (path, value) in json)
                        if (value is JsonValue item && item.TryGetValue(out bool enabled))
                            settings[path] = enabled;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                Console.WriteLine($"[Hex] Link couldn't read Git follow settings, {error.Message}");
            }
            return linkGitFollowChoices = settings;
        }
    }

    private void SetLinkGitFollow(string path, bool enabled)
    {
        lock (linkGitFollowGate)
        {
            var choices = new Dictionary<string, bool>(LinkGitFollowSettings(), PlatformPaths.Comparer)
            {
                [Path.GetFullPath(path)] = enabled
            };
            try
            {
                var json = new JsonObject(choices.Select(each => KeyValuePair.Create(each.Key, (JsonNode?)JsonValue.Create(each.Value))));
                Directory.CreateDirectory(linkData);
                HexLinkFiles.WriteAtomic(Path.Combine(linkData, "git-follow-folders.json"), Encoding.UTF8.GetBytes(json.ToJsonString()));
                linkGitFollowChoices = choices;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Say(error.Message, error: true);
            }
        }
    }

    private List<(string Text, Action Picked)> LinkGitFollowPaletteCommands()
    {
        List<(string, Action)> commands = [];
        var services = linkServices;
        if (services is null)
            return commands;
        foreach (HexLinkFolder folder in services.Workspace.Folders)
        {
            var peer = LinkPeers.FirstOrDefault(each => each.Workspace.Folders.Any(remote => remote.Name == folder.Name)
                && (folder.FolderAuthorityId is null || folder.FolderAuthorityId == services.Device.Id || folder.FolderAuthorityId == each.Device.Id));
            if (peer is null)
                continue;
            bool on = LinkGitFollowChoice(folder, peer, manual: false)();
            commands.Add((Said(on ? "link.git_follow.disable" : "link.git_follow.enable", ("folder", folder.Name)), () =>
            {
                SetLinkGitFollow(folder.Path, !on);
                foreach (LinkSyncShown sync in LinkSyncs.Where(sync => sync.Folder == folder.Name))
                    _ = LinkSyncStart(sync.DeviceId, sync.Folder, remember: false);
            }));
        }

        lock (linkSyncGate)
            foreach (var entry in linkSyncs.Values.Where(entry => entry.Sync?.GitFollowIssue is not null))
                commands.Add((Said("link.git_follow.align", ("folder", entry.Folder), ("device", entry.DeviceName)),
                    () => LinkAlignWorkspaceCommand(entry.DeviceId, entry.Folder)));
        return commands;
    }
}
