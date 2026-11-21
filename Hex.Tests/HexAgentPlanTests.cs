using System.Text.Json.Nodes;
using Hex.Core;
using Hex.Dawn;
using Xunit;

namespace Hex.Tests;

/// <summary>A chat's plan, its native updates from an agent system taken in beside what hex_plan_update wrote, and the turn context it adds.</summary>
public sealed class HexAgentPlanTests
{
    private static HexAgentPlan.NativeStep Step(string text, string status, string? id = null) => new(id, text, HexAgentPlan.StateOf(status));

    [Fact]
    public void A_native_update_after_an_mcp_write_keeps_notes_owners_files_and_unmatched_rows()
    {
        var written = new HexAgentPlan("Fix", [
            new HexPlanItem("Read it", HexPlanState.Doing, "read", "From chat", 4, ["a.cs"]),
            new HexPlanItem("Only in MCP", HexPlanState.Pending, "mcp")
        ]);

        HexAgentPlan taken = HexAgentPlan.TakeNative(written, [Step("Read it", "completed"), Step("Fix it", "in_progress")], null, 10, "t1")!;
        Assert.Equal("Fix", taken.Title);
        Assert.Equal(3, taken.Items.Count);
        Assert.Equal(new HexPlanItem("Read it", HexPlanState.Done, "read", "From chat", 4, written.Items[0].Files), taken.Items[0]);
        Assert.Equal(written.Items[1], taken.Items[1]);
        Assert.Equal(("Fix it", HexPlanState.Doing), (taken.Items[2].Text, taken.Items[2].State));
        Assert.NotNull(taken.Items[2].Id);

        // The same update again, an older one, and one that changes nothing are passed over
        Assert.Null(HexAgentPlan.TakeNative(taken, [Step("Read it", "pending")], null, 11, "t1"));
        Assert.Null(HexAgentPlan.TakeNative(taken, [Step("Read it", "pending")], null, 5, "t0"));
        Assert.Null(HexAgentPlan.TakeNative(taken, [Step("Read it", "completed")], null, 0, null));

        // A step with an ID matches the row with it whatever its text
        HexAgentPlan renamed = HexAgentPlan.TakeNative(taken, [Step("Read the handler", "completed", "read")], null, 12, "t2")!;
        Assert.Equal(("Read the handler", "From chat"), (renamed.Items[0].Text, renamed.Items[0].Note));
    }

    [Fact]
    public void A_saved_chat_keeps_when_its_last_native_update_was()
    {
        HexAgentPlan taken = HexAgentPlan.TakeNative(null, [Step("Read", "pending")], "T", 20, "k")!;
        HexAgentPlan restored = HexAgentPlan.FromJson(JsonNode.Parse(taken.ToStored().ToJsonString())!.AsObject());
        Assert.Equal((20.0, "k"), (restored.NativeAt, restored.NativeKey));
        Assert.Null(HexAgentPlan.TakeNative(restored, [Step("Read", "completed")], null, 19, "older"));
        // What hex_plan_read gives back says nothing of it
        Assert.Null(taken.ToJson()["native_at"]);
    }

    [Theory]
    [InlineData("in_progress", "Doing")]
    [InlineData("inProgress", "Doing")]
    [InlineData("completed", "Done")]
    [InlineData("pending", "Pending")]
    [InlineData("cancelled", "Dropped")]
    [InlineData(null, "Pending")]
    public void Native_statuses_map_to_plan_states(string? status, string state) => Assert.Equal(state, HexAgentPlan.StateOf(status).ToString());

    [Fact]
    public void An_owner_can_be_cleared_with_null_by_a_typed_client()
    {
        JsonObject schema = McpSchema.Object([("agent", McpSchema.IntegerOrNull("owner"))]);
        Assert.Null(McpSchema.Check(schema, new JsonObject { ["agent"] = null }));
        Assert.Null(McpSchema.Check(schema, new JsonObject { ["agent"] = 3 }));
        Assert.NotNull(McpSchema.Check(schema, new JsonObject { ["agent"] = "three" }));
        Assert.Equal("""["integer","null"]""", schema["properties"]!["agent"]!["type"]!.ToJsonString());
    }

    [Fact]
    public void New_ids_skip_those_taken()
    {
        Assert.Equal("s3", HexAgentPlan.NewId([new HexPlanItem("a", Id: "s1"), new HexPlanItem("b", Id: "s2")]));
        Assert.Equal("s2", HexAgentPlan.NewId([new HexPlanItem("a", Id: "s3")]));
    }

