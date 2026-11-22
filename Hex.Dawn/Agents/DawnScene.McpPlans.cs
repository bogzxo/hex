using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

// A chat's Plan tab as its agent writes it, and the files its steps say they'll change, which every other chat in the same
// checkout sees (hex_agent_list) so two agents started apart don't edit over each other. Advisory, nothing is locked: a
// step naming a file another chat's open step names hears who has it, the two can message each other about it though
// they're of different families, and when the step that had it finishes the other is told the file is free.
internal sealed partial class DawnScene
{
    private static JsonObject PlanItemSchema() => McpSchema.Object([
        ("id", McpSchema.String("Stable item ID. When omitted, an existing item with the same text is updated, or a new one gets an ID, given back.")),
        ("text", McpSchema.String("The to-do. Required for a new item.")),
        ("state", McpSchema.OneOf("Progress of this item. New items are pending by default.", "pending", "doing", "done", "blocked", "dropped")),
        ("note", McpSchema.String("Optional detail, the reason for a blocked step. An empty string clears it.")),
        ("agent", McpSchema.IntegerOrNull("Optional ID of the chat responsible for the item, a label. Use null to clear it.")),
        ("eta_seconds", McpSchema.Integer("Optional seconds until the step is expected to finish. Zero clears the estimate.")),
        ("files", new JsonObject
        {
            ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" },
            ["description"] = "Workspace-relative files this step will change. Other chats see them while the step is open and are told when it finishes. An empty list clears them."
        })
    ]);

    private IEnumerable<HexMcpTool> PlanTools() =>
    [
        new(new McpTool("hex_plan_update", "Update the Plan tab",
            "Write the to-do list in your Hex chat's Plan tab, which the user watches. Use it when asked for a plan and keep it current as work progresses. Merge by item ID or exact text by default, changing only supplied fields. Set merge to false to replace the list, for a new task with a new title, the finished steps of the old one moving to an earlier section. The user can change the plan in the tab too, you are told when they do, keep their changes. Returns each changed row's ID and state, and any other chat whose open step names the same files.",
            McpSchema.Object([
                ("items", new JsonObject { ["type"] = "array", ["items"] = PlanItemSchema() }),
                ("title", McpSchema.String("Optional title for the plan.")),
                ("merge", McpSchema.Flag("Keep other items and fields, true by default. False replaces the checklist, including an empty list to clear it."))
            ], "items")), PlanUpdate, TakesCaller: true),
        new(new McpTool("hex_plan_read", "Read the Plan tab",
            "Read the checklist shown in your Hex chat's Plan tab, or another chat in your agent family by ID.",
            McpSchema.Object([("id", McpSchema.Integer("Chat ID. Defaults to the caller's chat."))]), ReadOnly: true),
            PlanRead, TakesCaller: true)
    ];

