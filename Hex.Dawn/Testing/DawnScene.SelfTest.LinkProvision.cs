using System.Text.Json.Nodes;

using Hex.Core.Git;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private const string LINK_PROVISION_TEST_DESTINATION = "HEX_LINK_PROVISION_TEST_DESTINATION";
    private const string LINK_PROVISION_TEST_SOURCE = "provision-source";
    private const string PROVISION_TEST_UNINITIALIZED = "missing-child";
    private const string PROVISION_TEST_AFTER = "Host edit after getting the workspace.\n";
    private const string PROVISION_TEST_BACK = "Joiner edit after getting the workspace.\n";
    private const HexLinkRights PROVISION_TEST_EDIT = HexLinkRights.View | HexLinkRights.Edit;

    private void TestLinkProvision(HexPointerScript test)
    {
        if (System.Environment.GetEnvironmentVariable(LINK_PROVISION_TEST_DESTINATION) is { Length: > 0 } childDestination)
        {
            TestLinkProvisionJoiner(test, childDestination);
            return;
        }

        string home = LinkTestFolder("hex-link-provision-a-", test);
        string peerHome = LinkTestFolder("hex-link-provision-b-", test);
        string root = Path.Combine(home, LINK_PROVISION_TEST_SOURCE);
        string joiner = Path.Combine(peerHome, "picked-destination");
        string nested = Path.Combine(root, "nested"), submodule = Path.Combine(nested, "module");
        string joinedNested = Path.Combine(joiner, "nested"), joinedModule = Path.Combine(joinedNested, "module");
        string ready = Path.Combine(peerHome, "provision-ready.txt");
        string? setupError = null;
        HexLinkPeer? peer = null;

        test.Run(() =>
        {
            try
            {
                MakeProvisionTestRepo(root, "/nested/\n/out/\n");
                MakeProvisionTestRepo(nested);
                string moduleSource = Path.Combine(home, "module-source");
                MakeProvisionTestRepo(moduleSource);
                string missingCommit = ProvisionTestGit(moduleSource, "rev-parse", "HEAD");
                File.WriteAllText(Path.Combine(moduleSource, ".gitmodules"),
                    $"[submodule \"{PROVISION_TEST_UNINITIALIZED}\"]\n\tpath = {PROVISION_TEST_UNINITIALIZED}\n\turl = https://invalid.example/uninitialized.git\n");
                ProvisionTestGit(moduleSource, "update-index", "--add", "--cacheinfo", $"160000,{missingCommit},{PROVISION_TEST_UNINITIALIZED}");
                ProvisionTestGit(moduleSource, "add", ".gitmodules");
                ProvisionTestGit(moduleSource, "commit", "-qm", "Record the uninitialized child submodule");
                ProvisionTestGit(root, "remote", "add", "origin", "https://invalid.example/root.git");
                ProvisionTestGit(nested, "remote", "add", "origin", "https://invalid.example/nested.git");
                ProvisionTestGit(nested, "-c", "protocol.file.allow=always", "submodule", "add", "-q", moduleSource, "module");
                ProvisionTestGit(nested, "commit", "-qam", "Record the submodule locally");
                ProvisionTestGit(submodule, "remote", "set-url", "origin", "https://invalid.example/module.git");
                File.WriteAllText(Path.Combine(root, "committed.txt"), "Uncommitted root edit.\n");
                File.WriteAllText(Path.Combine(nested, "untracked.txt"), "Untracked nested file.\n");
                File.WriteAllText(Path.Combine(submodule, "committed.txt"), "Uncommitted submodule edit.\n");
                Directory.CreateDirectory(Path.Combine(root, "out"));
                File.WriteAllText(Path.Combine(root, "out", "built.dll"), "Ignored build output.");
                using (FileStream oversized = File.Create(Path.Combine(root, "outside-limit.tar")))
                    oversized.SetLength(HexLinkFiles.MOST_READ + 20L);

                linkTestPeerSaid.Clear();
                StopLink();
                linkTestExtraFolders = [root];
                if (!StartLink(port: 0, home: home, loopbackOnly: true))
                    throw new InvalidOperationException("The host could not listen on loopback.");
                linkByTest = true;
                LinkOpenPairing(TimeSpan.FromMinutes(3));
                LinkStartPeer(peerHome, $"127.0.0.1:{LinkPort}", new Dictionary<string, string>
                {
                    [LINK_PROVISION_TEST_DESTINATION] = joiner
                }, suite: "link-provision");
            }
            catch (Exception e)
            {
                setupError = e.Message;
                Console.WriteLine($"[Hex self-test] link-provision: {setupError}");
            }
        });
        test.Check("link-provision: host fixture ready, joiner has no destination directory", () =>
            setupError is null && linkTestPeer is not null && !Directory.Exists(joiner) && LinkTestBoundOnlyToLoopback);

        test.WaitFor(() => LinkPairingPending is not null || linkTestPeer is { HasExited: true }, 90.0f);
        test.Run(() => LinkPairingPending?.Decide(HexLinkRights.View));
        test.WaitFor(() => (peer = LinkPeers.FirstOrDefault()) is not null || linkTestPeer is { HasExited: true }, 30.0f);
        test.Check("link-provision: joiner paired with View and no advertised workspace folders", () =>
            peer is not null && peer.Workspace.Folders.Count == 0);

        test.WaitFor(() => File.Exists(ready) || linkTestPeer is { HasExited: true }, 100.0f);
        test.Check("link-provision: one Get action completed and started pull-only sync", () => ReadProvisionTestFile(ready) == "ready");
        test.Check("link-provision: root and nested repositories kept their local commit history and branches", () =>
            Directory.Exists(joiner) && Directory.Exists(joinedNested)
            && ProvisionTestGit(root, "rev-parse", "HEAD") == ProvisionTestGit(joiner, "rev-parse", "HEAD")
            && ProvisionTestGit(nested, "rev-parse", "HEAD") == ProvisionTestGit(joinedNested, "rev-parse", "HEAD")
            && ProvisionTestGit(joinedNested, "rev-list", "--count", "HEAD") == "2"
            && ProvisionTestGit(joinedNested, "branch", "--show-current") == "development");
        test.Check("link-provision: submodule initialized from the host's objects with the recorded commit and original remote", () =>
            Directory.Exists(joinedModule) && File.Exists(Path.Combine(joinedModule, ".git"))
            && ProvisionTestGit(submodule, "rev-parse", "HEAD") == ProvisionTestGit(joinedModule, "rev-parse", "HEAD")
            && ProvisionTestGit(joinedModule, "remote", "get-url", "origin") == "https://invalid.example/module.git");
        test.Check("link-provision: the nested uninitialized submodule kept its gitlink and URL", () =>
            Directory.Exists(joinedModule) && !HexLinkProvision.HasRepository(Path.Combine(joinedModule, PROVISION_TEST_UNINITIALIZED))
            && ProvisionTestGit(submodule, "ls-tree", "HEAD", PROVISION_TEST_UNINITIALIZED)
                == ProvisionTestGit(joinedModule, "ls-tree", "HEAD", PROVISION_TEST_UNINITIALIZED)
            && ProvisionTestGit(joinedModule, "config", "-f", ".gitmodules", "--get", $"submodule.{PROVISION_TEST_UNINITIALIZED}.url")
                == "https://invalid.example/uninitialized.git");
        test.Check("link-provision: uncommitted and untracked content arrived, ignored build output stayed on the host", () =>
            ReadProvisionTestFile(Path.Combine(joiner, "committed.txt")) == "Uncommitted root edit.\n"
            && ReadProvisionTestFile(Path.Combine(joinedNested, "untracked.txt")) == "Untracked nested file.\n"
            && ReadProvisionTestFile(Path.Combine(joinedModule, "committed.txt")) == "Uncommitted submodule edit.\n"
            && !Directory.Exists(Path.Combine(joiner, "out")));
        test.Check("link-provision: a working file over 8 MB arrived through bulk with only View", () =>
            File.Exists(Path.Combine(joiner, "outside-limit.tar"))
            && new FileInfo(Path.Combine(joiner, "outside-limit.tar")).Length == HexLinkFiles.MOST_READ + 20L);

        string upgrade = Path.Combine(peerHome, "allow-owner.txt");
        string viewCheck = Path.Combine(peerHome, "view-verified.txt");
        string mirrorFile = Path.Combine(joiner, "mirror-edit.txt");
        test.Check("link-provision: the owner has no engine when the replica grants no rights", () =>
            peer is not null && !HexLinkAllowed.Has(peer.Granted, HexLinkRights.View)
                && LinkSyncOf(peer.Device.Id, LINK_PROVISION_TEST_SOURCE) is null);
        test.Run(() => File.WriteAllText(Path.Combine(root, "mirror-edit.txt"), "One-way mirror.\n"));
        test.WaitFor(() => ReadProvisionTestFile(mirrorFile) == "One-way mirror.\n", 35.0f);
        test.Check("link-provision: the one-way View mirror received a host edit", () =>
            ReadProvisionTestFile(mirrorFile) == "One-way mirror.\n");
        Task<McpToolResult>? deniedStart = null;
        test.Run(() =>
        {
            if (peer is not null)
                deniedStart = ProvisionTestRemoteSync(peer, "start");
        });
        test.WaitFor(() => deniedStart is { IsCompleted: true }, 20.0f);
        test.Check("link-provision: the authority needs Steer to start the replica remotely", () =>
            deniedStart is { IsCompletedSuccessfully: true, Result.IsError: true } &&
            deniedStart.Result.AllText().Contains("hasn't granted this device steer", StringComparison.Ordinal));

        test.Run(() => File.WriteAllText(upgrade, "grant reverse Edit"));
        test.WaitFor(() => peer is not null && LinkSyncOf(peer.Device.Id, LINK_PROVISION_TEST_SOURCE) is
            { BoundAuthorityEngine: true, State: HexLinkSyncState.Idle }, 45.0f);
        test.Check("link-provision: granting reverse Edit moved the engine to the pinned authority", () =>
            peer is not null && LinkSyncOf(peer.Device.Id, LINK_PROVISION_TEST_SOURCE) is
                { BoundAuthorityEngine: true, State: HexLinkSyncState.Idle });
        test.WaitFor(() => File.Exists(viewCheck), 45.0f);
        test.Check("link-provision: the owner never took the View-only replica's file or commit", () =>
            File.Exists(viewCheck) && !File.Exists(Path.Combine(root, "view-only.txt"))
                && ProvisionTestGit(root, "log", "-1", "--format=%s") == "Initial commit");
        Task<McpToolResult>? remoteStop = null, remoteStart = null;
        test.Run(() =>
        {
            if (peer is not null)
                remoteStop = ProvisionTestRemoteSync(peer, "stop");
        });
        test.WaitFor(() => remoteStop is { IsCompleted: true } && peer is not null &&
            LinkSyncOf(peer.Device.Id, LINK_PROVISION_TEST_SOURCE) is { State: HexLinkSyncState.Off }, 30.0f);
        test.Run(() =>
        {
            if (peer is not null)
                remoteStart = ProvisionTestRemoteSync(peer, "start");
        });
        test.WaitFor(() => remoteStart is { IsCompleted: true }, 35.0f);
        test.Check("link-provision: the authority can resume its paused replica via the audited remote sync tool", () =>
            remoteStop is { IsCompletedSuccessfully: true, Result.IsError: false } &&
            remoteStart is { IsCompletedSuccessfully: true, Result.IsError: false } &&
            remoteStart.Result.AllText().Contains("Sync is on", StringComparison.Ordinal) &&
            peer is not null && LinkSyncOf(peer.Device.Id, LINK_PROVISION_TEST_SOURCE) is { BoundAuthorityEngine: true, State: HexLinkSyncState.Idle });
        test.Run(() =>
        {
            if (peer is not null)
                LinkSetRights(peer.Device.Id, PROVISION_TEST_EDIT);
        });

        HexLinkPeer? replacement = null;
        test.Run(() =>
        {
            int port = LinkPort;
            StopLink();
            linkByTest = StartLink(port: port, home: home, loopbackOnly: true);
        });
        test.WaitFor(() => (replacement = LinkPeers.FirstOrDefault()) is not null && replacement != peer, 50.0f);
        test.Check("link-provision: authority Link restarted and the replica reconnected", () => replacement is not null && replacement != peer);
        test.Run(() => File.WriteAllText(Path.Combine(submodule, "committed.txt"), PROVISION_TEST_AFTER));
        test.WaitFor(() => ReadProvisionTestFile(Path.Combine(joinedModule, "committed.txt")) == PROVISION_TEST_AFTER, 35.0f);
        test.Check("link-provision: a host edit reached the joiner after automatic reconnect", () =>
            ReadProvisionTestFile(Path.Combine(joinedModule, "committed.txt")) == PROVISION_TEST_AFTER);
        test.WaitFor(() => ReadProvisionTestFile(Path.Combine(nested, "joiner.txt")) == PROVISION_TEST_BACK, 35.0f);
        test.Check("link-provision: a later joiner edit reached the host", () =>
            ReadProvisionTestFile(Path.Combine(nested, "joiner.txt")) == PROVISION_TEST_BACK);
        test.WaitFor(() => LinkPeers.FirstOrDefault() is { } rejoined && rejoined != replacement &&
            LinkSyncOf(rejoined.Device.Id, LINK_PROVISION_TEST_SOURCE) is { BoundAuthorityEngine: true, State: HexLinkSyncState.Idle },
            60.0f);
        test.Check("link-provision: the authority resumed its engine after the replica restarted", () =>
            LinkPeers.FirstOrDefault() is { } rejoined && rejoined != replacement &&
            LinkSyncOf(rejoined.Device.Id, LINK_PROVISION_TEST_SOURCE) is { BoundAuthorityEngine: true, State: HexLinkSyncState.Idle });
        test.Run(() => File.WriteAllText(Path.Combine(root, "replica-restart.txt"), "Resumed.\n"));
        test.WaitFor(() => linkTestPeer is { HasExited: true }, 50.0f);
        test.Check("link-provision: joiner completed its checks before the host closed the link", () =>
            linkTestPeer is { HasExited: true, ExitCode: 0 });
        test.Run(StopLink);
    }

    private Task<McpToolResult> ProvisionTestRemoteSync(HexLinkPeer replica, string action) =>
        LinkCall(replica, "hex_link_sync", new JsonObject
        {
            ["device"] = LinkDevice?.Id,
            ["folder"] = LINK_PROVISION_TEST_SOURCE,
            ["action"] = action
        }, CancellationToken.None, caller: null);

    private void TestLinkProvisionJoiner(HexPointerScript test, string destination)
    {
        string home = System.Environment.GetEnvironmentVariable("HEX_HOME")!;
        string ready = Path.Combine(home, "provision-ready.txt");
        string? dial = System.Environment.GetEnvironmentVariable(LINK_TEST_DIAL);
        Task<McpToolResult>? getting = null;
        string? hostId = null;
        bool started = false;

        test.Run(() =>
        {
            StopLink();
            settings.LinkName = "Provision joiner";
            if (dial is null || !TryLinkAddress(dial, HexLinkTransport.DEFAULT_PORT, out string host, out int port)
                || !StartLink(port: 0, home: Path.Combine(home, "provision-link"), loopbackOnly: true))
                return;
            linkByTest = true;
            started = true;
            _ = LinkPair(host, port, _ => Task.FromResult<HexLinkRights?>(HexLinkRights.None));
        });
        test.Check("link-provision: joiner started empty", () => started && !Directory.Exists(destination));
        test.WaitFor(() => LinkPeers.FirstOrDefault() is { } peer
            && peer.Workspace.Folders.Any(folder => folder.Name == LINK_PROVISION_TEST_SOURCE), 80.0f);
        test.Run(() =>
        {
            if (LinkPeers.FirstOrDefault() is not { } peer)
                return;
            hostId = peer.Device.Id;
            ShowSide(HexSide.Search);
            getting = LinkProvisionTool(new JsonObject
            {
                ["device"] = peer.Device.Id, ["action"] = "get", ["destination"] = destination
            }, CancellationToken.None);
        });
        test.WaitFor(() => getting is { IsCompleted: true }, 100.0f);
        test.Run(() =>
        {
            string result = getting?.IsCompletedSuccessfully == true ? getting.Result.AllText()
                : getting?.Exception?.GetBaseException().Message ?? "Get did not complete.";
            Console.WriteLine($"[Hex self-test] link-provision: {result.ReplaceLineEndings(" | ")}");
            if (getting is { IsCompletedSuccessfully: true, Result.IsError: false }
                && hostId is not null && LinkSyncOf(hostId, LINK_PROVISION_TEST_SOURCE) is { State: HexLinkSyncState.PullOnly })
                File.WriteAllText(ready, "ready");
        });
        test.Check("link-provision: one MCP action got the workspace with View", () => ReadProvisionTestFile(ready) == "ready");
        test.Check("link-provision: MCP listed the skipped submodule and its reason", () =>
            getting is { IsCompletedSuccessfully: true, Result.IsError: false }
            && getting.Result.AllText().Contains($"Skipped nested/module/{PROVISION_TEST_UNINITIALIZED}. Submodule is uninitialized", StringComparison.Ordinal)
            && !getting.Result.AllText().Contains("Skipped outside-limit.tar", StringComparison.Ordinal));

        test.Check("link-provision: completion names the added folder, active sync and Open workspace action", () =>
            getting is { IsCompletedSuccessfully: true, Result.IsError: false } &&
            getting.Result.AllText().Contains("added to your workspace. Sync is on.", StringComparison.Ordinal) &&
            getting.Result.AllText().Contains(destination, StringComparison.OrdinalIgnoreCase) &&
            getting.Result.AllText().Contains("Open workspace", StringComparison.Ordinal) &&
            explorer.workspace.Folders.Contains(destination, PlatformPaths.Comparer));
        test.Run(() =>
        {
            foreach (var card in toasts.Cards.Where(card => !card.Closed))
            {
                if (card.Actions.FirstOrDefault(action => action.Label == "Open workspace") is { Pressed: { } pressed })
                {
                    pressed();
                    break;
                }
            }
        });
        test.Check("link-provision: Open workspace reveals the downloaded folders without replacing them", () =>
            ExplorerShowing && explorer.workspace.Folders.Contains(destination, PlatformPaths.Comparer));

        string upgrade = Path.Combine(home, "allow-owner.txt");
        test.WaitFor(() => File.Exists(upgrade), 60.0f);
        test.Run(() =>
        {
            if (hostId is not null)
                LinkSetRights(hostId, PROVISION_TEST_EDIT | HexLinkRights.Steer);
        });
        test.WaitFor(() => LinkPeers.FirstOrDefault() is { } authority &&
            LinkRemoteSyncs(authority).Any(sync => sync.Folder == LINK_PROVISION_TEST_SOURCE && sync.PullOnly)
            && LinkSyncOf(authority.Device.Id, LINK_PROVISION_TEST_SOURCE) is { State: HexLinkSyncState.Off }, 65.0f);
        test.Check("link-provision: the mirror stopped and the replica sees authority-owned pull-only sync", () =>
            LinkPeers.FirstOrDefault() is { } authority &&
            LinkRemoteSyncs(authority).Any(sync => sync.Folder == LINK_PROVISION_TEST_SOURCE && sync.PullOnly)
            && LinkSyncOf(authority.Device.Id, LINK_PROVISION_TEST_SOURCE) is { State: HexLinkSyncState.Off });
        Task<McpToolResult>? readOnlyFlush = null;
        test.Run(() =>
        {
            if (LinkPeers.FirstOrDefault() is not { } authority)
                return;
            ProvisionTestGit(destination, "config", "user.email", "link-test@example.com");
            ProvisionTestGit(destination, "config", "user.name", "Link Test");
            File.WriteAllText(Path.Combine(destination, "view-only.txt"), "Local only.\n");
            ProvisionTestGit(destination, "add", "view-only.txt");
            ProvisionTestGit(destination, "commit", "-qm", "Replica View-only commit");
            readOnlyFlush = LinkSyncTool(new JsonObject
            {
                ["device"] = authority.Device.Id, ["action"] = "flush", ["folder"] = LINK_PROVISION_TEST_SOURCE
            }, CancellationToken.None);
        });
        test.WaitFor(() => readOnlyFlush is { IsCompleted: true }, 35.0f);
        test.Check("link-provision: a View-only replica flush says its local edits are not sent", () =>
            readOnlyFlush is { IsCompletedSuccessfully: true, Result.IsError: false }
                && readOnlyFlush.Result.AllText().Contains("not sent: view only", StringComparison.Ordinal));
        test.Run(() =>
        {
            ProvisionTestGit(destination, "reset", "--mixed", "HEAD^");
            File.Delete(Path.Combine(destination, "view-only.txt"));
            File.WriteAllText(Path.Combine(home, "view-verified.txt"), "checked");
        });

        string changed = Path.Combine(destination, "nested", "module", "committed.txt");
        test.WaitFor(() => ReadProvisionTestFile(changed) == PROVISION_TEST_AFTER && LinkPeers.FirstOrDefault() is { } source
            && LinkRemoteSyncs(source).Any(sync => sync.Folder == LINK_PROVISION_TEST_SOURCE && !sync.PullOnly), 95.0f);
        test.Check("link-provision: joiner resumed the saved folder alias and received its host's edit", () =>
            ReadProvisionTestFile(changed) == PROVISION_TEST_AFTER);
        test.Run(() => File.WriteAllText(Path.Combine(destination, "nested", "joiner.txt"), PROVISION_TEST_BACK));
        Task<string?>? flushed = null;
        test.Run(() =>
        {
            if (LinkPeers.FirstOrDefault() is { } source)
                flushed = LinkFlushSync(source, LINK_PROVISION_TEST_SOURCE, CancellationToken.None, start: false);
        });
        test.WaitFor(() => flushed is { IsCompleted: true }, 35.0f);
        test.Check("link-provision: joiner flushed its edit to the host", () =>
        {
            if (flushed?.Exception is { } error)
                Console.WriteLine($"[Hex self-test] link-provision: flush failed, {error.GetBaseException().Message}");
            if (flushed is { IsCompletedSuccessfully: true, Result: { } failure })
                Console.WriteLine($"[Hex self-test] link-provision: {failure}");
            return flushed is { IsCompletedSuccessfully: true, Result: null };
        });
        test.Run(() =>
        {
            StopLink();
            linkByTest = StartLink(port: 0, home: Path.Combine(home, "provision-link"), loopbackOnly: true);
        });
        string resumed = Path.Combine(destination, "replica-restart.txt");
        test.WaitFor(() => ReadProvisionTestFile(resumed) == "Resumed.\n", 75.0f);
        test.Check("link-provision: the restarted replica kept its saved alias and received a host edit", () =>
            ReadProvisionTestFile(resumed) == "Resumed.\n");
        test.Run(StopLink);
    }

    private static void MakeProvisionTestRepo(string folder, string? ignores = null)
    {
        Directory.CreateDirectory(folder);
        ProvisionTestGit(folder, "init", "-q", "-b", "development");
        ProvisionTestGit(folder, "config", "user.email", "link-test@example.com");
        ProvisionTestGit(folder, "config", "user.name", "Link Test");
        ProvisionTestGit(folder, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(folder, "committed.txt"), "Committed contents.\n");
        if (ignores is not null)
            File.WriteAllText(Path.Combine(folder, ".gitignore"), ignores);
        ProvisionTestGit(folder, "add", ".");
        ProvisionTestGit(folder, "commit", "-qm", "Initial commit");
    }

    private static string ProvisionTestGit(string folder, params string[] args)
    {
        GitResult git = GitProcess.Run(folder, args, patience: TimeSpan.FromMinutes(2), unattended: true);
        return git.Ok ? git.Output.Trim() : throw new InvalidOperationException($"git {args[0]} in {folder}: {git.Error}");
    }

    private static string? ReadProvisionTestFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).ReplaceLineEndings("\n") : null; }
        catch (IOException) { return null; }
    }
}
