using System.Numerics;

using Hex.Shell;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Hex Link's UI driven by the pointer (docs/hex-link-plan.md, phase 3), against a second Hex started as the link part
// starts one (B, the link-peer part with HEX_LINK_TEST_UI set, which keeps a chat of its own running and says each Steer
// request it hears). Link's window opened, pairing started from it, the pairing dialog showing the code with nothing
// ticked, View and Steer ticked and Pair pressed; the device then listed as connected with its switches; Agentic's
// fleet showing a section of B's with its chat and the badge of B; picking it showing its rows under a banner saying
// where it runs; a message typed and sent reaching B; Runs on listing B while it lets this one steer and not after B
// takes Steer away, when the box is shut with the reason; and Forget taking the section away. A picture at each stage
// with HEX_SELFTEST_SHOTS set. Explicit-only, as the link part is.
internal sealed partial class DawnScene
{
    // What's typed in the box and sent to B's chat, and the environment that has B say each Steer request it hears
    private const string LINK_UI_SENT = "Hello from A over the link", LINK_TEST_UI = "HEX_LINK_TEST_UI";

    // B's side: what the other end was last said to be called
    private string? linkTestOtherName;

    /// <summary>B's side: the Steer requests its audit has since the last said, each said on a line of its own.</summary>
    private void LinkTestSaySteered(ref DateTime seen)
    {
        // And what the other end calls itself, each time it changes
        if (LinkPeers.FirstOrDefault() is { } other && other.Device.Name != linkTestOtherName)
        {
            linkTestOtherName = other.Device.Name;
            Console.WriteLine($"[Hex self-test] link-peer: other is called {other.Device.Name}");
        }

        foreach (HexLinkAuditEntry entry in LinkAuditLatest(40))
        {
            if (entry.Time <= seen || entry.Event != "steer")
                continue;
            seen = entry.Time;
            Console.WriteLine($"[Hex self-test] link-peer: steered {entry.Detail}");
        }
    }

