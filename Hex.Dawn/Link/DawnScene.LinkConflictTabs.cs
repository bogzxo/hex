namespace Hex.Dawn;

// A conflict that can't be settled while its file is open in a tab: the person is offered to close the tab and settle it,
// where the toast and the Devices window already offer keep mine, keep theirs and the merge editor
internal sealed partial class DawnScene
{
    // How long closing the tab is waited for (its question about unsaved changes may be up), a tenth of a second at a time
    private const int LINK_RESOLVE_CLOSE_WAIT_MS = 120_000;

    /// <summary>The tab a conflict's file is open in, null when it isn't open. On the editor's thread.</summary>
    private HexDocument? LinkConflictTab(string path) =>
        LinkLocalOf(path) is { } full && Context.Documents.Find(full) is { } tab && documents.Contains(tab) ? tab : null;

    // A resolve that was refused: said, with a way to close the tab that is open on the file and settle it again
    private void LinkResolveRefused(string deviceId, string path, HexLinkSyncKeep keep, string why)
    {
        string text = Said("link.conflict_failed", ("path", path), ("why", why.Replace(HexLinkSync.SHARED_OPEN_MARKER, string.Empty).TrimEnd()));
        if (!HexLinkSync.IsSharedOpenRefusal(why) || LinkConflictTab(path) is not { } tab)
        {
            Say(text, error: true);
            return;
        }

        Notify(text + " " + Said("link.conflict_tab_open", ("name", tab.Name)), error: true,
            (Said("link.conflict_close_resolve", ("keep", Said(keep == HexLinkSyncKeep.Mine ? "link.conflict_mine" : "link.conflict_theirs"))),
                () => LinkCloseTabAndResolve(deviceId, path, keep, tab)),
            (Said("link.conflict_merge"), () => LinkOpenConflictMerge(deviceId, path)));
    }

    /// <summary>Closes the tab a conflict's file is open in (asking about unsaved changes, as closing does) and then settles it.</summary>
    internal void LinkCloseTabAndResolve(string deviceId, string path, HexLinkSyncKeep keep, HexDocument tab)
    {
        CloseDocument(tab);
        _ = Task.Run(async () =>
        {
            long until = System.Environment.TickCount64 + LINK_RESOLVE_CLOSE_WAIT_MS;
            while (System.Environment.TickCount64 < until)
            {
                if (await OnEditorForLink(() => !documents.Contains(tab)))
                {
                    LinkResolveSaying(deviceId, path, keep);
                    return;
                }

                await Task.Delay(100);
            }

            PostLink(() => Say(Said("link.conflict_tab_kept", ("name", tab.Name)), error: true));
        });
    }
}
