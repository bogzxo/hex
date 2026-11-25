using System.Net;
using System.Net.Sockets;

using Hex.Core;

using Xunit;

namespace Hex.Tests;

public class HexLinkPortsTests
{
    private static readonly HexLinkRights Ports = HexLinkRights.View | HexLinkRights.Ports;

    // A TCP echo server on a free loopback port, the thing a pair shares
    private sealed class Echo : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();

        public Echo()
        {
            listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        TcpClient client = await listener.AcceptTcpClientAsync(stop.Token);
                        _ = Task.Run(async () =>
                        {
                            using (client)
                            {
                                try
                                {
                                    NetworkStream stream = client.GetStream();
                                    byte[] buffer = new byte[16 * 1024];
                                    int read;
                                    while ((read = await stream.ReadAsync(buffer, stop.Token)) > 0)
                                        await stream.WriteAsync(buffer.AsMemory(0, read), stop.Token);
                                }
                                catch (Exception)
                                {
                                }
                            }
                        });
                    }
                }
                catch (Exception)
                {
                }
            });
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
        }
    }

    private static async Task<TcpClient> Connect(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(10));
        return client;
    }

    private static async Task<byte[]> ReadExactly(TcpClient client, int count)
    {
        byte[] bytes = new byte[count];
        await client.GetStream().ReadExactlyAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(20));
        return bytes;
    }

    private static byte[] Pattern(int count, int seed)
    {
        byte[] bytes = new byte[count];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    // B's view of A is PeerA; B opens what A shares
    private static async Task<(LinkedPair Pair, Echo Echo, HexLinkShare Share, HexLinkOpenedPort Opened)> Opened(HexLinkRights bOnA = default)
    {
        LinkedPair pair = await LinkedPair.Up(bOnA == default ? Ports : bOnA);
        var echo = new Echo();
        HexLinkShare share = pair.A.Ports!.Share("echo", echo.Port);
        HexLinkOpenedPort opened = await pair.B.Ports!.OpenAsync(pair.PeerA, "echo");
        return (pair, echo, share, opened);
    }

    [Fact]
    public async Task A_connection_round_trips_through_the_tunnel()
    {
        var (pair, echo, _, opened) = await Opened();
        using (pair)
        using (echo)
        {
            using TcpClient client = await Connect(opened.LocalPort);
            byte[] sent = Pattern(1_000_000, 1);
            Task write = client.GetStream().WriteAsync(sent).AsTask();
            byte[] back = await ReadExactly(client, sent.Length);
            await write;
            Assert.Equal(sent, back);
            Assert.True(HexLinkTestKit.Eventually(() => pair.A.Audited.Any(entry => entry.What == "ports connect")));
            Assert.Contains(pair.B.Audited, entry => entry.What == "ports use");
            Assert.Contains(pair.B.Audited, entry => entry.What == "ports open");
        }
    }

    [Fact]
    public async Task Many_connections_run_at_once()
    {
        var (pair, echo, _, opened) = await Opened();
        using (pair)
        using (echo)
        {
            Task[] all = [.. Enumerable.Range(0, 12).Select(async index =>
            {
                using TcpClient client = await Connect(opened.LocalPort);
                byte[] sent = Pattern(300_000 + index, index);
                Task write = client.GetStream().WriteAsync(sent).AsTask();
                Assert.Equal(sent, await ReadExactly(client, sent.Length));
                await write;
            })];
            await Task.WhenAll(all);
        }
    }

    [Fact]
    public async Task Half_close_still_lets_the_reply_through()
    {
        var (pair, echo, _, opened) = await Opened();
        using (pair)
        using (echo)
        {
            using TcpClient client = await Connect(opened.LocalPort);
            byte[] sent = Pattern(5000, 3);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(sent);
            client.Client.Shutdown(SocketShutdown.Send);
            byte[] all = new byte[sent.Length + 1];
            int total = 0, read;
            while ((read = await stream.ReadAsync(all.AsMemory(total)).AsTask().WaitAsync(TimeSpan.FromSeconds(20))) > 0)
                total += read;
            Assert.Equal(sent.Length, total);
            Assert.Equal(sent, all[..total]);
        }
    }

    [Fact]
    public async Task Opens_the_same_port_when_free_and_the_next_when_not()
    {
        LinkedPair pair = await LinkedPair.Up(Ports);
        using (pair)
        {
            // Take a free port, hold it so the share's own port isn't available to the listener
            var held = new TcpListener(IPAddress.Loopback, 0);
            held.Start();
            int taken = ((IPEndPoint)held.LocalEndpoint).Port;
            try
            {
                pair.A.Ports!.Share("busy", taken);
                HexLinkOpenedPort opened = await pair.B.Ports!.OpenAsync(pair.PeerA, "busy");
                Assert.NotEqual(taken, opened.LocalPort);
                Assert.InRange(opened.LocalPort, taken + 1, taken + 101);
            }
            finally
            {
                held.Stop();
            }
        }
    }

    [Fact]
    public async Task Unsharing_closes_open_connections_and_the_listener()
    {
        var (pair, echo, share, opened) = await Opened();
        using (pair)
        using (echo)
        {
            using TcpClient client = await Connect(opened.LocalPort);
            await client.GetStream().WriteAsync(new byte[] { 1, 2, 3 });
            Assert.Equal([1, 2, 3], await ReadExactly(client, 3));
            Assert.True(pair.A.Ports!.Unshare(share.Id));

            // The connection ends and the opener forgets the port it no longer has
            Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.True(HexLinkTestKit.Eventually(() => pair.B.Ports!.Open.Count == 0));
            Assert.True(HexLinkTestKit.Eventually(() => pair.LinkA.PeerOf(HexLinkTestKit.DeviceB.Id)?.Tunnels?.Serving == 0));
            await Assert.ThrowsAnyAsync<Exception>(() => Connect(opened.LocalPort));
        }
    }

    [Fact]
    public async Task A_disconnect_closes_tunnels_and_listeners_and_the_ports_come_back_with_the_link()
    {
        var (pair, echo, _, opened) = await Opened();
        using (pair)
        using (echo)
        {
            using TcpClient client = await Connect(opened.LocalPort);
            await client.GetStream().WriteAsync(new byte[] { 9 });
            Assert.Equal([9], await ReadExactly(client, 1));
            pair.PeerA.Session.Close("test");

            Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.True(HexLinkTestKit.Eventually(() => pair.B.Ports!.Open.Count == 0));
            await Assert.ThrowsAnyAsync<Exception>(() => Connect(opened.LocalPort));

            // Linked again: the open is wanted still and comes back on its own port
            var (a, b) = HexLinkTestKit.Pair();
            Task<HexLinkPeer> asA = pair.LinkA.Add(new HexLinkConnection(a, HexLinkTestKit.DeviceB, "memory", true, () => pair.BOnA));
            Task<HexLinkPeer> asB = pair.LinkB.Add(new HexLinkConnection(b, HexLinkTestKit.DeviceA, "memory", false, () => pair.AOnB));
            await asA.WaitAsync(TimeSpan.FromSeconds(15));
            await asB.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(HexLinkTestKit.Eventually(() => pair.B.Ports!.Open.Count == 1));
            HexLinkOpenedPort again = pair.B.Ports!.Open[0];
            Assert.Equal(opened.LocalPort, again.LocalPort);
            using TcpClient second = await Connect(again.LocalPort);
            await second.GetStream().WriteAsync(new byte[] { 5, 6 });
            Assert.Equal([5, 6], await ReadExactly(second, 2));
        }
    }

    [Fact]
    public async Task Nothing_is_reachable_without_the_right()
    {
        using LinkedPair pair = await LinkedPair.Up(HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit);
        using var echo = new Echo();
        pair.A.Ports!.Share("echo", echo.Port);
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await HexLinkTestKit.CodeOf(pair.PeerA.ListPorts()));
        Assert.Equal(HexLinkErrors.NOT_ALLOWED, await HexLinkTestKit.CodeOf(pair.PeerA.Session.Ask("ports/connect", new System.Text.Json.Nodes.JsonObject
        {
            ["share"] = pair.A.Ports.Shares[0].Id
        })));
        await Assert.ThrowsAsync<HexLinkException>(() => pair.B.Ports!.OpenAsync(pair.PeerA, "echo"));
        Assert.Contains(pair.A.Audited, entry => entry.What == "ports refused");
        Assert.Equal(0, pair.LinkA.PeerOf(HexLinkTestKit.DeviceB.Id)!.Tunnels!.Serving);
    }

    [Fact]
    public async Task Nothing_is_reachable_without_the_share()
    {
        using LinkedPair pair = await LinkedPair.Up(Ports);
        using var echo = new Echo();
        // Shared with another device only, and one that isn't shared at all
        HexLinkShare other = pair.A.Ports!.Share("other", echo.Port, devices: [HexLinkTestKit.DeviceC.Id]);
        Assert.Empty(await pair.PeerA.ListPorts());
        Assert.Equal(HexLinkErrors.NOT_FOUND, await HexLinkTestKit.CodeOf(pair.PeerA.Session.Ask("ports/connect", new System.Text.Json.Nodes.JsonObject { ["share"] = other.Id })));
        Assert.Equal(HexLinkErrors.NOT_FOUND, await HexLinkTestKit.CodeOf(pair.PeerA.Session.Ask("ports/connect", new System.Text.Json.Nodes.JsonObject { ["share"] = "nope" })));
        Assert.Contains(pair.A.Audited, entry => entry.What == "ports refused");
    }

    [Fact]
    public async Task Taking_the_right_away_closes_the_tunnels()
    {
        var (pair, echo, _, opened) = await Opened();
        using (pair)
        using (echo)
        {
            using TcpClient client = await Connect(opened.LocalPort);
            await client.GetStream().WriteAsync(new byte[] { 1 });
            await ReadExactly(client, 1);
            pair.BOnA = HexLinkRights.View;
            pair.LinkA.RightsMoved(HexLinkTestKit.DeviceB.Id);
            Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(20)));
            Assert.Equal(HexLinkErrors.NOT_ALLOWED, await HexLinkTestKit.CodeOf(pair.PeerA.ListPorts()));
        }
    }

    [Fact]
    public void Shares_are_limited_and_only_loopback_unless_allowed()
    {
        using var ports = new HexLinkPorts();
        for (int index = 0; index < HexLinkPorts.MOST_SHARES; index++)
            ports.Share($"share {index}", 3000 + index);
        Assert.Throws<ArgumentException>(() => ports.Share("one more", 4000));

        using var lan = new HexLinkPorts();
        Assert.Throws<ArgumentException>(() => lan.Share("router", 80, "192.168.1.1"));
        Assert.Throws<ArgumentException>(() => lan.Share("bad", 0));
        HexLinkShare allowed = lan.Share("router", 80, "192.168.1.1", allowLan: true);
        Assert.False(allowed.IsLoopback);
        Assert.True(lan.Share("local", 80, "localhost").IsLoopback);
        Assert.True(lan.Share("v6", 80, "::1").IsLoopback);
    }

    [Fact]
    public void Shares_survive_a_restart()
    {
        string file = Path.Combine(Path.GetTempPath(), "hexlink-tests", Guid.NewGuid().ToString("N"), "ports.json");
        try
        {
            using (var first = new HexLinkPorts(file))
                first.Share("kept", 5173, devices: [HexLinkTestKit.DeviceB.Id]);
            using var second = new HexLinkPorts(file);
            HexLinkShare share = Assert.Single(second.Shares);
            Assert.Equal(("kept", 5173), (share.Name, share.Port));
            Assert.True(share.SharedWith(HexLinkTestKit.DeviceB.Id));
            Assert.False(share.SharedWith(HexLinkTestKit.DeviceC.Id));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(file)!, true); } catch (Exception) { }
        }
    }

    [Fact]
    public async Task At_most_sixty_four_connections_are_tunnelled_at_once()
    {
        var (pair, echo, _, opened) = await Opened();
        using (pair)
        using (echo)
        {
            var clients = new List<TcpClient>();
            try
            {
                for (int index = 0; index < HexLinkTunnels.MOST_TUNNELS; index++)
                {
                    TcpClient client = await Connect(opened.LocalPort);
                    clients.Add(client);
                    await client.GetStream().WriteAsync(new byte[] { 1 });
                    await ReadExactly(client, 1);
                }

                Assert.Equal(HexLinkTunnels.MOST_TUNNELS, pair.LinkA.PeerOf(HexLinkTestKit.DeviceB.Id)!.Tunnels!.Serving);
                using TcpClient over = await Connect(opened.LocalPort);
                Assert.Equal(0, await over.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(20)));
                Assert.Equal(HexLinkTunnels.MOST_TUNNELS, pair.LinkA.PeerOf(HexLinkTestKit.DeviceB.Id)!.Tunnels!.Serving);
            }
            finally
            {
                foreach (TcpClient client in clients)
                    client.Dispose();
            }
        }
    }
}
