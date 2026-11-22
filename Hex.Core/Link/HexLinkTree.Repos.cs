using System.Text.Json.Nodes;
using Hex.Core.Git;

namespace Hex.Core;

internal sealed partial class HexLinkTree
{
    internal const int MOST_REPOS = 256;

    private volatile string[] nestedRepositories = [];

    // Look for .git markers even in ignored directories. A workspace repository often ignores its nested checkouts.
    internal static List<string> FindNestedRepositories(string root, CancellationToken cancel = default)
    {
        var repositories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint };

        while (pending.TryPop(out string? parent))
        {
            cancel.ThrowIfCancellationRequested();
            foreach (string directory in Directory.EnumerateDirectories(parent, "*", options))
            {
                string name = Path.GetFileName(directory);
                if (SKIPPED.Contains(name) || name is ".vs" or ".venv")
                    continue;

                string marker = Path.Combine(directory, ".git");
                if (File.Exists(marker) || Directory.Exists(marker))
                {
                    // A junction or symlink to another repository is outside this workspace.
                    if ((File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    if (repositories.Count == MOST_REPOS)
                        throw new IOException($"A workspace has more than {MOST_REPOS} nested repositories.");
                    repositories.Add(Path.GetRelativePath(root, directory).Replace(Path.DirectorySeparatorChar, '/'));
                }
                pending.Push(directory);
            }
        }

        return repositories;
    }

    private IReadOnlyDictionary<string, string?>? AddNestedGitFiles(List<string> relative)
    {
        if (HexLinkGit.Why() is not null)
            return null;

        string[] repositories = [.. FindNestedRepositories(Root).Where(path => !Hidden(path, folder: true)).OrderByDescending(path => path.Length)];
        if (repositories.Length == 0)
        {
            nestedRepositories = [];
            return null;
        }

        relative.RemoveAll(path => RepositoryContaining(path, repositories) is not null);
        var heads = new Dictionary<string, string?>(StringComparer.Ordinal);
        var folders = new[] { Folder };
        var scans = new (string Output, string? Head, Exception? Error)[repositories.Length];
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount - 1, 1, 4) };
        Parallel.For(0, repositories.Length, options, i =>
        {
            string path = repositories[i];
            try
            {
                string directory = HexLinkFiles.Resolve(folders, Folder.Name + "/" + path);
                GitResult listed = GitProcess.Run(directory, ["ls-files", "-co", "--exclude-standard", "-z"], patience: TimeSpan.FromMinutes(2));
                if (!listed.Ok || !string.IsNullOrWhiteSpace(listed.Error))
                    throw new IOException($"Couldn't list Git files in {path}: {listed.Why}");
                GitResult head = GitProcess.Run(directory, ["rev-parse", "--verify", "--quiet", "HEAD"]);
                string commit = head.Output.Trim();
                if (head.Exit != 1 && (!head.Ok || !HexLinkGit.IsCommitId(commit)))
                    throw new IOException($"Couldn't read HEAD in {path}: {head.Why}");
                scans[i] = (listed.Output, head.Ok ? commit : null, null);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or HexLinkException)
            {
                scans[i] = ("", null, error);
            }
        });

        for (int i = 0; i < repositories.Length; i++)
        {
            var (output, head, error) = scans[i];
            if (error is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            string path = repositories[i];
            foreach (string file in output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal))
            {
                string full = path + "/" + file;
                if (RepositoryContaining(full, repositories) == path)
                    relative.Add(full);
            }
            heads.Add(path, head);
        }

        nestedRepositories = repositories;
        return heads;
    }

    private static string? RepositoryContaining(string relative, string[] repositories)
    {
        foreach (string repository in repositories)
        {
            if (relative.Length >= repository.Length && relative.StartsWith(repository, StringComparison.Ordinal)
                && (relative.Length == repository.Length || relative[repository.Length] == '/'))
                return repository;
        }
        return null;
    }

    private void AddGitIgnored(HashSet<string> ignored, IReadOnlyCollection<string> relative)
    {
        string[] repositories = nestedRepositories;
        foreach (var paths in relative.Where(path => !ignored.Contains(path)).GroupBy(path => RepositoryContaining(path, repositories)))
        {
            string? repository = paths.Key;
            if (repository is null && !InRepository)
            {
                foreach (string path in paths)
                {
                    if (path.Split('/').SkipLast(1).Any(SKIPPED.Contains))
                        ignored.Add(path);
                }
                continue;
            }

            string prefix = repository is null ? string.Empty : repository + "/";
            string directory = repository is null ? Root : Path.Combine(Root, repository);
            IReadOnlySet<string>? tracked = TrackedNow(directory);
            string[] ask = [.. paths.Where(path => path != repository).Select(path => path[prefix.Length..])
                .Where(path => tracked?.Contains(path) != true)];
            if (ask.Length == 0)
                continue;

            // Exit 1 means none are ignored. Any other failure cannot establish which files are safe to carry.
            GitResult said = GitProcess.Run(directory, ["check-ignore", "--stdin", "-z"], string.Join('\0', ask) + "\0");
            if (said.Exit is not (0 or 1) || !string.IsNullOrWhiteSpace(said.Error))
                throw new IOException($"Couldn't check which files Git ignores in {repository ?? Folder.Name}: {said.Why}");
            foreach (string each in said.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                ignored.Add(prefix + each);
        }
    }

    internal static string? CommitsDiffer(HexLinkManifest local, HexLinkManifest remote)
    {
        if (local.Head is { } here && remote.Head is { } there && !here.Equals(there, StringComparison.OrdinalIgnoreCase))
            return $"the folder is at {here[..10]} here and at {there[..10]} there";

        foreach (string path in (local.NestedHeads?.Keys ?? []).Union(remote.NestedHeads?.Keys ?? [], StringComparer.Ordinal))
        {
            if (local.NestedHeads is null || !local.NestedHeads.TryGetValue(path, out string? localHead))
                return $"{path} is a repository on the other device and is missing here";
            if (remote.NestedHeads is null || !remote.NestedHeads.TryGetValue(path, out string? remoteHead))
                return $"{path} is a repository here and is missing on the other device";
            if (!string.Equals(localHead, remoteHead, StringComparison.OrdinalIgnoreCase))
                return $"{path} is at {localHead ?? "no commit"} here and at {remoteHead ?? "no commit"} there";
        }

        return null;
    }

    private static JsonObject? NestedHeadsJson(IReadOnlyDictionary<string, string?>? heads)
    {
        if (heads is not { Count: > 0 })
            return null;
        var said = new JsonObject();
        foreach (var (path, head) in heads)
            said[path] = head;
        return said;
    }

    private static IReadOnlyDictionary<string, string?>? NestedHeadsOf(JsonObject said)
    {
        if (said["repositories"] is null)
            return null;
        if (said["repositories"] is not JsonObject entries || entries.Count > MOST_REPOS)
            throw new IOException("The other device sent an invalid repository list.");

        var heads = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (path, value) in entries)
        {
            string? head = value is JsonValue entry && entry.TryGetValue(out string? sha) ? sha : null;
            if (!Plain(path) || value is not null && (head is null || !HexLinkGit.IsCommitId(head)))
                throw new IOException("The other device sent an invalid repository path or commit.");
            heads.Add(path, head);
        }
        return heads;
    }
}
