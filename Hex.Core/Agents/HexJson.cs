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
    public static string Short(IRuntimeValue value) => value switch
    {
        StringValue text => text.Value,
        NullValue => string.Empty,
        _ => FromHidl(value)?.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? string.Empty
    };
}
