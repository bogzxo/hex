using Hex.Core;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private const string FOLLOW_TEST_DESTINATION = "HEX_LINK_FOLLOW_TEST_DESTINATION";
    private const string FOLLOW_TEST_FOLDER = "follow-root";
    private const string FOLLOW_TEST_CONTENT = "Second commit on a new branch.\n";
    private const string FOLLOW_TEST_REVERSE = "Commit from the joiner.\n";
    private const HexLinkRights FOLLOW_TEST_RIGHTS = HexLinkRights.View | HexLinkRights.Edit;

    private void TestLinkGitFollow(HexPointerScript test)
    {
        if (Environment.GetEnvironmentVariable(FOLLOW_TEST_DESTINATION) is { Length: > 0 } destination)
        {
            TestLinkGitFollowJoiner(test, destination);
            return;
        }

        string home = LinkTestFolder("hex-link-follow-a-", test);
        string peerHome = LinkTestFolder("hex-link-follow-b-", test);
        string root = Path.Combine(home, FOLLOW_TEST_FOLDER);
        string joined = Path.Combine(peerHome, "workspace");
        string ready = Path.Combine(peerHome, "git-follow-ready.txt");
        HexLinkHeadTip? final = null;
        test.Run(() =>
        {
            MakeProvisionTestRepo(root);
            linkTestPeerSaid.Clear();
            StopLink();
            linkTestExtraFolders = [root];
            if (!StartLink(port: 0, home: home, loopbackOnly: true) || linkServices is not { } services)
                return;
            linkByTest = true;
            services.FolderAuthorities.Bind(new HexLinkFolder(FOLLOW_TEST_FOLDER, root), new HexLinkAuthority(services.Device.Id, 1));
            LinkOpenPairing(TimeSpan.FromMinutes(3));
            LinkStartPeer(peerHome, $"127.0.0.1:{LinkPort}", new Dictionary<string, string> { [FOLLOW_TEST_DESTINATION] = joined },
                suite: "link-git-follow");
        });
        test.Check("link-git-follow: linked fixture started through loopback", () => linkTestPeer is not null && LinkTestBoundOnlyToLoopback);
        test.WaitFor(() => LinkPairingPending is not null || linkTestPeer is { HasExited: true }, 90.0f);
        test.Run(() => LinkPairingPending?.Decide(FOLLOW_TEST_RIGHTS));
        test.WaitFor(() => File.Exists(ready) || linkTestPeer is { HasExited: true }, 100.0f);
        test.Check("link-git-follow: joiner got the folder and its authority", () => File.Exists(ready));
        test.Run(() =>
        {
            ProvisionTestGit(root, "checkout", "-qb", "device-topic");
            File.WriteAllText(Path.Combine(root, "committed.txt"), "First commit.\n");
            ProvisionTestGit(root, "commit", "-qam", "first");
            File.WriteAllText(Path.Combine(root, "committed.txt"), FOLLOW_TEST_CONTENT);
            ProvisionTestGit(root, "commit", "-qam", "second");
            final = HexLinkGitFollow.Read(root, CancellationToken.None);
            File.WriteAllText(Path.Combine(peerHome, "git-follow-target.txt"), final.Head);
        });
        test.WaitFor(() => ReadProvisionTestFile(Path.Combine(joined, "committed.txt")) == FOLLOW_TEST_CONTENT
            || File.Exists(Path.Combine(peerHome, "git-follow-returned.txt")), 75.0f);
        test.WaitFor(() => ReadProvisionTestFile(Path.Combine(root, "committed.txt")) == FOLLOW_TEST_REVERSE, 85.0f);
        test.Check("link-git-follow: the joiner's commit and working file came back", () =>
            ReadProvisionTestFile(Path.Combine(root, "committed.txt")) == FOLLOW_TEST_REVERSE
            && ReadProvisionTestFile(Path.Combine(peerHome, "git-follow-returned.txt")) == ProvisionTestGit(root, "rev-parse", "HEAD"));
        test.Check("link-git-follow: history, index and checkout agree on the branch", () =>
            final is not null && ProvisionTestGit(root, "log", "-2", "--format=%s").ReplaceLineEndings("\n") == "reverse\nsecond"
            && ProvisionTestGit(root, "branch", "--show-current") == "device-topic"
            && ProvisionTestGit(root, "status", "--porcelain") == string.Empty);
        test.WaitFor(() => linkTestPeer is { HasExited: true }, 45.0f);
        test.Check("link-git-follow: child verified history and a clean index", () => linkTestPeer is { HasExited: true, ExitCode: 0 });
        test.Run(StopLink);
    }

    private void TestLinkGitFollowJoiner(HexPointerScript test, string destination)
    {
        string home = Environment.GetEnvironmentVariable("HEX_HOME")!;
        string? dial = Environment.GetEnvironmentVariable(LINK_TEST_DIAL);
        Task<string?>? get = null;
        HexLinkPeer? source = null;

        test.Run(() =>
        {
            StopLink();
            if (dial is null || !TryLinkAddress(dial, HexLinkTransport.DEFAULT_PORT, out string host, out int port)
                || !StartLink(port: 0, home: Path.Combine(home, "follow-link"), loopbackOnly: true))
                return;
            linkByTest = true;
            _ = LinkPair(host, port, _ => Task.FromResult<HexLinkRights?>(FOLLOW_TEST_RIGHTS));
        });
        test.WaitFor(() => (source = LinkPeers.FirstOrDefault())?.Workspace.Folders.Any(folder => folder.Name == FOLLOW_TEST_FOLDER) == true, 70.0f);
        test.Run(() =>
        {
            if (source is not null)
                get = LinkGetWorkspace(source.Device.Id, destination);
        });
        test.WaitFor(() => get is { IsCompleted: true }, 95.0f);
        test.Check("link-git-follow: workspace provisioning succeeded", () =>
        {
            if (get is { IsCompletedSuccessfully: true, Result: null })
                return true;
            Console.WriteLine($"[Hex self-test] link-git-follow: {(get?.IsCompletedSuccessfully == true ? get.Result : get?.Exception?.GetBaseException().Message)}");
            return false;
        });
        test.Run(() =>
        {
            if (source is not null && get is { IsCompletedSuccessfully: true, Result: null }
                && linkServices?.FolderAuthorities.Of(new HexLinkFolder(FOLLOW_TEST_FOLDER, destination))?.DeviceId == source.Device.Id)
                File.WriteAllText(Path.Combine(home, "git-follow-ready.txt"), "ready");
        });

        string target = Path.Combine(home, "git-follow-target.txt");
        string file = Path.Combine(destination, "committed.txt");
        test.WaitFor(() => ReadProvisionTestFile(target) is { } sha && ReadProvisionTestFile(file) == FOLLOW_TEST_CONTENT
            && HexLinkGitFollowScan.For(destination).Read(CancellationToken.None) is { Branch: "device-topic" } tip && tip.Head == sha, 80.0f);
        test.Check("link-git-follow: new branch and two commits arrived with a clean worktree", () =>
            ReadProvisionTestFile(file) == FOLLOW_TEST_CONTENT
            && ProvisionTestGit(destination, "rev-parse", "HEAD") == ReadProvisionTestFile(target)
            && ProvisionTestGit(destination, "branch", "--show-current") == "device-topic"
            && ProvisionTestGit(destination, "status", "--porcelain") == string.Empty);
        test.Run(() =>
        {
            ProvisionTestGit(destination, "config", "user.email", "link.test@example.com");
            ProvisionTestGit(destination, "config", "user.name", "Link Test");
            File.WriteAllText(file, FOLLOW_TEST_REVERSE);
            ProvisionTestGit(destination, "commit", "-qam", "reverse");
            File.WriteAllText(Path.Combine(home, "git-follow-returned.txt"), ProvisionTestGit(destination, "rev-parse", "HEAD"));
        });
        test.WaitFor(() => source is not null && LinkSyncOf(source.Device.Id, FOLLOW_TEST_FOLDER) is { } sync
            && sync.RemoteHead == ReadProvisionTestFile(Path.Combine(home, "git-follow-returned.txt")) && sync.Queued == 0, 85.0f);
        test.Check("link-git-follow: the authority accepted the joiner's commit", () =>
            source is not null && LinkSyncOf(source.Device.Id, FOLLOW_TEST_FOLDER)?.RemoteHead == ReadProvisionTestFile(Path.Combine(home, "git-follow-returned.txt")));
        test.Run(StopLink);
    }
}
