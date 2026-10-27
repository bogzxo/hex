using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core.Notebooks;

/// <summary>
/// One message of the Jupyter messaging protocol (version 5.3), its header (who sent it and what it is), the header of the
/// message it answers, its metadata and its content, and any binary buffers after them. On the wire it's the frames
/// "&lt;IDS|MSG&gt;", the HMAC-SHA256 of the four JSON parts in hex, then the four JSON parts, then the buffers, with the
/// identities of whoever it's for in front on a kernel's end of it.
/// </summary>
internal sealed class JupyterMessage
{
    public const string PROTOCOL = "5.3";
    private static readonly byte[] DELIMITER = "<IDS|MSG>"u8.ToArray();

    public required JsonObject Header { get; init; }
    public JsonObject ParentHeader { get; init; } = [];
    public JsonObject Metadata { get; init; } = [];
    public JsonObject Content { get; init; } = [];
    public List<byte[]> Buffers { get; init; } = [];

    /// <summary>What kind of message it is, execute_request, stream, status and so on.</summary>
    public string Type => Header["msg_type"]?.GetValue<string>() ?? string.Empty;

    /// <summary>Its own id, which the messages answering it carry as their parent's.</summary>
    public string Id => Header["msg_id"]?.GetValue<string>() ?? string.Empty;

    /// <summary>The id of the message this one answers, empty for none.</summary>
    public string ParentId => ParentHeader["msg_id"]?.GetValue<string>() ?? string.Empty;

    /// <summary>A new message of a session, its header filled in, answering nothing.</summary>
    public static JupyterMessage Make(string session, string type, JsonObject content) => new()
    {
        Header = new JsonObject
        {
            ["msg_id"] = Guid.NewGuid().ToString("N"),
            ["session"] = session,
            ["username"] = Environment.UserName,
            ["date"] = DateTimeOffset.UtcNow.ToString("o"),
            ["msg_type"] = type,
            ["version"] = PROTOCOL
        },
        Content = content
    };

    /// <summary>The frames to send it as from a client, signed with the connection's key (none when it's empty).</summary>
    public List<byte[]> ToFrames(byte[] key)
    {
        byte[] header = Json(Header), parent = Json(ParentHeader), metadata = Json(Metadata), content = Json(Content);
        var frames = new List<byte[]> { DELIMITER, Encoding.ASCII.GetBytes(Sign(key, header, parent, metadata, content)), header, parent, metadata, content };
        frames.AddRange(Buffers);
        return frames;
    }

    /// <summary>
    /// A message out of the frames it came as, null when they aren't one or its signature isn't the key's (a message
    /// not from the kernel this connection was made with).
    /// </summary>
    public static JupyterMessage? FromFrames(IReadOnlyList<byte[]> frames, byte[] key)
    {
        int at = -1;
        for (int i = 0; i < frames.Count; i++)
        {
            if (frames[i].AsSpan().SequenceEqual(DELIMITER))
            {
                at = i;
                break;
            }
        }

        if (at < 0 || frames.Count < at + 6)
            return null;

        byte[] header = frames[at + 2], parent = frames[at + 3], metadata = frames[at + 4], content = frames[at + 5];
        if (key.Length > 0)
        {
            string said = Encoding.ASCII.GetString(frames[at + 1]);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(said), Encoding.ASCII.GetBytes(Sign(key, header, parent, metadata, content))))
                return null;
        }

        try
        {
            return new JupyterMessage
            {
                Header = JsonNode.Parse(header) as JsonObject ?? [],
                ParentHeader = JsonNode.Parse(parent) as JsonObject ?? [],
                Metadata = JsonNode.Parse(metadata) as JsonObject ?? [],
                Content = JsonNode.Parse(content) as JsonObject ?? [],
                Buffers = [.. frames.Skip(at + 6)]
            };
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static byte[] Json(JsonObject part) => Encoding.UTF8.GetBytes(part.ToJsonString());

    // The signature, HMAC-SHA256 of the four parts one after the other, in lower case hex. Empty without a key
    private static string Sign(byte[] key, params byte[][] parts)
    {
        if (key.Length == 0)
            return string.Empty;

        using var hmac = new HMACSHA256(key);
        foreach (byte[] part in parts)
            hmac.TransformBlock(part, 0, part.Length, null, 0);
        hmac.TransformFinalBlock([], 0, 0);
        return Convert.ToHexStringLower(hmac.Hash!);
    }
}
