using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Hex.Core;

/// <summary>
/// Which addresses a link may come from by default (docs/hex-link-plan.md, 3.5). Only the ones that can't be reached from the open
/// internet: this machine, the private ranges a home or office network hands out, link-local, and the 100.64/10 block
/// carrier-grade NAT and the VPNs people run (Tailscale) use. Anything else is dropped before TLS starts, so a router
/// that forwards the port by mistake still lets nobody in unless the user has enabled public connections.
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

    /// <summary>
    /// The addresses a device said it's at, in the order they're worth dialling: a home or office network's first
    /// (192.168/16, then 10/8, then 172.16/12, which Docker, WSL and Hyper-V's virtual switches take as often as a real
    /// network does), then this machine, link-local, the 100.64/10 of a VPN (it works, the long way round), and IPv6 last.
    /// What isn't private is left out, there's no dialling it. Each once, the order among equals kept.
    /// </summary>
    public static List<IPAddress> Preferred(IEnumerable<IPAddress> addresses)
    {
        var seen = new HashSet<IPAddress>();
        return [.. addresses.Select(address => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
            .Where(address => IsPrivate(address) && seen.Add(address))
            .Select((address, at) => (address, at))
            .OrderBy(pair => Rank(pair.address)).ThenBy(pair => pair.at)
            .Select(pair => pair.address)];
    }

    /// <summary>
    /// Addresses in the order worth dialling from this machine: those inside a subnet of one of its own interfaces first
    /// (a virtual switch's 192.168.x.1 is not the LAN just because it is in 192.168/16), then the usual rank order. Nothing
    /// is dropped, only each address once, the order among equals kept; what may be dialled is DialAddresses' to say.
    /// </summary>
    public static List<IPAddress> Preferred(IEnumerable<IPAddress> addresses, IReadOnlyList<(IPAddress Address, int PrefixLength)> local)
    {
        var seen = new HashSet<IPAddress>();
        return [.. addresses.Select(address => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address)
            .Where(address => seen.Add(address))
            .Select((address, at) => (address, at))
            .OrderBy(pair => OnLocalNetwork(pair.address, local) ? 0 : 1).ThenBy(pair => Rank(pair.address)).ThenBy(pair => pair.at)
            .Select(pair => pair.address)];
    }

    /// <summary>The unicast IPv4 addresses and prefix lengths of this machine's interfaces that are up, loopback left out.</summary>
    public static List<(IPAddress Address, int PrefixLength)> LocalNetworks()
    {
        var networks = new List<(IPAddress, int)>();
        try
        {
            foreach (var face in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (face.OperationalStatus != OperationalStatus.Up || face.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;
                foreach (var unicast in face.GetIPProperties().UnicastAddresses)
                    if (unicast.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(unicast.Address))
                        networks.Add((unicast.Address, unicast.PrefixLength));
            }
        }
        catch (NetworkInformationException)
        {
        }

        return networks;
    }

    private static bool OnLocalNetwork(IPAddress address, IReadOnlyList<(IPAddress Address, int PrefixLength)> local)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        Span<byte> a = stackalloc byte[4];
        Span<byte> b = stackalloc byte[4];
        address.TryWriteBytes(a, out _);
        foreach (var (own, prefix) in local)
        {
            if (own.AddressFamily != AddressFamily.InterNetwork || prefix is < 1 or > 32 || !own.TryWriteBytes(b, out _))
                continue;
            int bits = prefix;
            bool same = true;
            for (int i = 0; i < 4 && bits > 0 && same; i++, bits -= 8)
            {
                int mask = bits >= 8 ? 0xFF : (0xFF << (8 - bits)) & 0xFF;
                same = (a[i] & mask) == (b[i] & mask);
            }

            if (same)
                return true;
        }

        return false;
    }

    // How good an address is to dial, lower first
    private static int Rank(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return 7;
        Span<byte> bytes = stackalloc byte[4];
        address.TryWriteBytes(bytes, out _);
        return bytes[0] switch
        {
            192 when bytes[1] == 168 => 0,
            10 => 1,
            172 when (bytes[1] & 0xF0) == 16 => 2,
            127 => 3,
            169 when bytes[1] == 254 => 4,
            100 when (bytes[1] & 0xC0) == 64 => 5,
            _ => 6
        };
    }

    /// <summary>An address as a person reads it, an IPv4 one written as IPv6 by a dual-mode socket said as IPv4.</summary>
    public static string Show(IPAddress address) => (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
}
