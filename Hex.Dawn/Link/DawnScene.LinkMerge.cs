namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    /// <summary>
    /// Every way into a file in conflict (the Sync Conflicts command, the toast's Review and Compare, the Devices list) comes
    /// here: the Link merge editor, which saves to the real file and settles the conflict. A tab already open for the file
    /// is shown again instead of another one being made.
    /// </summary>
    private void LinkOpenConflictMerge(string deviceId, string path)
    {
        if (LinkSyncOfPath(deviceId, path) is not { } sync)
            return;
        CancellationToken cancel = linkCancel?.Token ?? default;
        _ = Task.Run(async () =>
        {
            try
            {
                if (await sync.ConflictMergeSession(path, cancel) is { } merge)
                {
                    // A file open in a shared document is resolved through it: what the editor completes with goes into
                    // the room, which saves it, and the sync settles on that. Any other file completes as it always did.
                    var complete = merge.Complete;
                    merge = merge with
                    {
                        Complete = async bytes =>
                        {
                            string text = HexLinkFiles.Decode(bytes)?.Text ?? throw new IOException(Said("link.merge_not_text"));
                            if (await LinkSettleOn(sync, path, text, cancel, async () =>
                                {
                                    await complete(bytes);
                                    return null;
                                }) is { } why)
                                throw new IOException(why);
                        }
                    };
                    PostLink(() =>
                    {
                        if (documents.FirstOrDefault(tab => tab.Merge is { Session.LinkDevice: not null } open
                            && PlatformPaths.Comparer.Equals(open.Session.Path, merge.Path)) is { } already)
                            Show(already);
                        else
                            OpenMergeEditor(merge);
                    });
                }
                else
                    PostLink(() => Say(Said("link.merge_gone", ("name", Path.GetFileName(path))), error: true));
            }
            catch (Exception error) when (error is HexLinkException or IOException or UnauthorizedAccessException)
            {
                PostLink(() => Say(error.Message, error: true));
            }
        }, cancel);
    }
}