    [Fact]
    public void A_turn_in_a_chat_with_steps_left_starts_with_where_its_plan_stands_and_nothing_otherwise()
    {
        var chat = new HexAgentChat(1, "codex");
        Assert.Equal("what is 2+2", DawnScene.PlanPrompt(chat, "what is 2+2"));

        chat.SetPlan(new HexAgentPlan(null, [
            new HexPlanItem("Read", HexPlanState.Done, "s1"),
            new HexPlanItem("Fix", HexPlanState.Doing, "s2"),
            new HexPlanItem("Test", HexPlanState.Pending, "s3"),
            new HexPlanItem("Ship", HexPlanState.Blocked, "s4", "needs a key")
        ]));
        string said = DawnScene.PlanPrompt(chat, "go ahead");
        Assert.StartsWith("[Your Plan tab: 1 of 4 done, doing s2 \"Fix\"; next s3 \"Test\"; blocked s4 \"Ship\".", said);
        Assert.EndsWith("\n\ngo ahead", said);

        chat.SetPlan(new HexAgentPlan(null, [new HexPlanItem("Read", HexPlanState.Done, "s1")]));
        Assert.Equal("go ahead", DawnScene.PlanPrompt(chat, "go ahead"));
    }

    private static HexAgentPlan Sample() => new("Task", [
        new HexPlanItem("One", HexPlanState.Done, "s1"),
        new HexPlanItem("Two", HexPlanState.Doing, "s2"),
        new HexPlanItem("Three", HexPlanState.Done, "s3"),
        new HexPlanItem("Four", HexPlanState.Pending, "s4"),
        new HexPlanItem("Five", HexPlanState.Dropped, "s5")
    ]);

    [Fact]
    public void A_step_newly_done_is_stamped_and_keeps_its_time_until_reopened()
    {
        HexAgentPlan first = Sample();
        Assert.Same(first, HexAgentPlan.Stamped(null, first, 100));

        HexAgentPlan done = HexAgentPlan.Stamped(first, first.WithState("s2", HexPlanState.Done)!, 200);
        Assert.Equal(200, done.Items[1].DoneAt);
        // Done before, by ID or (without one) by its words, keeps what it had: nothing for a plan from before times were kept
        Assert.Equal(0, done.Items[0].DoneAt);
        Assert.Equal(200, HexAgentPlan.Stamped(done, done.WithText("s4", "Four again")!, 300).Items[1].DoneAt);

        HexAgentPlan reopened = HexAgentPlan.Stamped(done, done.WithState("s2", HexPlanState.Pending)!, 400);
        Assert.Equal(0, reopened.Items[1].DoneAt);
    }

    [Fact]
    public void A_new_task_puts_the_old_ones_done_steps_aside_under_its_title()
    {
        HexAgentPlan next = HexAgentPlan.NewTask(Sample(), "Next", [new HexPlanItem("Begin", HexPlanState.Doing, "n1")], 500);
        Assert.Equal(("Next", 1), (next.Title, next.Items.Count));
        HexPlanSection aside = Assert.Single(next.Earlier);
        Assert.Equal(("Task", 500L), (aside.Title, aside.At));
        Assert.Equal(["One", "Three"], aside.Items.Select(item => item.Text));

        // Only so many earlier tasks are kept, the oldest go
        HexAgentPlan plan = next;
        for (int i = 0; i < HexAgentPlan.EARLIER_KEPT + 3; i++)
            plan = HexAgentPlan.NewTask(plan with { Items = [new HexPlanItem($"T{i}", HexPlanState.Done, "x")] }, $"Task {i}", [], i);
        Assert.Equal(HexAgentPlan.EARLIER_KEPT, plan.Earlier.Count);
        Assert.Equal($"Task {HexAgentPlan.EARLIER_KEPT + 1}", plan.Earlier[^1].Title);
    }

    [Fact]
    public void Clearing_what_was_done_leaves_the_open_steps_and_puts_the_rest_aside()
    {
        HexAgentPlan cleared = Sample().ClearDone(700)!;
        Assert.Equal(["s2", "s4"], cleared.Items.Select(item => item.Id));
        Assert.Equal(["s1", "s3", "s5"], Assert.Single(cleared.Earlier).Items.Select(item => item.Id));
        Assert.Null(cleared.ClearDone(800));
    }

