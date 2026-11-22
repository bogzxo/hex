using System.Text;
using Hex.Core.Git;
using Hex.Core.Merge;

namespace Hex.Core;

internal sealed record HexLinkMergePreview(string LocalHash, string RemoteHash, byte[] Markers);

internal sealed partial class HexLinkSync
{
    private readonly Dictionary<string, HexLinkMergePreview> mergePreviews = new(StringComparer.Ordinal);

    public HexLinkMergePreview? MergePreview(string wire)
    {
        lock (gate)
            return conflicts.ContainsKey(wire) ? mergePreviews.GetValueOrDefault(wire) : null;
    }

    // Treat auto-merge as two checked writes. A changed hash schedules a fresh comparison instead of overwriting.
    private async Task<bool> TryTextMerge(string wire, string full, HexLinkSyncAgreed? agreed, byte[]? localBytes,
        string? mine, string? theirs, CancellationToken stop)
    {
        if (Holds?.IsHeld(wire) == true)
            return await ReconcileHold(wire, stop);
        if (agreed is null || localBytes is null || mine is null || theirs is null
            || localBytes.Length > HexLinkMerge.CACHE_FILE_BYTES || pullOnly || !remoteCertified)
            return false;

        byte[]? basis = await MergeBase(full, agreed, stop);
        if (basis is null)
            return false;

        HexLinkFile remoteFile = await Fetch(wire, stop);
        if (remoteFile.Hash != theirs)
        {
            Again(wire);
            return true;
        }

        if (remoteFile.Bytes.Length > HexLinkMerge.CACHE_FILE_BYTES
            || await HexLinkMerge.TryTextAsync(basis, localBytes, remoteFile.Bytes, stop) is not { } result)
            return false;

        if (!result.Clean)
        {
            // Keep the exact remote bytes for Keep Theirs, and the diff3 draft for the existing Accept lenses.
            (string copyFull, string copy) = Beside(full, wire);
            string? copyHash = await HexLinkFiles.CompareAndReplace(copyFull, copy, null, remoteFile.Bytes, stop);
            lock (gate)
            {
                if (mergePreviews.Count >= 8)
                    mergePreviews.Remove(mergePreviews.Keys.First());
                mergePreviews[wire] = new HexLinkMergePreview(mine, theirs, result.Bytes);
                conflicts[wire] = new HexLinkSyncConflict(wire, "changed on both", copy, copyHash);
                RemoveBase(wire);
            }

            Save();
            Notify();
            return true;
        }

        if (HexLinkFiles.Decode(result.Bytes) is not { } merged
            || HexLinkMerge.EncodeLike(merged.Text, remoteFile.Bytes) is not { } sending)
            return false;

        try
        {
            await WriteMerge(wire, full, mine, theirs, localBytes, result.Bytes, sending, stop);
        }
        catch (HexLinkException e) when (EditorDirty(e))
        {
            await HoldEditorConflict(wire, full, remoteFile.Bytes, "unsaved changes prevented automatic merge", stop);
        }
        catch (HexLinkException e) when (e.Code is HexLinkErrors.CONFLICT or HexLinkErrors.NOT_FOUND or HexLinkErrors.NOT_ALLOWED)
        {
            Again(wire, e.Message);
        }

        Notify();
        return true;
    }

    private static async Task<byte[]?> MergeBase(string full, HexLinkSyncAgreed agreed, CancellationToken stop)
    {
        if (HexLinkMerge.CachedBase(agreed.Local) is { } cached)
            return cached;

        // The previous Git blob is useful only if its raw or normalized hash matches the base kept by sync.
        string? directory = Path.GetDirectoryName(full);
        if (directory is null || HexLinkGit.Why() is not null)
            return null;

        return await Task.Run(() =>
        {
            GitResult blob = GitProcess.Run(directory, ["show", "HEAD:./" + Path.GetFileName(full)], unattended: true,
                patience: TimeSpan.FromSeconds(5), cancel: stop);
            if (!blob.Ok || blob.Output.Length > HexLinkMerge.CACHE_FILE_BYTES)
                return null;
            byte[] candidate = Encoding.UTF8.GetBytes(blob.Output);
            string? digest = TextAndEol(candidate).Text;
            return HexLinkFiles.Hash(candidate) == agreed.Local || digest is not null && digest == agreed.Text
                ? candidate : null;
        }, stop);
    }

