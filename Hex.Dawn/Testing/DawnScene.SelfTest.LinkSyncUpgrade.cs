using System.Text.Json.Nodes;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private const string UPGRADE_FOLDER = "upgrade-work";
    private const string UPGRADE_REPLICA = "HEX_LINK_UPGRADE_REPLICA";
    private const string UPGRADE_OWNER = "HEX_LINK_UPGRADE_OWNER";
    private const HexLinkRights UPGRADE_RIGHTS = HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit;

    private void TestLinkSyncUpgrade(HexPointerScript test)
    {
        if (Environment.GetEnvironmentVariable(UPGRADE_REPLICA) is { Length: > 0 } replica)
        {
            TestLinkSyncUpgradeReplica(test, replica);
            return;
        }

        string home = LinkTestFolder("hex-upgrade-a-", test);
        string peerHome = LinkTestFolder("hex-upgrade-b-", test);
        string work = Path.Combine(home, UPGRADE_FOLDER), localReplica = Path.Combine(peerHome, UPGRADE_FOLDER);
        string ready = Path.Combine(peerHome, "migrated"), paused = Path.Combine(peerHome, "paused");
        string provisioned = Path.Combine(peerHome, LINK_PROVISION_BINDINGS);
        string? otherId = null;
        Task<McpToolResult>? resumed = null;
        test.Run(() =>
        {
            if (LinkTestRepository(work, localReplica) is { } why)
                throw new IOException(why);
            using var owner = HexLinkIdentity.LoadOrCreate(home, "Upgrade owner", fileOnly: true);
            using var joiner = HexLinkIdentity.LoadOrCreate(peerHome, "Upgrade replica", fileOnly: true);
            Directory.CreateDirectory(Path.Combine(home, "data"));
            File.WriteAllText(Path.Combine(home, "data", "folder-authority.json"), new JsonObject
            {
                [work] = new HexLinkAuthority(joiner.Device.Id, 1).ToJson()
            }.ToJsonString());
            WriteUpgradeChoice(home, joiner.Device.Id, on: false);
            WriteUpgradeChoice(peerHome, owner.Device.Id, on: true);
            File.WriteAllText(provisioned, new JsonObject
            {
                ["folders"] = new JsonArray(new JsonObject { ["device"] = owner.Device.Id, ["name"] = UPGRADE_FOLDER, ["path"] = localReplica })
            }.ToJsonString());

            StopLink();
            linkTestExtraFolders = [work];
            if (!StartLink(port: 0, home: home, loopbackOnly: true))
                return;
            linkByTest = true;
            otherId = joiner.Device.Id;
            LinkOpenPairing(TimeSpan.FromMinutes(3));
            LinkStartPeer(peerHome, $"127.0.0.1:{LinkPort}", new Dictionary<string, string>
            {
                [UPGRADE_REPLICA] = localReplica, [UPGRADE_OWNER] = owner.Device.Id
            }, suite: "link-sync-upgrade");
        });
        test.Check("link-sync-upgrade: the election pin was cleared before the replica connected", () =>
            otherId is not null && linkServices?.FolderAuthorities.Of(new HexLinkFolder(UPGRADE_FOLDER, work)) is null);

        test.WaitFor(() => LinkPairingPending is not null || linkTestPeer is { HasExited: true }, 45);
        test.Run(() => LinkPairingPending?.Decide(UPGRADE_RIGHTS));
        test.WaitFor(() => File.Exists(ready) || linkTestPeer is { HasExited: true }, 50);
        test.Check("link-sync-upgrade: old Get recovered the authenticated source without discarding the stopped choice", () =>
            File.Exists(ready) && otherId is not null && LinkSyncOptedOut(otherId, UPGRADE_FOLDER) &&
            linkServices?.FolderAuthorities.Of(new HexLinkFolder(UPGRADE_FOLDER, work))?.DeviceId == LinkDevice?.Id);
        test.Run(() => File.WriteAllText(Path.Combine(peerHome, "resume-here"), "go"));
        test.WaitFor(() => otherId is not null && LinkSyncOf(otherId, UPGRADE_FOLDER) is { State: HexLinkSyncState.Idle }, 35);
        test.Check("link-sync-upgrade: replica start clears the owner's old stop and runs one engine", () =>
            otherId is not null && !LinkSyncOptedOut(otherId, UPGRADE_FOLDER) &&
            LinkSyncOf(otherId, UPGRADE_FOLDER) is { BoundAuthorityEngine: true, State: HexLinkSyncState.Idle });
        test.Run(() => File.WriteAllText(Path.Combine(work, "resume-one.txt"), "made by the owner"));
        test.WaitFor(() => File.Exists(paused) && otherId is not null && LinkSyncOf(otherId, UPGRADE_FOLDER)?.State == HexLinkSyncState.Off, 35);
        test.Check("link-sync-upgrade: the owner sees the replica's stop and its author", () =>
            File.Exists(paused) && otherId is not null && LinkPeers.FirstOrDefault()?.PausedSyncFolders
                .Any(item => item.Folder == UPGRADE_FOLDER && item.By == "Upgrade fixture") == true
            && LinkSyncStatusSaid(otherId).Contains("stopped by Upgrade fixture", StringComparison.Ordinal));
        test.Run(() =>
        {
            if (otherId is not null)
                resumed = StartUpgradeSync(otherId);
        });
        test.WaitFor(() => resumed is { IsCompleted: true }, 35);
        test.Check("link-sync-upgrade: owner start clears the replica's explicit stop", () =>
            resumed is { IsCompletedSuccessfully: true, Result.IsError: false });
        test.Run(() => File.WriteAllText(Path.Combine(work, "resume-two.txt"), "owner resumed"));
        test.WaitFor(() => linkTestPeer is { HasExited: true }, 45);
        test.Check("link-sync-upgrade: both resumes transferred a file", () =>
            linkTestPeer is { HasExited: true, ExitCode: 0 } &&
            ReadProvisionTestFile(Path.Combine(localReplica, "resume-two.txt")) == "owner resumed");
        test.Run(TidyLinkTest);
    }

    private void TestLinkSyncUpgradeReplica(HexPointerScript test, string folder)
    {
        string home = Environment.GetEnvironmentVariable("HEX_HOME")!;
        string owner = Environment.GetEnvironmentVariable(UPGRADE_OWNER)!;
        Task<McpToolResult>? started = null;
        test.Run(() =>
        {
            StopLink();
            string? dial = Environment.GetEnvironmentVariable(LINK_TEST_DIAL);
            linkTestExtraFolders = [folder];
            if (dial is null || !TryLinkAddress(dial, HexLinkTransport.DEFAULT_PORT, out string host, out int port) ||
                !StartLink(port: 0, home: home, loopbackOnly: true))
                return;
            linkByTest = true;
            _ = LinkPair(host, port, _ => Task.FromResult<HexLinkRights?>(UPGRADE_RIGHTS));
        });
        bool Migrated() => LinkPeers.FirstOrDefault() is { } peer &&
            linkServices?.FolderAuthorities.Of(new HexLinkFolder(UPGRADE_FOLDER, folder))?.DeviceId == owner &&
            peer.PausedSyncFolders.Any(item => item.Folder == UPGRADE_FOLDER) &&
            LinkSyncStatusSaid(owner).Contains("stopped by an unknown caller", StringComparison.Ordinal);
        test.WaitFor(Migrated, 45);
        test.Check("link-sync-upgrade: old provisioning source became the verified authority and status explains the stop", Migrated);
        test.Run(() => { if (Migrated()) File.WriteAllText(Path.Combine(home, "migrated"), "ready"); });
        test.WaitFor(() => File.Exists(Path.Combine(home, "resume-here")), 40);
        test.Run(() => started = StartUpgradeSync(owner));
        test.WaitFor(() => started is { IsCompleted: true }, 30);
        test.Check("link-sync-upgrade: local MCP start resumed the stopped pair", () =>
            started is { IsCompletedSuccessfully: true, Result.IsError: false } &&
            LinkSyncOf(owner, UPGRADE_FOLDER) is null or { State: HexLinkSyncState.Off });
        test.WaitFor(() => ReadProvisionTestFile(Path.Combine(folder, "resume-one.txt")) == "made by the owner", 30);
        test.Check("link-sync-upgrade: the owner sent a file after the replica resumed", () =>
            ReadProvisionTestFile(Path.Combine(folder, "resume-one.txt")) == "made by the owner");
        test.Run(() =>
        {
            LinkSyncStop(owner, UPGRADE_FOLDER, "Upgrade fixture");
            File.WriteAllText(Path.Combine(home, "paused"), "paused");
        });
        test.WaitFor(() => ReadProvisionTestFile(Path.Combine(folder, "resume-two.txt")) == "owner resumed", 40);
        test.Check("link-sync-upgrade: the owner resumed remotely and the replica has the bytes", () =>
            !LinkSyncOptedOut(owner, UPGRADE_FOLDER) && ReadProvisionTestFile(Path.Combine(folder, "resume-two.txt")) == "owner resumed");
        test.Run(StopLink);
    }

    private Task<McpToolResult> StartUpgradeSync(string device) => LinkSyncTool(new JsonObject
    {
        ["device"] = device, ["folder"] = UPGRADE_FOLDER, ["action"] = "start"
    }, CancellationToken.None);

    private static void WriteUpgradeChoice(string home, string device, bool on) =>
        File.WriteAllText(Path.Combine(home, LINK_SYNC_CHOICES), new JsonObject
        {
            ["syncs"] = new JsonArray(new JsonObject { ["device"] = device, ["folder"] = UPGRADE_FOLDER, ["enabled"] = on })
        }.ToJsonString());
}
