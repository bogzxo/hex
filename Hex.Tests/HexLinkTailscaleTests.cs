using System.Net;
using System.Net.Sockets;

using Hex.Core;

namespace Hex.Tests;

public sealed class HexLinkTailscaleTests
{
    // What `tailscale status --json` prints, cut down to what matters: this machine, a Mac up with both address families
    // (the IPv6 one first, as Tailscale sometimes lists them), a Linux box that's down, and a peer with no address yet
    private const string STATUS = """
        {
          "Version": "1.78.1",
          "BackendState": "Running",
          "Self": {
            "ID": "n1", "HostName": "this-pc", "DNSName": "this-pc.tail1234.ts.net.", "OS": "windows",
            "TailscaleIPs": ["100.64.0.1", "fd7a:115c:a1e0::1"], "Online": true
          },
          "Peer": {
            "nodekey:aaaa": {
              "ID": "n2", "HostName": "Bogdans-MacBook-Pro", "DNSName": "bogdans-macbook-pro.tail1234.ts.net.", "OS": "macOS",
              "TailscaleIPs": ["fd7a:115c:a1e0::2", "100.64.0.2"], "Online": true
            },
            "nodekey:bbbb": {
              "ID": "n3", "HostName": "linux-box", "DNSName": "linux-box.tail1234.ts.net.", "OS": "linux",
              "TailscaleIPs": ["100.64.0.3"], "Online": false
            },
            "nodekey:cccc": {
              "ID": "n4", "HostName": "fresh", "DNSName": "fresh.tail1234.ts.net.", "OS": "linux",
              "TailscaleIPs": [], "Online": true
            }
          }
        }
        """;

    [Fact]
    public void Only_the_peers_that_are_up_and_have_an_address_are_listed_and_this_machine_is_left_out()
    {
        HexLinkTailnetPeer peer = Assert.Single(HexLinkTailscale.ParseStatus(STATUS));
        Assert.Equal("Bogdans-MacBook-Pro", peer.Name);
        Assert.Equal("macOS", peer.Os);
    }

    [Fact]
    public void The_IPv4_address_comes_first_and_the_MagicDNS_name_loses_its_trailing_dot()
    {
        HexLinkTailnetPeer peer = Assert.Single(HexLinkTailscale.ParseStatus(STATUS));
        Assert.Equal(["100.64.0.2", "fd7a:115c:a1e0::2"], peer.Addresses.Select(address => address.ToString()));
        Assert.Equal("bogdans-macbook-pro.tail1234.ts.net", peer.DnsName);
    }

    [Theory]
    [InlineData("Stopped")]
    [InlineData("NeedsLogin")]
    [InlineData("Starting")]
    public void A_tailnet_that_isnt_running_has_no_peers(string state)
        => Assert.Empty(HexLinkTailscale.ParseStatus(STATUS.Replace("\"Running\"", $"\"{state}\"")));

    [Theory]
    [InlineData("""{"BackendState": "Running"}""")]
    [InlineData("""{"BackendState": "Running", "Peer": null}""")]
    [InlineData("""{"BackendState": "Running", "Peer": [1, 2]}""")]
    [InlineData("""{"BackendState": "Running", "Peer": {"k": {"Online": true, "TailscaleIPs": "100.64.0.9"}}}""")]
    [InlineData("""{"BackendState": "Running", "Peer": {"k": 7, "j": {"Online": true, "TailscaleIPs": ["not an address"], "HostName": "x"}}}""")]
    [InlineData("""{"BackendState": 3}""")]
    [InlineData("[]")]
    [InlineData("not json {")]
    [InlineData("")]
    [InlineData(null)]
    public void Missing_fields_and_garbage_make_nothing_and_never_throw(string? json)
        => Assert.Empty(HexLinkTailscale.ParseStatus(json));

    [Fact]
    public void A_peer_without_a_host_name_goes_by_the_first_label_of_its_MagicDNS_name()
    {
        const string json = """{"BackendState": "Running", "Peer": {"k": {"Online": true, "DNSName": "desk.tail1.ts.net.", "TailscaleIPs": ["100.64.0.5"]}}}""";
        Assert.Equal("desk", Assert.Single(HexLinkTailscale.ParseStatus(json)).Name);
    }

    [Fact]
    public void The_macOS_app_bundle_is_where_the_command_line_is_looked_for_first()
    {
        const string BUNDLE = "/Applications/Tailscale.app/Contents/MacOS/Tailscale";
        Assert.Equal(BUNDLE, HexLinkTailscale.FindCli("macos", _ => null, path => path is BUNDLE or "/usr/local/bin/tailscale"));
        Assert.Equal("/opt/homebrew/bin/tailscale", HexLinkTailscale.FindCli("macos", _ => null, path => path == "/opt/homebrew/bin/tailscale"));
    }

    [Fact]
    public void Windows_looks_in_both_program_files_and_then_the_path()
    {
        string x64 = Path.Combine(@"C:\Program Files", "Tailscale", "tailscale.exe");
        string x86 = Path.Combine(@"C:\Program Files (x86)", "Tailscale", "tailscale.exe");
        string? Environment(string name) => name switch
        {
            "ProgramFiles" => @"C:\Program Files",
            "ProgramFiles(x86)" => @"C:\Program Files (x86)",
            "PATH" => @"C:\Tools;C:\Other",
            _ => null
        };

        Assert.Equal(x64, HexLinkTailscale.FindCli("windows", Environment, path => path == x64 || path == x86));
        Assert.Equal(x86, HexLinkTailscale.FindCli("windows", Environment, path => path == x86));
        string onPath = Path.Combine(@"C:\Other", "tailscale.exe");
        Assert.Equal(onPath, HexLinkTailscale.FindCli("windows", Environment, path => path == onPath));
    }

    [Fact]
    public void Linux_looks_in_the_usual_places_and_then_the_path()
    {
        Assert.Equal("/usr/bin/tailscale", HexLinkTailscale.FindCli("unix", _ => null, path => path == "/usr/bin/tailscale"));
        string onPath = Path.Combine("/home/me/bin", "tailscale");
        Assert.Equal(onPath, HexLinkTailscale.FindCli("unix", name => name == "PATH" ? "/bin:/home/me/bin" : null, path => path == onPath));
    }

    [Theory]
    [InlineData("windows")]
    [InlineData("macos")]
    [InlineData("unix")]
    public void Nothing_installed_finds_nothing(string os)
        => Assert.Null(HexLinkTailscale.FindCli(os, _ => null, _ => false));

    [Fact]
    public async Task A_peer_that_accepts_a_connection_is_a_candidate_and_one_that_refuses_is_not()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int open = ((IPEndPoint)listener.LocalEndpoint).Port;

        int closed;
        using (var spare = new TcpListener(IPAddress.Loopback, 0))
        {
            spare.Start();
            closed = ((IPEndPoint)spare.LocalEndpoint).Port;
            spare.Stop();
        }

        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        Assert.True(await HexLinkTailscale.Listens(IPAddress.Loopback, open, cancel.Token));
        Assert.False(await HexLinkTailscale.Listens(IPAddress.Loopback, closed, cancel.Token));

        var peers = new[]
        {
            new HexLinkTailnetPeer("here", "here.tail1.ts.net", [IPAddress.Loopback], "linux"),
            new HexLinkTailnetPeer("there", null, [IPAddress.Loopback], null)
        };
        Assert.Equal(["here", "there"], (await HexLinkTailscale.Probe(peers, open, cancel.Token)).Select(peer => peer.Name));
        Assert.Empty(await HexLinkTailscale.Probe(peers, closed, cancel.Token));
    }
}
