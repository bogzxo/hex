using System.Net;
using System.Net.Sockets;

namespace Hex.Core;

/// <summary>
/// Which addresses a link may come from (docs/hex-link-plan.md, 3.5). Only the ones that can't be reached from the open
/// internet: this machine, the private ranges a home or office network hands out, link-local, and the 100.64/10 block
/// carrier-grade NAT and the VPNs people run (Tailscale) use. Anything else is dropped before TLS starts, so a router
/// that forwards the port by mistake still lets nobody in.
/// </summary>
internal static class HexLinkAddresses
{
    /// <summary>Whether an address is loopback, private, link-local or CGNAT, IPv4, IPv6 or IPv4 written as IPv6.</summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            Span<byte> bytes = stackalloc byte[4];
            address.TryWriteBytes(bytes, out _);
            return bytes[0] == 10                                       // 10/8
                || (bytes[0] == 172 && (bytes[1] & 0xF0) == 16)         // 172.16/12
                || (bytes[0] == 192 && bytes[1] == 168)                 // 192.168/16
                || (bytes[0] == 169 && bytes[1] == 254)                 // 169.254/16, link-local
                || (bytes[0] == 100 && (bytes[1] & 0xC0) == 64);        // 100.64/10, CGNAT and VPNs
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            Span<byte> bytes = stackalloc byte[16];
            address.TryWriteBytes(bytes, out _);
            return (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80)      // fe80::/10, link-local
                || (bytes[0] & 0xFE) == 0xFC;                           // fc00::/7, unique-local
        }

        return false;
    }

    /// <summary>An address as a person reads it, an IPv4 one written as IPv6 by a dual-mode socket said as IPv4.</summary>
    public static string Show(IPAddress address) => (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
}
