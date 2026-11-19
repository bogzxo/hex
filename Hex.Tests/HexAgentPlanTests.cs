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

    [Fact]
    public void The_plan_rules_are_said_without_apostrophes_for_codex_toml()
    {
        Assert.DoesNotContain('\'', HexAgentProfiles.PLAN_RULES);
        Assert.Contains("hex_plan_update", HexAgentProfiles.PLAN_RULES);
        Assert.DoesNotContain("hex_plan_update", HexAgentProfiles.HARNESS_RULES);
    }
}
