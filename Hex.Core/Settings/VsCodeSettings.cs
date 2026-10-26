using System.Globalization;
using System.Text.Json;

using Horizon.HIDL.Library;
using Horizon.HIDL.Runtime;

namespace Hex.Core;

/// <summary>
/// One setting of a VS Code settings.json as Hex would take it. The Hex key and its value for one Hex has, the key and
/// no value for one whose value Hex can't take (a font Hex hasn't got), neither for one Hex hasn't got at all.
/// </summary>
internal sealed record VsCodeSetting(string Id, string Written, string? Key, IRuntimeValue? Value)
{
    /// <summary>Whether Hex has it and can take what it's set to.</summary>
    public bool Mapped => Key is not null && Value is not null;
}

/// <summary>
/// VS Code's settings.json read and put in Hex's words. The file is JSON with comments, // and /* */, and commas left
/// after the last of something, the way VS Code writes and reads it. Each id VS Code has that Hex has too is a line of
/// the map below, its Hex key and how its value turns into Hex's. A setting Hex gets later is one more line there.
/// </summary>
internal static class VsCodeSettings
{
    // What turns a VS Code value into Hex's, null when Hex can't take it. Fonts are the code fonts Hex has
    private delegate IRuntimeValue? Converter(JsonElement value, IReadOnlyCollection<string> fonts);

    // Every id Hex knows, its key in Hex and how the value goes over
    private static readonly (string Id, string Key, Converter Convert)[] Map =
    [
        ("editor.tabSize", "tab_size", Number),
        ("editor.insertSpaces", "insert_spaces", Flag),
        ("editor.detectIndentation", "detect_indentation", Flag),
        ("editor.formatOnSave", "format_on_save", Flag),
        ("editor.minimap.enabled", "minimap", Flag),
        ("editor.inlayHints.enabled", "inlay_hints", Words(("on", true), ("onUnlessPressed", true), ("off", false), ("offUnlessPressed", false))),
        ("editor.lineNumbers", "line_numbers", Words(("on", true), ("relative", true), ("interval", true), ("off", false))),
        ("editor.guides.indentation", "indent_guides", Flag),
        ("editor.renderWhitespace", "render_whitespace", Choice(("none", "none"), ("boundary", "boundary"), ("selection", "selection"), ("all", "all"))),
        ("editor.stickyScroll.enabled", "sticky_scroll", Flag),
        ("editor.rulers", "rulers", Rulers),
        ("editor.cursorStyle", "cursor_style", Choice(("line", "line"), ("line-thin", "line"), ("block", "block"), ("block-outline", "block"), ("underline", "underline"), ("underline-thin", "underline"))),
        ("editor.wordWrap", "wrap_code", Words(("on", true), ("wordWrapColumn", true), ("bounded", true), ("off", false))),
        ("editor.fontSize", "code_zoom", Zoom),
        ("editor.fontFamily", "code_font", Font),
        ("files.associations", "files_associations", Pairs),
        ("files.trimTrailingWhitespace", "trim_whitespace", Flag),
        ("files.insertFinalNewline", "final_newline", Flag),
        ("files.autoSave", "auto_save", Words(("afterDelay", true), ("off", false))),
        ("files.autoSaveDelay", "auto_save_delay", Seconds),
        ("files.exclude", "files_exclude", Patterns),
        ("search.exclude", "search_exclude", Patterns),
        ("search.useIgnoreFiles", "search_use_ignore_files", Flag),
        ("git.autofetch", "git_autofetch", Flag),
        ("git.confirmSync", "git_confirm_sync", Flag),
        ("git.enableSmartCommit", "git_smart_commit", SmartCommit),
        ("git.inputValidationSubjectLength", "git_subject_length", Number),
        ("terminal.integrated.scrollback", "scrollback", Number),
        ("terminal.integrated.copyOnSelection", "copy_on_select", Flag),
        ("terminal.integrated.rightClickBehavior", "right_click_paste", RightClick)
    ];

