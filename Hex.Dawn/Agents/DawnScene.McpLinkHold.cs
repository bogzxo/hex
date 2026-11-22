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
            ("folder", McpSchema.String("Name of the shared folder. The first open shared folder is the default.")),
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
        HexLinkFolder? folder = requested is null ? services.Workspace.Folders.FirstOrDefault() :
            services.Workspace.Folders.FirstOrDefault(each => PlatformPaths.Comparer.Equals(each.Name, requested));
        if (folder is null)
            return McpToolResult.Error($"There is no shared folder{(requested is null ? "." : $" named {requested}.")}");

        string action = args.String("action") ?? "list";
        try
        {
            HexLinkHoldService service = LinkHoldServiceFor(folder);
            string[]? paths = args["paths"] is JsonArray list ? [.. list.Select(node => LinkHoldWire(folder, node!.GetValue<string>()))] : null;
            if (action == "hold")
            {
                if (paths is not { Length: > 0 })
                    return McpToolResult.Error("Say which files to hold.");
                var held = new List<HexLinkHold>();
                foreach (string path in paths.Distinct(PlatformPaths.Comparer))
                    held.Add(await LinkHoldDocument(service, path, cancel));
                return AsJson(new() { ["held"] = new JsonArray([.. held.Select(HeldJson)]) });
            }
            if (action == "list")
                return AsJson(new() { ["held"] = new JsonArray([.. service.List().Where(held => paths is null ||
                    paths.Contains(held.Path, PlatformPaths.Comparer)).Select(HeldJson)]) });

            if (action == "plan")
                return AsJson(new() { ["files"] = new JsonArray([.. (await service.Plan(paths, cancel)).Select(PlanJson)]) });

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
                ["passed"] = result.Passed,
                ["paths"] = new JsonArray([.. result.Paths.Select(path => (JsonNode?)JsonValue.Create(path))]),
                ["needs_merge_editor"] = new JsonArray([.. result.NeedsMerge.Select(PlanJson)]),
            };
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
        static JsonObject HeldJson(HexLinkHold held) => new()
        {
            ["path"] = held.Path, ["state"] = "Held", ["held_at"] = held.HeldAt,
            ["base_hash"] = held.BaseHash, ["base_source"] = held.BaseSource.ToString().ToLowerInvariant(),
            ["incoming"] = held.HasIncoming, ["incoming_deleted"] = held.IncomingDeleted,
            ["incoming_from"] = held.IncomingFrom, ["incoming_at"] = held.IncomingAt,
        };
        static JsonObject PlanJson(HexLinkHoldPlanEntry plan) => new()
        {
            ["path"] = plan.Path, ["outcome"] = LinkHoldOutcome(plan.Outcome),
            ["conflicts"] = plan.Session is { } session ? Math.Max(1, session.Model.ConflictCount) : 0,
        };
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
            regions.Add(new JsonObject
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

    private string LinkHoldWire(HexLinkFolder folder, string path)
    {
        string prefix = folder.Name + "/";
        string normalized = path.Replace('\\', '/');
        string wire = normalized.StartsWith(prefix, PlatformPaths.Comparison) ? normalized : prefix + normalized;
        string full = HexLinkFiles.Resolve([folder], wire, linkServices?.Excludes);
        if (Directory.Exists(full))
            throw HexLinkException.Invalid("Choose a file in the shared folder.");
        return wire;
    }
}
