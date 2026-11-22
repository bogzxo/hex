using System.Text.Json.Nodes;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private readonly Lock worktreeGateSettings = new();

    private HexMcpTool LinkWorktreeGateTool() => new(new McpTool("hex_link_worktree_gate", "Set a folder's publish gate",
        "Show, set or clear the shared folder's publish gate on its authority. Changes require the authority's person's confirmation. Linked callers need Edit.",
        McpSchema.Object([
            ("action", McpSchema.OneOf("Show, set or clear the gate.", "status", "set", "clear")),
            ("folder", McpSchema.String("Shared folder name.")),
            ("command", McpSchema.String("Build command.")),
            ("directory", McpSchema.String("Working directory relative to the shared folder.")),
            ("test_command", McpSchema.String("Optional test command after the build.")),
            ("test_filter", McpSchema.String("Optional dotnet test filter instead of a test command.")),
            ("timeout_seconds", new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 3600 })], "folder")),
        null, RunAway: EditWorktreeGate, NeedsEditor: false);

    private HexLinkWorktreeGatePolicy? LinkReadOwnedWorktreeGate(LinkServices services, HexLinkFolder folder) =>
        folder.Authority is { } pin && pin.DeviceId == services.Device.Id && LinkPrivateStoragePath is { } storage
            ? HexLinkWorktreeGatePolicyFile.Read(Path.Combine(storage, "gates.json"), folder, pin) : null;

    private async Task<McpToolResult> EditWorktreeGate(JsonObject arguments, CancellationToken cancel)
    {
        try
        {
            var services = linkServices ?? throw new IOException("Link is off.");
            string name = HexLinkData.Need(arguments, "folder", 200);
            var folder = services.Workspace.Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, name))
                ?? throw HexLinkException.NotFound("No shared folder has that name.");
            if (folder.Authority is not { } pin || pin.DeviceId != services.Device.Id)
                throw HexLinkException.NotAllowed("this shared folder's pinned authority");
            string action = arguments.String("action") ?? "status";
            if (action == "status")
                return McpToolResult.Text(LinkReadOwnedWorktreeGate(services, folder)?.Options.Describe() ?? "Default: build the nearest task or project in each affected repository.");
            var policy = action switch
            {
                "set" => HexLinkWorktreeGatePolicy.Of(arguments),
                "clear" => null,
                _ => throw HexLinkException.Invalid("Use status, set or clear.")
            };
            var remote = HexLinkClaimCaller.Of(arguments);
            string askedBy = remote?.Name ?? services.Device.Name;
            string proposed = policy?.Options.Describe() ?? "Use the default scoped builds.";
            if (!await LinkConfirmWorktreeGate("Change publish gate",
                $"{askedBy} requested a gate change for {folder.Name}. Replicas run this gate in their private checkout.\n\n{proposed}", cancel))
                return McpToolResult.Error("The authority's person declined the gate change.");

            cancel.ThrowIfCancellationRequested();
            if (remote is not null && (link?.PeerOf(remote.Id) is not { IsClosed: false } peer ||
                !HexLinkAllowed.Has(peer.Session.Connection.Rights, HexLinkRights.Edit)))
                throw HexLinkException.NotAllowed("edit");
            lock (worktreeGateSettings)
            {
                if (!ReferenceEquals(services, linkServices))
                    throw new IOException("The Link profile changed during confirmation.");
                services.FolderAuthorities.Require(folder, pin);
                HexLinkWorktreeGatePolicyFile.Save(Path.Combine(LinkPrivateStorage, "gates.json"), folder, pin, policy);
                services.Audit("publish-gate", remote ?? services.Device, $"{folder.Name}, epoch {pin.Epoch}, {action}");
            }
            PostLink(() => { linkDirty = true; link?.WorkspaceMoved(); LinkMoved(); });
            return McpToolResult.Text($"Publish gate for {folder.Name}: {proposed}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HexLinkException)
        {
            return McpToolResult.Error(error.Message);
        }
    }

    private async Task<bool> LinkConfirmWorktreeGate(string title, string detail, CancellationToken cancel)
    {
        var approved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await OnEditorForLink(() =>
        {
            if (!cancel.IsCancellationRequested)
                UIDialog.Show(chrome, title, detail,
                    new DialogChoice("Confirm", () => approved.TrySetResult(true), Risky: true),
                    new DialogChoice("Cancel", () => approved.TrySetResult(false)));
            return true;
        }).WaitAsync(cancel);
        return await approved.Task.WaitAsync(cancel);
    }

    private async Task<IReadOnlyList<HexLinkWorktreeGateOptions>> LinkWorktreeGatesOf(HexLinkPrivateWorktree draft,
        IReadOnlyList<string> paths, string? filter, bool preview, CancellationToken cancel)
    {
        var services = linkServices ?? throw new IOException("Link is off.");
        var folder = draft.SharedFolder;
        var pin = services.FolderAuthorities.Of(folder) ?? throw new IOException("This worktree has no pinned folder authority.");
        HexLinkWorktreeGatePolicy? policy;
        if (pin.DeviceId == services.Device.Id)
            policy = LinkReadOwnedWorktreeGate(services, folder with { Authority = pin });
        else
        {
            var peer = link?.PeerOf(pin.DeviceId) ?? throw new IOException("Connect to the pinned folder authority to read the publish gate.");
            await peer.RefreshWorkspace(cancel);
            if (peer.Workspace.Folders.FirstOrDefault(item => PlatformPaths.Comparer.Equals(item.Name, folder.Name)) is not { } remote ||
                remote.Authority != pin)
                throw new IOException("The folder authority changed. Refresh this workspace before publishing.");
            policy = remote.WorktreeGate;
        }
        if (policy is not null)
            return [policy.Options];

        return await OnEditorForLink(() =>
        {
            var selected = HexLinkWorktreeGateSelection.Defaults(folder, draft.RepositoryPaths, paths, directory =>
            {
                string file = Path.Combine(directory, HexTasks.TASKS_FILE);
                if (!File.Exists(file))
                    file = Path.Combine(directory, ".hex", HexTasks.TASKS_FILE);
                if (!File.Exists(file))
                    return null;
                string taskLocation = Path.Combine(draft.GateRoot, Path.GetRelativePath(folder.Path, directory));
                var builds = tasks.TasksOf(file, taskLocation).Where(task => task.Group == "build").ToArray();
                var build = builds.FirstOrDefault(task => task.Default) ?? builds.FirstOrDefault();
                if (build is null)
                    return null;
                string working = Path.GetFullPath(build.WorkingFolder());
                if (!PlatformPaths.IsUnder(working, draft.GateRoot) ||
                    (draft.RepositoryPaths.Count > 1 && PlatformPaths.Same(working, draft.GateRoot)))
                    throw new IOException("The default build task must run within its part of the private gate checkout.");
                return new HexLinkWorktreeGateOptions(Build: build.Command,
                    WorkingFolder: HexLinkWorktreeGateSelection.Relative(draft.GateRoot, working));
            }, preview ? draft.Root : draft.GateRoot);
            return (IReadOnlyList<HexLinkWorktreeGateOptions>)[.. selected.Select(gate => gate with { Tests = filter })];
        }).WaitAsync(cancel);
    }
}