    private void TestLinkUi(HexPointerScript test)
    {
        string home = LinkTestFolder("hex-link-ui-a-");
        string peerHome = LinkTestFolder("hex-link-ui-b-");
        HexLinkPeer? peer = null;
        string? peerId = null;
        int peerChat = 0;

        void Shot(string name) => test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, name));
        });

        Vector2 Centre(UIComponent? component) => component?.Bounds.Center ?? Vector2.Zero;
        Button? WindowButton(string label) => linkWindow is null ? null : Descendants(linkWindow).OfType<Button>().FirstOrDefault(button => button.Label == label && button.IsShownInHierarchy);
        ToggleButton? DialogRight(string name) => name switch { "view" => linkPairView, "steer" => linkPairSteer, _ => linkPairEdit };
        AgentCardButton? RemoteCard(UIComponent rows) => Descendants(rows).OfType<AgentCardButton>().FirstOrDefault(card => LinkRemoteOf(card.Chat) is { } remote && remote.DeviceName == LINK_PEER_NAME && card.Chat.Shown == LINK_PEER_CHAT);

        // A up at a free port in a folder of its own, its Link window opened
        test.Run(() =>
        {
            linkTestPeerSaid.Clear();
            StopLink();
            if (!StartLink(port: 0, home: home, discover: false))
                return;
            linkByTest = true;
            ShowFace(HexFace.Dawn);
            ShowLinkWindow();
        });
        test.Settle(0.4f);
        test.Check("link-ui: Link's window opens, this device's name, its fingerprint and Link on", () =>
            linkWindow is not null && LinkOn && LinkDevice is { } me && Descendants(linkWindow).OfType<Label>().Any(label => label.Text.Contains(me.Fingerprint, StringComparison.Ordinal)));
        test.Check("link-ui: the window says how the key is kept (a self test's is the file alone, said as a warning)", () =>
            Descendants(linkWindow!).OfType<Label>().Any(label => label.Text.Contains(Said("link.key_file"), StringComparison.Ordinal)));
        Shot("link_ui_window_empty.png");

        // Pairing started from the window, its countdown and addresses shown, B started to dial in
        test.Click(() => Centre(WindowButton(Said("link.pair_button"))));
        test.Settle(0.3f);
        test.Check("link-ui: Pair a Device opens pairing, the window counts it down", () =>
            LinkPairingUntil is not null && linkPairingCountdown is { Text.Length: > 0 } && WindowButton(Said("link.pair_stop")) is not null);
        Shot("link_ui_pairing_open.png");
        test.Run(() => LinkStartPeer(peerHome, $"127.0.0.1:{LinkPort}", ui: true));

        // The pairing dialog, the code large and grouped, nothing ticked, Pair waiting for View
        test.WaitFor(() => linkPairingDialog is { IsOpen: true } || linkTestPeer is { HasExited: true }, 90.0f);
        test.Settle(0.4f);
        test.Check("link-ui: the pairing dialog shows B, its code grouped three and three, nothing ticked and Pair greyed", () =>
            linkPairingDialog is { IsOpen: true } dialog && LinkPairingPending is { } asked && asked.Other.Name == LINK_PEER_NAME
            && Descendants(dialog).OfType<Label>().Any(label => label.Text.Contains(LinkCodeGrouped(asked.Code), StringComparison.Ordinal))
            && LinkPairTicked == HexLinkRights.None && !dialog.Buttons[0].Enabled);
        test.Check("link-ui: the dialog says Steer is as much as sitting at this machine", () =>
            Descendants(linkPairingDialog!).OfType<Label>().Any(label => label.Text.Contains(Said("link.right_means_steer"), StringComparison.Ordinal)));
        Shot("link_ui_pairing_dialog.png");
        test.Click(() => Centre(DialogRight("steer")));
        test.Wait();
        test.Check("link-ui: ticking Steer ticks View with it, and Pair can be pressed", () =>
            LinkPairTicked == (HexLinkRights.View | HexLinkRights.Steer) && linkPairingDialog?.Buttons[0].Enabled == true);
        test.Click(() => Centre(DialogRight("view")));
        test.Wait();
        test.Check("link-ui: taking View away takes Steer with it and greys Pair", () => LinkPairTicked == HexLinkRights.None && linkPairingDialog?.Buttons[0].Enabled == false);
        test.Click(() => Centre(DialogRight("view")));
        test.Click(() => Centre(DialogRight("steer")));
        test.Wait();
        Shot("link_ui_pairing_ticked.png");
        test.Click(() => Centre(linkPairingDialog?.Buttons[0]));
        test.WaitFor(() => LinkPeers.Count == 1 && LinkPeers[0].Fleet.Any(row => row.Name == LINK_PEER_CHAT), 40.0f);
        test.Check("link-ui: paired, B granted View and Steer here and nothing more", () =>
        {
            peer = LinkPeers.FirstOrDefault();
            peerId = peer?.Device.Id;
            peerChat = peer?.Fleet.FirstOrDefault(row => row.Name == LINK_PEER_CHAT)?.Id ?? 0;
            return peer is not null && LinkKnown.FirstOrDefault(known => known.Device.Id == peerId)?.Rights == (HexLinkRights.View | HexLinkRights.Steer);
        });

        // The window lists B as connected, its switches as granted and what B lets this machine do
        test.Settle(0.5f);
        test.Check("link-ui: the window lists B connected, its View and Steer switches on and Edit off, and what B lets this machine do", () =>
        {
            if (linkWindowDevices is null)
                return false;
            var labels = Descendants(linkWindowDevices).OfType<Label>().Select(label => label.Text).ToList();
            var toggles = Descendants(linkWindowDevices).OfType<ToggleButton>().ToList();
            return labels.Any(text => text.Contains(LINK_PEER_NAME, StringComparison.Ordinal))
                && Descendants(linkWindowDevices).OfType<Badge>().Any(badge => badge.Text == Said("link.device_connected"))
                && toggles.Count == 3 && toggles[0].State && toggles[1].State && !toggles[2].State
                && Descendants(linkWindowDevices).OfType<Badge>().Count(badge => badge.Text == Said("link.right_title_steer")) == 1;
        });
        Shot("link_ui_window_device.png");

        // This device's name: the machine's when the box is empty, typed and kept, heard by B at once, then back
        string machine = HexLinkNames.Clean(System.Environment.MachineName) ?? string.Empty;
        test.Check("link-ui: the name box is link_name, empty, with the machine's name in it greyed and said as what others see", () =>
            linkNameBox is { Text.Length: 0 } box && box.Placeholder == machine && settings.LinkName.Length == 0
            && linkNameSeen?.Text.Contains(machine, StringComparison.Ordinal) == true);
        test.Run(() => linkNameBox!.Text = "  A‮ renamed  ");
        test.Click(() => Centre(WindowButton(Said("link.name_keep"))));
        test.WaitFor(() => LinkTestPeerSaid("other is called A renamed"), 15.0f);
        test.Check("link-ui: a name kept is cleaned into link_name, and B hears it at once over the link", () =>
            settings.LinkName == "A renamed" && LinkDevice?.Name == "A renamed" && LinkTestPeerSaid("other is called A renamed"));
        Shot("link_ui_renamed_here.png");
        test.Click(() => Centre(WindowButton(Said("link.name_reset"))));
        // B heard the machine's name as the link came up, and again now
        bool HeardMachineAgain() => linkTestPeerSaid.Count(line => line.EndsWith($"link-peer: other is called {machine}", StringComparison.Ordinal)) >= 2;
        test.WaitFor(HeardMachineAgain, 15.0f);
        test.Check("link-ui: Use the Machine's Name empties link_name and B hears the machine's name", () =>
            settings.LinkName.Length == 0 && LinkDevice?.Name == machine && HeardMachineAgain());

        // B called something of this machine's own, which wins everywhere it's shown, and undone
        const string LOCAL = "Bee box";
        test.Run(() => LinkRename(peerId!, LOCAL));
        test.Settle(0.4f);
        test.Check("link-ui: B named here shows by that name in the window, saying what it calls itself, with Use Its Own Name", () =>
            linkWindowDevices is { } devices && Descendants(devices).OfType<Label>().Any(label => label.Text.Contains(LOCAL, StringComparison.Ordinal))
            && Descendants(devices).OfType<Label>().Any(label => label.Text.Contains(Said("link.device_calls_itself", ("name", LINK_PEER_NAME)), StringComparison.Ordinal))
            && WindowButton(Said("link.device_own_name")) is not null);
        test.Run(() =>
        {
            linkWindow?.Close();
            ShowFace(HexFace.Noon);
            noonWait = 0.0f;
        });
        test.WaitFor(() => Descendants(noonFleetRows).OfType<Badge>().Any(badge => badge.Text == LOCAL), 15.0f);
        test.Check("link-ui: the fleet's section and B's badge say the name given here", () =>
            Descendants(noonFleetRows).OfType<Badge>().Any(badge => badge.Text == LOCAL)
            && Descendants(noonFleetRows).OfType<Label>().Any(label => label.Text.Contains(LOCAL, StringComparison.Ordinal)));
        Shot("link_ui_named_here.png");
        // The window that went is gone before it's opened again
        test.Settle(0.6f);
        test.Run(ShowLinkWindow);
        test.Settle(0.4f);
        test.Click(() => Centre(WindowButton(Said("link.device_own_name"))));
        test.Settle(0.3f);
        test.Check("link-ui: Use Its Own Name gives B its own name back", () => LinkKnown.FirstOrDefault(known => known.Device.Id == peerId) is { Named: null, Shown: LINK_PEER_NAME });

        // Agentic's fleet with a section of B's, its chat and B's badge
        test.Run(() =>
        {
            linkWindow?.Close();
            ShowFace(HexFace.Noon);
            noonWait = 0.0f;
        });
        test.WaitFor(() => RemoteCard(noonFleetRows) is not null, 15.0f);
        test.Settle(0.5f);
        test.Check("link-ui: the fleet has a section of B's with its chat as a card", () => RemoteCard(noonFleetRows) is not null
            && Descendants(noonFleetRows).OfType<Label>().Any(label => label.Text.Contains(LINK_PEER_NAME, StringComparison.Ordinal)));
        test.Check("link-ui: B's card carries B's badge", () => RemoteCard(noonFleetRows)?.Parent is { } card && Descendants(card).OfType<Badge>().Any(badge => badge.Text == LINK_PEER_NAME));
        Shot("link_ui_fleet.png");

        // Picking it shows B's chat, its rows, under the banner saying where it runs
        test.Click(() => Centre(RemoteCard(noonFleetRows)));
        test.WaitFor(() => linkRemoteMirror is not null && agentChat == linkRemoteMirror, 20.0f);
        test.Settle(0.5f);
        test.Check("link-ui: picking B's card shows its chat mirrored, its rows as B has them", () =>
            linkRemoteMirror is { } mirror && agentChat == mirror && LinkTestRows(mirror) == LinkTestPeerLine("rows ") && !agentChats.Contains(mirror));
        test.Check("link-ui: the banner over the box says the chat runs on B", () =>
            linkBanner is { Visible: true } banner && Descendants(banner).OfType<Label>().Any(label => label.Tooltip.Contains(Said("link.chat_runs_on", ("device", LINK_PEER_NAME)), StringComparison.Ordinal)));
        Shot("link_ui_remote_chat.png");

        // Typed in the box and sent, it reaches B
        test.Run(() =>
        {
            agentsInput.Focus();
            agentsInput.Text = LINK_UI_SENT;
        });
        test.Settle(0.3f);
        test.Check("link-ui: the box can send to B's chat while B lets this one steer", () => agentsSend.Enabled && !agentsInput.ReadOnly);
        test.Click(() => Centre(agentsSend));
        test.WaitFor(() => LinkTestPeerSaid("steered tool hex_agent_send"), 20.0f);
        test.Check("link-ui: what was sent reached B as hex_agent_send to its chat", () =>
            LinkTestPeerSaid("steered tool hex_agent_send") && LinkTestPeerLine("steered tool hex_agent_send").Contains(LINK_UI_SENT, StringComparison.Ordinal));
        test.Check("link-ui: the box emptied as it sent", () => agentsInput.Text.Length == 0);

        // Runs on lists B while it lets this one steer
        test.Run(() =>
        {
            NewAgentChat();
            agentsOptionsDrawn = string.Empty;
            agentsWait = 0.0f;
        });
        test.Settle(0.4f);
        test.Check("link-ui: Runs on is over the box of a new chat and lists B", () =>
            linkRunsOnPicker is { IsShownInHierarchy: true } && LinkRunsOnDevices().Any(each => each.Device.Name == LINK_PEER_NAME)
            && LinkRunsOnMenu().Items.Any(item => item.Label == LINK_PEER_NAME));
        Shot("link_ui_runs_on.png");

        // B takes Steer away: the box under B's chat is shut with the reason, Runs on gone
        test.Run(() => _ = peer?.Write($"{LINK_PEER_FOLDER}/{LINK_FLAG_VIEW}", null, [1]));
        test.WaitFor(() => peer?.Granted == HexLinkRights.View, 20.0f);
        test.Run(() =>
        {
            agentsOptionsDrawn = string.Empty;
            agentsWait = 0.0f;
        });
        test.Settle(0.4f);
        test.Check("link-ui: with Steer taken away on B, Runs on is gone from a new chat's box", () =>
            LinkRunsOnDevices().Count == 0 && (linkRunsOnPicker is null || !linkRunsOnPicker.IsShownInHierarchy));
        test.Click(() => Centre(RemoteCard(noonFleetRows)));
        test.WaitFor(() => LinkRemoteOf(agentChat) is not null, 10.0f);
        test.Settle(0.5f);
        test.Check("link-ui: with Steer taken away, B's chat's box is shut and says B lets this machine watch, not steer", () =>
            agentsInput.ReadOnly && !agentsSend.Enabled && agentsPlaceholder.Text == Said("link.chat_watch_only", ("device", LINK_PEER_NAME))
            && !agentsStop.Enabled && agentsWaitStrip is not { Visible: true });
        test.Check("link-ui: B's section's chips say View alone now", () =>
            !Descendants(noonFleetRows).OfType<Badge>().Any(badge => badge.Text == Said("link.right_title_steer"))
            && Descendants(noonFleetRows).OfType<Badge>().Any(badge => badge.Text == Said("link.right_title_view")));
        Shot("link_ui_watch_only.png");

        // Forgotten from the window, asked once, B's section goes
        test.Run(ShowLinkWindow);
        test.Settle(0.4f);
        test.Click(() => Centre(WindowButton(Said("link.device_forget"))));
        test.Settle(0.3f);
        test.Click(() => Centre(chrome.Root.Children.OfType<UIDialog>().LastOrDefault(dialog => dialog.IsOpen)?.Buttons.FirstOrDefault(button => button.Label == Said("link.forget_go"))));
        test.WaitFor(() => LinkPeers.Count == 0, 10.0f);
        test.Run(() =>
        {
            linkWindow?.Close();
            noonWait = 0.0f;
        });
        test.Settle(0.6f);
        test.Check("link-ui: Forget cuts B and takes its section out of the fleet", () =>
            LinkPeers.Count == 0 && LinkKnown.All(known => known.Device.Id != peerId) && RemoteCard(noonFleetRows) is null
            && !Descendants(noonFleetRows).OfType<Badge>().Any(badge => badge.Text == LINK_PEER_NAME));
        test.Check("link-ui: B's chat on screen is let go of, the box open again", () => LinkRemoteOf(agentChat) is null && !agentsInput.ReadOnly);
        Shot("link_ui_forgotten.png");
        test.WaitFor(() => linkTestPeer is null or { HasExited: true }, 60.0f);
        test.Check("link-ui: B left by itself once forgotten", () => linkTestPeer is { HasExited: true, ExitCode: 0 });
        test.Run(() => ShowFace(HexFace.Dawn));
    }
}
