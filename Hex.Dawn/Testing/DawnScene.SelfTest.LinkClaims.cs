using System.Text.Json.Nodes;
using Hex.Core.Git;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private const string CLAIM_TEST_WORK = "claim-work";
    private const string CLAIM_TEST_CHILD = "HEX_LINK_CLAIMS_TEST_CHILD";
    private const string CLAIM_TEST_OWNER = "HEX_LINK_CLAIMS_TEST_OWNER";
    private const string CLAIM_TEST_PLAN = "claim-step";
    private const string CLAIM_TEST_FILE = "src/shared.txt";
    private const string CLAIM_TEST_BASE = "a\nb\nc\nd\ne\nf\ng\nh\ni\n";
    private const string CLAIM_TEST_MAIL = "Can you let me know when the planned file is free?";
    private const HexLinkRights CLAIM_TEST_RIGHTS = HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit;

    private void TestLinkClaims(HexPointerScript test)
    {
        if (System.Environment.GetEnvironmentVariable(CLAIM_TEST_CHILD) is { Length: > 0 } child
            && System.Environment.GetEnvironmentVariable(CLAIM_TEST_OWNER) is { Length: > 0 } owner)
        {
            TestLinkClaimsJoiner(test, child, owner);
            return;
        }

        string home = LinkTestFolder("hex-claims-a-", test), peerHome = LinkTestFolder("hex-claims-b-", test);
        string local = Path.Combine(home, CLAIM_TEST_WORK), remote = Path.Combine(peerHome, CLAIM_TEST_WORK);
        string localFile = Path.Combine(local, CLAIM_TEST_FILE);
        HexLinkPeer? peer = null, reconnect = null;
        HexAgentChat? holder = null;
        Task<McpToolResult>? planned = null, released = null;
        Task<string?>? initial = null, merged = null;
        string? why = null;
        test.Run(() =>
        {
            why = LinkTestRepository(local, remote);
            if (why is not null)
            {
                Console.WriteLine($"[Hex self-test] link-claims: fixture Git failed, {why}");
                return;
            }

            foreach (string folder in new[] { local, remote })
            foreach (string path in new[] { "clean.txt", "overlap.txt" })
                File.WriteAllText(Path.Combine(folder, path), CLAIM_TEST_BASE);

            StopLink();
            linkTestExtraFolders = [local];
            if (!StartLink(port: 0, home: Path.Combine(home, "link"), loopbackOnly: true))
                return;
            linkByTest = true;
            string authority = LinkDevice!.Id;
            linkServices!.FolderAuthorities.Bind(new HexLinkFolder(CLAIM_TEST_WORK, local), new HexLinkAuthority(authority, 1));
            holder = new HexAgentChat(++agentChatsMade, "link_stand_in") { Name = "Claims Builder" };
            agentChats.Add(holder);
            planned = McpTools.CallAs("hex_plan_update", ClaimTestPlan("doing"), CancellationToken.None, holder.Id);
            LinkOpenPairing(TimeSpan.FromMinutes(3));
            LinkStartPeer(peerHome, $"127.0.0.1:{LinkPort}", new Dictionary<string, string>
            {
                [CLAIM_TEST_CHILD] = remote, [CLAIM_TEST_OWNER] = authority
            }, suite: "link-claims");
        });
        test.Check("link-claims: both test checkouts were prepared", () => why is null && linkTestPeer is not null);
        test.WaitFor(() => LinkPairingPending is not null || linkTestPeer is { HasExited: true }, 90.0f);
        test.Run(() => LinkPairingPending?.Decide(CLAIM_TEST_RIGHTS));
        test.WaitFor(() => (peer = LinkPeers.FirstOrDefault()) is not null || linkTestPeer is { HasExited: true }, 35.0f);
        test.Check("link-claims: host plan was written through its MCP tool", () => planned is { IsCompletedSuccessfully: true, Result.IsError: false });

        test.WaitFor(() => holder?.Mail.Any(mail => mail.Text == CLAIM_TEST_MAIL) == true, 40.0f);
        test.Check("link-claims: the joiner's direct remote-holder message reached the host chat", () =>
            holder?.Mail.Any(mail => mail.Text == CLAIM_TEST_MAIL) == true);
        test.WaitFor(() => File.Exists(Path.Combine(peerHome, "claim-mail-ack")), 20.0f);
        test.Run(() =>
        {
            if (peer is not null)
                link?.Remove(peer.Device.Id, "the claims suite is checking reconnect");
        });
        test.WaitFor(() => (reconnect = LinkPeers.FirstOrDefault()) is not null && reconnect != peer, 55.0f);
        test.Check("link-claims: socket reconnected", () => reconnect is not null && reconnect != peer);
        test.WaitFor(() => File.Exists(Path.Combine(peerHome, "claim-reattached")), 38.0f);
        test.Check("link-claims: joiner saw the renewed claim after reconnect", () => File.Exists(Path.Combine(peerHome, "claim-reattached")));

        test.Run(() =>
        {
            if (holder is not null)
                released = McpTools.CallAs("hex_plan_update", ClaimTestPlan("done"), CancellationToken.None, holder.Id);
            Open(localFile);
        });
        test.Settle(1.0f);
        test.Check("link-claims: merely opening a clean file did not claim it for editing", () =>
            released is { IsCompletedSuccessfully: true, Result.IsError: false }
            && LinkClaimsAt(localFile).All(claim => claim.Kind != HexLinkClaimKind.Editing));
        test.Run(() =>
        {
            if (document.Path is not { } path || !PlatformPaths.Same(path, localFile))
                return;
            LinkDocumentHost(document, CLAIM_TEST_WORK + "/" + CLAIM_TEST_FILE);
            document.Editor.Document.Insert("Typing after releasing the plan.\n");
        });
        test.WaitFor(() => File.Exists(Path.Combine(peerHome, "claim-joined-document")), 35.0f);
        test.Check("link-claims: joiner opened the live document from the human typing claim", () =>
            File.Exists(Path.Combine(peerHome, "claim-joined-document")));
        test.Run(() =>
        {
            if (reconnect is not null)
                initial = FlushClaimsTestSync(reconnect.Device.Id);
        });
        test.WaitFor(() => initial is { IsCompleted: true }, 35.0f);
        test.Check("link-claims: baseline reached both syncs before simultaneous edits", () =>
            initial is { IsCompletedSuccessfully: true, Result: null });

        const string clean = "A\nb\nc\nd\ne\nf\ng\nh\nI\n";
        const string ours = "A\nb\nc\nd\nours\nf\ng\nh\ni\n", theirs = "a\nb\nc\nd\ntheirs\nf\ng\nh\nI\n";
        test.Run(() =>
        {
            if (reconnect is null || initial is not { IsCompletedSuccessfully: true, Result: null })
                return;
            LinkSyncStop(reconnect.Device.Id, CLAIM_TEST_WORK);
            File.WriteAllText(Path.Combine(local, "clean.txt"), "A" + CLAIM_TEST_BASE[1..]);
            File.WriteAllText(Path.Combine(remote, "clean.txt"), CLAIM_TEST_BASE[..^2] + "I\n");
            File.WriteAllText(Path.Combine(local, "overlap.txt"), ours);
            File.WriteAllText(Path.Combine(remote, "overlap.txt"), theirs);
            merged = FlushClaimsTestSync(reconnect.Device.Id);
        });
        test.WaitFor(() => merged is { IsCompleted: true }, 35.0f);
        test.Check("link-claims: disjoint edits converged on both disks", () =>
            merged is { IsCompletedSuccessfully: true, Result: null }
            && ReadProvisionTestFile(Path.Combine(local, "clean.txt")) == clean && ReadProvisionTestFile(Path.Combine(remote, "clean.txt")) == clean);

        string wire = CLAIM_TEST_WORK + "/overlap.txt";
        test.Check("link-claims: overlapping edits left one conflict and both originals", () =>
            reconnect is not null && LinkSyncOfPath(reconnect.Device.Id, wire)?.Conflicts.Count == 1
            && ReadProvisionTestFile(Path.Combine(local, "overlap.txt")) == ours
            && ReadProvisionTestFile(Path.Combine(remote, "overlap.txt")) == theirs);
        test.Run(() =>
        {
            if (reconnect is not null)
                LinkOpenConflictDifference(reconnect.Device.Id, wire);
            UpdateConflicts(now: true);
        });
        test.Check("link-claims: the overlap preview uses one native diff3 conflict", () =>
            linkMergeDrafts.ContainsKey(document) && HasConflicts() && conflicts.Count == 1);
        test.Run(() =>
        {
            if (!linkMergeDrafts.TryGetValue(document, out LinkMergeDraft? draft) || !HasConflicts())
                return;
            SettleConflict(GitConflictPick.Incoming);
            LinkPublishMerge(document, draft);
        });
        const string resolved = "A\nb\nc\nd\ntheirs\nf\ng\nh\nI\n";
        test.WaitFor(() => ReadProvisionTestFile(Path.Combine(remote, "overlap.txt")) == resolved, 25.0f);
        test.Check("link-claims: accepting the overlap published the resolution to both disks", () =>
            ReadProvisionTestFile(Path.Combine(local, "overlap.txt")) == resolved && ReadProvisionTestFile(Path.Combine(remote, "overlap.txt")) == resolved);
        test.Run(() => File.WriteAllText(Path.Combine(peerHome, "claim-finished"), "finished"));
        test.WaitFor(() => linkTestPeer is { HasExited: true }, 45.0f);
        test.Check("link-claims: joiner completed its checks", () => linkTestPeer is { HasExited: true, ExitCode: 0 });
        test.Run(() =>
        {
            StopLink();
            if (holder is not null)
                agentChats.Remove(holder);
            linkTestExtraFolders = [];
        });
    }

    private void TestLinkClaimsJoiner(HexPointerScript test, string local, string owner)
    {
        string home = System.Environment.GetEnvironmentVariable("HEX_HOME")!;
        string file = Path.Combine(local, CLAIM_TEST_FILE);
        HexLinkPeer? first = null;
        HexAgentChat? agent = null;
        HexLinkClaim? oldClaim = null;
        Task<McpToolResult>? edit = null, wrote = null, plan = null, mail = null, list = null, claim = null, release = null;
        test.Run(() =>
        {
            StopLink();
            explorer.OpenFolder(local);
            settings.LinkName = "Claims Joiner";
            if (System.Environment.GetEnvironmentVariable(LINK_TEST_DIAL) is not { } dial
                || !TryLinkAddress(dial, HexLinkTransport.DEFAULT_PORT, out string host, out int port)
                || !StartLink(port: 0, home: Path.Combine(home, "link"), loopbackOnly: true))
                return;
            linkByTest = true;
            linkServices!.FolderAuthorities.Bind(new HexLinkFolder(CLAIM_TEST_WORK, local), new HexLinkAuthority(owner, 1));
            agent = new HexAgentChat(++agentChatsMade, "link_stand_in") { Name = "Claims Joiner Agent" };
            agentChats.Add(agent);
            _ = LinkPair(host, port, _ => Task.FromResult<HexLinkRights?>(CLAIM_TEST_RIGHTS));
        });
        test.WaitFor(() => (first = LinkPeers.FirstOrDefault()) is not null, 85.0f);
        test.WaitFor(() => (oldClaim = HostClaim(HexLinkClaimKind.Planned)) is not null, 35.0f);
        test.Check("link-claims: the host plan holder and ETA reached the joiner", () =>
            oldClaim is { PlanStep: "Edit the shared file", Eta: not null, ChatId: > 0 } && oldClaim.Holder == $"#{oldClaim.ChatId} Claims Builder");
        test.Run(() =>
        {
            if (agent is null || oldClaim?.ChatId is not { } id)
                return;
            edit = McpTools.CallAs("hex_edit", new JsonObject
            {
                ["path"] = file, ["old_string"] = "Shared", ["new_string"] = "No", ["check"] = false
            }, CancellationToken.None, agent.Id);
            wrote = McpTools.CallAs("hex_write", new JsonObject { ["path"] = file, ["content"] = "No\n", ["check"] = false },
                CancellationToken.None, agent.Id);
            plan = McpTools.CallAs("hex_plan_update", ClaimTestPlan("doing"), CancellationToken.None, agent.Id);
            list = McpTools.CallAs("hex_agent_list", [], CancellationToken.None, agent.Id);
            mail = McpTools.CallAs("hex_agent_message", new JsonObject
            {
                ["to"] = $"{owner}:{id}", ["kind"] = "question", ["text"] = CLAIM_TEST_MAIL
            }, CancellationToken.None, agent.Id);
        });
        test.WaitFor(() => new[] { edit, wrote, plan, list, mail }.All(call => call is { IsCompleted: true }), 25.0f);
        test.Check("link-claims: edit and write refused the claimant's file and gave the exact message call", () =>
            edit is { IsCompletedSuccessfully: true, Result.IsError: true } && wrote is { IsCompletedSuccessfully: true, Result.IsError: true }
            && edit.Result.AllText().Contains("hex_agent_message", StringComparison.Ordinal)
            && edit.Result.AllText().Contains("Claims Builder", StringComparison.Ordinal));
        test.Check("link-claims: plan update and agent list show the remote holder and file", () =>
            plan is { IsCompletedSuccessfully: true, Result.IsError: false } && plan.Result.AllText().Contains("Also named by", StringComparison.Ordinal)
            && list is { IsCompletedSuccessfully: true, Result.IsError: false } && list.Result.AllText().Contains(CLAIM_TEST_FILE, StringComparison.Ordinal));
        test.Check("link-claims: remote-holder mail succeeded", () => mail is { IsCompletedSuccessfully: true, Result.IsError: false });
        test.Run(() => File.WriteAllText(Path.Combine(home, "claim-mail-ack"), "acknowledged"));

        test.WaitFor(() => LinkPeers.FirstOrDefault() is { } again && again != first
            && HostClaim(HexLinkClaimKind.Planned) is { } renewed && renewed.ExpiresTick > oldClaim?.ExpiresTick, 65.0f);
        test.Check("link-claims: replanted host claim refreshed its lease after reconnect", () =>
            LinkPeers.FirstOrDefault() is { } again && again != first
            && HostClaim(HexLinkClaimKind.Planned) is { } renewed && renewed.ExpiresTick > oldClaim?.ExpiresTick);
        test.Run(() => File.WriteAllText(Path.Combine(home, "claim-reattached"), "reattached"));
        test.WaitFor(() => HostClaim(HexLinkClaimKind.Planned) is null, 24.0f);
        test.Check("link-claims: plan completion released the remote claim", () => HostClaim(HexLinkClaimKind.Planned) is null);

        string explicitFile = Path.Combine(local, "explicit.txt");
        test.Run(() =>
        {
            if (agent is not null)
                claim = McpTools.CallAs("hex_link_claims", new JsonObject { ["action"] = "claim", ["path"] = explicitFile },
                    CancellationToken.None, agent.Id);
        });
        test.WaitFor(() => claim is { IsCompleted: true } && LinkClaimsAt(explicitFile).Any(item => item.ChatId == agent?.Id), 18.0f);
        test.Check("link-claims: agent took an explicit claim through MCP", () => claim is { IsCompletedSuccessfully: true, Result.IsError: false });
        test.Run(() =>
        {
            if (agent is not null)
                release = McpTools.CallAs("hex_link_claims", new JsonObject { ["action"] = "release", ["path"] = explicitFile },
                    CancellationToken.None, agent.Id);
        });
        test.WaitFor(() => release is { IsCompleted: true } && LinkClaimsAt(explicitFile).Count == 0, 18.0f);
        test.Check("link-claims: agent released its explicit claim", () =>
            release is { IsCompletedSuccessfully: true, Result.IsError: false } && LinkClaimsAt(explicitFile).Count == 0);

        test.WaitFor(() => HostClaim(HexLinkClaimKind.Editing) is not null, 35.0f);
        test.Check("link-claims: first edit created a human claim on the host", () => HostClaim(HexLinkClaimKind.Editing) is { ChatId: null });
        test.Run(() =>
        {
            if (HostClaim(HexLinkClaimKind.Editing) is { } typing)
                LinkOpenClaimDocument(typing);
        });
        test.WaitFor(() => linkDocument?.Peer?.Device.Id == owner, 25.0f);
        test.Check("link-claims: the claim's Open Shared action joined the published buffer", () =>
            linkDocument?.Peer?.Device.Id == owner && document.Editor.Text.Contains("Typing after releasing", StringComparison.Ordinal));
        test.Run(() => File.WriteAllText(Path.Combine(home, "claim-joined-document"), "joined"));
        test.WaitFor(() => File.Exists(Path.Combine(home, "claim-finished")), 120.0f);
        test.Check("link-claims: host completed its merge checks", () => File.Exists(Path.Combine(home, "claim-finished")));
        test.Run(() =>
        {
            StopLink();
            if (agent is not null)
                agentChats.Remove(agent);
        });

        HexLinkClaim? HostClaim(HexLinkClaimKind kind) => LinkClaimsAt(file).FirstOrDefault(item => item.DeviceId == owner && item.Kind == kind);
    }

    private async Task<string?> FlushClaimsTestSync(string deviceId)
    {
        string? why = await LinkSyncStart(deviceId, CLAIM_TEST_WORK);
        if (why is not null || LinkSyncOf(deviceId, CLAIM_TEST_WORK) is not { } sync)
            return why ?? "No working-tree sync.";
        await sync.Flush();
        return null;
    }

    private static JsonObject ClaimTestPlan(string state) => new()
    {
        ["items"] = new JsonArray(new JsonObject
        {
            ["id"] = CLAIM_TEST_PLAN, ["text"] = "Edit the shared file", ["state"] = state,
            ["files"] = new JsonArray(CLAIM_TEST_FILE), ["eta_seconds"] = 180
        })
    };
}