    [Fact]
    public void The_person_adds_moves_edits_and_removes_steps()
    {
        HexAgentPlan plan = Sample();

        HexAgentPlan added = plan.Added("  New one ", out string? id)!;
        Assert.Equal(("New one", HexPlanState.Pending, id), (added.Items[4].Text, added.Items[4].State, added.Items[4].Id));
        Assert.DoesNotContain(plan.Items, item => item.Id == id);
        Assert.Null(plan.Added("   ", out _));

        // Moving past the done step between to the next open one, none past the last
        Assert.Equal(["s1", "s4", "s3", "s2", "s5"], plan.Moved("s2", 1)!.Items.Select(item => item.Id));
        Assert.Equal(["s1", "s4", "s3", "s2", "s5"], plan.Moved("s4", -1)!.Items.Select(item => item.Id));
        Assert.Null(plan.Moved("s2", -1));
        Assert.Null(plan.Moved("s4", 1));
        Assert.Null(plan.Moved("nope", 1));

        Assert.Equal("Second", plan.WithText("s2", " Second ")!.Items[1].Text);
        Assert.Null(plan.WithText("s2", " "));
        Assert.Null(plan.WithText("s2", "Two"));
        Assert.Equal("why", plan.WithNote("s2", " why ")!.Items[1].Note);
        Assert.Null(plan.WithNote("s2", "")); // no note to clear
        Assert.Null(plan.WithState("s2", HexPlanState.Doing));
        Assert.Equal(4, plan.Removed("s3")!.Items.Count);
        Assert.Null(plan.Removed("nope"));
    }

    [Fact]
    public void Earlier_tasks_and_done_times_are_saved_with_the_chat_but_only_counted_for_the_agent()
    {
        HexAgentPlan plan = HexAgentPlan.NewTask(Sample(), "Next", [new HexPlanItem("Begin", HexPlanState.Done, "n1") { DoneAt = 42 }], 600);
        HexAgentPlan restored = HexAgentPlan.FromJson(JsonNode.Parse(plan.ToStored().ToJsonString())!.AsObject());
        Assert.Equal(42, restored.Items[0].DoneAt);
        HexPlanSection aside = Assert.Single(restored.Earlier);
        Assert.Equal(("Task", 600L, 2), (aside.Title, aside.At, aside.Items.Count));

        JsonObject read = plan.ToJson();
        Assert.Equal(1, read["earlier_tasks"]!.GetValue<int>());
        Assert.Null(read["earlier"]);
        Assert.Null(read["items"]![0]!["done_at"]);
    }

    [Fact]
    public void A_native_list_naming_none_of_a_finished_plans_rows_is_a_new_task()
    {
        HexAgentPlan finished = new("Old", [new HexPlanItem("Read", HexPlanState.Done, "s1"), new HexPlanItem("Fix", HexPlanState.Done, "s2")]);
        HexAgentPlan next = HexAgentPlan.TakeNative(finished, [Step("Plan menus", "in_progress"), Step("Group items", "pending")], "Menus", 10, "k")!;
        Assert.Equal(("Menus", 2), (next.Title, next.Items.Count));
        Assert.Equal(["Read", "Fix"], Assert.Single(next.Earlier).Items.Select(item => item.Text));
        Assert.Equal((10.0, "k"), (next.NativeAt, next.NativeKey));

        // A list that goes on with a plan still open adds to it, and keeps what was put aside before
        HexAgentPlan more = HexAgentPlan.TakeNative(next, [Step("Plan menus", "completed"), Step("Ship", "pending")], null, 11, "k2")!;
        Assert.Equal(3, more.Items.Count);
        Assert.Single(more.Earlier);
    }

    [Fact]
    public void The_plan_rules_are_said_without_apostrophes_for_codex_toml()
    {
        Assert.DoesNotContain('\'', HexAgentProfiles.PLAN_RULES);
        Assert.Contains("hex_plan_update", HexAgentProfiles.PLAN_RULES);
        Assert.DoesNotContain("hex_plan_update", HexAgentProfiles.HARNESS_RULES);

        // The rules an agent is given say where its scratch files go, still with nothing Codex's TOML would choke on
        string given = HexAgentProfiles.HarnessRules();
        Assert.StartsWith(HexAgentProfiles.HARNESS_RULES, given);
        Assert.Contains(HexData.AgentScratch.Replace('\\', '/'), given);
        Assert.DoesNotContain('\'', given);
    }
}
