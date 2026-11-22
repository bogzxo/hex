using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private const string LINK_DOC_TEST = "HEX_LINK_DOCUMENT_TEST";
    private const string LINK_DOC_OWNER = "HEX_LINK_DOCUMENT_TEST_OWNER";
    private const string LINK_DOC_WIRE = "shared/note.txt";
    private const int LINK_DOC_SAMPLES = 24;
    private int linkDocTestStage, linkDocLatencySample;
    private bool linkDocAuthorityReady;
    private readonly long[] linkDocWire = new long[LINK_DOC_SAMPLES], linkDocGuestWorker = new long[LINK_DOC_SAMPLES],
        linkDocReceived = new long[LINK_DOC_SAMPLES], linkDocApplied = new long[LINK_DOC_SAMPLES];

    private static string LinkDocSample(int sample) => $"seed{(char)('a' + sample - 1)}";

    private static void LinkDocStamp(long[] ticks, string text, long observed = 0)
    {
        if (text is ['s', 'e', 'e', 'd', >= 'a' and <= 'x' and var key])
            Interlocked.CompareExchange(ref ticks[key - 'a'], observed == 0 ? Stopwatch.GetTimestamp() : observed, 0);
    }

    private void LinkDocumentTestPeer(string work)
    {
        string? role = System.Environment.GetEnvironmentVariable(LINK_DOC_TEST);
        if (role is not ("editor" or "reader" or "relay") || linkServices is not { } services
            || System.Environment.GetEnvironmentVariable(LINK_DOC_OWNER) is not { } owner
            || System.Environment.GetEnvironmentVariable(LINK_TEST_FOLDER) is not { } folder)
            return;
        string? hostId = role == "relay" ? System.Environment.GetEnvironmentVariable(LINK_DOC_RELAY) : owner;
        var host = hostId is null ? null : LinkPeerOf(hostId);
        if (!linkDocAuthorityReady && host is null)
            return;
        bool Flag(string name) => File.Exists(Path.Combine(work, name));
        void Signal(string name, string contents = "") => File.WriteAllText(Path.Combine(work, name), contents);
        if (!linkDocAuthorityReady)
        {
            services.FolderAuthorities.Bind(new HexLinkFolder("shared", folder), new HexLinkAuthority(owner, 1));
            Signal("authority-ready");
            linkDocAuthorityReady = true;
        }
        if (Flag("headless-open"))
        {
            LinkDocumentSaveTestPeer(work, folder, role, hostId!, host);
            return;
        }
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
            if (document.Editor.ReadOnly && document.Editor.Text == "Y" && !Flag("agent-edit-approved"))
                Signal("agent-edit-approved");
            if (document.Editor.Text == "named" && document.Editor.RemoteCarets.Any(caret => caret.Label == "Agent Fixture"))
                Signal("agent");
            return;
        }

        if (linkDocTestStage == 1 && document.Editor.Text == "seed")
        {
            document.Editor.Document.Select(new(0, 0), new(0, 2));
            shared.TestTiming = (stage, text, ticks) =>
            {
                switch (stage)
                {
                    case LinkLiveTiming.WireReplay:
                        LinkDocStamp(linkDocWire, text, ticks);
                        break;
                    case LinkLiveTiming.WorkerStart:
                        LinkDocStamp(linkDocGuestWorker, text, ticks);
                        break;
                    case LinkLiveTiming.Prepared:
                        LinkDocStamp(linkDocReceived, text, ticks);
                        break;
                }
            };
            var buffer = document.Editor.Document;
            buffer.Changed += () => LinkDocStamp(linkDocApplied, buffer.Text);
            Signal("joined");
            linkDocTestStage++;
        }

        for (int sample = linkDocLatencySample + 1; sample <= LINK_DOC_SAMPLES; sample++)
        {
            if (document.Editor.Text != LinkDocSample(sample))
                break;
            Signal($"latency{sample}", FormattableString.Invariant($"{linkDocWire[sample - 1]}|{linkDocGuestWorker[sample - 1]}|{linkDocReceived[sample - 1]}|{linkDocApplied[sample - 1]}|{Stopwatch.GetTimestamp()}"));
            linkDocLatencySample = sample;
        }

        if (linkDocTestStage == 2 && Flag("overlap") && document.Editor.Text == "seed")
        {
            shared.HoldEditsForTest = true;
            Rewrite(document, "Y");
            Signal("draft");
            linkDocTestStage++;
        }
        if (linkDocTestStage == 3 && Flag("release"))
        {
            shared.HoldEditsForTest = false;
            LinkLiveFlush(shared, urgent: true);
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
            Signal("agent-edit-approved");
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
        string relayHome = LinkTestFolder("hex-link-doc-d-", test);
        string folder = Path.Combine(home, "shared"), path = Path.Combine(folder, "note.txt");
        string peerFolder = Path.Combine(peerHome, "shared"), readerFolder = Path.Combine(readerHome, "shared");
        string peerWork = Path.Combine(peerHome, LINK_PEER_FOLDER), readerWork = Path.Combine(readerHome, LINK_PEER_FOLDER);
        string[] files = [path, Path.Combine(peerFolder, "note.txt"), Path.Combine(readerFolder, "note.txt")];
        long[] sent = new long[LINK_DOC_SAMPLES], typed = new long[LINK_DOC_SAMPLES];
        long[] dispatched = new long[LINK_DOC_SAMPLES], ownerWorker = new long[LINK_DOC_SAMPLES], ordered = new long[LINK_DOC_SAMPLES];
        HexAgentChat? named = null;
        Task<McpToolResult>? agentEdit = null;
        Task<string?>? syncing = null;
        bool SyncReady(HexLinkPeer peer) => LinkSyncOf(peer.Device.Id, "shared")?.State is HexLinkSyncState.Idle or HexLinkSyncState.PullOnly;

        async Task<string?> StartFixtureSync(HexLinkPeer peer)
        {
            string? why = await LinkSyncStart(peer.Device.Id, "shared", remember: false);
            // The automatic check may have started first, then declined this plain folder.
            if (why is null && LinkSyncOf(peer.Device.Id, "shared")?.State == HexLinkSyncState.Off)
                why = await LinkSyncStart(peer.Device.Id, "shared", remember: false);
            if (why is not null)
                Console.WriteLine($"[Hex self-test] link-documents: sync start failed for {peer.Device.Name}: {why}");
            return why;
        }
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
                [LINK_DOC_TEST] = role, [LINK_DOC_OWNER] = linkServices!.Device.Id,
                [LINK_TEST_FOLDER] = workspace, [LINK_TEST_SECONDS] = "420"
            }, additional: additional);

        test.Run(() =>
        {
            foreach (string file in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "disk");
            }
            foreach (string root in new[] { folder, peerFolder, readerFolder, Path.Combine(relayHome, "shared") })
            {
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, LINK_DOC_HEADLESS_FILE),
                    root == folder ? "overlap\nlast\n" : "overlap\r\nlast\r\n");
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
        test.WaitFor(() => Flag(peerWork, "authority-ready"), 30);
        test.Run(() =>
        {
            if (LinkPeers.SingleOrDefault() is { } editor)
                syncing = StartFixtureSync(editor);
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

        test.Run(() =>
        {
            if (LinkLiveOf(document) is not { } shared)
                return;
            shared.TestTiming = (stage, text, ticks) => LinkDocStamp(stage switch
            {
                LinkLiveTiming.LocalChange => typed,
                LinkLiveTiming.Dispatch => dispatched,
                LinkLiveTiming.WorkerStart => ownerWorker,
                _ => ordered
            }, text, ticks);
            var buffer = document.Editor.Document;
            buffer.Changed += () => LinkDocStamp(typed, buffer.Text);
        });
        for (int sample = 1; sample <= sent.Length; sample++)
        {
            int number = sample;
            test.Run(() =>
            {
                document.Editor.Focus();
                document.Editor.Document.Select(new(0, 4), new(0, document.Editor.Text.Length));
                sent[number - 1] = Stopwatch.GetTimestamp();
                Engine.Input.Post(Horizon.Input.InputEvent.Typed(LinkDocSample(number)[^1..]));
            });
            Await($"live edit {number} reached the second editor", () => Flag(peerWork, $"latency{number}"));
        }
        test.Run(() =>
        {
            var samples = new List<long[]>();
            for (int i = 0; i < sent.Length; i++)
            {
                string flag = Path.Combine(peerWork, $"latency{i + 1}");
                if (!File.Exists(flag))
                    continue;
                string[] fields = File.ReadAllText(flag).Split('|');
                if (fields.Length == 5 && fields.All(each => long.TryParse(each, CultureInfo.InvariantCulture, out _)))
                    samples.Add([sent[i], typed[i], dispatched[i], ownerWorker[i], ordered[i],
                        .. fields.Select(each => long.Parse(each, CultureInfo.InvariantCulture))]);
            }
            Measure("input dispatch to local edit", 0, 1);
            Measure("local edit to exchange", 1, 2);
            Measure("exchange to authority worker", 2, 3);
            Measure("authority worker to ordered edit", 3, 4);
            Measure("authority order to wire replay", 4, 5);
            Measure("wire replay to peer worker", 5, 6);
            Measure("peer worker to prepared edit", 6, 7);
            Measure("prepared edit to remote editor", 7, 8);
            Measure("remote editor to fixture observation", 8, 9);
            Measure("input dispatch to remote editor", 0, 8);
            Measure("input dispatch to fixture observation", 0, 9);

            void Measure(string label, int from, int to)
            {
                double[] ms = [.. samples.Where(row => row[from] != 0 && row[to] != 0)
                    .Select(row => Stopwatch.GetElapsedTime(row[from], row[to]).TotalMilliseconds).Order()];
                if (ms.Length == 0)
                    return;
                double median = (ms[(ms.Length - 1) / 2] + ms[ms.Length / 2]) * 0.5;
                double p95 = ms[(int)Math.Ceiling(ms.Length * 0.95) - 1];
                Console.WriteLine(FormattableString.Invariant($"[Hex self-test] link-documents timing {label}, n={ms.Length}, median={median:F1} ms, p95={p95:F1} ms"));
            }
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
        test.WaitFor(() => Flag(readerWork, "authority-ready"), 30);
        test.Run(() =>
        {
            if (LinkPeers.FirstOrDefault(peer => !HexLinkAllowed.Has(peer.Session.Connection.Rights, HexLinkRights.Edit)) is { } reader)
                syncing = StartFixtureSync(reader);
        });
        Await("the reader's plain folder is synced", () =>
            syncing is { IsCompletedSuccessfully: true, Result: null } && LinkPeers.Count(SyncReady) == 2);
        test.Run(() => Signal(readerWork, "open"));
        Await("the third editor received the same buffer read-only", () => Flag(readerWork, "read") && LinkLiveOf(document)?.Participants.Count == 3);

        test.Run(() => document.Editor.Document.Undo());
        Await("A's undo kept the overlapping text from B", () => Flag(peerWork, "undo") && document.Editor.Text == "Y");
        bool ReplicasApprovedAgentEdit() => Flag(peerWork, "agent-edit-approved") && Flag(readerWork, "agent-edit-approved");
        test.WaitFor(ReplicasApprovedAgentEdit, 30);
        test.Run(() =>
        {
            if (!ReplicasApprovedAgentEdit())
            {
                Console.WriteLine("[Hex self-test] link-documents: the simulated participants did not approve the agent edit");
                return;
            }
            named = new HexAgentChat(++agentChatsMade, "codex") { Name = "Fixture" };
            agentChats.Add(named);
            agentEdit = McpTools.CallAs("hex_edit", new JsonObject
            {
                // The scripted owner and both replicas consent to the named agent editing their shared file.
                ["path"] = path, ["old_string"] = "Y", ["new_string"] = "named", ["check"] = false, ["override_claim"] = true
            }, default, caller: named.Id);
        });
        Await("all editors saw the named agent's live edit and caret", () =>
            agentEdit is { IsCompletedSuccessfully: true, Result.IsError: false } && document.Editor.Text == "named" && Flag(readerWork, "agent"));
        test.Run(() =>
        {
            if (agentEdit is { IsCompletedSuccessfully: true, Result.IsError: true })
                Console.WriteLine($"[Hex self-test] link-documents: agent edit failed: {agentEdit.Result.AllText()}");
            else if (agentEdit?.Exception is { } failure)
                Console.WriteLine($"[Hex self-test] link-documents: agent edit threw: {failure.GetBaseException().Message}");
            Signal(peerWork, "save");
        });
        Await("a save by B wrote the authority buffer and synced the same bytes", () =>
            files.All(file => ReadLinkTestFile(file) == "named"));
        TestLinkDocumentSave(test, folder, peerWork, readerWork, relayHome);
        test.Run(() =>
        {
            foreach (var peer in LinkPeers.ToArray())
                LinkForget(peer.Device.Id);
            if (named is not null)
                agentChats.Remove(named);
        });
    }
}
