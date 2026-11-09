using System.Text.Json.Nodes;
using Horizon.UI.Components;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private IEnumerable<HexMcpTool> AgentTools() =>
    [
        new(new McpTool("hex_agent_hint", "Show a dismissible code hint",
            "Show or update a hint at a line and column of an open text editor. It has a dismiss control and clears when the document changes. Text is an annotation and never changes source. Reuse an id to update it.",
            McpSchema.Object([("id", McpSchema.String("A stable hint id.")), ("text", McpSchema.String("The hint, at most 2000 characters.")), ("path", McpSchema.String("An open file, current by default.")), ("line", McpSchema.Integer("Line, 1 based.")), ("column", McpSchema.Integer("Column, 1 based, 1 by default.")), ("version", McpSchema.Integer("Optional expected editor document version."))], "id", "text", "line")), AgentHint),
        new(new McpTool("hex_agent_hint_remove", "Remove a code hint",
            "Remove a hint by id from the current or named open file.",
            McpSchema.Object([("id", McpSchema.String("The hint id.")), ("path", McpSchema.String("An open file, current by default."))], "id")), AgentHintRemove)
    ];

    private HexDocument? AgentHintTarget(JsonObject args) => args.String("path") is { } path
        ? documents.FirstOrDefault(open => open.Path is { } file && PlatformPaths.Same(file, McpPath(path))) : document;

    private IEnumerable<McpToolResult?> AgentHint(JsonObject args)
    {
        var target = AgentHintTarget(args);
        string id = args.String("id") ?? "", message = args.String("text") ?? "";
        int line = (args.Integer("line") ?? 0) - 1, column = (args.Integer("column") ?? 1) - 1;
        if (target is null || target.Picture is not null || target.DiffOf is not null || line < 0 || line >= target.Editor.Document.LineCount
            || column < 0 || column > target.Editor.Document.LineAt(line).Length || string.IsNullOrWhiteSpace(id) || id.Length > 100
            || string.IsNullOrWhiteSpace(message) || message.Length > 2000
            || (args.Integer("version") is { } version && version != target.Editor.Document.Version))
        {
            yield return McpToolResult.Error("An open text file, valid 1-based position, matching version, id and hint text are required.");
            yield break;
        }
        if (target.Editor.AgentHints.Count >= 50 && !target.Editor.AgentHints.Any(hint => hint.Id == id))
        {
            yield return McpToolResult.Error("At most 50 hints per document. Remove some first.");
            yield break;
        }
        target.Editor.AgentHints = [.. target.Editor.AgentHints.Where(hint => hint.Id != id), new(id, line, column, message)];
        agentHintVersions[target] = target.Editor.Document.Version;
        yield return McpToolResult.Text($"Hint {id} shown at {line + 1}:{column + 1}.");
    }

    private IEnumerable<McpToolResult?> AgentHintRemove(JsonObject args)
    {
        var target = AgentHintTarget(args);
        if (target is null) { yield return McpToolResult.Error("The file is not open."); yield break; }
        target.Editor.AgentHints = [.. target.Editor.AgentHints.Where(hint => hint.Id != args.String("id"))];
        yield return McpToolResult.Text("Hint removed.");
    }
}
