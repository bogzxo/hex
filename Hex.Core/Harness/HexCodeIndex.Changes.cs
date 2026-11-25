namespace Hex.Core;

// What changed and when, for an agent that read something a while ago and wants to know what moved since without
// reading it all again. Every look that finds a file come, changed or gone puts it in the log at the generation it made,
// and an answer of the harness ends with the generation it's of. The log keeps the last few thousand, an agent asking
// from before that is told so and has to take everything as changed.
internal sealed partial class HexCodeIndex
{
    // The most changes kept
    private const int MOST_CHANGES = 5000;

    private readonly List<HexChange> changes = [];

    // The oldest generation the log still has everything since, under the lock
    private long logFrom;

    /// <summary>
    /// How many people are watching for changes. While there's anybody the folders are looked at every couple of
    /// seconds without being asked, so <see cref="Changed"/> goes off when somebody types into a file, not when a
    /// query happens to come.
    /// </summary>
    private int watchers;
    private Timer? watching;

    // The changes between two snapshots put in the log, how many there were. Under the lock
    private int Log(HexIndexSnapshot was, HexIndexedFile[] now)
    {
        long generation = was.Generation + 1;
        int logged = 0;
        var still = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (HexIndexedFile file in now)
        {
            still.Add(file.Path);
            HexIndexedFile? before = was.File(file.Path);
            if (before is null)
                logged += Note(new HexChange(generation, file.Relative, HexChange.ADDED));
            else if (before.Length != file.Length || before.Written != file.Written)
                logged += Note(new HexChange(generation, file.Relative, HexChange.CHANGED));
        }

        foreach (HexIndexedFile gone in was.Files)
        {
            if (!still.Contains(gone.Path))
                logged += Note(new HexChange(generation, gone.Relative, HexChange.GONE));
        }

        return logged;
    }

    private int Note(HexChange change)
    {
        changes.Add(change);
        if (changes.Count > MOST_CHANGES)
        {
            int drop = changes.Count - MOST_CHANGES;
            logFrom = changes[drop - 1].Generation;
            changes.RemoveRange(0, drop);
        }

        return 1;
    }

    /// <summary>
    /// The files that changed after a generation, each once with what last happened to it, and whether the log goes back
    /// that far (false means more may have changed than it says).
    /// </summary>
    public (List<HexChange> Changes, bool Complete) ChangesSince(long since)
    {
        Look();
        lock (looking)
        {
            var last = new Dictionary<string, HexChange>(StringComparer.OrdinalIgnoreCase);
            foreach (HexChange change in changes)
            {
                if (change.Generation > since)
                    last[change.Relative] = change;
            }

            return ([.. last.Values.OrderBy(change => change.Relative, StringComparer.OrdinalIgnoreCase)], since >= logFrom);
        }
    }

    // How long a burst of file events is let settle before the one look that answers it
    private const int SETTLE_MS = 300;

    // How often the watchers are checked on while nothing happens: a folder added to the workspace is watched, and a
    // watcher that overflowed or failed has the folders looked at in full, the only time they're walked unasked
    private const int FALLBACK_MS = 3 * 60 * 1000;

    private readonly Lock watchLock = new();
    private readonly List<FileSystemWatcher> watchers_ = [];
    private string[] watchedRoots = [];
    private Timer? settle;
    private int dirty;
    private int watcherFailed;

    /// <summary>Somebody starts watching, the folders' own file events telling when to look until they stop.</summary>
    public void Watch()
    {
        if (Interlocked.Increment(ref watchers) != 1)
            return;

        lock (watchLock)
        {
            settle = new Timer(_ => Settled(), null, Timeout.Infinite, Timeout.Infinite);
            watching = new Timer(_ => Fallback(), null, FALLBACK_MS, FALLBACK_MS);
            Rewatch();
        }
    }

    /// <summary>Somebody stops watching, the watching stopped once nobody is.</summary>
    public void Unwatch()
    {
        if (Interlocked.Decrement(ref watchers) <= 0)
        {
            watchers = 0;
            lock (watchLock)
            {
                watching?.Dispose();
                watching = null;
                settle?.Dispose();
                settle = null;
                foreach (FileSystemWatcher watcher in watchers_)
                    watcher.Dispose();
                watchers_.Clear();
                watchedRoots = [];
            }
        }
    }

    // A watcher for each folder of the workspace now, those of before let go. Under watchLock
    private void Rewatch()
    {
        string[] now;
        try { now = [.. roots().Select(root => Path.GetFullPath(root).TrimEnd('\\', '/'))]; }
        catch (Exception) { return; }

        foreach (FileSystemWatcher watcher in watchers_)
            watcher.Dispose();
        watchers_.Clear();
        watchedRoots = now;
        Volatile.Write(ref watcherFailed, 0);
        foreach (string root in now)
        {
            if (!Directory.Exists(root))
                continue;
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };
                watcher.Changed += Event;
                watcher.Created += Event;
                watcher.Deleted += Event;
                watcher.Renamed += Event;
                watcher.Error += (_, _) => { Volatile.Write(ref watcherFailed, 1); Arm(); };
                watcher.EnableRaisingEvents = true;
                watchers_.Add(watcher);
            }
            catch (Exception)
            {
                Volatile.Write(ref watcherFailed, 1);
            }
        }
    }

    // A file event, git's own churn left out, starts (or restarts) the settling that ends in one look
    private void Event(object sender, FileSystemEventArgs e)
    {
        if (e.FullPath.Contains(@"\.git\", StringComparison.Ordinal) || e.FullPath.Contains("/.git/", StringComparison.Ordinal))
            return;
        Arm();
    }

    private void Arm()
    {
        Volatile.Write(ref dirty, 1);
        try { settle?.Change(SETTLE_MS, Timeout.Infinite); } catch (ObjectDisposedException) { }
    }

    private void Settled()
    {
        if (Interlocked.Exchange(ref dirty, 0) == 0)
            return;
        try { Look(force: true); } catch (Exception) { }
    }

    // The slow check: folders added to or taken out of the workspace are watched or let go, and after an error the walk
    // is made in full
    private void Fallback()
    {
        try
        {
            bool failed = Volatile.Read(ref watcherFailed) == 1;
            string[] now = [.. roots().Select(root => Path.GetFullPath(root).TrimEnd('\\', '/'))];
            bool moved = !now.SequenceEqual(watchedRoots, StringComparer.OrdinalIgnoreCase);
            if (moved || failed)
            {
                lock (watchLock)
                {
                    if (watching is not null)
                        Rewatch();
                }

                Look(force: true);
            }
        }
        catch (Exception) { }
    }
}
