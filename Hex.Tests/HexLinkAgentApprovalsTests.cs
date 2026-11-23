using System.Text.Json.Nodes;

using Hex.Core;

namespace Hex.Tests;

public sealed class HexLinkAgentApprovalsTests
{
    [Fact]
    public void Pending_approval_is_numbered_and_remains_visible_while_the_chat_runs()
    {
        var hub = new HexAgentHub();
        var chat = new HexAgentChat(1, "claude_code");
        hub.Add(chat);
        chat.Ask("Build");
        chat.Approval("PowerShell wants to run", "dotnet test", _ => { });

        hub.Pump(100);
        HexAgentApprovalStatus status = Assert.Single(hub.Snapshot.Rows[chat.Id].Approvals);
        Assert.Equal("asks leave: PowerShell wants to run (request 1)", status.Summary);
        Assert.Equal("dotnet test", status.Detail);
        Assert.True(chat.Running);
        chat.Decide(Assert.Single(HexAgentHub.Approvals(chat)), true);
        hub.Pump(100);
        Assert.Empty(hub.Snapshot.Rows[chat.Id].Approvals);
    }

    [Fact]
    public void A_link_child_reports_only_new_approvals_from_its_own_spawn_receipt()
    {
        var address = new HexLinkAgentStarter(new string('A', 64), 27, Guid.NewGuid().ToString("N"));
        var child = new HexLinkAgentChild(address);
        var approval = new HexAgentApprovalStatus(1, "PowerShell wants to run", "dotnet test");
        var row = new HexLinkRow(27, "tester", "claude_code", null, null, 0, "running", "", 1, 0, 2, true, null)
            { StarterReceipt = address.Receipt, Approvals = [approval] };

        Assert.Empty(child.NewApprovals(row with { StarterReceipt = Guid.NewGuid().ToString("N") }));
        Assert.Equal(approval, Assert.Single(child.NewApprovals(row)));
        Assert.Empty(child.NewApprovals(row));
        Assert.Empty(child.NewApprovals(row with { Approvals = [] }));
        Assert.Single(child.NewApprovals(row));
    }

    [Fact]
    public void Starter_identity_and_child_address_survive_reload_without_leaking_into_the_chat_mirror()
    {
        var address = new HexLinkAgentStarter(new string('A', 64), 27, Guid.NewGuid().ToString("N"));
        var chat = new HexAgentChat(10, "claude_code") { LinkStarter = address };
        chat.LinkChildren.Add(new HexLinkAgentChild(address with { Chat = 28 }));

        HexAgentChat saved = Assert.IsType<HexAgentChat>(HexAgentChatStore.Read(HexAgentChatStore.Head(chat)));
        Assert.Equal(address, saved.LinkStarter);
        Assert.Equal(28, Assert.Single(saved.LinkChildren).Address.Chat);
        Assert.True(address.Matches(new HexLinkAgentCall(new HexLinkDevice(address.Device, "starter"), 27, null)));
        Assert.False(address.Matches(new HexLinkAgentCall(new HexLinkDevice(new string('B', 64), "other"), 27, null)));
        Assert.False(address.Matches(new HexLinkAgentCall(new HexLinkDevice(address.Device, "starter"), 28, null)));
        Assert.Null(HexLinkChats.Head(chat)["link_starter"]);
        Assert.Null(HexLinkChats.Head(chat)["link_children"]);
    }

    [Theory]
    [InlineData("claude_code", "permission_mode", "default", "acceptEdits")]
    [InlineData("codex", "sandbox", "read-only", "workspace-write")]
    [InlineData("antigravity", "mode", "plan", "accept-edits")]
    public void The_target_bounds_provider_defaults(string agent, string key, string nothing, string edits)
    {
        var readOnly = new JsonObject();
        var editing = new JsonObject { ["options"] = new JsonObject { ["effort"] = "low" } };

        Assert.Null(HexLinkSpawnPolicy.Apply(readOnly, agent, HexLinkPreApproval.Nothing));
        Assert.Null(HexLinkSpawnPolicy.Apply(editing, agent, HexLinkPreApproval.Edits));
        Assert.Equal(nothing, readOnly["options"]![key]!.GetValue<string>());
        Assert.Equal(edits, editing["options"]![key]!.GetValue<string>());
        Assert.Equal("low", editing["options"]!["effort"]!.GetValue<string>());
    }

    [Fact]
    public void A_chat_started_by_another_takes_its_permission_level_unless_its_options_say_otherwise()
    {
        var inherited = new Dictionary<string, string>();
        Hex.Dawn.DawnScene.InheritPermissions(inherited, ["model", "permission_mode"], key => key == "permission_mode" ? "bypassPermissions" : "opus");
        Assert.Equal("bypassPermissions", inherited["permission_mode"]);
        Assert.Equal("opus", inherited["model"]);
        Assert.False(inherited.ContainsKey("effort"));

        var chosen = new Dictionary<string, string> { ["permission_mode"] = "plan" };
        Hex.Dawn.DawnScene.InheritPermissions(chosen, ["permission_mode"], _ => "bypassPermissions");
        Assert.Equal("plan", chosen["permission_mode"]);

        var none = new Dictionary<string, string>();
        Hex.Dawn.DawnScene.InheritPermissions(none, ["permission_mode"], _ => null);
        Assert.Empty(none);
    }

    [Fact]
    public void Edits_do_not_authorize_shell_rules_bypass_flags_or_hidden_options()
    {
        var edit = JsonNode.Parse("""{"permissions":{"allow":["Edit"],"deny":["PowerShell"]}}""")!.AsObject();
        var shell = JsonNode.Parse("""{"permissions":{"allow":["Edit\nPowerShell"]}}""")!.AsObject();
        var bypass = JsonNode.Parse("""{"options":{"permission_mode":"bypassPermissions"}}""")!.AsObject();
        var hidden = JsonNode.Parse("""{"options":{"allow":"PowerShell"}}""")!.AsObject();

        Assert.NotNull(HexLinkSpawnPolicy.Apply(edit, "claude_code", HexLinkPreApproval.Nothing));
        Assert.Null(HexLinkSpawnPolicy.Apply(edit, "claude_code", HexLinkPreApproval.Edits));
        Assert.NotNull(HexLinkSpawnPolicy.Apply(shell, "claude_code", HexLinkPreApproval.Edits));
        Assert.NotNull(HexLinkSpawnPolicy.Apply(bypass, "claude_code", HexLinkPreApproval.Edits));
        Assert.NotNull(HexLinkSpawnPolicy.Apply(hidden, "claude_code", HexLinkPreApproval.Edits));
        Assert.Null(HexLinkSpawnPolicy.Apply(bypass, "claude_code", HexLinkPreApproval.Everything));
    }
}
