using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

// Hex Link between two Hexes on this machine (docs/hex-link-plan.md, phase 2). This Hex (A) puts Link up at a free port
// in a folder of its own and starts a second Hex (B) of the same build with a home, a pipe and a key of its own, which
// runs the link-peer part: it pairs with A, makes a stand-in chat with an approval waiting, and serves until A forgets
// it. A pairs back as the person would through the pairing it's asked about, then reads B's hello, fleet and chat, calls
// its tools, answers its approval, writes, conflicts and reads a file of its workspace, sees a right taken away hold,
// turns away a stranger, asks through the hex_link tools, and forgets B, whose redial is refused and who leaves.
//
// B is told what to do next by files A writes into B's workspace over the link (B looks for them each tick): a row to
// add to its chat, A's rights to drop to View. Both are explicit-only parts, they start a second window. B is killed
// in the tidy after the part whatever happened, so no Hex is left running. Accepting a pairing without a person exists
// only here, in the self test.
internal sealed partial class DawnScene
{
    // The environment B is told what to do by, and how long it serves at most
    private const string LINK_TEST_LOOPBACK = "HEX_LINK_TEST_LOOPBACK";
    private const string LINK_TEST_DIAL = "HEX_LINK_TEST_DIAL", LINK_TEST_LISTEN = "HEX_LINK_TEST_LISTEN", LINK_TEST_GRANT = "HEX_LINK_TEST_GRANT",
        LINK_TEST_SECONDS = "HEX_LINK_TEST_SECONDS", LINK_TEST_FOLDER = "HEX_LINK_TEST_FOLDER";

    // What B calls itself, its workspace's folder and its stand-in chat, and the flag files A writes there for it
    private const string LINK_PEER_NAME = "Hex B", LINK_PEER_FOLDER = "link-peer-work", LINK_PEER_CHAT = "Link stand-in",
        LINK_FLAG_ROW = "link-test-add-row.flag", LINK_FLAG_VIEW = "link-test-view-only.flag", LINK_FLAG_PRESENCE = "link-test-presence.flag", LINK_FLAG_POINTER_AWAY = "link-test-pointer-away.flag", LINK_LATER_ROW = "A later row, added when A asked";

    // B, while it runs, what it printed, and the folders the part made
    private Process? linkTestPeer;
    private readonly ConcurrentQueue<string> linkTestPeerSaid = new();
    private readonly List<string> linkTestFolders = [];

    private bool LinkTestBoundOnlyToLoopback =>
        System.Net.IPAddress.Loopback.Equals(linkTransport?.ListeningAddress) && linkDiscovery is null;

