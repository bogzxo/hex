using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using Hex.Core;

namespace Hex.Tests;

public sealed class HexRouterPortMapperTests
{
    private const int ListenerPort = 47777;
    private static readonly TimeSpan TestLimit = TimeSpan.FromSeconds(12);
    // These are response payloads only. All UDP and HTTP requests stay on loopback.
    private static readonly IPAddress PublicAddress = IPAddress.Parse("8.8.4.4");

    [Fact]
    public async Task PcpUsesTcpNonceAndGrantedEndpointThroughRenewAndDelete()
    {
        using var control = LoopbackUdp();
        using var discovery = LoopbackUdp();
        var mapping = Mapper(control, discovery).TryMapAsync(ListenerPort);
        var first = await Receive(control);
        AssertPcpRequest(first, 900, ListenerPort);

        var unrelated = PcpResponse(first.Buffer, PublicAddress, 51500, 120);
        unrelated[24] ^= 1;
        await control.SendAsync(unrelated, first.RemoteEndPoint);
        await Reply(control, first, PcpResponse(first.Buffer, PublicAddress, 51000, 120));

        var lease = Assert.IsAssignableFrom<IHexRouterPortLease>(await mapping.WaitAsync(TestLimit));
        Assert.Equal(new IPEndPoint(PublicAddress, 51000), lease.PublicEndPoint);
        DateTimeOffset firstExpiry = lease.ExpiresAt;
        var renewal = lease.RenewAsync();
        var refresh = await Receive(control);
        AssertPcpRequest(refresh, 900, 51000);
        Assert.Equal(first.Buffer[24..36], refresh.Buffer[24..36]);
        Assert.Equal(PublicAddress.MapToIPv6().GetAddressBytes(), refresh.Buffer[44..60]);
        await Reply(control, refresh, PcpResponse(refresh.Buffer, PublicAddress, 51001, 180));
        Assert.True(await renewal.WaitAsync(TestLimit));
        Assert.Equal(51001, lease.PublicEndPoint.Port);
        Assert.True(lease.ExpiresAt > firstExpiry);

        var closing = lease.DisposeAsync().AsTask();
        var delete = await Receive(control);
        AssertPcpRequest(delete, 0, 51001);
        Assert.Equal(first.Buffer[24..36], delete.Buffer[24..36]);
        await Reply(control, delete, PcpResponse(delete.Buffer, PublicAddress, 51001, 0));
        await closing.WaitAsync(TestLimit);
        await lease.DisposeAsync();
        Assert.False(await lease.RenewAsync());
        Assert.Equal(0, control.Available);
    }

