using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// The bits of the Model Context Protocol that aren't anybody's in particular, the versions Hex speaks, the error codes
/// JSON-RPC answers with, and the handshake's choice of version. MCP is JSON-RPC 2.0, a line of JSON a message.
/// </summary>
internal static class McpProtocol
{
    /// <summary>The versions Hex can speak, the newest first. They're dates, so the newer of two compares as the bigger.</summary>
    public static readonly string[] Versions = ["2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];

    /// <summary>The newest one, what Hex answers with when the client asks for one it doesn't know.</summary>
    public static string Latest => Versions[0];

    // What JSON-RPC calls its errors
    public const int PARSE_ERROR = -32700, INVALID_REQUEST = -32600, METHOD_NOT_FOUND = -32601, INVALID_PARAMS = -32602, INTERNAL_ERROR = -32603;

    /// <summary>
    /// The version the session goes on in. The one the client asked for when Hex speaks it, an older client gets its own
    /// back, otherwise the newest Hex has, which a client that can't speak it hangs up on, the way the spec has it.
    /// </summary>
    public static string Negotiate(string? asked) =>
        asked is not null && Versions.Contains(asked, StringComparer.Ordinal) ? asked : Latest;

    /// <summary>Whether a version is at least another, for what came in with a later one (a tool's title, its annotations).</summary>
    public static bool AtLeast(string version, string since) => string.CompareOrdinal(version, since) >= 0;

    /// <summary>An answer to a request.</summary>
    public static JsonObject Result(JsonNode? id, JsonNode? result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result ?? new JsonObject() };

    /// <summary>A request that went wrong, with JSON-RPC's code for how.</summary>
    public static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
