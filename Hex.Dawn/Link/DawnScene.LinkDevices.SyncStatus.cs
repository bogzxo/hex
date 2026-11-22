namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private (string Key, string Text, long SaidAt)? linkSyncStatusError;

    private void LinkSaySyncError(string deviceId, string deviceName, string folder, string? reason)
    {
        Say(Said("link.sync_failed", ("folder", folder), ("device", deviceName), ("why", reason ?? string.Empty)), error: true);
        linkSyncStatusError = (LinkSyncKey(deviceId, folder), messageItem.Text, saidAt);
    }

    private void LinkClearRecoveredSyncError(string deviceId, string folder, HexLinkSyncState state)
    {
        if (state is not (HexLinkSyncState.Syncing or HexLinkSyncState.Idle or HexLinkSyncState.PullOnly) ||
            linkSyncStatusError is not { } error || !error.Key.Equals(LinkSyncKey(deviceId, folder), StringComparison.OrdinalIgnoreCase))
            return;

        linkSyncStatusError = null;
        if (!saidError || error.SaidAt != saidAt || error.Text != messageItem.Text)
            return;
        saidError = false;
        messageItem.Text = string.Empty;
        messageItem.Color = null;
    }
}
