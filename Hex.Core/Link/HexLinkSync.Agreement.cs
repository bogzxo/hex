using System.Text.Json.Nodes;
using Hex.Core.Git;

namespace Hex.Core;

internal sealed partial class HexLinkSync
{
    private JsonObject? baseAgreement;
    private HashSet<string> staleTracked = new(StringComparer.Ordinal);

    private static JsonObject AgreementOf(HexLinkManifest mine, HexLinkManifest theirs)
    {
        var repositories = new JsonObject();
        foreach (string repo in RepositoryPaths(mine, theirs))
            repositories[repo] = new JsonArray(HeadAt(mine, repo), HeadAt(theirs, repo));
        return new JsonObject
        {
            ["local_checkout"] = mine.Checkout,
            ["remote_checkout"] = theirs.Checkout,
            ["local_authority"] = mine.Authority?.ToJson(),
            ["remote_authority"] = theirs.Authority?.ToJson(),
            ["repositories"] = repositories
        };
    }

    private static IEnumerable<string> RepositoryPaths(HexLinkManifest mine, HexLinkManifest theirs) =>
        (mine.NestedHeads?.Keys ?? []).Union(theirs.NestedHeads?.Keys ?? [], StringComparer.Ordinal).Append(string.Empty);

    private static string? HeadAt(HexLinkManifest manifest, string path) =>
        path.Length == 0 ? manifest.Head : manifest.NestedHeads?.GetValueOrDefault(path);

    private HashSet<string> StaleTracked(JsonObject? saved, JsonObject current, IEnumerable<string> paths, CancellationToken cancel)
    {
        var stale = new HashSet<string>(StringComparer.Ordinal);
        var previous = HexLinkData.Object(saved, "repositories") ?? new JsonObject();
        var now = (JsonObject)current["repositories"]!;
        // Legacy state lacks proof of the checkout it described. Files tracked in HEAD are compared afresh.
        bool sameCheckout = saved is not null &&
            HexLinkData.String(current, "local_checkout", 64) is { } local &&
            HexLinkData.String(current, "remote_checkout", 64) is { } remote &&
            local == HexLinkData.String(saved, "local_checkout", 64) &&
            remote == HexLinkData.String(saved, "remote_checkout", 64) &&
            JsonNode.DeepEquals(saved["local_authority"], current["local_authority"]) &&
            JsonNode.DeepEquals(saved["remote_authority"], current["remote_authority"]);

        string[] repos = [.. now.Select(pair => pair.Key).Union(previous.Select(pair => pair.Key), StringComparer.Ordinal)
            .OrderByDescending(path => path.Length)];
        foreach (var group in paths.GroupBy(wire => RepositoryOf(wire, repos), StringComparer.Ordinal))
        {
            string repo = group.Key;
            if (sameCheckout && JsonNode.DeepEquals(previous[repo], now[repo]))
                continue;

            string[] commits = [.. CommitIds(now[repo]).Distinct(StringComparer.Ordinal)];
            if (commits.Length == 0)
                commits = [.. CommitIds(previous[repo]).Distinct(StringComparer.Ordinal)];
            if (commits.Length == 0)
                continue;

            var tracked = new HashSet<string>(StringComparer.Ordinal);
            string? directory = LocalRepository(repo);
            bool complete = directory is not null;
            foreach (string head in commits)
            {
                if (directory is null)
                    break;
                cancel.ThrowIfCancellationRequested();
                GitResult git = GitProcess.Run(directory, ["ls-tree", "-r", "--name-only", "-z", head],
                    patience: TimeSpan.FromMinutes(2), unattended: true, cancel: cancel);
                cancel.ThrowIfCancellationRequested();
                if (!git.Ok || !string.IsNullOrWhiteSpace(git.Error))
                {
                    complete = false;
                    break;
                }
                tracked.UnionWith(git.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries));
            }

            string prefix = folder.Name + "/" + (repo.Length == 0 ? "" : repo + "/");
            foreach (string wire in group)
            {
                if (!complete || tracked.Contains(wire[prefix.Length..]))
                    stale.Add(wire);
            }
        }
        return stale;
    }

    private string? LocalRepository(string repo)
    {
        try
        {
            return repo.Length == 0 ? tree.Root : Jailed(folder.Name + "/" + repo);
        }
        catch (HexLinkException)
        {
            return null;
        }
    }

    private string RepositoryOf(string wire, string[] repos)
    {
        ReadOnlySpan<char> relative = wire.AsSpan(folder.Name.Length + 1);
        foreach (string repo in repos)
        {
            if (repo.Length == 0 || (relative.Length > repo.Length && relative[repo.Length] == '/' &&
                relative.StartsWith(repo, StringComparison.Ordinal)))
                return repo;
        }
        return string.Empty;
    }

    private static IEnumerable<string> CommitIds(JsonNode? value)
    {
        if (value is JsonArray { Count: 2 } pair)
        {
            foreach (JsonNode? node in pair)
            {
                if (node is JsonValue item && item.TryGetValue<string>(out string? head) && head is not null && HexLinkGit.IsCommitId(head))
                    yield return head;
            }
        }
    }
}
