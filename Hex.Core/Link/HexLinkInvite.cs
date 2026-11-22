using System.Buffers.Binary;
using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Hex.Core;

/// <summary>A single-use invitation. The text and secret must never be written to the audit log.</summary>
internal sealed class HexLinkInvite
{
    internal const string Prefix = "hex-link:v1.";
    private const string PublicPrefix = "hex-link:v2.";
    internal const int MaxEndpoints = 8;
    private const int HeaderSize = 75;
    private const int PrivateSize = HeaderSize + MaxEndpoints * 17;
    private const int PublicSize = PrivateSize + 3 + HexLinkInviteRoute.MaxHostLength;
    internal const int MaxTokenLength = 12 + (PublicSize * 4 + 2) / 3;

    private HexLinkInvite(string pin, byte[] secret, int port, DateTimeOffset until, IPAddress[] addresses, HexLinkInviteRoute? publicRoute)
    {
        Pin = pin;
        Secret = secret;
        Port = port;
        Until = until;
        Addresses = addresses;
        PublicRoute = publicRoute;
    }

    public string Pin { get; }
    private byte[] Secret { get; }
    public int Port { get; }
    public DateTimeOffset Until { get; }
    public IReadOnlyList<IPAddress> Addresses { get; }
    public HexLinkInviteRoute? PublicRoute { get; }

    // Tailscale uses a CGNAT IPv4 range and a dedicated IPv6 ULA prefix.
    internal static bool IsVpn(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        address.TryWriteBytes(bytes, out int length);
        return length == 4 ? bytes[0] == 100 && (bytes[1] & 0xC0) == 64
            : bytes[..length].StartsWith((ReadOnlySpan<byte>)[0xfd, 0x7a, 0x11, 0x5c, 0xa1, 0xe0]);
    }

    public static HexLinkInvite? Create(string pin, int port, DateTimeOffset until, IEnumerable<IPAddress> addresses,
        HexLinkInviteRoute? publicRoute = null)
    {
        IPAddress[] endpoints = [.. HexLinkAddresses.Preferred(addresses).Where(IsPortable).Take(MaxEndpoints)];
        return (endpoints.Length == 0 && publicRoute is null) || port is <= 0 or > 65535 ? null
            : new HexLinkInvite(pin, RandomNumberGenerator.GetBytes(32), port, until, endpoints, publicRoute);
    }

