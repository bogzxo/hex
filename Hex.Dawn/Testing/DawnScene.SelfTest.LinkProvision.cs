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
            && !Directory.Exists(Path.Combine(joiner, "out"))
            && !File.Exists(Path.Combine(joiner, "outside-limit.tar")));

        HexLinkPeer? replacement = null;
        test.Run(() =>
        {
            if (peer is null)
                return;
            LinkSetRights(peer.Device.Id, PROVISION_TEST_EDIT);
            link?.Remove(peer.Device.Id, "the provisioning self-test is checking reconnect");
        });
        test.WaitFor(() => (replacement = LinkPeers.FirstOrDefault()) is not null && replacement != peer, 50.0f);
        test.Check("link-provision: dropped connection redialled", () => replacement is not null && replacement != peer);
        test.Run(() => File.WriteAllText(Path.Combine(submodule, "committed.txt"), PROVISION_TEST_AFTER));
        test.WaitFor(() => ReadProvisionTestFile(Path.Combine(joinedModule, "committed.txt")) == PROVISION_TEST_AFTER, 35.0f);
        test.Check("link-provision: a host edit reached the joiner after automatic reconnect", () =>
            ReadProvisionTestFile(Path.Combine(joinedModule, "committed.txt")) == PROVISION_TEST_AFTER);
        test.WaitFor(() => ReadProvisionTestFile(Path.Combine(nested, "joiner.txt")) == PROVISION_TEST_BACK, 35.0f);
        test.Check("link-provision: a later joiner edit reached the host", () =>
            ReadProvisionTestFile(Path.Combine(nested, "joiner.txt")) == PROVISION_TEST_BACK);
        test.WaitFor(() => linkTestPeer is { HasExited: true }, 50.0f);
        test.Check("link-provision: joiner completed its checks before the host closed the link", () =>
            linkTestPeer is { HasExited: true, ExitCode: 0 });
        test.Run(StopLink);
    }

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
            _ = LinkPair(host, port, _ => Task.FromResult<HexLinkRights?>(PROVISION_TEST_EDIT));
        });
        test.Check("link-provision: joiner started empty", () => started && !Directory.Exists(destination));
        test.WaitFor(() => LinkPeers.FirstOrDefault() is { } peer
            && peer.Workspace.Folders.Any(folder => folder.Name == LINK_PROVISION_TEST_SOURCE), 80.0f);
        test.Run(() =>
        {
            if (LinkPeers.FirstOrDefault() is not { } peer)
                return;
            hostId = peer.Device.Id;
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
            && getting.Result.AllText().Contains("Skipped outside-limit.tar. The working file exceeds", StringComparison.Ordinal));

        string changed = Path.Combine(destination, "nested", "module", "committed.txt");
        test.WaitFor(() => ReadProvisionTestFile(changed) == PROVISION_TEST_AFTER
            && hostId is not null && LinkSyncOf(hostId, LINK_PROVISION_TEST_SOURCE) is { PullOnly: false }, 95.0f);
        test.Check("link-provision: joiner resumed the saved folder alias and received its host's edit", () =>
            ReadProvisionTestFile(changed) == PROVISION_TEST_AFTER);
        test.Run(() => File.WriteAllText(Path.Combine(destination, "nested", "joiner.txt"), PROVISION_TEST_BACK));
        Task? flushed = null;
        test.Run(() =>
        {
            if (hostId is not null && LinkSyncOf(hostId, LINK_PROVISION_TEST_SOURCE) is { } sync)
                flushed = sync.Flush();
        });
        test.WaitFor(() => flushed is { IsCompleted: true }, 35.0f);
        test.Check("link-provision: joiner flushed its edit to the host", () =>
        {
            if (flushed?.Exception is { } error)
                Console.WriteLine($"[Hex self-test] link-provision: flush failed, {error.GetBaseException().Message}");
            return flushed is { IsCompletedSuccessfully: true };
        });
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
