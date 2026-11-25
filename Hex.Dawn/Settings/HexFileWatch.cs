using System.Diagnostics;

namespace Hex.Dawn;

/// <summary>
/// A file watched for changes from outside Hex, a burst of them (an editor's save is several) taken as one once it has
/// settled. The watcher tells on a thread of the pool, the editor asks every tick on its own whether there's a settled
/// change to take in. What the settings file and the workspace's each kept a copy of.
/// </summary>
internal sealed class HexFileWatch : IDisposable
{
    private FileSystemWatcher? watcher;
    private long changedAt;
    private int changed;

    /// <summary>
    /// Watches <paramref name="filter"/> in <paramref name="folder"/> from now on, what was watched before let go of. The
    /// reason it couldn't, null when it could.
    /// </summary>
    public string? Watch(string folder, string filter, NotifyFilters notify, bool deletes = false)
    {
        Stop();
        try
        {
            var made = new FileSystemWatcher(folder, filter) { NotifyFilter = notify };
            made.Changed += Touched;
            made.Created += Touched;
            made.Renamed += Touched;
            if (deletes)
                made.Deleted += Touched;
            made.EnableRaisingEvents = true;
            watcher = made;
            return null;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    /// <summary>Nothing watched, and a change heard and not taken in forgotten.</summary>
    public void Stop()
    {
        watcher?.Dispose();
        watcher = null;
        Interlocked.Exchange(ref changed, 0);
    }

    public void Dispose() => Stop();

    /// <summary>
    /// Whether a change from outside has settled for <paramref name="seconds"/> and is to be taken in now, once. Every
    /// tick on the editor's thread; while one is still settling the frames go on so it's looked at again.
    /// </summary>
    public bool TakeSettled(double seconds)
    {
        if (Volatile.Read(ref changed) == 0)
            return false;

        double remaining = seconds - Stopwatch.GetElapsedTime(Interlocked.Read(ref changedAt)).TotalSeconds;
        if (remaining > 0.0)
        {
            Horizon.Engine.GameEngine.Instance?.WindowManager.RequestTickIn(remaining);
            return false;
        }

        Interlocked.Exchange(ref changed, 0);
        return true;
    }

    // On a thread of the pool, noted for the editor's thread to take in once the burst settles
    private void Touched(object sender, FileSystemEventArgs e)
    {
        Interlocked.Exchange(ref changedAt, Stopwatch.GetTimestamp());
        Interlocked.Exchange(ref changed, 1);
        Horizon.Engine.GameEngine.Instance?.WindowManager.Wake();
    }
}
