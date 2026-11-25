namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    // How long a resolution waits for the room to save the file it was put into
    private const int LINK_RESOLVE_SAVE_WAIT_MS = 10_000;

    /// <summary>
    /// A conflict's resolved text put into the shared document the file is open in, the way an agent's edit is, and saved by
    /// the room. The sync never writes a file under a live room (the editor refuses every write the room hasn't saved
    /// itself), so a resolution written beside the room would be refused on both devices; sent through it, the room's own save
    /// is what the sync then finds on the disk, and settles on. False when the file isn't in a shared document and the sync
    /// can settle it the old way. Throws when it is but the document can't take the text now or the room didn't save it.
    /// </summary>
    private async Task<bool> LinkResolveThroughShared(HexLinkSync sync, string wire, string resolved, CancellationToken cancel)
    {
        (LinkLiveDocument? shared, HexDocSaved? before, string? blocked) = await OnEditorForLink<(LinkLiveDocument?, HexDocSaved?, string?)>(() =>
        {
            if (LinkLocalOf(wire) is not { } full || LinkLiveOf(Context.Documents.Find(full)) is not { } live)
                return (null, null, null);
            if (live.Offline || live.Draft is not null || live.Tab.Editor.ReadOnly || live.AgentEdits.Count >= 16)
                return (live, null, Said("link.live_resolve_blocked", ("name", Path.GetFileName(wire))));
            return (live, live.DiskSave, null);
        }).WaitAsync(cancel);
        if (shared is null)
            return false;
        if (blocked is not null)
            throw new IOException(blocked);

        // The buffer is held with plain line feeds, the text goes in the same way and the room keeps the file's own
        string after = resolved.ReplaceLineEndings("\n");
        var outcome = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool changed = await OnEditorForLink(() =>
        {
            string current = shared.Tab.Editor.Text;
            bool different = current.ReplaceLineEndings("\n") != after;
            if (different)
                shared.AgentEdits.Enqueue(new("link-merge", HexLinkNames.Clean(Said("link.merge_resolved_by")) ?? "Merge", current, after, outcome));
            shared.SaveRequested = true;
            LinkLiveFlush(shared, urgent: true);
            return different;
        }).WaitAsync(cancel);

        // Taken in turn by the room's exchange: when it never is (the room is waiting on the other device, say) the person is told
        // instead of the merge editor staying busy for good
        if (changed)
        {
            string? error;
            try
            {
                error = await outcome.Task.WaitAsync(TimeSpan.FromMilliseconds(LINK_RESOLVE_SAVE_WAIT_MS), cancel);
            }
            catch (TimeoutException)
            {
                await OnEditorForLink(() =>
                {
                    var kept = shared.AgentEdits.Where(request => request.Done != outcome).ToArray();
                    shared.AgentEdits.Clear();
                    foreach (var request in kept)
                        shared.AgentEdits.Enqueue(request);
                    return true;
                });
                throw new IOException(Said("link.live_resolve_timeout", ("name", Path.GetFileName(wire))));
            }
            if (error is not null)
                throw new IOException(error);
        }

        // The room has saved it once the tab holds nothing unsaved and the save is a new one (or it was the text already)
        long until = System.Environment.TickCount64 + LINK_RESOLVE_SAVE_WAIT_MS;
        while (true)
        {
            bool saved = await OnEditorForLink(() =>
            {
                if (!shared.Tab.Modified && shared.DiskSave is not null && (!changed || shared.DiskSave != before))
                    return true;
                // Asked again only once the last ask was taken, a flush a tenth of a second is a lot for a room
                if (!shared.SaveRequested)
                {
                    shared.SaveRequested = true;
                    LinkLiveFlush(shared, urgent: true);
                }
                return false;
            }).WaitAsync(cancel);
            if (saved)
                return true;
            if (System.Environment.TickCount64 >= until)
                throw new IOException(Said("link.live_resolve_timeout", ("name", Path.GetFileName(wire))));
            await Task.Delay(100, cancel);
        }
    }

    // Whether the file of a wire path is open in a shared document right now
    private Task<bool> LinkSharedOpen(string wire) =>
        OnEditorForLink(() => LinkLocalOf(wire) is { } full && LinkLiveOf(Context.Documents.Find(full)) is not null);

    // The conflict on a path settled on a text, through the shared document it's open in when there is one and the way it
    // always was when there isn't. Null when it's done, why when it isn't.
    private async Task<string?> LinkSettleOn(HexLinkSync sync, string wire, string resolved, CancellationToken cancel,
        Func<Task<string?>> otherwise)
    {
        try
        {
            return await LinkResolveThroughShared(sync, wire, resolved, cancel)
                ? await sync.ResolveToText(wire, resolved, cancel)
                : await otherwise();
        }
        catch (IOException e)
        {
            // A refusal caused by the open tab carries the marker, so the person is offered to close the tab and settle it again
            bool open = !HexLinkSync.IsSharedOpenRefusal(e.Message) && await LinkSharedOpen(wire);
            return open ? e.Message + " " + HexLinkSync.SHARED_OPEN_MARKER : e.Message;
        }
    }
}
