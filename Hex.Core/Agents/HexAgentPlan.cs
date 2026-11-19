using System.Text.Json.Nodes;

namespace Hex.Core;

internal enum HexPlanState { Pending, Doing, Done, Blocked, Dropped }

/// <param name="Agent">The chat said to be doing the item, a label of who owns it. A plan is only ever written by its own chat.</param>
/// <param name="Files">The files the step means to change, workspace-relative, what other chats in the same checkout see
/// as its claim while it's open (pending, doing or blocked) so two agents don't edit over each other. Advisory, nothing is locked.</param>
internal sealed record HexPlanItem(string Text, HexPlanState State = HexPlanState.Pending, string? Id = null, string? Note = null, int? Agent = null,
    IReadOnlyList<string>? Files = null)
{
    /// <summary>Whether the step is still to be done or going, its files still claimed.</summary>
    public bool Open => State is HexPlanState.Pending or HexPlanState.Doing or HexPlanState.Blocked;
}

/// <summary>A chat's checklist. Older chats can still show the checklist notes recorded by their agent system.</summary>
internal sealed record HexAgentPlan(string? Title, IReadOnlyList<HexPlanItem> Items)
{
    public int Done => Items.Count(item => item.State == HexPlanState.Done);

    /// <summary>When the last native update the agent system said (chat.plan) was, by its own clock, 0 for none.</summary>
    public double NativeAt { get; init; }

    /// <summary>The key of the last native update taken (a tool call's id), so the same one said again is passed over.</summary>
    public string? NativeKey { get; init; }

    /// <summary>A step as an agent system's own to-do list says it (Claude Code's TodoWrite, Codex's turn plan).</summary>
    internal readonly record struct NativeStep(string? Id, string Text, HexPlanState State);

    /// <summary>
    /// A state as an agent system words it: pending, in_progress or inProgress, completed, and Hex's own words.
    /// </summary>
    public static HexPlanState StateOf(string? said) => (said ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).ToLowerInvariant() switch
    {
        "inprogress" or "doing" or "active" or "running" => HexPlanState.Doing,
        "completed" or "complete" or "done" => HexPlanState.Done,
        "blocked" or "failed" => HexPlanState.Blocked,
        "dropped" or "cancelled" or "canceled" or "skipped" => HexPlanState.Dropped,
        _ => HexPlanState.Pending
    };

    /// <summary>
    /// The plan with an agent system's own to-do list taken in. Its steps match rows by ID first, then by their exact
    /// text, and change only the text and state, so the notes, owners and files an agent wrote with hex_plan_update stay,
    /// and rows the list doesn't name stay as they were. An update said before (its key) or older than the last one taken
    /// (at) gives null, nothing to change, as does one that changes nothing.
    /// </summary>
    public static HexAgentPlan? TakeNative(HexAgentPlan? current, IReadOnlyList<NativeStep> steps, string? title, double at, string? key)
    {
        if (current is not null && ((key is not null && key == current.NativeKey) || (at > 0 && at < current.NativeAt)))
            return null;

        List<HexPlanItem> items = current is null ? [] : [.. current.Items];
        HashSet<int> taken = [];
        foreach (NativeStep step in steps)
        {
            string text = step.Text.Trim();
            if (text.Length == 0)
                continue;

            int match = step.Id is { Length: > 0 } id ? items.FindIndex(item => item.Id == id) : -1;
            if (match < 0)
                match = Enumerable.Range(0, items.Count).FirstOrDefault(index => !taken.Contains(index) && items[index].Text == text, -1);
            if (match >= 0)
            {
                taken.Add(match);
                items[match] = items[match] with { Text = text, State = step.State };
            }
            else
            {
                items.Add(new HexPlanItem(text, step.State, step.Id is { Length: > 0 } given && items.All(item => item.Id != given) ? given : NewId(items)));
                taken.Add(items.Count - 1);
            }
        }

        string? titled = title is { Length: > 0 } ? title : current?.Title;
        if (current is not null && titled == current.Title && items.SequenceEqual(current.Items))
            return null;

        return new HexAgentPlan(titled, items) { NativeAt = Math.Max(at, current?.NativeAt ?? 0), NativeKey = key ?? current?.NativeKey };
    }

