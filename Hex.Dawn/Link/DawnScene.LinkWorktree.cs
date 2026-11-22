using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private ConcurrentDictionary<Guid, HexLinkPrivateWorktree> linkPrivateDrafts { get => LazyInitializer.EnsureInitialized(ref field, static () => new()); }

    private HexMcpTool LinkPublishTool() => new(new McpTool("hex_link_publish", "Publish private worktree changes",
        "Check an agent's private worktree, build and publish its changes, or discard the draft.",
        McpSchema.Object([
            ("action", McpSchema.OneOf("Check, publish, open a repository or discard this agent's private worktree.", "status", "publish", "materialize", "discard")),
            ("path", McpSchema.String("Private worktree-relative repository or file path to open.")),
            ("anyway", McpSchema.Flag("Explicitly publish without the build gate. The authority audits the override.")),
            ("test_filter", McpSchema.String("Optional dotnet test filter to run after the workspace build."))])),
        null, RunAway: LinkPublishAgentTool, NeedsEditor: false, TakesCaller: true);

    private async Task<McpToolResult> LinkPublishAgentTool(JsonObject args, CancellationToken cancel)
    {
        Guid? id = await OnEditorForLink(() => CallerOf(args) is { } caller ? ChatById(caller)?.LinkDraft : null).WaitAsync(cancel);
        if (id is not { } draft)
            return McpToolResult.Error("This chat has no private worktree.");
        string action = args.String("action") ?? "status";
        if (action == "materialize")
            return await LinkOpenPrivateRepositoryTool(draft, args.String("path"), cancel);
        bool bypass = action == "publish" && args.Flag("anyway");
        if (bypass && !await LinkConfirmWorktreeGate("Publish anyway",
            "Publish this agent's private worktree without its build gate? The authority records the override.", cancel))
            return McpToolResult.Error("Publish anyway was declined.");
        return await LinkDraftAction(draft, action, bypass, args.String("test_filter"), cancel);
    }

    private async Task<McpToolResult> LinkOpenPrivateRepositoryTool(Guid id, string? path, CancellationToken cancel)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return McpToolResult.Error("Supply a private worktree-relative repository or file path.");
            var draft = await LinkOpenStoredDraft(id, cancel);
            string relative = LinkPrivateRelativePath(path, draft.SharedFolder);
            string full = HexLinkFiles.Resolve([new HexLinkFolder("work", draft.Root)], "work/" + relative);
            await LinkMaterializePrivatePath(draft, Path.GetRelativePath(draft.Root, full).Replace('\\', '/'), cancel);
            return McpToolResult.Text($"Private repository ready for {path}.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HexLinkException)
        {
            return McpToolResult.Error(error.Message);
        }
    }

    private async Task<McpToolResult> LinkDraftAction(Guid id, string action, bool bypass, string? tests, CancellationToken cancel)
    {
        try
        {
            HexLinkPrivateWorktree draft = await LinkOpenStoredDraft(id, cancel);
            switch (action)
            {
                case "status":
                    HexLinkDraftStatus status = await draft.Status(cancel);
                    var paths = status.Paths.Select(path => LinkPrivateRelativePath(path, draft.SharedFolder));
                    string plannedGate;
                    try
                    {
                        var gates = await LinkWorktreeGatesOf(draft, status.Paths, tests, preview: true, cancel);
                        plannedGate = gates.Count > 0 ? string.Join("\n", gates.Select(gate => gate.Describe())) : "No changed files to gate.";
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or HexLinkException)
                    {
                        plannedGate = error.Message;
                    }
                    string repos = string.Join(", ", draft.MaterializedRepositories.Select(path => path.Length == 0 ? "root" : path));
                    string first = draft.FirstGate ? "\nThe first gate runs a full build in this private tree." : "";
                    return McpToolResult.Text($"{status.Files} unpublished files ({status.Bytes} bytes) in {draft.Root}.\n{string.Join('\n', paths)}" +
                        $"\nPrivate repositories: {repos}\nGate: {plannedGate}{first}");
                case "discard":
                    bool inUse = await OnEditorForLink(() => agentChats.Count(chat => chat.LinkDraft == id && chat.Running) > 1).WaitAsync(cancel);
                    if (inUse)
                        return McpToolResult.Error("Stop the other agents using this private worktree before discarding it.");
                    await draft.Discard(cancel);
                    linkPrivateDrafts.TryRemove(id, out _);
                    return McpToolResult.Text("Private worktree discarded.");
                case "publish":
                    if (LinkPrivatePublishRoute(draft.SharedFolder) is not { } route)
                        return McpToolResult.Error("Connect to a folder authority that supports checked publication.");
                    HexLinkDraftPublication result = await draft.Publish(route,
                        (paths, stop) => LinkWorktreeGatesOf(draft, paths, tests, preview: false, stop), bypass, cancel);
                    if (result.Gate is { Passed: false } failed)
                        return McpToolResult.Error($"Build gate exited {failed.ExitCode}.\nGate: {string.Join("\n", failed.Ran)}\n{failed.Output}\nLog: {failed.Log}");
                    if (result.Conflicts.Count > 0)
                        return McpToolResult.Error("Resolve the private worktree conflicts before publishing:\n" +
                            string.Join('\n', result.Conflicts.Select(conflict => conflict.Path)));
                    if (result.Receipt is not { } receipt)
                        return McpToolResult.Text("The authority already has these changes.");
                    return McpToolResult.Text($"Published {receipt.Files} files ({receipt.Id:N})." +
                        (result.Gate is { } gate ? $"\nGate: {string.Join("\n", gate.Ran)}\n{gate.Output}\nLog: {gate.Log}" : string.Empty));
                default:
                    return McpToolResult.Error("Use status, publish or discard.");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or HexLinkException)
        {
            return McpToolResult.Error(error.Message);
        }
    }

    private async Task<HexLinkPrivateWorktree> LinkOpenStoredDraft(Guid id, CancellationToken cancel)
    {
        string storage = LinkPrivateStorage;
        if (linkPrivateDrafts.TryGetValue(id, out HexLinkPrivateWorktree? existing) && PlatformPaths.IsUnder(existing.Root, storage))
            return existing;
        HexLinkFolder source = HexLinkPrivateWorktree.RecordedFolder(storage, id)
            ?? throw new IOException("The private worktree is unavailable.");
        HexLinkPrivateWorktree loaded = await HexLinkPrivateWorktree.Open(storage, id, source, null,
            () => linkServices?.Excludes ?? HexExcludes.Usual, cancel);
        return linkPrivateDrafts.AddOrUpdate(id, loaded, (_, cached) =>
            PlatformPaths.IsUnder(cached.Root, storage) ? cached : loaded);
    }

}
