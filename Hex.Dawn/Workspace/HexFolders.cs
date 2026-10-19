using System.Collections.Concurrent;

namespace Hex.Dawn;

/// <summary>
/// What is in the folders the explorer shows, read once a folder and kept until a watcher on the folders of the
/// workspace says something in one changed. The explorer read the disk every time it was put together, which was every
/// time anything was selected.
/// </summary>
internal sealed class HexFolders : IDisposable
{
    private readonly Dictionary<string, (string[] Folders, string[] Files)> read = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> watchers = new(StringComparer.OrdinalIgnoreCase);

    // The folders something changed in, from the watchers' threads, let go of on the editor's
    private readonly ConcurrentQueue<string> changed = new();

    /// <summary>The folders inside of a folder and its files, sorted by name, nothing for one that can't be read.</summary>
    public (string[] Folders, string[] Files) In(string folder)
    {
        if (read.TryGetValue(folder, out var known))
            return known;

        try
        {
            known = (
                [.. Directory.GetDirectories(folder).Order(StringComparer.OrdinalIgnoreCase)],
                [.. Directory.GetFiles(folder).Order(StringComparer.OrdinalIgnoreCase)]);
        }
        catch (Exception)
        {
            known = ([], []);
        }

        read[folder] = known;
        return known;
    }

    /// <summary>Watches the folders of a workspace (and only them, the ones taken out of it stop being watched).</summary>
    public void Watch(IEnumerable<string> roots)
    {
        var wanted = new HashSet<string>(roots.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);

        foreach (string gone in watchers.Keys.Where(root => !wanted.Contains(root)).ToList())
        {
            watchers[gone].Dispose();
            watchers.Remove(gone);
        }

        foreach (string root in wanted)
        {
            if (watchers.ContainsKey(root) || !Directory.Exists(root))
                continue;

            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                };
                watcher.Created += (_, e) => Changed(e.FullPath);
                watcher.Deleted += (_, e) => Changed(e.FullPath);
                watcher.Renamed += (_, e) =>
                {
                    Changed(e.OldFullPath);
                    Changed(e.FullPath);
                };

                // Too much at once (a build, a checkout), it says nothing about what, so everything is read again
                watcher.Error += (_, _) => changed.Enqueue(string.Empty);
                watcher.EnableRaisingEvents = true;
                watchers[root] = watcher;
            }
            catch (Exception)
            {
                // A folder that can't be watched is read again when the explorer is told to, F5 on it
            }
        }
    }

    private void Changed(string path)
    {
        if (Path.GetDirectoryName(path) is { } folder)
            changed.Enqueue(folder);
    }

    /// <summary>
    /// Lets go of what was read of the folders the watchers said changed. True if any did, the explorer is put together
    /// again then. On the editor's thread.
    /// </summary>
    public bool TakeChanges()
    {
        bool any = false;
        while (changed.TryDequeue(out string? folder))
        {
            any = true;
            if (folder.Length == 0)
                read.Clear();
            else
                read.Remove(folder);
        }

        return any;
    }

    /// <summary>Forgets everything that was read, the next look reads the disk again.</summary>
    public void Forget() => read.Clear();

    public void Dispose()
    {
        foreach (var watcher in watchers.Values)
            watcher.Dispose();
        watchers.Clear();
    }
}
