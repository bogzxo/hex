using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Hex.Core;

/// <summary>
/// The application id of Hex's Discord application, the one place it's said. Empty until somebody makes the application
/// at https://discord.com/developers/applications (the name there is what Discord shows after "Playing", call it Hex)
/// and pastes its Application ID here. Under Rich Presence, Art Assets upload the pictures the presence names:
/// <c>hex</c> (the small badge and the fallback large picture) and one per language key of <see cref="DiscordLanguages"/>
/// (cs, ts, py, md, ...). HEX_DISCORD_APP_ID in the environment replaces it, for trying a different application.
/// </summary>
internal static class HexDiscord
{
    /// <summary>The application id of Hex's application on Discord's developer portal.</summary>
    public const string ApplicationId = "1574400359294832660";

    /// <summary>Where the "Get Hex" button goes.</summary>
    public const string Website = "https://bogzxo.github.io/hex/";

    /// <summary>The id in force, the environment's or the constant, empty when Discord isn't set up.</summary>
    public static string EffectiveApplicationId(Func<string, string?>? environment = null)
    {
        string? fromEnvironment = (environment ?? System.Environment.GetEnvironmentVariable)("HEX_DISCORD_APP_ID");
        return fromEnvironment is { Length: > 0 } ? fromEnvironment.Trim() : ApplicationId;
    }

    /// <summary>Whether there's an application id to connect with.</summary>
    public static bool Configured => EffectiveApplicationId().Length > 0;
}

/// <summary>One frame of Discord's IPC, int32 LE opcode, int32 LE length, then UTF-8 JSON.</summary>
internal static class DiscordFrame
{
    public const int HANDSHAKE = 0, FRAME = 1, CLOSE = 2, PING = 3, PONG = 4;

    /// <summary>Discord holds a frame to 64 KB, anything bigger on the wire is garbage.</summary>
    public const int MAX_PAYLOAD = 64 * 1024;

    public static byte[] Encode(int opcode, ReadOnlySpan<byte> json)
    {
        byte[] frame = new byte[8 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), json.Length);
        json.CopyTo(frame.AsSpan(8));
        return frame;
    }

    public static byte[] Encode(int opcode, string json) => Encode(opcode, Encoding.UTF8.GetBytes(json));

    /// <summary>The next frame, null when the stream ended (or sent something that is no frame).</summary>
    public static async ValueTask<(int Opcode, byte[] Payload)?> ReadAsync(Stream stream, CancellationToken cancel)
    {
        byte[] header = new byte[8];
        if (!await FillAsync(stream, header, cancel).ConfigureAwait(false))
            return null;

        int opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length < 0 || length > MAX_PAYLOAD)
            return null;

        byte[] payload = new byte[length];
        return await FillAsync(stream, payload, cancel).ConfigureAwait(false) ? (opcode, payload) : null;
    }

    private static async ValueTask<bool> FillAsync(Stream stream, byte[] into, CancellationToken cancel)
    {
        int read = 0;
        while (read < into.Length)
        {
            int got = await stream.ReadAsync(into.AsMemory(read), cancel).ConfigureAwait(false);
            if (got == 0)
                return false;
            read += got;
        }

        return true;
    }
}

/// <summary>What Discord is told, made by <see cref="DiscordActivityBuilder"/>. Plain values, no paths.</summary>
internal sealed record DiscordActivity(string Details, string? State, long? StartUnixSeconds, string? LargeImage, string? LargeText,
    string? SmallImage, string? SmallText, string? ButtonLabel, string? ButtonUrl);

/// <summary>The JSON Discord's IPC is sent, written by hand with Utf8JsonWriter so none of it needs reflection.</summary>
internal static class DiscordJson
{
    public static byte[] Handshake(string clientId)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("v", 1);
            json.WriteString("client_id", clientId);
            json.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>SET_ACTIVITY, with a null activity to clear the presence.</summary>
    public static byte[] SetActivity(int pid, DiscordActivity? activity, string nonce)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("cmd", "SET_ACTIVITY");
            json.WriteStartObject("args");
            json.WriteNumber("pid", pid);
            if (activity is null)
            {
                json.WriteNull("activity");
            }
            else
            {
                json.WriteStartObject("activity");
                json.WriteString("details", activity.Details);
                if (activity.State is { Length: > 0 })
                    json.WriteString("state", activity.State);
                if (activity.StartUnixSeconds is { } start)
                {
                    json.WriteStartObject("timestamps");
                    json.WriteNumber("start", start);
                    json.WriteEndObject();
                }

                if (activity.LargeImage is { Length: > 0 } || activity.SmallImage is { Length: > 0 })
                {
                    json.WriteStartObject("assets");
                    if (activity.LargeImage is { Length: > 0 })
                    {
                        json.WriteString("large_image", activity.LargeImage);
                        if (activity.LargeText is { Length: > 0 })
                            json.WriteString("large_text", activity.LargeText);
                    }

                    if (activity.SmallImage is { Length: > 0 })
                    {
                        json.WriteString("small_image", activity.SmallImage);
                        if (activity.SmallText is { Length: > 0 })
                            json.WriteString("small_text", activity.SmallText);
                    }

                    json.WriteEndObject();
                }

                if (activity.ButtonLabel is { Length: > 0 } && activity.ButtonUrl is { Length: > 0 })
                {
                    json.WriteStartArray("buttons");
                    json.WriteStartObject();
                    json.WriteString("label", activity.ButtonLabel);
                    json.WriteString("url", activity.ButtonUrl);
                    json.WriteEndObject();
                    json.WriteEndArray();
                }

                json.WriteEndObject();
            }

            json.WriteEndObject();
            json.WriteString("nonce", nonce);
            json.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>The event of a dispatch ("READY", "ERROR"), null for what has none or is no JSON.</summary>
    public static string? EventOf(ReadOnlySpan<byte> payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload.ToArray());
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("evt", out JsonElement evt)
                && evt.ValueKind == JsonValueKind.String ? evt.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
