using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Hex.Core;

public sealed partial class HexRouterPortMapper
{
    private sealed class UpnpMapping(RouterRoute route, int localPort, TimeSpan timeout) : IMapping
    {
        private const string SoapNamespace = "http://schemas.xmlsoap.org/soap/envelope/";
        private const int NoSuchEntry = 714;
        private readonly string description = "Hex Link " + Guid.NewGuid().ToString("N");
        private readonly string port = localPort.ToString(CultureInfo.InvariantCulture);
        private Service? service;

        private sealed record Service(Uri Address, string Type);
        private sealed record Response(XElement? Body, int Fault);

        public async Task<Grant?> MapAsync(CancellationToken cancellationToken)
        {
            service ??= await Discover().ConfigureAwait(false);
            if (service is null)
                return null;

            var external = await Invoke("GetExternalIPAddress").ConfigureAwait(false);
            if (!IPAddress.TryParse(Field(external?.Body, "NewExternalIPAddress"), out var address) || !IsPublicIpv4(address))
                return null;

            var existing = await Specific().ConfigureAwait(false);
            if (existing?.Fault != NoSuchEntry && !Owns(existing?.Body))
                return null;

            cancellationToken.ThrowIfCancellationRequested();
            var sent = DateTimeOffset.UtcNow;
            var added = await InvokeMapping("AddPortMapping",
                new("NewInternalPort", port), new("NewInternalClient", route.Local.ToString()),
                new("NewEnabled", "1"), new("NewPortMappingDescription", description),
                new("NewLeaseDuration", Lifetime.ToString(CultureInfo.InvariantCulture))).ConfigureAwait(false);
            if (added is { Fault: > 0 })
                return null;

            // Check ownership and the reported lifetime even when the Add response was lost.
            var confirmed = await Specific().ConfigureAwait(false);
            if (Owns(confirmed?.Body) && Field(confirmed!.Body, "NewEnabled") is "1" or "true"
                && uint.TryParse(Field(confirmed!.Body, "NewLeaseDuration"), NumberStyles.None,
                    CultureInfo.InvariantCulture, out uint lifetime) && PublicGrant(address, localPort, lifetime, sent) is { } grant)
                return grant;

            await DeleteAsync().ConfigureAwait(false);
            return null;
        }

        public async Task DeleteAsync()
        {
            if (service is not null && Owns((await Specific().ConfigureAwait(false))?.Body))
                await InvokeMapping("DeletePortMapping").ConfigureAwait(false);
        }

        private bool Owns(XElement? body) => Field(body, "NewPortMappingDescription") == description
            && Field(body, "NewInternalPort") == port
            && IPAddress.TryParse(Field(body, "NewInternalClient"), out var client) && client.Equals(route.Local);

        private Task<Response?> Specific() => InvokeMapping("GetSpecificPortMappingEntry");

        private Task<Response?> InvokeMapping(string action, params XElement[] arguments) => Invoke(action,
            [new("NewRemoteHost", ""), new("NewExternalPort", port), new("NewProtocol", "TCP"), .. arguments]);

        private async Task<Service?> Discover()
        {
            Uri? location = await DiscoveryLocation().ConfigureAwait(false);
            if (location is null)
                return null;
            using var request = new HttpRequestMessage(HttpMethod.Get, location);
            XDocument? response = await Send(request).ConfigureAwait(false);
            if (response?.Root is not { } root)
                return null;

            if (Field(root, "URLBase") is { Length: > 0 } urlBase)
            {
                if (!Uri.TryCreate(urlBase, UriKind.Absolute, out var declared) || !OnGateway(declared))
                    return null;
                location = declared;
            }

            foreach (var entry in root.Descendants().Where(node => node.Name.LocalName == "service"))
            {
                string? type = Field(entry, "serviceType");
                if (type is not ("urn:schemas-upnp-org:service:WANIPConnection:2" or
                    "urn:schemas-upnp-org:service:WANIPConnection:1" or "urn:schemas-upnp-org:service:WANPPPConnection:1"))
                    continue;
                if (Field(entry, "controlURL") is { Length: > 0 } url
                    && Uri.TryCreate(location, url, out var control) && OnGateway(control))
                    return new(control, type);
            }
            return null;
        }

