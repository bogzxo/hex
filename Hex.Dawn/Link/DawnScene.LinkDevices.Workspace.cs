namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void LinkWorkspaceAdvertised(HexLinkWorkspace previous, HexLinkWorkspace current)
    {
        if (!HexLinkPeer.SameWorkspace(previous, current))
        {
            linkDirty = true;
            link?.WorkspaceMoved();
        }
    }

    private async Task LinkPrepareSharedWorkspace(HexLinkPeer peer)
    {
        bool refreshed = await TryRefreshLinkWorkspace(peer).ConfigureAwait(false);
        PostLink(() =>
        {
            if (peer.IsClosed || LinkPeerOf(peer.Device.Id) != peer || !HexLinkAllowed.Has(peer.Granted, HexLinkRights.View))
                return;
            if (refreshed)
                LinkLearnAuthority(peer);
            LinkSyncResume(peer);
            if (refreshed && LinkAutoSyncOwner(peer.Session.Connection.Dialled, peer.Session.Connection.Rights, peer.Granted))
                LinkSyncStartSharedFolders(peer);
        });
    }

    private async Task<bool> TryRefreshLinkWorkspace(HexLinkPeer peer)
    {
        try
        {
            await peer.RefreshWorkspace(linkCancel?.Token ?? CancellationToken.None).ConfigureAwait(false);
            return HexLinkAllowed.Has(peer.Granted, HexLinkRights.View);
        }
        catch (Exception error) when (error is HexLinkException or IOException or OperationCanceledException or TimeoutException or ObjectDisposedException)
        {
            return false;
        }
    }
}
