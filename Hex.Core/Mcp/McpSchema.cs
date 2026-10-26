using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// JSON Schema for a tool's arguments, the little of it tools need, written short. An object of properties, each a
/// string, a number, a whole number, a flag or one of a few strings, with a line about it, and which have to be there.
/// The arguments a call brings are checked against it before the tool sees them (<see cref="Check"/>).
/// </summary>
internal static class McpSchema
{
    public static JsonObject String(string description) => new() { ["type"] = "string", ["description"] = description };

    public static JsonObject Number(string description) => new() { ["type"] = "number", ["description"] = description };

    public static JsonObject Integer(string description) => new() { ["type"] = "integer", ["description"] = description };

    public static JsonObject Flag(string description) => new() { ["type"] = "boolean", ["description"] = description };

    public static JsonObject OneOf(string description, params string[] choices) =>
        new() { ["type"] = "string", ["description"] = description, ["enum"] = new JsonArray([.. choices.Select(choice => (JsonNode)choice)]) };

    /// <summary>Anything at all, for a setting's value which is whatever kind the setting is.</summary>
    public static JsonObject Any(string description) => new() { ["description"] = description };

    /// <summary>An object of properties, the names in <paramref name="required"/> having to be there.</summary>
    public static JsonObject Object(IEnumerable<(string Name, JsonObject Schema)> properties, params string[] required)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties)
            props[name] = schema;

        var made = new JsonObject { ["type"] = "object", ["properties"] = props, ["additionalProperties"] = false };
        if (required.Length > 0)
            made["required"] = new JsonArray([.. required.Select(name => (JsonNode)name)]);
        return made;
    }

    /// <summary>An object that takes nothing.</summary>
    public static JsonObject Empty() => Object([]);

    /// <summary>
    /// What's wrong with a call's arguments by a schema made here, null when nothing is. One that's missing, one that
    /// isn't a property, and one of the wrong kind, said the way a model can put right.
    /// </summary>
    public static string? Check(JsonObject schema, JsonObject arguments)
    {
        JsonObject properties = schema["properties"] as JsonObject ?? [];
        if (schema["required"] is JsonArray required)
        {
            foreach (JsonNode? name in required)
            {
                string wanted = name?.GetValue<string>() ?? string.Empty;
                if (arguments[wanted] is null)
                    return $"'{wanted}' is required.";
            }
        }

        foreach (var (name, value) in arguments)
        {
            if (properties[name] is not JsonObject property)
                return $"'{name}' isn't an argument of this tool, it takes {(properties.Count == 0 ? "none" : string.Join(", ", properties.Select(each => each.Key)))}.";

            if (value is null || property["type"]?.GetValue<string>() is not { } type)
                continue;

            JsonValueKind kind = value.GetValueKind();
            bool fits = type switch
            {
                "string" => kind == JsonValueKind.String,
                "number" => kind == JsonValueKind.Number,
                "integer" => kind == JsonValueKind.Number && value.AsValue().TryGetValue(out double whole) && whole == Math.Floor(whole),
                "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
                "object" => kind == JsonValueKind.Object,
                "array" => kind == JsonValueKind.Array,
                _ => true
            };

            if (!fits)
                return $"'{name}' should be {(type == "integer" ? "a whole number" : "a " + type)}.";

            if (property["enum"] is JsonArray choices && !choices.Any(choice => choice?.GetValue<string>() == value.GetValue<string>()))
                return $"'{name}' should be one of {string.Join(", ", choices.Select(choice => choice?.GetValue<string>()))}.";
        }

        return null;
    }
}

/// <summary>A tool's arguments read with what each comes as when it isn't there.</summary>
internal static class McpArguments
{
    public static string? String(this JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    public static double? Number(this JsonObject arguments, string name) =>
        arguments[name] is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue(out double number) ? number : null;

    public static int? Integer(this JsonObject arguments, string name) => arguments.Number(name) is { } number ? (int)Math.Clamp(number, int.MinValue, int.MaxValue) : null;

    public static bool Flag(this JsonObject arguments, string name, bool fallback = false) =>
        arguments[name] is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? value.GetValue<bool>() : fallback;
}
