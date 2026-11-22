using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hex.Core;

internal enum HexLinkBulkSupport
{
    None,
    Raw,
    Zstandard
}

internal enum HexLinkFrameKind : byte
{
    Data,
    Compressed,
    Credit,
    Abort
}

/// <summary>A binary frame starts with a zero byte at a message boundary. All integers are little-endian.</summary>
internal readonly record struct HexLinkFrameHeader(HexLinkFrameKind Kind, long StreamId, uint Sequence, int WireLength, int Length)
{
    public const byte Marker = 0;
    public const int Size = 22;
    public const int MaximumPayload = 256 * 1024;
    public const int Window = 8;

    public void Write(Span<byte> header)
    {
        header[0] = Marker;
        header[1] = (byte)Kind;
        BinaryPrimitives.WriteInt64LittleEndian(header[2..], StreamId);
        BinaryPrimitives.WriteUInt32LittleEndian(header[10..], Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(header[14..], WireLength);
        BinaryPrimitives.WriteInt32LittleEndian(header[18..], Length);
    }

    public static HexLinkFrameHeader Read(ReadOnlySpan<byte> header, HexLinkBulkSupport support)
    {
        var frame = new HexLinkFrameHeader((HexLinkFrameKind)header[1],
            BinaryPrimitives.ReadInt64LittleEndian(header[2..]), BinaryPrimitives.ReadUInt32LittleEndian(header[10..]),
            BinaryPrimitives.ReadInt32LittleEndian(header[14..]), BinaryPrimitives.ReadInt32LittleEndian(header[18..]));
        bool valid = frame.Kind switch
        {
            HexLinkFrameKind.Data => frame.Length == frame.WireLength,
            HexLinkFrameKind.Compressed => support == HexLinkBulkSupport.Zstandard && frame.WireLength < frame.Length,
            HexLinkFrameKind.Credit => frame.WireLength == 0 && frame.Length == 0,
            HexLinkFrameKind.Abort => frame.WireLength == 0 && frame.Length == 0 && frame.Sequence == 0,
            _ => false
        };
        bool payload = frame.Kind is HexLinkFrameKind.Data or HexLinkFrameKind.Compressed;
        if (support == HexLinkBulkSupport.None || header[0] != Marker || !valid || frame.StreamId <= 0 ||
            (payload && (frame.WireLength <= 0 || frame.Length > MaximumPayload)))
            throw new InvalidDataException("Invalid Link bulk frame header.");
        return frame;
    }
}

/// <summary>
/// What the other machine sent, read field by field. Everything that comes over a link is data from somebody else's
/// program: a field may be missing, of the wrong kind or absurdly long, and none of that may throw out of the reader or
/// make a string bigger than what it's for. Each of these gives null (or the default asked for) rather than trust it.
/// </summary>
internal static class HexLinkData
{
    /// <summary>A string field, null when it isn't one, cut to <paramref name="most"/> characters when it's longer.</summary>
    public static string? String(JsonObject? from, string name, int most)
    {
        if (from?[name] is not JsonValue value || value.GetValueKind() != JsonValueKind.String || !value.TryGetValue(out string? said))
            return null;
        return said.Length > most ? said[..most] : said;
    }

    /// <summary>A whole number field, null when it isn't one or doesn't fit.</summary>
    public static long? Long(JsonObject? from, string name)
    {
        if (from?[name] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
            return null;
        if (value.TryGetValue(out long whole))
            return whole;
        return value.TryGetValue(out double real) && double.IsFinite(real) && real is >= long.MinValue and <= long.MaxValue && Math.Floor(real) == real ? (long)real : null;
    }

    /// <summary>A whole number field that fits an int, null otherwise.</summary>
    public static int? Int(JsonObject? from, string name) => Long(from, name) is { } whole && whole is >= int.MinValue and <= int.MaxValue ? (int)whole : null;

    /// <summary>A number field, null when it isn't a finite one.</summary>
    public static double? Double(JsonObject? from, string name) =>
        from?[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue(out double real) && double.IsFinite(real) ? real : null;

    /// <summary>A true or false field, null when it's neither.</summary>
    public static bool? Bool(JsonObject? from, string name) =>
        from?[name] is JsonValue value ? value.GetValueKind() switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    /// <summary>An object field, null when it isn't one.</summary>
    public static JsonObject? Object(JsonObject? from, string name) => from?[name] as JsonObject;

    /// <summary>The objects of an array field, at most <paramref name="most"/> of them, what isn't an object passed over.</summary>
    public static IEnumerable<JsonObject> Objects(JsonObject? from, string name, int most) =>
        from?[name] is JsonArray array ? array.Take(most).OfType<JsonObject>() : [];

    /// <summary>The strings of an array field, at most <paramref name="most"/> of them each cut to <paramref name="length"/>.</summary>
    public static List<string> Strings(JsonObject? from, string name, int most, int length)
    {
        var said = new List<string>();
        if (from?[name] is not JsonArray array)
            return said;
        foreach (JsonNode? each in array)
        {
            if (said.Count >= most)
                break;
            if (each is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.TryGetValue(out string? text))
                said.Add(text.Length > length ? text[..length] : text);
        }

        return said;
    }

    /// <summary>A string a request has to have, or the request is refused as invalid.</summary>
    public static string Need(JsonObject from, string name, int most) =>
        String(from, name, most) ?? throw HexLinkException.Invalid($"It needs {name}, a string.");

    /// <summary>A whole number a request has to have, or the request is refused as invalid.</summary>
    public static int NeedInt(JsonObject from, string name) =>
        Int(from, name) ?? throw HexLinkException.Invalid($"It needs {name}, a whole number.");

    /// <summary>Rights as the wire says them, a word each, view, steer and edit.</summary>
    public static JsonArray Words(HexLinkRights rights)
    {
        var words = new JsonArray();
        if (rights.HasFlag(HexLinkRights.View))
            words.Add((JsonNode)"view");
        if (rights.HasFlag(HexLinkRights.Steer))
            words.Add((JsonNode)"steer");
        if (rights.HasFlag(HexLinkRights.Edit))
            words.Add((JsonNode)"edit");
        if (rights.HasFlag(HexLinkRights.Predict))
            words.Add((JsonNode)"predict");
        if (rights.HasFlag(HexLinkRights.Manage))
            words.Add((JsonNode)"manage");
        return words;
    }

    /// <summary>Rights read back from their words, the ones it doesn't know passed over.</summary>
    public static HexLinkRights RightsOf(JsonObject? from, string name)
    {
        HexLinkRights rights = HexLinkRights.None;
        foreach (string word in Strings(from, name, 8, 16))
        {
            rights |= word switch
            {
                "view" => HexLinkRights.View,
                "steer" => HexLinkRights.Steer,
                "edit" => HexLinkRights.Edit,
                "predict" => HexLinkRights.Predict,
                "manage" => HexLinkRights.Manage,
                _ => HexLinkRights.None
            };
        }

        return rights;
    }

    /// <summary>A right's word, for an error's needs and the audit log.</summary>
    public static string Word(HexLinkRights right) => right switch
    {
        HexLinkRights.View => "view",
        HexLinkRights.Steer => "steer",
        HexLinkRights.Edit => "edit",
        HexLinkRights.Predict => "predict",
        HexLinkRights.Manage => "manage",
        _ => right.ToString().ToLowerInvariant()
    };

    /// <summary>A device as the wire says it.</summary>
    public static JsonObject DeviceJson(HexLinkDevice device) => new()
    {
        ["id"] = device.Id,
        ["name"] = device.Name,
        ["os"] = device.Os,
        ["arch"] = device.Arch,
        ["version"] = device.Version
    };

    /// <summary>
    /// A device as the other end says it is, its name and the rest for showing. Its id is only ever the one the
    /// connection proved, <paramref name="proved"/>, whatever it claims.
    /// </summary>
    public static HexLinkDevice DeviceOf(JsonObject? said, HexLinkDevice proved) =>
        said is null ? proved : proved with
        {
            Name = String(said, "name", 80) is { Length: > 0 } name ? name : proved.Name,
            Os = String(said, "os", 40) ?? proved.Os,
            Arch = String(said, "arch", 20) ?? proved.Arch,
            Version = String(said, "version", 40) ?? proved.Version
        };
}
