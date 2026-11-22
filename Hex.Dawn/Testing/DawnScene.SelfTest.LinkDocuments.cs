using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private const string LINK_DOC_TEST = "HEX_LINK_DOCUMENT_TEST";
    private const string LINK_DOC_WIRE = "shared/note.txt";
    private int linkDocTestStage, linkDocLatencySample;

    private void LinkDocumentTestPeer(string work)
    {
        string? role = System.Environment.GetEnvironmentVariable(LINK_DOC_TEST);
        if (role is not ("editor" or "reader") || LinkPeers.FirstOrDefault() is null)
            return;
        string? folder = System.Environment.GetEnvironmentVariable(LINK_TEST_FOLDER);
        if (folder is null)
            return;
        bool Flag(string name) => File.Exists(Path.Combine(work, name));
        void Signal(string name, string contents = "") => File.WriteAllText(Path.Combine(work, name), contents);
        if (linkDocTestStage == 0 && Flag("open"))
        {
            if (Open(Path.Combine(folder, "note.txt")))
                linkDocTestStage = 1;
            return;
        }

        if (LinkLiveOf(document) is not { } shared || shared.Draft is not null)
            return;

        if (role == "reader")
        {
            if (document.Editor.ReadOnly && document.Editor.Text == "YX")
                Signal("read");
            if (document.Editor.Text == "named" && document.Editor.RemoteCarets.Any(caret => caret.Label == "Agent Fixture"))
                Signal("agent");
            return;
        }

        if (linkDocTestStage == 1 && document.Editor.Text == "seed")
        {
            document.Editor.Document.Select(new(0, 0), new(0, 2));
            Signal("joined");
            linkDocTestStage++;
        }

        for (int sample = linkDocLatencySample + 1; sample <= 4; sample++)
        {
            if (document.Editor.Text != $"seed{sample}")
                break;
            Signal($"latency{sample}", Stopwatch.GetTimestamp().ToString(CultureInfo.InvariantCulture));
            linkDocLatencySample = sample;
        }

        if (linkDocTestStage == 2 && Flag("overlap") && document.Editor.Text == "seed")
        {
            shared.Wait = 3600;
            Rewrite(document, "Y");
            Signal("draft");
            linkDocTestStage++;
        }
        if (linkDocTestStage == 3 && Flag("release"))
        {
            shared.Wait = 0;
            linkDocTestStage++;
        }
        if (linkDocTestStage == 4 && document.Editor.Text == "YX")
        {
            Signal("overlap_seen");
            linkDocTestStage++;
        }
        if (linkDocTestStage == 5 && document.Editor.Text == "Y")
        {
            Signal("undo");
            linkDocTestStage++;
        }
        if (linkDocTestStage == 6 && Flag("save") && document.Editor.Text == "named")
        {
            SaveTo(document.Path!);
            linkDocTestStage++;
        }
    }

    private void TestLinkDocuments(HexPointerScript test)
    {
        string home = LinkTestFolder("hex-link-doc-a-", test);
        string peerHome = LinkTestFolder("hex-link-doc-b-", test);
        string readerHome = LinkTestFolder("hex-link-doc-c-", test);
        string folder = Path.Combine(home, "shared"), path = Path.Combine(folder, "note.txt");
        string peerFolder = Path.Combine(peerHome, "shared"), readerFolder = Path.Combine(readerHome, "shared");
        string peerWork = Path.Combine(peerHome, LINK_PEER_FOLDER), readerWork = Path.Combine(readerHome, LINK_PEER_FOLDER);
        string[] files = [path, Path.Combine(peerFolder, "note.txt"), Path.Combine(readerFolder, "note.txt")];
        long[] sent = new long[4];
        HexAgentChat? named = null;
        Task<McpToolResult>? agentEdit = null;
        Task<string?>? syncing = null;
        bool SyncReady(HexLinkPeer peer) => LinkSyncOf(peer.Device.Id, "shared")?.State is HexLinkSyncState.Idle or HexLinkSyncState.PullOnly;
        bool Flag(string work, string name) => File.Exists(Path.Combine(work, name));
        void Signal(string work, string name) => File.WriteAllText(Path.Combine(work, name), "");
        void Await(string label, Func<bool> check)
        {
            test.WaitFor(check, 30);
            test.Check("link-documents: " + label, check);
        }
        void Child(string profile, string workspace, string role, bool additional = false) =>
            LinkStartPeer(profile, $"127.0.0.1:{LinkPort}", new Dictionary<string, string>
            {
                [LINK_DOC_TEST] = role, [LINK_TEST_FOLDER] = workspace, [LINK_TEST_SECONDS] = "420"
            }, additional: additional);

        test.Run(() =>
        {
            foreach (string file in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "disk");
            }
            StopLink();
            linkTestExtraFolders = [folder];
            if (!StartLink(port: 0, home: home, loopbackOnly: true) || linkServices is not { } services)
                return;
            linkByTest = true;
            services.FolderAuthorities.Bind(new HexLinkFolder("shared", folder), new HexLinkAuthority(services.Device.Id, 1));
            LinkOpenPairing(TimeSpan.FromMinutes(3));
            Child(peerHome, peerFolder, "editor");
        });
        test.WaitFor(() => LinkPairingPending is { IsDecided: false }, 90);
        test.Run(() => LinkPairingPending?.Decide(HexLinkRights.View | HexLinkRights.Edit));
        Await("two devices paired on isolated loopback", () => LinkTestBoundOnlyToLoopback && LinkPeers.Count == 1);
        test.Run(() =>
        {
            if (LinkPeers.SingleOrDefault() is { } editor)
                syncing = LinkSyncStart(editor.Device.Id, "shared", remember: false);
        });
        Await("the editor's plain folder is synced", () =>
            syncing is { IsCompletedSuccessfully: true, Result: null } && LinkPeers.SingleOrDefault() is { } editor && SyncReady(editor));
        test.Run(() =>
        {
            Open(path);
            Rewrite(document, "seed");
            Signal(peerWork, "open");
        });
        Await("a synced tab joined its owner's unsaved buffer automatically", () => Flag(peerWork, "joined") && LinkLiveOf(document) is not null);
        Await("caret and selection arrived from B", () => document.Editor.RemoteCarets.Any(c => c.Column == 2 && c.AnchorColumn == 0));

        for (int sample = 1; sample <= sent.Length; sample++)
        {
            int number = sample;
            test.Run(() =>
            {
                document.Editor.Focus();
                document.Editor.Document.Select(new(0, 4), new(0, document.Editor.Text.Length));
                sent[number - 1] = Stopwatch.GetTimestamp();
                Engine.Input.Post(Horizon.Input.InputEvent.Typed(number.ToString(CultureInfo.InvariantCulture)));
            });
            Await($"live edit {number} reached the second editor", () => Flag(peerWork, $"latency{number}"));
        }
        test.Run(() =>
        {
            var elapsed = new List<double>();
            for (int i = 0; i < sent.Length; i++)
            {
                string flag = Path.Combine(peerWork, $"latency{i + 1}");
                if (File.Exists(flag) && long.TryParse(File.ReadAllText(flag), CultureInfo.InvariantCulture, out long observed))
                    elapsed.Add(Stopwatch.GetElapsedTime(sent[i], observed).TotalMilliseconds);
            }
            if (elapsed.Count > 0)
                Console.WriteLine($"[Hex self-test] link-documents loopback edit to remote display: {string.Join(", ", elapsed.Select(ms => $"{ms:F1}"))} ms");
            Rewrite(document, "seed");
            Signal(peerWork, "overlap");
        });
        Await("B staged an overlapping replacement", () => Flag(peerWork, "draft"));
        test.Run(() => Rewrite(document, "X"));
        Await("A's authority accepted its replacement before B sent", () => LinkLiveOf(document)?.Replica.View.Text == "X");
        test.Run(() => Signal(peerWork, "release"));
        Await("two editors converged after overlapping replacements", () => Flag(peerWork, "overlap_seen") && document.Editor.Text == "YX");
        test.Check("link-documents: unsaved edits stayed in the shared buffer", () => files.All(file => ReadLinkTestFile(file) == "disk"));

        test.Run(() =>
        {
            LinkOpenPairing(TimeSpan.FromMinutes(3));
            Child(readerHome, readerFolder, "reader", additional: true);
        });
        test.WaitFor(() => LinkPairingPending is { IsDecided: false }, 90);
        test.Run(() => LinkPairingPending?.Decide(HexLinkRights.View));
        Await("a third device joined the link as a reader", () => LinkPeers.Count == 2);
        test.Run(() =>
        {
            if (LinkPeers.FirstOrDefault(peer => !HexLinkAllowed.Has(peer.Session.Connection.Rights, HexLinkRights.Edit)) is { } reader)
                syncing = LinkSyncStart(reader.Device.Id, "shared", remember: false);
        });
        Await("the reader's plain folder is synced", () =>
            syncing is { IsCompletedSuccessfully: true, Result: null } && LinkPeers.Count(SyncReady) == 2);
        test.Run(() => Signal(readerWork, "open"));
        Await("the third editor received the same buffer read-only", () => Flag(readerWork, "read") && LinkLiveOf(document)?.Participants.Count == 3);

        test.Run(() => document.Editor.Document.Undo());
        Await("A's undo kept the overlapping text from B", () => Flag(peerWork, "undo") && document.Editor.Text == "Y");
        test.Run(() =>
        {
            named = new HexAgentChat(++agentChatsMade, "codex") { Name = "Fixture" };
            agentChats.Add(named);
            agentEdit = McpTools.CallAs("hex_edit", new JsonObject
            {
                ["path"] = path, ["old_string"] = "Y", ["new_string"] = "named", ["check"] = false
            }, default, caller: named.Id);
        });
        Await("all editors saw the named agent's live edit and caret", () =>
            agentEdit is { IsCompletedSuccessfully: true, Result.IsError: false } && document.Editor.Text == "named" && Flag(readerWork, "agent"));
        test.Run(() => Signal(peerWork, "save"));
        Await("a save by B wrote the authority buffer and synced the same bytes", () =>
            files.All(file => ReadLinkTestFile(file) == "named"));
        test.Run(() =>
        {
            foreach (var peer in LinkPeers.ToArray())
                LinkForget(peer.Device.Id);
            if (named is not null)
                agentChats.Remove(named);
        });
    }
}
