using System.Numerics;

using Horizon.UI;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    // Uses the actual claims board and shared-document roster with local fixture data. No additional editor process.
    private void TestLinkEditorNotices(HexPointerScript test)
    {
        string home = LinkTestFolder("hex-link-editor-notices-", test);
        string folderPath = Path.Combine(home, "notes"), path = Path.Combine(folderPath, "build-notes.txt");
        const string wire = "notes/build-notes.txt";
        const string source = "Build notes\n\nRun the unit tests before handing off the workspace.\nCheck the warning output and share the results.\n";
        var remote = new HexLinkDevice(new string('B', 64), "North laptop");
        var worker = new HexLinkDevice(new string('C', 64), "Build worker");
        LinkLiveDocument? preview = null;
        HexLinkClaimExchange? claims = null;

        void Shot(string name)
        {
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_SHOTS") is not { Length: > 0 } shots)
                return;
            string file = Path.Combine(shots, name);
            test.Settle(0.2f);
            test.Run(() => Engine.CaptureScreenshot(file));
            test.WaitFor(() => File.Exists(file) && new FileInfo(file).Length > 0, 8);
            test.Check("link-editor-notices: wrote " + name, () => File.Exists(file) && new FileInfo(file).Length > 0);
        }
        bool Notice(string text) => linkEditorNotices is { Visible: true } pane && Descendants(pane).OfType<Label>()
            .Any(label => label.Text.Contains(text, StringComparison.Ordinal));
        Button? NoticeAction(string key) => linkEditorNotices is { Visible: true } pane ? Descendants(pane).OfType<Button>()
            .FirstOrDefault(button => button.Label == Said(key)) : null;
        Vector2 Where(Button? button) => button?.Module is { } module ? module.ToWorld(button.Bounds.Center) : Vector2.Zero;

        test.Run(() =>
        {
            Directory.CreateDirectory(folderPath);
            File.WriteAllText(path, source);
            StopLink();
            ShowFace(HexFace.Dawn);
            explorer.OpenFolder(folderPath);
            if (!StartLink(port: 0, home: Path.Combine(home, "link"), loopbackOnly: true))
                return;
            linkByTest = true;
            if (System.Environment.GetEnvironmentVariable("HEX_SELFTEST_LINK_THEME") is { Length: > 0 } theme)
                UseEditorTheme(theme, save: false);
            OpenText(path, source);
            if (linkServices is not { } services || link?.Claims is not { } exchange || LinkDevice is not { } local
                || document.Path is not { } openedPath || !PlatformPaths.Same(openedPath, path))
                return;
            var folder = new HexLinkFolder("notes", folderPath);
            var pin = services.FolderAuthorities.Bind(folder, new HexLinkAuthority(remote.Id, 1));
            services.Workspace = new HexLinkWorkspace("Link notes", [folder], []);
            linkTrust!.Add(remote, HexLinkRights.View);
            exchange.RefreshFolders();
            claims = exchange;
            HexLinkClaimScope scope = exchange.Folders.Single().Scope;
            long now = System.Environment.TickCount64;
            var draft = new HexLinkClaimDraft(new HexLinkClaimKey("build-notes.txt", false, null, HexLinkClaimKind.Editing), "Alex");
            exchange.Board.Receive(new HexLinkClaimFrame(scope, remote, "preview", 1, true, HexLinkClaims.LEASE_MS,
                [new HexLinkClaimRow(draft, now)], []), remote.Id, now);

            var room = new HexLinkDocumentRoom(wire, source);
            Hello(remote, 0, 5);
            Hello(worker, 3, 9);
            var replica = new HexLinkDocumentReplica(room.Join(local.Id, document, local) with { CanEdit = false });
            preview = new LinkLiveDocument(document, wire, pin, replica, room: null) { Busy = true };
            linkDirty = tabsDirty = true;

            void Hello(HexLinkDevice person, int line, int column)
            {
                object connection = new();
                HexDocExchange opened = room.Join(person.Id, connection, person);
                HexDocPoint caret = room.View().PointAt(room.View().OffsetAt(line, column));
                room.Exchange(person.Id, connection, opened.Replay.Epoch, opened.Replay.Head, [], caret, null, null, checkpointReady: false);
            }
        });

        test.WaitFor(() => Notice("Alex"), 8);
        test.Check("link-editor-notices: a remote claim marks the tab and displays its holder", () =>
            Notice("Alex") && docTabs.Tabs.Any(tab => tab.Tag == document && tab.Icon == "lock" && tab.Tooltip.Contains("North laptop", StringComparison.Ordinal)));
        test.Check("link-editor-notices: an unavailable shared editor shows a reason", () =>
            NoticeAction("link.devices_open_shared") is null && Notice(Said("link.devices_claim_cannot_open")));
        test.Check("link-editor-notices: the Explorer marks the claimed file", () =>
            explorer.explorerRows.Any(item => item.Path == document.Path && item.Row.Icon == "lock" &&
                item.Row.Tooltip.Contains("Claimed by Alex", StringComparison.Ordinal)));
        Shot("link_editor_claim.png");

        test.Run(() =>
        {
            if (preview is null)
                return;
            linkLiveDocuments.Add(document, preview);
            document.Editor.ReadOnly = true;
            document.Editor.RemoteCarets = LiveCarets(preview.Shown, preview.Participants, preview.Replica.Author);
            tabsDirty = true;
        });
        test.WaitFor(() => Notice(Said("link.devices_session_reader")), 8);
        test.Check("link-editor-notices: one notice gives the claim and View-only explanation with the roster in its tooltip", () =>
            Notice("North laptop") && Notice(Said("link.devices_session_reader")) &&
            linkEditorNotices is { Children.Count: 1 } merged && Descendants(merged).OfType<StackPanel>()
                .Any(row => row.Tooltip.Contains("North laptop", StringComparison.Ordinal) && row.Tooltip.Contains("Build worker", StringComparison.Ordinal)) &&
            document.Editor.ReadOnly && document.Editor.RemoteCarets.Count == 2 && NoticeAction("link.devices_open_shared") is null);
        Shot("link_editor_claim_and_reader_tags.png");
        test.Settle(2.25f);
        Shot("link_editor_claim_and_reader.png");
        test.Click(() => Where(NoticeAction("link.devices_session_follow")));
        test.Check("link-editor-notices: clicking Follow names the other participants", () =>
            chrome.Root.Children.OfType<ContextMenu>().Any(menu => menu.Showing is { } current &&
                current.Items.Any(item => item.Label == Said("link.devices_session_follow_person", ("device", remote.Name))) &&
                current.Items.Any(item => item.Label == Said("link.devices_session_follow_person", ("device", worker.Name)))));
        ContextMenu? FollowMenu() => chrome.Root.Children.OfType<ContextMenu>().FirstOrDefault(menu => menu.Showing is not null);
        string followNorth = Said("link.devices_session_follow_person", ("device", remote.Name));
        string stopFollowing = Said("link.devices_session_unfollow");
        Vector2 FollowChoice(string label) => FollowMenu() is { Showing: { } opened } menu && opened.Items.Any(item => item.Label == label)
            ? chrome.ToWorld(menu.ItemBounds(label).Center) : Vector2.Zero;
        string followingLabel = Said("link.devices_session_following", ("device", remote.Name));
        Button? FollowingButton() => linkEditorNotices is { Visible: true } pane ? Descendants(pane).OfType<Button>()
            .FirstOrDefault(button => button.Label == followingLabel) : null;
        test.Click(() => FollowChoice(followNorth));
        test.WaitFor(() => LinkFollowing == remote.Id && FollowingButton() is not null, 4);
        test.Check("link-editor-notices: Follow status and button use the participant's name", () =>
            FollowingButton() is not null && messageItem.Text == followingLabel);
        test.Click(() => Where(FollowingButton()));
        test.Check("link-editor-notices: the Follow menu marks the person being followed and can stop following", () =>
            LinkFollowing == remote.Id && FollowMenu()?.Showing is { } menu &&
            menu.Items.Any(item => item.Label == followNorth && item.IsChecked?.Invoke() == true) &&
            menu.Items.Any(item => item.Label == stopFollowing));
        Shot("link_editor_follow.png");
        test.Click(() => FollowChoice(stopFollowing));
        test.Check("link-editor-notices: Stop following releases the shared caret", () => LinkFollowing is null);

        test.Run(() =>
        {
            if (preview is not null)
                preview.Offline = true;
            document.Editor.RemoteCarets = [];
            tabsDirty = true;
            MarkLinkEditorNotices();
        });
        string offline = Said("link.devices_session_offline", ("device", remote.Name));
        test.WaitFor(() => Notice(offline), 5);
        test.Check("link-editor-notices: offline keeps text and shows Reconnect", () =>
            Notice(offline) && document.Editor.Text == source && NoticeAction("link.devices_session_follow") is null &&
            NoticeAction("link.devices_session_reconnect") is { Enabled: true });
        Shot("link_editor_offline.png");

        test.Run(() =>
        {
            linkManualDials[remote.Id] = new LinkDeviceConnection(false, DateTime.UtcNow.AddSeconds(20),
                "No listening address is known for this device.");
            ShowLinkWindowForTest();
        });
        string claimFile = Said("link.devices_one_claim", ("file", "build-notes.txt"));
        test.WaitFor(() => linkWindowDevices is { } pane && Descendants(pane).OfType<Label>().Any(label => label.Text == claimFile), 5);
        test.Check("link-editor-notices: the Devices row names the file and lists the full claim path", () =>
            linkWindowDevices is { } pane && Descendants(pane).OfType<Label>().Any(label => label.Text == claimFile && label.Tooltip == wire));
        test.Check("link-editor-notices: a retry without an address has a Join action and no double period", () =>
            linkWindowDevices is { } pane && Descendants(pane).OfType<Label>().Any(label => label.Text == Said("link.devices_no_reconnect")) &&
            Descendants(pane).OfType<Button>().Any(button => button.Label == Said("link.devices_join_from_invite") && button.Enabled) &&
            linkOfflineRows.Any(row => row.Device.Device.Id == remote.Id && row.Details.Text == Said("link.devices_retry_no_address")));
        Shot("link_device_retry_without_address.png");
        test.Run(() =>
        {
            linkWindow?.Close();
            claims?.Board.Expire(System.Environment.TickCount64 + HexLinkClaims.LEASE_MS + 1);
            linkDirty = true;
        });
        test.WaitFor(() => !Notice("Alex") && docTabs.Tabs.Where(tab => tab.Tag == document).All(tab => !tab.Tooltip.Contains("Claimed by Alex", StringComparison.Ordinal)), 5);
        test.Check("link-editor-notices: expiry clears both the banner and the tab mark", () =>
            !Notice("Alex") && docTabs.Tabs.Where(tab => tab.Tag == document).All(tab => !tab.Tooltip.Contains("Claimed by Alex", StringComparison.Ordinal)));
        test.Run(StopLink);
    }
}