    private void TestLink(HexPointerScript test)
    {
        string home = LinkTestFolder("hex-link-a-", test);
        string peerHome = LinkTestFolder("hex-link-b-", test);
        string peerWork = Path.Combine(peerHome, LINK_PEER_FOLDER);
        LinkPairingAsk? asked = null;
        HexLinkPeer? peer = null;
        Task? watched = null, wrote = null, conflicted = null, readBack = null, approved = null, refusedWrite = null, stranger = null;
        Task<McpToolResult>? listed = null, terminal = null, devicesTool = null, callTool = null;
        HexAgentChat? mirror = null;
        Exception? terminalRefusal = null, conflict = null, notAllowed = null, strangerRefusal = null;
        string? writtenHash = null;
        byte[] written = Encoding.UTF8.GetBytes("Written from A over Hex Link.\nSecond line.\n");
        HexLinkFile? read = null;
        int peerChat = 0;

        test.Run(() =>
        {
            linkTestPeerSaid.Clear();
            StopLink();
            if (!StartLink(port: 0, home: home, loopbackOnly: true))
                return;
            linkByTest = true;
            LinkOpenPairing(TimeSpan.FromMinutes(3));
            Console.WriteLine($"[Hex self-test] link: A is up on port {LinkPort} as {LinkDevice?.Fingerprint}");
            LinkStartPeer(peerHome, $"127.0.0.1:{LinkPort}");
        });
        test.Check("link: Link comes up at a free port, nothing listening before, the pairing window open", () =>
            LinkOn && LinkPort > 0 && LinkPairingUntil is not null && linkTestPeer is not null);

        test.Check("link: A listens on IPv4 loopback without LAN discovery", () => LinkTestBoundOnlyToLoopback);

        // B dials and pairs; A's person is asked with the code and grants what B may do here
        test.WaitFor(() => LinkPairingPending is not null || linkTestPeer is { HasExited: true }, 90.0f);
        test.Check("link: the pairing is asked about here with the other device, a six-digit code and the dialog up", () =>
        {
            asked = LinkPairingPending;
            return asked is { Host: true } && asked.Code.Length == 6 && asked.Code.All(char.IsAsciiDigit) && asked.Other.Name == LINK_PEER_NAME
                && linkPairingDialog is { IsOpen: true };
        });
        test.Run(() => asked?.Decide(HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit));
        test.WaitFor(() => LinkPeers.Count == 1 && LinkPeers[0].Workspace.Folders.Count > 0, 30.0f);
        test.Check("link: both screens showed the same code (B said it)", () => asked is not null && LinkTestPeerLine("pairing with ").EndsWith($", code {asked.Code}", StringComparison.Ordinal));
        test.Check("link: paired, B is known here with what was granted and the dialog gone", () =>
            LinkKnown.FirstOrDefault(known => known.Device.Name == LINK_PEER_NAME) is { Rights: HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit }
            && linkPairingDialog is null && LinkPairingPending is null);
        test.Check("link: B's hello gave its device, what it lets A do and its workspace", () =>
        {
            peer = LinkPeers.FirstOrDefault();
            Console.WriteLine($"[Hex self-test] link: linked to {peer?.Device.Name} over {(peer is null ? null : LinkTlsOf(peer.Device.Id))}");
            return peer is { Device.Name: LINK_PEER_NAME, Granted: HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit }
                && peer.Device.Os.Length > 0 && peer.Workspace.Folders.Any(folder => folder.Name == LINK_PEER_FOLDER);
        });

        // B's fleet and its stand-in chat, mirrored exactly
        test.Run(() => watched = peer?.WatchFleet());
        test.WaitFor(() => watched is { IsCompleted: true } && peer!.Fleet.Any(row => row.Name == LINK_PEER_CHAT), 15.0f);
        test.Check("link: B's fleet shows the stand-in chat B made, waiting on its person", () =>
        {
            HexLinkRow? row = peer?.Fleet.FirstOrDefault(each => each.Name == LINK_PEER_CHAT);
            peerChat = row?.Id ?? 0;
            return row is { Waiting: true };
        });
        test.Run(() =>
        {
            if (peer is not null)
                watched = peer.OpenChat(peerChat).ContinueWith(done => mirror = done.IsCompletedSuccessfully ? done.Result : null, TaskScheduler.Default);
        });
        test.WaitFor(() => watched is { IsCompleted: true } && LinkTestPeerSaid("rows "), 15.0f);
        test.Check("link: OpenChat mirrors the stand-in chat's rows exactly", () => mirror is not null && LinkTestRows(mirror) == LinkTestPeerLine("rows "));

        // Tools: an allowed one answers, one never let over a link is refused whatever was granted
        test.Run(() =>
        {
            listed = peer?.Call("hex_agent_list", []);
            terminal = peer?.Call("hex_terminal_run", new JsonObject { ["command"] = "echo hi" });
        });
        test.WaitFor(() => listed is { IsCompleted: true } && terminal is { IsCompleted: true }, 30.0f);
        test.Check("link: hex_agent_list called on B answers with its chats", () =>
            listed is { IsCompletedSuccessfully: true } && !listed.Result.IsError && listed.Result.AllText().Contains(LINK_PEER_CHAT, StringComparison.Ordinal));
        test.Check("link: hex_terminal_run is refused over a link whatever was granted", () =>
        {
            terminalRefusal = terminal?.Exception?.InnerException;
            return terminalRefusal is HexLinkException { Code: HexLinkErrors.NOT_ALLOWED } refused && HexLinkData.String(refused.Detail as JsonObject, "needs", 20) == "never";
        });

        // Edit: a file written into B's workspace, the same bytes on its disk; a stale base a conflict with the hash now; read back
        string wirePath = $"{LINK_PEER_FOLDER}/from-a.txt";
        test.Run(() => wrote = peer?.Write(wirePath, null, written).ContinueWith(done => writtenHash = done.IsCompletedSuccessfully ? done.Result : null, TaskScheduler.Default));
        test.WaitFor(() => wrote is { IsCompleted: true }, 15.0f);
        test.Check("link: Write puts a file into B's workspace with the same bytes on its disk", () =>
            writtenHash == HexLinkFiles.Hash(written) && File.Exists(Path.Combine(peerWork, "from-a.txt")) && File.ReadAllBytes(Path.Combine(peerWork, "from-a.txt")).AsSpan().SequenceEqual(written));
        test.Run(() => conflicted = peer?.Write(wirePath, HexLinkFiles.Hash(Encoding.UTF8.GetBytes("what it was before")), Encoding.UTF8.GetBytes("stale\n"))
            .ContinueWith(done => conflict = done.Exception?.InnerException, TaskScheduler.Default));
        test.WaitFor(() => conflicted is { IsCompleted: true }, 15.0f);
        test.Check("link: a write with a stale base is a conflict carrying the file's hash now, and changes nothing", () =>
            conflict is HexLinkException { Code: HexLinkErrors.CONFLICT } stale && HexLinkPeer.ConflictHash(stale) == writtenHash
            && File.ReadAllBytes(Path.Combine(peerWork, "from-a.txt")).AsSpan().SequenceEqual(written));
        test.Run(() => readBack = peer?.Read(wirePath).ContinueWith(done => read = done.IsCompletedSuccessfully ? done.Result : null, TaskScheduler.Default));
        test.WaitFor(() => readBack is { IsCompleted: true }, 15.0f);
        test.Check("link: Read gives the file back, its bytes and hash", () => read is not null && read.Bytes.AsSpan().SequenceEqual(written) && read.Hash == writtenHash);

        // The mirrored chat follows a row B adds once A asks for one (a flag file written over the link)
        test.Run(() => _ = peer?.Write($"{LINK_PEER_FOLDER}/{LINK_FLAG_ROW}", null, [1]));
        test.WaitFor(() => mirror?.Entries.Any(entry => entry.Text == LINK_LATER_ROW) == true, 20.0f);
        test.Check("link: the mirrored chat follows a row B adds later", () => mirror?.Entries.Any(entry => entry.Text == LINK_LATER_ROW) == true);

        // Steer: B's stand-in's approval answered from here, and B sees it decided
        test.Run(() => approved = peer?.Approve(peerChat, null, true));
        test.WaitFor(() => approved is { IsCompleted: true } && LinkTestPeerSaid("approval allowed"), 20.0f);
        test.Check("link: an approval waiting on B is answered from A, and B sees it allowed", () =>
            approved is Task<McpToolResult> { IsCompletedSuccessfully: true } answer && !answer.Result.IsError && LinkTestPeerSaid("approval allowed"));

        // B takes A's rights down to View; the same write is refused as not allowed, saying it takes edit
        test.Run(() => _ = peer?.Write($"{LINK_PEER_FOLDER}/{LINK_FLAG_VIEW}", null, [1]));
        test.WaitFor(() => peer?.Granted == HexLinkRights.View, 20.0f);
        test.Run(() => refusedWrite = peer?.Write(wirePath, writtenHash, Encoding.UTF8.GetBytes("not allowed\n")).ContinueWith(done => notAllowed = done.Exception?.InnerException, TaskScheduler.Default));
        test.WaitFor(() => refusedWrite is { IsCompleted: true }, 15.0f);
        test.Check("link: with A's rights dropped to View by B, a write is refused as not allowed", () =>
            peer?.Granted == HexLinkRights.View && notAllowed is HexLinkException { Code: HexLinkErrors.NOT_ALLOWED } refused
            && HexLinkData.String(refused.Detail as JsonObject, "needs", 20) == "edit" && File.ReadAllBytes(Path.Combine(peerWork, "from-a.txt")).AsSpan().SequenceEqual(written));

        // A stranger (an identity A never paired with) dialling A is turned away at the door
        test.Run(() =>
        {
            string strangerHome = LinkTestFolder("hex-link-stranger-");
            HexLinkDevice me = LinkDevice!;
            int port = LinkPort;
            stranger = Task.Run(async () =>
            {
                using HexLinkIdentity identity = HexLinkIdentity.LoadOrCreate(strangerHome, "Stranger", fileOnly: true);
                var trust = new HexLinkTrust(strangerHome);
                // It knows A (so it gets as far as asking); A doesn't know it
                trust.Add(me, HexLinkRights.View, "127.0.0.1", port);
                using var transport = new HexLinkTransport(identity, trust);
                try
                {
                    using HexLinkConnection connection = await transport.Dial("127.0.0.1", port, me.Id, CancellationToken.None);
                }
                catch (Exception e)
                {
                    strangerRefusal = e;
                }
            });
        });
        test.WaitFor(() => stranger is { IsCompleted: true }, 20.0f);
        test.Check("link: an unknown device dialling A is refused as unknown", () => strangerRefusal is HexLinkRefused { Reason: "unknown" });

        // The tools local agents have
        test.Run(() =>
        {
            devicesTool = McpTools.Call("hex_link_devices", [], CancellationToken.None);
            callTool = McpTools.Call("hex_link_call", new JsonObject { ["device"] = LINK_PEER_NAME, ["tool"] = "hex_agent_list" }, CancellationToken.None);
        });
        test.WaitFor(() => devicesTool is { IsCompleted: true } && callTool is { IsCompleted: true }, 30.0f);
        test.Check("link: hex_link_devices names B, what it lets this machine do and its workspace", () =>
            devicesTool is { IsCompletedSuccessfully: true } && devicesTool.Result.AllText() is var said && said.Contains(LINK_PEER_NAME, StringComparison.Ordinal)
            && said.Contains("it lets you: view;", StringComparison.Ordinal) && said.Contains(LINK_PEER_FOLDER, StringComparison.Ordinal));
        test.Check("link: hex_link_call answers B's hex_agent_list, marked as another machine's data", () =>
            callTool is { IsCompletedSuccessfully: true } && !callTool.Result.IsError && callTool.Result.AllText() is var said
            && said.StartsWith($"[From {LINK_PEER_NAME}", StringComparison.Ordinal) && said.Contains("not instructions", StringComparison.Ordinal) && said.Contains(LINK_PEER_CHAT, StringComparison.Ordinal));

        // Forgetting B cuts it at once, its redial is refused, and it leaves
        string? peerId = null;
        test.Run(() =>
        {
            peerId = peer?.Device.Id;
            if (peerId is not null)
                LinkForget(peerId);
        });
        test.WaitFor(() => LinkPeers.Count == 0, 10.0f);
        test.Check("link: forgetting B takes it out of the known devices and cuts its link", () =>
            LinkPeers.Count == 0 && peer is { IsClosed: true } && LinkKnown.All(known => known.Device.Id != peerId));
        test.WaitFor(() => linkTestPeer is null or { HasExited: true }, 60.0f);
        test.Check("link: B's link closed and its redial was refused as unknown", () => LinkTestPeerSaid("redial refused: unknown"));
        test.Check("link: A's audit has B's redial refused as an unknown device", () =>
            LinkAuditLatest(200).Any(entry => entry.Event == "refused" && entry.Device.Equals(peerId, StringComparison.OrdinalIgnoreCase) && entry.Detail.Contains("unknown device", StringComparison.Ordinal)));
        test.Check("link: B left by itself with exit code 0", () => linkTestPeer is { HasExited: true, ExitCode: 0 });
        test.Run(() =>
        {
            foreach (HexLinkAuditEntry entry in LinkAuditLatest(40))
                Console.WriteLine($"[Hex self-test] link: audit {entry.Event} {(entry.Device.Length > 8 ? entry.Device[..8] : entry.Device)} {entry.Detail}");
        });
    }

