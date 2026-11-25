using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    /// <summary>What a connected device's Pause / Resume entry in More does, from the syncs running with it and the folders paused.</summary>
    private HexLinkDevicePause LinkDevicePauseOf(HexLinkPeer peer)
    {
        string id = peer.Device.Id;
        IEnumerable<string> running = LinkSyncs
            .Where(sync => sync.DeviceId.Equals(id, StringComparison.OrdinalIgnoreCase) &&
                sync.State is not (HexLinkSyncState.Off or HexLinkSyncState.Failed))
            .Select(sync => sync.Folder);
        return HexLinkDevicePause.Of(running, LinkPausedSyncs(peer).Select(choice => choice.Folder));
    }

    /// <summary>Pauses every folder syncing with a device, or starts the paused ones again.</summary>
    private void LinkPauseDevice(string id, HexLinkDevicePause pause)
    {
        foreach (string folder in pause.Targets)
        {
            if (pause.CanPause)
                LinkSyncStop(id, folder);
            else
                StartLinkSyncFromWindow(id, folder, force: false);
        }

        linkWindowStale = true;
    }
}
