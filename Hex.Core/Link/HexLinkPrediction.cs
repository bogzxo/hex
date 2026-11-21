using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// A prediction asked of another machine's model over a link (predict/start, docs/hex-link-plan.md, 5.6): the text
/// before the caret, the text after it (empty for none), how many tokens at most, the strings that end it, and the
/// prompt's format as the prediction side names it. Link only carries it; what the fields mean is the prediction
/// side's, which maps them to and from its own request.
/// </summary>
internal sealed record HexLinkPrediction(string Prompt, string Suffix, int MaxTokens, IReadOnlyList<string> Stop, string Format)
{
    /// <summary>The most of a prompt and its suffix together, in bytes of UTF-8. More is refused as too big.</summary>
    public const int MOST_TEXT = 256 * 1024;

    /// <summary>The fewest and the most tokens a prediction is let ask for, what's asked clamped to them.</summary>
    public const int FEWEST_TOKENS = 1, MOST_TOKENS = 1024;

    /// <summary>The tokens asked for when a request doesn't say.</summary>
    public const int USUAL_TOKENS = 128;

    /// <summary>The most stop strings taken, and the longest; the rest are passed over.</summary>
    public const int MOST_STOPS = 16, LONGEST_STOP = 64;

    /// <summary>The longest format name and request id taken.</summary>
    public const int LONGEST_FORMAT = 40, LONGEST_ID = 64;

    /// <summary>The most text a predict/chunk is read with, a piece of a stream is never near it.</summary>
    public const int MOST_CHUNK = 64 * 1024;

    /// <summary>The status a prediction stopped by the machine making it says, never one of the model's own.</summary>
    public const string CANCELLED = "cancelled";

    /// <summary>The status a failure without one of its own says.</summary>
    public const string FAILED = "failed";

    /// <summary>The status said when the machine doesn't share its predictions.</summary>
    public const string REFUSED = "refused";

    /// <summary>The request as predict/start says it, with the asker's id.</summary>
    public JsonObject ToJson(string id) => new()
    {
        ["id"] = id,
        ["prompt"] = Prompt,
        ["suffix"] = Suffix,
        ["max_tokens"] = MaxTokens,
        ["stop"] = new JsonArray([.. Stop.Select(stop => (JsonNode)stop)]),
        ["format"] = Format
    };

    /// <summary>
    /// A request read off predict/start, every field with its limit: a prompt and suffix too big together are refused,
    /// the tokens clamped, the stops cut to how many and how long they may be. Missing text is empty.
    /// </summary>
    public static HexLinkPrediction Of(JsonObject said)
    {
        // A character past the most is enough to know it's too much, without taking all of it
        string prompt = HexLinkData.String(said, "prompt", MOST_TEXT + 1) ?? string.Empty;
        string suffix = HexLinkData.String(said, "suffix", MOST_TEXT + 1) ?? string.Empty;
        if (prompt.Length + suffix.Length > MOST_TEXT || Encoding.UTF8.GetByteCount(prompt) + Encoding.UTF8.GetByteCount(suffix) > MOST_TEXT)
            throw new HexLinkException(HexLinkErrors.TOO_BIG, $"A prediction's prompt and suffix may be {MOST_TEXT / 1024} KB together at most.");

        int tokens = Math.Clamp(HexLinkData.Int(said, "max_tokens") ?? USUAL_TOKENS, FEWEST_TOKENS, MOST_TOKENS);
        List<string> stops = HexLinkData.Strings(said, "stop", MOST_STOPS, LONGEST_STOP);
        stops.RemoveAll(stop => stop.Length == 0);
        return new HexLinkPrediction(prompt, suffix, tokens, stops, HexLinkData.String(said, "format", LONGEST_FORMAT) ?? string.Empty);
    }

    /// <summary>The error a prediction that couldn't be made is answered with, its status in a word in the data.</summary>
    public static HexLinkException Failed(string message, string status) =>
        new(HexLinkErrors.PREDICTION_FAILED, message, new JsonObject { ["status"] = status });

    /// <summary>The status a prediction's error says, failed when it says none.</summary>
    public static string StatusOf(HexLinkException error) =>
        error.Detail is JsonObject detail && HexLinkData.String(detail, "status", 40) is { Length: > 0 } status ? status : FAILED;

    /// <summary>What a machine that doesn't share its predictions makes of a request, IHexLinkServices' default.</summary>
    public static IAsyncEnumerable<string> NotServed() => throw Failed("This Hex doesn't share its predictions.", REFUSED);
}
