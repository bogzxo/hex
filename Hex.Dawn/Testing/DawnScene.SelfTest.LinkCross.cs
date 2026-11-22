using System.Globalization;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hex.Dawn;

// Hex Link across two machines (docs/hex-link-plan.md, phase 5), one command on each: HEX_LINK_TEST_LISTEN=port on one,
// HEX_LINK_TEST_DIAL=host:port on the other, the same build. A link is symmetric, so both sides run the same checks of
// the other: the pairing code both screens show, the other's hello, its fleet and its stand-in chat mirrored, its tools,
// its approval answered from here, a file written into its workspace and a stale write conflicted, a tool never let
// over a link refused, and once both have done that, each drops the other to View and sees its own write refused. The
// dialler then has an unpaired identity dial the listener (refused as unknown) and, given HEX_LINK_TEST_MITM, dials
// through a man in the middle expecting the listener's pin (refused). Last the listener forgets the dialler, whose redial
// is refused as unknown.
//
// The two sides agree on when to go on through a status file each keeps in its own workspace and the other reads over
// the link, a line a step. Each side prints what the other compares in its log: the code, the hash of its chat's rows,
// the SHA-256 of a file the other wrote, the TLS version and cipher. With HEX_LINK_TEST_AGENT (claude_code) each also
// starts a real agent on the other machine through hex_link_spawn (HEX_LINK_TEST_MODEL, haiku by default), answers its
// approvals over the link, and checks its reply carries the other machine's uname.
internal sealed partial class DawnScene
{
    private const string LINK_CROSS_FOLDER = "link-cross-work", LINK_CROSS_STATUS = "link-cross-status.txt", LINK_CROSS_CHAT = "Link cross stand-in",
        LINK_TEST_AGENT = "HEX_LINK_TEST_AGENT", LINK_TEST_MODEL = "HEX_LINK_TEST_MODEL", LINK_TEST_MITM = "HEX_LINK_TEST_MITM", LINK_TEST_MARKER = "HEX_LINK_TEST_MARKER",
        LINK_TEST_NAME = "HEX_LINK_TEST_NAME";

