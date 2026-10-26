using System.Text.Json.Nodes;

namespace Hex.Core;

/// <summary>
/// A tool an agent can call, as tools/list says it. The description is written for a model to read, one line of what it
/// does, and the schema is JSON Schema for its arguments (always an object).
/// </summary>
/// <param name="ReadOnly">Whether it only looks, never changes anything, which clients use to ask before the others.</param>
internal sealed record McpTool(string Name, string Title, string Description, JsonObject InputSchema, bool ReadOnly = false)
{
    /// <summary>How tools/list writes it, the parts a version hasn't got left off.</summary>
    public JsonObject Describe(string version)
    {
        var described = new JsonObject
        {
            ["name"] = Name,
            ["description"] = Description,
            ["inputSchema"] = InputSchema.DeepClone()
        };

        if (McpProtocol.AtLeast(version, "2025-06-18"))
            described["title"] = Title;

        if (McpProtocol.AtLeast(version, "2025-03-26"))
            described["annotations"] = new JsonObject { ["title"] = Title, ["readOnlyHint"] = ReadOnly, ["openWorldHint"] = false };

        return described;
    }
}

/// <summary>What a tool call came back with, the content a client shows the model and whether it went wrong.</summary>
internal sealed class McpToolResult
{
    public JsonArray Content { get; } = [];

    /// <summary>Whether the tool failed, which the model reads as something to fix and try again, not a broken server.</summary>
    public bool IsError { get; init; }

    public static McpToolResult Text(string text) => new McpToolResult().AddText(text);

    public static McpToolResult Error(string text) => new McpToolResult { IsError = true }.AddText(text);

    /// <summary>A picture, a PNG, with a line of text about it before it.</summary>
    public static McpToolResult Image(byte[] png, string said) => new McpToolResult().AddText(said).AddImage(png);

    public McpToolResult AddText(string text)
    {
        Content.Add((JsonNode)new JsonObject { ["type"] = "text", ["text"] = text });
        return this;
    }

    public McpToolResult AddImage(byte[] png, string mimeType = "image/png")
    {
        Content.Add((JsonNode)new JsonObject { ["type"] = "image", ["data"] = Convert.ToBase64String(png), ["mimeType"] = mimeType });
        return this;
    }

    /// <summary>The result as tools/call answers it.</summary>
    public JsonObject ToJson() => new() { ["content"] = Content.DeepClone(), ["isError"] = IsError };

    /// <summary>All the text of it in one, for a test or a log.</summary>
    public string AllText() =>
        string.Join("\n", Content.OfType<JsonObject>().Where(item => item["type"]?.GetValue<string>() == "text").Select(item => item["text"]?.GetValue<string>() ?? string.Empty));
}

/// <summary>
/// Whoever has the tools, Hex's editor or a test's stand in. Calls come from the connection's threads, the tools go and
/// run wherever they have to (the editor's own thread) and the task finishes when they're done.
/// </summary>
internal interface IMcpTools
{
    IReadOnlyList<McpTool> Tools { get; }

    Task<McpToolResult> Call(string name, JsonObject arguments, CancellationToken cancel);
}
