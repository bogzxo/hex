using System.Text.Json.Nodes;
using Hex.Core.Merge;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private HexMcpTool LinkHoldTool() => new(new McpTool("hex_link_hold", "Hold changes on this device",
        "Keep files on this device until you submit them. hold takes one or more paths; list says which are held; plan previews a submit; submit sends or merges them; update merges incoming changes while staying held; discard restores the shared version and keeps a local backup. Paths may be relative to the folder or include its name. Omit paths to operate on all held files. If a merge needs decisions, this tool returns the files and conflict counts. An agent without a merge editor reads the conflicting regions with conflicts (base, mine and theirs, numbered lines) and settles each file with resolve: resolution mine, theirs, or merged with text, the whole merged file.",
        McpSchema.Object([
            ("action", McpSchema.OneOf("What to do.", "hold", "list", "plan", "submit", "update", "discard", "conflicts", "resolve")),
            ("resolution", McpSchema.OneOf("For resolve, how to settle the one file in paths.", "mine", "theirs", "merged")),
            ("text", McpSchema.String("For resolve with merged, the whole merged text of the file.")),
            ("folder", McpSchema.String("Name of the shared folder. The shared folder is the default (the one synced or holding files, list covers all of them), the workspace's first folder when none is.")),
            ("paths", new JsonObject
            {
                ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Files in the shared folder, as relative or folder/relative paths."
            }),
            ("partial", McpSchema.Flag("Submit clean files now and leave the ones that need a merge held.")),
            ("check", McpSchema.Flag("Run the pre-publish check. A configured check runs by default; false skips it.")),
        ], "action")), null, Timeout: 300, RunAway: RunLinkHoldTool, NeedsEditor: false);

    private async Task<McpToolResult> RunLinkHoldTool(JsonObject args, CancellationToken cancel)
    {
        if (linkServices is not { } services)
            return McpToolResult.Error("Hex Link is off.");
        string? requested = args.String("folder");
        string[] named = args["paths"] is JsonArray asked ? [.. asked.Select(node => node!.GetValue<string>())] : [];
        HexLinkFolder? folder;
        if (requested is not null)
        {
            folder = services.Workspace.Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, requested));
            if (folder is null)
                return McpToolResult.Error($"There is no shared folder named {requested}.");
        }
        else if (named.Length == 0)
        {
            IReadOnlyList<HexLinkFolder> shared = LinkSharedFolders(services);
            if (shared.Count > 1 && (args.String("action") ?? "list") == "list")
            {
                // Every shared folder's holds, the paths say which folder each is in
                return McpToolResult.Text(new JsonObject
                {
                    ["held"] = new JsonArray([.. shared.SelectMany(each => LinkHoldServiceFor(each).List()).Select(LinkHeldJson)]),
                }.ToJsonString());
            }
            if (shared.Count > 1)
                return McpToolResult.Error($"More than one folder is shared ({string.Join(", ", shared.Select(each => each.Name))}), say which with folder.");
            folder = shared.Count == 1 ? shared[0] : services.Workspace.Folders.FirstOrDefault();
            if (folder is null)
                return McpToolResult.Error("There is no shared folder.");
        }
        else if (LinkHoldFolderFor(services, named, out string? unplaced) is not { } placed)
            return McpToolResult.Error(unplaced!);
        else
            folder = placed;

        string action = args.String("action") ?? "list";
        try
        {
            HexLinkHoldService service = LinkHoldServiceFor(folder);
            string[]? paths = args["paths"] is JsonArray ? [.. named.Select(path => LinkHoldWire(folder, path))] : null;
            string[] notHeld = [];
            if (paths is { Length: > 0 } && action is "plan" or "submit" or "update" or "discard")
            {
                // The held ones go on, the others are said, one that wasn't held (sent already) doesn't fail the rest
                var heldNow = service.List().Select(entry => entry.Path).ToHashSet(PlatformPaths.Comparer);
                notHeld = [.. paths.Where(path => !heldNow.Contains(path)).Distinct(PlatformPaths.Comparer)];
                paths = [.. paths.Where(heldNow.Contains)];
                if (paths.Length == 0)
                    return McpToolResult.Error($"None of these is held: {string.Join(", ", notHeld)}.");
            }
            if (action == "hold")
            {
                if (paths is not { Length: > 0 })
                    return McpToolResult.Error("Say which files to hold.");
                foreach (string path in paths)
                {
                    if (!File.Exists(HexLinkFiles.Resolve([folder], path, services.Excludes)))
                        return McpToolResult.Error($"{path} doesn't exist in {folder.Name} ({folder.Path}), nothing was held. Name a file that is there.");
                }
                var held = new List<HexLinkHold>();
                foreach (string path in paths.Distinct(PlatformPaths.Comparer))
                    held.Add(await LinkHoldDocument(service, path, cancel));
                return AsJson(new() { ["held"] = new JsonArray([.. held.Select(HeldJson)]) });
            }
            if (action == "list")
                return AsJson(new() { ["held"] = new JsonArray([.. service.List().Where(held => paths is null ||
                    paths.Contains(held.Path, PlatformPaths.Comparer)).Select(HeldJson)]) });

            if (action == "plan")
                return AsJson(new() { ["files"] = new JsonArray([.. (await service.Plan(paths, cancel)).Select(PlanJson)]), ["not_held"] = NotHeldJson(notHeld) });

            if (action == "conflicts")
                return AsJson(new() { ["conflicts"] = new JsonArray([.. service.PendingConflicts(paths).Select(LinkHoldConflictJson)]) });

            if (action == "resolve")
            {
                if (paths is not { Length: 1 })
                    return McpToolResult.Error("Say the one file to resolve in paths.");
                HexLinkHoldOperation settled = await service.ResolveMerge(paths[0], args.String("resolution") ?? string.Empty, args.String("text"), cancel);
                var left = new JsonObject
                {
                    ["resolved"] = new JsonArray([.. settled.Paths.Select(path => (JsonNode?)JsonValue.Create(path))]),
                    ["conflicts_left"] = new JsonArray([.. settled.NeedsMerge.Select(PlanJson)]),
                };
                if (settled.Gate is { Passed: false } failed)
                    left["check_output"] = failed.Output;
                return settled.Passed ? AsJson(left) : McpToolResult.Error(left.ToJsonString());
            }

            HexLinkHoldOperation result = action switch
            {
                "submit" => await service.Submit(paths, args.Flag("partial"), HexLinkData.Bool(args, "check"), cancel),
                "update" => await service.Update(paths, cancel),
                "discard" => await service.Discard(paths, cancel),
                _ => throw new ArgumentException("Unknown hold action."),
            };
            var said = new JsonObject
            {
                // Nothing sent while files wait for a merge: that reads as not done, not as a pass over no paths
                ["passed"] = result.Passed && result.NeedsMerge.Count == 0,
                ["state"] = result.NeedsMerge.Count > 0 ? "needs_merge" : result.Passed ? "done" : "failed",
                ["paths"] = new JsonArray([.. result.Paths.Select(path => (JsonNode?)JsonValue.Create(path))]),
                ["needs_merge_editor"] = new JsonArray([.. result.NeedsMerge.Select(PlanJson)]),
            };
            if (notHeld.Length > 0)
                said["not_held"] = NotHeldJson(notHeld);
            if (result.NeedsMerge.Count > 0)
                said["next"] = "Without a merge editor: action conflicts shows the regions, action resolve settles a file with resolution mine, theirs or merged (text is the whole merged file).";
            if (result.Gate is { } gate)
                said["check_output"] = gate.Output;
            if (result.DiscardedDirectory is { } directory)
                said["discarded_directory"] = directory;
            return result.Passed ? AsJson(said) : McpToolResult.Error(said.ToJsonString());
        }
        catch (Exception error) when (error is IOException or HexLinkException or ArgumentException or UnauthorizedAccessException)
        {
            return McpToolResult.Error(error.Message);
        }

        static McpToolResult AsJson(JsonObject json) => McpToolResult.Text(json.ToJsonString());
        static JsonArray NotHeldJson(string[] paths) => new([.. paths.Select(path => (JsonNode?)JsonValue.Create(path))]);
        static JsonObject HeldJson(HexLinkHold held) => LinkHeldJson(held);
        static JsonObject PlanJson(HexLinkHoldPlanEntry plan) => new()
        {
            ["path"] = plan.Path, ["outcome"] = LinkHoldOutcome(plan.Outcome),
            ["conflicts"] = plan.Session is { } session ? Math.Max(1, session.Model.ConflictCount) : 0,
        };
    }

    private static JsonObject LinkHeldJson(HexLinkHold held) => new()
    {
        ["path"] = held.Path, ["state"] = "Held", ["held_at"] = LinkHeldTime(held.HeldAt),
        ["base_hash"] = held.BaseHash, ["base_source"] = held.BaseSource.ToString().ToLowerInvariant(),
        ["incoming"] = held.HasIncoming, ["incoming_deleted"] = held.IncomingDeleted,
        ["incoming_from"] = held.IncomingFrom, ["incoming_at"] = held.IncomingAt is { } at ? LinkHeldTime(at) : null,
    };

    private static string LinkHeldTime(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The folders that are actually shared: synced with a device or holding files. A tool that's given no folder uses these
    /// before falling back to the workspace's first folder.
    /// </summary>
    internal static IReadOnlyList<HexLinkFolder> LinkSharedFolders(HexLinkWorkspace workspace, Func<HexLinkFolder, HexLinkHoldStore?> holds,
        IEnumerable<string> synced)
    {
        var names = new HashSet<string>(synced, PlatformPaths.Comparer);
        return [.. workspace.Folders.Where(folder => names.Contains(folder.Name) || holds(folder)?.Count > 0)];
    }

    private IReadOnlyList<HexLinkFolder> LinkSharedFolders(LinkServices services)
    {
        IHexLinkServices contract = services;
        IEnumerable<string> synced = LinkSyncs.Where(sync => sync.State != HexLinkSyncState.Off).Select(sync => sync.Folder)
            .Concat(LinkPeers.SelectMany(peer => peer.RemoteSyncReport.Select(report => report.Folder)))
            .Concat(LinkPeers.SelectMany(peer => contract.ChosenSyncFolders(peer.Device.Id)));
        return LinkSharedFolders(services.Workspace, folder => services.Holds(folder), synced);
    }

    // A waiting conflict as text for an agent: each region's base, mine and theirs lines numbered from where the region
    // starts in the merged draft (the file with the clean parts as they merged and each conflict marked)
    internal static JsonObject LinkHoldConflictJson(HexLinkHoldPlanEntry entry)
    {
        var json = new JsonObject { ["path"] = entry.Path, ["outcome"] = LinkHoldOutcome(entry.Outcome) };
        MergeModel? model = entry.Session?.Model;
        if (model is null || model.IsBinary)
        {
            json["binary_or_deleted"] = true;
            json["mine"] = entry.Mine is null ? "deleted here" : $"{entry.Mine.Length} bytes";
            json["theirs"] = entry.Snapshot.Hold.IncomingDeleted ? "deleted there" : $"{entry.Snapshot.IncomingBytes?.Length ?? 0} bytes";
            return json;
        }

        var regions = new JsonArray();
        int line = 1, index = 0;
        foreach (MergeRegion region in model.Regions)
        {
            if (region is MergeRegion.Clean clean)
            {
                line += clean.Lines.Count;
                continue;
            }

            var conflict = (MergeRegion.Conflict)region;
            regions.Add((JsonNode)new JsonObject
            {
                ["index"] = ++index,
                ["draft_line"] = line,
                ["base"] = Numbered(conflict.BaseLines, line),
                ["mine"] = Numbered(conflict.MineLines, line),
                ["theirs"] = Numbered(conflict.TheirsLines, line),
            });
            line += conflict.MarkedLines.Count;
        }

        json["regions"] = regions;
        json["resolve_with"] = "action resolve, resolution mine, theirs or merged with text, the whole file's merged text";
        return json;

        static JsonArray Numbered(IReadOnlyList<string>? lines, int from) =>
            new([.. (lines ?? []).Select((text, at) => (JsonNode?)new JsonObject { ["line"] = from + at, ["text"] = text })]);
    }

    // The folder of the workspace the paths are in: the one a path names first, or holds a file of that name, or has
    // it held; null with what's wrong (no folder has it, two do, or the paths are in different folders) when it isn't one.
    private static HexLinkFolder? LinkHoldFolderFor(LinkServices services, IReadOnlyList<string> paths, out string? why) =>
        LinkHoldFolderFor(services.Workspace, folder => services.Holds(folder), paths, out why);

    internal static HexLinkFolder? LinkHoldFolderFor(HexLinkWorkspace workspace, Func<HexLinkFolder, HexLinkHoldStore?> holds,
        IReadOnlyList<string> paths, out string? why)
    {
        why = null;
        var folders = new List<HexLinkFolder>();
        foreach (string path in paths)
        {
            string normalized = path.Replace('\\', '/').Trim();
            HexLinkFolder[] candidates;
            if (Path.IsPathFullyQualified(normalized))
                candidates = [.. workspace.Folders.Where(folder => PlatformPaths.IsUnder(Path.GetFullPath(normalized), folder.Path)
                    && !PlatformPaths.Same(Path.GetFullPath(normalized), folder.Path)).OrderByDescending(folder => folder.Path.Length).Take(1)];
            else
            {
                string first = normalized.Split('/')[0];
                HexLinkFolder? named = workspace.Folders.FirstOrDefault(folder => PlatformPaths.Comparer.Equals(folder.Name, first));
                if (named is not null && normalized.Contains('/'))
                    candidates = [named];
                else
                    candidates = [.. workspace.Folders.Where(folder =>
                    {
                        try
                        {
                            return File.Exists(Path.Combine(folder.Path, normalized))
                                || holds(folder)?.IsHeld(folder.Name + "/" + normalized) == true;
                        }
                        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { return false; }
                    })];
            }

            if (candidates.Length == 0)
            {
                why = $"{path} is in none of the workspace's folders ({string.Join(", ", workspace.Folders.Select(folder => $"{folder.Name} = {folder.Path}"))}), nothing was held. Name a file that is there.";
                return null;
            }
            if (candidates.Length > 1)
            {
                why = $"{path} is in more than one folder ({string.Join(", ", candidates.Select(folder => folder.Name))}), say which with folder or start the path with its name.";
                return null;
            }
            folders.Add(candidates[0]);
        }

        if (folders.Select(folder => folder.Name).Distinct(PlatformPaths.Comparer).Count() > 1)
        {
            why = $"The paths are in different folders ({string.Join(", ", folders.Select(folder => folder.Name).Distinct(PlatformPaths.Comparer))}), one call takes one folder.";
            return null;
        }
        return folders[0];
    }

    private string LinkHoldWire(HexLinkFolder folder, string path)
    {
        string prefix = folder.Name + "/";
        string normalized = path.Replace('\\', '/');
        if (Path.IsPathFullyQualified(normalized))
            normalized = Path.GetRelativePath(folder.Path, normalized).Replace('\\', '/');
        string wire = normalized.StartsWith(prefix, PlatformPaths.Comparison) ? normalized : prefix + normalized;
        string full = HexLinkFiles.Resolve([folder], wire, linkServices?.Excludes);
        if (Directory.Exists(full))
            throw HexLinkException.Invalid("Choose a file in the shared folder.");
        return wire;
    }
}
