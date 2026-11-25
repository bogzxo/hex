using System.Net;
using System.Net.Sockets;
using Hex.Core;
using Xunit;

namespace Hex.Tests;

public sealed class HexLinkDialOrderTests
{
    private static IPAddress Ip(string text) => IPAddress.Parse(text);

    [Fact]
    public void The_address_on_a_network_of_this_machine_comes_before_a_virtual_switchs()
    {
        var local = new List<(IPAddress, int)> { (Ip("192.168.0.78"), 24) };
        var ordered = HexLinkAddresses.Preferred(
            [Ip("192.168.176.1"), Ip("172.25.64.1"), Ip("192.168.0.50"), Ip("100.81.32.118")], local);

        Assert.Equal(Ip("192.168.0.50"), ordered[0]);
        Assert.Equal([Ip("192.168.0.50"), Ip("192.168.176.1"), Ip("172.25.64.1"), Ip("100.81.32.118")], ordered);
    }

    [Fact]
    public void With_no_local_networks_the_rank_order_stands()
    {
        var ordered = HexLinkAddresses.Preferred([Ip("172.25.64.1"), Ip("192.168.176.1"), Ip("10.0.0.2")], []);
        Assert.Equal([Ip("192.168.176.1"), Ip("10.0.0.2"), Ip("172.25.64.1")], ordered);
    }

    [Fact]
    public void The_prefix_length_decides_what_is_the_same_network()
    {
        var local = new List<(IPAddress, int)> { (Ip("10.1.2.3"), 16) };
        var ordered = HexLinkAddresses.Preferred([Ip("192.168.1.1"), Ip("10.2.0.5"), Ip("10.1.200.9")], local);
        Assert.Equal(Ip("10.1.200.9"), ordered[0]);
    }

    [Fact]
    public async Task A_dead_address_ahead_of_a_live_one_costs_only_its_own_share()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        using Socket socket = await HexLinkTransport.ConnectFirst(
            [Ip("10.255.255.1"), Ip("127.0.0.1")], port, TimeSpan.FromMilliseconds(300), CancellationToken.None);

        Assert.True(socket.Connected);
        Assert.Equal(Ip("127.0.0.1"), ((IPEndPoint)socket.RemoteEndPoint!).Address.MapToIPv4());
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10));
    }
}
