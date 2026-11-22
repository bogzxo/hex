using System.Text.Json.Nodes;

namespace Hex.Core;

internal sealed partial class HexLinkHost
{
    internal HexLinkPeer? GitFollowPeer { get; set; }
    private sealed record GitFollowRegistration(string Folder, HexLinkHeadTip Tip);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, GitFollowRegistration> followConfirmed = new(PlatformPaths.Comparer);

    private string ResolveGitFollowRepository(string wire)
    {
        string root = ResolveUsableTreeFile(wire);
        string marker = Path.Combine(root, ".git");
        if ((!File.Exists(marker) && !Directory.Exists(marker)) || File.GetAttributes(marker).HasFlag(FileAttributes.ReparsePoint))
            throw HexLinkException.NotFound("The selected folder has no Git checkout of its own.");
        return root;
    }

    private async Task<JsonObject> GitFollowTips(JsonObject parameters, CancellationToken cancel)
    {
        string folder = HexLinkData.Need(parameters, "folder", 200);
        _ = ResolveUsableTreeFile(folder);
        List<string> paths = HexLinkData.Strings(parameters, "paths", HexLinkTree.MOST_REPOS + 1, HexLinkFiles.MOST_PATH);
        var issues = new JsonObject();
        JsonObject tips = await Task.Run(async () =>
        {
            if (HexLinkGit.Why() is { } unavailable)
                throw HexLinkException.NotFound(unavailable);
            var result = new JsonObject();
            foreach (string path in paths.Distinct(StringComparer.Ordinal))
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    string wire = path.Length == 0 ? folder : folder + "/" + path;
                    var scope = HexLinkGitFollowScan.For(ResolveGitFollowRepository(wire));
                    if (await scope.ReadStable(cancel) is { } tip)
                        result[path] = tip.ToJson();
                    else
                        issues[path] = "Git's index or checkout is busy. Link will retry.";
                }
                catch (Exception error) when (error is HexLinkException or IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    issues[path] = error.Message[..Math.Min(error.Message.Length, 512)];
                }
            }
            return result;
        }, cancel);
        Need(HexLinkRights.View, "git/follow-tips");
        return new JsonObject { ["tips"] = tips, ["issues"] = issues };
    }

    private async Task<JsonObject> GitFollowRequest(string operation, JsonObject parameters, CancellationToken cancel)
    {
        string name = HexLinkData.Need(parameters, "folder", 200);
        if (operation == "follow_stop")
        {
            StopGitFollowWatching(name);
            return [];
        }
        if (!treeWatches.ContainsKey(name))
            throw HexLinkException.NotFound("Watch the shared folder before following its Git state.");
        string path = HexLinkData.Need(parameters, "path", HexLinkFiles.MOST_PATH);
        string wire = path.Length == 0 ? name : name + "/" + path;
        HexLinkFolder folder = Folders.FirstOrDefault(each => each.Name == name)
            ?? throw HexLinkException.NotFound("The shared folder is no longer open.");
        string root = ResolveGitFollowRepository(wire);
        HexLinkHeadTip expected = HexLinkHeadTip.Of(HexLinkData.Object(parameters, "expected"))
            ?? throw HexLinkException.Invalid("HEAD follow needs the exact checked-out commit and branch.");
        var policy = services as IHexLinkGitFollowing;
        HexLinkGitFollowState? state = policy is null ? null : new(policy.GitFollowData, folder, Device.Id);

        if (operation == "follow_confirm")
        {
            if (await Task.Run(() => HexLinkGitFollowScan.For(root).Read(cancel), cancel) != expected)
                throw new HexLinkException(HexLinkErrors.CONFLICT, "The Git checkout changed. Link will compare it again.");
            Need(HexLinkRights.View, "git/follow-confirm");
            if (!treeWatches.ContainsKey(name))
                throw HexLinkException.NotFound("The shared folder is no longer watched.");
            if (followConfirmed.Count < (HexLinkTree.MOST_REPOS + 1) * MOST_TREES || followConfirmed.ContainsKey(root))
                followConfirmed[root] = new(name, expected);
            if (state is not null && HexLinkData.Bool(parameters, "common") == true && state.Agreed(path) != expected)
                state.Agree(path, expected);
            return [];
        }

        Need(HexLinkRights.Edit, "git/follow-adopt");
        if (policy?.GitFollows(folder, Device) != true || GitFollowPeer is not { } source)
            throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "Git state follows is off for this shared folder.");
        if (parameters.ContainsKey("relay"))
            throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "Git follow requires a direct authenticated device.");

        HexLinkHeadTip target = HexLinkHeadTip.Of(HexLinkData.Object(parameters, "target"))
            ?? throw HexLinkException.Invalid("HEAD follow needs an exact target commit and branch.");
        var observed = await source.GitFollowTips(name, [path], cancel);
        if (observed.Tips.GetValueOrDefault(path) != target)
            throw new HexLinkException(HexLinkErrors.CONFLICT, observed.Issues.GetValueOrDefault(path)
                ?? "The other device's Git tip changed. Link will compare it again.");
        await HexLinkGitFollow.Fetch(source, wire, root, expected, target, cancel);
        HexLinkHeadMove move = await HexLinkGitFollow.Move(root, expected, target, state?.Agreed(path), cancel, () =>
        {
            Need(HexLinkRights.Edit, "git/follow-adopt");
            if (!policy.GitFollows(folder, Device) || !treeWatches.ContainsKey(name))
                throw new HexLinkException(HexLinkErrors.NOT_ALLOWED, "Git state follows was turned off for this connection.");
            if (services.FolderAuthorities is { } bindings && bindings.Of(folder) != folder.Authority)
                throw new HexLinkException(HexLinkErrors.CONFLICT, "The folder authority changed. Link will compare the Git tips again.");
        });
        if (move.Why is null)
        {
            followConfirmed[root] = new(name, target);
            state?.Agree(path, target);
            if (move.Backup is { } backup)
                services.Audit("git", Device, $"HEAD follow {wire}: {expected.Head} to {target.Head}, backup {backup}" + (move.Rewrite ? " (rewrite)" : ""));
        }
        return new JsonObject { ["why"] = move.Why, ["backup"] = move.Backup, ["rewrite"] = move.Rewrite };
    }

    private void StopGitFollowWatching(string folder)
    {
        foreach (var registration in followConfirmed)
            if (registration.Value.Folder == folder)
                followConfirmed.TryRemove(registration);
    }

    private async Task EnsureGitFollowFile(string full, CancellationToken cancel)
    {
        if (followConfirmed.Count == 0)
            return;
        for (string? path = Path.GetDirectoryName(full); path is not null; path = Path.GetDirectoryName(path))
        {
            if (!followConfirmed.TryGetValue(path, out GitFollowRegistration? expected))
                continue;
            if (await Task.Run(() => HexLinkGitFollowScan.For(path).Read(cancel), cancel) != expected.Tip)
                throw new HexLinkException(HexLinkErrors.CONFLICT, "Git is moving this repository. Link will compare its new branch and commit.");
            return;
        }
    }
}
