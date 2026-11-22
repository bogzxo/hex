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

    /// <summary>When the step was marked done, Unix milliseconds, 0 for not done or not known (a plan from before it was kept).</summary>
    public long DoneAt { get; init; }

    public long EtaAt { get; init; }
}

/// <summary>An earlier task's finished steps, put aside when the agent began a new plan or the person cleared what was done.</summary>
internal sealed record HexPlanSection(string? Title, IReadOnlyList<HexPlanItem> Items, long At);

/// <summary>A chat's checklist. Older chats can still show the checklist notes recorded by their agent system.</summary>
internal sealed record HexAgentPlan(string? Title, IReadOnlyList<HexPlanItem> Items)
{
    public int Done => Items.Count(item => item.State == HexPlanState.Done);

    /// <summary>Earlier tasks' finished steps, oldest first, folded away under the plan in the tab.</summary>
    public IReadOnlyList<HexPlanSection> Earlier { get; init; } = [];

    /// <summary>How many earlier tasks are kept, the oldest go past it.</summary>
    public const int EARLIER_KEPT = 8;

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

        // A list naming none of the rows of a plan that was all finished is the next task's, the old one put aside under it
        int before = current?.Items.Count ?? 0;
        HexAgentPlan plan = current is not null && before > 0 && !current.Unfinished && !taken.Any(index => index < before)
            ? NewTask(current, titled, items.Skip(before).ToArray(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            : (current ?? new HexAgentPlan(null, [])) with { Title = titled, Items = items };
        return plan with { NativeAt = Math.Max(at, current?.NativeAt ?? 0), NativeKey = key ?? current?.NativeKey };
    }

    /* A new task, and the person's hand on the plan. Each gives a new plan, the chat's SetPlan stamping when steps were done */

    /// <summary>
    /// A plan for a new task, the steps the last one finished put aside as an earlier task under it (its title with them),
    /// so a new plan isn't drowned in the old one's done rows. What it left open or dropped goes with it.
    /// </summary>
    public static HexAgentPlan NewTask(HexAgentPlan? before, string? title, IReadOnlyList<HexPlanItem> items, long now)
    {
        HexAgentPlan plan = (before ?? new HexAgentPlan(null, [])) with { Title = title, Items = items };
        return before is null ? plan : plan with { Earlier = PutAside(before.Earlier, before.Title, before.Items.Where(item => item.State == HexPlanState.Done), now) };
    }

    private static HexPlanSection[] PutAside(IReadOnlyList<HexPlanSection> earlier, string? title, IEnumerable<HexPlanItem> finished, long now)
    {
        HexPlanItem[] put = [.. finished];
        return put.Length == 0 ? [.. earlier] : [.. earlier.Append(new HexPlanSection(title, put, now)).TakeLast(EARLIER_KEPT)];
    }

    /// <summary>
    /// The plan with the done and dropped steps put aside as an earlier task, what's open left, null when nothing is done.
    /// </summary>
    public HexAgentPlan? ClearDone(long now)
    {
        if (!Items.Any(item => item.State is HexPlanState.Done or HexPlanState.Dropped))
            return null;
        return this with
        {
            Items = [.. Items.Where(item => item.Open)],
            Earlier = PutAside(Earlier, Title, Items.Where(item => item.State is HexPlanState.Done or HexPlanState.Dropped), now)
        };
    }

    private int IndexOf(string id)
    {
        for (int i = 0; i < Items.Count; i++)
            if (Items[i].Id == id)
                return i;
        return -1;
    }

    // The plan with one row changed, null for a row it hasn't or a change that changes nothing
    private HexAgentPlan? Changed(string id, Func<HexPlanItem, HexPlanItem> change)
    {
        int index = IndexOf(id);
        if (index < 0 || change(Items[index]) is not { } changed || changed == Items[index])
            return null;
        HexPlanItem[] items = [.. Items];
        items[index] = changed;
        return this with { Items = items };
    }

    /// <summary>A step marked doing, done, blocked, dropped or back to pending.</summary>
    public HexAgentPlan? WithState(string id, HexPlanState state) => Changed(id, item => item with { State = state });

    /// <summary>A step's words, blank words leave it as it was.</summary>
    public HexAgentPlan? WithText(string id, string text) =>
        text.Trim() is { Length: > 0 } said ? Changed(id, item => item with { Text = said }) : null;

    /// <summary>A step's note, the reason it's blocked, blank clears it.</summary>
    public HexAgentPlan? WithNote(string id, string? note) =>
        Changed(id, item => item with { Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim() });

    /// <summary>A step the person adds, pending, after the last step still open (before the done ones that follow it).</summary>
    public HexAgentPlan? Added(string text, out string? id)
    {
        id = null;
        if (text.Trim() is not { Length: > 0 } said)
            return null;
        id = NewId(Items);
        int after = -1;
        for (int i = 0; i < Items.Count; i++)
            if (Items[i].Open)
                after = i;
        List<HexPlanItem> items = [.. Items];
        items.Insert(after < 0 ? items.Count : after + 1, new HexPlanItem(said, HexPlanState.Pending, id));
        return this with { Items = items };
    }

    /// <summary>
    /// A step still open moved up (by -1) or down (by 1) past the next open step that way, the done ones between kept
    /// where they are. Null where there's none to move past.
    /// </summary>
    public HexAgentPlan? Moved(string id, int by)
    {
        int index = IndexOf(id);
        if (index < 0 || by == 0)
            return null;
        int step = Math.Sign(by);
        int other = index + step;
        while (other >= 0 && other < Items.Count && !Items[other].Open)
            other += step;
        if (other < 0 || other >= Items.Count)
            return null;
        HexPlanItem[] items = [.. Items];
        (items[index], items[other]) = (items[other], items[index]);
        return this with { Items = items };
    }

    /// <summary>A step taken out of the plan altogether.</summary>
    public HexAgentPlan? Removed(string id) =>
        IndexOf(id) is >= 0 and var index ? this with { Items = [.. Items.Where((_, i) => i != index)] } : null;

    /// <summary>
    /// The plan with when each step was done kept: a step newly done is stamped now, one done before keeps its time and
    /// one no longer done loses it. A first plan (nothing before) is left as it says.
    /// </summary>
    public static HexAgentPlan Stamped(HexAgentPlan? before, HexAgentPlan after, long now)
    {
        if (before is null)
            return after;
        HexPlanItem[]? items = null;
        for (int i = 0; i < after.Items.Count; i++)
        {
            HexPlanItem item = after.Items[i];
            long at = item.DoneAt;
            if (item.State != HexPlanState.Done)
                at = 0;
            else if (at == 0)
            {
                HexPlanItem? was = item.Id is { } id ? before.Items.FirstOrDefault(each => each.Id == id) : before.Items.FirstOrDefault(each => each.Text == item.Text);
                at = was is { State: HexPlanState.Done } ? was.DoneAt : now;
            }

            if (at == item.DoneAt)
                continue;
            items ??= [.. after.Items];
            items[i] = item with { DoneAt = at };
        }

        return items is null ? after : after with { Items = items };
    }

    /// <summary>What a chat's file keeps, the plan with when its last native update was.</summary>
    public JsonObject ToStored()
    {
        JsonObject stored = new() { ["title"] = Title, ["items"] = Rows(Items, stored: true) };
        if (NativeAt > 0) stored["native_at"] = NativeAt;
        if (NativeKey is not null) stored["native_key"] = NativeKey;
        if (Earlier.Count > 0)
            stored["earlier"] = new JsonArray([.. Earlier.Select(section => (JsonNode)new JsonObject
            {
                ["title"] = section.Title, ["at"] = section.At, ["items"] = Rows(section.Items, stored: true)
            })]);
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

    /// <summary>The plan as hex_plan_read gives it, the steps of the task going on and how many earlier tasks were put aside.</summary>
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["title"] = Title, ["items"] = Rows(Items, stored: false) };
        if (Earlier.Count > 0)
            json["earlier_tasks"] = Earlier.Count;
        return json;
    }

