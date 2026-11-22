using System.Text.Json.Nodes;

using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// One device managing another's agents over a link: the chats it started there take Steer, the rest take Manage, deleting
/// takes confirm, and every call is audited and noticed on the device that has the chats. B calls, A has the chats.
/// </summary>
public sealed class HexLinkAgentManageTests
{
    private static readonly HexLinkRights Steer = HexLinkRights.View | HexLinkRights.Steer;
    private static readonly HexLinkRights Manage = Steer | HexLinkRights.Manage;

    // #1 started by B, #2 started on A, #3 started by another device, #4 under #1 started on A, #5 under #1 started by B (running)
    private static void Fill(LinkedPair pair, bool foreignChild = true) => pair.A.OnEditor(() =>
    {
        pair.A.Hub.Add(new HexAgentChat(1, "claude_code") { LinkDevice = HexLinkTestKit.DeviceB.Id });
        pair.A.Hub.Add(new HexAgentChat(2, "claude_code") { Name = "mine" });
        pair.A.Hub.Add(new HexAgentChat(3, "claude_code") { LinkDevice = HexLinkTestKit.DeviceC.Id });
        pair.A.Hub.Add(new HexAgentChat(4, "claude_code") { Parent = foreignChild ? 1 : null, Depth = foreignChild ? 1 : 0 });
        var running = new HexAgentChat(5, "claude_code") { Parent = 1, Depth = 1, LinkDevice = HexLinkTestKit.DeviceB.Id };
        running.Ask("work");
        pair.A.Hub.Add(running);
    });

    private static IEnumerable<(string Name, JsonObject Arguments)> Managed(LinkedPair pair) =>
        pair.A.LinkTools.Called.Where(call => call.Name is "hex_agent_stop" or "hex_agent_restart" or "hex_agent_rename" or "hex_agent_close");

