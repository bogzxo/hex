namespace Hex.Dawn;

// A peer changed the rights it grants this device: a notice that names it and says what that means for sync and the tools,
// and a line in the Devices window's activity list
internal sealed partial class DawnScene
{
    /// <summary>What a change in the rights a peer grants means, one line a right, "Beta no longer lets this device edit there: your saves stay here until it does."</summary>
    private static List<string> LinkRightsNoticeLines(string device, HexLinkRightsChange change)
    {
        var lines = new List<string>();
        bool lostView = (change.Lost & HexLinkRights.View) != 0;
        foreach (HexLinkRights right in change.Revoked)
        {
            // Without View nothing else of it reaches here, so it is the one thing said
            if (lostView && right is HexLinkRights.Edit or HexLinkRights.Steer)
                continue;
            lines.Add(Said("link.rights_lost_" + LinkRightKey(right), ("device", device)));
        }

        foreach (HexLinkRights right in change.Granted)
            lines.Add(Said("link.rights_gained_" + LinkRightKey(right), ("device", device)));
        return lines;
    }

    private static string LinkRightKey(HexLinkRights right) => right switch
    {
        HexLinkRights.View => "view",
        HexLinkRights.Edit => "edit",
        HexLinkRights.Steer => "steer",
        _ => "predict"
    };

    // On the editor's thread, a peer's grant to this device moved from what it was
    private void LinkRightsGrantedMoved(HexLinkPeer peer, HexLinkRights was)
    {
        HexLinkRightsChange change = HexLinkRightsChange.Between(was, peer.Granted);
        if (!change.Any)
            return;
        string device = OneLine(LinkNameOf(peer.Device.Id, peer.Device.Name), 40);
        List<string> lines = LinkRightsNoticeLines(device, change);
        linkAudit?.Write("rights from", peer.Device.Id,
            $"{string.Join(' ', HexLinkTrust.RightsNames(was))} -> {string.Join(' ', HexLinkTrust.RightsNames(peer.Granted))}: {string.Join(' ', lines)}");
        linkDirty = true;
        // What an earlier card said of this device's rights is out of date now: a lost right stays up until it is given back
        if (linkRightsCards.Remove(peer.Device.Id, out UINotification? stale))
            stale.Close();
        if (lines.Count > 0)
        {
            UINotification card = Notify(string.Join("\n", lines), error: change.Lost != HexLinkRights.None);
            if (change.Lost != HexLinkRights.None)
                linkRightsCards[peer.Device.Id] = card;
        }
    }

    private Dictionary<string, UINotification> linkRightsCards { get => LazyInitializer.EnsureInitialized(ref field, static () => new(StringComparer.OrdinalIgnoreCase)); }
}