    /// <summary>What a chat's file keeps, the plan with when its last native update was.</summary>
    public JsonObject ToStored()
    {
        JsonObject stored = ToJson();
        if (NativeAt > 0) stored["native_at"] = NativeAt;
        if (NativeKey is not null) stored["native_key"] = NativeKey;
        return stored;
    }

    /// <summary>The step going on, the first doing one.</summary>
    public HexPlanItem? Doing => Items.FirstOrDefault(item => item.State == HexPlanState.Doing);

    /// <summary>Whether anything is left to do, pending, doing or blocked.</summary>
    public bool Unfinished => Items.Any(item => item.Open);

    /// <summary>A path the way two claims on it are compared, forward slashes, no leading ./, case as Windows has it.</summary>
    public static string FileKey(string path) => path.Trim().Replace('\\', '/').TrimStart('.', '/').ToLowerInvariant();

    /// <summary>An ID none of the items has, s1, s2 and on, for a row written without one.</summary>
    public static string NewId(IEnumerable<HexPlanItem> items)
    {
        HashSet<string> taken = [.. items.Select(item => item.Id).OfType<string>()];
        for (int n = taken.Count + 1; ; n++)
        {
            string id = $"s{n}";
            if (!taken.Contains(id))
                return id;
        }
    }

    public JsonObject ToJson()
    {
        var items = new JsonArray();
        foreach (HexPlanItem item in Items)
        {
            var row = new JsonObject { ["text"] = item.Text, ["state"] = item.State.ToString().ToLowerInvariant() };
            if (item.Id is not null) row["id"] = item.Id;
            if (item.Note is not null) row["note"] = item.Note;
            if (item.Agent is not null) row["agent"] = item.Agent;
            if (item.Files is { Count: > 0 } files) row["files"] = new JsonArray([.. files.Select(file => (JsonNode)file)]);
            items.Add((JsonNode)row);
        }

        return new JsonObject { ["title"] = Title, ["items"] = items };
    }

    public static HexAgentPlan FromJson(JsonObject plan) => new(plan.String("title"),
        [.. (plan["items"] as JsonArray ?? []).OfType<JsonObject>().Select(item => new HexPlanItem(
            item.String("text") ?? string.Empty, Enum.TryParse(item.String("state"), true, out HexPlanState state) ? state : HexPlanState.Pending,
            item.String("id"), item.String("note"), item.Integer("agent"), FilesOf(item["files"])))])
    {
        NativeAt = plan.Number("native_at") ?? 0,
        NativeKey = plan.String("native_key")
    };

    /// <summary>The files a row names, null for none.</summary>
    public static IReadOnlyList<string>? FilesOf(JsonNode? files)
    {
        if (files is not JsonArray listed)
            return null;
        string[] paths = [.. listed.OfType<JsonValue>().Select(each => each.ToString().Trim()).Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(50)];
        return paths.Length > 0 ? paths : null;
    }

    public static HexAgentPlan? Of(HexAgentChat? chat)
    {
        if (chat is null || chat.Plan is not null)
            return chat?.Plan;

        for (int i = chat.Entries.Count - 1; i >= 0; i--)
        {
            HexAgentEntry entry = chat.Entries[i];
            if (entry.Kind != HexAgentEntryKind.Note || entry.Text.Length < 4)
                continue;

            string[] lines = entry.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0 || !lines.All(line => line.StartsWith("[x] ", StringComparison.Ordinal)
                || line.StartsWith("[~] ", StringComparison.Ordinal) || line.StartsWith("[ ] ", StringComparison.Ordinal)))
                continue;

            return new(null, [.. lines.Select(line => new HexPlanItem(line[4..], line[1] switch
            {
                'x' => HexPlanState.Done,
                '~' => HexPlanState.Doing,
                _ => HexPlanState.Pending
            }))]);
        }

        return null;
    }
}
