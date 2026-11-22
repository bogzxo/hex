using System.Collections.Concurrent;

using Hex.Core.Git;

namespace Hex.Core;

internal sealed partial class HexLinkTree
{
    // A tracked path is not subject to Git ignore rules. Check membership against the exact bytes of the
    // current index. Untracked paths and indexes outside this fast path still go through git check-ignore.
    private const int MaximumIndexBytes = 16 * 1024 * 1024;
    private readonly ConcurrentDictionary<string, TrackedIndex> trackedIndexes = new(PlatformPaths.Comparer);

    private sealed class TrackedIndex
    {
        public readonly Lock Gate = new();
        public string? Hash;
        public IReadOnlySet<string>? Paths;
    }

    private IReadOnlySet<string>? TrackedNow(string repository)
    {
        if (Environment.GetEnvironmentVariable("GIT_INDEX_FILE") is not null ||
            Environment.GetEnvironmentVariable("GIT_DIR") is not null ||
            Environment.GetEnvironmentVariable("GIT_WORK_TREE") is not null ||
            Environment.GetEnvironmentVariable("GIT_COMMON_DIR") is not null)
            return null;

        string git = Path.Combine(repository, ".git");
        string index = Path.Combine(git, "index");
        try
        {
            if ((File.GetAttributes(git) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory ||
                (File.GetAttributes(index) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return null;
            TrackedIndex cached = trackedIndexes.GetOrAdd(repository, static _ => new TrackedIndex());
            lock (cached.Gate)
            {
                using FileStream read = HexLinkFileTransfer.Read(index);
                if (read.Length is < 12 or > MaximumIndexBytes)
                    return null;
                byte[] bytes = new byte[(int)read.Length];
                read.ReadExactly(bytes);
                if (read.ReadByte() != -1)
                    return null;
                string hash = HexLinkFiles.Hash(bytes);
                if (hash == cached.Hash)
                    return cached.Paths;

                IReadOnlySet<string>? tracked = ReadTracked(repository, git, bytes);
                cached.Hash = hash;
                cached.Paths = tracked;
                return tracked;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IReadOnlySet<string>? ReadTracked(string repository, string git, byte[] index)
    {
        // A split index can depend on a second file. Use Git's normal check in that case.
        if (Directory.EnumerateFiles(git, "sharedindex.*").Any())
            return null;
        string copy = Path.Combine(Path.GetTempPath(), $"hexlink-index-{Guid.NewGuid():N}");
        try
        {
            using (var file = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                file.Write(index);
            var variables = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = copy };
            GitResult listed = GitProcess.Run(repository, ["ls-files", "--cached", "-z"], environment: variables);
            if (!listed.Ok)
                return null;
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in listed.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Plain(path))
                    continue;
                paths.Add(path);
                if (paths.Count > MOST_FILES)
                    return null;
            }
            return paths;
        }
        finally
        {
            HexLinkGit.TryDelete(copy);
        }
    }
}
