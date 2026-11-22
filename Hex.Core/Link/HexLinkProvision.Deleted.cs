using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkPeer
{
    internal async Task<(IReadOnlyList<string> Deleted, IReadOnlyList<HexLinkProvisionSkip> Notes)> ProvisionDeleted(
        IReadOnlyList<HexLinkProvisionRepository> repositories, CancellationToken cancel)
    {
        if (repositories.Count == 0)
            return ([], []);
        string id = Guid.NewGuid().ToString("N");
        try
        {
            JsonObject result = await AskObject("git/bundle", new JsonObject
            {
                ["operation"] = "deleted", ["id"] = id,
                ["repositories"] = new JsonArray([.. repositories.Select(repository => (JsonNode)repository.Id)])
            }, cancel, TimeSpan.FromMinutes(3));
            var files = HexLinkData.Strings(result, "deleted", HexLinkTree.MOST_FILES, HexLinkFiles.MOST_PATH + 1);
            var notes = new List<HexLinkProvisionSkip>();
            if (result["notes"] is JsonArray entries)
            {
                foreach (JsonNode? node in entries.Take(HexLinkTree.MOST_REPOS))
                {
                    if (node is JsonObject entry)
                        notes.Add(new(HexLinkData.Need(entry, "path", HexLinkFiles.MOST_PATH + 1), HexLinkData.Need(entry, "reason", 1025), File: true));
                }
            }
            return (files, notes);
        }
        catch (Exception e) when (e is OperationCanceledException or HexLinkException { Code: HexLinkErrors.TIMED_OUT })
        {
            session.Tell("git/cancel", new JsonObject { ["id"] = id });
            throw;
        }
    }
}

internal sealed partial class HexLinkHost
{
    private async Task<JsonObject> ProvisionDeleted(JsonObject parameters, CancellationToken cancel)
    {
        HexLinkHostedProvision plan = hostedProvision ?? throw HexLinkException.NotFound("The workspace checkout plan is no longer available.");
        List<string> ids = HexLinkData.Strings(parameters, "repositories", HexLinkTree.MOST_REPOS, 80);
        var paths = new ConcurrentBag<string>();
        var notes = new ConcurrentBag<HexLinkProvisionSkip>();
        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancel }, (id, token) =>
        {
            Need(HexLinkRights.View, "git/bundle deleted");
            if (!plan.WirePaths.TryGetValue(id, out string? wire))
                throw HexLinkException.Invalid("A deletion list names a repository outside the checkout plan.");
            HexLinkProvisionRepository repository = plan.Plan.Repositories.First(candidate => candidate.Id == id);
            try
            {
                IReadOnlyList<HexLinkFolder> folders = Folders;
                string directory = HexLinkFiles.Resolve(folders, wire);
                int slash = wire.IndexOf('/');
                string name = slash < 0 ? wire : wire[..slash];
                bool includeSystemFiles = folders.Any(folder => PlatformPaths.Comparer.Equals(folder.Name, name) && folder.IncludeSystemFiles);
                string output = HexLinkProvision.Run(directory,
                    ["diff", "--no-renames", "--name-only", "--diff-filter=D", "--ignore-submodules=all", "-z", "HEAD"], token);
                foreach (string path in output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
                {
                    if ((includeSystemFiles || !HexLinkSystemFiles.Matches(path)) && HoldOf(wire + "/" + path) is null
                          && !File.Exists(Path.Combine(directory, path)))
                        paths.Add(repository.Path.Length == 0 ? path : repository.Path + "/" + path);
                }
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                if (HexLinkProvision.DiskFull(e))
                    throw;
                string reason = "Could not verify deleted working files. " + e.Message;
                notes.Add(new(repository.Path.Length == 0 ? wire : repository.Path, reason, File: true));
            }
            return ValueTask.CompletedTask;
        });

        return new JsonObject
        {
            ["deleted"] = new JsonArray([.. paths.Distinct(StringComparer.Ordinal).Take(HexLinkTree.MOST_FILES).Select(path => (JsonNode)path)]),
            ["notes"] = new JsonArray([.. notes.Select(note => (JsonNode)new JsonObject
            {
                ["path"] = note.Path, ["reason"] = note.Reason.Length <= 1024 ? note.Reason : note.Reason[..1024]
            })])
        };
    }
}
