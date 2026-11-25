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
        ReadOnlySpan<char> tool = ToolTitleSpan(name);
        if (tool.Equals("bash", StringComparison.OrdinalIgnoreCase) || tool.Equals("command", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("command_execution", StringComparison.OrdinalIgnoreCase) || tool.Equals("shell", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("hex_terminal_run", StringComparison.OrdinalIgnoreCase) || tool.Equals("hex_terminal", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("hex_terminal_send", StringComparison.OrdinalIgnoreCase) || tool.Equals("hex_terminal_read", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("hex_run_task", StringComparison.OrdinalIgnoreCase) || tool.Equals("hex_task_output", StringComparison.OrdinalIgnoreCase))
            return "terminal";
        if (tool.Equals("edit", StringComparison.OrdinalIgnoreCase) || tool.Equals("write", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("multiedit", StringComparison.OrdinalIgnoreCase) || tool.Equals("file_change", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("apply_patch", StringComparison.OrdinalIgnoreCase) || tool.Equals("notebookedit", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("hex_edit", StringComparison.OrdinalIgnoreCase) || tool.Equals("hex_write", StringComparison.OrdinalIgnoreCase))
            return "diff";
        if (tool.Equals("read", StringComparison.OrdinalIgnoreCase) || tool.Equals("hex_read", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("hex_skim", StringComparison.OrdinalIgnoreCase) || tool.Equals("hex_editor", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("hex_open", StringComparison.OrdinalIgnoreCase))
            return "file-code";
        if (tool.Equals("grep", StringComparison.OrdinalIgnoreCase) || tool.Equals("glob", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("hex_search", StringComparison.OrdinalIgnoreCase) || tool.Equals("hex_find_in_files", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("hex_files", StringComparison.OrdinalIgnoreCase) || tool.Equals("hex_refs", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("hex_references", StringComparison.OrdinalIgnoreCase))
            return "search";
        if (tool.Equals("hex_symbols", StringComparison.OrdinalIgnoreCase) || tool.Equals("hex_outline", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("hex_definition", StringComparison.OrdinalIgnoreCase) || tool.Equals("hex_symbol_info", StringComparison.OrdinalIgnoreCase))
            return "symbol-class";
        if (tool.Equals("hex_map", StringComparison.OrdinalIgnoreCase))
            return "list-tree";
        if (tool.Equals("hex_show_image", StringComparison.OrdinalIgnoreCase))
            return "file-media";
        if (tool.Equals("websearch", StringComparison.OrdinalIgnoreCase) || tool.Equals("webfetch", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("web_search", StringComparison.OrdinalIgnoreCase))
            return "globe";
        if (tool.Equals("todowrite", StringComparison.OrdinalIgnoreCase) || tool.Equals("todo_list", StringComparison.OrdinalIgnoreCase)
            || tool.Equals("hex_plan_update", StringComparison.OrdinalIgnoreCase) || tool.Equals("hex_plan_read", StringComparison.OrdinalIgnoreCase))
            return "checklist";
        if (tool.Equals("task", StringComparison.OrdinalIgnoreCase) || tool.Equals("agent", StringComparison.OrdinalIgnoreCase))
            return "organization";
        if (tool.StartsWith("hex_notebook", StringComparison.OrdinalIgnoreCase))
            return "notebook";
        if (tool.StartsWith("hex_", StringComparison.OrdinalIgnoreCase))
            return "robot";
        return "tools";
    }

    public static Vector4? ToolColour(string name, UIPalette? palette)
    {
        if (palette is null || !ToolTitleSpan(name).StartsWith("hex_", StringComparison.OrdinalIgnoreCase))
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

    private const int CACHED_SMALL_INTS = 257;
    private static readonly string[] SmallIntCache = CreateSmallIntCache();

    private static string[] CreateSmallIntCache()
    {
        var cache = new string[CACHED_SMALL_INTS];
        for (int i = 0; i < cache.Length; i++)
            cache[i] = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return cache;
    }

    /// <summary>Formats non-negative small integers without allocating on hot UI paths.</summary>
    public static string SmallInt(int value) =>
        (uint)value < (uint)SmallIntCache.Length
            ? SmallIntCache[value]
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private const int CACHED_TENTHS_MAX = 601; // 0.0s through 60.0s
    private static readonly string[] TenthsSecondsCache = CreateTenthsSecondsCache();

    private static string[] CreateTenthsSecondsCache()
    {
        var cache = new string[CACHED_TENTHS_MAX];
        for (int i = 0; i < cache.Length; i++)
            cache[i] = (i / 10.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s";
        return cache;
    }

    /// <summary>Formats a duration in seconds as <c>0.0s</c>, reusing cached strings for 0.0s–60.0s.</summary>
    public static string FormatTenthsSeconds(double seconds)
    {
        if (seconds < 0.0)
            seconds = 0.0;
        int tenths = (int)Math.Round(seconds * 10.0, MidpointRounding.AwayFromZero);
        if ((uint)tenths < (uint)TenthsSecondsCache.Length)
            return TenthsSecondsCache[tenths];
        return seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s";
    }

    private static readonly string[] ShortSecondsCache = CreateShortDurationCache(60, "s");
    private static readonly string[] ShortMinutesCache = CreateShortDurationCache(60, "m");

    private static string[] CreateShortDurationCache(int count, string suffix)
    {
        var cache = new string[count];
        for (int i = 0; i < count; i++)
            cache[i] = i.ToString(System.Globalization.CultureInfo.InvariantCulture) + suffix;
        return cache;
    }

    /// <summary>Formats short durations (<c>12s</c>, <c>11m</c>, <c>1h 5m</c>) with zero allocations under one hour.</summary>
    public static string FormatDurationShort(double seconds)
    {
        if (seconds < 0.0)
            seconds = 0.0;
        if (seconds < 60.0)
        {
            int whole = (int)Math.Round(seconds, MidpointRounding.AwayFromZero);
            if ((uint)whole < (uint)ShortSecondsCache.Length)
                return ShortSecondsCache[whole];
            return "1m";
        }
        if (seconds < 3600.0)
        {
            int mins = (int)Math.Round(seconds / 60.0, MidpointRounding.AwayFromZero);
            if ((uint)mins < (uint)ShortMinutesCache.Length)
                return ShortMinutesCache[mins];
            return "1h 0m";
        }
        return $"{(int)(seconds / 3600)}h {(int)(seconds % 3600 / 60)}m";
    }

    /// <summary>
    /// Finds the index right after the last blank line (<c>\n\n</c>) outside a fenced code block so completed
    /// markdown blocks above that point stay frozen while the tail streams in.
    /// When <paramref name="startFrom"/> is a previous split point (guaranteed outside a fence), scans only the delta.
    /// </summary>
    public static int StableMarkdownSplit(string text, int startFrom = 0)
    {
        if (startFrom < 0)
            startFrom = 0;
        if (startFrom >= text.Length)
            return startFrom;

        bool inFence = false;
        int lastSplit = startFrom;
        int i = startFrom;
        while (i < text.Length)
        {
            int lineStart = i;
            int nl = text.IndexOf('\n', i);
            int lineEnd = nl < 0 ? text.Length : nl;
            ReadOnlySpan<char> line = text.AsSpan(lineStart, lineEnd - lineStart).TrimEnd('\r');
            ReadOnlySpan<char> trimmed = line.TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal))
                inFence = !inFence;
            else if (!inFence && trimmed.Length == 0 && lineStart > 0 && nl >= 0)
                lastSplit = nl + 1;
            i = nl < 0 ? text.Length : nl + 1;
        }
        return lastSplit;
    }
}
