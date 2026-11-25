using Horizon.Rendering;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Undo for a resolved Link conflict (docs/hex-link-plan.md, 5.5): the resolve notice and the Devices window's list of
// settled conflicts offer it, and hex_link_sync undo does the same for tools. The losing versions are kept by the sync
// (HexLinkResolutions); this part finds them across the syncs and says what came of an undo.
internal sealed partial class DawnScene
{
    // How long the notice after a resolve keeps its Undo, in seconds
    private const float LINK_UNDO_NOTICE_SECONDS = 30.0f;

    /// <summary>The conflicts settled that can still be undone, newest first, of one device or of all, each with the sync that has it.</summary>
    internal IReadOnlyList<(HexLinkSync Sync, HexLinkResolution Record)> LinkResolutionList(string? deviceId = null, string? folder = null)
    {
        var all = new List<(HexLinkSync, HexLinkResolution)>();
        lock (linkSyncGate)
        {
            foreach (LinkSyncEntry entry in linkSyncs.Values)
            {
                if (entry.Sync is not { } sync || deviceId is not null && !entry.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase)
                    || folder is not null && !PlatformPaths.Comparer.Equals(entry.Folder, folder))
                    continue;
                foreach (HexLinkResolution record in sync.Resolutions())
                    all.Add((sync, record));
            }
        }

        return [.. all.OrderByDescending(each => each.Item2.At)];
    }

    /// <summary>One settled conflict undone by its id, from any thread. A file changed since comes back as needs_merge, nothing written.</summary>
    internal async Task<HexLinkUndoResult> LinkUndo(string id, string? deviceId = null)
    {
        if (LinkResolutionList(deviceId).FirstOrDefault(each => each.Record.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is not { Sync: { } sync })
            return new(HexLinkUndoStatus.Refused, $"There is nothing to undo for {id}, it was undone or has expired.");
        HexLinkUndoResult result = await sync.Undo(id, linkCancel?.Token ?? default);
        linkDirty = true;
        LinkMoved();
        return result;
    }

    // An undo from a button: said on the status bar, and the merge editor with the three versions when the file moved on
    private void LinkUndoSaying(string id, string? deviceId = null) => _ = Task.Run(async () =>
    {
        HexLinkUndoResult result;
        HexLinkSync? sync = LinkResolutionList(deviceId).FirstOrDefault(each => each.Record.Id == id).Sync;
        string path = LinkResolutionList(deviceId).FirstOrDefault(each => each.Record.Id == id).Record?.Path ?? id;
        try
        {
            result = await LinkUndo(id, deviceId);
        }
        catch (Exception e) when (e is IOException or HexLinkException or InvalidOperationException)
        {
            result = new(HexLinkUndoStatus.Refused, e.Message);
        }

        string name = Path.GetFileName(path);
        if (result.Status == HexLinkUndoStatus.NeedsMerge && sync is not null)
        {
            Core.Merge.MergeSession? merge = null;
            try
            {
                merge = await sync.UndoMergeSession(id, linkCancel?.Token ?? default);
            }
            catch (Exception e) when (e is IOException or HexLinkException or UnauthorizedAccessException)
            {
                result = new(HexLinkUndoStatus.Refused, e.Message);
            }

            if (merge is not null)
            {
                PostLink(() =>
                {
                    Say(Said("link.undo_needs_merge", ("name", name)));
                    OpenMergeEditor(merge);
                });
                return;
            }

            if (result.Status == HexLinkUndoStatus.NeedsMerge)
                result = new(HexLinkUndoStatus.Refused, Said("link.undo_gone", ("name", name)));
        }

        PostLink(() =>
        {
            if (result.Status == HexLinkUndoStatus.Undone)
                NotifyDone(Said("link.undo_done", ("name", name)));
            else
                NotifyError(result.Message);
        });
    });

    // The notice after a resolve: what was kept, with Undo for a little while
    private UINotification LinkNotifyResolved(string deviceId, string path, string message)
    {
        string? id = LinkResolutionList(deviceId).FirstOrDefault(each => each.Record.Path == path).Record?.Id;
        Note(message, error: false);
        if (id is null)
            return toasts.Show(message, "check", compositor.Skin?.Palette.Success, 4.0f);
        UINotification? card = null;
        card = toasts.Show(message, "check", compositor.Skin?.Palette.Success, LINK_UNDO_NOTICE_SECONDS,
            (Said("link.undo_button"), () =>
            {
                card?.Close();
                LinkUndoSaying(id, deviceId);
            }));
        return card;
    }

    // The settled conflicts of a folder with a device, in the Devices window: each with Undo while it can be undone
    private void DrawLinkResolutions(StackPanel holder, HexLinkPeer peer, string folder)
    {
        IReadOnlyList<(HexLinkSync Sync, HexLinkResolution Record)> settled = LinkResolutionList(peer.Device.Id, folder);
        if (settled.Count == 0)
            return;
        holder.Add(new Label { Text = Said("link.undo_heading"), TextScale = LINK_SMALL, Bold = true, Align = Origin.Left, Fill = UIFill.Horizontal, Padding = new UIEdges(18, 0, 0, 0) });
        foreach ((HexLinkSync _, HexLinkResolution record) in settled.Take(5))
        {
            string name = Path.GetFileName(record.Path);
            string when = LinkHoldAge(DateTimeOffset.UtcNow - record.At);
            string text = record.Keep == HexLinkSyncKeep.Mine
                ? Said("link.undo_kept_mine", ("name", name), ("when", when))
                : Said("link.undo_kept_theirs", ("name", name), ("device", LinkNameOf(peer.Device.Id, peer.Device.Name)), ("when", when));
            var row = holder.Add(new StackPanel { Direction = UIDirection.Horizontal, Spacing = 8.0f, Fill = UIFill.Horizontal, Size = new System.Numerics.Vector2(0, LINK_ROW), Padding = new UIEdges(18, 0, 0, 0) });
            row.Add(new Label { Text = UIRichText.Literal(OneLine(text, 48)), TextScale = LINK_SMALL, Anchor = Origin.Left, Pivot = Origin.Left, Align = Origin.Left, Tooltip = record.Path });
            string id = record.Id;
            row.Add(LinkButton(Said("link.undo_button"), "sync", () => LinkUndoSaying(id, peer.Device.Id))).Tooltip = Said("link.undo_tip");
        }
    }
}
