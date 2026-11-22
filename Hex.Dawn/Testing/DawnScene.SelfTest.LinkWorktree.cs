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
            StopLink();
            linkTestExtraFolders = [mine];
            if (!StartLink(port: 0, home: home, loopbackOnly: true))
                return;
            linkByTest = true;
            LinkOpenPairing(TimeSpan.FromMinutes(3));
            LinkStartPeer(peerHome, $"127.0.0.1:{LinkPort}", new Dictionary<string, string>
            {
                [LINK_TEST_FOLDER] = theirs, [LINK_TEST_SECONDS] = "420", [LINK_TEST_PUBLICATION_OWNER] = linkServices!.Device.Id
            });
        });
        test.Check("link-worktree: both repositories and the replica app started", () => why is null && linkTestPeer is not null);
        test.WaitFor(() => LinkPairingPending is not null || linkTestPeer is { HasExited: true }, 90.0f);
        test.Run(() => LinkPairingPending?.Decide(HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit));
        test.WaitFor(() => LinkPeers.Count == 1 && linkTestPeerSaid.Any(line => line.Contains("link-worktree: replica ready", StringComparison.Ordinal)), 60.0f);
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
        test.Run(() =>
        {
            if (draft is null)
                return;
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
                agentChats.Remove(agent);
            TidyLinkTest();
            linkTestExtraFolders = [];
        });
    }

    private void TestLinkWorktreePeer(HexPointerScript test, string? workspace)
    {
        if (workspace is null || System.Environment.GetEnvironmentVariable(LINK_TEST_PUBLICATION_OWNER) is not { Length: 64 } owner ||
            !owner.All(char.IsAsciiHexDigit))
            return;
        Task? restoring = null;
        test.Run(() =>
        {
            if (linkServices is not { } services || LinkPeers.FirstOrDefault() is not { } peer || peer.Device.Id != owner)
                return;
            var folder = services.Workspace.Folders.Single(each => each.Name == Path.GetFileName(workspace));
            services.FolderAuthorities.Bind(folder, new HexLinkAuthority(owner, 1));
            restoring = LinkActivateWorktreePublication(_ => [],
                (_, _, _, _) => Task.CompletedTask, (_, _, _, _) => Task.CompletedTask,
                (_, _, _) => Task.CompletedTask, (_, _, _) => { }, linkCancel?.Token ?? CancellationToken.None);
        });
        test.WaitFor(() => restoring?.IsCompleted == true, 30.0f);
        test.Check("link-worktree: publication receiver is active", () => restoring?.IsCompletedSuccessfully == true);
        test.Run(() =>
        {
            if (restoring?.IsCompletedSuccessfully == true)
                Console.WriteLine("[Hex self-test] link-worktree: replica ready");
        });
    }
}
