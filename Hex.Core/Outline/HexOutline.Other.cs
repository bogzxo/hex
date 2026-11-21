using System.Text.RegularExpressions;

namespace Hex.Core;

// The outline of a file that isn't HIDL, what it declares by the shapes its language writes declarations in, a
// line at a time, how deep each is by how far in it is. Not a parser and not meant to be one, a language server's
// symbols are what a file with a server gets, this is for the rest and for before the server has answered. C#, D, the
// shaders, Python, JavaScript and TypeScript, Markdown's headings, the keys of JSON and YAML.
internal static partial class HexOutline
{
    [GeneratedRegex(@"^(\s*)(?:(?:public|private|protected|internal|static|sealed|abstract|partial|readonly|unsafe|file|ref|new)\s+)*(class|struct|record(?:\s+(?:struct|class))?|interface|enum)\s+(\w+)")]
    private static partial Regex CsType();

    [GeneratedRegex(@"^(\s*)(?:(?:public|private|protected|internal|static|sealed|abstract|override|virtual|async|readonly|unsafe|extern|new|required|partial)\s+)+[\w<>\[\],.?() ]+?\s+(\w+)\s*(\(|\{|=>|=|;)")]
    private static partial Regex CsMember();

    [GeneratedRegex(@"^(\s*)(?:\[[^\]]*\]\s*)*(?:static\s+|inline\s+)?(struct)\s+(\w+)|^(\s*)(?:\[[^\]]*\]\s*)*[\w<>]+\s+(\w+)\s*\([^;]*$")]
    private static partial Regex ShaderFunction();

    [GeneratedRegex(@"^(\s*)(?:(?:public|private|protected|package|export|static|final|abstract|shared|immutable|const|synchronized|extern\([^)]*\)|align\([^)]*\)|@\w+)\s+)*(class|struct|union|interface|enum|template|mixin\s+template)\s+(\w+)")]
    private static partial Regex DType();

    [GeneratedRegex(@"^(\s*)(?:(?:public|private|protected|package|export|static|final|abstract|override|nothrow|pure|ref|auto|const|immutable|shared|inout|synchronized|deprecated|extern\([^)]*\)|@\w+)\s+)*([\w!.\[\]*]+(?:\([^)]*\))?)\s+(\w+)\s*(?:\([^)]*\)\s*)?\([^)]*\)\s*(?:(?:const|immutable|shared|inout|nothrow|pure|scope|return|@\w+)\s*)*(?:\{.*|in\b.*|out\b.*|do\b.*|if\s*\(.*)?$")]
    private static partial Regex DFunction();

    [GeneratedRegex(@"^(\s+)(?:(?:public|private|protected|package|static|const|immutable|shared|__gshared)\s+)*[\w!.\[\]*]+(?:\([^)]*\))?\s+(\w+(?:\s*=\s*[^,;]+)?(?:\s*,\s*\w+(?:\s*=\s*[^,;]+)?)*)\s*;\s*$")]
    private static partial Regex DField();

    [GeneratedRegex(@"(?:^|,)\s*(\w+)")]
    private static partial Regex DFieldName();

    [GeneratedRegex(@"^(\s*)(def|class|async def)\s+(\w+)")]
    private static partial Regex Python();

    [GeneratedRegex(@"^(\s*)(?:export\s+)?(?:default\s+)?(?:async\s+)?(function|class|interface|type|enum)\s+(\w+)|^(\s*)(?:export\s+)?(?:const|let)\s+(\w+)\s*=\s*(?:async\s*)?\(")]
    private static partial Regex Script();

    [GeneratedRegex(@"^(#{1,6})\s+(.+?)\s*#*\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)""([^""]+)""\s*:")]
    private static partial Regex JsonKey();

    [GeneratedRegex(@"^(\s*)([\w.\-""']+)\s*:(\s|$)")]
    private static partial Regex YamlKey();


    // The first words a line of C# that declares something can start with, a modifier or the kind of type. A line that
    // starts any other way isn't one the two expressions above could match, which is nearly all of the lines of code
    private static readonly System.Collections.Frozen.FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> CsStarts =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(["public", "private", "protected", "internal", "static", "sealed", "abstract", "partial", "readonly", "unsafe", "file", "ref", "new",
            "override", "virtual", "async", "extern", "required", "class", "struct", "record", "interface", "enum"], StringComparer.Ordinal).GetAlternateLookup<ReadOnlySpan<char>>();

    private static readonly HashSet<string> NotNames = ["if", "for", "foreach", "while", "switch", "return", "new", "catch", "using", "lock", "fixed", "nameof", "typeof", "sizeof", "base", "this", "throw", "await", "get", "set", "init", "value", "else"];

