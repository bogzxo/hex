using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// What a bridge keeps of the conversation going through it, so that when the Hex at the other end goes (restarted into
/// a build of itself) the client never notices more than a call that has to be made again. It keeps the client's
/// handshake to say again to the next Hex, the calls still waiting for an answer to fail when the line drops, and what
/// the client said while there was no Hex to hand to the next one. Lines are the protocol's, a JSON message each.
/// </summary>
internal sealed class McpBridgeSession
{
    // The id the handshake is said again under, whose answer is the bridge's own and never reaches the client
    internal const string REPLAY_ID = "hex-bridge-again";

    private readonly Lock gate = new();
    private string? initialize, initialized;
    private readonly Dictionary<string, string> waiting = [];
    private readonly Queue<string> held = new();

    /// <summary>
    /// A line from the client, noted on its way to Hex. The handshake is kept, a request waits for its answer. False when
    /// there's no Hex to give it to now, it's held for the next one then.
    /// </summary>
    public void FromClient(string line, bool connected)
    {
        Head head = HeadOf(line);
        lock (gate)
        {
            string? method = head.Method;
            if (method == "initialize")
                initialize = line;
            else if (method == "notifications/initialized")
                initialized = line;

            if (head.Id is { } id && method is not null)
                waiting[id] = method;

            if (!connected)
                held.Enqueue(line);
        }
    }

    /// <summary>
    /// A line from Hex, the line to give the client, or null for the answer to the handshake said again, which is the
    /// bridge's. An answer lets go of the request it answers.
    /// </summary>
    public string? FromHex(string line)
    {
        Head head = HeadOf(line);
        if (head.Id is not { } key || head.HasMethod)
            return line;

        if (head.Replay)
            return null;
        lock (gate)
            waiting.Remove(key);
        return line;
    }

    /// <summary>
    /// The line to Hex dropped. An error for every call that was waiting for an answer, which will never come now, said
    /// so the client can make it again once Hex is back. Those are let go of.
    /// </summary>
    public List<string> Dropped()
    {
        lock (gate)
        {
            var said = new List<string>();
            foreach (var (id, method) in waiting)
            {
                // The handshake said again is the bridge's, and a request held for the next Hex is still to be sent
                if (id == REPLAY_ID || held.Any(line => HeadOf(line).Id == id))
                    continue;
                var error = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = JsonNode.Parse(id),
                    ["error"] = new JsonObject
                    {
                        ["code"] = -32000,
                        ["message"] = $"Hex restarted while {method} was going on, make the call again, the tools are back."
                    }
                };
                said.Add(((JsonNode)error).ToJsonString());
            }

            waiting.Clear();
            return said;
        }
    }

    /// <summary>
    /// What the next Hex is told before anything else, the client's handshake under the bridge's own id and the
    /// notification that it's done, then what the client said while there was none. Empty when the client never shook
    /// hands, there's nothing to say again then.
    /// </summary>
    public List<string> Replay()
    {
        lock (gate)
        {
            var said = new List<string>();
            if (initialize is not null && Parse(initialize) is { } asked)
            {
                asked["id"] = REPLAY_ID;
                said.Add(((JsonNode)asked).ToJsonString());
                if (initialized is not null)
                    said.Add(initialized);
            }

            while (held.Count > 0)
            {
                string line = held.Dequeue();
                if (line != initialize && line != initialized)
                    said.Add(line);
            }

            return said;
        }
    }

    /// <summary>What the client said while there was no Hex and hasn't been passed on yet, in the order it said it.</summary>
    public List<string> Held()
    {
        lock (gate)
        {
            var said = new List<string>(held);
            held.Clear();
            return said;
        }
    }

    /// <summary>What the client is told once the line's back, that the tools may be other ones in the new build.</summary>
    public static string ToolsChanged() => """{"jsonrpc":"2.0","method":"notifications/tools/list_changed"}""";

    /// <summary>
    /// What the top of a line says: its id as JSON (null when it has none, or a null one), its method when that's a
    /// text, whether it has a method of any sort that isn't null, and whether the id is the handshake said again.
    /// Nothing for a line that isn't a JSON object.
    /// </summary>
    private readonly record struct Head(string? Id, string? Method, bool HasMethod, bool Replay);

    /// <summary>
    /// The top of a line read without making the message: the id and the method are found among the properties of the
    /// object and everything else is stepped over. A tool's answer can be a picture of a few megabytes, and every line
    /// both ways was made into a whole tree of nodes for these two. Still read to the end, a line that isn't JSON has
    /// neither, as it had when it was parsed whole.
    /// </summary>
    private static Head HeadOf(string line)
    {
        byte[] bytes = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetByteCount(line));
        try
        {
            var reader = new Utf8JsonReader(bytes.AsSpan(0, Encoding.UTF8.GetBytes(line, bytes)));
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return default;

            string? id = null, method = null;
            bool replay = false, hasMethod = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isId = reader.ValueTextEquals("id"u8), isMethod = reader.ValueTextEquals("method"u8);
                reader.Read();
                if (isId)
                {
                    replay = reader.TokenType == JsonTokenType.String && reader.ValueTextEquals(REPLAY_ID);
                    id = reader.TokenType == JsonTokenType.Null ? null : IdOf(ref reader);
                }
                else if (isMethod)
                {
                    hasMethod = reader.TokenType != JsonTokenType.Null;
                    method = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    reader.Skip();
                }
                else
                {
                    reader.Skip();
                }
            }

            // Nothing after the object, the way a parse of the whole line has it
            return reader.Read() ? default : new Head(id, method, hasMethod, replay);
        }
        catch (JsonException)
        {
            return default;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    // An id said the same way whether it came as a number or a text, its JSON written again the way a node writes it
    private static string IdOf(ref Utf8JsonReader reader)
    {
        var written = new ArrayBufferWriter<byte>(32);
        using (var writer = new Utf8JsonWriter(written))
            JsonElement.ParseValue(ref reader).WriteTo(writer);
        return Encoding.UTF8.GetString(written.WrittenSpan);
    }

    private static JsonObject? Parse(string line)
    {
        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
