using System.Collections.Concurrent;

namespace Hex.Dawn;

/// <summary>
/// Watches the files that are open for being written by something else, the game saving its options, git, Syncthing
/// bringing an edit over from another machine. A watcher a folder with an open file in it, the files it says changed
/// kept until they've been left alone a moment (a write comes as a few events, and an editor's save as a delete and a
/// rename), then handed over on the editor's thread. Nothing is polled.
/// </summary>
internal sealed class HexDiskWatch : IDisposable
{
    // How long a file has to be left alone after it changed before it's read
    private const long SETTLE_MS = 250;

    private readonly Dictionary<string, FileSystemWatcher> watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> watched = new(StringComparer.OrdinalIgnoreCase);

    // From the watchers' threads
    private readonly ConcurrentQueue<string> changed = new();

    // On the editor's thread, a file and when it last changed
    private readonly Dictionary<string, long> waiting = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> settled = [];

    /// <summary>The files to watch, the open ones with a file. The folders that have none of them any more stop being watched.</summary>
    public void Watch(IEnumerable<string> files)
    {
        watched.Clear();
        foreach (string file in files)
            watched.Add(Path.GetFullPath(file));

        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in watched)
        {
            if (Path.GetDirectoryName(file) is { } folder && Directory.Exists(folder))
                folders.Add(folder);
        }

        foreach (string gone in watchers.Keys.Where(folder => !folders.Contains(folder)).ToList())
        {
            watchers[gone].Dispose();
            watchers.Remove(gone);
        }

        foreach (string folder in folders)
        {
            if (watchers.ContainsKey(folder))
                continue;

            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    IncludeSubdirectories = false,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
                };
                watcher.Changed += (_, e) => changed.Enqueue(e.FullPath);
                watcher.Created += (_, e) => changed.Enqueue(e.FullPath);
                watcher.Deleted += (_, e) => changed.Enqueue(e.FullPath);
                watcher.Renamed += (_, e) => changed.Enqueue(e.FullPath);
                watcher.EnableRaisingEvents = true;
                watchers[folder] = watcher;
            }
            catch (Exception)
            {
                // a folder that can't be watched is checked when the file is saved, see HexScene.SaveTo
            }
        }
    }

    /// <summary>
    /// The watched files that changed and have been left alone long enough to read, none most ticks. The list is the
    /// watch's own and good until the next call. On the editor's thread.
    /// </summary>
    public IReadOnlyList<string> Settled()
    {
        settled.Clear();
        if (changed.IsEmpty && waiting.Count == 0)
            return settled;

        long now = Environment.TickCount64;
        while (changed.TryDequeue(out string? path))
        {
            if (watched.Contains(path))
                waiting[path] = now;
        }

        foreach (var (path, at) in waiting)
        {
            if (now - at >= SETTLE_MS)
                settled.Add(path);
        }

        foreach (string path in settled)
            waiting.Remove(path);
        return settled;
    }

    /// <summary>A file that couldn't be read yet (somebody still writing it) is looked at again a moment later.</summary>
    public void Again(string path) => waiting[path] = Environment.TickCount64;

    public void Dispose()
    {
        foreach (var watcher in watchers.Values)
            watcher.Dispose();
        watchers.Clear();
    }
}