    private void TestLinkCross(HexPointerScript test)
    {
        string? dial = System.Environment.GetEnvironmentVariable(LINK_TEST_DIAL);
        int listen = int.TryParse(System.Environment.GetEnvironmentVariable(LINK_TEST_LISTEN), NumberStyles.None, CultureInfo.InvariantCulture, out int port) ? port : 0;
        float seconds = float.TryParse(System.Environment.GetEnvironmentVariable(LINK_TEST_SECONDS), NumberStyles.Float, CultureInfo.InvariantCulture, out float asked) ? asked : 300.0f;
        string? agent = System.Environment.GetEnvironmentVariable(LINK_TEST_AGENT) is { Length: > 0 } wanted ? wanted : null;
        bool loopbackOnly = System.Environment.GetEnvironmentVariable(LINK_TEST_LOOPBACK) == "1";
        bool documentSoak = System.Environment.GetEnvironmentVariable("HEX_LINK_TEST_DOCUMENT_SOAK") == "1";
        bool documents = documentSoak || System.Environment.GetEnvironmentVariable("HEX_LINK_TEST_DOCUMENTS") == "1";
        bool recovery = System.Environment.GetEnvironmentVariable("HEX_LINK_TEST_DOCUMENT_RECOVERY") == "1";
        bool worktree = System.Environment.GetEnvironmentVariable("HEX_LINK_TEST_WORKTREE") == "1";
        bool prediction = System.Environment.GetEnvironmentVariable("HEX_LINK_TEST_PREDICTION") == "1";
        bool files = System.Environment.GetEnvironmentVariable("HEX_LINK_TEST_FILES") == "1";
        bool[] scenarios = [documents, recovery, worktree, prediction, files];
        int selectedScenarios = scenarios.Count(selected => selected);
        bool workspaceOnly = selectedScenarios > 0;
        bool coding = System.Environment.GetEnvironmentVariable("HEX_LINK_TEST_CODING") == "1";
        // By default both sides make a real turn. A one-way run makes only the caller's turn and checks the same file from both sides.
        string? codingRole = System.Environment.GetEnvironmentVariable("HEX_LINK_TEST_CODING_ROLE");
        bool codingCaller = codingRole == "caller", codingReceiver = codingRole == "receiver";
        if (codingRole is not null && (!coding || (!codingCaller && !codingReceiver)))
            throw new InvalidOperationException("The coding role must be caller or receiver and requires HEX_LINK_TEST_CODING=1.");
        if (selectedScenarios > 1 || (workspaceOnly && (agent is not null || coding)))
            throw new InvalidOperationException("Choose one link-cross scenario.");
        if (coding && (!loopbackOnly || (!codingReceiver && agent is null) || (codingReceiver && agent is not null)))
            throw new InvalidOperationException("Coding requires loopback. Set HEX_LINK_TEST_AGENT on each side starting an agent.");
        string model = System.Environment.GetEnvironmentVariable(LINK_TEST_MODEL) is { Length: > 0 } named ? named : "haiku";
        string? mitm = System.Environment.GetEnvironmentVariable(LINK_TEST_MITM) is { Length: > 0 } middle ? middle : null;
        string marker = System.Environment.GetEnvironmentVariable(LINK_TEST_MARKER) is { Length: > 0 } said ? said : "HEXLINK-PLAINTEXT-MARKER-7731";
        string myName = System.Environment.GetEnvironmentVariable(LINK_TEST_NAME) is { Length: > 0 } calledBy ? calledBy : "Hex " + System.Environment.MachineName;
        var pairingRights = prediction ? HexLinkRights.Predict : HexLinkRights.View | HexLinkRights.Edit;
        if (!workspaceOnly)
            pairingRights |= HexLinkRights.Steer;
        bool dialler = dial is not null;
        if (coding && dial is not null && (!TryLinkAddress(dial, HexLinkTransport.DEFAULT_PORT, out string codingHost, out _) || codingHost != "127.0.0.1"))
            throw new InvalidOperationException("The coding fixture dials 127.0.0.1 only.");
        string role = dialler ? "dialler" : "listener";
        string work = Path.Combine(System.Environment.GetEnvironmentVariable("HEX_HOME") is { Length: > 0 } home ? home : Path.GetTempPath(), LINK_CROSS_FOLDER);
        string status = Path.Combine(work, LINK_CROSS_STATUS);
        string myFile = "from-" + Regex.Replace(myName, "[^A-Za-z0-9]+", "-") + ".txt";
        byte[] written = Encoding.UTF8.GetBytes($"Written over Hex Link by {myName}, {marker}.\nSecond line.\n");

        HexAgentChat? standIn = null;
        HexLinkPeer? peer = null;
        HexAgentChat? mirror = null;
        Task<HexLinkPairResult>? paired = null;
        Task? step = null, poll = null, agentRun = null;
        Task<McpToolResult>? listed = null, terminal = null, devicesTool = null, callTool = null, approved = null;
        Exception? terminalRefusal = null, conflict = null, notAllowed = null, strangerRefusal = null, mitmRefusal = null;
        string? writtenHash = null, myCode = null, otherId = null, agentReply = null, agentWhy = null;
        string theirs = string.Empty;
        const string codingFile = "LinkProbe.cs";
        string codingPath = Path.Combine(work, codingFile);
        byte[]? remoteCodingBytes = null;
        bool cut = false, redialRefused = false, gotFile = false, droppedHere = false;
        var stop = new CancellationTokenSource();

        void Say(string line) => Console.WriteLine($"[Hex self-test] link-cross: {line}");
        void Mark(string line)
        {
            File.AppendAllText(status, line + "\n");
            Say("status " + line);
        }

        // A line of the other's status that starts so, what follows it, null when it hasn't said it yet
        string? Theirs(string start) => theirs.Split('\n').FirstOrDefault(line => line.StartsWith(start, StringComparison.Ordinal)) is { } line ? line[start.Length..].Trim() : null;

        // Accepting without a person, only here: everything granted, the code said for the other side to compare
        Task<HexLinkRights?> Accept(HexLinkPairing pairing)
        {
            myCode = pairing.Code;
            Say($"pairing with {pairing.Other.Name} ({pairing.Other.Fingerprint}) at {pairing.Address}, code {pairing.Code}");
            return Task.FromResult<HexLinkRights?>(pairingRights);
        }

        test.Run(() =>
        {
            if (Directory.Exists(work))
                Directory.Delete(work, recursive: true);
            Directory.CreateDirectory(work);
            if (files && !dialler)
                PrepareLinkCrossFiles(work);
            explorer.OpenFolder(work);
            if (!workspaceOnly)
                File.WriteAllText(status, string.Empty);
            settings.LinkName = myName;

            if (!workspaceOnly)
            {
                standIn = new HexAgentChat(++agentChatsMade, "link_stand_in") { Name = LINK_CROSS_CHAT };
                standIn.Ask($"Build the thing on {myName}, {marker}");
                standIn.Say("Working on it, the tests next.");
                standIn.Approval("Run the tests", "dotnet test", allowed =>
                {
                    Say($"approval {(allowed ? "allowed" : "denied")}");
                    Mark($"approval {(allowed ? "allowed" : "denied")}");
                });
                agentChats.Add(standIn);
                string rows = LinkTestRows(standIn);
                Say($"rows {rows}");
                Mark($"rows {LinkCrossHash(rows)}");
            }

            StopLink();
            if (!StartLink(port: listen, discover: false, loopbackOnly: loopbackOnly))
                return;
            linkByTest = true;
            if (documents && !dialler)
                LinkCrossPublishDocument(work, documentSoak ? LINK_DOCUMENT_SOAK_START : "seed");
            Say($"{role}, up as {myName} ({LinkDevice?.Fingerprint}) on port {LinkPort}, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
            LinkRedialRefused = (_, why) =>
            {
                Say($"redial refused: {why}");
                if (why == "unknown")
                    redialRefused = true;
            };
            link!.Closed += (device, why) =>
            {
                Say($"link to {device.Name} closed, {why}");
                cut = true;
            };

            if (dialler && TryLinkAddress(dial!, HexLinkTransport.DEFAULT_PORT, out string host, out int dialPort))
            {
                Say($"dialling {host}:{dialPort} to pair");
                paired = LinkPair(host, dialPort, Accept);
            }
            else
            {
                Say($"listening on port {LinkPort}, pairing open");
                LinkOpenPairing(TimeSpan.FromSeconds(seconds));
            }
        });

        // Paired: the pairing that comes in answered here when this side listens
        test.WaitFor(() =>
        {
            if (LinkPairingPending is { IsDecided: false } pending)
            {
                myCode = pending.Code;
                Say($"pairing with {pending.Other.Name} ({pending.Other.Fingerprint}) at {pending.Address}, code {pending.Code}");
                pending.Decide(pairingRights);
            }

            return (prediction ? LinkPeers.FirstOrDefault() is not null : LinkPeers.FirstOrDefault() is { Workspace.Folders.Count: > 0 })
                || paired is { IsCompleted: true, Result.Paired: false };
        }, seconds);
        test.Check("link-cross: paired with the other machine and linked", () =>
        {
            peer = LinkPeers.FirstOrDefault();
            otherId = peer?.Device.Id;
            if (peer is null)
            {
                Say($"not linked{(paired is { IsCompleted: true } ended ? ", the pairing ended " + ended.Result.Reason : string.Empty)}");
                return false;
            }

            var ssl = LinkTransportOf(peer.Device.Id)?.Stream as SslStream;
            Say($"linked to {peer.Device.Name} ({peer.Device.Fingerprint}) os {peer.Device.Os} arch {peer.Device.Arch} version {peer.Device.Version}, at {LinkTransportOf(peer.Device.Id)?.Address}, over {ssl?.SslProtocol} {ssl?.NegotiatedCipherSuite}, it lets this one {string.Join(',', HexLinkTrust.RightsNames(peer.Granted))}");
            if (workspaceOnly)
                return peer.Granted == pairingRights;

            Mark($"code {myCode}");
            Mark($"tls {ssl?.SslProtocol} {ssl?.NegotiatedCipherSuite}");
            // The other's status read over the link from now on, twice a second
            poll = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested && peer is { IsClosed: false } other)
                {
                    try
                    {
                        HexLinkFile file = await other.Read($"{LINK_CROSS_FOLDER}/{LINK_CROSS_STATUS}");
                        theirs = Encoding.UTF8.GetString(file.Bytes);
                    }
                    catch (Exception)
                    {
                        // Not written yet, or the right to read it gone with the link
                    }

                    await Task.Delay(500);
                }
            });
            return true;
        });
        if (workspaceOnly)
        {
            if (prediction)
                TestLinkCrossPrediction(test, () => peer, dialler, work);
            else if (documentSoak)
                TestLinkCrossDocumentSoak(test, () => peer, dialler, work);
            else if (documents)
                TestLinkCrossDocuments(test, () => peer, dialler, work);
            else if (recovery)
                TestLinkCrossRecovery(test, () => peer, dial, work);
            else if (files)
                TestLinkCrossFiles(test, () => peer, dialler, work);
            else
                TestLinkCrossWorktree(test, () => peer, dial, work);
            test.Run(StopLink);
            return;
        }

        test.WaitFor(() => Theirs("code ") is not null, 30.0f);
        test.Check("link-cross: both machines showed the same six-digit code", () =>
        {
            Say($"code here {myCode}, there {Theirs("code ")}");
            return myCode is { Length: 6 } && myCode == Theirs("code ");
        });
        test.Check("link-cross: the other's hello says its device name, OS, architecture and version, and its workspace", () =>
            peer is { Device.Name.Length: > 0, Device.Os.Length: > 0, Device.Arch.Length: > 0, Device.Version.Length: > 0 }
            && peer.Workspace.Folders.Any(folder => folder.Name == LINK_CROSS_FOLDER));

        // The other's fleet and its stand-in chat, mirrored as its rows are there
        test.Run(() => step = peer?.WatchFleet());
        test.WaitFor(() => step is { IsCompleted: true } && peer!.Fleet.Any(row => row.Name == LINK_CROSS_CHAT), 20.0f);
        int otherChat = 0;
        test.Check("link-cross: the other's fleet shows its stand-in chat, waiting on its person", () =>
        {
            HexLinkRow? row = peer?.Fleet.FirstOrDefault(each => each.Name == LINK_CROSS_CHAT);
            otherChat = row?.Id ?? 0;
            Say($"its fleet: {string.Join(", ", peer?.Fleet.Select(each => $"#{each.Id} {each.Name} {each.State}{(each.Waiting ? " waiting" : string.Empty)}") ?? [])}");
            return row is { Waiting: true };
        });
        test.Run(() =>
        {
            if (peer is not null)
                step = peer.OpenChat(otherChat).ContinueWith(done => mirror = done.IsCompletedSuccessfully ? done.Result : null, TaskScheduler.Default);
        });
        test.WaitFor(() => step is { IsCompleted: true } && Theirs("rows ") is not null, 20.0f);
        test.Check("link-cross: the mirrored chat's rows hash the same as the other side's own", () =>
        {
            string? mine = mirror is null ? null : LinkCrossHash(LinkTestRows(mirror));
            Say($"mirror rows {(mirror is null ? "(none)" : LinkTestRows(mirror))}");
            Say($"mirror hash {mine}, theirs {Theirs("rows ")}");
            return mine is not null && mine == Theirs("rows ");
        });

        // Tools through the link: an allowed one, the hex_link tools, one never let over a link
        test.Run(() =>
        {
            listed = peer?.Call("hex_agent_list", []);
            terminal = peer?.Call("hex_terminal_run", new JsonObject { ["command"] = "echo hi" });
            devicesTool = McpTools.Call("hex_link_devices", [], CancellationToken.None);
            callTool = peer is null ? null : McpTools.Call("hex_link_call", new JsonObject { ["device"] = peer.Device.Name, ["tool"] = "hex_agent_list" }, CancellationToken.None);
        });
        test.WaitFor(() => listed is { IsCompleted: true } && terminal is { IsCompleted: true } && devicesTool is { IsCompleted: true } && callTool is { IsCompleted: true }, 30.0f);
        test.Check("link-cross: hex_agent_list on the other machine answers with its stand-in chat", () =>
            listed is { IsCompletedSuccessfully: true } && !listed.Result.IsError && listed.Result.AllText().Contains(LINK_CROSS_CHAT, StringComparison.Ordinal));
        test.Check("link-cross: hex_link_devices and hex_link_call reach the other machine, its answer marked as its data", () =>
        {
            Say($"hex_link_devices: {devicesTool?.Result.AllText().ReplaceLineEndings(" / ")}");
            return devicesTool is { IsCompletedSuccessfully: true } && peer is not null && devicesTool.Result.AllText().Contains(peer.Device.Name, StringComparison.Ordinal)
                && callTool is { IsCompletedSuccessfully: true } && !callTool.Result.IsError && callTool.Result.AllText() is var call
                && call.StartsWith($"[From {peer.Device.Name}", StringComparison.Ordinal) && call.Contains(LINK_CROSS_CHAT, StringComparison.Ordinal);
        });
        test.Check("link-cross: hex_terminal_run on the other machine is refused, never over a link", () =>
        {
            terminalRefusal = terminal?.Exception?.InnerException;
            return terminalRefusal is HexLinkException { Code: HexLinkErrors.NOT_ALLOWED } refused && HexLinkData.String(refused.Detail as JsonObject, "needs", 20) == "never";
        });

        // Edit: a file into the other's workspace, the same SHA-256 on its disk as here; a stale base conflicts
        test.Run(() => step = peer?.Write($"{LINK_CROSS_FOLDER}/{myFile}", null, written).ContinueWith(done => writtenHash = done.IsCompletedSuccessfully ? done.Result : null, TaskScheduler.Default));
        test.WaitFor(() =>
        {
            LinkCrossIncoming(work, myFile, ref gotFile, Mark);
            return step is { IsCompleted: true } && Theirs($"got {myFile} ") is not null;
        }, 30.0f);
        test.Check("link-cross: a file written into the other's workspace has the same SHA-256 on its disk", () =>
        {
            Say($"wrote {myFile}, {written.Length} bytes, sha256 here {HexLinkFiles.Hash(written)}, answered {writtenHash}, on its disk {Theirs($"got {myFile} ")}");
            return writtenHash == HexLinkFiles.Hash(written) && Theirs($"got {myFile} ") == writtenHash;
        });
        test.Run(() => step = peer?.Write($"{LINK_CROSS_FOLDER}/{myFile}", HexLinkFiles.Hash(Encoding.UTF8.GetBytes("what it was before")), Encoding.UTF8.GetBytes("stale\n"))
            .ContinueWith(done => conflict = done.Exception?.InnerException, TaskScheduler.Default));
        test.WaitFor(() =>
        {
            LinkCrossIncoming(work, myFile, ref gotFile, Mark);
            return step is { IsCompleted: true };
        }, 20.0f);
        test.Check("link-cross: a write with a stale base is a conflict carrying the file's hash there", () =>
            conflict is HexLinkException { Code: HexLinkErrors.CONFLICT } stale && HexLinkPeer.ConflictHash(stale) == writtenHash);

        // Steer: the other's stand-in's approval answered from here, and the other sees it allowed
        test.Run(() => approved = peer?.Approve(otherChat, null, true));
        test.WaitFor(() =>
        {
            LinkCrossIncoming(work, myFile, ref gotFile, Mark);
            return approved is { IsCompleted: true } && Theirs("approval ") is not null;
        }, 30.0f);
        test.Check("link-cross: the other's approval answered from here, and it saw it allowed", () =>
            approved is { IsCompletedSuccessfully: true } answer && !answer.Result.IsError && Theirs("approval ") == "allowed");

        // A real agent started on the other machine through the link, its approvals answered from here
        if (agent is not null)
        {
            test.Run(() =>
            {
                HexLinkPeer other = peer!;
                agentRun = Task.Run(async () =>
                {
                    try
                    {
                        (agentReply, agentWhy) = await LinkCrossAgent(other, agent, model, coding, Say);
                        if (codingCaller && agentWhy is null)
                            remoteCodingBytes = (await other.Read($"{LINK_CROSS_FOLDER}/{codingFile}")).Bytes;
                    }
                    catch (Exception e)
                    {
                        agentWhy = e.Message;
                    }
                });
            });
            test.WaitFor(() =>
            {
                LinkCrossIncoming(work, myFile, ref gotFile, Mark);
                return agentRun is { IsCompleted: true } && (!coding || codingCaller || File.Exists(codingPath));
            }, 420.0f);
            test.Check(coding ? $"link-cross: remote {agent} completed the edit and the fixture has the exact bytes" : $"link-cross: a real {agent} agent started on the other machine replies with that machine's uname", () =>
            {
                Say($"agent reply: {agentReply ?? "(none)"}{(agentWhy is null ? string.Empty : ", " + agentWhy)}");
                if (coding)
                {
                    byte[]? bytes = remoteCodingBytes;
                    string? expectedName = peer?.Device.Name;
                    if (!codingCaller)
                    {
                        bytes = File.Exists(codingPath) ? File.ReadAllBytes(codingPath) : null;
                        expectedName = myName;
                    }

                    return agentWhy is null && agentReply?.Contains("fixture complete", StringComparison.OrdinalIgnoreCase) == true
                        && expectedName is not null && LinkCrossCodingBytesMatch(bytes, expectedName);
                }
                string expect = peer?.Device.Os switch
                {
                    "macos" => "Darwin",
                    "windows" => "_NT",
                    "linux" => "Linux",
                    _ => "?"
                };
                return agentReply is not null && agentReply.Contains("pong", StringComparison.OrdinalIgnoreCase) && agentReply.Contains(expect, StringComparison.Ordinal);
            });
        }

        if (codingReceiver)
        {
            test.WaitFor(() => File.Exists(codingPath), 420.0f);
            test.Check("link-cross: the incoming remote agent wrote the exact local fixture bytes", () =>
                File.Exists(codingPath) && LinkCrossCodingBytesMatch(File.ReadAllBytes(codingPath), myName));
        }

        // Rights: once the other has watched and steered this side, it drops to View here, and this side to View there
        test.Run(() => Mark("watched"));
        test.WaitFor(() =>
        {
            LinkCrossIncoming(work, myFile, ref gotFile, Mark);
            if (!droppedHere && Theirs("watched") is not null && otherId is not null)
            {
                droppedHere = true;
                LinkSetRights(otherId, HexLinkRights.View);
                Mark("dropped");
            }

            return peer?.Granted == HexLinkRights.View && droppedHere;
        }, agent is null && !codingReceiver ? 120.0f : 480.0f);
        test.Run(() => step = peer?.Write($"{LINK_CROSS_FOLDER}/{myFile}", writtenHash, Encoding.UTF8.GetBytes("not allowed\n")).ContinueWith(done => notAllowed = done.Exception?.InnerException, TaskScheduler.Default));
        test.WaitFor(() => step is { IsCompleted: true }, 20.0f);
        test.Check("link-cross: with this side dropped to View by the other, its write is refused as needing edit", () =>
            peer?.Granted == HexLinkRights.View && notAllowed is HexLinkException { Code: HexLinkErrors.NOT_ALLOWED } refused
            && HexLinkData.String(refused.Detail as JsonObject, "needs", 20) == "edit");
        test.Run(() => Mark("rights-checked"));

        if (dialler)
        {
            // An identity the listener never paired with, dialling it from this machine, refused as unknown
            test.Run(() =>
            {
                string strangerHome = LinkTestFolder("hex-link-stranger-");
                HexLinkDevice other = peer!.Device;
                TryLinkAddress(dial!, HexLinkTransport.DEFAULT_PORT, out string host, out int dialPort);
                step = Task.Run(async () =>
                {
                    using HexLinkIdentity identity = HexLinkIdentity.LoadOrCreate(strangerHome, "Stranger", fileOnly: true);
                    var trust = new HexLinkTrust(strangerHome);
                    trust.Add(other, HexLinkRights.View, host, dialPort);
                    using var transport = new HexLinkTransport(identity, trust);
                    try
                    {
                        using HexLinkConnection connection = await transport.Dial(host, dialPort, other.Id, CancellationToken.None);
                    }
                    catch (Exception e)
                    {
                        strangerRefusal = e;
                    }
                });
            });
            test.WaitFor(() => step is { IsCompleted: true }, 30.0f);
            test.Check("link-cross: an unpaired identity dialling the listener from this machine is refused as unknown", () =>
            {
                Say($"stranger: {strangerRefusal?.Message}");
                return strangerRefusal is HexLinkRefused { Reason: "unknown" };
            });
            // Through a man in the middle that answers with a key of its own: this machine's own key and known devices,
            // dialling expecting the listener's pin, are refused before a word is said
            if (mitm is not null)
            {
                test.Run(() =>
                {
                    TryLinkAddress(mitm, HexLinkTransport.DEFAULT_PORT, out string host, out int middlePort);
                    string expect = otherId!;
                    HexLinkIdentity me = linkIdentity!;
                    HexLinkTrust trust = linkTrust!;
                    step = Task.Run(async () =>
                    {
                        var transport = new HexLinkTransport(me, trust);
                        try
                        {
                            using HexLinkConnection connection = await transport.Dial(host, middlePort, expect, CancellationToken.None);
                        }
                        catch (Exception e)
                        {
                            mitmRefusal = e;
                        }
                        finally
                        {
                            // Its listener was never started, disposing it closes nothing of the real link's
                            transport.Dispose();
                        }
                    });
                });
                test.WaitFor(() => step is { IsCompleted: true }, 30.0f);
                test.Check("link-cross: dialling the paired listener through a man in the middle is refused, the pin isn't the listener's", () =>
                {
                    Say($"through the middle: {(mitmRefusal as HexLinkRefused)?.Reason} {mitmRefusal?.Message}");
                    return mitmRefusal is HexLinkRefused { Reason: "pin" } && LinkPeers.Count == 1;
                });
            }

            // The listener forgets this one once it's said it's done: the link is cut and the redial refused as unknown
            test.Run(() => Mark("stranger-done"));
            test.WaitFor(() => cut && redialRefused, 120.0f);
            test.Check("link-cross: forgotten by the listener, the link was cut and the redial refused as unknown", () => cut && redialRefused && LinkPeers.Count == 0);
        }
        else
        {
            test.WaitFor(() => Theirs("rights-checked") is not null && Theirs("stranger-done") is not null, 120.0f);
            test.Check("link-cross: the dialler's unpaired stranger was refused here as an unknown device", () =>
                LinkAuditLatest(200).Any(entry => entry.Event == "refused" && entry.Detail.Contains("unknown device \"Stranger\"", StringComparison.Ordinal)));
            test.Run(() =>
            {
                stop.Cancel();
                if (otherId is not null)
                    LinkForget(otherId);
            });
            test.WaitFor(() => LinkPeers.Count == 0 && LinkAuditLatest(200).Any(entry => entry.Event == "refused" && entry.Device.Equals(otherId, StringComparison.OrdinalIgnoreCase)
                && entry.Detail.Contains("unknown device", StringComparison.Ordinal)), 90.0f);
            test.Check("link-cross: forgetting the dialler cut its link, and its redial was refused here as unknown", () =>
                LinkPeers.Count == 0 && LinkKnown.All(known => known.Device.Id != otherId)
                && LinkAuditLatest(200).Any(entry => entry.Event == "refused" && entry.Device.Equals(otherId, StringComparison.OrdinalIgnoreCase) && entry.Detail.Contains("unknown device", StringComparison.Ordinal)));
        }

        test.Run(() =>
        {
            stop.Cancel();
            foreach (HexLinkAuditEntry entry in LinkAuditLatest(80))
                Say($"audit {entry.Event} {(entry.Device.Length > 8 ? entry.Device[..8] : entry.Device)} {entry.Detail}");
            StopLink();
            if (standIn is not null)
                agentChats.Remove(standIn);
        });
    }

    /// <summary>
    /// Whether this Hex finds another on the LAN by mDNS: Link up as the settings would put it with link_discoverable on
    /// (HEX_LINK_TEST_LISTEN the port, HEX_LINK_TEST_SECONDS how long to look, 60 by default), what's heard printed as it
    /// comes, passing once another Hex than this one is heard. Run on two machines at once.
    /// </summary>
    private void TestLinkNearby(HexPointerScript test)
    {
        int listen = int.TryParse(System.Environment.GetEnvironmentVariable(LINK_TEST_LISTEN), NumberStyles.None, CultureInfo.InvariantCulture, out int port) ? port : 0;
        float seconds = float.TryParse(System.Environment.GetEnvironmentVariable(LINK_TEST_SECONDS), NumberStyles.Float, CultureInfo.InvariantCulture, out float asked) ? asked : 60.0f;
        string myName = System.Environment.GetEnvironmentVariable(LINK_TEST_NAME) is { Length: > 0 } calledBy ? calledBy : "Hex " + System.Environment.MachineName;
        string heardSaid = string.Empty;

        test.Run(() =>
        {
            settings.LinkName = myName;
            settings.LinkDiscoverable = true;
            StopLink();
            if (!StartLink(port: listen, discover: true))
                return;
            linkByTest = true;
            Console.WriteLine($"[Hex self-test] link-nearby: up as {myName} ({LinkDevice?.Id[..8]}) on port {LinkPort}, looking for {seconds} s{(LinkNearbyOff is { } off ? ", can't look: " + off : string.Empty)}");
        });
        test.Check("link-nearby: the mDNS browser started without a socket error", () => linkDiscovery is not null && LinkNearbyOff is null);
        test.WaitFor(() =>
        {
            string said = string.Join("; ", LinkNearby.Select(heard => $"{heard.Name} ({heard.Id8}) port {heard.Port} at {string.Join(',', heard.Addresses)}"));
            if (said != heardSaid)
            {
                heardSaid = said;
                Console.WriteLine($"[Hex self-test] link-nearby: heard {(said.Length > 0 ? said : "nobody")}");
            }

            return LinkNearby.Any(heard => LinkDevice is { } me && !me.Id.StartsWith(heard.Id8, StringComparison.OrdinalIgnoreCase));
        }, seconds);
        test.Check("link-nearby: another Hex is heard on the LAN", () =>
            LinkNearby.Any(heard => LinkDevice is { } me && !me.Id.StartsWith(heard.Id8, StringComparison.OrdinalIgnoreCase)));
        // Heard, it stays a little longer so the other machine hears this one too
        test.WaitFor(() => false, Math.Min(15.0f, seconds));
        test.Run(StopLink);
    }

    // A file the other side wrote into this one's workspace, its SHA-256 on this disk said and put in the status once
    private static void LinkCrossIncoming(string work, string mine, ref bool seen, Action<string> mark)
    {
        if (seen)
            return;
        foreach (string file in Directory.EnumerateFiles(work, "from-*.txt"))
        {
            string name = Path.GetFileName(file);
            if (name == mine)
                continue;
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(file);
            }
            catch (IOException)
            {
                return;
            }

            if (bytes.Length == 0)
                return;
            seen = true;
            Console.WriteLine($"[Hex self-test] link-cross: {name} arrived, {bytes.Length} bytes, sha256 on this disk {HexLinkFiles.Hash(bytes)}");
            mark($"got {name} {HexLinkFiles.Hash(bytes)}");
            return;
        }
    }

    // The first 16 hex digits of the SHA-256 of a text, what both sides print of a chat's rows
    private static string LinkCrossHash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    // A link's connection to a device, for its TLS stream and address
    private HexLinkConnection? LinkTransportOf(string id) => linkTransport?.LinkTo(id);

    private static bool LinkCrossCodingBytesMatch(byte[]? bytes, string name) =>
        bytes is not null && bytes.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes($"// {name}\n"));

    /// <summary>
    /// A real agent started on the other machine with hex_link_spawn, told to run uname and say what it printed, its
    /// approvals answered from here over the link (a few at most), waited for over the link and its reply read back.
    /// The reply, or null and why.
    /// </summary>
    private async Task<(string? Reply, string? Why)> LinkCrossAgent(HexLinkPeer other, string agent, string model, bool coding, Action<string> say)
    {
        string prompt = coding
            ? $"Use your local Hex MCP hex_write tool to create LinkProbe.cs in the open workspace. The UTF-8 file must have one line terminated by LF, with no BOM. Write the line between BEGIN and END, excluding the markers:\nBEGIN\n// {other.Device.Name}\nEND\nUse no shell and no other writes. Reply with exactly: fixture complete"
            : "Run the shell command `uname -sm` exactly once. Then reply with exactly one line and nothing else: "
            + "pong from <this machine's host name>: <what uname printed>";
        McpToolResult started = await McpTools.Call("hex_link_spawn", new JsonObject
        {
            ["device"] = other.Device.Name,
            ["prompt"] = prompt,
            ["name"] = coding ? "Link coding fixture" : "Link pong",
            ["agent"] = agent,
            ["model"] = model
        }, CancellationToken.None);
        say($"hex_link_spawn: {started.AllText().ReplaceLineEndings(" / ")}");
        Match id = Regex.Match(started.AllText(), @"Started #(\d+)");
        if (started.IsError || !id.Success)
            return (null, "it didn't start");
        int chat = int.Parse(id.Groups[1].Value, CultureInfo.InvariantCulture);

        int approvals = 0;
        DateTime until = DateTime.UtcNow.AddMinutes(6);
        while (DateTime.UtcNow < until)
        {
            if (coding)
                await Task.Delay(1000);
            McpToolResult waited = await McpTools.Call("hex_link_call", new JsonObject
            {
                ["device"] = other.Device.Name,
                ["tool"] = "hex_agent_wait",
                ["arguments"] = new JsonObject { ["ids"] = new JsonArray(chat), ["timeout"] = 15 }
            }, CancellationToken.None);
            HexLinkRow? row = other.Fleet.FirstOrDefault(each => each.Id == chat);
            say($"waited: {row?.State} {(row?.Waiting == true ? "waiting " : string.Empty)}{waited.AllText().ReplaceLineEndings(" / ")[..Math.Min(300, waited.AllText().Length)]}");
            if (coding && row is { Waiting: true })
                return (null, "The coding fixture requires approval.");
            if (row is { Waiting: true } && approvals < 4)
            {
                approvals++;
                McpToolResult answer = await other.Approve(chat, null, true);
                say($"approved over the link: {answer.AllText().ReplaceLineEndings(" / ")}");
                continue;
            }

            if (row is { State: "done" or "failed" or "stopped" })
            {
                McpToolResult read = await other.Call("hex_agent_read", new JsonObject { ["id"] = chat });
                return (read.AllText().Trim(), row.State == "done" ? null : row.State);
            }
        }

        return (null, "it didn't finish in six minutes");
    }
}
