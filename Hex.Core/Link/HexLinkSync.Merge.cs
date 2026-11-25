using System.Text;
using Hex.Core.Git;
using Hex.Core.Merge;

namespace Hex.Core;

internal sealed record HexLinkMergePreview(string LocalHash, string RemoteHash, byte[] Markers);

internal sealed partial class HexLinkSync
{
    private readonly Dictionary<string, HexLinkMergePreview> mergePreviews = new(StringComparer.Ordinal);

    // The text files this sync last agreed on, kept on the disk by hash so a merge has its base after a restart
    // (none when the state is kept inside the folder synced)
    private readonly HexLinkMergeBases? mergeBases;

    // Keeps the bytes of a file just agreed on in the store, off the caller's thread and outside the gate: a copy of a
    // file is a disk write the sync never waits for. A base that isn't written costs a two-way merge, nothing more.
    // Only for a real change of one file, never the bulk and line-end-only agreements, which would copy the repository.
    private void KeepBase(byte[]? bytes)
    {
        if (bytes is null || bytes.Length > HexLinkMerge.CACHE_FILE_BYTES || mergeBases is not { } store)
            return;
        _ = Task.Run(() =>
        {
            try
            {
                store.Add(bytes);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Never reaches the sync: a base not kept only means a comparison without it
            }
        });
    }

    // The agreed base of a path, read under the gate by the caller before RemoveBase drops it
    private HexLinkSyncAgreed? AgreedFor(string wire) => bases.GetValueOrDefault(wire);

    // A conflict is about to take the base out of the sync's list: whatever bytes of it are known (the memory cache or
    // Git's last version of the file) go to the store so the merge editor can still find them by the conflict's hash
    private async Task KeepConflictBase(string full, HexLinkSyncAgreed? agreed, CancellationToken stop)
    {
        if (agreed is null || await MergeBase(full, agreed, stop) is not { } basis)
            return;
        KeepBase(basis);
    }

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

        if (SameText(localBytes, remoteFile.Bytes, ignoreUtf8Bom: true))
        {
            lock (gate)
                SetBase(wire, AgreedOf(localBytes, remoteFile.Bytes) with { Executable = remoteFile.Executable });
            Interlocked.Increment(ref eolOnly);
            Notify();
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
                conflicts[wire] = new HexLinkSyncConflict(wire, "changed on both", copy, copyHash, agreed.Local);
                RemoveBase(wire);
            }

            // The base is about to leave the sync's list, so the conflict keeps its hash and the store its bytes
            KeepBase(basis);
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

    private Task<byte[]?> MergeBase(string full, HexLinkSyncAgreed agreed, CancellationToken stop) =>
        MergeBase(full, agreed.Local, agreed.Text, stop);

    // The common base of a file by the hash of this side's bytes when both agreed, looked for in the memory cache, then
    // in the store kept on the disk (which is what survives a restart), then in Git. The text it was agreed with is
    // only known while the sync still has the base, a conflict kept alone has the hash.
    private async Task<byte[]?> MergeBase(string full, string hash, string? text, CancellationToken stop)
    {
        if (HexLinkMerge.CachedBase(hash) is { } cached)
            return cached;
        if (await Task.Run(() => mergeBases?.Get(hash), stop) is { } stored)
        {
            HexLinkMerge.RememberBase(hash, stored);
            return stored;
        }

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
            return HexLinkFiles.Hash(candidate) == hash || digest is not null && digest == text
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
        KeepBase(localBytes);
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
            // Deleted here and changed there leaves no copy beside it, but the other device still has its version
            bool theirsExists;
            lock (gate)
                theirsExists = remote.TryGetValue(wire, out HexLinkTreeFile? there) && there.Hash is not null && !there.Unavailable;
            if (theirs is null && mine is null && theirsExists)
            {
                try { theirs = (await Fetch(wire, cancel)).Bytes; }
                catch (HexLinkException) { }
            }
            if (mine is null || theirs is null)
                model = MergeModel.Binary([], mine ?? [], theirs ?? []);
            else if (conflict.Base is { } baseHash && await MergeBase(full, baseHash, null, cancel) is { } basis)
            {
                // The draft is gone (a restart) but the base isn't: merged region by region, so only the regions both
                // sides changed are conflicts instead of the whole file
                model = await MergeModel.FromThree(basis, mine, theirs, cancel);
            }
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
                    })
                    {
                        // One side deleted it and the other changed it: each side said by what it did, and each button by what it keeps
                        MineButton = mine is null && theirs is not null ? "Keep deleted" : theirs is null && mine is not null ? "Keep my version" : null,
                        TheirsButton = mine is null && theirs is not null ? "Keep their version" : theirs is null && mine is not null ? "Keep deleted" : null,
                        MineDescription = mine is null && theirs is not null ? "Deleted here" : null,
                        TheirsDescription = mine is null && theirs is not null ? $"Changed on {peer.Device.Name}  {theirs.LongLength:N0} bytes"
                            : theirs is null && mine is not null ? $"Deleted on {peer.Device.Name}" : null,
                    },
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

        return new(Path.GetFileName(full), "Your copy", $"{peer.Device.Name}'s copy", "Original", model, async bytes =>
        {
            string resolved = HexLinkFiles.Decode(bytes)?.Text ?? throw new IOException("The merge result is not text.");
            if (await PublishMerge(wire, preview!, resolved, cancel) is { } why)
                throw new IOException(why);
        }, static () => { }, full) { LinkDevice = peer.Device.Name };
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
            byte[]? localMade = HexLinkMerge.EncodeKeeping(resolved, localBytes, remoteFile.Bytes),
                remoteMade = HexLinkMerge.EncodeKeeping(resolved, remoteFile.Bytes, localBytes);
            string name = Path.GetFileName(wire);
            if (localMade is null || remoteMade is null)
                return $"Hex couldn't write the merged text in the encoding {(localMade is null ? "here" : peer.Device.Name)} uses. Save {name} as plain text on both devices, then compare again.";
            if (localMade.Length > HexLinkFiles.MOST_READ || remoteMade.Length > HexLinkFiles.MOST_READ)
                return $"The merged {name} is larger than Link sends. Shorten it, then save again.";
            byte[] localResolved = localMade, remoteResolved = remoteMade;
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
