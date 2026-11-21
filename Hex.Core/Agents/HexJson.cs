using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Hex.Core;

/// <summary>
/// JSON as HIDL values and back, since HIDL has no JSON of its own and every agent says what it's doing in it. Objects are
/// objects, arrays lists, strings texts, numbers numbers (HIDL's are floats, so a big id is better read as a text), true
/// and false flags and null null.
/// </summary>
internal static class HexJson
{
    /// <summary>A line of JSON as a HIDL value, null when it isn't JSON at all.</summary>
    public static IRuntimeValue? Parse(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed[0] is not ('{' or '['))
            return null;

        try
        {
            return ToHidl(JsonNode.Parse(trimmed));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static IRuntimeValue ToHidl(JsonNode? node) => node switch
    {
        null => Values.Null,
        JsonObject made => Natives.Object([.. made.Select(pair => (pair.Key, ToHidl(pair.Value)))]),
        JsonArray list => new ListValue([.. list.Select(ToHidl)]),
        JsonValue value => value.GetValueKind() switch
        {
            JsonValueKind.String => new StringValue(value.GetValue<string>()),
            JsonValueKind.Number => new NumberValue((float)value.GetValue<double>()),
            JsonValueKind.True => Values.Bool(true),
            JsonValueKind.False => Values.Bool(false),
            _ => Values.Null
        },
        _ => Values.Null
    };

    /// <summary>A HIDL value as JSON, anything that isn't data as its text.</summary>
    public static JsonNode? FromHidl(IRuntimeValue value) => value switch
    {
        ObjectValue made => new JsonObject(made.Properties.Select(pair => KeyValuePair.Create(pair.Key, FromHidl(pair.Value)))),
        ListValue list => new JsonArray([.. list.Items.Select(FromHidl)]),
        StringValue text => JsonValue.Create(text.Value),
        NumberValue number => number.Value == MathF.Floor(number.Value) && MathF.Abs(number.Value) < 1e7f ? JsonValue.Create((long)number.Value) : JsonValue.Create(number.Value),
        BooleanValue flag => JsonValue.Create(flag.Value),
        NullValue => null,
        _ => JsonValue.Create(value.ToString())
    };

    /// <summary>A HIDL value written out short for a row of the panel, a text as it is and anything else as compact JSON.</summary>
    public static string Short(IRuntimeValue value)
    {
        switch (value)
        {
            case StringValue text:
                return text.Value;
            case NullValue:
                return string.Empty;
        }

        // Written straight out of the HIDL value, as FromHidl's JSON would have been, into a buffer kept for it. Every tool
        // an agent called and what it said back came here, and a tree of JSON made of each to be written once was most of
        // what the agent's lines cost
        shortBuffer ??= new ArrayBufferWriter<byte>(1024);
        if (shortWriter is null)
            shortWriter = new Utf8JsonWriter(shortBuffer, ShortOptions);
        else
            shortWriter.Reset(shortBuffer);

        try
        {
            Write(shortWriter, value);
            shortWriter.Flush();
            return Encoding.UTF8.GetString(shortBuffer.WrittenSpan);
        }
        finally
        {
            // The buffer let go of after a big value rather than held onto
            if (shortBuffer.Capacity > 64 * 1024)
                shortBuffer = null;
            else
                shortBuffer.ResetWrittenCount();
        }
    }

    // As ToJsonString wrote with an encoder that leaves quotes and the like as they are, its depth the serializer's own
    private static readonly JsonWriterOptions ShortOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 64 };

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? shortBuffer;

    [ThreadStatic]
    private static Utf8JsonWriter? shortWriter;

    // A HIDL value as JSON, the same as FromHidl's node written out, as deep as the writer lets it go
    private static void Write(Utf8JsonWriter writer, IRuntimeValue value)
    {
        switch (value)
        {
            case ObjectValue made:
                writer.WriteStartObject();
                foreach (KeyValuePair<string, IRuntimeValue> pair in made.Properties)
                {
                    writer.WritePropertyName(pair.Key);
                    Write(writer, pair.Value);
                }
                writer.WriteEndObject();
                break;
            case ListValue list:
                writer.WriteStartArray();
                foreach (IRuntimeValue item in list.Items)
                    Write(writer, item);
                writer.WriteEndArray();
                break;
            case StringValue text:
                writer.WriteStringValue(text.Value);
                break;
            case NumberValue number when number.Value == MathF.Floor(number.Value) && MathF.Abs(number.Value) < 1e7f:
                writer.WriteNumberValue((long)number.Value);
                break;
            case NumberValue number:
                writer.WriteNumberValue(number.Value);
                break;
            case BooleanValue flag:
                writer.WriteBooleanValue(flag.Value);
                break;
            case NullValue:
                writer.WriteNullValue();
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }
}
