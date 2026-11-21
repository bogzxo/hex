using System.Buffers.Binary;
using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;

namespace Hex.Core;

/// <summary>A private, single-use invitation. The text and secret must never be written to the audit log.</summary>
internal sealed class HexLinkInvite
{
    internal const string Prefix = "hex-link:v1.";
    internal const int MaxEndpoints = 8;
    private const int HeaderSize = 75;

    private HexLinkInvite(string pin, byte[] secret, int port, DateTimeOffset until, IPAddress[] addresses)
    {
        Pin = pin;
        Secret = secret;
        Port = port;
        Until = until;
        Addresses = addresses;
    }

    public string Pin { get; }
    private byte[] Secret { get; }
    public int Port { get; }
    public DateTimeOffset Until { get; }
    public IReadOnlyList<IPAddress> Addresses { get; }

    public static HexLinkInvite? Create(string pin, int port, DateTimeOffset until, IEnumerable<IPAddress> addresses)
    {
        IPAddress[] endpoints = [.. HexLinkAddresses.Preferred(addresses).Where(IsPortable).Take(MaxEndpoints)];
        return endpoints.Length == 0 || port is <= 0 or > 65535 ? null
            : new HexLinkInvite(pin, RandomNumberGenerator.GetBytes(32), port, until, endpoints);
    }

    public string Encode()
    {
        Span<byte> bytes = stackalloc byte[HeaderSize + MaxEndpoints * 17];
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

        return Prefix + Base64Url.EncodeToString(bytes[..offset]);
    }

    public static bool TryParse(string text, out HexLinkInvite? invite)
    {
        invite = null;
        if (!text.StartsWith(Prefix, StringComparison.Ordinal) || text.Length > Prefix.Length + 282)
            return false;

        Span<byte> bytes = stackalloc byte[HeaderSize + MaxEndpoints * 17];
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
        if (port == 0 || seconds is < 0 or > 253402300799 || count is < 1 or > MaxEndpoints)
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

        if (offset != written)
            return false;
        invite = new HexLinkInvite(Convert.ToHexString(bytes.Slice(10, 32)), bytes.Slice(42, 32).ToArray(),
            port, DateTimeOffset.FromUnixTimeSeconds(seconds), addresses);
        return true;
    }

    // IPv6 link-local zone IDs are scoped to the host's interfaces and cannot be passed to another machine.
    private static bool IsPortable(IPAddress address) => HexLinkAddresses.IsPrivate(address) && !address.IsIPv6LinkLocal;

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
