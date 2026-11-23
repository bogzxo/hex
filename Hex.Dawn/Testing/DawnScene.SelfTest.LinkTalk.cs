using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using Horizon.Input;
using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestLinkTalk(HexPointerScript test)
    {
        string home = LinkTestFolder("hex-link-talk-", test);
        const string otherName = "Chat laptop", later = "[b]One more message.[/b]";
        const string first = "Hello from the laptop.\nThis second line should wrap inside the panel while keeping the editor beside it.";
        const string reply = "Reply from the editor.\nWith a second line.";
        HexLinkIdentity? identity = null;
        HexLinkTransport? transport = null;
        Task<HexLinkSession>? connected = null;
        Task<bool>? sent = null;
        var received = new ConcurrentQueue<HexLinkTalk>();

        test.Run(() =>
        {
            StopLink();
            ShowFace(HexFace.Dawn);
            if (!StartLink(port: 0, home: Path.Combine(home, "editor"), loopbackOnly: true) || LinkDevice is not { } here)
                return;
            linkByTest = true;
            identity = HexLinkIdentity.LoadOrCreate(Path.Combine(home, "peer"), otherName, fileOnly: true);
            var trust = new HexLinkTrust(Path.Combine(home, "peer"));
            trust.Add(here, HexLinkRights.View);
            linkTrust!.Add(identity.Device, HexLinkRights.View);
            transport = new HexLinkTransport(identity, trust);
            connected = Connect(transport, identity.Device, here, LinkPort);
        });
        test.WaitFor(() => connected?.IsFaulted == true || (LinkPeers.Count == 1 && !center.Bounds.IsEmpty &&
            (!RoomForDock() || linkTalkPanel is { IsShownInHierarchy: true, Bounds.IsEmpty: false })), 15);
        test.Check("link-talk: the first connection opens the dock where there is room and leaves a narrow editor clear", () =>
            connected?.IsCompletedSuccessfully == true && linkTalkPanel is { } pane &&
            (RoomForDock() ? pane.IsShownInHierarchy && linkTalkDock == pane.Parent : !pane.IsShownInHierarchy));
        test.Run(() =>
        {
            if (!RoomForDock())
                ToggleLinkTalk();
        });
        test.WaitFor(() => linkTalkPanel is { IsShownInHierarchy: true, Bounds.IsEmpty: false }, 5);
        test.Check("link-talk: the panel docks beside a wide editor or opens over the right edge of a narrow editor", () =>
            linkTalkPanel is { IsShownInHierarchy: true } pane && (RoomForDock()
                ? pane.Bounds.Min.X >= center.Bounds.Max.X
                : pane.Parent == chrome.Root && MathF.Abs(pane.Bounds.Max.X - center.Bounds.Max.X) < 1));

        test.Run(() => SayThere(first));
        test.WaitFor(() => MessageLabel(0, first) is { LineCount: > 2 }, 8);
        test.Check("link-talk: the other device's name and wrapped multiline message are in the panel", () =>
            sent is { IsCompletedSuccessfully: true, Result: true } && MessageLabel(0, first) is { LineCount: > 2 } &&
            linkTalkRows!.ComponentOf(0)!.Children.OfType<Label>().Any(label => label.Text.Contains(otherName, StringComparison.Ordinal)));

        test.Run(() =>
        {
            if (linkTalkPanel?.Children.OfType<Button>().FirstOrDefault(button => button.Icon == "close") is { } close)
                close.OnPressed?.Invoke();
            SayThere(later);
        });
        test.WaitFor(() => linkTalkUnread == 1, 8);
        test.Check("link-talk: a message received with the panel closed is counted in the status bar", () =>
            linkTalkPanel?.Visible == false && linkTalkUnread == 1 && linkTalkStatus?.Text.Contains(Said("link.talk.unread", ("count", "1")), StringComparison.Ordinal) == true);
        test.Run(() => LinkPaletteCommands().Single(command => command.Text == Said("link.talk.command")).Picked());
        test.WaitFor(() => RowShows(1, LinkTalkLiteral(later)), 8);
        test.Check("link-talk: the palette opens the same transcript, clears unread and neutralises the message's markup", () =>
            linkTalkPanel?.Visible == true && linkTalkUnread == 0 && RowShows(1, LinkTalkLiteral(later)));

        test.Run(() =>
        {
            if (linkTalkDraft is not { } draft)
                return;
            draft.Text = "A draft";
            draft.Focus();
            Engine.Input.Keyboard.Tap([Key.ShiftLeft, Key.Enter]);
        });
        test.WaitFor(() => linkTalkDraft?.Text.Contains('\n') == true, 3);
        test.Check("link-talk: Shift+Enter adds a new line in the draft", () => linkTalkDraft?.Text.Contains('\n') == true && received.IsEmpty);
        test.Run(() =>
        {
            if (linkTalkDraft is not { } draft)
                return;
            draft.Text = reply;
            draft.Focus();
            UIKeyboard.Press(Key.Enter, false, false, false, false);
        });
        test.WaitFor(() => received.Any(message => message.Text == reply) && RowShows(2, reply), 8);
        test.Check("link-talk: Enter sends the draft to the peer and marks its local copy", () =>
            received.TryPeek(out HexLinkTalk? said) && said.Text == reply && linkTalkDraft?.Text == string.Empty && RowShows(2, reply) &&
            linkTalkRows!.ComponentOf(2)!.Children.OfType<Label>().Any(label => label.Text.Contains(
                Said("link.talk.you", ("device", LinkDevice?.Name ?? string.Empty)), StringComparison.Ordinal)));

        test.Run(() =>
        {
            StopLink();
            if (connected?.IsCompletedSuccessfully == true)
                connected.Result.Dispose();
            transport?.Dispose();
            identity?.Dispose();
        });
        test.Check("link-talk: stopping Link clears the transcript and hides chat", () =>
            linkTalkMessages.Count == 0 && linkTalkPanel?.Visible == false && linkTalkUnread == 0);

        bool RoomForDock() => ColumnWidths(chrome.Root.Bounds.Width).Center >= LINK_TALK_EDITOR_ROOM + LINK_TALK_MIN_WIDTH + LINK_TALK_GAP;

        Label? MessageLabel(int row, string text) => linkTalkRows?.ComponentOf(row)?.Children.OfType<Label>()
            .FirstOrDefault(label => label.IsShownInHierarchy && label.Text == text);
        bool RowShows(int row, string text) => MessageLabel(row, text) is not null;

        void SayThere(string text)
        {
            if (connected?.IsCompletedSuccessfully != true || identity is null)
                return;
            var message = new HexLinkTalk(Guid.NewGuid(), identity.Device, text, DateTimeOffset.UtcNow);
            sent = connected.Result.TellAsync(HexLinkTalk.METHOD, message.ToJson()).AsTask();
        }

        async Task<HexLinkSession> Connect(HexLinkTransport dialler, HexLinkDevice remote, HexLinkDevice here, int port)
        {
            HexLinkConnection connection = await dialler.Dial("127.0.0.1", port, here.Id, CancellationToken.None);
            return new HexLinkSession(connection)
            {
                Requested = (method, _, _) => Task.FromResult<JsonNode?>(method == "link/hello"
                    ? new JsonObject
                    {
                        ["protocol"] = HexLinkHost.PROTOCOL, ["device"] = HexLinkData.DeviceJson(remote),
                        ["granted"] = HexLinkData.Words(HexLinkRights.View), ["workspace"] = new JsonObject()
                    }
                    : new JsonObject()),
                Told = (method, parameters) =>
                {
                    if (method == HexLinkTalk.METHOD && HexLinkTalk.Of(parameters, here) is { } message)
                        received.Enqueue(message);
                }
            }.Start();
        }
    }
}
