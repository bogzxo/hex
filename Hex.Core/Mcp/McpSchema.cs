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

    /// <summary>A whole number or null, for a field a typed client clears by sending null.</summary>
    public static JsonObject IntegerOrNull(string description) =>
        new() { ["type"] = new JsonArray("integer", "null"), ["description"] = description };

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

    // The other names a model gives an argument (the ones of its own tools, Read's file_path, or a near guess), each
    // with the names it may mean in the order tried, the first a tool takes and wasn't given being the one
    private static readonly Dictionary<string, string[]> ALIASES = new(StringComparer.OrdinalIgnoreCase)
    {
        ["file_path"] = ["path", "paths"],
        ["filepath"] = ["path", "paths"],
        ["file"] = ["path", "paths"],
        ["filename"] = ["path", "paths"],
        ["file_name"] = ["path", "paths"],
        ["target_file"] = ["path", "paths"],
        ["uri"] = ["path", "paths"],
        ["path"] = ["paths", "file", "folder"],
        ["files"] = ["paths", "path"],
        ["name"] = ["symbol", "query"],
        ["symbol"] = ["name", "query"],
        ["symbol_name"] = ["symbol", "name", "query"],
        ["identifier"] = ["symbol", "name", "query"],
        ["function"] = ["symbol", "name"],
        ["method"] = ["symbol", "name"],
        ["query"] = ["symbol", "pattern", "name"],
        ["q"] = ["query", "symbol", "pattern"],
        ["search"] = ["query", "pattern", "symbol"],
        ["pattern"] = ["query"],
        ["text"] = ["query", "new_text"],
        ["line_number"] = ["line"],
        ["start"] = ["start_line", "line"],
        ["end"] = ["end_line"],
        ["max"] = ["max_chars", "most"],
        ["limit"] = ["max_chars", "most"],
        ["directory"] = ["folder", "under", "path"],
        ["dir"] = ["folder", "under", "path"],
        ["old_text"] = ["old_string"],
        ["content"] = ["new_text", "text"],
        ["code"] = ["new_text"]
    };

    /// <summary>
    /// A call's arguments with the names a tool doesn't take put to the ones it does (file_path to path, name to symbol,
    /// camelCase to snake_case), and a value of the near kind made the kind it should be (a list for one string, "3" for
    /// a whole number, "true" for a flag). What still doesn't fit is left for <see cref="Check"/> to say. The same object
    /// when nothing was changed.
    /// </summary>
    public static JsonObject Normalize(JsonObject schema, JsonObject arguments)
    {
        if (schema["properties"] is not JsonObject properties)
            return arguments;

        JsonObject? made = null;
        JsonObject Made() => made ??= (JsonObject)arguments.DeepClone();
        foreach (var (given, _) in arguments.ToList())
        {
            if (properties.ContainsKey(given))
                continue;

            string? to = null;
            string snake = Snake(given);
            if (properties.ContainsKey(snake) && !arguments.ContainsKey(snake))
                to = snake;
            else if ((ALIASES.TryGetValue(given, out string[]? meant) || ALIASES.TryGetValue(snake, out meant))
                && meant.FirstOrDefault(each => properties.ContainsKey(each) && !arguments.ContainsKey(each)) is { } found)
                to = found;
            if (to is null)
                continue;

            JsonObject into = Made();
            JsonNode? value = into[given];
            into.Remove(given);
            into[to] = value;
        }

        // Values of the near kind made the kind the schema says
        foreach (var (name, value) in (made ?? arguments).ToList())
        {
            if (value is null || properties[name] is not JsonObject property || property["type"] is not JsonValue type || type.GetValueKind() != JsonValueKind.String)
                continue;
            JsonValueKind kind = value.GetValueKind();
            JsonNode? fitted = type.GetValue<string>() switch
            {
                // Several strings for one (paths as a list) joined the way the tool reads them, a comma between
                "string" when kind == JsonValueKind.Array && value.AsArray().All(each => each?.GetValueKind() == JsonValueKind.String) =>
                    string.Join(",", value.AsArray().Select(each => each!.GetValue<string>())),
                "array" when kind is JsonValueKind.String or JsonValueKind.Number => new JsonArray(value.DeepClone()),
                "integer" or "number" when kind == JsonValueKind.String && double.TryParse(value.GetValue<string>(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double number) =>
                    JsonValue.Create(number),
                "boolean" when kind == JsonValueKind.String && value.GetValue<string>().ToLowerInvariant() is "true" or "false" =>
                    JsonValue.Create(value.GetValue<string>().Equals("true", StringComparison.OrdinalIgnoreCase)),
                _ => null
            };
            if (fitted is not null)
                Made()[name] = fitted;
        }

        return made ?? arguments;
    }

    // filePath to file_path, maxChars to max_chars
    private static string Snake(string name)
    {
        var made = new System.Text.StringBuilder(name.Length + 4);
        for (int at = 0; at < name.Length; at++)
        {
            char c = name[at];
            if (char.IsUpper(c) && at > 0 && name[at - 1] != '_')
                made.Append('_');
            made.Append(c == '-' ? '_' : char.ToLowerInvariant(c));
        }

        return made.ToString();
    }

    /// <summary>The arguments a schema takes in one line, the required ones said so, for an error to end with.</summary>
    public static string Takes(JsonObject schema)
    {
        JsonObject properties = schema["properties"] as JsonObject ?? [];
        if (properties.Count == 0)
            return "It takes no arguments.";
        HashSet<string> required = schema["required"] is JsonArray names ? [.. names.Select(name => name?.GetValue<string>() ?? string.Empty)] : [];
        return $"It takes {string.Join(", ", properties.Select(each => required.Contains(each.Key) ? each.Key + " (required)" : each.Key))}.";
    }

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
                    return $"'{wanted}' is required. {Takes(schema)}";
            }
        }

        foreach (var (name, value) in arguments)
        {
            if (properties[name] is not JsonObject property)
                return $"'{name}' isn't an argument of this tool, it takes {(properties.Count == 0 ? "none" : string.Join(", ", properties.Select(each => each.Key)))}.";

            // A type of several (["integer", "null"]) fits when any of them does, the first being what it's said to be
            string[] types = property["type"] switch
            {
                JsonValue one when one.GetValueKind() == JsonValueKind.String => [one.GetValue<string>()],
                JsonArray several => [.. several.OfType<JsonValue>().Where(each => each.GetValueKind() == JsonValueKind.String).Select(each => each.GetValue<string>())],
                _ => []
            };
            if (value is null || types.Length == 0)
                continue;

            JsonValueKind kind = value.GetValueKind();
            string type = types[0];
            bool fits = types.Any(each => each switch
            {
                "string" => kind == JsonValueKind.String,
                "number" => kind == JsonValueKind.Number,
                // A number off the wire reads as a double, one Hex put in itself as the int it was
                "integer" => kind == JsonValueKind.Number && (value.AsValue().TryGetValue(out double whole) ? whole == Math.Floor(whole)
                    : value.AsValue().TryGetValue(out int _) || value.AsValue().TryGetValue(out long _)),
                "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
                "object" => kind == JsonValueKind.Object,
                "array" => kind == JsonValueKind.Array,
                "null" => kind == JsonValueKind.Null,
                _ => true
            });

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

    // A number parsed off the wire is a JsonElement, one Hex put in itself (the caller of an agent's call) is the int it
    // was, which won't come out as a double, so both are asked for
    public static double? Number(this JsonObject arguments, string name) =>
        arguments[name] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number ? null
        : value.TryGetValue(out double number) ? number
        : value.TryGetValue(out int whole) ? whole
        : value.TryGetValue(out long big) ? big
        : null;

    public static int? Integer(this JsonObject arguments, string name) => arguments.Number(name) is { } number ? (int)Math.Clamp(number, int.MinValue, int.MaxValue) : null;

    public static bool Flag(this JsonObject arguments, string name, bool fallback = false) =>
        arguments[name] is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? value.GetValue<bool>() : fallback;
}
