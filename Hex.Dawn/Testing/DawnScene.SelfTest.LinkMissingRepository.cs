namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private const string MISSING_TEST_CHILD = "HEX_LINK_MISSING_REPOSITORY_CHILD";
    private const string MISSING_TEST_FOLDER = "partial-work";
    private const string MISSING_TEST_TEXT = "The ordinary file still syncs.\n";
    private const string MISSING_TEST_NEXT = "Synced after fetching just the missing repository.\n";

    private void TestLinkMissingRepository(HexPointerScript test)
    {
        if (System.Environment.GetEnvironmentVariable(MISSING_TEST_CHILD) is { } child)
        {
            TestLinkMissingRepositoryJoiner(test, child);
            return;
        }

        string home = LinkTestFolder("hex-partial-a-", test);
        string peerHome = LinkTestFolder("hex-partial-b-", test);
        string root = Path.Combine(home, MISSING_TEST_FOLDER), guest = Path.Combine(peerHome, MISSING_TEST_FOLDER);
        string repository = Path.Combine(root, "nested"), target = Path.Combine(guest, "nested");
        string? setup = null;
        HexLinkSync? initial = null;
        HexLinkPeer? peer = null;
        test.Run(() =>
        {
            try
            {
                MakeProvisionTestRepo(root, "/nested/\n/other/\n");
                ProvisionTestGit(peerHome, "clone", "-q", root, guest);
                MakeProvisionTestRepo(repository);
                MakeProvisionTestRepo(Path.Combine(root, "other"));
                ProvisionTestGit(repository, "remote", "add", "origin", "https://invalid.example/nested.git");
                File.WriteAllText(Path.Combine(repository, "local.txt"), "Host-only work before Get.\n");
                StopLink();
                linkTestExtraFolders = [root];
                if (!StartLink(port: 0, home: home, loopbackOnly: true))
                    throw new InvalidOperationException("The host could not listen on loopback.");
                linkByTest = true;
                LinkOpenPairing(TimeSpan.FromMinutes(3));
                LinkStartPeer(peerHome, $"127.0.0.1:{LinkPort}",
                    new Dictionary<string, string> { [MISSING_TEST_CHILD] = guest }, suite: "link-missing-repository");
            }
            catch (Exception e)
            {
                setup = e.Message;
                Console.WriteLine($"[Hex self-test] link-missing-repository: {setup}");
            }
        });
        test.Check("link-missing-repository: fixtures share their root commit and the joiner lacks the nested checkouts", () =>
            setup is null && linkTestPeer is not null && Directory.Exists(guest) && !Directory.Exists(target));
        test.WaitFor(() => LinkPairingPending is not null || linkTestPeer is { HasExited: true }, 90.0f);
        test.Run(() => LinkPairingPending?.Decide(PROVISION_TEST_EDIT));
        test.WaitFor(() => (peer = LinkPeers.FirstOrDefault()) is not null && (initial = LinkSyncOf(peer.Device.Id, MISSING_TEST_FOLDER)) is
            { State: HexLinkSyncState.Idle, MissingRepositories.There.Count: 2 }, 65.0f);
        test.Check("link-missing-repository: the host owns a running sync and sees two absent repositories", () =>
            initial is { State: HexLinkSyncState.Idle, MissingRepositories.There.Count: 2 });

        test.Run(() => File.WriteAllText(Path.Combine(root, "ordinary.txt"), MISSING_TEST_TEXT));
        test.WaitFor(() => ReadProvisionTestFile(Path.Combine(peerHome, "partial-ready")) == "ready", 100.0f);
        test.Check("link-missing-repository: the joiner got the requested branch, commit, origin and untracked file", () =>
            ReadProvisionTestFile(Path.Combine(peerHome, "partial-ready")) == "ready" && Directory.Exists(target)
            && ProvisionTestGit(repository, "rev-parse", "HEAD") == ProvisionTestGit(target, "rev-parse", "HEAD")
            && ProvisionTestGit(target, "branch", "--show-current") == "development"
            && ProvisionTestGit(target, "remote", "get-url", "origin") == "https://invalid.example/nested.git"
            && ReadProvisionTestFile(Path.Combine(target, "local.txt")) == "Host-only work before Get.\n"
            && !Directory.Exists(Path.Combine(guest, "other")));
        test.WaitFor(() => peer is not null && LinkSyncOf(peer.Device.Id, MISSING_TEST_FOLDER) is
            { State: HexLinkSyncState.Idle, MissingRepositories.There.Count: 1 } renewed && renewed != initial, 55.0f);
        test.Check("link-missing-repository: the existing host sync refreshed its repository boundary", () =>
            peer is not null && LinkSyncOf(peer.Device.Id, MISSING_TEST_FOLDER) is
                { State: HexLinkSyncState.Idle, MissingRepositories.There.Count: 1 } renewed && renewed != initial);
        test.Run(() => File.WriteAllText(Path.Combine(repository, "local.txt"), MISSING_TEST_NEXT));
        test.WaitFor(() => ReadProvisionTestFile(Path.Combine(target, "local.txt")) == MISSING_TEST_NEXT, 40.0f);
        test.Check("link-missing-repository: a later edit syncs inside the newly fetched repository", () =>
            ReadProvisionTestFile(Path.Combine(target, "local.txt")) == MISSING_TEST_NEXT);
        test.WaitFor(() => linkTestPeer is { HasExited: true }, 50.0f);
        test.Check("link-missing-repository: the joiner passed its checks", () => linkTestPeer is { HasExited: true, ExitCode: 0 });
        test.Run(() =>
        {
            StopLink();
            linkTestExtraFolders = [];
        });
    }

    private void TestLinkMissingRepositoryJoiner(HexPointerScript test, string local)
    {
        string home = System.Environment.GetEnvironmentVariable("HEX_HOME")!;
        string nested = Path.Combine(local, "nested");
        Task<string?>? getting = null;
        test.Run(() =>
        {
            StopLink();
            explorer.OpenFolder(local);
            settings.LinkName = "Missing repository joiner";
            if (System.Environment.GetEnvironmentVariable(LINK_TEST_DIAL) is not { } dial
                || !TryLinkAddress(dial, HexLinkTransport.DEFAULT_PORT, out string host, out int port)
                || !StartLink(port: 0, home: Path.Combine(home, "link"), loopbackOnly: true))
                return;
            linkByTest = true;
            _ = LinkPair(host, port, _ => Task.FromResult<HexLinkRights?>(PROVISION_TEST_EDIT));
        });
        test.WaitFor(() => ReadProvisionTestFile(Path.Combine(local, "ordinary.txt")) == MISSING_TEST_TEXT, 80.0f);
        test.Check("link-missing-repository: ordinary edits arrived without creating the missing checkout", () =>
            ReadProvisionTestFile(Path.Combine(local, "ordinary.txt")) == MISSING_TEST_TEXT && !Directory.Exists(nested));
        test.Run(() =>
        {
            if (LinkPeers.FirstOrDefault() is { } peer)
                getting = LinkGetMissingRepository(peer.Device.Id, MISSING_TEST_FOLDER, "nested");
        });
        test.WaitFor(() => getting is { IsCompleted: true }, 90.0f);
        test.Check("link-missing-repository: Get downloaded only the selected checkout", () =>
            getting is { IsCompletedSuccessfully: true, Result: null } && Directory.Exists(nested) && !Directory.Exists(Path.Combine(local, "other")));
        test.Run(() =>
        {
            if (getting is { IsCompletedSuccessfully: true, Result: null })
                File.WriteAllText(Path.Combine(home, "partial-ready"), "ready");
            else
                Console.WriteLine($"[Hex self-test] link-missing-repository: {getting?.Exception?.GetBaseException().Message ?? (getting?.IsCompletedSuccessfully == true ? getting.Result : "Get did not complete.")}");
        });
        test.WaitFor(() => ReadProvisionTestFile(Path.Combine(nested, "local.txt")) == MISSING_TEST_NEXT, 85.0f);
        test.Check("link-missing-repository: the joiner received a later edit from the same automatic sync", () =>
            ReadProvisionTestFile(Path.Combine(nested, "local.txt")) == MISSING_TEST_NEXT);
        test.Run(StopLink);
    }
}
