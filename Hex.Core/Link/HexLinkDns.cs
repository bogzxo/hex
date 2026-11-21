using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Hex.Core;

/// <summary>A question of a DNS message, a name and the type of record wanted.</summary>
/// <param name="Unicast">mDNS's QU bit, the asker would like the answer sent to it alone.</param>
internal sealed record HexLinkDnsQuestion(string Name, ushort Type, bool Unicast = false);

/// <summary>
/// A record of a DNS message, the few kinds Link's discovery says and reads: PTR and SRV (<see cref="Target"/>, SRV's
/// <see cref="Port"/>), TXT (<see cref="Texts"/>), A and AAAA (<see cref="Address"/>). Any other kind is kept as its
/// bytes and otherwise let be.
/// </summary>
/// <param name="Flush">mDNS's cache-flush bit, this is the whole of the records of its name and type.</param>
internal sealed record HexLinkDnsRecord(string Name, ushort Type, uint Ttl, bool Flush = false)
{
    public string Target { get; init; } = "";
    public ushort Port { get; init; }
    public IReadOnlyList<string> Texts { get; init; } = [];
    public IPAddress? Address { get; init; }
    public byte[] Data { get; init; } = [];
}

/// <summary>A DNS message as mDNS sends them, a query or a response.</summary>
internal sealed record HexLinkDnsMessage(ushort Id, bool Response, IReadOnlyList<HexLinkDnsQuestion> Questions, IReadOnlyList<HexLinkDnsRecord> Answers, IReadOnlyList<HexLinkDnsRecord> Additionals);

/// <summary>
/// DNS messages to bytes and back (RFC 1035, mDNS's bits from RFC 6762), as pure functions so they're tested without a
/// socket. Names are written whole, without compression, which every reader takes; names read follow compression
/// pointers, which every responder writes, at most a few dozen jumps so a packet pointing at itself can't hang the
/// reader. A packet that doesn't make sense reads as null rather than throwing.
/// </summary>
internal static class HexLinkDns
{
    public const ushort A = 1;
    public const ushort PTR = 12;
    public const ushort TXT = 16;
    public const ushort AAAA = 28;
    public const ushort SRV = 33;
    public const ushort ANY = 255;

    private const ushort IN = 1;
    private const ushort TOP_BIT = 0x8000;

    // A response from the one with the authority over its names, QR and AA
    private const ushort RESPONSE_FLAGS = 0x8400;

    private const int MOST_JUMPS = 32;
    private const int MOST_ITEMS = 256;

    /// <summary>The message as the bytes a UDP packet carries.</summary>
    public static byte[] Encode(HexLinkDnsMessage message)
    {
        var bytes = new List<byte>(512);
        U16(bytes, message.Id);
        U16(bytes, message.Response ? RESPONSE_FLAGS : (ushort)0);
        U16(bytes, (ushort)message.Questions.Count);
        U16(bytes, (ushort)message.Answers.Count);
        U16(bytes, 0);
        U16(bytes, (ushort)message.Additionals.Count);
        foreach (HexLinkDnsQuestion question in message.Questions)
        {
            Name(bytes, question.Name);
            U16(bytes, question.Type);
            U16(bytes, (ushort)(IN | (question.Unicast ? TOP_BIT : 0)));
        }

        foreach (HexLinkDnsRecord record in message.Answers.Concat(message.Additionals))
            Record(bytes, record);
        return [.. bytes];
    }

