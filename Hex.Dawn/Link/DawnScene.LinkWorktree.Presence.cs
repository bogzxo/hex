using System.Globalization;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private Lock privatePresenceGate { get => LazyInitializer.EnsureInitialized(ref field, static () => new()); }
    private Dictionary<string, (long Until, Task<HexLinkDraftPresence> Scan)> privatePresence { get => LazyInitializer.EnsureInitialized(ref field, static () => new(PlatformPaths.Comparer)); }
    private Dictionary<(string Peer, string Folder), (long Until, HexLinkDraftPresence? Status)> remotePrivatePresence { get => LazyInitializer.EnsureInitialized(ref field, static () => []); }

    private async Task<HexLinkDraftPresence> ReadPrivateWorktreePresence(HexLinkFolder folder, CancellationToken cancel)
    {
        string full = Path.GetFullPath(folder.Path);
        Task<HexLinkDraftPresence> result;
        lock (privatePresenceGate)
        {
            long now = Environment.TickCount64;
            if (!privatePresence.TryGetValue(full, out var cached) || cached.Until <= now && cached.Scan.IsCompleted)
            {
                CancellationToken running = linkCancel?.Token ?? CancellationToken.None;
                cached = (now + 5000, Task.Run(() => ScanPrivateNames(full, running), running));
                privatePresence[full] = cached;
            }
            result = cached.Scan;
        }
        return await result.WaitAsync(cancel);
    }

    private async Task<HexLinkDraftPresence> ScanPrivateNames(string folder, CancellationToken cancel)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (HexLinkPrivateWorktree draft in linkPrivateDrafts.Values)
        {
            if (!PlatformPaths.Comparer.Equals(Path.GetFullPath(draft.SharedFolder.Path), folder))
                continue;
            HexLinkDraftStatus status = await draft.Status(cancel);
            paths.UnionWith(status.Paths);
        }
        return new(paths.Count, [.. paths.Order(StringComparer.Ordinal).Take(HexLinkDraftPresence.MostNames)]);
    }

    private void DrawLinkPrivateWorktreePresence(StackPanel holder, HexLinkPeer peer, string name)
    {
        if (linkServices is not { } services ||
            (LinkProvisionedLocal(peer.Device.Id, name) ?? services.Workspace.Folders.FirstOrDefault(folder => folder.Name == name)) is not { } local)
            return;
        if (HexLinkPublicationFence.ForRoot(local.Path).Status is { } fence)
        {
            string key = fence.Recovering ? "link.publication_recovering" : fence.Kind switch
            {
                HexLinkFenceKind.GitFollow => "link.publication_following",
                HexLinkFenceKind.BulkApply => "link.publication_pack",
                _ => "link.publication_applying"
            };
            holder.Add(LinkLine(Said(key)));
        }

        if (CurrentLinkPublication is null || !HexLinkAllowed.Has(peer.Granted, HexLinkRights.View) ||
            services.FolderAuthorities.Of(local) is not { } pin)
            return;

        var keyForPeer = (peer.Device.Id, name);
        long now = Environment.TickCount64;
        if (remotePrivatePresence.TryGetValue(keyForPeer, out var last) && last.Status is { Count: > 0 } status)
        {
            var line = holder.Add(LinkLine(Said("link.publication_unpublished",
                ("count", status.Count.ToString(CultureInfo.InvariantCulture)))));
            line.Tooltip = string.Join('\n', status.Paths);
        }
        if (last.Until > now)
            return;
        remotePrivatePresence[keyForPeer] = (long.MaxValue, last.Status);
        CancellationToken cancel = linkCancel?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            HexLinkDraftPresence? presence = null;
            try { presence = await peer.DraftPresence(name, pin.Epoch, cancel); }
            catch (Exception error) when (error is HexLinkException or IOException or OperationCanceledException) { }
            PostLink(() =>
            {
                remotePrivatePresence[keyForPeer] = (Environment.TickCount64 + 5000, presence);
                if (presence?.Count != last.Status?.Count || presence?.Paths.SequenceEqual(last.Status?.Paths ?? []) == false)
                    linkDirty = true;
            });
        });
    }
}
