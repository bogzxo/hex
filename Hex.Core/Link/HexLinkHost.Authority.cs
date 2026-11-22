using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkHost
{
    private Task<JsonNode?> AuthorityTransfer(JsonObject parameters, CancellationToken cancel)
    {
        Need(HexLinkRights.Edit, "authority/transfer");
        if (parameters.ContainsKey("relay"))
            throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "Authority transfer needs the new owner's authenticated connection.");
        string name = HexLinkData.Need(parameters, "folder", 200);
        HexLinkAuthority expected = HexLinkAuthority.Of(HexLinkData.Object(parameters, "expected"))
            ?? throw HexLinkException.Invalid("An authority transfer needs its current epoch.");
        if (!expected.DeviceId.Equals(services.Device.Id, StringComparison.OrdinalIgnoreCase))
            throw new HexLinkException(HexLinkErrors.CONFLICT, "This device is not the authority named in the request.");
        HexLinkFolder folder = Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, name))
            ?? throw HexLinkException.NotFound($"The workspace has no folder called {name}.");
        HexLinkAuthorityStore store = services.FolderAuthorities
            ?? throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "This device cannot change folder authority.");
        cancel.ThrowIfCancellationRequested();
        HexLinkAuthority next = store.Transfer(folder, expected, Device.Id);
        services.Audit("authority-transfer", Device, $"{folder.Name} moved from {expected.DeviceId} to {next.DeviceId}, epoch {next.Epoch}");
        return Task.FromResult<JsonNode?>(next.ToJson());
    }
}