    /// <summary>The message a packet carries, null when it isn't a DNS message that reads to its end.</summary>
    public static HexLinkDnsMessage? Decode(ReadOnlySpan<byte> packet)
    {
        try
        {
            if (packet.Length < 12)
                return null;
            ushort id = BinaryPrimitives.ReadUInt16BigEndian(packet);
            ushort flags = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);
            int questions = BinaryPrimitives.ReadUInt16BigEndian(packet[4..]);
            int answers = BinaryPrimitives.ReadUInt16BigEndian(packet[6..]);
            int authorities = BinaryPrimitives.ReadUInt16BigEndian(packet[8..]);
            int additionals = BinaryPrimitives.ReadUInt16BigEndian(packet[10..]);
            if (questions + answers + authorities + additionals > MOST_ITEMS)
                return null;

            int at = 12;
            var asked = new List<HexLinkDnsQuestion>(questions);
            for (int i = 0; i < questions; i++)
            {
                string? name = ReadName(packet, ref at);
                if (name is null || at + 4 > packet.Length)
                    return null;
                ushort type = BinaryPrimitives.ReadUInt16BigEndian(packet[at..]);
                ushort klass = BinaryPrimitives.ReadUInt16BigEndian(packet[(at + 2)..]);
                at += 4;
                asked.Add(new HexLinkDnsQuestion(name, type, (klass & TOP_BIT) != 0));
            }

            var said = new List<HexLinkDnsRecord>(answers);
            var more = new List<HexLinkDnsRecord>(additionals);
            for (int i = 0; i < answers + authorities + additionals; i++)
            {
                HexLinkDnsRecord? record = ReadRecord(packet, ref at);
                if (record is null)
                    return null;
                if (i < answers)
                    said.Add(record);
                else if (i >= answers + authorities)
                    more.Add(record);
            }

            return new HexLinkDnsMessage(id, (flags & TOP_BIT) != 0, asked, said, more);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static void Record(List<byte> bytes, HexLinkDnsRecord record)
    {
        Name(bytes, record.Name);
        U16(bytes, record.Type);
        U16(bytes, (ushort)(IN | (record.Flush ? TOP_BIT : 0)));
        U32(bytes, record.Ttl);
        int lengthAt = bytes.Count;
        U16(bytes, 0);
        int start = bytes.Count;
        switch (record.Type)
        {
            case PTR:
                Name(bytes, record.Target);
                break;
            case SRV:
                U16(bytes, 0);
                U16(bytes, 0);
                U16(bytes, record.Port);
                Name(bytes, record.Target);
                break;
            case TXT:
                if (record.Texts.Count == 0)
                    bytes.Add(0);
                foreach (string text in record.Texts)
                {
                    byte[] said = Encoding.UTF8.GetBytes(text);
                    int length = Math.Min(said.Length, 255);
                    bytes.Add((byte)length);
                    bytes.AddRange(said.AsSpan(0, length));
                }

                break;
            case A or AAAA when record.Address is not null:
                bytes.AddRange(record.Address.GetAddressBytes());
                break;
            default:
                bytes.AddRange(record.Data);
                break;
        }

        int written = bytes.Count - start;
        bytes[lengthAt] = (byte)(written >> 8);
        bytes[lengthAt + 1] = (byte)written;
    }

    private static HexLinkDnsRecord? ReadRecord(ReadOnlySpan<byte> packet, ref int at)
    {
        string? name = ReadName(packet, ref at);
        if (name is null || at + 10 > packet.Length)
            return null;
        ushort type = BinaryPrimitives.ReadUInt16BigEndian(packet[at..]);
        ushort klass = BinaryPrimitives.ReadUInt16BigEndian(packet[(at + 2)..]);
        uint ttl = BinaryPrimitives.ReadUInt32BigEndian(packet[(at + 4)..]);
        int length = BinaryPrimitives.ReadUInt16BigEndian(packet[(at + 8)..]);
        at += 10;
        if (at + length > packet.Length)
            return null;

        int start = at, end = at + length;
        at = end;
        var record = new HexLinkDnsRecord(name, type, ttl, (klass & TOP_BIT) != 0) { Data = packet[start..end].ToArray() };
        switch (type)
        {
            case PTR:
            {
                int inside = start;
                return ReadName(packet, ref inside) is { } target ? record with { Target = target } : null;
            }
            case SRV:
            {
                if (length < 7)
                    return null;
                int inside = start + 6;
                return ReadName(packet, ref inside) is { } target ? record with { Port = BinaryPrimitives.ReadUInt16BigEndian(packet[(start + 4)..]), Target = target } : null;
            }
            case TXT:
            {
                var texts = new List<string>();
                for (int inside = start; inside < end;)
                {
                    int size = packet[inside++];
                    if (inside + size > end)
                        return null;
                    if (size > 0)
                        texts.Add(Encoding.UTF8.GetString(packet.Slice(inside, size)));
                    inside += size;
                }

                return record with { Texts = texts };
            }
            case A when length == 4:
            case AAAA when length == 16:
                return record with { Address = new IPAddress(packet[start..end]) };
            default:
                return record;
        }
    }

    /// <summary>A name read from where <paramref name="at"/> is, which is moved past it, following compression pointers.</summary>
    public static string? ReadName(ReadOnlySpan<byte> packet, ref int at)
    {
        var labels = new StringBuilder();
        int position = at, jumps = 0;
        bool jumped = false;
        while (true)
        {
            if (position >= packet.Length)
                return null;
            int length = packet[position];
            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= packet.Length || ++jumps > MOST_JUMPS)
                    return null;
                int pointer = ((length & 0x3F) << 8) | packet[position + 1];
                if (!jumped)
                    at = position + 2;
                jumped = true;
                position = pointer;
                continue;
            }

            if ((length & 0xC0) != 0)
                return null;

            if (length == 0)
            {
                if (!jumped)
                    at = position + 1;
                return labels.ToString();
            }

            if (position + 1 + length > packet.Length || labels.Length > 255)
                return null;
            if (labels.Length > 0)
                labels.Append('.');
            labels.Append(Encoding.UTF8.GetString(packet.Slice(position + 1, length)));
            position += 1 + length;
        }
    }

    private static void Name(List<byte> bytes, string name)
    {
        foreach (string label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] said = Encoding.UTF8.GetBytes(label);
            int length = Math.Min(said.Length, 63);
            bytes.Add((byte)length);
            bytes.AddRange(said.AsSpan(0, length));
        }

        bytes.Add(0);
    }

    private static void U16(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }

    private static void U32(List<byte> bytes, uint value)
    {
        U16(bytes, (ushort)(value >> 16));
        U16(bytes, (ushort)value);
    }

    /// <summary>Whether two names are the same, DNS names being the same whatever their case.</summary>
    public static bool Same(string one, string other) => string.Equals(one.TrimEnd('.'), other.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether an address is IPv4, the only kind the responder says (A records).</summary>
    public static bool IsFour(IPAddress address) => address.AddressFamily == AddressFamily.InterNetwork;
}
