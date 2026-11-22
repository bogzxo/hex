using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text.Json.Nodes;

using Hex.Shell;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

// Hex Link's UI driven by the pointer (docs/hex-link-plan.md, phase 3), against a second Hex started as the link part
// starts one (B, the link-peer part with HEX_LINK_TEST_UI set, which keeps a chat of its own running and says each Steer
// request it hears). Link's window opened, pairing started from it, the pairing dialog showing the code with nothing
// ticked, View and Steer ticked and Pair pressed; the device then listed as connected with its switches; Agentic's
// fleet showing a section of B's with its chat and the badge of B; picking it showing its rows under a banner saying
// where it runs; a message typed and sent reaching B; a new chat started on B through Runs on and then steered;
// revocation closing its composer and removing B from Runs on; and Forget taking the section away. A picture at each stage
// with HEX_SELFTEST_SHOTS set. Explicit-only, as the link part is.
internal sealed partial class DawnScene
{
    // What's typed in the box and sent to B's chat, and the environment that has B say each Steer request it hears
    private const string LINK_UI_SENT = "Hello from A over the link", LINK_TEST_UI = "HEX_LINK_TEST_UI";

    private Action<string>? linkInviteCopyForTest;

    private const string LINK_UI_AGENT = "link_ui_fixture", LINK_UI_AGENT_NAME = "Link fixture agent";
    private const string LINK_UI_START = "Start from the Runs on picker", LINK_UI_FOLLOW = "Follow up on the same chat";
    private const string LINK_UI_DENIED = "This follow-up must be refused", LINK_UI_OUTPUT = "link-ui-fixture-ran";
    private const string LINK_UI_APPROVAL_CHILD = "Link approval child", LINK_UI_APPROVAL_TASK = "Review fixture output";

    // This fixture's process emits a fixed line and exits. The prompt is never passed to the shell.
    private HexExtension StartLinkUiAgent(string root)
    {
        string folder = Path.Combine(root, LINK_UI_AGENT);
        Directory.CreateDirectory(folder);
        string program = OperatingSystem.IsWindows() ? Path.Combine(System.Environment.SystemDirectory, "cmd.exe").Replace('\\', '/') : "/bin/echo";
        string args = OperatingSystem.IsWindows()
            ? $$"""["/d", "/c", "echo", "{{LINK_UI_OUTPUT}}"]"""
            : $$"""["{{LINK_UI_OUTPUT}}"]""";
        File.WriteAllText(Path.Combine(folder, HexExtension.FILE), $$"""
            let extension = { name: "{{LINK_UI_AGENT_NAME}}" };
            func on_start(hex) {
                hex.agent({ id: "{{LINK_UI_AGENT}}", name: "{{LINK_UI_AGENT_NAME}}", run: {
                    program: "{{program}}", stdin: false, arguments: (prompt, chat) => {{args}}
                } });
            }
            """);
        var extension = new HexExtension(folder, builtIn: false) { Enabled = true };
        extensions.Add(extension);
        extensions.Start(extension);
        return extension;
    }

    // B's side: what the other end was last said to be called
    private string? linkTestOtherName;