    /// <summary>
    /// B's side of the link test, and a Hex to link to from another machine: HEX_LINK_TEST_DIAL=host:port dials and
    /// pairs, HEX_LINK_TEST_LISTEN=port puts Link up there with pairing open and grants what HEX_LINK_TEST_GRANT says
    /// (view,steer,edit by default). It makes a stand-in chat with an approval waiting, serves until its link is cut and
    /// a redial refused (or HEX_LINK_TEST_SECONDS pass, 120 by default), and says what it saw.
    /// </summary>
    private void TestLinkPeer(HexPointerScript test)
    {
        string? dial = System.Environment.GetEnvironmentVariable(LINK_TEST_DIAL);
        bool loopbackOnly = System.Environment.GetEnvironmentVariable(LINK_TEST_LOOPBACK) == "1";
        int listen = int.TryParse(System.Environment.GetEnvironmentVariable(LINK_TEST_LISTEN), NumberStyles.None, CultureInfo.InvariantCulture, out int port) ? port : 0;
        float seconds = float.TryParse(System.Environment.GetEnvironmentVariable(LINK_TEST_SECONDS), NumberStyles.Float, CultureInfo.InvariantCulture, out float asked) ? asked : 120.0f;
        HexLinkRights grant = LinkTestGrant(System.Environment.GetEnvironmentVariable(LINK_TEST_GRANT));
        string work = Path.Combine(System.Environment.GetEnvironmentVariable("HEX_HOME") is { Length: > 0 } home ? home : Path.GetTempPath(), LINK_PEER_FOLDER);
        HexAgentChat? standIn = null;
        Task<HexLinkPairResult>? paired = null;
        bool cut = false, refused = false, wasLinked = false;
        string? otherId = null;
        bool ui = System.Environment.GetEnvironmentVariable(LINK_TEST_UI) == "1";

        // Accepting without a person, only here: what the environment says is granted, the code said for A to compare
        Task<HexLinkRights?> Accept(HexLinkPairing pairing)
        {
            Console.WriteLine($"[Hex self-test] link-peer: pairing with {pairing.Other.Name} ({pairing.Other.Fingerprint}) at {pairing.Address}, code {pairing.Code}");
            return Task.FromResult<HexLinkRights?>(grant);
        }

        // The link-files part's folder, a clone of a repository A has, opened as B's workspace (where its agents run), the
        // usual folder alongside it in what the link says B has
        string? syncFolder = System.Environment.GetEnvironmentVariable(LINK_TEST_FOLDER) is { Length: > 0 } given && Directory.Exists(given) ? given : null;

        test.Run(() =>
        {
            Directory.CreateDirectory(work);
            explorer.OpenFolder(syncFolder ?? work);
            if (syncFolder is not null)
                linkTestExtraFolders = [work];
            settings.LinkName = LINK_PEER_NAME;
            if (ui)
            {
                StartLinkUiAgent(work);
                agentPicked = LINK_UI_AGENT;
            }

            standIn = new HexAgentChat(++agentChatsMade, "link_stand_in") { Name = LINK_PEER_CHAT };
            standIn.Ask("Build the thing on this machine");
            standIn.Say("Working on it, the tests next.");
            standIn.Approval("Run the tests", "dotnet test", allowed => Console.WriteLine($"[Hex self-test] link-peer: approval {(allowed ? "allowed" : "denied")}"));
            agentChats.Add(standIn);
            Console.WriteLine($"[Hex self-test] link-peer: rows {LinkTestRows(standIn)}");

            StopLink();
            if (!StartLink(port: listen, discover: false, loopbackOnly: loopbackOnly))
                return;
            linkByTest = true;
            LinkRedialRefused = (id, why) =>
            {
                Console.WriteLine($"[Hex self-test] link-peer: redial refused: {why}");
                if (why == "unknown")
                    refused = true;
            };
            link!.Closed += (device, why) =>
            {
                Console.WriteLine($"[Hex self-test] link-peer: link to {device.Name} closed, {why}");
                cut = true;
            };

            if (dial is not null && TryLinkAddress(dial, HexLinkTransport.DEFAULT_PORT, out string host, out int dialPort))
            {
                Console.WriteLine($"[Hex self-test] link-peer: dialling {host}:{dialPort} to pair");
                paired = LinkPair(host, dialPort, Accept);
            }
            else
            {
                Console.WriteLine($"[Hex self-test] link-peer: listening on port {LinkPort}, pairing open, granting {string.Join(',', HexLinkTrust.RightsNames(grant))}");
                LinkOpenPairing(TimeSpan.FromSeconds(seconds));
            }
        });

        if (loopbackOnly)
            test.Check("link-peer: B listens on IPv4 loopback without LAN discovery", () => LinkTestBoundOnlyToLoopback);

        // The pairing that comes in, when it's B that listens, answered here as A's test answers its own
        test.WaitFor(() =>
        {
            if (LinkPairingPending is { IsDecided: false } pending)
            {
                Console.WriteLine($"[Hex self-test] link-peer: pairing with {pending.Other.Name} ({pending.Other.Fingerprint}) at {pending.Address}, code {pending.Code}");
                pending.Decide(grant);
            }

            return LinkPeers.Count > 0 || paired is { IsCompleted: true, Result.Paired: false };
        }, Math.Min(seconds, 120.0f));
        test.Check("link-peer: paired with the other Hex and linked", () =>
        {
            HexLinkPeer? other = LinkPeers.FirstOrDefault();
            otherId = other?.Device.Id;
            wasLinked = other is not null;
            if (other is not null)
                Console.WriteLine($"[Hex self-test] link-peer: linked to {other.Device.Name} ({other.Device.Fingerprint}) over {LinkTlsOf(other.Device.Id)}, it lets this one {string.Join(',', HexLinkTrust.RightsNames(other.Granted))}");
            else if (paired is { IsCompleted: true } ended)
                Console.WriteLine($"[Hex self-test] link-peer: the pairing ended {ended.Result.Reason}");
            return wasLinked;
        });

        // Serving: what A asks for by the files it writes here done, until A cuts the link and the redial is refused
        bool rowAdded = false, viewOnly = false, presenceSet = false, pointerAway = false;
        int uiLooked = 0;
        DateTime uiSteeredSeen = DateTime.MinValue;
        test.WaitFor(() =>
        {
            LinkDocumentTestPeer(work);
            if (!rowAdded && File.Exists(Path.Combine(work, LINK_FLAG_ROW)) && standIn is not null)
            {
                rowAdded = true;
                standIn.Say(LINK_LATER_ROW);
                Console.WriteLine("[Hex self-test] link-peer: added a row as asked");
            }

            if (!viewOnly && File.Exists(Path.Combine(work, LINK_FLAG_VIEW)) && otherId is not null)
            {
                viewOnly = true;
                LinkSetRights(otherId, HexLinkRights.View);
                Console.WriteLine("[Hex self-test] link-peer: the other may only view here now");
            }

            // For the link-ui part, each Steer request heard said, what A's UI sent checked by
            if (ui && ++uiLooked % 20 == 0)
                LinkTestSaySteered(ref uiSteeredSeen);

            // The caret put in the synced folder's README when asked, for the other to see where this one is (link-files)
            if (!presenceSet && syncFolder is not null && File.Exists(Path.Combine(work, LINK_FLAG_PRESENCE)) && Open(Path.Combine(syncFolder, "README.md")))
            {
                presenceSet = true;
                SwitchView(HexView.Code);
                document.Editor.GoTo(0, 2);
                compositor.PointerSource = () => new(document.Module.ToWorld(document.Editor.PlaceOf(new(0, 4))), false);
                Console.WriteLine("[Hex self-test] link-peer: the caret is in README.md at 0:2");
            }

            if (presenceSet && !pointerAway && File.Exists(Path.Combine(work, LINK_FLAG_POINTER_AWAY)))
            {
                pointerAway = true;
                compositor.PointerSource = () => new(new System.Numerics.Vector2(-100000, -100000), false);
            }

            // A patch the other offers applied, as the person here would press Apply (the link-files part)
            foreach (LinkPatchOffer offer in LinkPatchesOffered)
                Console.WriteLine($"[Hex self-test] link-peer: patch \"{offer.Title}\" {LinkDecidePatch(offer, "applied")}");

            return cut && refused;
        }, seconds);
        test.Check("link-peer: the link was cut and the redial refused", () => !wasLinked || (cut && refused));
        test.Run(() =>
        {
            foreach (HexLinkAuditEntry entry in LinkAuditLatest(60))
                Console.WriteLine($"[Hex self-test] link-peer: audit {entry.Event} {(entry.Device.Length > 8 ? entry.Device[..8] : entry.Device)} {entry.Detail}");
            StopLink();
            if (ui)
                StopMcp();
            compositor.PointerSource = () => test.Pointer;
            if (standIn is not null)
                agentChats.Remove(standIn);
        });
    }

