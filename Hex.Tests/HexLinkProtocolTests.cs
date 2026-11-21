using System.Text;
using System.Text.Json.Nodes;

using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

/// <summary>
/// Hex Link's session and host over a connection in memory: asking and answering both ways at once, errors crossing
/// the link with their codes, the line limit, pings, the request rate, hello, and every method refused without the
/// right it takes and allowed with it, rights changed mid-link taking hold at the next request, the tool allow list,
/// and a peer sending garbage.
/// </summary>
public sealed class HexLinkProtocolTests
{
    private static Func<string, JsonObject, CancellationToken, Task<JsonNode?>> Echo(string who) =>
        (method, parameters, _) => method switch
        {
            "echo" => Task.FromResult<JsonNode?>(new JsonObject { ["who"] = who, ["said"] = parameters["text"]?.DeepClone() }),
            "slow" => Task.Delay(400).ContinueWith(_ => (JsonNode?)new JsonObject { ["slow"] = true }),
            "conflict" => throw new HexLinkException(HexLinkErrors.CONFLICT, "it moved", new JsonObject { ["hash"] = "abc" }),
            "boom" => throw new InvalidOperationException("bang"),
            _ => throw new HexLinkException(HexLinkErrors.METHOD_NOT_FOUND, "no")
        };

    [Fact]
    public async Task Both_ends_ask_and_answer_at_once()
    {
        var (a, b) = Sessions();
        a.Requested = Echo("a");
        b.Requested = Echo("b");
        a.Start();
        b.Start();

        Task<JsonNode?>[] asked = [.. Enumerable.Range(0, 20).SelectMany(i => new[]
        {
            a.Ask("echo", new JsonObject { ["text"] = $"from a {i}" }),
            b.Ask("echo", new JsonObject { ["text"] = $"from b {i}" })
        })];
        JsonNode?[] answers = await Task.WhenAll(asked).WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS));
        for (int i = 0; i < 20; i++)
        {
            Assert.Equal("b", answers[i * 2]!["who"]!.GetValue<string>());
            Assert.Equal($"from a {i}", answers[i * 2]!["said"]!.GetValue<string>());
            Assert.Equal("a", answers[i * 2 + 1]!["who"]!.GetValue<string>());
        }

        a.Dispose();
        b.Dispose();
    }

    [Fact]
    public async Task A_slow_request_never_holds_up_a_quick_one()
    {
        var (a, b) = Sessions();
        b.Requested = Echo("b");
        a.Start();
        b.Start();
        Task<JsonNode?> slow = a.Ask("slow");
        JsonNode? quick = await a.Ask("echo", new JsonObject { ["text"] = "hi" }).WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS));
        Assert.False(slow.IsCompleted);
        Assert.Equal("hi", quick!["said"]!.GetValue<string>());
        Assert.True((await slow)!["slow"]!.GetValue<bool>());
        a.Dispose();
    }

    [Fact]
    public async Task Errors_cross_the_link_with_their_code_and_data()
    {
        var (a, b) = Sessions();
        b.Requested = Echo("b");
        a.Start();
        b.Start();

        var conflict = await Assert.ThrowsAsync<HexLinkException>(() => a.Ask("conflict"));
        Assert.Equal(HexLinkErrors.CONFLICT, conflict.Code);
        Assert.Equal("abc", conflict.Detail!["hash"]!.GetValue<string>());
        Assert.Equal("it moved", conflict.Message);

        Assert.Equal(HexLinkErrors.INTERNAL_ERROR, await CodeOf(a.Ask("boom")));
        Assert.Equal(HexLinkErrors.METHOD_NOT_FOUND, await CodeOf(a.Ask("what")));
        a.Dispose();
    }

    [Fact]
    public async Task A_line_too_long_closes_the_link_and_fails_what_was_waiting()
    {
        var (a, b) = Sessions();
        a.Requested = (_, _, _) => new TaskCompletionSource<JsonNode?>().Task;
        string? why = null;
        b.Closed += (_, said) => why = said;
        a.Start();
        b.Start();
        Task<JsonNode?> waiting = b.Ask("never answered");

        // Past the limit with no new line, in pieces as a network would bring it
        var end = (End)a.Connection.Stream;
        byte[] piece = new byte[1024 * 1024];
        Array.Fill(piece, (byte)'x');
        for (int i = 0; i <= HexLinkSession.MOST_LINE / piece.Length; i++)
            end.Write(piece);

        Assert.True(Eventually(() => b.IsClosed));
        Assert.Contains("longer", why);
        Assert.Equal(HexLinkErrors.CLOSED, await CodeOf(waiting));
        Assert.Equal(HexLinkErrors.CLOSED, await CodeOf(b.Ask("after")));
        Assert.True(Eventually(() => a.IsClosed));
    }

    [Fact]
    public async Task What_would_be_too_long_isnt_sent_and_the_asker_hears_so()
    {
        var (a, b) = Sessions();
        b.Requested = (_, _, _) => Task.FromResult<JsonNode?>(new JsonObject { ["big"] = new string('y', HexLinkSession.MOST_LINE) });
        a.Start();
        b.Start();
        Assert.Equal(HexLinkErrors.TOO_BIG, await CodeOf(a.Ask("big")));
        Assert.Equal(HexLinkErrors.TOO_BIG, await CodeOf(a.Ask("echo", new JsonObject { ["text"] = new string('z', HexLinkSession.MOST_LINE) })));
        Assert.False(a.IsClosed);
        Assert.False(b.IsClosed);
        a.Dispose();
    }

    [Fact]
    public void Pings_keep_a_link_up_and_a_dead_end_is_noticed()
    {
        var (a, b) = HexLinkTestKit.Pair();
        var live = new HexLinkSession(new HexLinkConnection(a, DeviceB, "memory", true, () => HexLinkRights.View)) { PingEvery = TimeSpan.FromMilliseconds(100) };
        var other = new HexLinkSession(new HexLinkConnection(b, DeviceA, "memory", false, () => HexLinkRights.View)) { PingEvery = TimeSpan.FromMilliseconds(100) };
        live.Start();
        other.Start();

        // Well past three pings both are still up, each answering the other's
        Thread.Sleep(800);
        Assert.False(live.IsClosed);
        Assert.False(other.IsClosed);

        // An end that's there but says nothing at all (a frozen machine): its stream is open, nothing comes back
        var (c, d) = HexLinkTestKit.Pair();
        var waiting = new HexLinkSession(new HexLinkConnection(c, DeviceB, "memory", true, () => HexLinkRights.View)) { PingEvery = TimeSpan.FromMilliseconds(100) };
        string? why = null;
        waiting.Closed += (_, said) => why = said;
        waiting.Start();
        Assert.True(Eventually(() => waiting.IsClosed, 5000));
        Assert.Contains("stopped answering", why);
        d.Dispose();
        live.Dispose();
        other.Dispose();
    }

    [Fact]
    public async Task Past_the_rate_requests_are_told_busy_and_the_link_stays()
    {
        var (a, b) = Sessions();
        b.Requested = Echo("b");
        a.Start();
        b.Start();

        // Sent within the same second (most of the time; a second boundary splits them, so a few hundred)
        Task<JsonNode?>[] flood = [.. Enumerable.Range(0, 300).Select(i => a.Ask("echo", new JsonObject { ["text"] = "x" }))];
        int?[] codes = await Task.WhenAll(flood.Select(CodeOf));
        Assert.Contains(codes, code => code == HexLinkErrors.BUSY);
        Assert.Contains(codes, code => code is null);
        Assert.True(codes.Count(code => code is null) <= HexLinkSession.MOST_REQUESTS * 6);
        Assert.False(a.IsClosed);

        await Task.Delay(1100);
        Assert.NotNull(await a.Ask("echo", new JsonObject { ["text"] = "after" }));
        a.Dispose();
    }

    [Fact]
    public async Task Garbage_from_a_peer_never_throws_out_of_the_reader()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit, HexLinkRights.View);
        var end = (End)pair.PeerB.Session.Connection.Stream;

        string[] garbage =
        [
            "not json at all",
            "[1,2,3]",
            "{\"jsonrpc\":\"2.0\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":{\"a\":1},\"method\":\"fleet/get\"}",
            "{\"jsonrpc\":\"2.0\",\"id\":999999,\"result\":{}}",
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":\"nope\"}",
            "{\"jsonrpc\":\"2.0\",\"method\":42}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"fleet/changed\",\"params\":{\"rows\":[{\"id\":\"x\"},{\"id\":1,\"name\":5,\"depth\":1e300,\"cost\":\"lots\"},7,null]}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"fleet/changed\",\"params\":{\"rows\":" + "[" + string.Join(',', Enumerable.Repeat("{}", 5000)) + "]}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"chat/changed\",\"params\":{\"id\":1,\"seq\":\"x\",\"rows\":{\"a\":1}}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"link/rights\",\"params\":{\"granted\":[\"view\",7,\"root\",null]}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"git/chunk\",\"params\":{\"id\":\"nobody\",\"at\":0,\"data\":\"!!\"}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"presence/set\",\"params\":{\"file\":12,\"line\":\"a\",\"selection\":[1]}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"sync/changed\",\"params\":{\"folder\":[],\"changes\":5}}",
            "{\"jsonrpc\":\"2.0\",\"method\":\"fs/changed\",\"params\":{\"path\":{},\"hash\":1}}",
            "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":[\"x\"],\"arguments\":7}}",
            "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"fs/read\",\"params\":{\"path\":null}}",
            "﻿{\"jsonrpc\":\"2.0\",\"method\":\"x\"}",
            new string('[', 5000) + new string(']', 5000)
        ];
        foreach (string line in garbage)
            end.Inject(Encoding.UTF8.GetBytes(line + "\n"));

        // Still up, and still answering
        JsonNode? still = await pair.PeerA.Session.Ask("link/ping").WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS));
        Assert.NotNull(still);
        Assert.False(pair.PeerA.IsClosed);
        Assert.False(pair.PeerB.IsClosed);
        Assert.True(pair.PeerB.Fleet.Count <= 2000);
    }

    [Fact]
    public async Task Hello_carries_the_device_what_it_grants_and_the_workspace()
    {
        using var pair = new LinkedPair(HexLinkRights.View | HexLinkRights.Steer, HexLinkRights.None);
        pair.A.Workspace = pair.A.Workspace with { Repositories = [new HexLinkRepository("hex", "development", new string('1', 40), pair.A.Work)] };
        var (a, b) = HexLinkTestKit.Pair();
        Task<HexLinkPeer> peerB = pair.LinkA.Add(new HexLinkConnection(a, DeviceB, "memory", true, () => pair.BOnA));
        HexLinkPeer peerA = await pair.LinkB.Add(new HexLinkConnection(b, DeviceA, "memory", false, () => pair.AOnB));
        HexLinkPeer fromA = await peerB;

        // B sees A as A said it, what A lets it do, A's folders by name with no path, and its repository
        Assert.Equal(DeviceA.Id, peerA.Device.Id);
        Assert.Equal("Desk", peerA.Device.Name);
        Assert.Equal("windows", peerA.Device.Os);
        Assert.Equal(HexLinkRights.View | HexLinkRights.Steer, peerA.Granted);
        Assert.Equal("Test", peerA.Workspace.Name);
        Assert.Equal("work", Assert.Single(peerA.Workspace.Folders).Name);
        Assert.Equal(string.Empty, peerA.Workspace.Folders[0].Path);
        HexLinkRepository repository = Assert.Single(peerA.Workspace.Repositories);
        Assert.Equal(("hex", "development", new string('1', 40)), (repository.Name, repository.Branch, repository.Head));

        // A was granted nothing on B, so it hears nothing of B's workspace
        Assert.Equal(HexLinkRights.None, fromA.Granted);
        Assert.Empty(fromA.Workspace.Folders);
        Assert.Single(pair.LinkA.Peers);
    }

    [Fact]
    public async Task A_device_claiming_another_id_is_still_the_one_its_connection_proved()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        Assert.Equal(DeviceB.Id, pair.PeerB.Device.Id);
        Assert.Equal(DeviceA.Id, pair.PeerA.Device.Id);
        Assert.Same(pair.PeerB, pair.LinkA.PeerOf(DeviceB.Id));
    }

    // Every method a device can ask, what it takes and what to ask it with
    private static IEnumerable<(string Method, HexLinkRights Needs, JsonObject Params)> Methods() =>
    [
        ("fleet/get", HexLinkRights.View, []),
        ("chat/get", HexLinkRights.View, new JsonObject { ["id"] = 1 }),
        ("chat/watch", HexLinkRights.View, new JsonObject { ["id"] = 1 }),
        ("chat/answer", HexLinkRights.Steer, new JsonObject { ["id"] = 1, ["answer"] = "yes" }),
        ("tools/list", HexLinkRights.View, []),
        ("tools/call", HexLinkRights.View, new JsonObject { ["name"] = "hex_read", ["arguments"] = new JsonObject { ["path"] = "a" } }),
        ("tools/call", HexLinkRights.Steer, new JsonObject { ["name"] = "hex_agent_send", ["arguments"] = new JsonObject { ["id"] = 1, ["text"] = "go" } }),
        ("tools/call", HexLinkRights.Edit, new JsonObject { ["name"] = "hex_write", ["arguments"] = new JsonObject { ["path"] = "a" } }),
        ("fs/list", HexLinkRights.View, new JsonObject { ["path"] = "work" }),
        ("fs/read", HexLinkRights.View, new JsonObject { ["path"] = "work/a.txt" }),
        ("fs/write", HexLinkRights.Edit, new JsonObject { ["path"] = "work/new.txt", ["base"] = null, ["text"] = "new" }),
        ("fs/delete", HexLinkRights.Edit, new JsonObject { ["path"] = "work/gone.txt", ["base"] = "00" }),
        ("fs/watch", HexLinkRights.View, new JsonObject { ["path"] = "work/a.txt" }),
        ("sync/manifest", HexLinkRights.View, new JsonObject { ["folder"] = "work" }),
        ("sync/watch", HexLinkRights.View, new JsonObject { ["folder"] = "work" }),
        ("sync/flush", HexLinkRights.View, new JsonObject { ["folder"] = "work" }),
        ("git/bundle", HexLinkRights.View, new JsonObject { ["repository"] = "none", ["id"] = "x1" }),
        ("patch/offer", HexLinkRights.View, new JsonObject { ["title"] = "t", ["diff"] = "d" })
    ];

    [Fact]
    public async Task Every_method_is_refused_without_its_right_and_allowed_with_it()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.None);
        File.WriteAllText(Path.Combine(pair.A.Work, "a.txt"), "hello");
        pair.A.OnEditor(() => pair.A.Hub.Add(new HexAgentChat(1, "claude_code")));

        foreach (var (method, needs, parameters) in Methods())
        {
            // Nothing granted, and everything but the right it takes
            foreach (HexLinkRights rights in new[] { HexLinkRights.None, (HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit) & ~needs })
            {
                pair.BOnA = rights;
                var refused = await Assert.ThrowsAsync<HexLinkException>(() => pair.PeerA.Session.Ask(method, (JsonObject)parameters.DeepClone()));
                Assert.True(refused.Code == HexLinkErrors.NOT_ALLOWED, $"{method} with {rights} said {refused.Code} {refused.Message}");
                Assert.Equal(HexLinkData.Word(needs), refused.Detail!["needs"]!.GetValue<string>());
            }

            // The right it takes and View (Steer and Edit take View too): not refused, whatever else it says
            pair.BOnA = needs | HexLinkRights.View;
            int? code = await CodeOf(pair.PeerA.Session.Ask(method, (JsonObject)parameters.DeepClone()));
            Assert.True(code != HexLinkErrors.NOT_ALLOWED, $"{method} with {pair.BOnA} was refused");
        }

        // Steer or Edit without View is a mistake in somebody's ticks, refused
        pair.BOnA = HexLinkRights.Steer | HexLinkRights.Edit;
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(pair.PeerA.Answer(1, "yes")));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(pair.PeerA.Write("work/x.txt", null, [1])));
        Assert.Contains(pair.A.Audited, line => line.What == "refused" && line.Who == DeviceB);
    }

    [Fact]
    public async Task Rights_changed_mid_link_hold_from_the_next_request_and_the_device_is_told()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        Assert.NotNull(await pair.PeerA.WatchFleet());

        pair.BOnA = HexLinkRights.None;
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await CodeOf(pair.PeerA.WatchFleet()));
        pair.LinkA.RightsMoved(DeviceB.Id);
        Assert.True(Eventually(() => pair.PeerA.Granted == HexLinkRights.None));

        pair.BOnA = HexLinkRights.View | HexLinkRights.Steer;
        pair.LinkA.RightsMoved(DeviceB.Id);
        Assert.True(Eventually(() => pair.PeerA.Granted == (HexLinkRights.View | HexLinkRights.Steer)));
        Assert.NotNull(await pair.PeerA.WatchFleet());
        Assert.False((await pair.PeerA.Send(1, "go")).IsError);
    }

    [Fact]
    public async Task A_tool_off_the_allow_list_is_refused_whatever_was_granted()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit);
        foreach (string tool in new[] { "hex_terminal_run", "hex_screenshot", "hex_settings_set", "hex_dev_restart", "hex_nothing" })
        {
            var refused = await Assert.ThrowsAsync<HexLinkException>(() => pair.PeerA.Call(tool, new JsonObject { ["command"] = "rm -rf /" }));
            Assert.Equal(HexLinkErrors.NOT_ALLOWED, refused.Code);
        }

        Assert.DoesNotContain(pair.A.LinkTools.Called, call => call.Name is "hex_terminal_run" or "hex_screenshot");

        // tools/list lists only what may be called, nothing off the list
        IReadOnlyList<McpTool> listed = await pair.PeerA.ListTools();
        Assert.Contains(listed, tool => tool.Name == "hex_edit");
        Assert.DoesNotContain(listed, tool => tool.Name is "hex_terminal_run" or "hex_screenshot");
        pair.BOnA = HexLinkRights.View;
        listed = await pair.PeerA.ListTools();
        Assert.Contains(listed, tool => tool.Name == "hex_agent_wait");
        Assert.DoesNotContain(listed, tool => tool.Name is "hex_edit" or "hex_agent_send");
    }

    [Fact]
    public async Task The_calling_chat_a_peer_says_is_stripped_and_arguments_are_checked()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View | HexLinkRights.Steer);
        McpToolResult sent = await pair.PeerA.Call("hex_agent_send", new JsonObject { ["id"] = 3, ["text"] = "go", [HexLinkAllowed.CALLER] = 7 });
        Assert.False(sent.IsError);
        var (name, arguments) = Assert.Single(pair.A.LinkTools.Called);
        Assert.Equal("hex_agent_send", name);
        Assert.Null(arguments[HexLinkAllowed.CALLER]);
        Assert.Equal(3, arguments["id"]!.GetValue<int>());
        Assert.Contains(pair.A.Audited, line => line.What == "steer" && line.Detail.Contains("hex_agent_send", StringComparison.Ordinal));

        // A required argument missing, the tool fails the way MCP's does and never runs
        McpToolResult wrong = await pair.PeerA.Call("hex_read", new JsonObject { ["nonsense"] = 1 });
        Assert.True(wrong.IsError);
        Assert.Contains("can't take those arguments", wrong.AllText());
        Assert.Single(pair.A.LinkTools.Called);
    }

    [Fact]
    public async Task The_steering_helpers_call_the_tools_they_stand_for()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View | HexLinkRights.Steer);
        await pair.PeerA.Send(2, "carry on");
        await pair.PeerA.Stop(2);
        await pair.PeerA.Restart(2, "again");
        await pair.PeerA.Approve(2, 1, true);
        Assert.True(await pair.PeerA.Answer(2, "the second"));
        Assert.Equal(["hex_agent_send", "hex_agent_stop", "hex_agent_restart", "hex_agent_approve"], pair.A.LinkTools.Called.Select(call => call.Name));
        Assert.Equal((2, "the second"), Assert.Single(pair.A.Answers));
        Assert.True(pair.A.LinkTools.Called.Last().Arguments["allow"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_patch_offered_reaches_the_person_and_their_answer_comes_back()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        pair.A.PatchAnswer = "applied";
        Assert.Equal("applied", await pair.PeerA.OfferPatch("Fix the build", "diff --git a/x b/x\n"));
        Assert.Equal(("Fix the build", "diff --git a/x b/x\n"), Assert.Single(pair.A.Offered));
        pair.A.PatchAnswer = "whatever";
        Assert.Equal("discarded", await pair.PeerA.OfferPatch("Again", "d"));
    }

    [Fact]
    public async Task Presence_goes_to_the_peer_and_on_to_a_third_device_that_may_see_but_not_to_one_that_may_not()
    {
        var hub = new LinkServices(DeviceA);
        var link = new HexLink(hub);
        var rights = new Dictionary<string, HexLinkRights> { [DeviceB.Id] = HexLinkRights.View, [DeviceC.Id] = HexLinkRights.View };
        var others = new List<(LinkServices Services, HexLink Link)>();
        HexLinkPeer? hubFromB = null;
        foreach (HexLinkDevice device in new[] { DeviceB, DeviceC })
        {
            var services = new LinkServices(device);
            var theirs = new HexLink(services);
            var (a, b) = HexLinkTestKit.Pair();
            Task<HexLinkPeer> mine = link.Add(new HexLinkConnection(a, device, "memory", false, () => rights[device.Id]));
            HexLinkPeer peer = await theirs.Add(new HexLinkConnection(b, DeviceA, "memory", true, () => HexLinkRights.View));
            await mine;
            others.Add((services, theirs));
            if (device == DeviceB)
                hubFromB = peer;
        }

        // B says where it is: A shows it and passes it on to C
        hubFromB!.SetPresence(new HexLinkPresence("work/a.cs", 4, 2, 4, 9));
        Assert.True(Eventually(() => hub.Presences.Any(each => each.Who == DeviceB && each.Where?.File == "work/a.cs")));
        Assert.True(Eventually(() => others[1].Services.Presences.Any(each => each.Who.Id == DeviceB.Id && each.Where is { File: "work/a.cs", Line: 4, Column: 2, ToColumn: 9 })));
        Assert.Equal("work/a.cs", link.PeerOf(DeviceB.Id)!.Presence?.File);

        // C may no longer see A: it isn't told where B is any more
        rights[DeviceC.Id] = HexLinkRights.None;
        int before = others[1].Services.Presences.Count;
        hubFromB.SetPresence(new HexLinkPresence("work/b.cs", 1, 1));
        Assert.True(Eventually(() => hub.Presences.Any(each => each.Where?.File == "work/b.cs")));
        Thread.Sleep(300);
        Assert.Equal(before, others[1].Services.Presences.Count);

        link.Dispose();
        foreach (var (services, theirs) in others)
        {
            theirs.Dispose();
            services.Dispose();
        }

        hub.Dispose();
    }

    [Fact]
    public async Task A_link_holds_at_most_eight_and_one_per_device()
    {
        using var services = new LinkServices(DeviceA);
        using var link = new HexLink(services);
        var (a, _) = HexLinkTestKit.Pair();
        var (c, _) = HexLinkTestKit.Pair();
        _ = link.Add(new HexLinkConnection(a, DeviceB, "memory", false, () => HexLinkRights.View));
        Assert.Equal(HexLinkErrors.BUSY, await CodeOf(link.Add(new HexLinkConnection(c, DeviceB, "memory", false, () => HexLinkRights.View))));
        for (int i = 0; i < HexLink.MOST - 1; i++)
        {
            var (e, _) = HexLinkTestKit.Pair();
            _ = link.Add(new HexLinkConnection(e, new HexLinkDevice(i.ToString("X64"), $"d{i}"), "memory", false, () => HexLinkRights.View));
        }

        var (f, _) = HexLinkTestKit.Pair();
        Assert.Equal(HexLinkErrors.BUSY, await CodeOf(link.Add(new HexLinkConnection(f, DeviceC, "memory", false, () => HexLinkRights.View))));
    }

    [Fact]
    public void The_allow_list_says_what_each_tool_takes()
    {
        Assert.Equal(HexLinkRights.View, HexLinkAllowed.Needs("hex_read"));
        Assert.Equal(HexLinkRights.View, HexLinkAllowed.Needs("hex_agent_wait"));
        Assert.Equal(HexLinkRights.View, HexLinkAllowed.Needs("hex_agent_inbox"));
        Assert.Equal(HexLinkRights.Steer, HexLinkAllowed.Needs("hex_agent_spawn"));
        Assert.Equal(HexLinkRights.Edit, HexLinkAllowed.Needs("hex_rename"));
        Assert.Null(HexLinkAllowed.Needs("hex_terminal_run"));
        Assert.Null(HexLinkAllowed.Needs("HEX_READ"));
        Assert.False(HexLinkAllowed.May(HexLinkRights.Steer, "hex_agent_send"));
        Assert.True(HexLinkAllowed.May(HexLinkRights.View | HexLinkRights.Steer, "hex_agent_send"));
        Assert.False(HexLinkAllowed.May(HexLinkRights.View | HexLinkRights.Steer, "hex_edit"));
        Assert.False(HexLinkAllowed.May(HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit, "hex_debug_start"));
    }
}
