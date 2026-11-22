namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private (HexLinkClaimExchange? Exchange, long Revision) linkShownClaims;

    partial void LinkRefreshClaimSurfaces()
    {
        HexLinkClaimExchange? exchange = link?.Claims;
        var seen = (exchange, exchange?.Board.Revision ?? 0);
        if (seen == linkShownClaims)
            return;
        linkShownClaims = seen;
        LinkClaimSurfacesChanged();
    }

    partial void LinkReadClaimMark(string path, ref LinkClaimMark? mark)
    {
        if (LinkClaimForEditor(path) is { } claim)
            mark = new LinkClaimMark(OneLine(ClaimHolder(claim), 130), CanOpenLinkClaim(claim), claim.DeviceId);
    }

    partial void LinkOpenClaimPath(string path, ref bool opened)
    {
        if (LinkClaimForEditor(path) is { } claim && CanOpenLinkClaim(claim))
            opened = LinkOpenClaimDocument(claim);
    }

    partial void LinkReadClaimPaths(string deviceId, ref IReadOnlyList<string> paths)
    {
        if (link?.Claims is not { } exchange)
            return;
        paths = [.. exchange.List(System.Environment.TickCount64)
            .Where(claim => claim.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase))
            .Select(claim => claim.Wire + (claim.Prefix ? "/" : string.Empty))
            .Distinct(PlatformPaths.Comparer)];
    }

    private HexLinkClaim? LinkClaimForEditor(string path)
    {
        string? local = LinkDevice?.Id;
        HexLinkClaim? fallback = null;
        foreach (HexLinkClaim claim in LinkClaimsAt(path))
        {
            if (string.Equals(claim.DeviceId, local, StringComparison.OrdinalIgnoreCase))
                continue;
            if (CanOpenLinkClaim(claim))
                return claim;
            fallback ??= claim;
        }
        return fallback;
    }

    private bool CanOpenLinkClaim(HexLinkClaim claim) => !claim.Prefix &&
        (LinkPeerOf(claim.DeviceId) ?? LinkPeerOf(claim.Via)) is { IsClosed: false } peer &&
        HexLinkAllowed.Has(peer.Granted, HexLinkRights.View);
}
