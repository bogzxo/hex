using System.Net;
using System.Text.Json.Nodes;

using Hex.Core;
using Hex.Dawn;

namespace Hex.Tests;

/// <summary>
/// Hex Link as the editor wires it, what can be tested without a window: its settings (user settings only, kept and
/// read back), the order a device's addresses are dialled in and the waits between redials, the workspace as a link
/// describes it, addresses as somebody types them, a link listed only once it said hello, and the hex_link tools'
/// devices and the device a tool names.
/// </summary>
public class HexLinkWiringTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "hex-link-wiring-" + Guid.NewGuid().ToString("N"));

    public HexLinkWiringTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try
        {
            // git keeps its objects read-only
            foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void LinkSettingsAreOffAsTheyComeAndKeptByTheUserAlone()
    {
        var settings = new HexSettings(Path.Combine(folder, "settings.hor"));
        Assert.False(settings.Link);
        Assert.Equal(HexLinkTransport.DEFAULT_PORT, settings.LinkPort);
        Assert.Equal(string.Empty, settings.LinkName);
        Assert.True(settings.LinkDiscoverable);

        // Nothing a cloned repository's settings can turn on
        foreach (string key in (string[])["link", "link_port", "link_name", "link_discoverable"])
        {
            Assert.False(HexSettings.IsWorkspaceKey(key));
            Assert.NotNull(HexSettings.RowOf(key));
            Assert.True(Hex.Shell.EditorSettingsCatalog.Usual.ContainsKey(key));
        }

        Assert.Equal((1024.0f, 65535.0f), HexSettings.Ranges["link_port"]);
    }

    [Fact]
    public void LinkSettingsGoRoundTheFile()
    {
        string file = Path.Combine(folder, "settings.hor");
        var settings = new HexSettings(file) { Link = true, LinkPort = 48000, LinkName = "Studio Mac", LinkDiscoverable = false };
        Assert.Null(settings.Save());

        var read = new HexSettings(file);
        Assert.True(read.Link);
        Assert.Equal(48000, read.LinkPort);
        Assert.Equal("Studio Mac", read.LinkName);
        Assert.False(read.LinkDiscoverable);

        // A port past either end is held to them, a name has its spaces taken off
        File.WriteAllText(file, "let settings = {\n    editor: {\n        link_port: 80,\n        link_name: \"  Desk  \"\n    }\n};\n");
        var held = new HexSettings(file);
        Assert.Equal(1024, held.LinkPort);
        Assert.Equal("Desk", held.LinkName);
    }

    [Fact]
    public void LinkSettingsInAFoldersFileAreNotTaken()
    {
        string user = Path.Combine(folder, "settings.hor");
        string work = Path.Combine(folder, "work");
        Directory.CreateDirectory(work);
        File.WriteAllText(user, "let settings = {\n    editor: {\n        link: false\n    }\n};\n");
        Directory.CreateDirectory(Path.GetDirectoryName(HexSettings.WorkspacePathOf(work))!);
        File.WriteAllText(HexSettings.WorkspacePathOf(work), "let settings = {\n    editor: {\n        link: true,\n        link_port: 50000\n    }\n};\n");

        var settings = new HexSettings(user);
        settings.LoadWorkspace(work);
        Assert.False(settings.Link);
        Assert.Equal(HexLinkTransport.DEFAULT_PORT, settings.LinkPort);
    }

    [Fact]
    public void AddressesAreDialledLanFirstAndOnlyPrivateOnes()
    {
        IPAddress[] said =
        [
            IPAddress.Parse("100.101.1.2"), IPAddress.Parse("8.8.8.8"), IPAddress.Parse("172.20.0.5"), IPAddress.Parse("fe80::1"),
            IPAddress.Parse("10.0.0.9"), IPAddress.Parse("192.168.1.20"), IPAddress.Parse("169.254.3.3"), IPAddress.Parse("192.168.1.20"),
            IPAddress.Parse("::ffff:192.168.7.7")
        ];
        Assert.Equal(["192.168.1.20", "192.168.7.7", "10.0.0.9", "172.20.0.5", "169.254.3.3", "100.101.1.2", "fe80::1"],
            HexLinkAddresses.Preferred(said).Select(address => address.ToString()));
    }

    [Fact]
    public void RedialsWaitLongerEachTimeUpToAMinuteAndStartOverWhenHeard()
    {
        var redial = new HexLinkRedial();
        DateTime now = new(2026, 11, 21, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(redial.Due("A", now));

        TimeSpan[] waits = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60)];
        foreach (TimeSpan wait in waits)
        {
            redial.Failed("A", now);
            Assert.Equal(now + wait, redial.Next("A"));
            Assert.False(redial.Due("A", now + wait - TimeSpan.FromMilliseconds(1)));
            Assert.True(redial.Due("A", now + wait));
            now += wait;
        }

        // Heard on the LAN, or linked, at once and from the start again
        redial.Soon("A");
        Assert.True(redial.Due("A", now));
        redial.Failed("A", now);
        Assert.Equal(now + HexLinkRedial.FIRST, redial.Next("A"));
        Assert.True(redial.Due("B", now));
    }

    [Fact]
    public void AKnownDeviceIsDialledWhereTheLanSaysAndWhereItLastWas()
    {
        var known = new HexLinkKnown(new HexLinkDevice("ABCDEF12" + new string('0', 56), "Mac"), HexLinkRights.View, DateTime.UtcNow, DateTime.UtcNow, "192.168.1.30", 47833);
        HexLinkNearby[] nearby =
        [
            new("Mac", "ABCDEF12", [IPAddress.Parse("100.64.0.3"), IPAddress.Parse("192.168.1.31")], 47900, DateTime.UtcNow),
            new("Other", "99999999", [IPAddress.Parse("192.168.1.40")], 47833, DateTime.UtcNow)
        ];
        Assert.Equal([("192.168.1.31", 47900), ("100.64.0.3", 47900), ("192.168.1.30", 47833)], DawnScene.LinkAddressesOf(known, nearby));

        // Only ever came in, its port not known, and not heard: nowhere to dial, it's the one that dials
        Assert.Empty(DawnScene.LinkAddressesOf(known with { Port = 0 }, []));
    }

    [Fact]
    public void AddressesAreReadAsTyped()
    {
        Assert.True(DawnScene.TryLinkAddress(" 192.168.1.20 ", 47833, out string host, out int port));
        Assert.Equal(("192.168.1.20", 47833), (host, port));
        Assert.True(DawnScene.TryLinkAddress("mac.local:47900", 47833, out host, out port));
        Assert.Equal(("mac.local", 47900), (host, port));
        Assert.True(DawnScene.TryLinkAddress("[fe80::1]:5000", 47833, out host, out port));
        Assert.Equal(("fe80::1", 5000), (host, port));
        Assert.True(DawnScene.TryLinkAddress("fe80::1", 47833, out host, out port));
        Assert.Equal(("fe80::1", 47833), (host, port));
        Assert.False(DawnScene.TryLinkAddress("", 47833, out _, out _));
        Assert.False(DawnScene.TryLinkAddress("mac:port", 47833, out _, out _));
        Assert.False(DawnScene.TryLinkAddress("mac:70000", 47833, out _, out _));
        Assert.False(DawnScene.TryLinkAddress("two words", 47833, out _, out _));
    }

    [Fact]
    public void TheWorkspaceIsDescribedWithANameAFolderAndEachRepositoryOnce()
    {
        string one = Path.Combine(folder, "a", "hex"), two = Path.Combine(folder, "b", "hex"), three = Path.Combine(folder, "b", "hex", "docs");
        foreach (string each in (string[])[one, two, three])
            Directory.CreateDirectory(each);

        HexLinkWorkspace described = HexLinkWorkspaces.Describe(string.Empty, [one, two, three, one + Path.DirectorySeparatorChar],
            path => path.StartsWith(two, StringComparison.OrdinalIgnoreCase) ? new HexLinkRepository("hex", "main", "abc123", two) : null);

        Assert.Equal("hex", described.Name);
        Assert.Equal(["hex", "hex-2", "docs"], described.Folders.Select(each => each.Name));
        Assert.Equal(Path.GetFullPath(two), described.Folders[1].Path);
        HexLinkRepository repository = Assert.Single(described.Repositories);
        Assert.Equal(("hex", "main", "abc123"), (repository.Name, repository.Branch, repository.Head));
        Assert.Equal("Work", HexLinkWorkspaces.Describe("Work", [one], _ => null).Name);
    }

    [Fact]
    public void TheRepositoryOfAFolderSaysItsBranchAndHead()
    {
        if (Hex.Core.Git.GitProcess.Installed is false)
            return;
        string repo = Path.Combine(folder, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        Hex.Core.Git.GitProcess.Run(repo, ["init", "-q", "-b", "trunk"]);
        // No commit yet, still the repository, its head nothing
        Assert.Equal(("repo", string.Empty), HexLinkWorkspaces.RepositoryOf(Path.Combine(repo, "src")) is { } empty ? (empty.Name, empty.Head) : default);
        File.WriteAllText(Path.Combine(repo, "a.txt"), "a");
        Hex.Core.Git.GitProcess.Run(repo, ["add", "."]);
        Hex.Core.Git.GitProcess.Run(repo, ["-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "a"]);

        HexLinkRepository? found = HexLinkWorkspaces.RepositoryOf(Path.Combine(repo, "src"));
        Assert.NotNull(found);
        Assert.Equal(("repo", "trunk", 40), (found.Name, found.Branch, found.Head.Length));
        Assert.True(Horizon.Core.Platform.PlatformPaths.Same(Path.GetFullPath(repo), found.Path));
        Assert.Null(HexLinkWorkspaces.RepositoryOf(Path.Combine(folder, "nowhere")));
    }

    [Fact]
    public async Task ALinkIsListedOnlyOnceItHasSaidHello()
    {
        using var services = new LinkServices(HexLinkTestKit.DeviceA);
        using var links = new HexLink(services);
        var (a, b) = HexLinkTestKit.Pair();
        Task<HexLinkPeer> adding = links.Add(new HexLinkConnection(a, HexLinkTestKit.DeviceB, "memory", true, () => HexLinkRights.View));

        // B's end says nothing back: linked, but not listed
        Assert.True(HexLinkTestKit.Eventually(() => links.PeerOf(HexLinkTestKit.DeviceB.Id) is not null));
        Assert.Empty(links.Peers);
        b.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => adding.WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.Empty(links.Peers);

        using var pair = await LinkedPair.Up(HexLinkRights.View, HexLinkRights.View | HexLinkRights.Steer);
        Assert.Same(pair.PeerB, Assert.Single(pair.LinkA.Peers));
    }

    [Fact]
    public async Task TheLinkToolsSayTheDevicesAndFindTheOneNamed()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View, HexLinkRights.View | HexLinkRights.Steer);
        await pair.PeerB.WatchFleet();
        var known = new List<HexLinkKnown>
        {
            new(HexLinkTestKit.DeviceB, HexLinkRights.View, DateTime.UtcNow, DateTime.UtcNow, "192.168.1.5", 47833),
            new(HexLinkTestKit.DeviceC, HexLinkRights.View | HexLinkRights.Edit, DateTime.UtcNow, new DateTime(2026, 11, 20, 9, 30, 0, DateTimeKind.Utc), "192.168.1.6", 47833)
        };

        string said = DawnScene.LinkDevicesSaid(pair.LinkA.Peers, known);
        Assert.Contains("- Laptop (BBBBBBBB, macos arm64)", said, StringComparison.Ordinal);
        Assert.Contains("it lets you: view, steer; you let it: view", said, StringComparison.Ordinal);
        Assert.Contains("folders: work", said, StringComparison.Ordinal);
        Assert.Contains("agents: 0", said, StringComparison.Ordinal);
        Assert.Contains("Paired, not connected:\n- Tablet (CCCCCCCC), last seen 2026-11-20 09:30 UTC at 192.168.1.6", said, StringComparison.Ordinal);
        Assert.Contains("Connected:\nnone", DawnScene.LinkDevicesSaid([], known), StringComparison.Ordinal);

        Assert.Same(pair.PeerB, DawnScene.LinkPeerOf(pair.LinkA.Peers, "laptop", out _));
        Assert.Same(pair.PeerB, DawnScene.LinkPeerOf(pair.LinkA.Peers, "bbbb", out _));
        Assert.Null(DawnScene.LinkPeerOf(pair.LinkA.Peers, "bbb", out string? tooShort));
        Assert.Contains("Connected: Laptop (BBBBBBBB)", tooShort, StringComparison.Ordinal);
        Assert.Null(DawnScene.LinkPeerOf(pair.LinkA.Peers, " ", out string? none));
        Assert.Contains("hex_link_devices", none, StringComparison.Ordinal);
        Assert.Null(DawnScene.LinkPeerOf([], "Laptop", out string? away));
        Assert.Contains("None is.", away, StringComparison.Ordinal);
    }
}