    private static readonly Dictionary<string, (string Key, Converter Convert)> ById = Map.ToDictionary(line => line.Id, line => (line.Key, line.Convert), StringComparer.Ordinal);

    /// <summary>The Hex key a VS Code id is, null for an id Hex hasn't got.</summary>
    public static string? HexKeyOf(string id) => ById.TryGetValue(id, out var line) ? line.Key : null;

    /// <summary>Every VS Code id Hex knows, in the order the map has them.</summary>
    public static IEnumerable<string> Ids => Map.Select(line => line.Id);

    /// <summary>Where VS Code keeps the user's settings, Code first, then Insiders, then VSCodium. Null when none of them has a file.</summary>
    public static string? UserFile()
    {
        string roaming = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
        foreach (string product in (string[])["Code", "Code - Insiders", "VSCodium"])
        {
            string path = Path.Combine(roaming, product, "User", "settings.json");
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    /// <summary>A folder's own VS Code settings, .vscode\settings.json in it.</summary>
    public static string WorkspaceFile(string folder) => Path.Combine(folder, ".vscode", "settings.json");

    /// <summary>The text of a settings.json read, each id and its value. Throws JsonException for one that doesn't read.</summary>
    public static Dictionary<string, JsonElement> Read(string text)
    {
        var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
        using JsonDocument document = JsonDocument.Parse(text, options);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("settings.json isn't an object");

        var read = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
            read[property.Name] = property.Value.Clone();
        return read;
    }

    /// <summary>Every setting of a file read, in the order it had them, as Hex would take each.</summary>
    public static List<VsCodeSetting> Plan(IReadOnlyDictionary<string, JsonElement> read, IReadOnlyCollection<string> fonts)
    {
        var plan = new List<VsCodeSetting>(read.Count);
        foreach (var (id, value) in read)
        {
            string written = Written(value);
            if (!ById.TryGetValue(id, out var line))
                plan.Add(new VsCodeSetting(id, written, null, null));
            else
                plan.Add(new VsCodeSetting(id, written, line.Key, line.Convert(value, fonts)));
        }

        return plan;
    }

    // A value the way the preview shows it, short
    private static string Written(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(Written)),
        JsonValueKind.Object => value.EnumerateObject().Count() is var count && count == 1 ? "{1 entry}" : $"{{{count} entries}}",
        _ => value.GetRawText()
    };

    /* The converters */

    private static IRuntimeValue? Flag(JsonElement value, IReadOnlyCollection<string> fonts) => value.ValueKind switch
    {
        JsonValueKind.True => Values.Bool(true),
        JsonValueKind.False => Values.Bool(false),
        _ => null
    };

    private static IRuntimeValue? Number(JsonElement value, IReadOnlyCollection<string> fonts) =>
        value.ValueKind == JsonValueKind.Number ? new NumberValue((float)value.GetDouble()) : null;

    // A word of VS Code's that's on or off in Hex
    private static Converter Words(params (string Word, bool On)[] words) => (value, _) =>
        value.ValueKind == JsonValueKind.String && Array.FindIndex(words, word => word.Word == value.GetString()) is var at and >= 0 ? Values.Bool(words[at].On) : null;

    // A word of VS Code's that's one of Hex's
    private static Converter Choice(params (string Word, string Hex)[] words) => (value, _) =>
        value.ValueKind == JsonValueKind.String && Array.FindIndex(words, word => word.Word == value.GetString()) is var at and >= 0 ? new StringValue(words[at].Hex) : null;

    // A list of columns, or of objects with a column in each, written the way Hex writes them, "80, 120"
    private static IRuntimeValue? Rulers(JsonElement value, IReadOnlyCollection<string> fonts)
    {
        if (value.ValueKind != JsonValueKind.Array)
            return null;

        var columns = new List<int>();
        foreach (JsonElement ruler in value.EnumerateArray())
        {
            JsonElement column = ruler.ValueKind == JsonValueKind.Object && ruler.TryGetProperty("column", out JsonElement inside) ? inside : ruler;
            if (column.ValueKind == JsonValueKind.Number && column.TryGetInt32(out int at) && at > 0)
                columns.Add(at);
        }

        return new StringValue(HexSettings.RulersText([.. columns]));
    }

    // Hex zooms its code rather than having a size, 14 being what it comes as
    private static IRuntimeValue? Zoom(JsonElement value, IReadOnlyCollection<string> fonts) =>
        value.ValueKind == JsonValueKind.Number ? new NumberValue(Math.Clamp((float)value.GetDouble() / 14.0f, 0.5f, 3.0f)) : null;

    // The first of the families that's a font Hex has, "'Cascadia Mono', Consolas, monospace" is cascadia_mono
    private static IRuntimeValue? Font(JsonElement value, IReadOnlyCollection<string> fonts)
    {
        if (value.ValueKind != JsonValueKind.String)
            return null;

        static string Plain(string name) => new([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
        foreach (string family in (value.GetString() ?? string.Empty).Split(','))
        {
            string wanted = Plain(family);
            if (fonts.FirstOrDefault(font => Plain(font) == wanted) is { } found)
                return new StringValue(found);
        }

        return null;
    }

    // An object of texts by text, a pattern's language, whatever isn't a text left out
    private static IRuntimeValue? Pairs(JsonElement value, IReadOnlyCollection<string> fonts)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return null;

        var pairs = new Dictionary<string, IRuntimeValue>(StringComparer.Ordinal);
        foreach (JsonProperty pair in value.EnumerateObject())
        {
            if (pair.Value.ValueKind == JsonValueKind.String)
                pairs[pair.Name] = new StringValue(pair.Value.GetString() ?? string.Empty);
        }

        return new ObjectValue(pairs);
    }

    // VS Code's waits in milliseconds, Hex's in seconds
    private static IRuntimeValue? Seconds(JsonElement value, IReadOnlyCollection<string> fonts) =>
        value.ValueKind == JsonValueKind.Number ? new NumberValue((float)(value.GetDouble() / 1000.0)) : null;

    // An object of globs by whether they're left out, the ones that are as a list. One that's a when clause isn't a plain yes
    private static IRuntimeValue? Patterns(JsonElement value, IReadOnlyCollection<string> fonts) =>
        value.ValueKind != JsonValueKind.Object ? null
            : Natives.List(value.EnumerateObject().Where(pair => pair.Value.ValueKind == JsonValueKind.True).Select(pair => pair.Name));

    // Committing everything when nothing's staged, true always does and false asks
    private static IRuntimeValue? SmartCommit(JsonElement value, IReadOnlyCollection<string> fonts) => value.ValueKind switch
    {
        JsonValueKind.True => new StringValue("always"),
        JsonValueKind.False => new StringValue("ask"),
        _ => null
    };

    // Only paste is Hex's paste on a right click, the rest keep the menu
    private static IRuntimeValue? RightClick(JsonElement value, IReadOnlyCollection<string> fonts) =>
        value.ValueKind == JsonValueKind.String ? Values.Bool(value.GetString() == "paste") : null;

    /// <summary>A value of Hex's the way the preview shows it, short and without quotes.</summary>
    public static string Shown(IRuntimeValue value) => value switch
    {
        BooleanValue flag => flag.Value ? "on" : "off",
        NumberValue number => number.Value.ToString("0.###", CultureInfo.InvariantCulture),
        StringValue text => text.Value,
        ListValue list => string.Join(", ", list.Items.Select(Shown)),
        ObjectValue { Properties: { } pairs } => string.Join(", ", pairs.Select(pair => $"{pair.Key} {Shown(pair.Value)}")),
        _ => value.ToString() ?? string.Empty
    };
}
