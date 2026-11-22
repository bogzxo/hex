namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    partial void LinkCanAutoShare(ref bool available) =>
        available = linkServices?.Workspace.Folders.Count > 0;

    partial void LinkDeviceProvisionActions(string deviceId, ref Action? share, ref Action? get)
    {
        share = () => LinkShareWorkspaceCommand(deviceId);
        get = () => LinkGetWorkspaceCommand(deviceId);
    }

    partial void LinkDeviceSyncProgress(LinkSyncShown sync, ref int filesDone, ref int filesTotal, ref long bytesDone,
        ref long bytesTotal, ref int queued, ref string? detail)
    {
        filesDone = sync.FilesDone;
        filesTotal = sync.FilesTotal;
        bytesDone = sync.BytesDone;
        bytesTotal = sync.BytesTotal;
        queued = sync.Queued;
        detail = sync.Detail;
    }

    partial void LinkDeviceRedial(string deviceId, ref LinkDeviceConnection? state)
    {
        if (LinkRedialState(deviceId) is { } reported)
            state = new LinkDeviceConnection(reported.Status == HexLinkRedialStatus.Connecting, reported.RetryAtUtc, reported.LastFailure);
    }

    partial void LinkDeviceIdentityRecovery(ref string? reason, ref string? label, ref Action? recovery)
    {
        if (LinkIdentityFailure is not { } failure)
            return;

        if (failure.Problem == HexLinkIdentityProblem.Unavailable)
        {
            reason = Said("link.devices_identity_unavailable") + " " + OneLine(failure.Reason, 140);
            label = Said("link.devices_identity_retry");
            recovery = () => LinkSwitch(true);
        }
        else
        {
            reason = Said("link.devices_identity_corrupt");
            label = Said("link.devices_identity_reset");
            recovery = AskResetLinkIdentity;
        }
    }

    private void AskResetLinkIdentity()
    {
        string confirm = Said("link.devices_identity_reset_confirm");
        Context.Windows.Ask(Said("link.devices_identity_reset_title"), Said("link.devices_identity_corrupt"),
            [Said("link.pair_cancel"), confirm], picked =>
            {
                if (picked != confirm)
                    return;
                if (ResetLinkIdentity(out _, out string? why))
                    Say(Said("link.devices_identity_reset_ok"));
                else
                    Say(Said("link.devices_identity_reset_failed", ("why", why ?? string.Empty)), error: true);
            }, risky: confirm);
    }
}