    [Fact]
    public async Task PcpGrantArrivingAfterCancellationIsDeleted()
    {
        using var control = LoopbackUdp();
        using var discovery = LoopbackUdp();
        using var cancel = new CancellationTokenSource();
        var mapping = Mapper(control, discovery).TryMapAsync(ListenerPort, cancel.Token);
        var first = await Receive(control);
        cancel.Cancel();
        await Reply(control, first, PcpResponse(first.Buffer, PublicAddress, 51000, 120));

        var delete = await Receive(control);
        AssertPcpRequest(delete, 0, 51000);
        Assert.Equal(first.Buffer[24..36], delete.Buffer[24..36]);
        await Reply(control, delete, PcpResponse(delete.Buffer, PublicAddress, 51000, 0));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mapping.WaitAsync(TestLimit));
        Assert.Equal(0, discovery.Available);
    }

    [Fact]
    public async Task NatPmpFallbackUsesReportedPublicAddressAndDeletesOnlyTheTcpPort()
    {
        using var control = LoopbackUdp();
        using var discovery = LoopbackUdp();
        var mapping = Mapper(control, discovery).TryMapAsync(ListenerPort);
        await RejectPcp(control, natPmpOnly: true);
        var address = await Receive(control);
        Assert.Equal(new byte[] { 0, 0 }, address.Buffer);
        byte[] addressReply = [0, 128, 0, 0, 0, 0, 0, 1, .. PublicAddress.GetAddressBytes()];
        await Reply(control, address, addressReply);
        var map = await Receive(control);
        Assert.Equal(new byte[] { 0, 2, 0, 0, 186, 161, 186, 161, 0, 0, 3, 132 }, map.Buffer);
        await Reply(control, map, NatResponse(51900, 90));

        var lease = Assert.IsAssignableFrom<IHexRouterPortLease>(await mapping.WaitAsync(TestLimit));
        Assert.Equal(new IPEndPoint(PublicAddress, 51900), lease.PublicEndPoint);
        var renewal = lease.RenewAsync();
        await Reply(control, await Receive(control), addressReply);
        var refresh = await Receive(control);
        Assert.Equal(51900, BinaryPrimitives.ReadUInt16BigEndian(refresh.Buffer.AsSpan(6)));
        await Reply(control, refresh, NatResponse(51901, 120));
        Assert.True(await renewal.WaitAsync(TestLimit));
        Assert.Equal(51901, lease.PublicEndPoint.Port);

        var closing = lease.DisposeAsync().AsTask();
        var delete = await Receive(control);
        Assert.Equal(new byte[] { 0, 2, 0, 0, 186, 161, 0, 0, 0, 0, 0, 0 }, delete.Buffer);
        await Reply(control, delete, NatResponse(0, 0));
        await closing.WaitAsync(TestLimit);
    }

    [Fact]
    public async Task NatPmpTimeoutDoesNotDeleteAnUnconfirmedMapping()
    {
        using var control = LoopbackUdp();
        using var discovery = LoopbackUdp();
        var mapping = Mapper(control, discovery, TimeSpan.FromSeconds(1)).TryMapAsync(ListenerPort);
        await RejectPcp(control, natPmpOnly: true);
        var address = await Receive(control);
        await Reply(control, address, [0, 128, 0, 0, 0, 0, 0, 1, .. PublicAddress.GetAddressBytes()]);

        for (int attempt = 0; attempt < 2; attempt++)
        {
            var map = await Receive(control);
            Assert.Equal(900u, BinaryPrimitives.ReadUInt32BigEndian(map.Buffer.AsSpan(8)));
        }
        await Receive(discovery);
        Assert.Null(await mapping.WaitAsync(TestLimit));
        Assert.Equal(0, control.Available);
    }

    [Fact]
    public async Task UpnpVerifiesFiniteOwnershipAndRefusesToDeleteAReplacement()
    {
        using var control = LoopbackUdp();
        using var discovery = LoopbackUdp();
        using var http = new TcpListener(IPAddress.Loopback, 0);
        http.Start();
        var mapping = Mapper(control, discovery).TryMapAsync(ListenerPort);
        await DiscoverUpnp(control, discovery, http);

        using (var external = await ReceiveHttp(http, "GetExternalIPAddress"))
            await external.Respond(Soap("GetExternalIPAddress", new XElement("NewExternalIPAddress", PublicAddress.ToString())));
        using (var lookup = await ReceiveHttp(http, "GetSpecificPortMappingEntry"))
            await lookup.Respond(NoSuchEntry(), 500);

        string description;
        using (var add = await ReceiveHttp(http, "AddPortMapping"))
        {
            Assert.Equal(ListenerPort.ToString(), add.Field("NewExternalPort"));
            Assert.Equal(ListenerPort.ToString(), add.Field("NewInternalPort"));
            Assert.Equal("TCP", add.Field("NewProtocol"));
            Assert.Equal("127.0.0.1", add.Field("NewInternalClient"));
            Assert.Equal("900", add.Field("NewLeaseDuration"));
            description = Assert.IsType<string>(add.Field("NewPortMappingDescription"));
            Assert.StartsWith("Hex Link ", description);
            await add.Respond(Soap("AddPortMapping"));
        }
        using (var confirm = await ReceiveHttp(http, "GetSpecificPortMappingEntry"))
            await confirm.Respond(UpnpEntry(description));
        var lease = Assert.IsAssignableFrom<IHexRouterPortLease>(await mapping.WaitAsync(TestLimit));
        Assert.Equal(new IPEndPoint(PublicAddress, ListenerPort), lease.PublicEndPoint);
        Assert.InRange(lease.ExpiresAt - DateTimeOffset.UtcNow, TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(120));

        var closing = lease.DisposeAsync().AsTask();
        using (var lookup = await ReceiveHttp(http, "GetSpecificPortMappingEntry"))
            await lookup.Respond(UpnpEntry("Another application"));
        await closing.WaitAsync(TestLimit);
        Assert.False(http.Pending());
    }

    [Fact]
    public async Task UpnpDeletesAConfirmedMappingWhenConsentIsWithdrawnDuringAdd()
    {
        using var control = LoopbackUdp();
        using var discovery = LoopbackUdp();
        using var http = new TcpListener(IPAddress.Loopback, 0);
        using var cancel = new CancellationTokenSource();
        http.Start();
        var mapping = Mapper(control, discovery).TryMapAsync(ListenerPort, cancel.Token);
        await DiscoverUpnp(control, discovery, http);

        using (var external = await ReceiveHttp(http, "GetExternalIPAddress"))
            await external.Respond(Soap("GetExternalIPAddress", new XElement("NewExternalIPAddress", PublicAddress.ToString())));
        using (var lookup = await ReceiveHttp(http, "GetSpecificPortMappingEntry"))
            await lookup.Respond(NoSuchEntry(), 500);

        string description;
        using (var add = await ReceiveHttp(http, "AddPortMapping"))
        {
            description = Assert.IsType<string>(add.Field("NewPortMappingDescription"));
            cancel.Cancel();
            await add.Respond(Soap("AddPortMapping"));
        }
        for (int lookup = 0; lookup < 2; lookup++)
        {
            using var ownership = await ReceiveHttp(http, "GetSpecificPortMappingEntry");
            await ownership.Respond(UpnpEntry(description));
        }
        using (var delete = await ReceiveHttp(http, "DeletePortMapping"))
        {
            Assert.Equal(ListenerPort.ToString(), delete.Field("NewExternalPort"));
            Assert.Equal("TCP", delete.Field("NewProtocol"));
            await delete.Respond(Soap("DeletePortMapping"));
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mapping.WaitAsync(TestLimit));
    }

    [Theory]
    [InlineData("10.20.30.40", false)]
    [InlineData("100.80.0.1", false)]
    [InlineData("172.26.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("192.0.2.1", false)]
    [InlineData("198.19.0.1", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("8.8.4.4", true)]
    public void OnlyPublicIpv4IsAdvertised(string address, bool expected) =>
        Assert.Equal(expected, HexRouterPortMapper.IsPublicIpv4(IPAddress.Parse(address)));

    private static UdpClient LoopbackUdp() => new(new IPEndPoint(IPAddress.Loopback, 0));

    private static HexRouterPortMapper Mapper(UdpClient control, UdpClient discovery, TimeSpan? timeout = null) =>
        new(() => new(IPAddress.Loopback, (IPEndPoint)control.Client.LocalEndPoint!,
            (IPEndPoint)discovery.Client.LocalEndPoint!), timeout ?? TimeSpan.FromSeconds(4));

    private static async Task<UdpReceiveResult> Receive(UdpClient socket)
    {
        using var timeout = new CancellationTokenSource(TestLimit);
        return await socket.ReceiveAsync(timeout.Token);
    }

    private static Task<int> Reply(UdpClient socket, UdpReceiveResult request, byte[] message) =>
        socket.SendAsync(message, message.Length, request.RemoteEndPoint);

    private static async Task RejectPcp(UdpClient control, bool natPmpOnly = false)
    {
        var pcp = await Receive(control);
        AssertPcpRequest(pcp, 900, ListenerPort);
        if (natPmpOnly)
        {
            await Reply(control, pcp, [0, 0, 0, 1, 0, 0, 0, 1]);
            return;
        }
        byte[] unsupported = new byte[24];
        unsupported[0] = 2;
        unsupported[1] = 129;
        unsupported[3] = 4;
        await Reply(control, pcp, unsupported);
    }

    private static void AssertPcpRequest(UdpReceiveResult packet, uint lifetime, int suggested)
    {
        var bytes = packet.Buffer;
        Assert.Equal(60, bytes.Length);
        Assert.Equal((2, 1, 6), ((int)bytes[0], (int)bytes[1], (int)bytes[36]));
        Assert.Equal(lifetime, BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4)));
        Assert.Equal(IPAddress.Loopback.MapToIPv6().GetAddressBytes(), bytes[8..24]);
        Assert.Equal(ListenerPort, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(40)));
        Assert.Equal(suggested, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(42)));
    }

    private static byte[] PcpResponse(byte[] request, IPAddress publicAddress, ushort port, uint lifetime)
    {
        byte[] response = (byte[])request.Clone();
        response[1] = 129;
        Array.Clear(response, 8, 16);
        BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(4), lifetime);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(42), port);
        publicAddress.MapToIPv6().GetAddressBytes().CopyTo(response, 44);
        return response;
    }

    private static byte[] NatResponse(ushort external, uint lifetime)
    {
        byte[] response = [0, 130, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(8), ListenerPort);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(10), external);
        BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(12), lifetime);
        return response;
    }

    private const string Service = "urn:schemas-upnp-org:service:WANIPConnection:1";
    private static readonly XNamespace SoapNamespace = "http://schemas.xmlsoap.org/soap/envelope/";

    private static async Task DiscoverUpnp(UdpClient control, UdpClient discovery, TcpListener http)
    {
        await RejectPcp(control);
        var nat = await Receive(control);
        await Reply(control, nat, [0, 128, 0, 2, 0, 0, 0, 1, 0, 0, 0, 0]);
        var search = await Receive(discovery);
        Assert.StartsWith("M-SEARCH * HTTP/1.1\r\n", Encoding.ASCII.GetString(search.Buffer));
        var location = $"http://127.0.0.1:{((IPEndPoint)http.LocalEndpoint).Port}/desc.xml";
        await Reply(discovery, search, Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nLOCATION: {location}\r\n\r\n"));
        using var description = await ReceiveHttp(http);
        Assert.Equal("GET /desc.xml HTTP/1.1", description.RequestLine);
        await description.Respond($"<root><device><serviceList><service><serviceType>{Service}</serviceType>"
            + "<controlURL>/control</controlURL></service></serviceList></device></root>");
    }

    private static string Soap(string name, params XElement[] fields) => Envelope(new XElement(
        XName.Get(name + "Response", Service), fields));

    private static string NoSuchEntry() => Envelope(new XElement(SoapNamespace + "Fault",
        new XElement("detail", new XElement("UPnPError", new XElement("errorCode", "714")))));

    private static string UpnpEntry(string description) => Soap("GetSpecificPortMappingEntry",
        new("NewInternalPort", ListenerPort), new("NewInternalClient", "127.0.0.1"),
        new("NewEnabled", "1"), new("NewPortMappingDescription", description), new("NewLeaseDuration", "120"));

    private static string Envelope(XElement content) =>
        new XElement(SoapNamespace + "Envelope", new XElement(SoapNamespace + "Body", content)).ToString(SaveOptions.DisableFormatting);

    private static async Task<HttpFixtureRequest> ReceiveHttp(TcpListener listener, string? action = null)
    {
        using var timeout = new CancellationTokenSource(TestLimit);
        TcpClient client = await listener.AcceptTcpClientAsync(timeout.Token);
        try
        {
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, leaveOpen: true);
            string requestLine = Assert.IsType<string>(await reader.ReadLineAsync(timeout.Token));
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadLineAsync(timeout.Token) is { Length: > 0 } line)
            {
                string[] pair = line.Split(':', 2);
                headers[pair[0]] = pair[1].Trim();
            }
            XElement? body = null;
            if (action is not null)
            {
                Assert.Equal("POST /control HTTP/1.1", requestLine);
                Assert.Equal($"\"{Service}#{action}\"", headers["SOAPAction"]);
                char[] xml = new char[int.Parse(headers["Content-Length"])];
                Assert.Equal(xml.Length, await reader.ReadBlockAsync(xml, timeout.Token));
                body = XDocument.Parse(new string(xml)).Root!.Element(SoapNamespace + "Body")!.Elements().Single();
            }
            return new(client, requestLine, body);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private sealed class HttpFixtureRequest(TcpClient client, string requestLine, XElement? body) : IDisposable
    {
        public string RequestLine => requestLine;
        public string? Field(string name) => body?.Elements().SingleOrDefault(element => element.Name.LocalName == name)?.Value;
        public async Task Respond(string body, int status = 200)
        {
            byte[] content = Encoding.UTF8.GetBytes(body);
            byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Fixture\r\nContent-Type: text/xml\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n");
            using var timeout = new CancellationTokenSource(TestLimit);
            await client.GetStream().WriteAsync(header, timeout.Token);
            await client.GetStream().WriteAsync(content, timeout.Token);
        }

        public void Dispose() => client.Dispose();
    }
}
