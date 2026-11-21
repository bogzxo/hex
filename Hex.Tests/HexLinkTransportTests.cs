using System.Net;
using System.Text;

using Hex.Core;

namespace Hex.Tests;

/// <summary>
/// Link's door over loopback: two known devices link and talk, and everything that isn't let in isn't, an unknown key,
/// no certificate, the wrong device at an address, a forgotten one, a second link, an address that isn't private.
/// </summary>
public sealed class HexLinkTransportTests : IDisposable
{
    private readonly HexLinkTestMachine pc = new("PC");
    private readonly HexLinkTestMachine mac = new("Mac");

    public void Dispose()
    {
        pc.Dispose();
        mac.Dispose();
    }

    private async Task<(HexLinkConnection Dialled, HexLinkConnection Accepted)> Link()
    {
        HexLinkTestMachine.Know(pc, HexLinkRights.View, mac, HexLinkRights.View | HexLinkRights.Steer);
        int port = mac.Listen();
        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        HexLinkConnection dialled = await pc.Transport.Dial("127.0.0.1", port, mac.Device.Id, cancel.Token);
        HexLinkConnection accepted = await mac.Accepted.Task.WaitAsync(HexLinkTestMachine.WAIT);
        return (dialled, accepted);
    }

    [Fact]
    public async Task Two_known_devices_link_and_talk_both_ways()
    {
        (HexLinkConnection dialled, HexLinkConnection accepted) = await Link();

        Assert.Equal(mac.Device.Id, dialled.Device.Id);
        Assert.Equal("Mac", dialled.Device.Name);
        Assert.True(dialled.Dialled);
        Assert.Equal(pc.Device.Id, accepted.Device.Id);
        Assert.Equal("PC", accepted.Device.Name);
        Assert.False(accepted.Dialled);

        // Each side's rights are what it granted the other
        Assert.Equal(HexLinkRights.View, dialled.Rights);
        Assert.Equal(HexLinkRights.View | HexLinkRights.Steer, accepted.Rights);

        await HexLinkTestMachine.WriteLine(dialled.Stream, "hello mac");
        Assert.Equal("hello mac", await HexLinkTestMachine.ReadLine(accepted.Stream));
        await HexLinkTestMachine.WriteLine(accepted.Stream, "hello pc");
        Assert.Equal("hello pc", await HexLinkTestMachine.ReadLine(dialled.Stream));

        Assert.Single(pc.Transport.Links);
        Assert.Single(mac.Transport.Links);
        Assert.Equal("127.0.0.1", mac.Trust.Find(pc.Device.Id)!.Address);
    }

    [Fact]
    public async Task An_unknown_device_is_refused_having_been_shown_nothing()
    {
        // The PC knows the Mac, the Mac doesn't know the PC
        pc.Trust.Add(mac.Device, HexLinkRights.View);
        int port = mac.Listen();

        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        HexLinkException refused = await Assert.ThrowsAsync<HexLinkException>(() => pc.Transport.Dial("127.0.0.1", port, mac.Device.Id, cancel.Token));
        Assert.Equal("unknown", refused.Reason);

        // By hand: everything the Mac says to an unknown device is the refusal
        using var ssl = await HexLinkTestMachine.RawClient(port, pc.Identity.Certificate);
        Assert.NotNull(ssl);
        await HexLinkTestMachine.WriteLine(ssl, "HEX-LINK 1 link \"PC\"");
        Assert.Equal("NO unknown\n", await HexLinkTestMachine.ReadToEnd(ssl));
        Assert.False(mac.Accepted.Task.IsCompleted);
        Assert.Empty(mac.Transport.Links);
    }

    [Fact]
    public async Task A_client_without_a_certificate_is_refused()
    {
        int port = mac.Listen();
        using var ssl = await HexLinkTestMachine.RawClient(port, null);
        if (ssl is not null)
        {
            // TLS 1.3, the client finds out on its first read
            try
            {
                await HexLinkTestMachine.WriteLine(ssl, "HEX-LINK 1 link \"nobody\"");
            }
            catch (IOException)
            {
            }

            Assert.Equal("", await HexLinkTestMachine.ReadToEnd(ssl));
        }

        Assert.False(mac.Accepted.Task.IsCompleted);
    }

