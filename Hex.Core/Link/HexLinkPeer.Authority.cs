using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkPeer
{
    public async Task<HexLinkAuthority> TransferAuthority(string folder, HexLinkAuthority expected, CancellationToken cancel)
    {
        if (!Device.Id.Equals(expected.DeviceId, StringComparison.OrdinalIgnoreCase) || !HexLinkAllowed.Has(Granted, HexLinkRights.Edit))
            throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "Changing authority requires Edit on the current owner.");
        var parameters = new JsonObject { ["folder"] = folder, ["expected"] = expected.ToJson() };
        JsonObject said = await AskObject("authority/transfer", parameters, cancel);
        HexLinkAuthority? changed = HexLinkAuthority.Of(said);
        if (changed is null || !changed.DeviceId.Equals(me.Id, StringComparison.OrdinalIgnoreCase) || changed.Epoch != checked(expected.Epoch + 1))
            throw new HexLinkException(HexLinkErrors.CONFLICT, "The previous authority did not confirm the requested transfer.");
        return changed;
    }
}
