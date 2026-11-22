namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    internal IReadOnlyList<LinkSyncShown> LinkRemoteSyncs(HexLinkPeer peer) =>
        [.. peer.RemoteSyncReport.Select(status => new LinkSyncShown(peer.Device.Id, peer.Device.Name, status.Folder, status.State,
            $"{status.Folder} managed by {peer.Device.Name}: {status.Received} sent, {status.Sent} received, " +
            $"{status.Conflicts} conflicts, {status.Skipped} skipped" +
                (status.Held > 0 ? $", {status.Held} held on {peer.Device.Name}" : ""),
            CanSendToAuthority(peer) ? status.Why : "Changes saved here are not sent: view only.",
            status.Received, status.Sent, status.Conflicts, status.Skipped, !CanSendToAuthority(peer),
            status.FilesDone, status.FilesTotal, status.BytesDone, status.BytesTotal, status.Queued, status.Detail, status.Held))];

    private static bool CanSendToAuthority(HexLinkPeer peer) =>
        HexLinkAllowed.Has(peer.Granted, HexLinkRights.Edit);

    private void LinkReportSync(string deviceId)
    {
        if (link?.PeerOf(deviceId) is { } peer)
            peer.QueueSyncReport(() => LinkMakeSyncReport(deviceId));
    }

    private IReadOnlyList<HexLinkSyncReport> LinkMakeSyncReport(string deviceId)
    {
        lock (linkSyncGate)
        {
            return [.. linkSyncs.Values.Where(entry => entry.DeviceId == deviceId && entry.Sync is { State: not HexLinkSyncState.Off })
                .OrderBy(entry => entry.Folder, PlatformPaths.Comparer).Take(HexLinkHost.MOST_TREES).Select(entry =>
                {
                    HexLinkSync sync = entry.Sync!;
                    var conflicts = sync.Conflicts;
                    var skipped = sync.Skipped;
                    return new HexLinkSyncReport(entry.Folder, sync.State, sync.Why, sync.Sent, sync.Received,
                        conflicts.Count, skipped.Count, sync.PullOnly, sync.FilesDone, sync.FilesTotal, sync.BytesDone, sync.BytesTotal,
                        sync.Queued, sync.GitFollowIssue ?? sync.Detail,
                        [.. conflicts.Take(HexLinkSyncReport.MAX_SAMPLES).Select(row => new HexLinkSyncSkipped(row.Path, row.Kind))],
                        [.. skipped.Take(HexLinkSyncReport.MAX_SAMPLES)], sync.Held);
                })];
        }
    }
}