    [Fact]
    public async Task Dialling_one_device_and_finding_another_is_refused()
    {
        using var other = new HexLinkTestMachine("Other");
        HexLinkTestMachine.Know(pc, HexLinkRights.View, mac, HexLinkRights.View);
        pc.Trust.Add(other.Device, HexLinkRights.View);
        int port = mac.Listen();

        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        HexLinkException refused = await Assert.ThrowsAsync<HexLinkException>(() => pc.Transport.Dial("127.0.0.1", port, other.Device.Id, cancel.Token));
        Assert.Equal("pin", refused.Reason);
        Assert.False(mac.Accepted.Task.IsCompleted);
        Assert.Empty(pc.Transport.Links);
    }

    [Fact]
    public async Task A_device_that_answers_unknown_to_the_dialler_is_refused()
    {
        // The Mac knows the PC but the PC never paired with the Mac
        mac.Trust.Add(pc.Device, HexLinkRights.View);
        int port = mac.Listen();
        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        HexLinkException refused = await Assert.ThrowsAsync<HexLinkException>(() => pc.Transport.Dial("127.0.0.1", port, cancel.Token));
        Assert.Equal("unknown", refused.Reason);
        Assert.False(mac.Accepted.Task.IsCompleted);
    }

    [Fact]
    public async Task A_forgotten_device_is_cut_at_once_and_its_redial_refused()
    {
        (HexLinkConnection dialled, HexLinkConnection accepted) = await Link();

        Assert.True(mac.Trust.Forget(pc.Device.Id));

        // The PC's end sees its link close, and the Mac's end has no rights left
        Assert.Equal("", await HexLinkTestMachine.ReadToEnd(dialled.Stream));
        Assert.Equal(HexLinkRights.None, accepted.Rights);
        Assert.Empty(mac.Transport.Links);

        dialled.Dispose();
        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        HexLinkException refused = await Assert.ThrowsAsync<HexLinkException>(() => pc.Transport.Dial("127.0.0.1", mac.Transport.Port, mac.Device.Id, cancel.Token));
        Assert.Equal("unknown", refused.Reason);
    }

    [Fact]
    public async Task Rights_are_read_live()
    {
        (_, HexLinkConnection accepted) = await Link();
        Assert.Equal(HexLinkRights.View | HexLinkRights.Steer, accepted.Rights);

        mac.Trust.SetRights(pc.Device.Id, HexLinkRights.Edit);
        Assert.Equal(HexLinkRights.Edit, accepted.Rights);
    }

    [Fact]
    public async Task A_second_link_to_a_linked_device_is_refused_busy()
    {
        (HexLinkConnection dialled, _) = await Link();

        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        HexLinkException busy = await Assert.ThrowsAsync<HexLinkException>(() => pc.Transport.Dial("127.0.0.1", mac.Transport.Port, mac.Device.Id, cancel.Token));
        Assert.Equal("busy", busy.Reason);

        // Another transport of the same device's, which doesn't know it has a link already, is told so by the Mac
        using var again = new HexLinkTransport(pc.Identity, pc.Trust);
        busy = await Assert.ThrowsAsync<HexLinkException>(() => again.Dial("127.0.0.1", mac.Transport.Port, mac.Device.Id, cancel.Token));
        Assert.Equal("busy", busy.Reason);

        // Once the first is gone a new one is let in
        dialled.Dispose();
        HexLinkConnection replaced = await RetryDial(again, mac.Transport.Port, mac.Device.Id);
        Assert.Equal(mac.Device.Id, replaced.Device.Id);
        replaced.Dispose();
    }

    // The Mac notices a closed link when it next looks, so a redial straight after the close may still be busy for a moment
    private static async Task<HexLinkConnection> RetryDial(HexLinkTransport transport, int port, string id)
    {
        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        while (true)
        {
            try
            {
                return await transport.Dial("127.0.0.1", port, id, cancel.Token);
            }
            catch (HexLinkException e) when (e.Reason == "busy" && !cancel.IsCancellationRequested)
            {
                await Task.Delay(50);
            }
        }
    }

    [Fact]
    public async Task Two_devices_dialling_each_other_at_once_end_with_one_link()
    {
        HexLinkTestMachine.Know(pc, HexLinkRights.View, mac, HexLinkRights.View);
        int pcPort = pc.Listen(), macPort = mac.Listen();
        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);

