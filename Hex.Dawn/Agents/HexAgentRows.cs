using System.Numerics;
using System.Text.Json;

using Horizon.UI.Drawing;
using Horizon.UI.Skinning;

namespace Hex.Dawn;

/// <summary>
/// What the rows of an agent's chat are made of that needs no window, how a tool is named and pictured, what of its
/// input is worth showing, a note's words and a subagent's name. Shared by the Agents panel and Noon, and tested on its
/// own.
/// </summary>
internal static class HexAgentRows
{
    // The arguments a tool's input is read by, the first that's there said in its place
    private static readonly string[] PreviewKeys = ["command", "cmd", "query", "pattern", "file_path", "path", "filename", "url", "description", "prompt"];

    /// <summary>Prefer the useful argument over JSON scaffolding; plain or partial tool input remains readable.</summary>
    public static string ToolPreview(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        // A call's input is parsed once for as long as that text lives, a group's tooltip asked it of every call in it each
        // time the group was made again
        return Previews.GetValue(input, PreviewOf);
    }

    // What each input text was read to say, kept as long as the text is
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, string> Previews = new();

    private static string PreviewOf(string input)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(input);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.String) return root.GetString() ?? string.Empty;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (string key in PreviewKeys)
                    if (root.TryGetProperty(key, out JsonElement value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                        return value.GetString()!;
                return string.Join(" · ", root.EnumerateObject().Take(3).Select(property =>
                    property.Value.ValueKind == JsonValueKind.String ? $"{property.Name}: {property.Value.GetString()}" : property.Name));
            }
        }
        catch (JsonException) { }
        return input.Trim();
    }

    /// <summary>
    /// A tool's input laid out to be read, its JSON indented and with what JSON writers escape for a web page's sake
    /// written as itself: a shell command's && came out as &&, its quotes as ' and its 2> as 2>. What
    /// JSON has to escape (a quote inside a text, a backslash, a line break) still is. Input that isn't JSON as it was.
    /// </summary>
    public static string Readable(string input)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(input);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                document.RootElement.WriteTo(writer);
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return input;
        }
    }

    /// <summary>
    /// A note as its row says it, the rest taken as it is but an icon it starts with drawn, which is how a message
    /// between agents is told from the other notes.
    /// </summary>
    public static string NoteText(string text)
    {
        int end = text.StartsWith("$(", StringComparison.Ordinal) ? text.IndexOf(") ", StringComparison.Ordinal) : -1;
        return end > 2 && text[2..end].All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? text[..(end + 2)] + UIRichText.Literal(text[(end + 2)..])
            : UIRichText.Literal(text);
    }

    /// <summary>
    /// A tool row that's a subagent, Claude Code's Task or Agent or one that said steps of its own, named for the kind
    /// of agent it is and said by what it was sent to do. Null for any other tool.
    /// </summary>
    public static (string Name, string About)? SubagentOf(HexAgentEntry entry)
    {
        if (!IsSubagent(entry))
            return null;

        string kind = string.Empty, about = ToolPreview(entry.Input);
        try
        {
            using JsonDocument document = JsonDocument.Parse(entry.Input ?? string.Empty);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (document.RootElement.TryGetProperty("subagent_type", out JsonElement type) && type.ValueKind == JsonValueKind.String)
                    kind = type.GetString() ?? string.Empty;
                if (document.RootElement.TryGetProperty("description", out JsonElement said) && said.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(said.GetString()))
                    about = said.GetString()!;
            }
        }
        catch (JsonException) { }

        string name = kind.Length > 0 ? $"Subagent · {kind}" : "Subagent";
        return (name, about);
    }

    /// <summary>
    /// Whether a tool row is a subagent, by its name and its steps alone, nothing parsed or made. The rows ask it of every
    /// call several times a redraw, ten times a second while an agent streams.
    /// </summary>
    public static bool IsSubagent(HexAgentEntry entry)
    {
        if (entry.Steps > 0)
            return true;
        ReadOnlySpan<char> tool = ToolTitleSpan(entry.ToolName ?? string.Empty);
        return tool.Equals("task", StringComparison.OrdinalIgnoreCase) || tool.Equals("agent", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A text cut to so many lines, the rest said as how many more there were.</summary>
    public static string Lines(string text, int most)
    {
        // The lines are counted and cut at in place, a tool's output of thousands of lines was split whole for the twelve shown
        string plain = text.Contains('\r') ? text.Replace("\r", string.Empty) : text;
        ReadOnlySpan<char> trimmed = plain.AsSpan().Trim('\n');
        int total = trimmed.Count('\n') + 1;
        if (total <= most)
            return trimmed.Length == plain.Length ? plain : trimmed.ToString();

        // Where the last line to show ends, at the newline that's the `most`th of them
        int end = -1;
        for (int shown = 0; shown < most; shown++)
            end += 1 + trimmed[(end + 1)..].IndexOf('\n');
        end = Math.Max(end, 0);
        return string.Concat(trimmed[..end], $"\n... {total - most} more lines");
    }


    /// <summary>A text as one line of so many characters at the most.</summary>
    public static string OneLine(string text, int most)
    {
        // A line already, the usual for a row's text, is only trimmed, nothing split and joined for it
        string line = text.AsSpan().IndexOfAny('\n', '\r', '\t') < 0 ? text.Trim()
            : string.Join(' ', text.Split(['\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries).Select(part => part.Trim())).Trim();
        most = Math.Max(1, most);
        return line.Length <= most ? line : most <= 3 ? new string('.', most) : line[..(most - 3)] + "...";
    }

    /// <summary>A tool's name as a row says it, Hex's without the mcp__hex__ a host puts in front.</summary>
    public static string ToolTitle(string name) => ToolTitleSpan(name) is var title && title.Length == name.Length ? name : title.ToString();

    // The same as a span of the name, for asking about a tool's name without making a string of it
    private static ReadOnlySpan<char> ToolTitleSpan(string name)
    {
        int at = name.LastIndexOf("__", StringComparison.Ordinal);
        return name.StartsWith("mcp__", StringComparison.Ordinal) && at > 4 ? name.AsSpan(at + 2) : name;
    }

    /// <summary>The icon of a tool by what kind of tool it is, a command, a file changed or read, a search, Hex's own.</summary>
    public static string ToolIcon(string name)
    {
        string tool = ToolTitle(name).ToLowerInvariant();
        return tool switch
        {
            "bash" or "command" or "command_execution" or "shell" or "hex_terminal_run" or "hex_terminal" or "hex_terminal_send" or "hex_terminal_read" or "hex_run_task" or "hex_task_output" => "terminal",
            "edit" or "write" or "multiedit" or "file_change" or "apply_patch" or "notebookedit" or "hex_edit" or "hex_write" => "diff",
            "read" or "hex_read" or "hex_skim" or "hex_editor" or "hex_open" => "file-code",
            "grep" or "glob" or "hex_search" or "hex_find_in_files" or "hex_files" or "hex_refs" or "hex_references" => "search",
            "hex_symbols" or "hex_outline" or "hex_definition" or "hex_symbol_info" => "symbol-class",
            "hex_map" => "list-tree",
            "hex_show_image" => "file-media",
            "websearch" or "webfetch" or "web_search" => "globe",
            "todowrite" or "todo_list" or "hex_plan_update" or "hex_plan_read" => "checklist",
            "task" or "agent" => "organization",
            _ when tool.StartsWith("hex_notebook", StringComparison.Ordinal) => "notebook",
            _ when tool.StartsWith("hex_", StringComparison.Ordinal) => "robot",
            _ => "tools"
        };
    }

    public static Vector4? ToolColour(string name, UIPalette? palette)
    {
        if (palette is null || !ToolTitle(name).StartsWith("hex_", StringComparison.OrdinalIgnoreCase))
            return null;

        return ToolIcon(name) switch
        {
            "file-code" => palette.Syntax.Property,
            "search" or "symbol-class" or "list-tree" => palette.Syntax.Type,
            "diff" => palette.Syntax.Number,
            // A call's lavender, not the keyword's pink, which in Solstice sat next to the error's red and read as a failure
            "terminal" => palette.Syntax.Call,
            "checklist" or "notebook" => palette.Syntax.Reference,
            _ => palette.Accent
        };
    }
}