        private async Task<Uri?> DiscoveryLocation()
        {
            using var wait = new CancellationTokenSource(timeout);
            try
            {
                using var client = new UdpClient(new IPEndPoint(route.Local, 0));
                if (route.Discovery.Address.GetAddressBytes()[0] is >= 224 and < 240)
                {
                    client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, route.Local.GetAddressBytes());
                    client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                }

                foreach (int version in new[] { 1, 2 })
                {
                    byte[] message = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\n"
                        + $"HOST: {route.Discovery}\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\n"
                        + $"ST: urn:schemas-upnp-org:device:InternetGatewayDevice:{version}\r\n\r\n");
                    await client.SendAsync(message, route.Discovery, wait.Token).ConfigureAwait(false);
                }

                while (!wait.IsCancellationRequested)
                {
                    var packet = await client.ReceiveAsync(wait.Token).ConfigureAwait(false);
                    if (!packet.RemoteEndPoint.Address.Equals(route.Control.Address) || packet.Buffer.Length > 8192)
                        continue;
                    string[] lines = Encoding.ASCII.GetString(packet.Buffer).Split("\r\n");
                    if (!lines[0].StartsWith("HTTP/1.1 200 ", StringComparison.Ordinal))
                        continue;
                    foreach (string line in lines)
                    {
                        if (line.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase)
                            && Uri.TryCreate(line[9..].Trim(), UriKind.Absolute, out Uri? uri) && OnGateway(uri))
                            return uri;
                    }
                }
            }
            catch (Exception error) when (error is SocketException or OperationCanceledException)
            {
            }
            return null;
        }

        private bool OnGateway(Uri address) => address.IsAbsoluteUri && address.Scheme == Uri.UriSchemeHttp
            && address.UserInfo.Length == 0 && address.Fragment.Length == 0
            && IPAddress.TryParse(address.Host, out var host) && host.Equals(route.Control.Address);

        private async Task<Response?> Invoke(string action, params XElement[] arguments)
        {
            if (service is null)
                return null;
            XNamespace soap = SoapNamespace;
            XNamespace type = service.Type;
            var method = new XElement(type + action, arguments);
            var envelope = new XElement(soap + "Envelope",
                new XAttribute(soap + "encodingStyle", "http://schemas.xmlsoap.org/soap/encoding/"),
                new XElement(soap + "Body", method));

            using var request = new HttpRequestMessage(HttpMethod.Post, service.Address)
            {
                Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml")
            };
            request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{service.Type}#{action}\"");
            var document = await Send(request).ConfigureAwait(false);
            var body = document?.Root?.Element(soap + "Body");
            if (body?.Element(type + (action + "Response")) is { } success)
                return new(success, 0);
            string? fault = body?.Element(soap + "Fault")?.Descendants().FirstOrDefault(node => node.Name.LocalName == "errorCode")?.Value;
            return int.TryParse(fault, NumberStyles.None, CultureInfo.InvariantCulture, out int code) ? new(null, code) : null;
        }

        private async Task<XDocument?> Send(HttpRequestMessage request)
        {
            // No DNS, redirects or proxy. The same gateway selected for UDP is the only allowed HTTP host.
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectCallback = async (context, token) =>
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        socket.Bind(new IPEndPoint(route.Local, 0));
                        await socket.ConnectAsync(new IPEndPoint(route.Control.Address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };
            using var http = new HttpClient(handler) { Timeout = timeout, MaxResponseContentBufferSize = 131072 };
            try
            {
                using var reply = await http.SendAsync(request).ConfigureAwait(false);
                using var stream = await reply.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 131072 });
                return XDocument.Load(reader);
            }
            catch (Exception error) when (error is HttpRequestException or IOException or XmlException or OperationCanceledException)
            {
                return null;
            }
        }

        private static string? Field(XElement? body, string name) =>
            body?.Elements().FirstOrDefault(node => node.Name.LocalName == name)?.Value.Trim();
    }
}
