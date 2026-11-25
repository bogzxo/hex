using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// A chat model another machine serves over a link (ai/models, docs/hex-link-plan.md, 5.7): the name the machine knows it
/// by (what goes in a request's "model"), a name to show, what it can do (<see cref="LocalModelCaps"/> as a number), its
/// context in tokens (0 when unknown) and the one line of size and quant the picker shows.
/// </summary>
internal sealed record HexLinkAiModel(string Id, string Name, int Caps, int Context, string Detail)
{
    public const int MOST_MODELS = 64;

    public JsonObject ToJson() => new() { ["id"] = Id, ["name"] = Name, ["caps"] = Caps, ["context"] = Context, ["detail"] = Detail };

    public static HexLinkAiModel? Of(JsonObject said) =>
        HexLinkData.String(said, "id", HexLinkAiRequest.LONGEST_MODEL) is { Length: > 0 } id
            ? new HexLinkAiModel(id, HexLinkData.String(said, "name", 120) is { Length: > 0 } name ? name : id, HexLinkData.Int(said, "caps") ?? 0,
                HexLinkData.Int(said, "context") ?? 0, HexLinkData.String(said, "detail", 80) ?? string.Empty)
            : null;
}

/// <summary>
/// A chat completion asked of another machine's model over a link (ai/start): the model's name there and the request as an
/// OpenAI-style chat completions body (messages, tools, sampling), kept as JSON text so Link carries it without knowing
/// what's in it. The machine serving it bounds it (<see cref="Bound"/>): streaming forced on, the answer's tokens clamped,
/// one choice. What comes back are the server's own streamed chunks (chat.completion.chunk objects), their text in
/// ai/chunk notifications, so content, reasoning and tool calls all arrive as the server wrote them.
/// </summary>
internal sealed record HexLinkAiRequest(string Model, string Body)
{
    /// <summary>The biggest request body taken, in bytes of UTF-8. More is refused as too big.</summary>
    public const int MOST_BODY = 1024 * 1024;

    /// <summary>The most tokens an answer may have, and what it has when the request doesn't say.</summary>
    public const int MOST_TOKENS = 8192, USUAL_TOKENS = 2048;

    /// <summary>The longest model name and request id taken.</summary>
    public const int LONGEST_MODEL = 200, LONGEST_ID = 64;

    /// <summary>The most text an ai/chunk is read with, a server's chunk is never near it.</summary>
    public const int MOST_CHUNK = 256 * 1024;

    /// <summary>The most requests of one device that are running or waiting; one runs and the rest queue behind it.</summary>
    public const int MOST_QUEUED = 3;

    /// <summary>The status an answer stopped by the machine asking says, never one of the model's own.</summary>
    public const string CANCELLED = "cancelled";

    /// <summary>The status a failure without one of its own says.</summary>
    public const string FAILED = "failed";

    /// <summary>The status said when the machine doesn't serve AI (the setting is off, or the model went).</summary>
    public const string REFUSED = "refused";

    /// <summary>The status said when the device has too many requests running or waiting.</summary>
    public const string BUSY = "busy";

    /// <summary>The status said when the machine has no model of that name.</summary>
    public const string NO_MODEL = "nomodel";

    /// <summary>The request as ai/start says it, with the asker's id.</summary>
    public JsonObject ToJson(string id) => new() { ["id"] = id, ["model"] = Model, ["body"] = Body };

    /// <summary>A request read off ai/start, its body bounded: too big, or not a chat completions body, is refused.</summary>
    public static HexLinkAiRequest Of(JsonObject said)
    {
        string model = HexLinkData.String(said, "model", LONGEST_MODEL + 1) ?? string.Empty;
        if (model.Length == 0 || model.Length > LONGEST_MODEL)
            throw HexLinkException.Invalid($"A chat request names a model of 1 to {LONGEST_MODEL} characters.");
        string body = HexLinkData.String(said, "body", MOST_BODY + 1) ?? string.Empty;
        if (body.Length > MOST_BODY || Encoding.UTF8.GetByteCount(body) > MOST_BODY)
            throw new HexLinkException(HexLinkErrors.TOO_BIG, $"A chat request may be {MOST_BODY / 1024} KB at most.");
        return new HexLinkAiRequest(model, Bound(body, model));
    }

    /// <summary>
    /// The body this machine sends its model: the model's name as it knows it, streaming on, one choice, the tokens an
    /// answer may have clamped to <see cref="MOST_TOKENS"/> (<see cref="USUAL_TOKENS"/> when it asks for none), and the
    /// keys that would change where the server sends things left out.
    /// </summary>
    public static string Bound(string body, string model)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(body) as JsonObject ?? throw HexLinkException.Invalid("A chat request is a JSON object.");
        }
        catch (JsonException)
        {
            throw HexLinkException.Invalid("A chat request is a JSON object.");
        }

        if (root["messages"] is not JsonArray { Count: > 0 })
            throw HexLinkException.Invalid("A chat request has messages.");
        root["model"] = model;
        root["stream"] = true;
        root["n"] = 1;
        root.Remove("logit_bias");
        int tokens = HexLinkData.Int(root, "max_tokens") ?? HexLinkData.Int(root, "max_completion_tokens") ?? USUAL_TOKENS;
        tokens = Math.Clamp(tokens <= 0 ? USUAL_TOKENS : tokens, 1, MOST_TOKENS);
        root["max_tokens"] = tokens;
        root.Remove("max_completion_tokens");
        return root.ToJsonString();
    }

    /// <summary>The error a chat that couldn't be made is answered with, its status in a word in the data.</summary>
    public static HexLinkException Failed(string message, string status) =>
        new(HexLinkErrors.AI_FAILED, message, new JsonObject { ["status"] = status });

    /// <summary>The status a chat's error says, failed when it says none.</summary>
    public static string StatusOf(HexLinkException error) =>
        error.Detail is JsonObject detail && HexLinkData.String(detail, "status", 40) is { Length: > 0 } status ? status : FAILED;

    /// <summary>What a machine that doesn't serve AI makes of a request, IHexLinkServices' default.</summary>
    public static IAsyncEnumerable<string> NotServed() => throw Failed("This Hex doesn't serve AI.", REFUSED);
}

/// <summary>
/// The id a model on a linked device has in the library and the settings: <c>link:&lt;device fingerprint&gt;:&lt;model&gt;</c>, the
/// fingerprint the device's XXXX-XXXX-XXXX-XXXX (which never has a colon) and the model the name the device serves it by
/// (which may, Ollama's qwen3:14b).
/// </summary>
internal static class LinkAiModelId
{
    public const string PREFIX = "link:";

    public static string Make(HexLinkDevice device, string model) => PREFIX + device.Fingerprint + ":" + model;

    public static bool Is(string? id) => id is not null && id.StartsWith(PREFIX, StringComparison.OrdinalIgnoreCase);

    /// <summary>The fingerprint and model a linked model's id names, false for an id that isn't one.</summary>
    public static bool TryParse(string? id, out string fingerprint, out string model)
    {
        fingerprint = model = string.Empty;
        if (!Is(id))
            return false;
        int colon = id!.IndexOf(':', PREFIX.Length);
        if (colon <= PREFIX.Length || colon == id.Length - 1)
            return false;
        fingerprint = id[PREFIX.Length..colon];
        model = id[(colon + 1)..];
        return true;
    }
}
