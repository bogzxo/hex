namespace Hex.Core;

internal sealed partial class HexLinkTree
{
    private static readonly Lock sharedTrees = new();
    private static readonly Dictionary<(string Root, string Name, string? Cache, HexExcludes Rules), WeakReference<HexLinkTree>> trees = [];
    private bool shareFreshScans;
    private long scanVersion, lastScanVersion, scannedAt;
    private HexLinkManifest? lastScan;
    private int publishingPacks;
    private long packQuietAt;

    internal HexLinkManifest? PreviousManifest => Volatile.Read(ref lastScan);

    internal bool DelayDirectoryRescan => Volatile.Read(ref publishingPacks) != 0 ||
        Environment.TickCount64 < Volatile.Read(ref packQuietAt);

    internal PackPublication BeginPackPublication()
    {
        Interlocked.Increment(ref publishingPacks);
        return new PackPublication(this);
    }

    internal readonly struct PackPublication(HexLinkTree tree) : IDisposable
    {
        public void Dispose()
        {
            Volatile.Write(ref tree.packQuietAt, Environment.TickCount64 + 500);
            Interlocked.Decrement(ref tree.publishingPacks);
        }
    }

    public static HexLinkTree Shared(HexLinkFolder folder, Func<HexExcludes> excludes, string? cacheDirectory, bool streamLarge)
    {
        string full = Path.GetFullPath(folder.Path);
        string keyRoot = PlatformPaths.Comparison == StringComparison.OrdinalIgnoreCase ? full.ToUpperInvariant() : full;
        var key = (keyRoot, folder.Name, cacheDirectory is null ? null : Path.GetFullPath(cacheDirectory), excludes());
        lock (sharedTrees)
        {
            if (trees.TryGetValue(key, out WeakReference<HexLinkTree>? known) && known.TryGetTarget(out HexLinkTree? tree))
            {
                if (streamLarge && !tree.streamLarge)
                {
                    tree.streamLarge = true;
                    tree.InvalidateSnapshot();
                }
                return tree;
            }

            if (trees.Count > 128)
            {
                foreach (var old in trees.Where(each => !each.Value.TryGetTarget(out _)).Select(each => each.Key).ToArray())
                    trees.Remove(old);
            }

            tree = new HexLinkTree(folder, excludes, cacheDirectory, streamLarge) { shareFreshScans = true };
            trees[key] = new WeakReference<HexLinkTree>(tree);
            return tree;
        }
    }

    internal void InvalidateSnapshot() => Interlocked.Increment(ref scanVersion);

    internal static HexLinkManifest ForLegacy(HexLinkManifest manifest) =>
        manifest.Files.Any(file => file.Size > HexLinkFiles.MOST_READ && !file.Big)
            ? manifest with
            {
                Files = [.. manifest.Files.Select(file => file.Size > HexLinkFiles.MOST_READ
                    ? file with { Hash = null, Text = null, Eol = null, Executable = null }
                    : file)]
            }
            : manifest;

    internal static HexLinkTreeChange ForLegacy(HexLinkTreeChange change) =>
        change.Size > HexLinkFiles.MOST_READ
            ? change with { Hash = null, Text = null, Eol = null, Executable = null }
            : change;
}

/// <summary>One watcher for a local tree and an ordered subscription per sync or linked device.</summary>
internal sealed class HexLinkTreeSubscription : IDisposable
{
    private static readonly Lock subscriptions = new();
    private static readonly Dictionary<HexLinkTree, SharedWatch> watches = [];

    private sealed class SharedWatch
    {
        private readonly List<HexLinkTreeSubscription> subscribers = [];
        private bool seeded;

        public SharedWatch(HexLinkTree tree, TimeSpan quiet, TimeSpan rescan)
        {
            Tree = tree;
            Watch = new HexLinkTreeWatch(tree, FanOut, quiet, rescan);
        }

        public HexLinkTree Tree { get; }
        public HexLinkTreeWatch Watch { get; }

        public void Add(HexLinkTreeSubscription subscriber) => subscribers.Add(subscriber);

        public bool Remove(HexLinkTreeSubscription subscriber)
        {
            subscribers.Remove(subscriber);
            return subscribers.Count == 0;
        }

        public void Seed(HexLinkManifest snapshot)
        {
            if (seeded)
                return;
            seeded = true;
            Watch.Seed(snapshot);
        }

        private void FanOut(IReadOnlyList<HexLinkTreeChange> batch)
        {
            HexLinkTreeSubscription[] listening;
            lock (subscriptions)
                listening = [.. subscribers];
            foreach (HexLinkTreeSubscription subscriber in listening)
                subscriber.Deliver(batch);
        }
    }

    private readonly SharedWatch shared;
    private readonly Lock delivery = new();
    private readonly Action<IReadOnlyList<HexLinkTreeChange>> changed;
    private List<IReadOnlyList<HexLinkTreeChange>>? waiting = [];
    private int waitingCount;
    private bool closed;

    public HexLinkTreeSubscription(HexLinkTree tree, Action<IReadOnlyList<HexLinkTreeChange>> changed, TimeSpan quiet, TimeSpan rescan)
    {
        this.changed = changed;
        lock (subscriptions)
        {
            if (!watches.TryGetValue(tree, out SharedWatch? found))
            {
                // The preceding snapshot may straddle a period with no watcher.
                tree.InvalidateSnapshot();
                watches.Add(tree, found = new SharedWatch(tree, quiet, rescan));
            }
            shared = found;
            shared.Add(this);
        }
    }

    private void Deliver(IReadOnlyList<HexLinkTreeChange> batch)
    {
        lock (delivery)
        {
            if (closed)
                return;
            if (waiting is null)
            {
                changed(batch);
                return;
            }

            if (waitingCount <= HexLinkTree.MOST_FILES)
            {
                waitingCount += batch.Count;
                if (waitingCount <= HexLinkTree.MOST_FILES)
                    waiting.Add(batch);
            }
        }
    }

    public void Seed(HexLinkManifest snapshot)
    {
        lock (delivery)
        {
            if (closed || waiting is not { } batches)
                return;
            if (waitingCount > HexLinkTree.MOST_FILES)
                throw new HexLinkException(HexLinkErrors.BUSY, "The shared Link watcher saw too many changes while the manifest was read.");
            lock (subscriptions)
                shared.Seed(snapshot);
            waiting = null;
            foreach (IReadOnlyList<HexLinkTreeChange> batch in batches)
                changed(batch);
        }
    }

    public Task Flush() => shared.Watch.Flush();

    public void Dispose()
    {
        lock (delivery)
        {
            if (closed)
                return;
            closed = true;
            waiting = null;
        }

        bool last;
        lock (subscriptions)
        {
            last = shared.Remove(this);
            if (last)
            {
                shared.Tree.InvalidateSnapshot();
                watches.Remove(shared.Tree);
            }
        }
        if (last)
            shared.Watch.Dispose();
    }
}
