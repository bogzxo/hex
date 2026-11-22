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
        Assert.False(settings.LinkAllowPublic);
        Assert.False(settings.LinkMapPublicPort);
        Assert.False(HexSettings.IsWorkspaceKey("link_allow_public"));
        Assert.False(HexSettings.IsWorkspaceKey("link_map_public_port"));
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
        var settings = new HexSettings(file) { Link = true, LinkPort = 48000, LinkName = "Studio Mac", LinkDiscoverable = false, LinkAllowPublic = true, LinkMapPublicPort = true };
        Assert.Null(settings.Save());

        var read = new HexSettings(file);
        Assert.True(read.Link);
        Assert.Equal(48000, read.LinkPort);
        Assert.Equal("Studio Mac", read.LinkName);
        Assert.False(read.LinkDiscoverable);
        Assert.True(read.LinkAllowPublic);
        Assert.True(read.LinkMapPublicPort);

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
    public void Selected_route_is_exclusive_and_survives_updates_and_restart()
    {
        var trust = new HexLinkTrust(folder);
        var lan = new HexLinkEndpoint("192.168.0.30", 47900);
        var vpn = new HexLinkEndpoint("100.80.122.37", 47900);
        HexLinkDevice peer = HexLinkTestKit.DeviceB;
        HexLinkKnown known = trust.Add(peer, HexLinkRights.View, vpn.Host, vpn.Port);

        // Settings written before routing existed default to Automatic. Prefer LAN even when VPN was last used.
        Assert.Null(new HexLinkTrust(folder).Find(peer.Id)!.PreferredRoute);
        Assert.Equal([lan, vpn], DawnScene.LinkDialRoutes(known, [], [lan, vpn]));
        Assert.True(trust.SetRoute(peer.Id, vpn));
        Assert.True(trust.Seen(peer.Id, peer, lan.Host, lan.Port));
        Assert.True(trust.SetRights(peer.Id, HexLinkRights.View | HexLinkRights.Edit));

        trust = new HexLinkTrust(folder);
        known = trust.Find(peer.Id)!;
        Assert.Equal(vpn, known.PreferredRoute);
        Assert.Equal(HexLinkRights.View | HexLinkRights.Edit, known.Rights);
        Assert.Equal([vpn], DawnScene.LinkDialRoutes(known, [], [lan]));
        string listed = DawnScene.LinkDevicesSaid([], [known]);
        Assert.Contains("route preference: 100.80.122.37:47900", listed);
        Assert.Contains("LAN addresses: 192.168.0.30:47900", listed);
        Assert.Contains("VPN addresses: 100.80.122.37:47900", listed);

        Assert.True(trust.SetRoute(peer.Id, null));
        Assert.Null(new HexLinkTrust(folder).Find(peer.Id)!.PreferredRoute);
        Assert.Equal([lan, vpn], DawnScene.LinkDialRoutes(trust.Find(peer.Id)!, [], [vpn]));
    }

    [Fact]
    public void Advertised_addresses_survive_restart_and_make_an_inbound_device_dialable()
    {
        var known = new HexLinkKnown(HexLinkTestKit.DeviceB, HexLinkRights.View, DateTime.UtcNow, DateTime.UtcNow, "192.168.1.30", 0);
        var redial = new HexLinkRedial();
        redial.Open(folder, [known.Device.Id]);
        JsonObject advertisement = HexLinkRedial.Advertisement(47900, ["192.168.1.50", "100.100.0.7", "8.8.8.8", "127.0.0.1"]);
        HexLinkEndpoint[] endpoints = HexLinkRedial.AdvertisedBy(JsonNode.Parse(advertisement.ToJsonString())!.AsObject(), known.Address);
        redial.Remember(known.Device.Id, endpoints);
        redial.Clear();
        redial.Open(folder, [known.Device.Id]);
        Assert.Equal([("192.168.1.30", 47900), ("192.168.1.50", 47900), ("100.100.0.7", 47900)],
            DawnScene.LinkAddressesOf(known, [], redial.Addresses(known.Device.Id)));
        redial.Remember(known.Device.Id, [], HexLinkRedial.Endpoint(known.Address, 47911));
        redial.Clear();
        redial.Open(folder, [known.Device.Id]);
        Assert.Equal([("192.168.1.30", 47911), ("192.168.1.30", 47900), ("192.168.1.50", 47900), ("100.100.0.7", 47900)],
            DawnScene.LinkAddressesOf(known, [], redial.Addresses(known.Device.Id)));
        DateTime now = DateTime.UtcNow;
        redial.Failed(known.Device.Id, now, "unknown");
        Assert.Equal(new HexLinkRedialState(HexLinkRedialStatus.Retrying, now + HexLinkRedial.FIRST, "unknown"),
            redial.State(known.Device.Id, dialling: false));
        Assert.Equal(new HexLinkRedialState(HexLinkRedialStatus.Connecting, null, "unknown"),
            redial.State(known.Device.Id, dialling: true));
    }

    [Fact]
    public void Link_visibility_filters_every_harness_query_without_reusing_local_answers()
    {
        string hidden = Path.Combine(folder, "concealed");
        Directory.CreateDirectory(hidden);
        string hiddenSource = Path.Combine(hidden, "PrivateMarker.cs");
        File.WriteAllText(hiddenSource, "class PrivateMarker {}");
        File.WriteAllText(Path.Combine(hidden, "PrivateMarker.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(Path.Combine(folder, "PublicMarker.cs"), "class PublicMarker {}");
        var index = new HexCodeIndex(() => [folder]) { StaleAfter = 0 };
        long generation = index.Current().Generation;
        File.AppendAllText(hiddenSource, " // changed");
        const int budget = 8000;
        Func<string>[] queries =
        [
            () => index.Search("Marker", false, true, false, null, 0, 20, budget).Text,
            () => index.References("PrivateMarker", null, 20, budget).Text,
            () => index.Files("Marker", 20, budget).Text,
            () => index.Symbols("Marker", null, null, 20, budget).Text,
            () => DawnScene.LinkSymbolLocations(index.VisibleSnapshot(), [new HexLinkFolder("work", folder)],
                new JsonObject { ["query"] = "Marker" }).Text,
            () => index.Read(null, "PrivateMarker", null, null, true, budget).Text,
            () => index.Read(Path.Combine(hidden, "PrivateMarker.cs"), null, 1, 1, true, budget).Text,
            () => index.Skim("**/*.cs", 2, budget).Text,
            () => index.Map(null, 2, budget).Text,
            () => index.Changes(generation, budget).Text,
            () => index.Projects(null, false, budget).Text
        ];
        foreach (Func<string> query in queries)
            Assert.Contains("PrivateMarker", query(), StringComparison.Ordinal);

        try
        {
            HexLinkAllowed.RemoteFileAllowed.Value = path => !path.StartsWith(hidden, StringComparison.OrdinalIgnoreCase);
            foreach (Func<string> query in queries)
            {
                string said = query();
                Assert.DoesNotContain("class PrivateMarker", said, StringComparison.Ordinal);
                if (!said.StartsWith("Nothing called", StringComparison.Ordinal) && !said.StartsWith("No file", StringComparison.Ordinal)
                    && !said.StartsWith("PrivateMarker isn't used", StringComparison.Ordinal))
                    Assert.DoesNotContain("PrivateMarker", said, StringComparison.Ordinal);
            }
        }
        finally
        {
            HexLinkAllowed.RemoteFileAllowed.Value = null;
        }
        Assert.Contains("PrivateMarker", queries[0](), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Link_edit_and_git_diff_reject_excluded_paths_before_calling_editor_tools()
    {
        using var pair = await LinkedPair.Up(HexLinkRights.View | HexLinkRights.Edit);
        Directory.CreateDirectory(Path.Combine(pair.A.Work, ".vs"));
        File.WriteAllText(Path.Combine(pair.A.Work, ".vs", "PrivateMarker.cs"), "class PrivateMarker {}");
        foreach (string tool in (string[])["hex_edit", "hex_write", "hex_edit_symbol", "hex_rename", "hex_git_diff"])
        {
            Assert.Equal(HexLinkErrors.NOT_ALLOWED,
                await HexLinkTestKit.CodeOf(pair.PeerA.Call(tool, new JsonObject { ["path"] = ".vs/PrivateMarker.cs" })));
        }
        Assert.Equal(HexLinkErrors.NOT_ALLOWED,
            await HexLinkTestKit.CodeOf(pair.PeerA.Call("hex_git_diff", new JsonObject { ["path"] = pair.A.Work })));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED,
            await HexLinkTestKit.CodeOf(pair.PeerA.Call("hex_git_diff", new JsonObject { ["path"] = "" })));
        Assert.Empty(pair.A.LinkTools.Called);
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

    [Fact]
    public async Task A_third_device_applies_its_own_rights_and_refuses_a_second_relay()
    {
        using var first = new LinkServices(HexLinkTestKit.DeviceA);
        using var middle = new RelayServices();
        using var last = new LinkServices(HexLinkTestKit.DeviceC);
        using var a = new HexLink(first);
        using var b = new HexLink(middle);
        using var c = new HexLink(last);
        HexLinkRights rightsAtB = HexLinkRights.View, rightsAtC = HexLinkRights.View;
        var (ab, ba) = HexLinkTestKit.Pair();
        var (bc, cb) = HexLinkTestKit.Pair();
        static HexLinkConnection To(Stream end, HexLinkDevice device, Func<HexLinkRights> rights) => new(end, device, "memory", false, rights);
        Task<HexLinkPeer> firstHop = a.Add(To(ab, middle.Device, () => HexLinkRights.View));
        Task<HexLinkPeer> fromB = b.Add(To(ba, first.Device, () => rightsAtB));
        Task<HexLinkPeer> secondHop = b.Add(To(bc, last.Device, () => HexLinkRights.View));
        Task<HexLinkPeer> fromC = c.Add(To(cb, middle.Device, () => rightsAtC));
        await Task.WhenAll(firstHop, fromB, secondHop, fromC);
        middle.Destination = await secondHop;
        HexLinkPeer peer = await firstHop;
        static JsonObject Call(string tool) => new() { ["tool"] = tool };

        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await HexLinkTestKit.CodeOf(peer.Call("hex_link_call", Call("hex_agent_send"))));
        Assert.Empty(last.Audited);
        rightsAtB |= HexLinkRights.Steer;
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await HexLinkTestKit.CodeOf(peer.Call("hex_link_call", Call("hex_agent_send"))));
        Assert.Empty(last.LinkTools.Called);
        Assert.Contains(middle.Audited, audit => audit.What == "relay" && audit.Who == first.Device && audit.Detail.Contains(first.Device.Name, StringComparison.Ordinal));
        Assert.Contains(last.Audited, audit => audit.What == "relay" && audit.Who == middle.Device && audit.Detail.Contains(first.Device.Id, StringComparison.Ordinal));

        rightsAtC |= HexLinkRights.Steer;
        Assert.False((await peer.Call("hex_link_call", Call("hex_agent_send"))).IsError);
        Assert.Equal("hex_agent_send", Assert.Single(last.LinkTools.Called).Name);
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await HexLinkTestKit.CodeOf(peer.Call("hex_link_call", Call("hex_link_devices"))));
        Assert.Single(last.LinkTools.Called);
    }

    private sealed class RelayServices : IHexLinkServices, IMcpTools, IDisposable
    {
        private readonly LinkServices inner = new(HexLinkTestKit.DeviceB);
        public HexLinkPeer Destination { get; set; } = null!;
        public HexLinkDevice Device => inner.Device;
        public HexLinkWorkspace Workspace => inner.Workspace;
        public HexHubSnapshot Fleet => inner.Fleet;
        public IReadOnlyDictionary<int, HexLinkChatExtra> FleetExtras => inner.FleetExtras;
        IMcpTools IHexLinkServices.Tools => this;
        public IReadOnlyList<McpTool> Tools { get; } = [new("hex_link_call", "Relay", "Relay a tool.",
            McpSchema.Object([("tool", McpSchema.String("The destination's tool."))], "tool"))];
        public System.Collections.Concurrent.ConcurrentQueue<(string What, HexLinkDevice Who, string Detail)> Audited => inner.Audited;
        public Task<JsonObject?> Chat(int id, Func<HexAgentChat, JsonObject?> read) => inner.Chat(id, read);
        public Task<bool> Answer(int chat, string answer) => inner.Answer(chat, answer);
        public void Presence(HexLinkDevice device, HexLinkPresence? presence) => inner.Presence(device, presence);
        public Task<string> PatchOffered(HexLinkDevice device, string title, string diff) => inner.PatchOffered(device, title, diff);
        public void Audit(string what, HexLinkDevice who, string detail) => inner.Audit(what, who, detail);
        public Task<McpToolResult> Call(string name, JsonObject args, CancellationToken cancel) =>
            Destination.Call(args["tool"]!.GetValue<string>(), [], cancel);
        public void Dispose() => inner.Dispose();
    }
}
