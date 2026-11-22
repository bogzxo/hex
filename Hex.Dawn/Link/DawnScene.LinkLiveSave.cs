using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private bool LinkLiveHasLocalFile(LinkLiveDocument shared, string full, bool requirePin = false)
    {
        if (LinkLiveOf(shared.Tab) != shared || shared.Draft is not null ||
            shared.Tab.Path is not { } path || !PlatformPaths.Same(path, full) ||
            linkServices is not { } services || LiveFolder(shared.Wire) is not { } folder)
            return false;
        var pin = services.FolderAuthorities.Of(folder);
        if (pin != shared.Authority && (requirePin || pin is not null))
            return false;
        try { return PlatformPaths.Same(HexLinkFiles.Resolve([folder], shared.Wire, services.Excludes), full); }
        catch (HexLinkException) { return false; }
    }

    private async Task<HexDocSaved?> LinkLiveKnownSyncSave(LinkLiveDocument shared, string full, string hash,
        string textHash, CancellationToken cancel)
    {
        var (valid, peer, previous) = await OnEditorForLink(() =>
            (LinkLiveHasLocalFile(shared, full), LinkLiveRoute(shared.Authority, shared.Connection), shared.Saved))
            .WaitAsync(cancel).ConfigureAwait(false);
        if (!valid)
            return null;
        HexDocSaved? receipt;
        try
        {
            var saved = shared.Room is { } room
                ? await room.ReadSaved(null, includeText: false, cancel).ConfigureAwait(false)
                : peer is null ? null : await peer.ReadSavedDocument(shared.Wire, includeText: false, cancel,
                    LinkLiveRelay(shared.Authority, peer)).ConfigureAwait(false);
            receipt = saved?.Receipt;
        }
        catch (HexLinkException e) when (e.Code == HexLinkErrors.METHOD_NOT_FOUND)
        {
            // An older authority can only attest the exact file bytes.
            return previous is not null && StringComparer.OrdinalIgnoreCase.Equals(previous.Hash, hash) ? previous : null;
        }
        catch (HexLinkException e) when (e.Code is HexLinkErrors.NOT_FOUND or HexLinkErrors.NOT_ALLOWED)
        {
            return null;
        }
        return receipt is not null && receipt.Matches(hash, textHash) ? receipt : null;
    }

    private async Task<bool> LinkLiveSaveRelayed(LinkLiveDocument shared, HexLinkPeer peer, string full,
        HexDocFileBasis basis, HexDocSaved receipt, CancellationToken cancel)
    {
        if (!await OnEditorForLink(() => LinkLiveHasLocalFile(shared, full, requirePin: true))
            .WaitAsync(cancel).ConfigureAwait(false))
            return false;
        var saved = await peer.ReadSavedDocument(shared.Wire, includeText: true, cancel,
            LinkLiveRelay(shared.Authority, peer)).ConfigureAwait(false);
        if (saved is not { Text: { } text } || saved.Receipt != receipt)
            return false;

        string raw = DocumentFile.WithLineEndings(text, basis.Text, basis.CrLf, basis.CrLf);
        byte[] bytes = basis.Bytes(raw);
        await LinkLiveWriteSaved(shared, receipt, full, shared.Wire, basis.Expected, bytes, HexLinkFiles.Hash(bytes),
            raw, basis.Text, basis.Encoding, cancel, relayed: true).ConfigureAwait(false);
        return true;
    }

    private async Task<string?> LinkLiveWriteSaved(LinkLiveDocument shared, HexDocSaved receipt, string full,
        string wire, string? baseHash, byte[] bytes, string hash, string raw, string? before, Encoding encoding,
        CancellationToken cancel, bool? executable = null, HexLinkFiles.PackDirectories? directories = null, bool relayed = false)
    {
        bool active = await OnEditorForLink(() =>
        {
            if (cancel.IsCancellationRequested || !LinkLiveHasLocalFile(shared, full, requirePin: relayed) ||
                shared.Tab.DiskText != before || LinkLiveOlderSave(receipt, shared.DiskSave))
                return false;
            shared.TestBeforeDiskSave?.Invoke();
            shared.DiskSaveInFlight++;
            return true;
        }).ConfigureAwait(false);
        if (!active)
        {
            cancel.ThrowIfCancellationRequested();
            throw LinkLiveSaveConflict(full, wire);
        }

        try
        {
            string? written = await HexLinkFiles.CompareAndReplace(full, wire, baseHash, bytes, cancel, executable, directories)
                .ConfigureAwait(false);
            await OnEditorForLink(() =>
            {
                LinkLiveSetDiskSave(shared, receipt, raw, before, encoding);
                return true;
            }).ConfigureAwait(false);
            QueuePrivateFollowForPath(full);
            return written;
        }
        catch (HexLinkException e) when (relayed && e.Code == HexLinkErrors.CONFLICT && HexLinkFiles.HashOf(full) == hash)
        {
            // Another local instance can share this working directory and have published the same revision.
            await OnEditorForLink(() =>
            {
                LinkLiveSetDiskSave(shared, receipt, raw, before, encoding);
                return true;
            }).ConfigureAwait(false);
            return hash;
        }
        finally
        {
            PostLink(() => shared.DiskSaveInFlight--);
        }
    }

    // Another Hex with this same checkout can receive file sync before our room update arrives.
    private bool LinkLiveCheckObservedDisk(LinkLiveDocument shared, string path, string raw)
    {
        if (!LinkLiveHasLocalFile(shared, path) || LinkLiveRoute(shared.Authority, shared.Connection) is null)
            return false;
        if (shared.CheckingObservedSave)
            disk.Again(path);
        else
        {
            shared.CheckingObservedSave = true;
            _ = LinkLiveReviewObservedDisk(shared, path, raw, shared.Tab.DiskText, linkCancel?.Token ?? default);
        }
        return true;
    }

    private async Task LinkLiveReviewObservedDisk(LinkLiveDocument shared, string path, string raw, string? before,
        CancellationToken cancel)
    {
        HexDocSaved? receipt = null;
        try
        {
            string digest = await Task.Run(() => HexDocSaved.TextDigest(raw), cancel).ConfigureAwait(false);
            receipt = await LinkLiveKnownSyncSave(shared, path, string.Empty, digest, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HexLinkException or IOException or TimeoutException or OperationCanceledException) { }

        PostLink(() =>
        {
            shared.CheckingObservedSave = false;
            if (cancel.IsCancellationRequested || Context.Documents.Find(path) != shared.Tab || shared.Tab.DiskText != before)
                return;
            if (shared.DiskSaveInFlight > 0 || ReadNow(path) != raw)
                disk.Again(path);
            else if (receipt is not null && LinkLiveHasLocalFile(shared, path))
                LinkLiveSetDiskSave(shared, receipt, raw, before);
            else
                DiskChangedText(shared.Tab, raw);
        });
    }

    private static bool LinkLiveOlderSave(HexDocSaved receipt, HexDocSaved? current) =>
        current is not null && receipt.Epoch == current.Epoch && receipt.Revision < current.Revision;

    private static void LinkLiveRememberSaved(LinkLiveDocument shared, HexDocSaved? receipt)
    {
        if (receipt is not null && !LinkLiveOlderSave(receipt, shared.Saved))
            shared.Saved = receipt;
    }

    private void LinkLiveSetDiskSave(LinkLiveDocument shared, HexDocSaved receipt, string raw, string? before,
        Encoding? encoding = null)
    {
        var tab = shared.Tab;
        if (LinkLiveOf(tab) != shared || shared.Draft is not null || LinkLiveOlderSave(receipt, shared.DiskSave) ||
            tab.DiskText != before && tab.DiskText != raw)
            return;

        bool crlf = raw.Contains("\r\n", StringComparison.Ordinal);
        if (tab.CrLf == tab.SavedCrLf)
            tab.CrLf = crlf;
        tab.SavedCrLf = crlf;
        tab.DiskText = raw;
        if (encoding is not null)
            tab.DiskEncoding = encoding;
        tab.SavedCode = HexDocSaved.NormalText(raw);
        shared.DiskSave = receipt;
        LinkLiveRememberSaved(shared, receipt);
        tab.Modified = HasChanges(tab);
        tabsDirty = true;
    }

    private static HexLinkException LinkLiveSaveConflict(string full, string wire) =>
        new(HexLinkErrors.CONFLICT, $"{wire} is open in a shared document.",
            new JsonObject { ["hash"] = HexLinkFiles.HashOf(full), ["editor_dirty"] = true });
}
