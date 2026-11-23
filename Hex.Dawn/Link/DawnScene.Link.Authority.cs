using System.Text.Json.Nodes;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private HexMcpTool LinkAuthorityTool() => new(new McpTool("hex_link_authority", "Make this device the authority",
        "Move authority for a shared folder to this device. Requires Edit on the previous authority when connected. If it is permanently gone, the person must confirm locally.",
        McpSchema.Object([("folder", McpSchema.String("The name of the shared folder."))], "folder")),
        null, RunAway: LinkMakeAuthorityTool, NeedsEditor: false);

    private async Task<McpToolResult> LinkMakeAuthorityTool(JsonObject args, CancellationToken cancel)
    {
        try
        {
            string result = await LinkMakeAuthority(args.String("folder") ?? string.Empty, cancel);
            return McpToolResult.Text(result);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HexLinkException)
        {
            return McpToolResult.Error(error.Message);
        }
    }

    private void LinkMakeAuthorityCommand(string folder) => _ = Task.Run(async () =>
    {
        McpToolResult result = await LinkMakeAuthorityTool(new JsonObject { ["folder"] = folder }, linkCancel?.Token ?? CancellationToken.None);
        PostLink(() => Say(result.AllText(), error: result.IsError));
    });

    private async Task<string> LinkMakeAuthority(string name, CancellationToken cancel)
    {
        if (linkServices is not { } services || link is not { } connected)
            return Said("link.off");
        HexLinkFolder folder = services.Workspace.Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, name))
            ?? throw HexLinkException.NotFound(Said("link.sync_no_folder_here", ("folder", name)));
        string mine = services.Device.Id;
        if (services.FolderAuthorities.Of(folder) is not { } previous)
        {
            services.FolderAuthorities.Bind(folder, new HexLinkAuthority(mine, 1), HexLinkAuthorityProvenance.Explicit);
            linkAudit?.Write("authority-transfer", mine, $"{folder.Name} bound to {mine}, epoch 1");
            connected.WorkspaceMoved();
            LinkMoved();
            return Said("link.authority_changed", ("folder", folder.Name), ("epoch", "1"));
        }
        if (previous.DeviceId.Equals(mine, StringComparison.OrdinalIgnoreCase))
            return Said("link.authority_changed", ("folder", folder.Name), ("epoch", previous.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        if (connected.PeerOf(previous.DeviceId) is { IsClosed: false } old)
            await old.TransferAuthority(folder.Name, previous, cancel);
        else if (!await LinkConfirmAuthorityLost(folder.Name, cancel))
            return Said("keys.leave_it");

        HexLinkAuthority next = services.FolderAuthorities.Transfer(folder, previous, mine);
        linkAudit?.Write("authority-transfer", mine, $"{folder.Name}, {previous.DeviceId} -> {mine}, epoch {next.Epoch}");
        connected.WorkspaceMoved();
        PostLink(() => { linkDirty = true; LinkMoved(); });
        return Said("link.authority_changed", ("folder", folder.Name), ("epoch", next.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private async Task<bool> LinkConfirmAuthorityLost(string name, CancellationToken cancel)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await OnEditorForLink(() =>
        {
            UIDialog.Show(chrome, Said("link.authority_make"), Said("link.authority_confirm_lost", ("folder", name)),
                new DialogChoice(Said("link.authority_make"), () => done.TrySetResult(true), Risky: true),
                new DialogChoice(Said("keys.leave_it"), () => done.TrySetResult(false)));
            return true;
        }).WaitAsync(cancel);
        return await done.Task.WaitAsync(cancel);
    }

    // A folder kept in sync with a linked device from before authorities were pinned (restored at start, or set up with
    // Sync Folder) gets the pin a sync set up now would: the one the device advertises when it does, otherwise the one the
    // election gives (both Hexes work out the same owner), and the sync is started again for it. Nothing is pinned for a folder the
    // person stopped syncing or one the device holds no View right on.
    private void LinkPinSyncedFolders(HexLinkPeer peer, bool start = true)
    {
        if (linkServices is not { } services || link is not { } connected || peer.IsClosed || !HexLinkAllowed.Has(peer.Granted, HexLinkRights.View))
            return;
        foreach (string name in LinkSyncedWith(peer.Device.Id))
        {
            HexLinkFolder? local = services.Workspace.Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, name));
            HexLinkFolder? remote = peer.Workspace.Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, name));
            if (local is null || remote is null || services.FolderAuthorities.Of(local) is not null || LinkSyncOptedOut(peer.Device.Id, name))
                continue;
            HexLinkAuthority pin = remote.Authority ?? new HexLinkAuthority(
                LinkAutoSyncOwner(peer.Session.Connection.Dialled, peer.Session.Connection.Rights, peer.Granted) ? services.Device.Id : peer.Device.Id, 1);
            if (pin.DeviceId != services.Device.Id && pin.DeviceId != peer.Device.Id)
                continue;
            // Pinned for publication and claims, the sync stays the two-way one it was, as both devices pin it together. A
            // folder the device pinned already (a share, a folder provisioned as a replica) isn't two-way, nothing says it ran so
            if (remote.Authority is null && LinkProvisionedLocal(peer.Device.Id, name) is null)
                LinkSyncMarkTwoWay(peer.Device.Id, name);
            try
            {
                services.FolderAuthorities.Bind(local, pin, HexLinkAuthorityProvenance.Provisioned);
            }
            catch (HexLinkException error)
            {
                Say(error.Message, error: true);
                continue;
            }

            linkAudit?.Write("authority-pin", services.Device.Id, $"{name} pinned to {pin.DeviceId} for the sync with {peer.Device.Name}");
            connected.WorkspaceMoved();
            if (start)
                _ = Task.Run(async () =>
            {
                try { await LinkSyncStart(peer.Device.Id, name, remember: false, resume: true, cancel: peer.Session.Closing); }
                catch (Exception e) when (e is OperationCanceledException or HexLinkException or IOException) { }
            });
        }
    }

    // Only the pinned old owner can advertise its immediate successor to this device.
    private void LinkLearnAuthority(HexLinkPeer peer)
    {
        if (linkServices is not { } services)
            return;
        foreach (HexLinkFolder remote in peer.Workspace.Folders)
        {
            if (remote.Authority is not { } next || services.Workspace.Folders.FirstOrDefault(
                    each => PlatformPaths.Comparer.Equals(each.Name, remote.Name)) is not { Authority: { } before } local ||
                before.Epoch == long.MaxValue || next.Epoch != before.Epoch + 1 ||
                !before.DeviceId.Equals(peer.Device.Id, StringComparison.OrdinalIgnoreCase) || before.DeviceId == next.DeviceId)
                continue;
            try
            {
                services.FolderAuthorities.Transfer(local, before, next.DeviceId);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or HexLinkException)
            {
                Say(error.Message, error: true);
            }
        }
    }
}
