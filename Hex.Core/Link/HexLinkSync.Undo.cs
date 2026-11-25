using Hex.Core.Merge;

namespace Hex.Core;

internal sealed partial class HexLinkSync
{
    // The losing versions of the conflicts settled here, for Undo (none when the state is kept inside the folder synced)
    private readonly HexLinkResolutions? resolutions;

    /// <summary>The conflicts settled here that can still be undone (the last 50, for seven days), newest first.</summary>
    public IReadOnlyList<HexLinkResolution> Resolutions() => resolutions?.List().Where(each => !each.Undone).ToArray() ?? [];

    /// <summary>
    /// Brings back the version a resolve threw away, as an ordinary local change: written through the guarded local
    /// write when the file is still what the resolve left, and then sent by the sync like any other edit. When the file
    /// changed since, nothing is written and the result is <see cref="HexLinkUndoStatus.NeedsMerge"/>, for
    /// <see cref="UndoMergeSession"/>.
    /// </summary>
    public async Task<HexLinkUndoResult> Undo(string id, CancellationToken cancel = default)
    {
        if (resolutions?.Get(id) is not { Undone: false } record)
            return new(HexLinkUndoStatus.Refused, $"There is nothing to undo for {id}, it was undone or has expired.");
        lock (gate)
        {
            if (!live || !ready)
                return new(HexLinkUndoStatus.Refused, Summary);
            if (conflicts.ContainsKey(record.Path))
                return new(HexLinkUndoStatus.Refused, $"{record.Path} is in conflict again, settle that first.");
        }

        if (pullOnly)
            return new(HexLinkUndoStatus.Refused, $"{peer.Device.Name} doesn't let this device write there, the restored version would stay here.");
        byte[]? restored = record.LoserHash is null ? null : resolutions.Bytes(record, record.LoserHash);
        if (record.LoserHash is not null && restored is null)
            return new(HexLinkUndoStatus.Refused, "The version that was set aside is no longer kept.");

        try
        {
            string full = Jailed(record.Path);
            byte[]? current = await ReadLocal(full, cancel);
            string? currentHash = current is null ? null : HexLinkFiles.Hash(current);
            if (currentHash != record.ResultHash)
                return new(HexLinkUndoStatus.NeedsMerge, $"{record.Path} changed since it was settled, put the versions together instead.");

            string? written = await ApplyLocal(full, record.Path, currentHash, restored, cancel, null);
            if (written != (restored is null ? null : HexLinkFiles.Hash(restored)))
                return new(HexLinkUndoStatus.Refused, $"{record.Path} changed locally during the undo.");
            resolutions.MarkUndone(record.Id);
            // The watcher may still be reporting the resolve's own write (which it reads as already agreed), so this one is
            // looked at directly, whenever it was pressed
            Dirty(record.Path);
            Notify();
            return new(HexLinkUndoStatus.Undone, $"{record.Path} is back to {(record.Keep == HexLinkSyncKeep.Mine ? peer.Device.Name + "'s" : "your")} version and will sync like an edit.");
        }
        catch (HexLinkException e) when (e.Code == HexLinkErrors.CONFLICT)
        {
            return new(HexLinkUndoStatus.Refused, OpenTabMarked(e));
        }
        catch (Exception e) when (e is HexLinkException or IOException or UnauthorizedAccessException)
        {
            return new(HexLinkUndoStatus.Refused, e.Message);
        }
    }

    /// <summary>
    /// The merge editor's session for an undo that needs a merge: the file as it is now, the version the resolve set
    /// aside, and the one it kept as the common ground. Completing it writes the result as a local change. Null when
    /// the versions aren't text or aren't kept any more.
    /// </summary>
    internal async Task<MergeSession?> UndoMergeSession(string id, CancellationToken cancel)
    {
        if (resolutions?.Get(id) is not { Undone: false } record
            || resolutions.Bytes(record, record.LoserHash) is not { } kept
            || resolutions.Bytes(record, record.WinnerHash) is not { } other)
            return null;
        string full = Jailed(record.Path);
        if (await ReadLocal(full, cancel) is not { } current)
            return null;
        MergeModel model = await MergeModel.FromThree(other, current, kept, cancel);
        if (model.IsBinary)
            return null;
        string currentHash = HexLinkFiles.Hash(current);
        return new(Path.GetFileName(full), "Current file", "Set aside", "Kept by the resolve", model, async bytes =>
        {
            string text = HexLinkFiles.Decode(bytes)?.Text ?? throw new IOException("The merge result is not text.");
            byte[] resolved = HexLinkMerge.EncodeLike(text, current) ?? throw new IOException("The merge result can't be written like the file.");
            if (await ApplyLocal(full, record.Path, currentHash, resolved, cancel, null) != HexLinkFiles.Hash(resolved))
                throw new IOException($"{record.Path} changed while the merge was open.");
            resolutions.MarkUndone(record.Id);
            Dirty(record.Path);
            Notify();
        }, static () => { }, full) { LinkDevice = peer.Device.Name };
    }
}
