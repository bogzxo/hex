using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private const string LINK_TEST_PUBLICATION_OWNER = "HEX_LINK_TEST_PUBLICATION_OWNER";

    private void TestLinkWorktree(HexPointerScript test)
    {
        string home = LinkTestFolder("wa-", test);
        string peerHome = LinkTestFolder("wb-", test);
        string mine = Path.Combine(LinkTestFolder("wr-", test), LINK_SYNC_FOLDER);
        string theirs = Path.Combine(peerHome, LINK_SYNC_FOLDER);
        string target = Path.Combine(mine, "src", "shared.txt");
        string remote = Path.Combine(theirs, "src", "shared.txt");
        const string Seed = "first\nsecond\nthird\nfourth\n";
        const string ChangedThere = "first\nsecond\nthird\nremote fourth\n";
        const string Merged = "private first\nsecond\nthird\nremote fourth\n";
        HexLinkPeer? peer = null;
        HexLinkPrivateWorktree? draft = null;
        HexAgentChat? agent = null;
        Task<McpToolResult>? draftStatus = null;
        Task? step = null;
        Task<HexLinkDraftPublication>? result = null;
        bool acceptedWhilePreparing = false;
        bool cut = false;
        int cuts = 0;
        string? why = null;
        string? device = null;
        CancellationToken cancel = CancellationToken.None;
        string other = LinkTestFolder("worktree-engine-", test);
        string otherFile = Path.Combine(other, "EngineFixture.cs");
        string sparseFile = "extra/src/Fixture.cs";
        string nativeFile = "native/src/HookFixture.cs";
        bool nativeReadReady = false;
        string? fullPathError = null, unmatchedError = null;
        Task<McpToolResult[]>? toolChecks = null;

        async Task<McpToolResult[]> CheckAgentTools(HexAgentChat chat)
        {
            Task<McpToolResult> Call(string tool, JsonObject args) => McpTools.CallAs(tool, args, cancel, chat.Id);
            var found = await Call("hex_files", new JsonObject { ["query"] = LINK_SYNC_FOLDER + "/" + sparseFile });
            if (File.Exists(Path.Combine(draft!.Root, sparseFile)))
                throw new IOException("The test repository was already materialized.");
            var edited = await Call("hex_edit", new JsonObject
            {
                ["path"] = LINK_SYNC_FOLDER + "/" + sparseFile, ["old_string"] = "class Fixture",
                ["new_string"] = "class PrivateFixture", ["check"] = false
            });
            var read = await Call("hex_read", new JsonObject { ["path"] = sparseFile, ["symbol"] = "PrivateFixture" });
            var outside = await Call("hex_read", new JsonObject { ["path"] = otherFile, ["symbol"] = "EngineFixture" });
            var search = await Call("hex_search", new JsonObject { ["query"] = "EngineFixture" });
            var symbols = await Call("hex_symbols", new JsonObject { ["query"] = "EngineFixture" });
            var outsideEdit = await Call("hex_edit", new JsonObject
            {
                ["path"] = otherFile, ["old_string"] = "EngineFixture", ["new_string"] = "Wrong", ["check"] = false
            });
            var terminal = await Call("hex_terminal_run", new JsonObject { ["command"] = "git rev-parse --show-toplevel", ["timeout"] = 15 });
            string nativePath = Path.Combine(draft.Root, nativeFile);
            if (!File.Exists(nativePath))
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    WorkingDirectory = draft.Root, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true
                };
                if (LinkTestRunByHost() is { } program)
                    start.ArgumentList.Add(program);
                foreach (string arg in (string[])["--harness-hook", "--guard", "off", "--agent-chat", chat.Id.ToString(),
                    "--private-root", draft.Root, "--folder", mine])
                    start.ArgumentList.Add(arg);
                using var hook = Process.Start(start)!;
                await hook.StandardInput.WriteLineAsync(new JsonObject
                {
                    ["tool_name"] = "Read", ["tool_input"] = new JsonObject { ["file_path"] = nativePath }
                }.ToJsonString());
                hook.StandardInput.Close();
                string response = await hook.StandardOutput.ReadToEndAsync(cancel);
                await hook.WaitForExitAsync(cancel);
                nativeReadReady = hook.ExitCode == 0 && response.Length == 0 &&
                    ReadLinkTestFile(nativePath) == "class HookFixture { }\n";
            }
            return [found, edited, read, outside, search, symbols, outsideEdit, terminal];
        }

        async Task OpenDraft(HexAgentChat chat)
        {
            var folder = linkServices!.Workspace.Folders.Single(folder => folder.Name == LINK_SYNC_FOLDER);
            string owner = linkServices.Device.Id;
            linkServices.FolderAuthorities.Bind(folder, new HexLinkAuthority(owner, 1));
            await LinkActivateWorktreePublication(PublicationRecipients,
                (bound, set, peers, _) =>
                {
                    foreach (string recipient in peers)
                        PublicationPair(bound, recipient)?.DeferPublication(set);
                    if (cut && Interlocked.Increment(ref cuts) == 1)
                        link?.PeerOf(device!)?.Session.Close("self-test lost the publication connection");
                    return Task.CompletedTask;
                },
                (bound, set, recipient, _) =>
                {
                    (PublicationPair(bound, recipient) ?? throw new HexLinkException(HexLinkErrors.BUSY, "waiting for the authority sync pair")).InstallPublicationBaseline(set);
                    return Task.CompletedTask;
                }, (_, _, _) => Task.CompletedTask,
                (bound, set, recipient) => PublicationPair(bound, recipient)?.ReleasePublication(set), cancel);
            if (await LinkSyncStart(device!, folder.Name, remember: false, automatic: true) is { } why)
                throw new IOException(why);
            if (PublicationPair(folder, device!) is not { PublicationPairReady: true } sync)
                throw new IOException("The authority did not start its live sync pair.");
            await sync.Flush();
            HexLinkWorktreeGatePolicyFile.Save(Path.Combine(LinkPrivateStorage, "gates.json"), folder,
                new HexLinkAuthority(owner, 1), new HexLinkWorktreeGatePolicy("exit 0", "src", null, null, 15));
            var launched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var request = new LinkPrivateAgentStart(folder.Name, null, "test-family", []);
            await OnEditorForLink(() =>
            {
                chat.Ask("Private draft test", "Private draft test", null, null);
                agentsStarting.Add(chat);
                LinkPrepareAgent(chat, request, () =>
                {
                    agentsStarting.Remove(chat);
                    chat.Done();
                    launched.TrySetResult();
                });
                acceptedWhilePreparing = HexAgentHub.StateWord(chat).StartsWith("running (preparing private worktree", StringComparison.Ordinal)
                    && !launched.Task.IsCompleted;
                return true;
            });
            await launched.Task.WaitAsync(TimeSpan.FromSeconds(20));
            if (chat.LinkDraft is not { } id)
                throw new IOException("The agent has no private worktree.");
            draft = await LinkOpenStoredDraft(id, cancel);
            var child = new HexAgentChat(-10, chat.Agent);
            if (await LinkCreateAgentDraft(child, folder.Name, cancel, request) is not null || child.LinkDraft != id)
                throw new IOException("The same agent family did not reopen the existing private tree.");
            privateAgentRoots.TryRemove(child.Id, out _);
            fullPathError = await LinkCreateAgentDraft(child, folder.Path, cancel, request);
            unmatchedError = await LinkCreateAgentDraft(child, "missing-private-folder", cancel, request);
            privateAgentRoots.TryRemove(child.Id, out _);
        }

        async Task<HexLinkDraftPublication> Publish(string command)
        {
            if (PublicationPair(draft!.SharedFolder, device!) is { PublicationPairReady: true } sync)
                await sync.Flush();
            var pin = linkServices!.FolderAuthorities.Of(draft.SharedFolder)!;
            HexLinkWorktreeGatePolicyFile.Save(Path.Combine(LinkPrivateStorage, "gates.json"), draft.SharedFolder, pin,
                new HexLinkWorktreeGatePolicy(command, "src", null, null, 15));
            return await draft.Publish(LinkPrivatePublishRoute(draft.SharedFolder)!,
                (paths, stop) => LinkWorktreeGatesOf(draft, paths, null, preview: false, stop), false, cancel);
        }

        test.Run(() =>
        {
            why = LinkTestRepository(mine, theirs);
            if (why is not null)
                return;
            File.WriteAllText(target, Seed);
            File.WriteAllText(remote, Seed);
            why = LinkTestRepository(Path.Combine(mine, "extra"), Path.Combine(theirs, "extra"));
            if (why is not null)
                return;
            File.WriteAllText(Path.Combine(mine, sparseFile), "class Fixture { }\n");
            File.WriteAllText(Path.Combine(theirs, sparseFile), "class Fixture { }\n");
            why = LinkTestRepository(Path.Combine(mine, "native"), Path.Combine(theirs, "native"));
            if (why is not null)
                return;
            File.WriteAllText(Path.Combine(mine, nativeFile), "class HookFixture { }\n");
            File.WriteAllText(Path.Combine(theirs, nativeFile), "class HookFixture { }\n");
            File.WriteAllText(otherFile, "class EngineFixture { }\n");
            StopLink();
            explorer.OpenFolder(mine);
            explorer.AddFolder(other);
            if (!StartLink(port: 0, home: home, loopbackOnly: true))
                return;
            linkByTest = true;
            var folder = new HexLinkFolder(LINK_SYNC_FOLDER, mine);
            linkServices!.FolderAuthorities.Bind(folder, new HexLinkAuthority(linkServices.Device.Id, 1));
            LinkOpenPairing(TimeSpan.FromMinutes(3));
            LinkStartPeer(peerHome, $"127.0.0.1:{LinkPort}", new Dictionary<string, string>
            {
                [LINK_TEST_FOLDER] = theirs, [LINK_TEST_SECONDS] = "420", [LINK_TEST_PUBLICATION_OWNER] = linkServices!.Device.Id
            });
        });
        test.Check("link-worktree: both repositories and the replica app started", () => why is null && linkTestPeer is not null);
        test.WaitFor(() => LinkPairingPending is not null || linkTestPeer is { HasExited: true }, 90.0f);
        test.Run(() => LinkPairingPending?.Decide(HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit));
        bool ReplicaSaid(string text) => linkTestPeerSaid.Any(line => line.Contains(text, StringComparison.Ordinal));
        test.WaitFor(() => LinkPeers.Count == 1 && ReplicaSaid("link-worktree: replica ready"), 60.0f);
        test.Check("link-worktree: the existing replica can publish through its sync source", () =>
            ReplicaSaid("pass: link-worktree: syncing an existing replica pins the source and enables checked publication"));
        test.Run(() =>
        {
            peer = LinkPeers.FirstOrDefault();
            if (peer is null)
                return;
            device = peer.Device.Id;
            agent = new HexAgentChat(++agentChatsMade, "link_worktree_fixture") { Name = "Private worktree test" };
            agentChats.Add(agent);
            step = OpenDraft(agent);
        });
        test.WaitFor(() => step?.IsCompleted == true, 30.0f);
        test.Run(() => { if (step?.Exception is { } error) Console.WriteLine("[Hex self-test] link-worktree: " + error.GetBaseException().Message); });
        test.Check("link-worktree: a chat became visible before its detached private draft was ready", () =>
            acceptedWhilePreparing && step is { IsCompletedSuccessfully: true } && draft is not null && agent is not null && LinkAgentFolder(agent) == draft.Root);
        test.Check("link-worktree: spawn accepts a full folder path and lists folders on a mismatch", () =>
            draft is not null && fullPathError is null && unmatchedError?.Contains(mine, StringComparison.Ordinal) == true);
        test.Run(() =>
        {
            if (agent is not null && draft is not null)
            {
                if (mcpHost is null)
                    StartMcp();
                toolChecks = CheckAgentTools(agent);
            }
        });
        test.WaitFor(() => draft is null || toolChecks?.IsCompleted == true, 60.0f);
        test.Run(() =>
        {
            if (toolChecks is { IsFaulted: true })
                Console.WriteLine("[Hex self-test] link-worktree tool checks: " + toolChecks.Exception!.GetBaseException().Message);
            else if (toolChecks is { IsCompletedSuccessfully: true })
                foreach (var (answer, index) in toolChecks.Result.Select((answer, index) => (answer, index)))
                    if (answer.IsError && index != 6)
                        Console.WriteLine($"[Hex self-test] link-worktree tool {index}: {answer.AllText()}");
        });
        bool ToolSaid(int index, string text, bool error = false) => toolChecks is { IsCompletedSuccessfully: true } &&
            toolChecks.Result[index].IsError == error && toolChecks.Result[index].AllText().Contains(text, StringComparison.Ordinal);
        test.Check("link-worktree: file results are relative to the private root", () =>
            ToolSaid(0, sparseFile) && !ToolSaid(0, LINK_SYNC_FOLDER + "/" + sparseFile));
        test.Check("link-worktree: a prefixed edit materializes the sparse repository and a symbol read sees the edit", () =>
            ToolSaid(1, "Edited") && ToolSaid(2, "class PrivateFixture") &&
            ReadLinkTestFile(Path.Combine(mine, sparseFile)) == "class Fixture { }\n");
        test.Check("link-worktree: reads, search and symbols include the other workspace root, which stays read-only", () =>
            ToolSaid(3, "class EngineFixture") && ToolSaid(4, "EngineFixture.cs") && ToolSaid(5, "EngineFixture.cs") &&
            ToolSaid(6, "private worktree", error: true) && ReadLinkTestFile(otherFile) == "class EngineFixture { }\n");
        test.Check("link-worktree: the Claude Code Read hook opens a sparse private repository", () => nativeReadReady);
        test.Check("link-worktree: terminal commands start in the private root", () =>
            draft is not null && ToolSaid(7, "Exit 0") && ToolSaid(7, draft.Root.Replace('\\', '/')));
        test.Run(() =>
        {
            if (draft is null)
                return;
            if (File.Exists(Path.Combine(draft.Root, sparseFile)))
                File.WriteAllText(Path.Combine(draft.Root, sparseFile), "class Fixture { }\n");
            File.WriteAllText(Path.Combine(draft.Root, "src", "shared.txt"), "private first\nsecond\nthird\nfourth\n");
            File.WriteAllText(Path.Combine(draft.Root, "only-draft.txt"), "private");
            var pin = linkServices!.FolderAuthorities.Of(draft.SharedFolder)!;
            HexLinkWorktreeGatePolicyFile.Save(Path.Combine(LinkPrivateStorage, "gates.json"), draft.SharedFolder, pin,
                new HexLinkWorktreeGatePolicy("exit 7", "src", null, null, 15));
            draftStatus = LinkPublishAgentTool(new JsonObject { [HexMcpCaller.CALLER] = agent!.Id, ["action"] = "status" }, cancel);
            result = Publish("exit 7");
        });
        test.WaitFor(() => draft is null || result?.IsCompleted == true, 40.0f);
        test.WaitFor(() => draft is null || draftStatus?.IsCompleted == true, 20.0f);
        test.Check("link-worktree: hex_link_publish status reports private files for this agent", () =>
            draftStatus is { IsCompletedSuccessfully: true, Result.IsError: false } &&
            draftStatus.Result.AllText().Contains("2 unpublished files", StringComparison.Ordinal) &&
            draftStatus.Result.AllText().Contains("src | exit 7 | 15s", StringComparison.Ordinal) &&
            draftStatus.Result.AllText().Contains("\nsrc/shared.txt", StringComparison.Ordinal) &&
            !draftStatus.Result.AllText().Contains("\n" + LINK_SYNC_FOLDER + "/src/shared.txt", StringComparison.Ordinal) &&
            LinkScopePrivateAgentCall(agent!.Id, "hex_link_worktree_gate", []) is not null &&
            LinkScopePrivateAgentCall(agent.Id, "hex_link_authority", []) is not null);
        test.Check("link-worktree: the failed gate leaves both editors at the shared version", () =>
            result is { IsCompletedSuccessfully: true, Result: { Gate.ExitCode: 7, Receipt: null } } &&
            ReadLinkTestFile(target) == Seed && ReadLinkTestFile(remote) == Seed &&
            !File.Exists(Path.Combine(theirs, "only-draft.txt")));

        test.Run(() =>
        {
            if (draft is not null)
                File.WriteAllText(Path.Combine(theirs, "incoming-live.txt"), "from ordinary live sync");
        });
        test.WaitFor(() => draft is null || ReadLinkTestFile(Path.Combine(draft.Root, "incoming-live.txt")) == "from ordinary live sync", 45.0f);
        test.Check("link-worktree: an ordinary live change also reaches the untouched private checkout", () =>
            draft is not null && ReadLinkTestFile(Path.Combine(draft.Root, "incoming-live.txt")) == "from ordinary live sync");

        test.Run(() =>
        {
            if (draft is null)
                return;
            // Model a previously synced incoming save. The draft retains the original editing base.
            File.WriteAllText(target, ChangedThere);
            File.WriteAllText(remote, ChangedThere);
            result = Publish("exit 0");
        });
        test.WaitFor(() => draft is null || result?.IsCompleted == true && ReadLinkTestFile(remote) == Merged, 70.0f);
        test.Check("link-worktree: a clean merge passes its gate and both files arrive in one published set", () =>
            result is { IsCompletedSuccessfully: true, Result.Receipt.Files: 2 } &&
            ReadLinkTestFile(target) == Merged && ReadLinkTestFile(remote) == Merged &&
            ReadLinkTestFile(Path.Combine(theirs, "only-draft.txt")) == "private");

        test.Run(() =>
        {
            if (draft is null)
                return;
            File.WriteAllText(Path.Combine(draft.Root, "src", "shared.txt"), Merged.Replace("private first", "draft conflict", StringComparison.Ordinal));
            string shared = Merged.Replace("private first", "authority conflict", StringComparison.Ordinal);
            File.WriteAllText(target, shared);
            File.WriteAllText(remote, shared);
            result = Publish("exit 0");
        });
        test.WaitFor(() => draft is null || result?.IsCompleted == true, 40.0f);
        test.Check("link-worktree: overlapping edits wait for a person and preserve both versions", () =>
            result is { IsCompletedSuccessfully: true, Result: { Receipt: null, Conflicts.Count: 1 } } &&
            ReadLinkTestFile(remote).StartsWith("authority conflict", StringComparison.Ordinal));

        test.Run(() =>
        {
            if (draft is null)
                return;
            File.WriteAllText(Path.Combine(draft.Root, "src", "shared.txt"), ReadLinkTestFile(target));
            File.WriteAllText(Path.Combine(draft.Root, "after-cut.txt"), "after reconnect");
            cut = true;
            result = Publish("exit 0");
        });
        test.WaitFor(() => draft is null || result?.IsCompleted == true && ReadLinkTestFile(Path.Combine(theirs, "after-cut.txt")) == "after reconnect", 90.0f);
        test.Check("link-worktree: a committed publication reaches the replica after reconnect", () =>
            result is { IsCompletedSuccessfully: true, Result.Receipt.Files: 1 } && cuts == 1 &&
            ReadLinkTestFile(Path.Combine(theirs, "after-cut.txt")) == "after reconnect");

        test.Run(() =>
        {
            if (device is not null)
                LinkForget(device);
        });
        test.WaitFor(() => linkTestPeer is null or { HasExited: true }, 40.0f);
        test.Run(() =>
        {
            if (agent is not null)
                CloseAgentChat(agent);
            StopMcp();
            explorer.RemoveFolder(mine);
            explorer.RemoveFolder(other);
            TidyLinkTest();
        });
    }

    private void TestLinkWorktreePeer(HexPointerScript test, string? workspace)
    {
        if (workspace is null || System.Environment.GetEnvironmentVariable(LINK_TEST_PUBLICATION_OWNER) is not { Length: 64 } owner ||
            !owner.All(char.IsAsciiHexDigit))
            return;
        Task? restoring = null;
        bool checkedRoute = false;
        test.Run(() =>
        {
            if (linkServices is not { } services || LinkPeers.FirstOrDefault() is not { } peer || peer.Device.Id != owner)
                return;
            restoring = StartReplica(services, peer);
        });

        async Task StartReplica(LinkServices services, HexLinkPeer peer)
        {
            var folder = services.Workspace.Folders.Single(each => each.Name == Path.GetFileName(workspace));
            var cancel = linkCancel?.Token ?? CancellationToken.None;
            await LinkActivateWorktreePublication(_ => [],
                (_, _, _, _) => Task.CompletedTask, (_, _, _, _) => Task.CompletedTask,
                (_, _, _) => Task.CompletedTask, (_, _, _) => { }, cancel);
            string? why = await LinkSyncStart(peer.Device.Id, folder.Name, cancel: cancel);
            checkedRoute = why is null && services.FolderAuthorities.Of(folder)?.DeviceId == owner &&
                LinkPrivatePublishRoute(folder) is not null;
            if (!checkedRoute)
                Console.WriteLine($"[Hex self-test] link-worktree: the existing replica has no checked route, {why ?? "sync started"}");
        }
        test.WaitFor(() => restoring?.IsCompleted == true, 30.0f);
        test.Check("link-worktree: publication receiver is active", () => restoring?.IsCompletedSuccessfully == true);
        test.Check("link-worktree: syncing an existing replica pins the source and enables checked publication", () => checkedRoute);
        test.Run(() =>
        {
            if (restoring?.IsCompletedSuccessfully == true)
                Console.WriteLine("[Hex self-test] link-worktree: replica ready");
        });
    }
}