    /// <summary>B's side: the Steer requests its audit has since the last said, each said on a line of its own.</summary>
    private void LinkTestSaySteered(ref DateTime seen)
    {
        HexAgentChat? approvalChild = agentChats.FirstOrDefault(chat => chat.Name == LINK_UI_APPROVAL_CHILD &&
            chat.LinkStarter is not null && !chat.Running && !AgentRunBusy(chat) &&
            chat.Entries.All(entry => entry.Kind != HexAgentEntryKind.Approval));
        if (approvalChild is not null)
        {
            approvalChild.Ask("Ask before using the fixture output.");
            approvalChild.Approval(LINK_UI_APPROVAL_TASK, "fixture output", allow =>
            {
                Console.WriteLine($"[Hex self-test] link-peer: starter approval {(allow ? "allowed" : "denied")}");
                approvalChild.Done();
            });
        }

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
        string home = LinkTestFolder("hex-link-ui-a-", test);
        string peerHome = LinkTestFolder("hex-link-ui-b-", test);
        HexLinkPeer? peer = null;
        string? peerId = null;
        int peerChat = 0, spawnedChat = 0, localChatsMade = 0;
        string? previousAgent = null;
        bool localMcpWasRunning = false;
        HexExtension? localFixture = null;
        Task<McpToolResult>? refused = null;
        string localWorkspace = Path.Combine(home, "workspace", LINK_PEER_FOLDER);
        string remoteWorkspace = Path.Combine(peerHome, LINK_PEER_FOLDER);
        string? workspaceFailure = null;

        void Shot(string name) => test.Run(() =>
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is { Length: > 0 } shots)
                Engine.CaptureScreenshot(Path.Combine(shots, name));
        });

        Vector2 Centre(UIComponent? component) => component?.Module is { } module ? module.ToWorld(component.Bounds.Center) : Vector2.Zero;
        ContextMenu? OpenMenu() => chrome.Root.Children.OfType<ContextMenu>().FirstOrDefault(menu => menu.Showing is not null);
        Vector2 MenuItem(string label) => OpenMenu() is { Showing: { } menu } open && menu.Items.Any(item => item.Label == label)
            ? chrome.ToWorld(open.ItemBounds(label).Center) : Vector2.Zero;
        bool SpawnedMirror() => spawnedChat > 0 && agentChat is not null && agentChat == linkRemoteMirror
            && LinkRemoteOf(agentChat) is { } remote && remote.DeviceId == peerId && remote.Id == spawnedChat;
        bool MirrorHas(string text) => agentChat?.Entries.Any(row => row.Text.Contains(text, StringComparison.Ordinal)) == true;
        void ClearToasts()
        {
            foreach (UINotification card in toasts.Cards)
                card.Close();
        }
        Button? WindowButton(string label)
        {
            UIComponent? surface = (UIComponent?)linkOnboardingDialog ?? (UIComponent?)linkThisDeviceDialog ?? linkWindow;
            return surface is null ? null : Descendants(surface).OfType<Button>().FirstOrDefault(button => button.Label == label && button.IsShownInHierarchy);
        }
        void ClickWindow(string key) => ClickWindowLabel(Said(key));

        void ClickWindowLabel(string label)
        {
            test.Run(() =>
            {
                if (linkOnboardingDialog is null && linkThisDeviceDialog is null && WindowButton(label) is { } button)
                    linkDevicesScroll?.ScrollIntoView(button);
            });
            test.Settle(0.2f);
            test.Click(() => Centre(WindowButton(label)));
        }

        void ClickMore(string key, bool local = false)
        {
            Button? More() => linkWindow is null ? null : Descendants(linkWindow).OfType<Button>().FirstOrDefault(button =>
                button.Label == Said("link.devices_more") && (button.Tooltip == Said("link.devices_more_this")) == local);
            test.Run(() =>
            {
                if (More() is { } button)
                    linkDevicesScroll?.ScrollIntoView(button);
            });
            test.Settle(0.2f);
            test.Click(() => Centre(More()));
            string label = Said(key);
            test.WaitFor(() => OpenMenu()?.Showing?.Items.Any(item => item.Label == label) == true, 3);
            test.Click(() => MenuItem(label));
        }

        ToggleButton? DialogRight(string name) => name switch { "view" => linkPairView, "steer" => linkPairSteer, "predict" => linkPairPredict, _ => linkPairEdit };
        AgentCardButton? RemoteCard(UIComponent rows) => Descendants(rows).OfType<AgentCardButton>().FirstOrDefault(card => LinkRemoteOf(card.Chat) is { } remote && remote.DeviceName == LINK_PEER_NAME && card.Chat.Shown == LINK_PEER_CHAT);

        void RevealRemoteFleet()
        {
            test.Run(() =>
            {
                if (noonNarrow && !noonFleet.Visible)
                    noonFleetBack.OnPressed?.Invoke();
            });
            test.WaitFor(() => RemoteCard(noonFleetRows) is { IsShownInHierarchy: true }, 8);
            test.Run(() =>
            {
                if (RemoteCard(noonFleetRows) is { } card)
                    noonFleetScroll.ScrollIntoView(card);
            });
            test.Settle(0.35f);
        }

        // A up at a free port in a folder of its own, its Link window opened
        test.Run(() =>
        {
            linkTestPeerSaid.Clear();
            StopLink();
            if (!StartLink(port: 0, home: home, loopbackOnly: true))
                return;
            linkByTest = true;
            ShowFace(HexFace.Dawn);
            Directory.CreateDirectory(peerHome);
            workspaceFailure = LinkTestRepository(localWorkspace, remoteWorkspace);
            if (workspaceFailure is not null)
                Console.WriteLine($"[Hex self-test] link-ui: fixture repository failed: {workspaceFailure}");
            explorer.OpenFolder(localWorkspace);
            LinkLookAtWorkspace(force: true);
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_LINK_THEME") is { Length: > 0 } theme)
                UseEditorTheme(theme, save: false);
            ShowLinkWindowForTest();
        });
        test.Settle(0.4f);
        test.Check("link-ui: workspace fixtures are independent repositories", () => workspaceFailure is null && Directory.Exists(Path.Combine(remoteWorkspace, ".git")));
        test.Check("link-ui: Link's window opens, this device's name, its fingerprint and Link on", () =>
            linkWindow is not null && LinkOn && LinkDevice is { } me && Descendants(linkWindow).OfType<Label>().Any(label => label.Text.Contains(me.Fingerprint, StringComparison.Ordinal)));
        test.Check("link-ui: the window says how the key is kept (a self test's is the file alone, said as a warning)", () =>
            Descendants(linkWindow!).OfType<Badge>().Any(badge => badge.Text == Said("link.devices_key_file_chip") && badge.Tooltip == Said("link.key_file")));
        Shot("link_ui_window_empty.png");
        test.Check("link-ui: A listens on IPv4 loopback without LAN discovery", () => LinkTestBoundOnlyToLoopback);

        // Pairing started from the window, its countdown and addresses shown, B started to dial in
        ClickMore("link.pair_button", local: true);
        test.Settle(0.3f);
        test.Check("link-ui: Pair a Device opens pairing, the window counts it down", () =>
            LinkPairingUntil is not null && linkPairingCountdown is { Text.Length: > 0 } && WindowButton(Said("link.pair_stop")) is not null);
        Shot("link_ui_pairing_open.png");
        test.Run(() => LinkStartPeer(peerHome, $"127.0.0.1:{LinkPort}", new Dictionary<string, string> { [LINK_TEST_UI] = "1" }));

        // The pairing dialog, the code large and grouped, nothing ticked, Pair waiting for View
        test.WaitFor(() => linkPairingDialog is { IsOpen: true } || linkTestPeer is { HasExited: true }, 90.0f);
        test.Run(() =>
        {
            if (linkPairingDialog is { IsOpen: true })
                return;
            string state = linkTestPeer is { HasExited: true } process ? $"exited with {process.ExitCode}" : "did not reach pairing";
            Console.WriteLine($"[Hex self-test] link-ui: fixture B {state}");
            foreach (string line in linkTestPeerSaid.TakeLast(12))
                Console.WriteLine($"[Hex self-test] link-ui: fixture B output: {line}");
        });
        test.Settle(0.4f);
        test.Check("link-ui: the pairing dialog shows B, its code grouped three and three, nothing ticked and Pair greyed", () =>
            linkPairingDialog is { IsOpen: true } dialog && LinkPairingPending is { } asked && asked.Other.Name == LINK_PEER_NAME
            && Descendants(dialog).OfType<Label>().Any(label => label.Text.Contains(LinkCodeGrouped(asked.Code), StringComparison.Ordinal))
            && LinkPairTicked == HexLinkRights.None && !dialog.Buttons[0].Enabled);
        Shot("link_ui_pairing_dialog.png");
        // Autocomplete stands alone: it can be paired for without View, and doesn't tick View
        test.Click(() => Centre(DialogRight("predict")));
        test.Wait();
        test.Check("link-ui: Autocomplete alone can be paired for, without View", () =>
            LinkPairTicked == HexLinkRights.Predict && linkPairingDialog?.Buttons[0].Enabled == true);
        test.Click(() => Centre(DialogRight("predict")));
        test.Wait();
        test.Click(() => Centre(DialogRight("steer")));
        test.Wait();
        test.Check("link-ui: ticking Steer ticks View with it, and Pair can be pressed", () =>
            LinkPairTicked == (HexLinkRights.View | HexLinkRights.Steer) && linkPairingDialog?.Buttons[0].Enabled == true);
        test.Check("link-ui: granting Steer shows the command-execution warning", () => linkPairingDialog is { } dialog &&
            Descendants(dialog).OfType<Label>().Any(label => label.IsShownInHierarchy && label.Text.Contains(Said("link.right_means_steer"), StringComparison.Ordinal)));
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
        test.Check("link-ui: B's card shows Connected and the exact grants in both directions", () =>
        {
            if (linkWindowDevices is null)
                return false;
            var labels = Descendants(linkWindowDevices).OfType<Label>().Select(label => label.Text).ToList();
            Badge[] badges = [.. Descendants(linkWindowDevices).OfType<Badge>()];
            return labels.Any(text => text.Contains(LINK_PEER_NAME, StringComparison.Ordinal))
                && badges.Any(badge => badge.Text == Said("link.device_connected"))
                && badges.Count(badge => badge.Text == Said("link.right_title_steer") && badge.Icon == "warning") == 2
                && badges.Count(badge => badge.Text == Said("link.right_title_view")) == 2
                && badges.Count(badge => badge.Text == Said("link.right_title_edit")) == 1
                && !badges.Any(badge => badge.Text == Said("link.right_title_predict"));
        });
        Shot("link_ui_window_device.png");
        ClickWindow("link.devices_change_rights");
        test.Check("link-ui: Change opens the rights editor after pairing", () =>
            chrome.Root.Children.OfType<UIDialog>().Any(dialog => dialog.IsOpen &&
                Descendants(dialog).OfType<Button>().Any(button => button.Label == Said("link.devices_preset_worker"))));
        test.Run(() => chrome.Root.Children.OfType<UIDialog>().LastOrDefault(dialog => dialog.IsOpen)?.Close());
        ClickMore("link.devices_identity_details", local: true);
        test.Settle(0.3f);

        // This device's name: the machine's when the box is empty, typed and kept, heard by B at once, then back
        string machine = HexLinkNames.Clean(System.Environment.MachineName) ?? string.Empty;
        test.Check("link-ui: the name box is link_name, empty, with the machine's name in it greyed and said as what others see", () =>
            linkNameBox is { Text.Length: 0 } box && box.Placeholder == machine && settings.LinkName.Length == 0
            && linkNameSeen?.Text.Contains(machine, StringComparison.Ordinal) == true);
        test.Run(() => linkNameBox!.Text = "  A‮ renamed  ");
        ClickWindow("link.name_keep");
        test.WaitFor(() => LinkTestPeerSaid("other is called A renamed"), 15.0f);
        test.Check("link-ui: a name kept is cleaned into link_name, and B hears it at once over the link", () =>
            settings.LinkName == "A renamed" && LinkDevice?.Name == "A renamed" && LinkTestPeerSaid("other is called A renamed"));
        Shot("link_ui_renamed_here.png");
        ClickWindow("link.name_reset");
        // B heard the machine's name as the link came up, and again now
        bool HeardMachineAgain() => linkTestPeerSaid.Count(line => line.EndsWith($"link-peer: other is called {machine}", StringComparison.Ordinal)) >= 2;
        test.WaitFor(HeardMachineAgain, 15.0f);
        test.Check("link-ui: Use the Machine's Name empties link_name and B hears the machine's name", () =>
            settings.LinkName.Length == 0 && LinkDevice?.Name == machine && HeardMachineAgain());

        ClickWindow("link.devices_done");

        const string ConflictName = "link-ui-conflict.txt";
        Task? wroteRemote = null;
        test.Run(() =>
        {
            File.WriteAllText(Path.Combine(localWorkspace, ConflictName), "this device\n");
            wroteRemote = peer?.Write($"{LINK_PEER_FOLDER}/{ConflictName}", null, "the other device\n"u8.ToArray());
        });
        test.WaitFor(() => wroteRemote?.IsCompleted == true, 5);
        test.Run(() => LinkSaySyncError(peerId!, LINK_PEER_NAME, LINK_PEER_FOLDER, "fixture interruption"));
        test.Check("link-ui: a sync failure is visible in the status bar", () =>
            saidError && messageItem.Text.Contains("fixture interruption", StringComparison.Ordinal));
        ClickWindow("link.sync_start");
        bool SyncRecovered() => peerId is not null && LinkSyncs.Any(sync =>
            sync.DeviceId == peerId && sync.State is HexLinkSyncState.Idle or HexLinkSyncState.PullOnly)
            && linkSyncStatusError is null && !messageItem.Text.Contains("fixture interruption", StringComparison.Ordinal);
        test.WaitFor(SyncRecovered, 40);
        test.Check("link-ui: a recovered sync clears its failure from the status bar", SyncRecovered);
        test.Run(() =>
        {
            LinkSaySyncError(peerId!, LINK_PEER_NAME, LINK_PEER_FOLDER, "fixture interruption");
            Say("unrelated fixture failure", error: true);
            LinkClearRecoveredSyncError(peerId!, LINK_PEER_FOLDER, HexLinkSyncState.Idle);
        });
        test.Check("link-ui: sync recovery keeps a newer, unrelated status error", () =>
            saidError && messageItem.Text.Contains("unrelated fixture failure", StringComparison.Ordinal));
        test.Run(() => Say(string.Empty));
        test.WaitFor(() => LinkConflicts.Any(conflict => conflict.Path.EndsWith(ConflictName, StringComparison.Ordinal)), 20);
        Shot("link_ui_conflict.png");
        ClickWindowLabel(Said("link.devices_resolve", ("count", "1")));
        test.WaitFor(() => chrome.Root.Children.OfType<UIDialog>().Any(dialog => dialog is { IsOpen: true, IsPalette: true }), 4);
        test.Check("link-ui: Resolve opens a chooser with the conflicting file", () =>
            chrome.Root.Children.OfType<UIDialog>().Where(dialog => dialog is { IsOpen: true, IsPalette: true })
                .SelectMany(Descendants).OfType<ListBox>().Any(list => list.Items.Any(item => item.EndsWith(ConflictName, StringComparison.Ordinal))));
        test.Run(() => chrome.Root.Children.OfType<UIDialog>().LastOrDefault(dialog => dialog is { IsOpen: true, IsPalette: true })?.Close());
        test.Settle(0.25f);
        Button? ConfirmStopSharing() => chrome.Root.Children.OfType<UIDialog>().LastOrDefault(dialog => dialog.IsOpen)?.Buttons
            .FirstOrDefault(button => button.Danger && button.Label == Said("link.devices_stop_sharing"));
        ClickMore("link.devices_stop_sharing");
        test.WaitFor(() => ConfirmStopSharing() is not null, 4);
        test.Settle(0.35f);
        test.Check("link-ui: Stop sharing asks before stopping a running sync", () =>
            peerId is not null && LinkSyncs.Any(sync => sync.DeviceId == peerId && sync.State != HexLinkSyncState.Off)
            && ConfirmStopSharing() is not null);
        test.Click(() => Centre(ConfirmStopSharing()));
        test.WaitFor(() => LinkSyncs.All(sync => sync.DeviceId != peerId || sync.State == HexLinkSyncState.Off), 5);
        test.Check("link-ui: confirming Stop sharing stops the sync and keeps the peer connected", () =>
            LinkSyncs.All(sync => sync.DeviceId != peerId || sync.State == HexLinkSyncState.Off) && LinkPeers.Count == 1);
        test.Run(() =>
        {
            UIDialog? confirmation = chrome.Root.Children.OfType<UIDialog>().LastOrDefault(dialog => dialog.IsOpen &&
                dialog.Buttons.Any(button => button.Label == Said("link.devices_stop_sharing")));
            confirmation?.Close();
        });

        // B called something of this machine's own, which wins everywhere it's shown, and undone
        const string LOCAL = "Bee box";
        test.Run(() => LinkRename(peerId!, LOCAL));
        test.Settle(0.4f);
        test.Check("link-ui: B named here shows by that name and keeps its original name visible", () =>
            linkWindowDevices is { } devices && Descendants(devices).OfType<Label>().Any(label => label.Text.Contains(LOCAL, StringComparison.Ordinal))
            && Descendants(devices).OfType<Label>().Any(label => label.Text.Contains(Said("link.device_calls_itself", ("name", LINK_PEER_NAME)), StringComparison.Ordinal)));
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
        test.Run(ShowLinkWindowForTest);
        test.Settle(0.4f);
        ClickMore("link.device_own_name");
        test.Settle(0.3f);
        test.Check("link-ui: Use Its Own Name gives B its own name back", () => LinkKnown.FirstOrDefault(known => known.Device.Id == peerId) is { Named: null, Shown: LINK_PEER_NAME });

        // Agentic's fleet with a section of B's, its chat and B's badge
        test.Run(() =>
        {
            linkWindow?.Close();
            ShowFace(HexFace.Noon);
            noonWait = 0.0f;
        });
        RevealRemoteFleet();
        test.Check("link-ui: the fleet has a section of B's with its chat as a card", () => RemoteCard(noonFleetRows) is not null
            && Descendants(noonFleetRows).OfType<Label>().Any(label => label.Text.Contains(LINK_PEER_NAME, StringComparison.Ordinal)));
        test.Check("link-ui: B's card carries B's badge", () => RemoteCard(noonFleetRows)?.Parent is { } card && Descendants(card).OfType<Badge>().Any(badge => badge.Text == LINK_PEER_NAME));
        test.Check("link-ui: the remote fleet identifies the pending approval and its request number", () =>
            peer?.Fleet.Any(row => row.Id == peerChat && row.Approvals.Any(approval => approval.Number == 1 && approval.Title == "Run the tests")) == true &&
            RemoteCard(noonFleetRows)?.Parent is { } approvalCard && Descendants(approvalCard).OfType<Badge>()
                .Any(badge => badge.Text == Said("agents_panel.waiting_approval_short")));
        Shot("link_ui_fleet.png");

        Task<McpToolResult>? waitingOnApproval = null;
        test.Run(() => waitingOnApproval = McpTools.Call("hex_link_call", new JsonObject
        {
            ["device"] = LINK_PEER_NAME, ["tool"] = "hex_agent_wait",
            ["arguments"] = new JsonObject { ["ids"] = new JsonArray(JsonValue.Create(peerChat)), ["timeout"] = 60 }
        }, CancellationToken.None));
        test.WaitFor(() => waitingOnApproval?.IsCompleted == true, 8);
        test.Check("link-ui: waiting over Link returns immediately when B asks leave", () =>
            waitingOnApproval is { IsCompletedSuccessfully: true } reply && !reply.Result.IsError &&
            reply.Result.AllText().Contains("asks leave: Run the tests (request 1)", StringComparison.Ordinal));

        // Picking it shows B's chat, its rows, under the banner saying where it runs
        test.Click(() => Centre(RemoteCard(noonFleetRows)));
        test.WaitFor(() => linkRemoteMirror is not null && agentChat == linkRemoteMirror, 20.0f);
        test.Settle(0.5f);
        test.Check("link-ui: picking B's card shows its chat mirrored, its rows as B has them", () =>
            linkRemoteMirror is { } mirror && agentChat == mirror && LinkTestRows(mirror) == LinkTestPeerLine("rows ") && !agentChats.Contains(mirror));
        test.Check("link-ui: the banner over the box says the chat runs on B", () =>
            linkBanner is { Visible: true } banner && Descendants(banner).OfType<Label>().Any(label => label.Tooltip.Contains(Said("link.chat_runs_on", ("device", LINK_PEER_NAME)), StringComparison.Ordinal)));
        Shot("link_ui_remote_chat.png");
        Button? RemoteApprovalButton(string icon) => agentsWaitStrip is { Visible: true } strip
            ? Descendants(strip).OfType<Button>().FirstOrDefault(button => button.Icon == icon && button.Enabled) : null;
        test.WaitFor(() => RemoteApprovalButton("check") is not null, 6);
        test.Check("link-ui: a Steer mirror shows Allow and Deny for the pending approval", () =>
            RemoteApprovalButton("check") is not null && RemoteApprovalButton("close") is not null);
        test.Click(() => Centre(RemoteApprovalButton("check")));
        test.WaitFor(() => LinkTestPeerSaid("approval allowed"), 8);
        test.Check("link-ui: Allow from the remote approval card reaches the hosting chat", () => LinkTestPeerSaid("approval allowed"));

        HexAgentChat? approvalStarter = null;
        Task<McpToolResult>? approvalSpawn = null, approvalReply = null;
        test.Run(() =>
        {
            approvalStarter = new HexAgentChat(++agentChatsMade, "link_approval_starter");
            agentChats.Add(approvalStarter);
            approvalSpawn = new HexMcpCaller(McpTools, approvalStarter.Id).Call("hex_link_spawn", new JsonObject
            {
                ["device"] = LINK_PEER_NAME, ["agent"] = LINK_UI_AGENT, ["name"] = LINK_UI_APPROVAL_CHILD,
                ["prompt"] = "Review the fixture.", ["options"] = new JsonObject { ["effort"] = "low" },
                ["permissions"] = new JsonObject { ["deny"] = new JsonArray(JsonValue.Create("Bash")) }
            }, CancellationToken.None);
        });
        bool StarterHasQuestion() => approvalStarter?.UnreadMail().Any(mail => mail.Kind == "question" &&
            mail.Text.Contains($"asks leave: {LINK_UI_APPROVAL_TASK} (request 1)", StringComparison.Ordinal)) == true;
        test.WaitFor(StarterHasQuestion, 16);
        test.Check("link-ui: hex_link_spawn routes a request to its exact starter as a question", () =>
            approvalSpawn is { IsCompletedSuccessfully: true } spawn && !spawn.Result.IsError && StarterHasQuestion());
        test.Run(() =>
        {
            if (approvalStarter?.LinkChildren.FirstOrDefault() is { } child)
                approvalReply = new HexMcpCaller(McpTools, approvalStarter.Id).Call("hex_link_call", new JsonObject
                {
                    ["device"] = LINK_PEER_NAME, ["tool"] = "hex_agent_approve",
                    ["arguments"] = new JsonObject { ["id"] = child.Address.Chat, ["request"] = 1, ["allow"] = true }
                }, CancellationToken.None);
        });
        test.WaitFor(() => LinkTestPeerSaid("starter approval allowed"), 8);
        test.Check("link-ui: the starter can answer its numbered remote request through hex_link_call", () =>
            approvalReply is { IsCompletedSuccessfully: true } answer && !answer.Result.IsError && LinkTestPeerSaid("starter approval allowed"));
        test.Run(() =>
        {
            if (approvalStarter is not null)
                agentChats.Remove(approvalStarter);
        });

        test.Check("link-ui: a running mirror may show typing before its first entry arrives", () =>
        {
            var empty = new HexAgentChat(-1, LINK_UI_AGENT);
            empty.MirrorHead("New remote chat", running: true, 0);
            ShowTyping(empty, still: true);
            bool shown = empty.Entries.Count == 0 && agentsTyping is not null;
            empty.MirrorHead("New remote chat", running: false, 0);
            ShowTyping(empty, still: true);
            return shown;
        });

        // Typed in the box and sent, it reaches B
        bool InFixtureMirror() => peerId is not null && agentChat is { } shown && shown == linkRemoteMirror && LinkRemoteOf(shown)?.DeviceId == peerId;
        test.Run(() =>
        {
            if (!InFixtureMirror())
                return;
            agentsInput.Focus();
            agentsInput.Text = LINK_UI_SENT;
        });
        test.Settle(0.3f);
        test.Check("link-ui: the box can send to B's chat while B lets this one steer", () => InFixtureMirror() && agentsSend.Enabled && !agentsInput.ReadOnly);
        // A notification left by an earlier suite can cover Send until dismissed.
        test.Run(ClearToasts);
        test.WaitFor(() => toasts.Cards.Count == 0, 2.0f);
        test.Click(() => InFixtureMirror() ? Centre(agentsSend) : Vector2.Zero);
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

        // A local fallback would start the same harmless process. Only B's copy should run.
        test.Run(() =>
        {
            previousAgent = agentPicked;
            localMcpWasRunning = mcpHost is not null;
            localFixture = StartLinkUiAgent(home);
            agentPicked = LINK_UI_AGENT;
            localChatsMade = agentChatsMade;
        });
        test.Click(() => Centre(linkRunsOnPicker));
        test.WaitFor(() => OpenMenu()?.Showing?.Items.Any(item => item.Label == LINK_PEER_NAME) == true, 3.0f);
        test.Click(() => MenuItem(LINK_PEER_NAME));
        test.WaitFor(() => peerId is not null && linkRemoteSystems.TryGetValue(peerId, out var systems)
            && systems.Any(system => system.Id == LINK_UI_AGENT && !system.Missing), 12.0f);
        test.Check("link-ui: choosing B from the open Runs on menu selects B and loads its agent systems", () =>
            peer is not null && LinkRunsOnPeer() == peer && linkRemoteSystems.TryGetValue(peer.Device.Id, out var systems)
            && systems.Any(system => system.Id == LINK_UI_AGENT && !system.Missing));
        // Expose the interval between the device's answer arriving and the composer drawing again.
        test.Run(() =>
        {
            if (peerId is null || !linkRemoteSystems.Remove(peerId, out List<LinkRemoteSystem>? loaded))
                return;
            agentsOptionsDrawn = string.Empty;
            DrawAgentOptions();
            linkRemoteSystems[peerId] = loaded;
            agentsWait = 3.0f;
        });
        Button? AgentSystemPicker() => Descendants(agentsOptions).OfType<Button>().FirstOrDefault(button =>
            button.Tooltip == Said("link.runs_on_agent_tip", ("device", LINK_PEER_NAME)));
        test.WaitFor(() => AgentSystemPicker() is { IsShownInHierarchy: true } button && !button.Bounds.IsEmpty, 3.0f);
        test.Settle(0.3f);
        test.Click(() => Centre(AgentSystemPicker()));
        test.WaitFor(() => OpenMenu()?.Showing?.Items.Any(item => item.Label == LINK_UI_AGENT_NAME) == true, 3.0f);
        test.Check("link-ui: B's agent menu uses the latest systems before the composer redraws", () =>
        {
            if (OpenMenu() is not { Showing: { } choices } opened)
            {
                Console.WriteLine("[Hex self-test] link-ui: remote agent picker did not open its menu");
                return false;
            }
            Vector2 point = MenuItem(LINK_UI_AGENT_NAME);
            if (point != Vector2.Zero)
                return true;
            Console.WriteLine($"[Hex self-test] link-ui: no fixture target in {string.Join(", ", choices.Items.Select(item => item.Label))}; menu {opened.ListBounds}");
            return false;
        });
        test.Settle(0.25f);
        Shot("link_ui_agent_system_menu.png");
        test.Click(() => MenuItem(LINK_UI_AGENT_NAME));
        test.Check("link-ui: the agent-system picker selects B's fixture without covering Send", () =>
        {
            if (peerId is not null && linkRemotePicked.GetValueOrDefault(peerId).Agent == LINK_UI_AGENT)
                return true;
            Button? picker = Descendants(agentsOptions).OfType<Button>().FirstOrDefault(button =>
                button.Tooltip == Said("link.runs_on_agent_tip", ("device", LINK_PEER_NAME)));
            Console.WriteLine($"[Hex self-test] link-ui: fixture selection failed at composer width {agentsCompose.Bounds.Width:F0}, agent picker {picker?.Bounds.Center}, send {agentsSend.Bounds.Center}, menu items {OpenMenu()?.Showing?.Items.Count}");
            return false;
        });
        test.Run(() =>
        {
            if (peerId is not null && LinkRunsOnPeer()?.Device.Id == peerId && linkRemotePicked.GetValueOrDefault(peerId).Agent == LINK_UI_AGENT)
                agentsInput.Text = LINK_UI_START;
        });
        test.WaitFor(() => agentsSend.Enabled, 3.0f);
        test.Check("link-ui: the selected fixture agent can be sent a first prompt", () => agentsSend.Enabled && agentsInput.Text == LINK_UI_START);
        test.Click(() => Centre(agentsSend));
        test.WaitFor(() =>
        {
            if (agentChat is null || agentChat != linkRemoteMirror || LinkRemoteOf(agentChat) is not { } remote
                || remote.DeviceId != peerId || remote.Id == peerChat)
                return false;
            spawnedChat = remote.Id;
            return MirrorHas(LINK_UI_START) && MirrorHas(LINK_UI_OUTPUT)
                && peer?.Fleet.Any(row => row.Id == spawnedChat && row.Agent == LINK_UI_AGENT) == true;
        }, 20.0f);
        test.Check("link-ui: Send starts a new fixture chat on B and opens its mirror with the first prompt and process output", () =>
            SpawnedMirror() && MirrorHas(LINK_UI_START) && MirrorHas(LINK_UI_OUTPUT)
            && peer?.Fleet.Any(row => row.Id == spawnedChat && row.Agent == LINK_UI_AGENT) == true);
        test.Check("link-ui: sending the first prompt creates no local chat or agent run", () =>
            agentChatsMade == localChatsMade && !agentChats.Any(chat => chat.Agent == LINK_UI_AGENT)
            && !agentRuns.Any(run => run.Chat.Agent == LINK_UI_AGENT));
        Shot("link_ui_runs_on_spawned.png");

        test.WaitFor(() => SpawnedMirror() && agentChat is { Running: false }, 8.0f);
        test.Run(ClearToasts);
        test.WaitFor(() => toasts.Cards.Count == 0, 2.0f);
        test.Run(() =>
        {
            if (SpawnedMirror())
                agentsInput.Text = LINK_UI_FOLLOW;
        });
        test.WaitFor(() => agentsSend.Enabled, 3.0f);
        test.Click(() => Centre(agentsSend));
        test.WaitFor(() => SpawnedMirror() && MirrorHas(LINK_UI_FOLLOW)
            && agentChat!.Entries.Count(row => row.Text == LINK_UI_OUTPUT) == 2, 45.0f);
        test.Check("link-ui: a follow-up sent while Steer is granted runs on the same remote chat", () =>
            SpawnedMirror() && MirrorHas(LINK_UI_FOLLOW) && agentChat!.Entries.Count(row => row.Text == LINK_UI_OUTPUT) == 2);

        // B takes Steer away while the newly started chat is still open.
        test.Run(() => _ = peer?.Write($"{LINK_PEER_FOLDER}/{LINK_FLAG_VIEW}", null, [1]));
        test.WaitFor(() => peer?.Granted == HexLinkRights.View, 20.0f);
        test.Settle(0.5f);
        test.Check("link-ui: revocation keeps the spawned chat mirrored and makes its composer watch-only", () =>
            SpawnedMirror() && MirrorHas(LINK_UI_FOLLOW) && agentsInput.ReadOnly && !agentsSend.Enabled
            && agentsPlaceholder.Text == Said("link.chat_watch_only", ("device", LINK_PEER_NAME))
            && !agentsStop.Enabled && agentsWaitStrip is not { Visible: true });
        test.Run(() =>
        {
            if (peer is not null && spawnedChat > 0)
                refused = peer.Send(spawnedChat, LINK_UI_DENIED);
        });
        test.WaitFor(() => refused?.IsCompleted == true, 8.0f);
        test.Check("link-ui: B refuses a Steer request for the spawned chat after revocation", () =>
            refused?.Exception?.GetBaseException() is HexLinkException { Code: HexLinkErrors.NOT_ALLOWED }
            && SpawnedMirror() && !MirrorHas(LINK_UI_DENIED));
        Shot("link_ui_watch_only.png");

        test.Run(() =>
        {
            NewAgentChat();
            agentsOptionsDrawn = string.Empty;
            agentsWait = 0.0f;
        });
        test.Settle(0.4f);
        test.Check("link-ui: with Steer taken away on B, Runs on is gone from a new chat's box", () =>
            LinkRunsOnPeer() is null && LinkRunsOnDevices().Count == 0 && (linkRunsOnPicker is null || !linkRunsOnPicker.IsShownInHierarchy));
        RevealRemoteFleet();
        test.Click(() => Centre(RemoteCard(noonFleetRows)));
        test.WaitFor(() => LinkRemoteOf(agentChat) is not null, 10.0f);
        test.Settle(0.5f);
        test.Check("link-ui: B's section's chips say View alone now", () =>
            !Descendants(noonFleetRows).OfType<Badge>().Any(badge => badge.Text == Said("link.right_title_steer"))
            && Descendants(noonFleetRows).OfType<Badge>().Any(badge => badge.Text == Said("link.right_title_view")));

        // Forgotten from the window, asked once, B's section goes
        test.Run(ShowLinkWindowForTest);
        test.Settle(0.4f);
        ClickMore("link.device_forget");
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
        TestLinkInviteUi(test, home, Centre, WindowButton, ClickWindow, key => ClickMore(key), Shot);
        test.Run(() =>
        {
            foreach (HexAgentChat chat in agentChats.Where(chat => chat.Agent == LINK_UI_AGENT && chat.Id > localChatsMade).ToArray())
                CloseAgentChat(chat);
            if (!localMcpWasRunning && mcpHost is not null)
                StopMcp();
            if (localFixture is not null)
            {
                extensions.ForgetAgents(localFixture);
                extensions.Remove(localFixture);
            }

            if (peerId is not null)
            {
                linkRemoteSystems.Remove(peerId);
                linkRemotePicked.Remove(peerId);
                if (linkRunsOn.GetValueOrDefault(LinkWorkspaceKey) == peerId)
                    LinkPickRunsOn(null);
            }
            agentPicked = previousAgent;
            ShowFace(HexFace.Dawn);
        });
    }

    private void ShowLinkWindowForTest()
    {
        ShowLinkWindow();
        if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_LINK_NARROW") == "1" && linkWindow is not null)
            linkWindow.Size = linkWindow.Size with { X = LINK_WINDOW_LEAST.X };
    }

    private void TestLinkInviteUi(HexPointerScript test, string home, Func<UIComponent?, Vector2> centre, Func<string, Button?> windowButton,
        Action<string> clickWindow, Action<string> clickMore, Action<string> shot)
    {
        HexLinkIdentity? identity = null;
        HexLinkTransport? other = null;
        HexLinkInvite? token = null;
        TcpListener? secondRoute = null;
        HexLinkEndpoint alternate = default, direct = default;
        Task<McpToolResult>? routeTool = null, routeList = null;
        var sessions = new System.Collections.Concurrent.ConcurrentBag<HexLinkSession>();
        const string Device = "Invite fixture";
        string? clipboard = null;
        int workspaceOffers = 0;
        UIDialog? OpenDialog() => chrome.Root.Children.OfType<UIDialog>().LastOrDefault(dialog => dialog.IsOpen);
        bool HasBadge(string key) => linkWindowDevices is { } rows &&
            Descendants(rows).OfType<Badge>().Any(badge => badge.Text == Said(key));

        void ShowConnectionState(string badge, LinkDeviceConnection state, string file)
        {
            test.Run(() =>
            {
                if (identity is not null)
                    linkManualDials[identity.Id] = state;
                linkWindowStale = true;
            });
            test.Settle(0.2f);
            test.Check($"link-ui: device card shows {Said(badge)}", () => HasBadge(badge));
            shot(file);
        }

        test.Run(() =>
        {
            foreach (UIDialog dialog in chrome.Root.Children.OfType<UIDialog>().Where(dialog => dialog.IsOpen).ToArray())
                dialog.Close();
            linkWindow?.Close();
            StopLink();
            StartLink(port: 0, home: Path.Combine(home, "invite-ui"), loopbackOnly: true);
            linkByTest = true;
            ShowFace(HexFace.Dawn);
            ShowLinkWindowForTest();
            linkInviteCopyForTest = copied => clipboard = copied;
        });
        clickWindow("link.devices_invite");
        clickWindow("link.devices_preset_worker");
        test.Check("link-ui: worker preset grants View and Edit and requests Steer on the worker", () =>
            linkInvitePreset == HexLinkPreset.Worker && linkInviteRights == (HexLinkRights.View | HexLinkRights.Edit) &&
            LinkInvitationTerms().HostRequests == (HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit));
        shot("link_ui_invite_preset.png");
        test.WaitFor(() => LinkActiveInvite is not null && linkPairingCountdown?.Text.Length > 0, 4);
        test.Check("link-ui: Invite creates one scoped token and countdown", () => LinkActiveInvite is { } open &&
            open.Pin == LinkDevice?.Id && open.Port == LinkPort && linkPairingCountdown?.Text.Length > 0);
        clickWindow("link.devices_copy_invite");
        test.Check("link-ui: Copy invite puts the token and its loopback endpoint on the clipboard", () =>
            HexLinkInvite.TryParse(clipboard ?? string.Empty, out HexLinkInvite? copied) && copied is not null &&
            copied.Port == LinkPort && copied.Addresses.SequenceEqual([IPAddress.Loopback]));
        shot("link_ui_invite_open.png");
        clickWindow("link.devices_cancel_invite");
        test.Check("link-ui: Cancel invite revokes the invitation", () => LinkActiveInvite is null && LinkPairingUntil is null);

        test.Run(() =>
        {
            identity = HexLinkIdentity.LoadOrCreate(Path.Combine(home, "invite-fixture"), Device, fileOnly: true);
            other = new HexLinkTransport(identity, new HexLinkTrust(Path.Combine(home, "invite-fixture")));
            other.Accepted = connection => sessions.Add(new HexLinkSession(connection)
            {
                Requested = (method, parameters, _) =>
                {
                    if (method == "git/bundle" && parameters?["operation"]?.GetValue<string>() == "offer")
                        Interlocked.Increment(ref workspaceOffers);
                    if (method != "link/hello")
                        return Task.FromResult<JsonNode?>(new JsonObject());
                    return Task.FromResult<JsonNode?>(new JsonObject
                    {
                        ["protocol"] = HexLinkHost.PROTOCOL,
                        ["device"] = HexLinkData.DeviceJson(identity.Device),
                        ["granted"] = HexLinkData.Words(HexLinkRights.View | HexLinkRights.Steer),
                        ["workspace"] = new JsonObject
                        {
                            ["name"] = "Fixture workspace",
                            ["repositories"] = new JsonArray(new JsonObject { ["name"] = "fixture", ["branch"] = "main", ["head"] = "0123456789abcdef" })
                        }
                    });
                }
            }.Start());
            other.Start(0, loopbackOnly: true);
            other.OpenInvite([IPAddress.Loopback], TimeSpan.FromMinutes(1), _ => Task.FromResult<HexLinkRights?>(HexLinkRights.View | HexLinkRights.Steer));
            token = other.ActiveInvite;
        });
        clickWindow("link.devices_join");
        void TypeJoin(string text)
        {
            if (linkAddressBox is not null)
                linkAddressBox.Text = text;
            LinkSetJoinMessage(null);
        }

        test.Run(() => TypeJoin("hex-link:v1.bad"));
        clickWindow("link.devices_join_submit");
        test.Check("link-ui: a malformed invite gives recovery instructions in the join card", () =>
            linkJoinStatus?.Text.Contains(Said("link.devices_bad_invite"), StringComparison.Ordinal) == true && linkJoinSubmit?.Enabled == false);
        shot("link_ui_join.png");
        test.Run(() => TypeJoin(token?.Encode() ?? string.Empty));
        clickWindow("link.devices_join_submit");
        test.WaitFor(() => linkPairingDialog is { IsOpen: true }, 12);
        test.Settle(0.35f);
        test.Check("link-ui: join by invite verifies identity and opens grants with no comparison code", () =>
            LinkPairingPending is { Invited: true, Other.Name: Device, Code.Length: 0 } &&
            linkPairingDialog is { IsOpen: true } dialog && Descendants(dialog).OfType<Label>().Any(label => label.Text.Contains(Said("link.devices_invite_verified"), StringComparison.Ordinal)));
        test.Click(() => centre(linkPairingDialog is { } dialog ? Descendants(dialog).OfType<Button>().FirstOrDefault(button =>
            button.Label == Said("link.devices_preset_viewer")) : null));
        test.Settle(0.2f);
        test.Check("link-ui: viewer preset grants View alone", () => LinkPairTicked == HexLinkRights.View);
        shot("link_ui_invite_verified.png");
        test.Click(() => centre(linkPairingDialog?.Buttons[0]));
        test.WaitFor(() => LinkPeers.Any(peer => peer.Device.Name == Device) && HasBadge("link.device_connected"), 12);
        test.Check("link-ui: a verified invite becomes a connected device and consumes the invitation", () =>
            LinkPeers.Any(peer => peer.Device.Name == Device) && other?.ActiveInvite is null && HasBadge("link.device_connected"));
        test.WaitFor(() => linkLatencies.Values.Any(sample => sample.Milliseconds is not null), 5);
        test.Check("link-ui: the connected card shows the advertised branch and abbreviated commit", () =>
            linkWindowDevices is { } devices && Descendants(devices).OfType<Label>()
                .Any(label => label.Text.Contains("main @ 01234567", StringComparison.Ordinal)));
        shot("link_ui_invite_connected.png");
        clickWindow("link.devices_share");
        test.WaitFor(() => Volatile.Read(ref workspaceOffers) == 1, 5);
        test.Check("link-ui: Share workspace sends a provisioning offer to the paired device", () =>
            Volatile.Read(ref workspaceOffers) == 1);
        clickMore("link.devices_get");
        test.WaitFor(() => textDialog is { IsOpen: true }, 5);
        test.Check("link-ui: Get workspace opens the destination prompt", () =>
            textDialog is { IsOpen: true } destination && destination.Buttons[0].Label == Said("link.provision.get_button"));
        test.Run(() =>
        {
            textDialog?.Close();
            linkWindow?.Close();
            foreach (string folder in explorer.workspace.Folders.ToArray())
                explorer.RemoveFolder(folder);
            LinkLookAtWorkspace(force: true);
        });
        Task<JsonNode?>? sentOffer = null;
        bool IsWorkspaceOffer(UIDialog dialog) => dialog.Buttons.Any(button => button.Label == Said("link.devices_workspace_offer_get"));
        test.WaitFor(() => linkServices?.Workspace.Folders.Count == 0 && linkWindow is null && OpenDialog() is null, 8);
        test.Settle(0.35f);
        test.Run(() =>
        {
            if (sessions.TryPeek(out HexLinkSession? session))
                sentOffer = session.Ask("git/bundle", new JsonObject { ["operation"] = "offer", ["workspace"] = "Fixture workspace" });
        });
        test.WaitFor(() => sentOffer is { IsCompletedSuccessfully: true } && OpenDialog() is { } dialog && IsWorkspaceOffer(dialog), 8);
        test.Settle(0.45f);
        test.Check("link-ui: a received workspace offer opens a choice with no workspace or Devices window open", () =>
            sentOffer is { IsCompletedSuccessfully: true } && explorer.workspace.Folders.Count == 0 &&
            linkWindow is null && OpenDialog() is { } dialog && IsWorkspaceOffer(dialog));
        shot("link_ui_workspace_offer.png");
        test.Click(() => centre(OpenDialog()?.Buttons.FirstOrDefault(button => button.Label == Said("link.devices_workspace_offer_later"))));
        test.WaitFor(() => OpenDialog() is null, 5);
        test.Settle(0.3f);
        test.Run(ShowLinkWindowForTest);
        test.Settle(0.25f);
        test.Check("link-ui: Not now keeps the workspace offer on the device row", () =>
            linkWindowDevices is { } rows && Descendants(rows).OfType<Label>().Any(label =>
                label.Text == Said("link.devices_workspace_offer", ("device", Device), ("workspace", "Fixture workspace"))));
        clickWindow("link.devices_get");
        test.WaitFor(() => textDialog is { IsOpen: true }, 5);
        test.Check("link-ui: getting a deferred workspace offer opens the folder chooser", () =>
            textDialog is { IsOpen: true } picker && picker.Buttons.Any(button => button.Label == Said("explorer_workspace.add")));
        test.Run(() => textDialog?.Close());
        test.WaitFor(() => OpenDialog() is null, 5);
        test.Settle(0.3f);
        clickWindow("link.devices_open_files");
        test.WaitFor(() => OpenDialog() is { IsPalette: true }, 5);
        test.Check("link-ui: Open its files opens a browser for the connected device", () => OpenDialog() is { IsOpen: true, IsPalette: true });
        test.Run(() => OpenDialog()?.Close());
        clickWindow("link.devices_start_agent");
        test.WaitFor(() => linkWindow is null, 4);
        test.Check("link-ui: Start agent there opens a new draft with Runs on set", () => agentChat is null && LinkRunsOnPeer()?.Device.Name == Device);
        test.Run(ShowLinkWindowForTest);
        clickMore("link.device_disconnect");
        test.WaitFor(() => LinkPeers.Count == 0 && HasBadge("link.device_held_off"), 5);
        test.Check("link-ui: Disconnect holds the device offline without forgetting it", () =>
            identity is not null && LinkHeldOff(identity.Id) && LinkKnown.Any(known => known.Device.Id == identity.Id) && HasBadge("link.device_held_off"));
        shot("link_ui_disconnected.png");
        ShowConnectionState("link.devices_connecting", new LinkDeviceConnection(true, null, null), "link_ui_connecting.png");
        ShowConnectionState("link.devices_version", new LinkDeviceConnection(false, null, "version"), "link_ui_update_needed.png");
        ShowConnectionState("link.devices_refused", new LinkDeviceConnection(false, null, "refused"), "link_ui_refused.png");
        test.Run(() =>
        {
            if (identity is not null)
                linkManualDials[identity.Id] = new LinkDeviceConnection(false, DateTime.UtcNow.AddSeconds(25), "unreachable");
            linkWindowStale = true;
        });
        test.Settle(0.2f);
        test.Check("link-ui: retrying shows a countdown and actionable firewall guidance", () =>
            linkOfflineRows.Any(row => row.State.Text.StartsWith(Said("link.devices_retrying", ("seconds", "")).TrimEnd(' ', 's'), StringComparison.Ordinal)
                && row.Details.Text.Contains("firewall", StringComparison.OrdinalIgnoreCase)));
        shot("link_ui_retrying.png");
        test.Run(() =>
        {
            if (identity is not null)
                linkManualDials.Remove(identity.Id);
            linkWindowStale = true;
        });
        clickWindow("link.device_connect");
        test.WaitFor(() => LinkPeers.Count == 1 && HasBadge("link.device_connected"), 8);
        test.Check("link-ui: Connect redials the saved route", () => LinkPeers.Count == 1 && HasBadge("link.device_connected"));

        // Use an IPv6 loopback entry as a second route to the same pinned TLS peer.
        test.Run(() =>
        {
            if (other is null || identity is null)
                return;
            secondRoute = new TcpListener(IPAddress.IPv6Loopback, 0);
            secondRoute.Start();
            direct = new HexLinkEndpoint(IPAddress.Loopback.ToString(), other.Port);
            alternate = new HexLinkEndpoint(IPAddress.IPv6Loopback.ToString(), ((IPEndPoint)secondRoute.LocalEndpoint).Port);
            linkRedial.Remember(identity.Id, [direct, alternate]);
            _ = LinkUiRelay(secondRoute, other.Port);
        });
        ContextMenu? RouteMenu() => chrome.Root.Children.OfType<ContextMenu>().FirstOrDefault(menu => menu.Showing is not null);
        clickMore("link.devices_route_reconnect");
        test.WaitFor(() => RouteMenu()?.OpenSubmenu?.Title == Said("link.devices_route_reconnect"), 4);
        string RouteLabel(HexLinkEndpoint route) => $"{Said("link.devices_route_lan")} · {LinkRouteAddress(route)}";
        test.Check("link-ui: Reconnect via lists Automatic and both known endpoints", () =>
            RouteMenu()?.OpenSubmenu is { } routes && routes.Items.Any(item => item.Label == Said("link.devices_route_auto") && item.IsChecked?.Invoke() == true) &&
            routes.Items.Any(item => item.Label == RouteLabel(direct)) && routes.Items.Any(item => item.Label == RouteLabel(alternate)));
        shot("link_ui_routes.png");
        test.Click(() => chrome.ToWorld(RouteMenu()?.ItemBounds(RouteLabel(alternate)).Center ?? Vector2.Zero));
        test.WaitFor(() => LinkPeers.Any(peer => peer.Session.Connection.Address == alternate.Host), 13);
        test.Check("link-ui: pointer switches to the second route with the existing pin and rights", () =>
            LinkPeers is [var routed] && routed.Device.Id == identity?.Id && routed.Session.Connection.Address == alternate.Host &&
            routed.Granted == (HexLinkRights.View | HexLinkRights.Steer) && LinkKnown is [{ PreferredRoute: var saved, Rights: HexLinkRights.View }] &&
            saved == alternate && LinkPairingPending is null);
        test.Run(() => routeList = McpTools.Call("hex_link_devices", [], CancellationToken.None));
        test.WaitFor(() => routeList?.IsCompleted == true, 5);
        test.Check("link-ui: hex_link_devices shows the selected route and both addresses", () =>
            routeList is { IsCompletedSuccessfully: true } listed && listed.Result.AllText() is { } report &&
            report.Contains($"current route: LAN {alternate.Host}", StringComparison.Ordinal) &&
            report.Contains(LinkRouteAddress(direct), StringComparison.Ordinal) &&
            report.Contains(LinkRouteAddress(alternate), StringComparison.Ordinal));
        test.Run(() => routeTool = McpTools.Call("hex_link_route",
            new JsonObject { ["device"] = Device, ["address"] = LinkRouteAddress(direct) }, CancellationToken.None));
        test.WaitFor(() => routeTool is { IsCompletedSuccessfully: true } && LinkPeers.Any(peer => peer.Session.Connection.Address == direct.Host), 13);
        test.Check("link-ui: hex_link_route reconnects directly without pairing", () =>
            routeTool is { IsCompletedSuccessfully: true, Result.IsError: false } && LinkPeers is [var routed] &&
            routed.Device.Id == identity?.Id && routed.Session.Connection.Address == direct.Host && LinkPairingPending is null);
        test.Run(() => routeTool = McpTools.Call("hex_link_route",
            new JsonObject { ["device"] = Device, ["address"] = "automatic" }, CancellationToken.None));
        test.WaitFor(() => routeTool is { IsCompletedSuccessfully: true } && LinkPeers.Count == 1, 13);
        test.Check("link-ui: Automatic clears the route pin and reconnects", () =>
            routeTool is { IsCompletedSuccessfully: true, Result.IsError: false } && LinkKnown is [{ PreferredRoute: null }] && LinkPeers.Count == 1);

        clickMore("link.device_forget");
        test.WaitFor(() => OpenDialog() is not null, 5);
        test.Settle(0.3f);
        test.Check("link-ui: Forget asks for confirmation before removing the pin", () => LinkKnown.Count == 1 && OpenDialog() is { IsOpen: true });
        test.Click(() => centre(OpenDialog()?.Buttons.FirstOrDefault(button => button.Label == Said("link.forget_go"))));
        test.WaitFor(() => LinkKnown.Count == 0, 6);
        test.Check("link-ui: confirming Forget removes the invited device and its live link", () => LinkKnown.Count == 0 && LinkPeers.Count == 0);
        test.Run(() =>
        {
            LinkPickRunsOn(null);
            linkWindow?.Close();
            foreach (HexLinkSession session in sessions)
                session.Dispose();
            secondRoute?.Stop();
            other?.Dispose();
            identity?.Dispose();
            linkInviteCopyForTest = null;
        });
    }

    private static async Task LinkUiRelay(TcpListener second, int destination)
    {
        try
        {
            using TcpClient incoming = await second.AcceptTcpClientAsync();
            using var outgoing = new TcpClient(AddressFamily.InterNetwork);
            await outgoing.ConnectAsync(IPAddress.Loopback, destination);
            NetworkStream left = incoming.GetStream(), right = outgoing.GetStream();
            Task toPeer = left.CopyToAsync(right), fromPeer = right.CopyToAsync(left);
            await await Task.WhenAny(toPeer, fromPeer);
        }
        catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException)
        {
            Console.WriteLine($"[Hex self-test] link-ui alternate route closed: {error.Message}");
        }
    }
}
