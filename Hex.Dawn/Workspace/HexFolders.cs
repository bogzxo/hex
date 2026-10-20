using System.Collections.Concurrent;

using Horizon.UI.Scripting;

namespace Hex.Dawn;

/// <summary>
/// What is in the folders the explorer shows, read once a folder and kept until a watcher on the folders of the
/// workspace says something in one changed. The explorer read the disk every time it was put together, which was every
/// time anything was selected.
/// </summary>
internal sealed class HexFolders : IDisposable
{
    private readonly Dictionary<string, (string[] Folders, string[] Files)> read = new(PlatformPaths.Comparer);
    private readonly Dictionary<string, FileSystemWatcher> watchers = new(PlatformPaths.Comparer);

    // The folders something changed in, from the watchers' threads, let go of on the editor's
    private readonly ConcurrentQueue<string> changed = new();

    // What happened to files, for the language servers (HexScene.Linting), the most kept between two looks at them
    private readonly ConcurrentQueue<(string Path, LanguageServerFileChange Change)> files = new();
    private const int MOST_FILE_CHANGES = 2000;

    // Folders whose files change all the time and mean nothing to a server, a build's and git's
    private static readonly string[] Unwatched = ["bin", "obj", ".git", ".vs", "node_modules"];

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
        var wanted = new HashSet<string>(roots.Select(Path.GetFullPath), PlatformPaths.Comparer);

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
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                };
                watcher.Created += (_, e) =>
                {
                    Changed(e.FullPath);
                    FileChanged(e.FullPath, LanguageServerFileChange.Created);
                };
                watcher.Deleted += (_, e) =>
                {
                    Changed(e.FullPath);
                    FileChanged(e.FullPath, LanguageServerFileChange.Deleted);
                };
                watcher.Renamed += (_, e) =>
                {
                    Changed(e.OldFullPath);
                    Changed(e.FullPath);
                    FileChanged(e.OldFullPath, LanguageServerFileChange.Deleted);
                    FileChanged(e.FullPath, LanguageServerFileChange.Created);
                };

                // What's in a file changing changes nothing the explorer shows, only what a server knows
                watcher.Changed += (_, e) => FileChanged(e.FullPath, LanguageServerFileChange.Changed);

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

    private void FileChanged(string path, LanguageServerFileChange change)
    {
        if (files.Count >= MOST_FILE_CHANGES)
            return;

        foreach (string part in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (Array.Exists(Unwatched, folder => folder.Equals(part, StringComparison.OrdinalIgnoreCase)))
                return;
        }

        // A folder's own writes (its time changing) are nothing
        if (change == LanguageServerFileChange.Changed && Directory.Exists(path))
            return;

        files.Enqueue((path, change));
    }

    /// <summary>What happened to files since the last time this was asked, each file once with what happened last. On the editor's thread.</summary>
    public IReadOnlyList<(string Path, LanguageServerFileChange Change)> TakeFileChanges()
    {
        // Asked every tick, nothing made when nothing happened
        if (files.IsEmpty)
            return [];

        var latest = new Dictionary<string, LanguageServerFileChange>(PlatformPaths.Comparer);
        while (files.TryDequeue(out var file))
        {
            // Made and written to before anybody looked is made
            latest[file.Path] = file.Change == LanguageServerFileChange.Changed && latest.TryGetValue(file.Path, out var before) && before == LanguageServerFileChange.Created
                ? LanguageServerFileChange.Created
                : file.Change;
        }

        return [.. latest.Select(pair => (pair.Key, pair.Value))];
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
