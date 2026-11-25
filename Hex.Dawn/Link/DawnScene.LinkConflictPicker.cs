using System.Globalization;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    // The key of the one card that stands for several files in conflict with a device, beside the keys of single files
    private const string LINK_CONFLICT_GROUP = "*group";

    /// <summary>
    /// Several files edited on both computers at once: one card that opens the picker, not a card (and four buttons) each.
    /// The card goes when the last of them is settled, see the sweep of the cards in LinkSyncSettled.
    /// </summary>
    private void LinkSyncAnnounceConflicts(LinkSyncEntry entry, List<HexLinkSyncConflict> fresh)
    {
        string id = entry.DeviceId;
        string key = id + "\n" + LINK_CONFLICT_GROUP;
        // The card counts every file of the device in conflict, those this pass found and those it already knew
        int all = Math.Max(fresh.Count, entry.Sync?.Conflicts.Count ?? 0);
        string text = GroupText(all);
        if (linkConflictCards.TryGetValue(key, out UINotification? older) && !older.Closed)
        {
            older.Text = text;
            older.Renew();
            return;
        }

        var buttons = new List<(string Label, Action Pressed)>
        {
            (Said("link.sync_review"), () => LinkPickConflict([.. LinkConflicts.Where(each => each.DeviceId == id)]))
        };
        // One file alone can be settled from the card, the way a card of its own would: keep mine or use the device's
        if (all == 1 && fresh.Count == 1)
        {
            string path = fresh[0].Path, device = LinkNameOf(id, entry.DeviceName);
            buttons.Add((Said("link.sync_keep_mine"), () => LinkResolveSaying(id, path, HexLinkSyncKeep.Mine)));
            buttons.Add((Said("link.sync_keep_theirs", ("device", device)), () => LinkResolveSaying(id, path, HexLinkSyncKeep.Theirs)));
        }

        UINotification card = toasts.Show(text, "warning", null, 0.0f, [.. buttons]);
        linkConflictCards[key] = card;
        Note(text, error: false);
    }

    private string GroupText(int count) => count == 1
        ? Said("link.sync_conflicts_one")
        : Said("link.sync_conflicts_many", ("n", count.ToString(CultureInfo.InvariantCulture)));

    /// <summary>The grouped card's count made right as files are settled, said again (renewed) when it changes.</summary>
    private void LinkSyncCountGroup(LinkSyncEntry entry, int count)
    {
        if (count == 0 || !linkConflictCards.TryGetValue(entry.DeviceId + "\n" + LINK_CONFLICT_GROUP, out UINotification? card) || card.Closed)
            return;
        string text = GroupText(count);
        if (card.Text != text)
        {
            card.Text = text;
            card.Renew();
        }
    }

    private readonly Dictionary<string, UINotification> linkErrorCards = new(StringComparer.Ordinal);

    /// <summary>
    /// An error as a card that goes by itself after about eight seconds, and the same error said again takes the place of the
    /// one still up instead of stacking beside it.
    /// </summary>
    private UINotification NotifyError(string message)
    {
        if (linkErrorCards.Remove(message, out UINotification? same))
            same.Close();
        Note(message, error: true);
        UINotification card = toasts.Show(message, "error", ErrorColor, 8.0f);
        linkErrorCards[message] = card;
        foreach (string gone in linkErrorCards.Where(each => each.Value.Closed).Select(each => each.Key).ToArray())
            linkErrorCards.Remove(gone);
        return card;
    }

    /// <summary>A card with a tick that goes by itself after a few seconds, for what went well; the output keeps the words.</summary>
    private UINotification NotifyDone(string message, params (string Label, Action Pressed)[] actions)
    {
        Note(message, error: false);
        return toasts.Show(message, "check", compositor.Skin?.Palette.Success, 4.0f, actions);
    }

    /// <summary>
    /// The picker of files in conflict: each file, then keep mine, use the other device's, or compare, in the words the cards
    /// use. The Devices window is put away first, it sat over the picker and took its keys.
    /// </summary>
    private void LinkPickConflict(IReadOnlyList<LinkConflictShown> conflicts)
    {
        if (conflicts.Count == 0)
            return;
        CloseLinkWindow();
        ChooseFrom(Said("link.devices_conflict_title"), Said("link.devices_conflict_help"), [.. conflicts.Select(conflict => (conflict.Path, (Action)(() =>
        {
            string device = LinkNameOf(conflict.DeviceId, conflict.DeviceName);
            ChooseFrom(Said("link.devices_conflict_title"), conflict.Path,
            [
                // Compare first: it is what Enter picks, and the other two throw a side away
                (Said("link.conflict_merge"), () => LinkOpenConflictMerge(conflict.DeviceId, conflict.Path)),
                (Said("link.conflict_mine"), () => LinkResolveSaying(conflict.DeviceId, conflict.Path, HexLinkSyncKeep.Mine)),
                (Said("link.conflict_theirs", ("device", device)), () => LinkResolveSaying(conflict.DeviceId, conflict.Path, HexLinkSyncKeep.Theirs))
            ]);
        })))]);
    }
}
