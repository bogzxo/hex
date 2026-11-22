using System.Text.Json.Nodes;
using Hex.Core;
using Hex.Core.Git;
using static Hex.Tests.HexLinkTestKit;

namespace Hex.Tests;

public sealed class HexLinkProvisionBulkTests
{
    [Fact]
    public async Task A_one_way_view_get_streams_history_and_working_files_larger_than_the_json_limit()
    {
        if (HexLinkGit.Why() is not null)
            return;
        using var host = new LinkServices(DeviceA);
        using var joiner = new LinkServices(DeviceB);
        using var hostLink = new HexLink(host);
        using var joinerLink = new HexLink(joiner);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var (a, b) = Pair();
        Task<HexLinkPeer> hostView = hostLink.Add(Connection(a, DeviceB, HexLinkRights.View, dialled: false), timeout.Token);
        Task<HexLinkPeer> joinerView = joinerLink.Add(Connection(b, DeviceA, HexLinkRights.None, dialled: true), timeout.Token);
        HexLinkPeer[] peers = await Task.WhenAll(hostView, joinerView);
        Assert.Equal(HexLinkRights.None, peers[0].Granted);
        Assert.Equal(HexLinkRights.View, peers[1].Granted);

        Setup(host.Work);
        byte[] expected = new byte[HexLinkFiles.MOST_READ + 373];
        new Random(17).NextBytes(expected);
        File.WriteAllBytes(Path.Combine(host.Work, "large-tracked.bin"), expected);
        File.WriteAllBytes(Path.Combine(host.Work, "large-deleted.bin"), expected);
        Git(host.Work, "add", "large-tracked.bin", "large-deleted.bin");
        Git(host.Work, "commit", "-qm", "Add the first large version");
        expected[expected.Length / 2] ^= 0x42;
        File.WriteAllBytes(Path.Combine(host.Work, "large-tracked.bin"), expected);
        File.WriteAllBytes(Path.Combine(host.Work, "large-untracked.bin"), expected);
        File.WriteAllBytes(Path.Combine(host.Work, "ignored.bin"), expected);
        File.Delete(Path.Combine(host.Work, "large-deleted.bin"));

        string target = Path.Combine(joiner.Root, "empty-destination");
        HexLinkProvisionResult result = await HexLinkProvision.Get(peers[1], target,
            Path.Combine(joiner.Root, "provision.json"), null, timeout.Token);
        Assert.Empty(result.Skipped);
        Assert.Equal(Git(host.Work, "rev-parse", "HEAD"), Git(target, "rev-parse", "HEAD"));
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(target, "large-untracked.bin")));
        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(target, "large-tracked.bin")));
        Assert.Contains("large-tracked.bin", Git(target, "status", "--porcelain", "--", "large-tracked.bin"));
        Assert.False(File.Exists(Path.Combine(target, "ignored.bin")));
        Assert.False(File.Exists(Path.Combine(target, "large-deleted.bin")));
    }

    [Fact]
    public async Task A_peer_with_binary_framing_but_without_provision_bulk_uses_json_bundles_and_files()
    {
        if (HexLinkGit.Why() is not null)
            return;
        using var host = new LinkServices(DeviceA);
        using var joiner = new LinkServices(DeviceB);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var (a, b) = Pair();
        using var hostSession = new HexLinkSession(Connection(a, DeviceB, HexLinkRights.View, dialled: false));
        using var joinerSession = new HexLinkSession(Connection(b, DeviceA, HexLinkRights.None, dialled: true));
        using var receiver = new HexLinkHost(hostSession, host);
        using var peer = new HexLinkPeer(joinerSession, DeviceB);
        int probes = 0, bundles = 0, reads = 0;
        hostSession.Requested = (method, args, cancel) =>
        {
            switch (method)
            {
                case "provision/bulk":
                    Interlocked.Increment(ref probes);
                    throw new HexLinkException(HexLinkErrors.METHOD_NOT_FOUND, "Old host.");
                case "git/bundle" when HexLinkData.String(args, "repository", 200) is not null:
                    Interlocked.Increment(ref bundles);
                    break;
                case "fs/read":
                    Interlocked.Increment(ref reads);
                    break;
            }
            return receiver.Handle(method, args, cancel);
        };
        hostSession.Told = (method, args) => receiver.Told(method, args);
        joinerSession.Told = peer.Told;
        hostSession.Start();
        joinerSession.Start();
        await peer.Hello(timeout.Token);
        Setup(host.Work);
        File.WriteAllText(Path.Combine(host.Work, "new.txt"), "ordinary working file\n");

        string target = Path.Combine(joiner.Root, "legacy-destination");
        HexLinkProvisionResult result = await HexLinkProvision.Get(peer, target,
            Path.Combine(joiner.Root, "provision.json"), null, timeout.Token);
        Assert.Empty(result.Skipped);
        Assert.True(probes > 0 && bundles > 0 && reads > 0);
        Assert.Equal("ordinary working file\n", File.ReadAllText(Path.Combine(target, "new.txt")));
    }

    private static HexLinkConnection Connection(Stream stream, HexLinkDevice device, HexLinkRights rights, bool dialled) =>
        new(stream, device, "memory", dialled, () => rights, bulk: HexLinkBulkSupport.Raw);

    private static void Setup(string root)
    {
        Git(root, "init", "-qb", "development");
        Git(root, "config", "user.name", "Provision Test");
        Git(root, "config", "user.email", "provision-test@example.test");
        File.WriteAllText(Path.Combine(root, ".gitignore"), "ignored.bin\n");
        File.WriteAllText(Path.Combine(root, "readme.txt"), "committed content\n");
        Git(root, "add", ".");
        Git(root, "commit", "-qm", "Initial");
    }

    private static string Git(string root, params string[] args)
    {
        GitResult result = GitProcess.Run(root, args, patience: TimeSpan.FromMinutes(2), unattended: true);
        Assert.True(result.Ok, result.Why);
        return result.Output.Trim();
    }
}
