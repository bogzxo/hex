using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkPrivateWorktree
{
    private static string SetupFile(string data) => Path.Combine(data, "setup.json");

    private static FileStream SetupLock(string data) =>
        new(Path.Combine(data, "setup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private void WriteSetup(IEnumerable<string> repositories, string source)
    {
        var marker = new JsonObject
        {
            ["source"] = source,
            ["repositories"] = new JsonArray([.. repositories.Select(repo => (JsonNode)JsonValue.Create(repo)!)])
        };
        HexLinkFiles.WriteAtomic(SetupFile(data), Encoding.UTF8.GetBytes(marker.ToJsonString()));
    }

    public static async Task RecoverSetups(string storage, CancellationToken cancel)
    {
        string home = Path.GetFullPath(storage);
        string drafts = Path.Combine(home, "drafts");
        if (!Directory.Exists(drafts))
            return;
        using var maintenance = await LockStorage(home, cancel);
        foreach (string data in Directory.EnumerateDirectories(drafts))
        {
            cancel.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileName(data), "N", out _) || !File.Exists(SetupFile(data)) ||
                (File.GetAttributes(data) & FileAttributes.ReparsePoint) != 0)
                continue;

            FileStream setup;
            try { setup = SetupLock(data); }
            catch (IOException) { continue; } // Another editor is still preparing this draft.
            using (setup)
            {
                try { await RecoverSetup(data, home, cancel); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or HexLinkException or System.Text.Json.JsonException)
                {
                    Console.WriteLine($"[Hex] Private worktree setup recovery for {Path.GetFileName(data)} is pending: {error.Message}");
                }
            }
        }
    }

    private static async Task RecoverSetup(string data, string home, CancellationToken cancel)
    {
        string file = SetupFile(data);
        if (!File.Exists(file))
            return;
        var saved = JsonNode.Parse(await File.ReadAllBytesAsync(file, cancel)) as JsonObject;
        if (saved?["repositories"] is not JsonArray requested || HexLinkData.String(saved, "source", 4000) is not { } source ||
            !Path.IsPathFullyQualified(source))
            throw new IOException("Invalid private worktree setup journal.");

        string metadata = Path.Combine(data, "state.json");
        var committed = new HashSet<string>(PlatformPaths.Comparer);
        if (File.Exists(metadata))
        {
            if (JsonNode.Parse(await File.ReadAllBytesAsync(metadata, cancel)) is not JsonObject state ||
                state["repositories"] is not JsonArray repositories)
                throw new IOException("Invalid private worktree metadata during setup recovery.");
            foreach (JsonObject row in repositories.OfType<JsonObject>())
                if (HexLinkData.String(row, "path", 4000) is { } path)
                    committed.Add(path);
        }

        foreach (JsonNode? entry in requested.Reverse())
        {
            cancel.ThrowIfCancellationRequested();
            if (entry is not JsonValue item || !item.TryGetValue(out string? repo) || repo is null)
                throw new IOException("Invalid private worktree repository in the setup journal.");
            if (committed.Contains(repo))
                continue;

            string root = Path.Combine(data, "tree");
            string target = HexLinkFiles.Resolve([new HexLinkFolder("draft", root)], repo.Length == 0 ? "draft" : "draft/" + repo);
            if (!PlatformPaths.IsUnder(target, root) || !PlatformPaths.Same(target, Path.Combine(root, repo)))
                throw new IOException("Setup recovery found a path outside its private worktree.");
            string objects = ObjectDirectory(home, source, repo);
            if (File.Exists(Path.Combine(objects, "HEAD")))
            {
                var git = await HexLinkWorktreeGit.Open(objects, cancel);
                try { await git.Remove(target, cancel); }
                catch (IOException) when (!File.Exists(Path.Combine(target, ".git"))) { }
            }
            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
        }
        File.Delete(file);
    }
}
