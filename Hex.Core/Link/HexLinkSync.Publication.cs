namespace Hex.Core;

internal sealed partial class HexLinkSync
{
    private sealed class PublicationPath
    {
        public long Observed;
        public readonly Dictionary<Guid, long> Receipts = [];
    }

    // The outbox is durable. Restore its pending sets into this table before the initial comparison.
    private readonly Dictionary<string, PublicationPath> publicationPaths = new(StringComparer.Ordinal);
    private int hasPublicationPaths;

    internal bool PublicationPairReady { get { lock (gate) return live && ready; } }

    internal HexLinkFenceStatus? PublicationFenceStatus => HexLinkPublicationFence.ForRoot(tree.Root).Status;

    internal void DeferPublication(HexLinkCommittedSet set)
    {
        RequirePublicationFolder(set);
        lock (gate)
        {
            foreach (var file in set.Files)
            {
                if (!publicationPaths.TryGetValue(file.Path, out PublicationPath? path))
                    publicationPaths.Add(file.Path, path = new());
                path.Receipts.TryAdd(set.Id, path.Observed);
            }
            Volatile.Write(ref hasPublicationPaths, publicationPaths.Count);
        }
    }

    // Check after taking the live lease, before making a comparison or CAS decision for this pair.
    internal bool DefersPublication(string path)
    {
        if (Volatile.Read(ref hasPublicationPaths) == 0)
            return false;
        lock (gate)
            return publicationPaths.ContainsKey(path);
    }

    // Record even removals. An ACK for an earlier projection must preserve a later watch observation.
    internal void ObserveDuringPublication(string path)
    {
        if (Volatile.Read(ref hasPublicationPaths) == 0)
            return;
        lock (gate)
            if (publicationPaths.TryGetValue(path, out PublicationPath? pending))
                pending.Observed++;
    }

    // Call under the exclusive scope before durably acknowledging the receipt. Keep the deferrals until that ACK is on disk.
    internal void InstallPublicationBaseline(HexLinkCommittedSet set)
    {
        RequirePublicationFolder(set);
        long minimum;
        lock (gate)
        {
            if (!live || !ready)
                throw new HexLinkException(HexLinkErrors.BUSY, "Wait for the sync pair before saving a publication baseline.");
            foreach (var file in set.Files)
            {
                RemoveBase(file.Path);
                if (file.Hash is { } hash)
                {
                    HexLinkSyncAgreed agreed = Identical(hash);
                    bases[file.Path] = agreed;
                    Count(agreed, 1);
                }

                if (publicationPaths.TryGetValue(file.Path, out PublicationPath? path) &&
                    path.Receipts.TryGetValue(set.Id, out long observed) && observed == path.Observed)
                    There(file.Path, file.Hash);
            }
            minimum = snapshotVersion + 1;
        }

        Save();
        lock (saving)
            if (writtenVersion < minimum)
                throw new IOException("The publication sync baseline could not be saved.");
    }

    // The outbox has removed this peer from the durable receipt. Requeue the paths so any newer observation is compared.
    internal void ReleasePublication(HexLinkCommittedSet set)
    {
        RequirePublicationFolder(set);
        var requeue = new List<string>();
        lock (gate)
        {
            foreach (var file in set.Files)
            {
                if (!publicationPaths.TryGetValue(file.Path, out PublicationPath? path) ||
                    !path.Receipts.Remove(set.Id) || path.Receipts.Count != 0)
                    continue;
                publicationPaths.Remove(file.Path);
                requeue.Add(file.Path);
            }
            Volatile.Write(ref hasPublicationPaths, publicationPaths.Count);
        }

        foreach (string path in requeue)
            Dirty(path);
        Notify();
    }

    private void RequirePublicationFolder(HexLinkCommittedSet set)
    {
        if (set.Folder != folder.Name || set.Files.Any(file => Ours(file.Path)))
            throw HexLinkException.Invalid("The committed set is outside this sync pair.");
    }
}