    [Theory]
    [InlineData("stop", "hex_agent_stop")]
    [InlineData("restart", "hex_agent_restart")]
    [InlineData("rename", "hex_agent_rename")]
    [InlineData("close", "hex_agent_close")]
    [InlineData("delete", "hex_agent_close")]
    public async Task A_device_manages_a_chat_it_started_with_steer_and_it_is_audited_and_noticed(string action, string tool)
    {
        using var pair = await LinkedPair.Up(Steer);
        Fill(pair, foreignChild: false);
        // #5 is the device's chat and running, which stopping, restarting and deleting have to cope with
        int chat = action is "stop" or "restart" or "rename" ? 5 : 1;
        var (ok, message) = await pair.PeerA.AgentAction(chat, action, action == "rename" ? "renamed" : null);
        Assert.True(ok, message);
        Assert.Equal(tool, Assert.Single(Managed(pair)).Name);
        Assert.Equal(action == "delete", Managed(pair).Single().Arguments["confirm"]?.GetValue<bool>() == true);
        string said = action == "close" ? "clear" : action;
        Assert.Contains(pair.A.Audited, line => line.What == "manage" && line.Who == HexLinkTestKit.DeviceB && line.Detail.StartsWith(said, StringComparison.Ordinal) && line.Detail.Contains($"#{chat}", StringComparison.Ordinal));
        Assert.Contains(pair.A.Notices, notice => notice.Contains(HexLinkTestKit.DeviceB.Name, StringComparison.Ordinal) && notice.Contains(said, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("stop", 2)]
    [InlineData("restart", 3)]
    [InlineData("rename", 2)]
    [InlineData("close", 3)]
    [InlineData("delete", 2)]
    public async Task A_chat_the_device_did_not_start_is_refused_without_manage_naming_the_permission(string action, int chat)
    {
        using var pair = await LinkedPair.Up(Steer);
        Fill(pair);
        var (ok, message) = await pair.PeerA.AgentAction(chat, action, "x");
        Assert.False(ok);
        Assert.Contains("Manage agents", message, StringComparison.Ordinal);
        Assert.Empty(Managed(pair));
        Assert.Contains(pair.A.Audited, line => line.What == "manage refused" && line.Detail.Contains($"#{chat}", StringComparison.Ordinal));
        Assert.Contains(pair.A.Notices, notice => notice.Contains("refused", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("stop", 2)]
    [InlineData("restart", 3)]
    [InlineData("rename", 3)]
    [InlineData("close", 2)]
    [InlineData("delete", 3)]
    public async Task Manage_lets_a_device_manage_any_chat(string action, int chat)
    {
        using var pair = await LinkedPair.Up(Manage);
        Fill(pair);
        var (ok, message) = await pair.PeerA.AgentAction(chat, action, "x");
        Assert.True(ok, message);
        Assert.Single(Managed(pair));
        Assert.Contains(pair.A.Audited, line => line.What == "manage" && line.Detail.Contains("started by someone else", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Manage_without_steer_does_nothing()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View | HexLinkRights.Manage);
        Fill(pair);
        var refused = await Assert.ThrowsAnyAsync<HexLinkException>(() => pair.PeerA.Stop(1));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, refused.Code);
        Assert.Empty(Managed(pair));
    }

    [Fact]
    public async Task A_chat_of_the_devices_with_somebody_elses_chat_under_it_needs_manage_to_stop_or_delete()
    {
        using var pair = await LinkedPair.Up(Steer);
        Fill(pair);
        // #1 is the device's own, #4 under it isn't
        var (stopped, why) = await pair.PeerA.AgentAction(1, "stop");
        Assert.False(stopped);
        Assert.Contains("#4", why, StringComparison.Ordinal);
        Assert.Contains("Manage agents", why, StringComparison.Ordinal);
        var (deleted, _) = await pair.PeerA.AgentAction(1, "delete");
        Assert.False(deleted);
        // Restarting and renaming don't reach the chats under it
        Assert.True((await pair.PeerA.AgentAction(1, "restart")).Ok);
        Assert.True((await pair.PeerA.AgentAction(1, "rename", "named")).Ok);
        pair.BOnA = Manage;
        Assert.True((await pair.PeerA.AgentAction(1, "stop")).Ok);
    }

    [Fact]
    public async Task Deleting_takes_confirm_and_clearing_does_not()
    {
        using var pair = await LinkedPair.Up(Steer);
        Fill(pair, foreignChild: false);
        McpToolResult unconfirmed = await pair.PeerA.Call("hex_agent_close", new JsonObject { ["id"] = 1 });
        Assert.True(unconfirmed.IsError);
        Assert.Contains("confirm", unconfirmed.AllText(), StringComparison.Ordinal);
        Assert.Empty(Managed(pair));
        Assert.False((await pair.PeerA.Call("hex_agent_close", new JsonObject { ["id"] = 1, ["clear"] = true })).IsError);
        Assert.False((await pair.PeerA.Call("hex_agent_close", new JsonObject { ["id"] = 1, ["confirm"] = true })).IsError);
        Assert.Equal(2, Managed(pair).Count());
    }

    [Fact]
    public async Task A_chat_that_is_not_there_is_refused_for_every_action()
    {
        using var pair = await LinkedPair.Up(Manage);
        Fill(pair);
        foreach (string action in new[] { "stop", "restart", "rename", "close", "delete" })
        {
            var (ok, message) = await pair.PeerA.AgentAction(99, action, "x");
            Assert.False(ok);
            Assert.Contains("no such chat", message, StringComparison.Ordinal);
        }

        Assert.Empty(Managed(pair));
        Assert.Equal(5, pair.A.Audited.Count(line => line.What == "manage refused"));
    }

    [Fact]
    public async Task The_fleet_says_which_chats_the_caller_started()
    {
        using var pair = await LinkedPair.Up(Steer);
        Fill(pair);
        await pair.PeerA.WatchFleet();
        Assert.True(HexLinkTestKit.Eventually(() => pair.PeerA.Fleet.Count == 5));
        Assert.Equal([1, 5], pair.PeerA.Fleet.Where(row => row.StartedByYou).Select(row => row.Id).Order());
    }

    [Fact]
    public async Task The_owner_of_a_chat_is_kept_when_it_is_written_down()
    {
        var chat = new HexAgentChat(7, "codex") { LinkDevice = HexLinkTestKit.DeviceB.Id };
        JsonObject written = HexAgentChatStore.Head(chat, includeLinkOwnership: true);
        Assert.Equal(HexLinkTestKit.DeviceB.Id, HexAgentChatStore.Read(written)!.LinkDevice);
        Assert.Null(HexAgentChatStore.Head(chat, includeLinkOwnership: false)["link_device"]);
        await Task.CompletedTask;
    }

    [Fact]
    public void Manage_is_spoken_and_kept_as_a_word()
    {
        Assert.Equal(HexLinkRights.View | HexLinkRights.Manage, HexLinkTrust.RightsOf(["view", "manage"]));
        Assert.Contains("manage", HexLinkTrust.RightsNames(Manage));
        Assert.Equal("manage", HexLinkData.Word(HexLinkRights.Manage));
        Assert.Equal(Manage, HexLinkData.RightsOf(new JsonObject { ["granted"] = HexLinkData.Words(Manage) }, "granted"));
        Assert.Equal(HexLinkRights.Steer, HexLinkAllowed.Needs("hex_agent_rename"));
    }
}
