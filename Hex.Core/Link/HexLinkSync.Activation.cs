using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed record HexLinkSyncPause(string Folder, string? By);

internal interface IHexLinkSyncActivation
{
    Task<HexLinkSyncReport?> ActivateSync(HexLinkPeer peer, HexLinkFolder folder, string action, bool resume, CancellationToken cancel);
    HexLinkAuthority? BindProvisionSource(HexLinkFolder folder) => null;
    Task ResumeLegacyPair(HexLinkPeer peer, HexLinkFolder folder, CancellationToken cancel) =>
        throw HexLinkException.NotFound("This Hex cannot resume a legacy pair remotely.");
}

internal sealed partial class HexLinkHost
{
    private async Task<JsonNode?> SyncResumeLegacyPair(JsonObject parameters, CancellationToken cancel)
    {
        Need(HexLinkRights.Steer, "sync/resume-pair");
        if (parameters.ContainsKey("relay") || services is not IHexLinkSyncActivation activation ||
            GitFollowPeer is not { IsClosed: false } peer)
            throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "Resuming a legacy sync requires a direct link.");
        string name = HexLinkData.Need(parameters, "folder", 200);
        HexLinkFolder folder = services.Workspace.Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, name))
            ?? throw HexLinkException.NotFound("That folder is no longer exported.");
        await activation.ResumeLegacyPair(peer, folder, cancel);
        return new JsonObject();
    }

    private JsonNode SyncProvisionAuthority(JsonObject parameters)
    {
        Need(HexLinkRights.View, "sync/provision-authority");
        if (parameters.ContainsKey("relay") || services is not IHexLinkSyncActivation activation)
            throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "Provisioning a folder authority requires a direct link.");
        string name = HexLinkData.Need(parameters, "folder", 200);
        HexLinkFolder folder = services.Workspace.Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, name))
            ?? throw HexLinkException.NotFound("That folder is no longer exported.");
        // Reading an existing pin needs View. Creating the source's pin for a legacy Get changes its local profile.
        if (folder.Authority is null)
            Need(HexLinkRights.Steer, "sync/provision-authority");
        return new JsonObject { ["authority"] = activation.BindProvisionSource(folder)?.ToJson() };
    }

    private async Task<JsonNode?> SyncActivate(JsonObject parameters, CancellationToken cancel)
    {
        Need(HexLinkRights.View, "sync/activate");
        if (parameters.ContainsKey("relay") || services is not IHexLinkSyncActivation activation ||
            GitFollowPeer is not { IsClosed: false } peer)
            throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "Workspace sync activation requires a direct link.");
        string name = HexLinkData.Need(parameters, "folder", 200);
        string owner = HexLinkData.Need(parameters, "authority", 80);
        long epoch = HexLinkData.Long(parameters, "epoch") ?? 0;
        string action = HexLinkData.Need(parameters, "action", 16);
        if (epoch < 1 || action is not ("ready" or "pull" or "flush" or "stop" or "recheck"))
            throw HexLinkException.Invalid("Invalid workspace sync activation.");
        bool resume = HexLinkData.Bool(parameters, "resume") == true;
        if (resume && action is not ("ready" or "pull" or "recheck"))
            throw HexLinkException.Invalid("This operation cannot resume a paused sync.");
        var pin = new HexLinkAuthority(owner, epoch);
        if (services.Workspace.Folders.FirstOrDefault(folder => PlatformPaths.Comparer.Equals(folder.Name, name)) is not { } folder ||
            folder.Authority != pin || pin.DeviceId != (action == "recheck" ? Device.Id : services.Device.Id))
            throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "The workspace folder and pinned authority epoch must match.");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel, session.Closing);
        limit.CancelAfter(HexLinkPeer.SyncActivationPatience);
        HexLinkSyncReport? report = await activation.ActivateSync(peer, folder, action, resume, limit.Token);
        Need(HexLinkRights.View, "sync/activate");
        return new JsonObject { ["status"] = report?.Json() };
    }
}

internal sealed partial class HexLinkPeer
{
    internal static readonly TimeSpan SyncActivationPatience = TimeSpan.FromMinutes(2);

    internal Task<JsonObject> ResumeLegacyPair(string folder, CancellationToken cancel) =>
        AskObject("sync/resume-pair", new JsonObject { ["folder"] = folder }, cancel, TimeSpan.FromSeconds(5));

    internal async Task<HexLinkAuthority?> ProvisionSyncAuthority(string folder, CancellationToken cancel)
    {
        JsonObject reply = await AskObject("sync/provision-authority", new JsonObject { ["folder"] = folder },
            cancel, TimeSpan.FromSeconds(5));
        HexLinkAuthority? pin = HexLinkAuthority.Of(HexLinkData.Object(reply, "authority"));
        return pin?.DeviceId == session.Connection.Device.Id ? pin : null;
    }

    internal async Task<HexLinkSyncReport?> ActivateSync(string folder, HexLinkAuthority pin, string action, CancellationToken cancel,
        bool resume = false)
    {
        if (pin.DeviceId != (action == "recheck" ? me.Id : Device.Id))
            throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "Only the pinned folder authority can activate sync.");
        JsonObject reply = await AskObject("sync/activate", new JsonObject
        {
            ["folder"] = folder, ["authority"] = pin.DeviceId, ["epoch"] = pin.Epoch, ["action"] = action, ["resume"] = resume
        }, cancel, SyncActivationPatience);
        if (reply["status"] is not JsonObject status)
            return null;
        var report = HexLinkSyncReport.Of(status);
        if (report is null || !PlatformPaths.Comparer.Equals(report.Folder, folder))
            throw HexLinkException.Invalid("The authority answered for a different workspace folder.");
        return report;
    }
}
