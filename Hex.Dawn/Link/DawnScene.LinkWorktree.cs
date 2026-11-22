using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private readonly ConcurrentDictionary<Guid, HexLinkPrivateWorktree> linkPrivateDrafts = new();

    private HexMcpTool LinkPublishTool() => new(new McpTool("hex_link_publish", "Publish private worktree changes",
        "Check an agent's private worktree, build and publish its changes, or discard the draft.",
        McpSchema.Object([
            ("action", McpSchema.OneOf("Check, publish or discard this agent's private worktree.", "status", "publish", "discard")),
            ("anyway", McpSchema.Flag("Explicitly publish without the build gate. The authority audits the override.")),
            ("test_filter", McpSchema.String("Optional dotnet test filter to run after the workspace build."))])),
        null, RunAway: LinkPublishAgentTool, NeedsEditor: false, TakesCaller: true);

    private async Task<McpToolResult> LinkPublishAgentTool(JsonObject args, CancellationToken cancel)
    {
        Guid? id = await OnEditorForLink(() => CallerOf(args) is { } caller ? ChatById(caller)?.LinkDraft : null).WaitAsync(cancel);
        if (id is not { } draft)
            return McpToolResult.Error("This chat has no private worktree.");
        return await LinkDraftAction(draft, args.String("action") ?? "status", args.Flag("anyway"), args.String("test_filter"), cancel);
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
                    return McpToolResult.Text($"{status.Files} unpublished files ({status.Bytes} bytes) in {draft.Root}.\n{string.Join('\n', status.Paths)}");
                case "discard":
                    await draft.Discard(cancel);
                    linkPrivateDrafts.TryRemove(id, out _);
                    return McpToolResult.Text("Private worktree discarded.");
                case "publish":
                    if (LinkPrivatePublishRoute(draft.SharedFolder) is not { } route)
                        return McpToolResult.Error("Connect to a folder authority that supports checked publication.");
                    var options = await OnEditorForLink(() => LinkWorktreeGateOf(draft, tests)).WaitAsync(cancel);
                    HexLinkDraftPublication result = await draft.Publish(route, options, bypass, cancel);
                    if (result.Gate is { Passed: false } failed)
                        return McpToolResult.Error($"Build gate exited {failed.ExitCode}.\n{failed.Output}\nLog: {failed.Log}");
                    if (result.Conflicts.Count > 0)
                        return McpToolResult.Error("Resolve the private worktree conflicts before publishing:\n" +
                            string.Join('\n', result.Conflicts.Select(conflict => conflict.Path)));
                    if (result.Receipt is not { } receipt)
                        return McpToolResult.Text("The authority already has these changes.");
                    return McpToolResult.Text($"Published {receipt.Files} files ({receipt.Id:N})." +
                        (result.Gate is { } gate ? $"\n{gate.Output}\nLog: {gate.Log}" : string.Empty));
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
        if (linkPrivateDrafts.TryGetValue(id, out HexLinkPrivateWorktree? existing))
            return existing;
        string storage = LinkPrivateStorage;
        HexLinkFolder source = HexLinkPrivateWorktree.RecordedFolder(storage, id)
            ?? throw new IOException("The private worktree is unavailable.");
        HexLinkPrivateWorktree loaded = await HexLinkPrivateWorktree.Open(storage, id, source, null,
            () => linkServices?.Excludes ?? HexExcludes.Usual, cancel);
        return linkPrivateDrafts.GetOrAdd(id, loaded);
    }

    private HexLinkWorktreeGateOptions LinkWorktreeGateOf(HexLinkPrivateWorktree draft, string? tests)
    {
        string source = draft.SharedFolder.Path;
        string file = Path.Combine(source, HexTasks.TASKS_FILE);
        if (!File.Exists(file))
            file = Path.Combine(source, ".hex", HexTasks.TASKS_FILE);
        if (!File.Exists(file))
            return new HexLinkWorktreeGateOptions(Tests: tests);
        var builds = tasks.TasksOf(file, draft.GateRoot).Where(task => task.Group == "build").ToArray();
        HexTask? build = builds.FirstOrDefault(task => task.Default) ?? builds.FirstOrDefault();
        if (build is null)
            return new HexLinkWorktreeGateOptions(Tests: tests);
        string working = Path.GetFullPath(build.WorkingFolder());
        if (!PlatformPaths.IsUnder(working, draft.GateRoot))
            throw new IOException("The build task must run inside the private gate checkout.");
        string relative = Path.GetRelativePath(draft.GateRoot, working).Replace('\\', '/');
        return new HexLinkWorktreeGateOptions(build.Command, tests, relative == "." ? null : relative);
    }
}
