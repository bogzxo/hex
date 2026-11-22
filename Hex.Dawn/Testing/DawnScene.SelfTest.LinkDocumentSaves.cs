using System.Globalization;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private const string LINK_DOC_RELAY = "HEX_LINK_DOCUMENT_TEST_RELAY";
    private const string LINK_DOC_HEADLESS_FILE = "headless.txt";
    private const string LINK_DOC_HEADLESS_WIRE = "shared/" + LINK_DOC_HEADLESS_FILE;
    private int linkDocSaveStage;

    private void LinkDocumentSaveTestPeer(string work, string folder, string role, string hostId, HexLinkPeer? host)
    {
        bool Flag(string name) => File.Exists(Path.Combine(work, name));
        void Signal(string name, string contents = "") => File.WriteAllText(Path.Combine(work, name), contents);

        if (role == "editor" && Flag("prepare-relay") && !Flag("relay-address"))
        {
            LinkOpenPairing(TimeSpan.FromMinutes(3));
            Signal("relay-address", FormattableString.Invariant($"{LinkPort}|{linkServices!.Device.Id}"));
        }
        if (role == "editor" && LinkPairingPending is { IsDecided: false } pending)
            pending.Decide(HexLinkRights.View | HexLinkRights.Edit | HexLinkRights.Steer);

        if (role == "editor" && Flag("hold-relay-route") && !Flag("relay-route-held") &&
            LinkPeers.FirstOrDefault(peer => peer.Device.Id != hostId) is { } relayed)
        {
            linkHeldOff.Add(relayed.Device.Id);
            Signal("relay-route-held");
        }

        string path = Path.Combine(folder, LINK_DOC_HEADLESS_FILE);
        if (linkDocSaveStage == 0)
        {
            if (Open(path))
                linkDocSaveStage++;
            return;
        }

        if (LinkLiveOf(document) is not { Draft: null, Wire: LINK_DOC_HEADLESS_WIRE } shared)
            return;
        void When(string name, Func<bool> condition)
        {
            if (!Flag(name) && condition())
                Signal(name);
        }
        bool CleanAt(string text) => document.Editor.Text == text && document.SavedCode == text && !HasChanges(document)
            && shared.DiskSave is not null;

        When("headless-joined", () => shared.Room is null && !shared.Offline && (role != "reader" || document.Editor.ReadOnly));
        if (role == "editor" && linkDocSaveStage == 1 && Flag("change-direct"))
        {
            Rewrite(document, "direct\nlast\n");
            linkDocSaveStage++;
        }
        When("direct-seen", () => document.Editor.Text == "direct\nlast\n" && shared.Replica.View.Text == "direct\nlast\n");
        if (role == "editor" && linkDocSaveStage == 2 && Flag("save-direct"))
        {
            SaveTo(path);
            linkDocSaveStage++;
        }
        When("direct-clean", () => CleanAt("direct\nlast\n"));
        if (role == "relay" && linkDocSaveStage == 1 && Flag("change-relay"))
        {
            Rewrite(document, "relayed\nlast\n");
            linkDocSaveStage++;
        }
        When("relay-seen", () => document.Editor.Text == "relayed\nlast\n" && shared.Replica.View.Text == "relayed\nlast\n");
        if (role == "editor" && linkDocSaveStage == 3 && Flag("hold-newer"))
        {
            shared.HoldEditsForTest = true;
            shared.TestBeforeDiskSave = () =>
            {
                shared.TestBeforeDiskSave = null;
                Rewrite(shared.Tab, "later\nlast\n");
                Signal("newer-typed");
            };
            Signal("newer-armed");
            linkDocSaveStage++;
        }
        if (role == "relay" && linkDocSaveStage == 2 && Flag("save-relay"))
        {
            SaveTo(path);
            linkDocSaveStage++;
        }
        When("relay-clean", () => CleanAt("relayed\nlast\n"));
        When("newer-dirty", () => document.Editor.Text == "later\nlast\n" && document.SavedCode == "relayed\nlast\n" && HasChanges(document));
        if (role == "relay" && linkDocSaveStage == 3 && Flag("go-offline") && host is not null)
        {
            LinkDisconnect(hostId);
            linkDocSaveStage++;
        }
        When("relay-offline", () => role == "relay" && linkDocSaveStage == 4 && shared.Offline && host is null);
        if (role == "editor" && linkDocSaveStage == 4 && Flag("save-later"))
        {
            shared.HoldEditsForTest = false;
            SaveTo(path);
            linkDocSaveStage++;
        }
        if (role == "relay" && linkDocSaveStage == 4 && Flag("reconnect-relay") &&
            TryLinkAddress(System.Environment.GetEnvironmentVariable(LINK_TEST_DIAL)!, HexLinkTransport.DEFAULT_PORT, out string address, out int port))
        {
            _ = LinkConnect(address, port, hostId);
            linkDocSaveStage++;
        }
        When("later-clean", () => CleanAt("later\nlast\n"));
    }

    private void TestLinkDocumentSave(HexPointerScript test, string folder, string editorWork, string readerWork, string relayHome)
    {
        string relayFolder = Path.Combine(relayHome, "shared"), relayWork = Path.Combine(relayHome, LINK_PEER_FOLDER);
        string ownerFile = Path.Combine(folder, LINK_DOC_HEADLESS_FILE);
        string[] replicas = [editorWork, readerWork, relayWork];
        string[] roots =
        [
            Path.GetDirectoryName(editorWork)!, Path.GetDirectoryName(readerWork)!, relayHome
        ];
        string ReplicaPath(string home) => Path.Combine(home, "shared", LINK_DOC_HEADLESS_FILE);
        bool Flag(string work, string name) => File.Exists(Path.Combine(work, name));
        bool All(string name) => replicas.All(work => Flag(work, name));
        void Signal(string work, string name) => File.WriteAllText(Path.Combine(work, name), "");
        bool Disk(string line) => ReadLinkTestFile(ownerFile) == line + "\nlast\n" &&
            roots.All(home => ReadLinkTestFile(ReplicaPath(home)) == line + "\r\nlast\r\n");
        void Await(string label, Func<bool> check)
        {
            test.WaitFor(check, 22);
            test.Check("link-documents: " + label, check);
        }

        test.Run(() =>
        {
            Signal(editorWork, "headless-open");
            Signal(editorWork, "prepare-relay");
            Signal(readerWork, "headless-open");
        });
        Await("direct editor and View-only tab joined a room without an authority tab", () =>
            Flag(editorWork, "headless-joined") && Flag(readerWork, "headless-joined") && Context.Documents.Find(ownerFile) is null);
        test.WaitFor(() => Flag(editorWork, "relay-address"), 22);
        test.Run(() =>
        {
            string[] listener = ReadLinkTestFile(Path.Combine(editorWork, "relay-address")).Split('|');
            if (listener.Length != 2 || !int.TryParse(listener[0], CultureInfo.InvariantCulture, out int port))
                return;
            Directory.CreateDirectory(relayWork);
            Signal(relayWork, "headless-open");
            LinkStartPeer(relayHome, $"127.0.0.1:{port}", new Dictionary<string, string>
            {
                [LINK_DOC_TEST] = "relay", [LINK_DOC_OWNER] = linkServices!.Device.Id, [LINK_DOC_RELAY] = listener[1],
                [LINK_TEST_FOLDER] = relayFolder, [LINK_TEST_SECONDS] = "420"
            }, additional: true);
        });
        Await("a fourth Hex sees the headless room through the direct editor alone", () => All("headless-joined") && LinkPeers.Count == 2);
        test.Run(() => Signal(editorWork, "change-direct"));
        Await("direct, read-only, and relayed buffers agree before saving", () => All("direct-seen") && Disk("overlap"));
        test.Run(() => Signal(editorWork, "save-direct"));
        Await("one direct save cleans all three tabs and keeps each disk's line endings", () => All("direct-clean") && Disk("direct"));

        test.Run(() => Signal(relayWork, "change-relay"));
        Await("all three buffers receive the relayed edit", () => All("relay-seen"));
        test.Run(() => Signal(editorWork, "hold-newer"));
        test.WaitFor(() => Flag(editorWork, "newer-armed"), 22);
        test.Run(() => Signal(relayWork, "save-relay"));
        Await("one relayed save updates all disks and keeps the newer direct edit dirty", () =>
            Disk("relayed") && Flag(editorWork, "newer-typed") && Flag(editorWork, "newer-dirty") && Flag(readerWork, "relay-clean") && Flag(relayWork, "relay-clean"));

        test.Run(() => Signal(editorWork, "hold-relay-route"));
        test.WaitFor(() => Flag(editorWork, "relay-route-held"), 22);
        test.Run(() => Signal(relayWork, "go-offline"));
        Await("the relayed editor is offline", () => Flag(relayWork, "relay-offline"));
        test.Run(() => Signal(editorWork, "save-later"));
        Await("online replicas save the newer edit while the offline disk stays at the prior revision", () =>
            Flag(editorWork, "later-clean") && Flag(readerWork, "later-clean") &&
            ReadLinkTestFile(ReplicaPath(relayHome)) == "relayed\r\nlast\r\n");
        test.Run(() => Signal(relayWork, "reconnect-relay"));
        Await("the offline tab catches up clean on reconnect without a conflict copy", () =>
            All("later-clean") && Disk("later") && Context.Documents.Find(ownerFile) is null &&
            !roots.Append(Path.GetDirectoryName(folder)!).Any(home => Directory.EnumerateFiles(Path.Combine(home, "shared"))
                .Any(file => Path.GetFileName(file).Contains(HexLinkSync.THEIRS, StringComparison.Ordinal))));
    }
}
