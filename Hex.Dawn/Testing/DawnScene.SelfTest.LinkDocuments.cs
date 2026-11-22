using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private const string LINK_DOC_TEST = "HEX_LINK_DOCUMENT_TEST";
    private const string LINK_DOC_WIRE = "shared/note.txt";
    private int linkDocTestStage;

    private void LinkDocumentTestPeer(string work)
    {
        if (System.Environment.GetEnvironmentVariable(LINK_DOC_TEST) != "1" || LinkPeers.FirstOrDefault() is not { } peer)
            return;
        bool Flag(string name) => File.Exists(Path.Combine(work, name));
        void Signal(string name) => File.WriteAllText(Path.Combine(work, name), "");
        var shared = linkDocument;
        switch (linkDocTestStage)
        {
            case 0 when Flag("host"):
                LinkLegacyDocumentJoin(peer, LINK_DOC_WIRE);
                break;
            case 1 when shared is { Busy: false } && shared.Tab.Editor.Text == "seed":
                document.Editor.GoTo(0, 2);
                compositor.PointerSource = () => new(document.Module.ToWorld(document.Editor.PlaceOf(new UITextPosition(0, 3))), false);
                shared.Wait = 3600;
                Rewrite(document, "seedB");
                document.Editor.GoTo(0, 2);
                Signal("draft1");
                break;
            case 2 when shared != null && Flag("release1"):
                if (document.Editor.RemoteCarets.Any(c => c.Column == 1 && c.AnchorColumn == 0 && c.PointerColumn == 3))
                    Signal("host-presence");
                shared.Wait = 0;
                break;
            case 3 when shared is { Busy: false } && shared.Tab.Editor.Text == "AseedB":
                shared.Wait = 3600;
                document.Editor.Document.Undo();
                if (document.Editor.Text == "Aseed") Signal("undo-preserved-host");
                document.Editor.Document.Redo();
                Rewrite(document, "AseedBy");
                Signal("draft2");
                break;
            case 4 when shared != null && Flag("release2"):
                shared.Wait = 0;
                break;
            case 5 when shared is { Busy: false } && shared.Tab.Editor.Text == "AseedBxy":
                shared.Wait = 3600;
                Rewrite(document, "AguestBxy");
                Signal("draft3");
                break;
            case 6 when shared != null && Flag("release3"):
                shared.Wait = 0;
                break;
            case 7 when shared is { Conflict: true } && shared.Tab.Editor.Text == "AguestBxy":
                Signal("conflict");
                break;
            case 8 when shared != null && Flag("recover"):
                shared.Conflict = false;
                break;
            case 9 when shared == null && document.Editor.Text == "AguestBxy":
                Signal("ended");
                break;
            default: return;
        }
        linkDocTestStage++;
    }

    private void TestLinkDocuments(HexPointerScript test)
    {
        string home = LinkTestFolder("hex-link-doc-a-", test), peerHome = LinkTestFolder("hex-link-doc-b-", test);
        string folder = Path.Combine(home, "shared"), path = Path.Combine(folder, "note.txt");
        string work = Path.Combine(peerHome, LINK_PEER_FOLDER);
        bool Flag(string name) => File.Exists(Path.Combine(work, name));
        void Signal(string name) => File.WriteAllText(Path.Combine(work, name), "");
        void Await(string label, Func<bool> check)
        {
            test.WaitFor(check, 20);
            test.Check("link-documents: " + label, check);
        }
        test.Run(() =>
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, "disk");
            StopLink();
            linkTestExtraFolders = [folder];
            if (!StartLink(port: 0, home: home, loopbackOnly: true)) return;
            linkByTest = true;
            LinkOpenPairing(TimeSpan.FromMinutes(3));
            LinkStartPeer(peerHome, $"127.0.0.1:{LinkPort}", new Dictionary<string,string> { [LINK_DOC_TEST] = "1", [LINK_TEST_SECONDS] = "360" });
        });
        test.WaitFor(() => LinkPairingPending != null, 90);
        test.Run(() => LinkPairingPending?.Decide(HexLinkRights.View | HexLinkRights.Edit));
        Await("paired on isolated loopback", () => LinkTestBoundOnlyToLoopback && LinkPeers.Count == 1);
        test.Run(() =>
        {
            Open(path);
            Rewrite(document, "seed");
            LinkDocumentHost(document, LINK_DOC_WIRE);
            Signal("host");
        });
        Await("guest joined unsaved text and typed before host update", () => Flag("draft1"));
        Await("guest caret and pointer independently positioned", () => document.Editor.RemoteCarets.Any(c =>
            c.Column == 2 && c.PointerColumn == 3));
        test.Run(() =>
        {
            Rewrite(document, "Aseed");
            document.Editor.Document.Select(new(0, 0), new(0, 1));
        });
        test.Hover(() => document.Module.ToWorld(document.Editor.PlaceOf(new UITextPosition(0, 3))));
        test.Settle(1);
        Await("host committed before guest released its concurrent edit", () => linkDocument?.Base.Text == "Aseed");
        test.Run(() => Signal("release1"));
        Await("both editors converge on disjoint changes", () => Flag("draft2") && document.Editor.Text == "AseedB");
        test.Check("link-documents: guest saw host selection, caret and pointer independently", () => Flag("host-presence"));
        test.Check("link-documents: guest Undo preserved the concurrent host insertion", () => Flag("undo-preserved-host"));
        test.Run(() => Rewrite(document, "AseedBx"));
        Await("host insertion committed at shared offset", () => linkDocument?.Base.Text == "AseedBx");
        test.Run(() => Signal("release2"));
        Await("both editors see same-offset insertions in acceptance order", () => Flag("draft3") && document.Editor.Text == "AseedBxy");
        test.Run(() => Rewrite(document, "AhostBxy"));
        Await("host replacement committed", () => linkDocument?.Base.Text == "AhostBxy");
        test.Run(() => Signal("release3"));
        Await("overlapping guest draft preserved and host retained its edit", () => Flag("conflict") && document.Editor.Text == "AhostBxy");
        test.Run(() => Signal("recover"));
        Await("guest explicitly resumed with its draft", () => document.Editor.Text == "AguestBxy");
        test.Check("link-documents: no automatic disk writes or edit echoes", () => File.ReadAllText(path) == "disk" && linkDocument?.Base.Revision == 6);
        test.Run(LinkDocumentStop);
        Await("guest detached with its final text after host ended session", () => Flag("ended"));
        test.Run(() =>
        {
            if (LinkPeers.FirstOrDefault() is { } peer) LinkForget(peer.Device.Id);
            linkTestExtraFolders = [];
        });
        test.WaitFor(() => linkTestPeer is null or { HasExited: true }, 60);
    }
}
