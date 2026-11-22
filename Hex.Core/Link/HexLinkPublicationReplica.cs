namespace Hex.Core;

/// <summary>Projects one authenticated authority set. The adapter installs both sides' exact baselines before ACK.</summary>
internal sealed class HexLinkPublicationReplica(HexLinkCommittedSets receipts,
    Func<HexLinkFolder, HexExcludes> excludes,
    Func<string, string, string?, byte[]?, CancellationToken, Task<string?>> apply,
    Func<HexLinkFolder, HexLinkCommittedSet, CancellationToken, Task> installBaseline,
    Action<HexLinkCommittedSet>? committed = null)
{
    public void Restore(Func<HexLinkCommittedSet, HexLinkFolder> folder)
    {
        foreach (HexLinkCommittedSet set in receipts.UnfinishedIncoming())
            Scope(folder(set), set).HoldRecovery(set.Id, set.Kind);
    }

    public async Task<HexLinkCommittedAck> Receive(HexLinkFolder folder, HexLinkCommittedSet set,
        IReadOnlyList<HexLinkPublishEdit> edits, CancellationToken cancel)
    {
        if (set.Folder != folder.Name || edits.Count != set.Files.Count)
            throw HexLinkException.Invalid("The publication does not match this folder.");
        var fence = Scope(folder, set);
        await using var exclusive = await fence.EnterExclusive(set.Kind, cancel, recovery: set.Id);
        CancellationToken stop = exclusive.Stop;
        var (prepared, ack) = receipts.Incoming(set);
        if (ack is { } complete)
        {
            fence.ReleaseRecovery(set.Id);
            return complete;
        }

        var changes = new List<(HexLinkCommittedFile Entry, string Full, byte[]? Bytes, bool Apply)>(edits.Count);
        for (int i = 0; i < edits.Count; i++)
        {
            HexLinkCommittedFile entry = set.Files[i];
            HexLinkPublishEdit edit = edits[i];
            if (entry.Path != edit.Path || entry.Base != edit.Base ||
                entry.Hash != (edit.Bytes is null ? null : HexLinkFiles.Hash(edit.Bytes)))
                throw HexLinkException.Invalid("The incoming bytes do not match the publication.");

            string full = HexLinkFiles.Resolve([folder], entry.Path, excludes(folder));
            string? current = await CurrentHash(full, stop);
            if (current != entry.Base && !(prepared && current == entry.Hash))
                throw new HexLinkException(HexLinkErrors.CONFLICT, $"{entry.Path} changed before the committed set could land.");
            changes.Add((entry, full, edit.Bytes, current != entry.Hash));
        }

        receipts.PrepareIncoming(set);
        fence.HoldRecovery(set.Id, set.Kind);
        foreach (var change in changes)
        {
            if (change.Apply)
                await apply(change.Full, change.Entry.Path, change.Entry.Base, change.Bytes, stop);
        }
        await installBaseline(folder, set, stop);
        HexLinkCommittedAck receipt = receipts.CompleteIncoming(set);
        fence.ReleaseRecovery(set.Id);
        committed?.Invoke(set);
        return receipt;
    }

    private HexLinkPublicationFence Scope(HexLinkFolder folder, HexLinkCommittedSet set) =>
        HexLinkPublicationFence.ForRoot(HexLinkFiles.Resolve([folder],
            set.Scope.Length == 0 ? folder.Name : folder.Name + "/" + set.Scope, excludes(folder)));

    internal static async Task<string?> CurrentHash(string full, CancellationToken cancel)
    {
        try { return HexLinkFiles.Hash(await HexLinkFiles.ReadSharedAsync(full, cancel)); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