    public string Encode()
    {
        Span<byte> bytes = stackalloc byte[PublicSize];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)Port);
        BinaryPrimitives.WriteInt64BigEndian(bytes[2..], Until.ToUnixTimeSeconds());
        Convert.FromHexString(Pin).CopyTo(bytes[10..]);
        Secret.CopyTo(bytes[42..]);
        bytes[74] = (byte)Addresses.Count;
        int offset = HeaderSize;
        foreach (IPAddress address in Addresses)
        {
            address.TryWriteBytes(bytes[(offset + 1)..], out int length);
            bytes[offset] = (byte)length;
            offset += length + 1;
        }

        if (PublicRoute is null)
            return Prefix + Base64Url.EncodeToString(bytes[..offset]);

        BinaryPrimitives.WriteUInt16BigEndian(bytes[offset..], (ushort)PublicRoute.Port);
        bytes[offset + 2] = (byte)PublicRoute.Host.Length;
        offset += 3 + Encoding.ASCII.GetBytes(PublicRoute.Host, bytes[(offset + 3)..]);
        return PublicPrefix + Base64Url.EncodeToString(bytes[..offset]);
    }

    public static bool TryParse(string text, out HexLinkInvite? invite)
    {
        invite = null;
        bool hasPublic = text.StartsWith(PublicPrefix, StringComparison.Ordinal);
        int maxLength = hasPublic ? MaxTokenLength : Prefix.Length + 282;
        if (text.Length > maxLength || (!hasPublic && !text.StartsWith(Prefix, StringComparison.Ordinal)))
            return false;

        Span<byte> bytes = stackalloc byte[PublicSize];
        int written;
        try
        {
            if (!Base64Url.TryDecodeFromChars(text.AsSpan(Prefix.Length), bytes, out written) || written < HeaderSize)
                return false;
        }
        catch (FormatException)
        {
            return false;
        }
        int port = BinaryPrimitives.ReadUInt16BigEndian(bytes);
        long seconds = BinaryPrimitives.ReadInt64BigEndian(bytes[2..]);
        int count = bytes[74];
        if (port == 0 || seconds is < 0 or > 253402300799 || count > MaxEndpoints || (count == 0 && !hasPublic))
            return false;

        var addresses = new IPAddress[count];
        int offset = HeaderSize;
        for (int i = 0; i < count; i++)
        {
            if (offset >= written)
                return false;
            int length = bytes[offset++];
            if (length is not (4 or 16) || offset + length > written)
                return false;
            var address = new IPAddress(bytes.Slice(offset, length));
            if (!IsPortable(address) || address.IsIPv4MappedToIPv6)
                return false;
            addresses[i] = address;
            offset += length;
        }

        HexLinkInviteRoute? publicRoute = null;
        if (hasPublic)
        {
            if (written - offset < 3)
                return false;
            int publicPort = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]);
            int length = bytes[offset + 2];
            offset += 3;
            if (length == 0 || written - offset != length ||
                !HexLinkInviteRoute.TryCreate(Encoding.ASCII.GetString(bytes.Slice(offset, length)), publicPort, out publicRoute))
                return false;
            offset += length;
        }

        if (offset != written)
            return false;
        invite = new HexLinkInvite(Convert.ToHexString(bytes.Slice(10, 32)), bytes.Slice(42, 32).ToArray(),
            port, DateTimeOffset.FromUnixTimeSeconds(seconds), addresses, publicRoute);
        return true;
    }

    // IPv6 link-local zone IDs are scoped to the host's interfaces and cannot be passed to another machine.
    private static bool IsPortable(IPAddress address) => HexLinkAddresses.IsPrivate(address) && !address.IsIPv6LinkLocal;

    private const HexLinkRights Proposed = HexLinkRights.View | HexLinkRights.Steer | HexLinkRights.Edit;

    internal static string TermsLine(HexLinkPairingTerms? terms) => terms is null ? "terms -" :
        FormattableString.Invariant($"terms {(int)terms.Preset} {(int)(terms.HostGrants & Proposed)} {(int)(terms.HostRequests & Proposed)}");

    internal static bool TryTerms(string line, out HexLinkPairingTerms? terms)
    {
        terms = null;
        if (line == "terms -")
            return true;
        if (line.Length > 32)
            return false;
        string[] words = line.Split(' ');
        if (words.Length != 4 || words[0] != "terms"
            || !byte.TryParse(words[1], NumberStyles.None, CultureInfo.InvariantCulture, out byte role)
            || !byte.TryParse(words[2], NumberStyles.None, CultureInfo.InvariantCulture, out byte grants)
            || !byte.TryParse(words[3], NumberStyles.None, CultureInfo.InvariantCulture, out byte asks))
            return false;

        HexLinkPreset preset = role <= (int)HexLinkPreset.Custom ? (HexLinkPreset)role : HexLinkPreset.Custom;
        terms = new HexLinkPairingTerms(preset, Mask(grants), Mask(asks));
        return true;

        static HexLinkRights Mask(byte value) => ((HexLinkRights)value & HexLinkRights.View) == 0
            ? HexLinkRights.None : (HexLinkRights)value & Proposed;
    }

    internal byte[] Proof(bool host, string joinerPin, byte[] hostNonce, byte[] joinerNonce)
    {
        using var mac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Secret);
        mac.AppendData(host ? "hex-link-invite-host-v1"u8 : "hex-link-invite-joiner-v1"u8);
        mac.AppendData(Convert.FromHexString(Pin));
        mac.AppendData(Convert.FromHexString(joinerPin));
        mac.AppendData(hostNonce);
        mac.AppendData(joinerNonce);
        return mac.GetHashAndReset();
    }
}
