namespace Hex.Core;

/// <summary>
/// What a peer changed in the rights it grants this device: the rights lost and gained. The editor says each in a notice,
/// naming the device and what it means for sync and the tools.
/// </summary>
internal sealed record HexLinkRightsChange(HexLinkRights Lost, HexLinkRights Gained)
{
    private static readonly HexLinkRights[] ALL = [HexLinkRights.View, HexLinkRights.Edit, HexLinkRights.Steer, HexLinkRights.Predict];

    public bool Any => Lost != HexLinkRights.None || Gained != HexLinkRights.None;

    public static HexLinkRightsChange Between(HexLinkRights was, HexLinkRights now) => new(was & ~now, now & ~was);

    /// <summary>Each single right that went, in the order View, Edit, Steer, Predict.</summary>
    public IEnumerable<HexLinkRights> Revoked => ALL.Where(right => (Lost & right) != 0);

    /// <summary>Each single right that came, in the order View, Edit, Steer, Predict.</summary>
    public IEnumerable<HexLinkRights> Granted => ALL.Where(right => (Gained & right) != 0);
}