    private async Task WriteMerge(string wire, string full, string mine, string theirs, byte[] previousLocal, byte[] localBytes,
        byte[] remoteBytes, CancellationToken stop)
    {
        if (pullOnly || !remoteCertified)
            throw HexLinkException.NotAllowed("Edit");

        string localHash = HexLinkFiles.Hash(localBytes), remoteHash = HexLinkFiles.Hash(remoteBytes);
        if (await ApplyLocal(full, wire, mine, localBytes, stop, null) != localHash)
            throw new HexLinkException(HexLinkErrors.CONFLICT, $"{wire} changed locally during merge.");
        Interlocked.Increment(ref received);

        string written;
        try
        {
            written = await Asked(() => peer.Write(wire, theirs, remoteBytes, stop), stop);
        }
        catch (HexLinkException error) when (error.Code is HexLinkErrors.CONFLICT or HexLinkErrors.NOT_ALLOWED or HexLinkErrors.NOT_FOUND)
        {
            // The peer rejected the write. Restore our original only if nobody has edited the merged bytes here.
            await ApplyLocal(full, wire, localHash, previousLocal, stop, null);
            throw;
        }
        if (written != remoteHash)
            throw new HexLinkException(HexLinkErrors.CONFLICT, $"{wire} changed remotely during merge.");

        Interlocked.Increment(ref sent);
        lock (gate)
            SetBase(wire, AgreedOf(localBytes, remoteBytes));
    }

    internal async Task<MergeSession?> ConflictMergeSession(string wire, CancellationToken cancel)
    {
        HexLinkSyncConflict? conflict;
        HexLinkMergePreview? preview;
        lock (gate)
        {
            if (!conflicts.TryGetValue(wire, out conflict))
                return null;
            preview = mergePreviews.GetValueOrDefault(wire);
        }

        string full = Jailed(wire);
        MergeModel model;
        if (preview is not null && HexLinkFiles.Decode(preview.Markers) is { } draft)
            model = MergeModel.FromMarkers(draft.Text, draft.Encoding);
        else
        {
            byte[]? mine = await ReadLocal(full, cancel);
            byte[]? theirs = conflict.Copy is { } copy
                ? await HexLinkFiles.ReadSharedAsync(HexLinkFiles.Resolve([folder], copy), cancel) : null;
            if (mine is null || theirs is null)
                model = MergeModel.Binary([], mine ?? [], theirs ?? []);
            else
                model = MergeModel.FromTwo(mine, theirs);
            if (model.IsBinary)
            {
                return new MergeSession(Path.GetFileName(full), "Mine", "Theirs", "Base", model,
                    _ => Task.CompletedTask, static () => { }, full)
                {
                    SideChoices = new(mine is null, theirs is null, async side =>
                    {
                        if (await Resolve(wire, side == MergeSide.Mine ? HexLinkSyncKeep.Mine : HexLinkSyncKeep.Theirs, cancel) is { } why)
                            throw new IOException(why);
                    }),
                    MineModified = mine is null ? null : new DateTimeOffset(File.GetLastWriteTimeUtc(full)),
                };
            }

            if (mine is null || theirs is null)
                return null;
            preview = new(HexLinkFiles.Hash(mine), HexLinkFiles.Hash(theirs), model.ToBytes());
            lock (gate)
            {
                if (conflicts.GetValueOrDefault(wire) != conflict)
                    return null;
                mergePreviews[wire] = preview;
            }
        }

        return new(Path.GetFileName(full), "Mine", "Theirs", "Base", model, async bytes =>
        {
            string resolved = HexLinkFiles.Decode(bytes)?.Text ?? throw new IOException("The merge result is not text.");
            if (await PublishMerge(wire, preview!, resolved, cancel) is { } why)
                throw new IOException(why);
        }, static () => { }, full);
    }

    public async Task<string?> PublishMerge(string wire, HexLinkMergePreview preview, string resolved, CancellationToken cancel = default)
    {
        HexLinkSyncConflict? conflict;
        lock (gate)
        {
            if (!live || !ready)
                return Summary;
            if (!mergePreviews.TryGetValue(wire, out var current) || current != preview || !conflicts.TryGetValue(wire, out conflict))
                return "The Link conflict changed. Open the latest Merge Preview.";
        }

        try
        {
            string full = Jailed(wire);
            byte[]? localBytes = await ReadLocal(full, cancel);
            if (localBytes is null || HexLinkFiles.Hash(localBytes) != preview.LocalHash)
                return "The local file changed since the merge preview was opened.";
            HexLinkFile remoteFile = await Fetch(wire, cancel);
            if (remoteFile.Hash != preview.RemoteHash)
                return "The remote file changed since the merge preview was opened.";
            if (HexLinkMerge.EncodeLike(resolved, localBytes) is not { Length: <= HexLinkFiles.MOST_READ } localResolved
                || HexLinkMerge.EncodeLike(resolved, remoteFile.Bytes) is not { Length: <= HexLinkFiles.MOST_READ } remoteResolved)
                return "The resolved file is too large or its encoding changed.";
            await WriteMerge(wire, full, preview.LocalHash, preview.RemoteHash, localBytes, localResolved, remoteResolved, cancel);
            lock (gate)
            {
                mergePreviews.Remove(wire);
                conflicts.Remove(wire);
            }
            await DropCopy(conflict, cancel);
            Save();
            Notify();
            return null;
        }
        catch (Exception e) when (e is HexLinkException or IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
    }
}