    /// <summary>The outline of a file of another language, by its extension, empty for one it doesn't know.</summary>
    private static List<OutlineEntry> OutlineOfOther(string text, string extension)
    {
        var entries = new List<OutlineEntry>();
        string[] lines = text.Split('\n');
        bool fenced = false;

        // How deep the D type the lines are in is, its fields one in from it, -1 outside of one
        int typeDepth = -1;
        for (int line = 0; line < lines.Length && entries.Count < 3000; line++)
        {
            string row = lines[line].TrimEnd('\r');
            switch (extension)
            {
                case ".cs":
                    if (!CsStarts.Contains(FirstWord(row)))
                        break;
                    if (CsType().Match(row) is { Success: true } type)
                        entries.Add(new OutlineEntry(type.Groups[3].Value, line, type.Groups[3].Index, Depth(type.Groups[1].Value, 4), type.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1] switch
                        {
                            "interface" => "symbol-interface",
                            "enum" => "symbol-enum",
                            "struct" => "symbol-struct",
                            _ => "symbol-class"
                        }));
                    else if (CsMember().Match(row) is { Success: true } member && !NotNames.Contains(member.Groups[2].Value))
                        entries.Add(new OutlineEntry(member.Groups[2].Value, line, member.Groups[2].Index, Depth(member.Groups[1].Value, 4),
                            member.Groups[3].Value == "(" ? "symbol-method" : member.Groups[3].Value is "{" or "=>" ? "symbol-property" : "symbol-field"));
                    break;

                case ".d" or ".di":
                    if (DType().Match(row) is { Success: true } dType)
                    {
                        typeDepth = Depth(dType.Groups[1].Value, 4);
                        entries.Add(new OutlineEntry(dType.Groups[3].Value, line, dType.Groups[3].Index, typeDepth, dType.Groups[2].Value switch
                        {
                            "interface" => "symbol-interface",
                            "enum" => "symbol-enum",
                            "struct" or "union" => "symbol-struct",
                            "class" => "symbol-class",
                            _ => "symbol-namespace"
                        }));
                    }
                    else if (DFunction().Match(row) is { Success: true } dFunction && !NotNames.Contains(dFunction.Groups[2].Value) && !NotNames.Contains(dFunction.Groups[3].Value))
                    {
                        int depth = Depth(dFunction.Groups[1].Value, 4);
                        if (depth <= typeDepth)
                            typeDepth = -1;
                        entries.Add(new OutlineEntry(dFunction.Groups[3].Value, line, dFunction.Groups[3].Index, depth, "symbol-method"));
                    }
                    else if (typeDepth >= 0 && DField().Match(row) is { Success: true } dField && Depth(dField.Groups[1].Value, 4) == typeDepth + 1)
                    {
                        // A field of the type the line is in, every name of int width, height; a field of its own
                        foreach (Match named in DFieldName().Matches(dField.Groups[2].Value))
                        {
                            string name = named.Groups[1].Value;
                            if (!NotNames.Contains(name))
                                entries.Add(new OutlineEntry(name, line, dField.Groups[2].Index + named.Groups[1].Index, typeDepth + 1, "symbol-field"));
                        }
                    }
                    break;

                case ".slang" or ".hlsl":
                    if (ShaderFunction().Match(row) is { Success: true } shader)
                    {
                        bool isStruct = shader.Groups[2].Success;
                        System.Text.RegularExpressions.Group name = isStruct ? shader.Groups[3] : shader.Groups[5];
                        if (!NotNames.Contains(name.Value))
                            entries.Add(new OutlineEntry(name.Value, line, name.Index, Depth(isStruct ? shader.Groups[1].Value : shader.Groups[4].Value, 4), isStruct ? "symbol-struct" : "symbol-method"));
                    }
                    break;

                case ".py":
                    if (Python().Match(row) is { Success: true } python)
                        entries.Add(new OutlineEntry(python.Groups[3].Value, line, python.Groups[3].Index, Depth(python.Groups[1].Value, 4), python.Groups[2].Value == "class" ? "symbol-class" : "symbol-method"));
                    break;

                case ".js" or ".ts" or ".mjs" or ".jsx" or ".tsx":
                    if (Script().Match(row) is { Success: true } script)
                    {
                        bool declared = script.Groups[3].Success;
                        System.Text.RegularExpressions.Group name = declared ? script.Groups[3] : script.Groups[5];
                        string kind = declared ? script.Groups[2].Value : "function";
                        entries.Add(new OutlineEntry(name.Value, line, name.Index, Depth(declared ? script.Groups[1].Value : script.Groups[4].Value, 2),
                            kind switch { "class" => "symbol-class", "interface" or "type" => "symbol-interface", "enum" => "symbol-enum", _ => "symbol-method" }));
                    }
                    break;

                case ".md" or ".markdown":
                    if (row.TrimStart().StartsWith("```", StringComparison.Ordinal))
                        fenced = !fenced;
                    else if (!fenced && Heading().Match(row) is { Success: true } heading)
                        entries.Add(new OutlineEntry(heading.Groups[2].Value, line, heading.Groups[2].Index, heading.Groups[1].Length - 1, "symbol-string"));
                    break;

                case ".json":
                    if (JsonKey().Match(row) is { Success: true } key && Depth(key.Groups[1].Value, 2) <= 3)
                        entries.Add(new OutlineEntry(key.Groups[2].Value, line, key.Groups[2].Index, Math.Max(0, Depth(key.Groups[1].Value, 2) - 1), "symbol-key"));
                    break;

                case ".yaml" or ".yml":
                    if (!row.TrimStart().StartsWith('#') && YamlKey().Match(row) is { Success: true } yaml && Depth(yaml.Groups[1].Value, 2) <= 3)
                        entries.Add(new OutlineEntry(yaml.Groups[2].Value.Trim('"', '\''), line, yaml.Groups[2].Index, Depth(yaml.Groups[1].Value, 2), "symbol-key"));
                    break;
            }
        }

        return entries;
    }

    // The first word of a line, what's before the first white space after the indent
    private static ReadOnlySpan<char> FirstWord(string row)
    {
        ReadOnlySpan<char> line = row.AsSpan().TrimStart();
        int end = 0;
        while (end < line.Length && !char.IsWhiteSpace(line[end]))
            end++;

        return line[..end];
    }

    // how deep a line is by how far in it is, a tab as wide as so many spaces
    private static int Depth(string indent, int width)
    {
        int columns = 0;
        foreach (char c in indent)
            columns += c == '\t' ? width : 1;
        return columns / Math.Max(1, width);
    }
}
