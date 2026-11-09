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

    /// <summary>Somebody starts watching, the folders looked at every couple of seconds from now on until they stop.</summary>
    public void Watch()
    {
        if (Interlocked.Increment(ref watchers) == 1)
            watching = new Timer(_ => { try { Look(); } catch (Exception) { } }, null, StaleAfter, StaleAfter);
    }

    /// <summary>Somebody stops watching, the looking stopped once nobody is.</summary>
    public void Unwatch()
    {
        if (Interlocked.Decrement(ref watchers) <= 0)
        {
            watchers = 0;
            watching?.Dispose();
            watching = null;
        }
    }
}
