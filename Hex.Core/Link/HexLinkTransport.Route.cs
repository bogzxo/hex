using System.Net.Security;
using System.Net.Sockets;

namespace Hex.Core;

internal sealed partial class HexLinkTransport
{
    private async Task<bool> ListenerSettlesKnown(SslStream ssl, Socket socket, Hello hello, string address, CancellationToken cancel)
    {
        // An incoming connection's source port is ephemeral. Match the peer's selected IP.
        if (trust.Find(hello.Device.Id)?.PreferredRoute is { } route &&
            HexLinkRedial.Endpoint(address, route.Port) != route)
        {
            Audit?.Write("refused", hello.Device.Id, $"{address}, a different route is selected");
            await Say(ssl, "NO route", cancel);
            return false;
        }

        return await ListenerSettles(ssl, socket, hello.Device, address, port: 0, cancel, bulk: NegotiateBulk(hello.Bulk));
    }

    private void RequireSelectedRoute(string pin, string address, int port)
    {
        if (trust.Find(pin)?.PreferredRoute is { } route && HexLinkRedial.Endpoint(address, port) != route)
            throw new HexLinkRefused("route", "a different route is selected for this device");
    }
}
