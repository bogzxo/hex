namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private sealed record LinkMergeDraft(HexLinkSync Sync, string Wire, HexLinkMergePreview Preview);
    private readonly Dictionary<HexDocument, LinkMergeDraft> linkMergeDrafts = [];

    private bool LinkOpenMergePreview(string deviceId, string path)
    {
        if (LinkSyncOfPath(deviceId, path) is not { } sync || sync.MergePreview(path) is not { } preview
            || HexLinkFiles.Decode(preview.Markers) is not { } text)
            return false;
        try
        {
            string dir = Path.Combine(linkData, "merge-previews", Guid.NewGuid().ToString("N"));
            string draftPath = Path.Combine(dir, Path.GetFileName(path));
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(draftPath, preview.Markers);
            if (!OpenText(draftPath, text.Text))
            {
                HexLinkGit.TryDelete(draftPath);
                return false;
            }
            foreach (HexDocument closed in linkMergeDrafts.Keys.Where(tab => !documents.Contains(tab)).ToArray())
                linkMergeDrafts.Remove(closed);
            linkMergeDrafts[document] = new LinkMergeDraft(sync, path, preview);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Say(error.Message, error: true);
            return false;
        }
    }

    private bool LinkOfferResolvedMerge(HexDocument tab)
    {
        if (!linkMergeDrafts.TryGetValue(tab, out LinkMergeDraft? draft))
            return false;
        Notify(Said("link.merge.ready", ("name", Path.GetFileName(draft.Wire))),
            actions: (Said("link.merge.publish"), () => LinkPublishMerge(tab, draft)));
        return true;
    }

    private void LinkPublishMerge(HexDocument tab, LinkMergeDraft draft)
    {
        string text = tab.Editor.Text;
        if (text.ReplaceLineEndings("\n").Split('\n').Any(line =>
            line.StartsWith("<<<<<<< Current", StringComparison.Ordinal)
            || line.StartsWith("||||||| Base", StringComparison.Ordinal)
            || line.StartsWith(">>>>>>> Incoming", StringComparison.Ordinal)))
        {
            Say(Said("link.merge.unresolved"), error: true);
            return;
        }

        CancellationToken cancel = linkCancel?.Token ?? default;
        _ = Task.Run(async () =>
        {
            string? why;
            try
            {
                why = await draft.Sync.PublishMerge(draft.Wire, draft.Preview, text, cancel);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            PostLink(() =>
            {
                if (why is not null)
                {
                    Say(why, error: true);
                    return;
                }
                linkMergeDrafts.Remove(tab);
                Say(Said("link.merge.published", ("path", draft.Wire)));
            });
        }, cancel);
    }
}
