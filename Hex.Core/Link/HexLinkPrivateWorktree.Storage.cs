using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkPrivateWorktree
{
    private string Storage => Path.GetDirectoryName(Path.GetDirectoryName(data)!)!;

    // A snapshot ref can exist before its draft state is written. Every writer takes this lock so
    // maintenance cannot mistake an in-progress capture for an abandoned one, even in another editor.
    private static async Task<FileStream> LockStorage(string home, CancellationToken cancel)
    {
        Directory.CreateDirectory(home);
        string path = Path.Combine(home, "objects.lock");
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (File.Exists(path))
            {
                await Task.Delay(50, cancel);
            }
        }
    }

    private async Task CompactStorage(bool force, CancellationToken cancel)
    {
        try
        {
            HexLinkWorktreeGit[] stores = [.. repositories.Select(repo => repo.Git)
                .Where(git => force || git.NeedsSnapshotMaintenance())];
            if (stores.Length == 0)
                return;

            var retained = stores.ToDictionary(store => store.DirectoryPath, _ => new HashSet<string>(StringComparer.Ordinal),
                PlatformPaths.Comparer);
            foreach (string folder in Directory.EnumerateDirectories(Path.Combine(Storage, "drafts")))
            {
                cancel.ThrowIfCancellationRequested();
                if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out _) ||
                    (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                    continue;
                string file = Path.Combine(folder, "state.json");
                if (!File.Exists(file))
                    continue;

                var state = JsonNode.Parse(await File.ReadAllBytesAsync(file, cancel)) as JsonObject;
                if (HexLinkData.String(state, "source", 4000) is not { } source ||
                    state?["repositories"] is not JsonArray repositories || state["bases"] is not JsonObject bases)
                    throw new IOException("Invalid private worktree metadata during snapshot cleanup.");

                var commits = new HashSet<string>?[repositories.Count];
                for (int i = 0; i < repositories.Count; i++)
                {
                    if (repositories[i] is not JsonObject repo || HexLinkData.String(repo, "path", 4000) is not { } path)
                        throw new IOException("Invalid private repository metadata during snapshot cleanup.");
                    if (retained.TryGetValue(ObjectDirectory(Storage, source, path), out commits[i]))
                        Retain(commits[i]!, repo);
                }

                foreach (var (_, node) in bases)
                {
                    if (node is not JsonObject basis || HexLinkData.Int(basis, "repository") is not int i || i < 0 || i >= commits.Length)
                        throw new IOException("Invalid private file metadata during snapshot cleanup.");
                    if (commits[i] is { } needed)
                        Retain(needed, basis);
                }
            }

            foreach (HexLinkWorktreeGit store in stores)
                await store.PruneSnapshots(retained[store.DirectoryPath], cancel);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            // The worktree operation already committed. The next maintenance pass can finish cleanup.
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.WriteLine($"[Hex] Private snapshot cleanup is pending: {error.Message}");
        }
    }

    private static void Retain(HashSet<string> needed, JsonObject item)
    {
        if (HexLinkData.String(item, "commit", 64) is not { } commit || !HexLinkGit.IsCommitId(commit))
            throw new IOException("Invalid private snapshot reference during cleanup.");
        needed.Add(commit);
    }
}
