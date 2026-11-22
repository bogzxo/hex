using System.Text.Json.Nodes;

namespace Hex.Core;

internal interface IHexLinkSyncActivation
{
    Task<HexLinkSyncReport?> ActivateSync(HexLinkPeer peer, HexLinkFolder folder, string action, CancellationToken cancel);
}

internal sealed partial class HexLinkHost
{
    private async Task<JsonNode?> SyncActivate(JsonObject parameters, CancellationToken cancel)
    {
        Need(HexLinkRights.View, "sync/activate");
        if (parameters.ContainsKey("relay") || services is not IHexLinkSyncActivation activation ||
            GitFollowPeer is not { IsClosed: false } peer)
            throw HexLinkException.NotAllowed("Workspace sync activation requires a direct link.");
        string name = HexLinkData.Need(parameters, "folder", 200);
        string owner = HexLinkData.Need(parameters, "authority", 80);
        long epoch = HexLinkData.Long(parameters, "epoch") ?? 0;
        string action = HexLinkData.Need(parameters, "action", 16);
        if (epoch < 1 || action is not ("ready" or "pull" or "flush" or "stop" or "recheck"))
            throw HexLinkException.Invalid("Invalid workspace sync activation.");
        var pin = new HexLinkAuthority(owner, epoch);
        if (services.Workspace.Folders.FirstOrDefault(folder => PlatformPaths.Comparer.Equals(folder.Name, name)) is not { } folder ||
            folder.Authority != pin || pin.DeviceId != (action == "recheck" ? Device.Id : services.Device.Id))
            throw HexLinkException.NotAllowed("The workspace folder and pinned authority epoch must match.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel, session.Closing);
        limit.CancelAfter(HexLinkPeer.SyncActivationPatience);
        HexLinkSyncReport? report = await activation.ActivateSync(peer, folder, action, limit.Token);
        Need(HexLinkRights.View, "sync/activate");
        return new JsonObject { ["status"] = report?.Json() };
    }
}

internal sealed partial class HexLinkPeer
{
    internal static readonly TimeSpan SyncActivationPatience = TimeSpan.FromMinutes(2);

    internal async Task<HexLinkSyncReport?> ActivateSync(string folder, HexLinkAuthority pin, string action, CancellationToken cancel)
    {
        if (pin.DeviceId != (action == "recheck" ? me.Id : Device.Id))
            throw HexLinkException.NotAllowed("Only the pinned folder authority can activate sync.");
        JsonObject reply = await AskObject("sync/activate", new JsonObject
        {
            ["folder"] = folder, ["authority"] = pin.DeviceId, ["epoch"] = pin.Epoch, ["action"] = action
        }, cancel, SyncActivationPatience);
        if (reply["status"] is not JsonObject status)
            return null;
        var report = HexLinkSyncReport.Of(status);
        if (report is null || !PlatformPaths.Comparer.Equals(report.Folder, folder))
            throw HexLinkException.Invalid("The authority answered for a different workspace folder.");
        return report;
    }
}
