using System.Numerics;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestLinkHeldChanges(HexPointerScript test)
    {
        string home = LinkTestFolder("hex-link-hold-a-", test);
        string peerHome = LinkTestFolder("hex-link-hold-b-", test);
        string mine = Path.Combine(home, LINK_SYNC_FOLDER), theirs = Path.Combine(peerHome, LINK_SYNC_FOLDER);
        string file = Path.Combine(mine, "src", "shared.txt"), peerFile = Path.Combine(theirs, "src", "shared.txt");
        const string wire = LINK_SYNC_FOLDER + "/src/shared.txt";
        const string baseline = "one\ntwo\nthree\n", local = "local\ntwo\nthree\n";
        const string remote = "one\ntwo\nremote\n", updated = "local\ntwo\nremote\n";
        const string competing = "peer\ntwo\nremote\n", resolved = "resolved\ntwo\nremote\n";
        HexLinkPeer? peer = null;
        HexDocument? heldTab = null;
        HexLinkFolder? folder = null;
        Task<string?>? starting = null;
        Task<McpToolResult>? listing = null, command = null;
        Task? flushing = null;
        bool made = false;

        test.Run(() =>
        {
            made = LinkTestRepository(mine, theirs) is null;
            if (!made)
                return;
            File.WriteAllText(file, baseline);
            File.WriteAllText(peerFile, baseline);
            StopLink();
            linkTestExtraFolders = [mine];
            if (!StartLink(port: 0, home: Path.Combine(home, "link"), loopbackOnly: true))
                return;
            linkByTest = true;
            linkLivePaused.Add(wire);
            LinkOpenPairing(TimeSpan.FromMinutes(3));
            LinkStartPeer(peerHome, $"127.0.0.1:{LinkPort}", new Dictionary<string, string>
            {
                [LINK_TEST_FOLDER] = theirs, [LINK_TEST_SECONDS] = "280",
            });
        });
        Check("the two private workspaces start", () => made && linkTestPeer is not null, 3);
        test.WaitFor(() => LinkPairingPending is not null, 20);
        test.Run(() => LinkPairingPending?.Decide(HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit));
        Check("the peer shares the fixture folder", () => LinkPeers.Any(device =>
            device.Workspace.Folders.Any(each => each.Name == LINK_SYNC_FOLDER)), 20);
        test.Run(() =>
        {
            peer = LinkPeers.FirstOrDefault();
            folder = linkServices?.Workspace.Folders.FirstOrDefault(each => each.Name == LINK_SYNC_FOLDER);
            if (peer is not null && folder is not null)
                starting = LinkSyncStart(peer.Device.Id, folder.Name);
        });
        Check("the shared baseline is synced", () => starting is { IsCompletedSuccessfully: true, Result: null } &&
            Sync() is { State: HexLinkSyncState.Idle } && ReadLinkTestFile(peerFile) == baseline, 20);

        test.Run(() =>
        {
            Open(file);
            heldTab = document;
            command = McpTools.Call("hex_run_command", new JsonObject { ["name"] = "Hex Link: Hold changes to this file" }, CancellationToken.None);
        });
        Check("the palette command holds the file and shows the held tooltip", () =>
            command is { IsCompletedSuccessfully: true, Result.IsError: false } &&
            LinkHeldFile(file) is { BaseSource: HexLinkHoldBaseSource.Agreed } &&
            docTabs.Tabs.Any(tab => tab.Tag == heldTab && tab.Tooltip.Contains(HELD_TIP, StringComparison.Ordinal)), 8);
        test.Run(() =>
        {
            Rewrite(heldTab!, local);
            Save();
            flushing = Sync()?.Flush();
            listing = McpTools.Call("hex_link_hold", new JsonObject { ["action"] = "list", ["folder"] = LINK_SYNC_FOLDER }, CancellationToken.None);
        });
        Check("a normal save stays on the holder after flush", () => flushing is { IsCompletedSuccessfully: true } &&
            ReadLinkTestFile(file) == local && ReadLinkTestFile(peerFile) == baseline, 10);
        Check("the local hold tool returns the path and state", () => listing is { IsCompletedSuccessfully: true, Result.IsError: false } &&
            JsonNode.Parse(listing.Result.AllText())?["held"]?[0]?["state"]?.GetValue<string>() == "Held", 5);

        test.Run(() => File.WriteAllText(peerFile, remote));
        Check("the incoming notice and tooltip appear without changing the open tab", () =>
            LinkHeldFile(file) is { HasIncoming: true, NoticeShown: true } && heldTab?.Editor.Text == local &&
            docTabs.Tabs.Any(tab => tab.Tag == heldTab && tab.Tooltip.Contains("Others changed it", StringComparison.Ordinal)) &&
            toasts.Cards.Any(card => card.Text.Contains("changed shared.txt, which you hold", StringComparison.Ordinal)), 15);
        test.Run(() => PreviewLinkUpdate(folder!, [wire]));
        Check("Update from others merges cleanly and keeps the new base held", () =>
            LinkHeldFile(file) is { HasIncoming: false } && heldTab?.Editor.Text == updated && ReadLinkTestFile(file) == updated &&
            ReadLinkTestFile(peerFile) == remote, 10);

        test.Run(() => File.WriteAllText(peerFile, competing));
        Check("the second incoming edit conflicts with the held edit", () =>
            LinkHeldFile(file) is { HasIncoming: true } && ReadLinkTestFile(file) == updated, 15);
        test.Run(() => command = McpTools.Call("hex_run_command", new JsonObject
        {
            ["name"] = "Hex Link: Submit held changes…",
        }, CancellationToken.None));
        Check("the Submit held changes palette command previews the conflict", () =>
            command is { IsCompletedSuccessfully: true, Result.IsError: false } && Choice("Submit held changes") is not null, 8);
        test.Settle(0.3f);
        test.Click(() => Center(Choice("Submit held changes")));
        Check("Submit held changes opens the merge editor with one unresolved conflict", () =>
            document.Merge is { UnresolvedCount: 1 } && LinkHeldFile(file) is not null, 8);
        test.Run(() =>
        {
            if (document.Merge is not null)
            {
                ResolveByTyping("local", "resolved");
                CompleteMergeEditor(document);
            }
        });
        Check("Complete merge shares the exact final result and releases the hold", () =>
            LinkHeldFile(file) is null && ReadLinkTestFile(file) == resolved && ReadLinkTestFile(peerFile) == resolved, 15);

        test.Run(() =>
        {
            Open(file);
            var menu = new Menu("test_hold_again");
            AddLinkHoldMenu(menu, file);
            menu.Items.Single(item => item.Label == "Hold changes").OnPicked?.Invoke();
        });
        Check("a new hold starts from the submitted version", () => LinkHeldFile(file) is not null, 8);
        test.Run(() =>
        {
            Rewrite(document, "discard me\n");
            Save();
            ConfirmLinkDiscard(folder!, [wire]);
        });
        test.WaitFor(() => Choice("Discard held changes") is not null, 8);
        test.Settle(0.3f);
        test.Click(() => Center(Choice("Discard held changes")));
        Check("Discard held changes restores the shared text and saves a local backup", () => LinkHeldFile(file) is null &&
            ReadLinkTestFile(file) == resolved && ReadLinkTestFile(peerFile) == resolved && document.Editor.Text == resolved &&
            folder is not null && linkServices?.Holds(folder) is { } store &&
            Directory.EnumerateFiles(Path.Combine(store.BlobDirectory, "discarded"), "shared.txt", SearchOption.AllDirectories)
                .Any(path => ReadLinkTestFile(path) == "discard me\n"), 10);

        string gatedRoot = Path.Combine(home, "gate-fixture");
        string gatedFile = Path.Combine(gatedRoot, "typed.txt");
        const string gatedWire = "gate-fixture/typed.txt", typed = "hand resolved after the check\n";
        HexLinkHoldStore? gatedStore = null;
        test.Run(() =>
        {
            Directory.CreateDirectory(gatedRoot);
            File.WriteAllText(gatedFile, "mine\n");
            var gated = new HexLinkFolder("gate-fixture", gatedRoot)
            {
                WorktreeGate = new("exit 19", null, null, null, 10),
            };
            gatedStore = linkServices!.Holds(gated);
            gatedStore.Hold(gatedWire, "base\n"u8.ToArray(), HexLinkHoldBaseSource.Agreed);
            gatedStore.SetIncoming(gatedWire, "theirs\n"u8.ToArray(), "Fixture peer");
            PreviewLinkSubmit(gated, [gatedWire]);
        });
        Check("the pre-publish check is enabled in the submit preview", () =>
            chrome.Dialog?.Body.Children.OfType<ToggleButton>().Any(toggle => toggle.State) == true, 8);
        test.Settle(0.3f);
        test.Click(() => Center(Choice("Submit held changes")));
        test.WaitFor(() => document.Merge is { UnresolvedCount: 1 }, 8);
        test.Run(() =>
        {
            if (document.Merge is not null)
            {
                ResolveByTyping("mine", typed.TrimEnd('\n'));
                CompleteMergeEditor(document);
            }
        });
        Check("a failed check keeps the typed Result and restores the held file", () =>
            document.Merge is { Busy: true } && code.Text == typed &&
            Choice("Submit anyway") is not null && gatedStore?.IsHeld(gatedWire) == true &&
            ReadLinkTestFile(gatedFile) == "mine\n", 10);
        test.Settle(0.3f);
        test.Click(() => Center(Choice("Submit anyway")));
        Check("Submit anyway shares the exact typed resolution without another edit", () =>
            document.Merge is null && gatedStore?.IsHeld(gatedWire) == false && ReadLinkTestFile(gatedFile) == typed, 10);

        test.Run(() =>
        {
            if (peer is not null)
                LinkForget(peer.Device.Id);
            linkLivePaused.Remove(wire);
            linkTestExtraFolders = [];
            StopLink();
        });

        void ResolveByTyping(string old, string text)
        {
            int line = Enumerable.Range(0, code.Document.LineCount).First(i => code.Document.LineAt(i) == old);
            code.Document.Select(new(line, 0), new(line, old.Length));
            code.Document.Insert(text);
        }

        HexLinkSync? Sync() => peer is not null && folder is not null ? LinkSyncOf(peer.Device.Id, folder.Name) : null;
        Button? Choice(string label) => chrome.Dialog?.Buttons.FirstOrDefault(button => button.Label == label);
        static Vector2 Center(Button? button) => button?.Module is { } module ? module.ToWorld(button.Bounds.Center) : Vector2.Zero;
        void Check(string message, Func<bool> check, float timeout)
        {
            test.WaitFor(check, timeout);
            test.Check("link-hold: " + message, () =>
            {
                if (check())
                    return true;
                Console.WriteLine($"[Hex self-test] link-hold diagnostic: dialog={string.Join(",", chrome.Dialog?.Buttons.Select(button => button.Label) ?? [])}; status={messageItem.Text}; list={listing?.Status} {listing?.Exception?.GetBaseException().Message}");
                return false;
            });
        }
    }
}
