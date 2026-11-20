using System.Text;
using System.Text.Json.Nodes;

namespace Hex.Dawn;

// Pictures an agent shows the person, screenshots it took or images it made, under its call in the chat as small ones a
// click opens full screen, the same as a picture pasted into what was asked. The call's answer lists them as Markdown
// pictures (![name](file:///...)), which is what the chat's row finds them by, see ShowsPictures
internal sealed partial class DawnScene
{
    /// <summary>The most pictures one call shows.</summary>
    private const int PICTURES_SHOWN = 12;

    private IEnumerable<HexMcpTool> PictureTools() =>
    [
        new(new McpTool("hex_show_image", "Show images",
            "Show the user one or more image files (PNG, JPEG, BMP, GIF, ...) in your Hex chat, under this call as thumbnails they can click to open full screen and zoom. Use it for screenshots, comparisons, diagrams or anything you made that's easier seen than described. Paths are absolute or workspace-relative.",
            McpSchema.Object([
                ("paths", new JsonObject
                {
                    ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" },
                    ["description"] = $"The image files, in the order to show them, at most {PICTURES_SHOWN}."
                }),
                ("caption", McpSchema.String("Optional words said above them."))
            ], "paths"), ReadOnly: true), ShowImages)
    ];

    private IEnumerable<McpToolResult?> ShowImages(JsonObject args)
    {
        if (args["paths"] is not JsonArray asked || asked.Count == 0)
        {
            yield return McpToolResult.Error("'paths' should be a list of image files.");
            yield break;
        }

        var said = new StringBuilder();
        if (args.String("caption")?.Trim() is { Length: > 0 } caption)
            said.Append(caption).Append("\n\n");

        var missing = new List<string>();
        int shown = 0;
        foreach (JsonNode? node in asked)
        {
            if (node?.GetValue<string>() is not { Length: > 0 } given)
                continue;
            string path = McpPath(given.Trim());
            if (!File.Exists(path) || !IsPicture(path))
            {
                missing.Add(given);
                continue;
            }
            if (shown++ == PICTURES_SHOWN)
                break;

            // As a file URI, a path with a space in it is still one Markdown link
            said.Append("![").Append(Path.GetFileName(path)).Append("](").Append(new Uri(path).AbsoluteUri).Append(")\n");
        }

        if (shown == 0)
        {
            yield return McpToolResult.Error($"No image file to show: {string.Join(", ", missing)}.");
            yield break;
        }

        if (missing.Count > 0)
            said.Append("\nNot shown, not an image file there: ").Append(string.Join(", ", missing)).Append('.');

        yield return McpToolResult.Text(said.ToString().TrimEnd());
    }

    /// <summary>Whether a tool's row shows the pictures its answer lists, hex_show_image's under whatever agent system's name for it.</summary>
    private static bool ShowsPictures(HexAgentEntry entry) =>
        entry.Kind == HexAgentEntryKind.Tool && entry.ToolName is { } name && name.EndsWith("hex_show_image", StringComparison.Ordinal);
}