    private IEnumerable<McpToolResult?> PlanUpdate(JsonObject args)
    {
        if (CallerOf(args) is not { } caller || ChatById(caller) is not { } chat)
        {
            yield return McpToolResult.Error("Open a chat in Hex to update its Plan tab.");
            yield break;
        }

        if (args["items"] is not JsonArray updates)
        {
            yield return McpToolResult.Error("'items' should be an array of plan items.");
            yield break;
        }

        HexAgentPlan? before = HexAgentPlan.Of(chat);
        HexAgentPlan? previous = args.Flag("merge", fallback: true) ? before : null;
        List<HexPlanItem> items = previous is null ? [] : [.. previous.Items];
        List<int> changed = [];
        JsonObject schema = PlanItemSchema();
        for (int index = 0; index < updates.Count; index++)
        {
            if (updates[index] is not JsonObject update)
            {
                yield return McpToolResult.Error($"Item {index + 1} should be an object.");
                yield break;
            }

            if (McpSchema.Check(schema, update) is { } error)
            {
                yield return McpToolResult.Error($"Item {index + 1}: {error}");
                yield break;
            }

            string? id = update.String("id")?.Trim();
            string? text = update.String("text")?.Trim();
            if (id is "" || text is "")
            {
                yield return McpToolResult.Error($"Item {index + 1}: IDs and item text cannot be blank.");
                yield break;
            }

            int match = items.FindIndex(item => id is null ? item.Text == text : item.Id == id);
            HexPlanItem? current = match >= 0 ? items[match] : null;
            text ??= current?.Text;
            if (text is null)
            {
                yield return McpToolResult.Error($"Item {index + 1} needs text or the ID of an existing item.");
                yield break;
            }

            HexPlanState state = Enum.TryParse(update.String("state"), ignoreCase: true, out HexPlanState supplied)
                ? supplied : current?.State ?? HexPlanState.Pending;
            string? note = update.ContainsKey("note") ? update.String("note")?.Trim() : current?.Note;
            int? agent = update.ContainsKey("agent") ? update.Integer("agent") : current?.Agent;
            IReadOnlyList<string>? files = update.ContainsKey("files") ? HexAgentPlan.FilesOf(update["files"]) : current?.Files;
            long eta = update.Integer("eta_seconds") is { } estimate ? estimate is > 0 and <= 43200 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + estimate * 1000L : 0 : current?.EtaAt ?? 0;
            var item = new HexPlanItem(text, state, id ?? current?.Id ?? HexAgentPlan.NewId(items), string.IsNullOrEmpty(note) ? null : note, agent, files) { EtaAt = eta };
            if (match >= 0)
                items[match] = item;
            else
            {
                items.Add(item);
                match = items.Count - 1;
            }

            if (!changed.Contains(match))
                changed.Add(match);
        }

        // A row an older checklist left without an ID gets one, so it can be updated by it from now on
        for (int index = 0; index < items.Count; index++)
        {
            if (items[index].Id is null)
                items[index] = items[index] with { Id = HexAgentPlan.NewId(items) };
        }

        // What the agent system said of its own list (chat.plan) is kept with it, for its next update to be weighed against.
        // A new task, the list replaced or a plan all finished given a new title and steps none of its own, puts the old
        // one's done steps aside under it, so they don't drown the new one. An empty list clears the lot
        string? title = args.ContainsKey("title") ? args.String("title")?.Trim() : previous?.Title;
        int kept = previous?.Items.Count ?? 0;
        bool replaced = previous is null && before is { Items.Count: > 0 };
        bool next = before is { Items.Count: > 0 } && previous is not null && !previous.Unfinished && title != previous.Title && changed.Count > 0 && changed.All(index => index >= kept);
        HexAgentPlan plan = items.Count == 0 && replaced ? new HexAgentPlan(title, []) { NativeAt = before!.NativeAt, NativeKey = before.NativeKey }
            : replaced ? HexAgentPlan.NewTask(before, title, items, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            : next ? HexAgentPlan.NewTask(before, title, items.Skip(kept).ToArray(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            : (chat.Plan ?? new HexAgentPlan(null, [])) with { Title = title, Items = items };
        chat.SetPlan(plan);

        var said = new StringBuilder($"Plan: {plan.Done} of {plan.Items.Count} done.");
        if (plan.Doing is { } doing)
            said.Append($" Doing \"{OneLine(doing.Text, 120)}\".");
        if (changed.Count > 0)
            said.Append("\nChanged: ").Append(string.Join(", ", changed.Select(index => $"{items[index].Id} {items[index].State.ToString().ToLowerInvariant()}")));

        foreach (string overlap in Overlaps(chat, plan).Concat(LinkPlanOverlaps(chat, plan)))
            said.Append('\n').Append(overlap);
        foreach (string told in HandOff(chat, before, plan))
            said.Append('\n').Append(told);
        yield return McpToolResult.Text(said.ToString());
    }

    private IEnumerable<McpToolResult?> PlanRead(JsonObject args)
    {
        int? caller = CallerOf(args);
        int? id = args.Integer("id") ?? caller;
        if (id is null || ChatById(id.Value) is not { } chat)
        {
            yield return McpToolResult.Error("Specify a Hex chat ID. hex_agent_list shows the available chats.");
            yield break;
        }

        if (caller is { } from && ChatById(from) is { } requesting && RootOf(requesting) != RootOf(chat))
        {
            yield return McpToolResult.Error($"Chat #{chat.Id} is outside your agent family. hex_agent_list shows the step it is doing and the files it named.");
            yield break;
        }

        yield return McpToolResult.Text((HexAgentPlan.Of(chat) ?? new HexAgentPlan(null, [])).ToJson().ToJsonString());
    }

    /// <summary>The files a plan's open steps name, by how they're compared.</summary>
    private static HashSet<string> ClaimedFiles(HexAgentPlan? plan) =>
        plan is null ? [] : [.. plan.Items.Where(item => item.Open).SelectMany(item => item.Files ?? []).Select(HexAgentPlan.FileKey)];

    /// <summary>The other chats whose open steps name a file this one's open steps do, with the file and their step.</summary>
    private IEnumerable<(HexAgentChat Chat, HexPlanItem Step, string File)> Claims(HexAgentChat self, HexAgentPlan plan)
    {
        Dictionary<string, string> mine = [];
        foreach (string file in plan.Items.Where(item => item.Open).SelectMany(item => item.Files ?? []))
            mine.TryAdd(HexAgentPlan.FileKey(file), file);
        if (mine.Count == 0)
            yield break;

        foreach (HexAgentChat other in agentChats)
        {
            if (other == self || other.Plan is not { } theirs)
                continue;
            foreach (HexPlanItem step in theirs.Items.Where(item => item.Open && item.Files is not null))
            {
                foreach (string file in step.Files!)
                {
                    if (mine.TryGetValue(HexAgentPlan.FileKey(file), out string? named))
                        yield return (other, step, named);
                }
            }
        }
    }

    /// <summary>Whether two chats' open steps name a file in common, which lets them talk across families.</summary>
    private bool ShareClaim(HexAgentChat one, HexAgentChat other) =>
        one.Plan is { } plan && Claims(one, plan).Any(claim => claim.Chat == other);

    // What the agent writing its plan is told of the other chats that have the same files in an open step
    private IEnumerable<string> Overlaps(HexAgentChat chat, HexAgentPlan plan) =>
        Claims(chat, plan).GroupBy(claim => (claim.Chat, claim.Step)).Select(group =>
            $"Also named by {MailName(group.Key.Chat)} ({StateWord(group.Key.Chat)}), its step \"{OneLine(group.Key.Step.Text, 80)}\" is "
            + $"{group.Key.Step.State.ToString().ToLowerInvariant()}: {string.Join(", ", group.Select(claim => claim.File).Distinct())}. "
            + $"Agree who goes first with hex_agent_message to {group.Key.Chat.Id} and take other work meanwhile, it is told when you finish.");

    /// <summary>
    /// The chats waiting on files this chat's plan no longer has in an open step told they're free, a message each in
    /// their inbox. Says who was told, for the agent that finished.
    /// </summary>
    private IEnumerable<string> HandOff(HexAgentChat chat, HexAgentPlan? before, HexAgentPlan after)
    {
        HashSet<string> freed = ClaimedFiles(before);
        freed.ExceptWith(ClaimedFiles(after));
        if (freed.Count == 0)
            yield break;

        string[] finished = [.. (before?.Items ?? []).Where(item => item.Open && (item.Files ?? []).Any(file => freed.Contains(HexAgentPlan.FileKey(file))))
            .Select(item => after.Items.FirstOrDefault(now => now.Id == item.Id) is { } now ? $"\"{OneLine(now.Text, 80)}\" {now.State.ToString().ToLowerInvariant()}" : $"\"{OneLine(item.Text, 80)}\" removed")];
        foreach (HexAgentChat other in agentChats.ToArray())
        {
            if (other == chat || other.Plan is not { } theirs)
                continue;
            string[] waiting = [.. theirs.Items.Where(item => item.Open).SelectMany(item => item.Files ?? []).Where(file => freed.Contains(HexAgentPlan.FileKey(file))).Distinct()];
            if (waiting.Length == 0)
                continue;

            string text = $"{MailName(chat)} is finished with {string.Join(", ", waiting)} ({string.Join("; ", finished)}). Your plan names them, go ahead when it suits your work.";
            Deliver(other, chat, "progress", text, directive: false);
            yield return $"Told {MailName(other)} that {string.Join(", ", waiting)} are free.";
        }
    }

    /// <summary>A chat's step going on and the files its open steps name, all that other chats see of its plan.</summary>
    private static string PlanLine(HexAgentChat chat)
    {
        if (chat.Plan is not { } plan || !plan.Unfinished)
            return string.Empty;
        string doing = plan.Doing is { } step ? $" doing \"{OneLine(step.Text, 60)}\"" : string.Empty;
        string[] files = [.. plan.Items.Where(item => item.Open).SelectMany(item => item.Files ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Take(8)];
        return $"\n  plan {plan.Done}/{plan.Items.Count}{doing}" + (files.Length > 0 ? $", files {string.Join(", ", files)}" : string.Empty);
    }
}