    private static JsonArray Rows(IEnumerable<HexPlanItem> items, bool stored)
    {
        var rows = new JsonArray();
        foreach (HexPlanItem item in items)
        {
            var row = new JsonObject { ["text"] = item.Text, ["state"] = item.State.ToString().ToLowerInvariant() };
            if (item.Id is not null) row["id"] = item.Id;
            if (item.Note is not null) row["note"] = item.Note;
            if (item.Agent is not null) row["agent"] = item.Agent;
            if (item.Files is { Count: > 0 } files) row["files"] = new JsonArray([.. files.Select(file => (JsonNode)file)]);
            if (stored && item.DoneAt > 0) row["done_at"] = item.DoneAt;
            if (item.EtaAt > 0) row["eta_at"] = item.EtaAt;
            rows.Add((JsonNode)row);
        }

        return rows;
    }

    private static List<HexPlanItem> ItemsOf(JsonNode? items) =>
        [.. (items as JsonArray ?? []).OfType<JsonObject>().Select(item => new HexPlanItem(
            item.String("text") ?? string.Empty, Enum.TryParse(item.String("state"), true, out HexPlanState state) ? state : HexPlanState.Pending,
            item.String("id"), item.String("note"), item.Integer("agent"), FilesOf(item["files"]))
        { DoneAt = (long)(item.Number("done_at") ?? 0), EtaAt = (long)(item.Number("eta_at") ?? 0) })];

    public static HexAgentPlan FromJson(JsonObject plan) => new(plan.String("title"), ItemsOf(plan["items"]))
    {
        NativeAt = plan.Number("native_at") ?? 0,
        NativeKey = plan.String("native_key"),
        Earlier = [.. (plan["earlier"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(section => new HexPlanSection(section.String("title"), ItemsOf(section["items"]), (long)(section.Number("at") ?? 0)))
            .Where(section => section.Items.Count > 0).TakeLast(EARLIER_KEPT)]
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
