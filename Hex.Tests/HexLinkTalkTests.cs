using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

public sealed class HexLinkTalkTests
{
    [Fact]
    public async Task View_is_enough_to_send_chat_and_the_author_sees_one_local_copy()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        var ours = new ConcurrentQueue<HexLinkTalk>();
        var theirs = new TaskCompletionSource<HexLinkTalk>(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.LinkB.TalkHeard += ours.Enqueue;
        pair.LinkA.TalkHeard += message => theirs.TrySetResult(message);

        const string text = "First line\nSecond line of chat.";
        Assert.Equal(HexLinkTalkResult.Sent, pair.LinkB.SayTalk(text));
        HexLinkTalk message = await theirs.Task.WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS));
        HexLinkTalk local = Assert.Single(ours);
        Assert.Equal(DeviceB, message.Sender);
        Assert.Equal(text, message.Text);
        Assert.Equal(local.Id, message.Id);
        Assert.Equal(local.SentAt.ToUnixTimeMilliseconds(), message.SentAt.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task A_hub_relays_each_id_once_to_viewers_and_sends_no_echo()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        using var servicesC = new LinkServices(DeviceC);
        using var servicesD = new LinkServices(new HexLinkDevice(new string('D', 64), "Guest"));
        using var linkC = new HexLink(servicesC);
        using var linkD = new HexLink(servicesD);
        await Attach(pair.LinkA, linkC, servicesC.Device, HexLinkRights.View);
        await Attach(pair.LinkA, linkD, servicesD.Device, HexLinkRights.None);
        var atHub = new ConcurrentQueue<HexLinkTalk>();
        var atC = new ConcurrentQueue<HexLinkTalk>();
        var atD = new ConcurrentQueue<HexLinkTalk>();
        var echo = new ConcurrentQueue<JsonObject>();
        var told = pair.PeerA.Session.ToldAsync!;
        pair.PeerA.Session.ToldAsync = (method, data) =>
        {
            if (method == HexLinkTalk.METHOD)
                echo.Enqueue(data);
            return told(method, data);
        };
        pair.LinkA.TalkHeard += atHub.Enqueue;
        linkC.TalkHeard += atC.Enqueue;
        linkD.TalkHeard += atD.Enqueue;

        var message = new HexLinkTalk(Guid.NewGuid(), DeviceB, "Coming from the laptop.", DateTimeOffset.UtcNow);
        var after = message with { Id = Guid.NewGuid(), Text = "After the duplicate." };
        var forged = message with { Sender = DeviceC, Id = Guid.NewGuid(), Text = "Forged sender." };
        Assert.True(await pair.PeerA.Session.TellAsync(HexLinkTalk.METHOD, forged.ToJson(relayed: true)));
        Assert.True(await pair.PeerA.Session.TellAsync(HexLinkTalk.METHOD, message.ToJson()));
        Assert.True(await pair.PeerA.Session.TellAsync(HexLinkTalk.METHOD, (message with { Text = "Same id." }).ToJson()));
        Assert.True(await pair.PeerA.Session.TellAsync(HexLinkTalk.METHOD, after.ToJson()));
        Assert.True(Eventually(() => atC.Any(received => received.Id == after.Id)));

        Assert.Equal([message.Text, after.Text], atHub.Select(received => received.Text));
        Assert.Equal([message.Text, after.Text], atC.Select(received => received.Text));
        Assert.All(atC, received => Assert.Equal((DeviceB.Id, DeviceB.Name), (received.Sender.Id, received.Sender.Name)));
        Assert.Empty(atD);
        Assert.Empty(echo);
    }

    [Fact]
    public async Task The_receiver_checks_each_field_and_enforces_view_after_the_grant_changes()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        var heard = new ConcurrentQueue<HexLinkTalk>();
        pair.LinkA.TalkHeard += heard.Enqueue;
        var message = new HexLinkTalk(Guid.NewGuid(), DeviceB, new string('x', HexLinkTalk.MOST_TEXT), DateTimeOffset.UtcNow);
        (string Field, JsonNode? Value)[] bad =
        [
            ("id", "not a guid"),
            ("id", message.Id.ToString("N") + "f"),
            ("text", " \n\t"),
            ("text", new string('x', HexLinkTalk.MOST_TEXT + 1)),
            ("text", "\u001b badly formed"),
            ("text", new JsonObject()),
            ("sentAt", -1L),
            ("sentAt", long.MaxValue),
            ("sentAt", "yesterday"),
            ("device", null)
        ];
        foreach (var (field, value) in bad)
        {
            JsonObject invalid = message.ToJson();
            invalid[field] = value;
            Assert.True(await pair.PeerA.Session.TellAsync(HexLinkTalk.METHOD, invalid));
        }
        Assert.True(await pair.PeerA.Session.TellAsync(HexLinkTalk.METHOD, message.ToJson()));
        Assert.True(Eventually(() => heard.Count > 0));
        Assert.Equal(message.Text, Assert.Single(heard).Text);

        pair.BOnA = HexLinkRights.Steer;
        pair.LinkA.RightsMoved(DeviceB.Id);
        Assert.True(Eventually(() => pair.PeerA.Granted == HexLinkRights.Steer));
        Assert.Equal(HexLinkTalkResult.NoView, pair.LinkB.SayTalk("View taken away."));
        var denied = message with { Id = Guid.NewGuid(), Text = "Tried without View." };
        Assert.True(await pair.PeerA.Session.TellAsync(HexLinkTalk.METHOD, denied.ToJson()));

        // Wait for a later notification handled on the same dispatch task before inspecting the queue.
        await DispatchThrough(pair.PeerA, pair.PeerB);
        Assert.Single(heard);
        Assert.Equal(HexLinkTalkResult.Invalid, pair.LinkB.SayTalk(new string('x', HexLinkTalk.MOST_TEXT + 1)));
    }

    [Fact]
    public async Task The_receiver_limits_a_burst_from_one_connection()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View);
        var heard = new ConcurrentQueue<HexLinkTalk>();
        pair.LinkA.TalkHeard += heard.Enqueue;
        var message = new HexLinkTalk(Guid.NewGuid(), DeviceB, "One in a burst.", DateTimeOffset.UtcNow);
        for (int i = 0; i < 64; i++)
        {
            var each = message with { Id = Guid.NewGuid() };
            Assert.True(await pair.PeerA.Session.TellAsync(HexLinkTalk.METHOD, each.ToJson()));
        }
        await DispatchThrough(pair.PeerA, pair.PeerB);

        Assert.InRange(heard.Count, 1, 63);
    }

    private static async Task Attach(HexLink hub, HexLink other, HexLinkDevice device, HexLinkRights rights)
    {
        var (a, b) = Pair();
        await Task.WhenAll(hub.Add(new HexLinkConnection(a, device, "memory", false, () => rights)),
            other.Add(new HexLinkConnection(b, DeviceA, "memory", true, () => HexLinkRights.View)))
            .WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS));
    }

    private static async Task DispatchThrough(HexLinkPeer sender, HexLinkPeer receiver)
    {
        const string marker = "test/talk-marker";
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var took = receiver.Session.ToldAsync!;
        receiver.Session.ToldAsync = (method, data) =>
        {
            if (method == marker)
                arrived.TrySetResult();
            return took(method, data);
        };
        Assert.True(await sender.Session.TellAsync(marker));
        await arrived.Task.WaitAsync(TimeSpan.FromMilliseconds(WAIT_MS));
    }
}