    // What HEX_LINK_TEST_GRANT says is granted, everything when it says nothing
    private static HexLinkRights LinkTestGrant(string? said) => said is { Length: > 0 }
        ? HexLinkTrust.RightsOf(said.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        : HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit;

    // A chat's rows in a line, their kinds and texts, what B says and A compares its mirror with
    private static string LinkTestRows(HexAgentChat chat) =>
        string.Join(" | ", chat.Entries.Select(entry => $"{entry.Kind}:{entry.Text.ReplaceLineEndings(" ")}"));

    // Suites are all queued before they run. Create and track a folder during its own suite so the prior tidy cannot delete it.
    private string LinkTestFolder(string prefix, HexPointerScript? test = null)
    {
        string folder = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..12]);

        void Create()
        {
            Directory.CreateDirectory(folder);
            linkTestFolders.Add(folder);
        }

        if (test is null)
            Create();
        else
            test.Run(Create);
        return folder;
    }

    /// <summary>
    /// B started: this Hex again with a home, a pipe and a key of its own, told to dial and pair with A, what it prints
    /// kept for the checks and said after "B|". Killed in the tidy after the part (and as this process exits) whatever happened.
    /// </summary>
    private void LinkStartPeer(string peerHome, string dial, IReadOnlyDictionary<string, string>? also = null, string suite = "link-peer")
    {
        var start = new ProcessStartInfo(System.Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        // Run as "dotnet Hex.dll" the process is dotnet, which is told the program to run before its arguments
        if (LinkTestRunByHost() is { } program)
            start.ArgumentList.Add(program);
        foreach (string argument in (string[])["--selftest", suite, "--exit", "--size", "1200x800"])
            start.ArgumentList.Add(argument);
        start.Environment["HEX_HOME"] = peerHome;
        start.Environment["HEX_REMOTE_PIPE"] = "hex-link-peer-" + Guid.NewGuid().ToString("N")[..12];
        start.Environment["HORIZON_AUDIO"] = "off";
        start.Environment[HexLinkSecrets.KEYSTORE_VARIABLE] = "file";
        start.Environment[LINK_TEST_DIAL] = dial;
        start.Environment[LINK_TEST_LOOPBACK] = "1";
        start.Environment[LINK_TEST_SECONDS] = "150";
        start.Environment.Remove(LINK_TEST_LISTEN);
        start.Environment.Remove(LINK_TEST_FOLDER);
        foreach (var (name, value) in also ?? new Dictionary<string, string>())
            start.Environment[name] = value;

        var process = new Process { StartInfo = start };
        void Heard(object sender, DataReceivedEventArgs said)
        {
            if (said.Data is not { } line)
                return;
            linkTestPeerSaid.Enqueue(line);
            if (line.Contains("[Hex self-test]", StringComparison.Ordinal))
                Console.WriteLine($"  B| {line.Replace("[Hex self-test] ", string.Empty, StringComparison.Ordinal)}");
        }

        process.OutputDataReceived += Heard;
        process.ErrorDataReceived += Heard;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        linkTestPeer = process;
        AppDomain.CurrentDomain.ProcessExit -= KillLinkTestPeer;
        AppDomain.CurrentDomain.ProcessExit += KillLinkTestPeer;
    }

    /// <summary>
    /// The program's own file when this Hex was started by the dotnet host ("dotnet Hex.dll", which Windows' firewall
    /// already lets listen), so a second Hex is started the same way; null when the process is Hex itself.
    /// </summary>
    internal static string? LinkTestRunByHost()
    {
        if (!string.Equals(Path.GetFileNameWithoutExtension(System.Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            return null;
        string program = Path.Combine(AppContext.BaseDirectory, "Hex.dll");
        return File.Exists(program) ? program : null;
    }

    // Whether B printed a line with this in it
    private bool LinkTestPeerSaid(string part) => linkTestPeerSaid.Any(line => line.Contains("link-peer: " + part, StringComparison.Ordinal));

    // What came after this in the line B printed with it
    private string LinkTestPeerLine(string part)
    {
        string? line = linkTestPeerSaid.FirstOrDefault(each => each.Contains("link-peer: " + part, StringComparison.Ordinal));
        return line is null ? "(B said nothing of it)" : line[(line.IndexOf("link-peer: " + part, StringComparison.Ordinal) + "link-peer: ".Length + part.Length)..];
    }

    private void KillLinkTestPeer(object? sender, EventArgs e) => KillLinkTestPeer();

    /// <summary>After the link parts: B killed when it's still running, Link taken down, the part's folders deleted.</summary>
    private void TidyLinkTest()
    {
        if (!linkByTest && linkTestPeer is null && linkTestFolders.Count == 0)
            return;
        KillLinkTestPeer();
        StopLink();
        linkTestExtraFolders = [];
        foreach (string folder in linkTestFolders)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        linkTestFolders.Clear();
    }

    private void KillLinkTestPeer()
    {
        Process? peer = Interlocked.Exchange(ref linkTestPeer, null);
        if (peer is null)
            return;
        try
        {
            if (!peer.HasExited)
            {
                peer.Kill(entireProcessTree: true);
                peer.WaitForExit(5000);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        finally
        {
            peer.Dispose();
        }
    }
}
