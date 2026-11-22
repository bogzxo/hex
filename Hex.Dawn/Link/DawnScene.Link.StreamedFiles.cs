using System.Text.Json.Nodes;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private async Task<LinkTabStamp> LinkTabForCheckedWrite(string full, string wire, CancellationToken cancel)
    {
        LinkTabStamp before = await OnEditorForLink(() => LinkTabBeforeWrite(full)).WaitAsync(cancel);
        if (before.Dirty)
            throw new HexLinkException(HexLinkErrors.CONFLICT, $"{wire} has unsaved changes in the editor.",
                new JsonObject { ["hash"] = HexLinkFiles.HashOf(full), ["editor_dirty"] = true });
        return before;
    }

    private async Task<string?> LinkCompareAndReplaceStream(string full, string wire, string? baseHash, Stream? source,
        long length, string? expectedHash, CancellationToken cancel, bool? executable)
    {
        LinkTabStamp before = await LinkTabForCheckedWrite(full, wire, cancel);
        if (before.Document is not null)
            throw new HexLinkException(HexLinkErrors.CONFLICT, $"{wire} is open in the editor. Close its tab before replacing it over Link.",
                new JsonObject { ["editor_dirty"] = true });

        string? hash = await HexLinkFiles.CompareAndReplace(full, wire, baseHash, source, length, expectedHash, cancel, executable);
        QueuePrivateFollowForPath(full);
        return hash;
    }
}
