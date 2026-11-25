using Hex.Core;

using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

/// <summary>
/// What a person types in a remote chat's box over Hex Link: it reaches the device as hex_agent_send flagged from_person
/// (an agent's send carries nothing), and what the device records for it, a user row, shows once in the mirror.
/// </summary>
public sealed class HexLinkPersonSendTests
{
    [Fact]
    public async Task A_persons_message_sent_over_the_link_is_flagged_and_stands_once_in_host_chat_and_mirror()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View | HexLinkRights.Steer);
        var chat = new HexAgentChat(6, "claude_code");
        pair.A.OnEditor(() =>
        {
            pair.A.Hub.Add(chat);
            chat.Ask("first");
        });
        await pair.PeerA.WatchFleet();
        HexAgentChat mirror = await pair.PeerA.OpenChat(chat.Id);

        McpToolResult first = await pair.PeerA.Send(chat.Id, "typed by a person", person: true);
        McpToolResult second = await pair.PeerA.Send(chat.Id, "from an agent");
        var calls = pair.A.LinkTools.Called.Where(call => call.Name == "hex_agent_send").ToArray();
        Assert.True(calls.Length == 2, $"{calls.Length} calls, {first.IsError}/{second.IsError} {string.Join("|", first.Content.Select(c => c.ToString()))}: {string.Join(",", pair.A.LinkTools.Called.Select(c => c.Name))}");
        Assert.True(calls[0].Arguments["from_person"]?.GetValue<bool>());
        Assert.Null(calls[1].Arguments["from_person"]);

        // What the device does for it, as the handler does
        pair.A.OnEditor(() => chat.SayAsPerson("typed by a person"));
        pair.LinkA.ChatMoved(chat.Id);
        Assert.True(Eventually(() =>
        {
            pair.PeerA.Pump();
            return mirror.Entries.Any(entry => entry.Kind == HexAgentEntryKind.User && entry.Text == "typed by a person");
        }));

        HexAgentEntry[] host;
        lock (pair.A.Editor)
            host = [.. chat.Entries.Where(entry => entry.Text == "typed by a person")];
        HexAgentEntry hosted = Assert.Single(host);
        Assert.Equal(HexAgentEntryKind.User, hosted.Kind);
        Assert.False(hosted.Queued);
        pair.PeerA.Pump();
        Assert.Single(mirror.Entries, entry => entry.Text == "typed by a person");
    }
}