        Task<HexLinkConnection> one = pc.Transport.Dial("127.0.0.1", macPort, mac.Device.Id, cancel.Token);
        Task<HexLinkConnection> two = mac.Transport.Dial("127.0.0.1", pcPort, pc.Device.Id, cancel.Token);
        try
        {
            await Task.WhenAll(one, two);
        }
        catch (HexLinkException)
        {
        }

        // At least one got through, and neither side holds two
        Assert.True(one.IsCompletedSuccessfully || two.IsCompletedSuccessfully);
        Assert.True(pc.Transport.Links.Count <= 1);
        Assert.True(mac.Transport.Links.Count <= 1);
    }

    [Fact]
    public async Task An_address_that_isnt_private_is_dropped_before_tls()
    {
        HexLinkTestMachine.Know(pc, HexLinkRights.View, mac, HexLinkRights.View);
        var asked = new List<IPAddress>();
        mac.Transport.Admits = address =>
        {
            lock (asked)
                asked.Add(address);
            return false;
        };
        int port = mac.Listen();

        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        await Assert.ThrowsAnyAsync<IOException>(() => pc.Transport.Dial("127.0.0.1", port, mac.Device.Id, cancel.Token));
        Assert.False(mac.Accepted.Task.IsCompleted);
        lock (asked)
            Assert.True(IPAddress.IsLoopback(Assert.Single(asked).IsIPv4MappedToIPv6 ? asked[0].MapToIPv4() : asked[0]));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.255", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("172.15.0.1", false)]
    [InlineData("192.168.0.30", true)]
    [InlineData("192.169.0.1", false)]
    [InlineData("169.254.10.10", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.81.32.118", true)]
    [InlineData("100.80.122.37", true)]
    [InlineData("100.127.255.255", true)]
    [InlineData("100.128.0.1", false)]
    [InlineData("100.63.255.255", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("1.1.1.1", false)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("febf::1", true)]
    [InlineData("fec0::1", false)]
    [InlineData("fd7a:115c:a1e0::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("2001:4860:4860::8888", false)]
    [InlineData("::ffff:192.168.1.1", true)]
    [InlineData("::ffff:100.80.122.37", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void Private_addresses_are_the_ones_said(string address, bool isPrivate)
    {
        Assert.Equal(isPrivate, HexLinkAddresses.IsPrivate(IPAddress.Parse(address)));
    }

    [Fact]
    public void Nothing_listens_before_start_and_stop_closes_it()
    {
        Assert.False(mac.Transport.Listening);
        Assert.Equal(0, mac.Transport.Port);
        int port = mac.Listen();
        Assert.True(mac.Transport.Listening);
        Assert.NotEqual(0, port);
        mac.Transport.Stop();
        Assert.False(mac.Transport.Listening);
        using var client = new System.Net.Sockets.TcpClient();
        Assert.ThrowsAny<System.Net.Sockets.SocketException>(() => client.Connect("127.0.0.1", port));
    }

    [Fact]
    public async Task Stop_closes_every_link()
    {
        (HexLinkConnection dialled, _) = await Link();
        mac.Transport.Stop();
        Assert.Equal("", await HexLinkTestMachine.ReadToEnd(dialled.Stream));
        Assert.Empty(mac.Transport.Links);
    }

    [Fact]
    public async Task The_first_lines_take_nothing_of_what_follows()
    {
        // Bytes sent by the dialler straight after its OK reach the protocol whole
        HexLinkTestMachine.Know(pc, HexLinkRights.View, mac, HexLinkRights.View);
        int port = mac.Listen();
        using var cancel = new CancellationTokenSource(HexLinkTestMachine.WAIT);
        HexLinkConnection dialled = await pc.Transport.Dial("127.0.0.1", port, mac.Device.Id, cancel.Token);
        await dialled.Stream.WriteAsync(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\"}\n"));
        await dialled.Stream.FlushAsync();
        HexLinkConnection accepted = await mac.Accepted.Task.WaitAsync(HexLinkTestMachine.WAIT);
        Assert.Equal("{\"jsonrpc\":\"2.0\"}", await HexLinkTestMachine.ReadLine(accepted.Stream));
        dialled.Dispose();
    }
}
