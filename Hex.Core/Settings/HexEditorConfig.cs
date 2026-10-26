using System.Text;
using System.Text.RegularExpressions;

namespace Hex.Core;

/// <summary>
/// What a project's .editorconfig files say about one file, null for whatever none of them say. Only the keys about
/// how it's indented and how it's saved are read, the way most editors take them in.
/// </summary>
internal sealed record HexEditorConfigValues(bool? UseTabs, int? IndentSize, int? TabWidth, bool? TrimTrailingWhitespace, bool? InsertFinalNewline)
{
    /// <summary>Whether any of them said anything at all.</summary>
    public bool Any => UseTabs is not null || IndentSize is not null || TabWidth is not null || TrimTrailingWhitespace is not null || InsertFinalNewline is not null;
}

/// <summary>
/// A small reader of .editorconfig (editorconfig.org). The files are looked for from the file's folder up until one says
/// root = true, a nearer one winning over a further one and a later section of a file over an earlier one. Sections are
/// globs, [*], [*.cs], [*.{yml,yaml}], [Makefile], [src/**.cs], a glob with no slash in it matching the file's name
/// wherever it is under the file's folder.
/// </summary>
internal static class HexEditorConfig
{
    public const string FILE = ".editorconfig";

    // The most folders it goes up, past any project there is
    private const int MOST_FOLDERS = 32;

    /// <summary>What applies to a file, everything null for a file no .editorconfig says anything about.</summary>
    public static HexEditorConfigValues For(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return new HexEditorConfigValues(null, null, null, null, null);
        }

        // The files from the nearest up, stopping at the one that says it's the root
        var found = new List<(string Folder, List<(string Glob, Dictionary<string, string> Pairs)> Sections)>();
        int up = 0;
        for (string? folder = Path.GetDirectoryName(full); !string.IsNullOrEmpty(folder) && up < MOST_FOLDERS; folder = Path.GetDirectoryName(folder), up++)
        {
            string file = Path.Combine(folder, FILE);
            if (!File.Exists(file))
                continue;

            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception)
            {
                continue;
            }

            var (root, sections) = Parse(text);
            found.Add((folder, sections));
            if (root)
                break;
        }

        // The furthest first so the nearer ones go over it
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = found.Count - 1; i >= 0; i--)
        {
            string relative = Path.GetRelativePath(found[i].Folder, full).Replace('\\', '/');
            foreach (var (glob, its) in found[i].Sections)
            {
                if (!Matches(glob, relative))
                    continue;

                foreach (var (key, value) in its)
                    pairs[key] = value;
            }
        }

        return Values(pairs);
    }

    /// <summary>The values out of the pairs that apply, an indent_size of tab going by tab_width and each standing in for the other.</summary>
    internal static HexEditorConfigValues Values(Dictionary<string, string> pairs)
    {
        bool? useTabs = pairs.GetValueOrDefault("indent_style") switch
        {
            "tab" => true,
            "space" => false,
            _ => null
        };

        int? tabWidth = Whole(pairs.GetValueOrDefault("tab_width"));
        string? sized = pairs.GetValueOrDefault("indent_size");
        int? indentSize = sized == "tab" ? tabWidth : Whole(sized);
        if (sized == "tab")
            useTabs ??= true;
        tabWidth ??= indentSize;
        indentSize ??= tabWidth;

        return new HexEditorConfigValues(useTabs, indentSize, tabWidth, Flag(pairs.GetValueOrDefault("trim_trailing_whitespace")),
            Flag(pairs.GetValueOrDefault("insert_final_newline")));
    }

    private static int? Whole(string? text) => int.TryParse(text, out int value) && value is >= 1 and <= 64 ? value : null;

    private static bool? Flag(string? text) => text switch
    {
        "true" => true,
        "false" => false,
        _ => null
    };

    /// <summary>Whether the file says root = true before its first section, and its sections in order, keys and values lower case.</summary>
    internal static (bool Root, List<(string Glob, Dictionary<string, string> Pairs)> Sections) Parse(string text)
    {
        bool root = false;
        var sections = new List<(string, Dictionary<string, string>)>();
        Dictionary<string, string>? current = null;

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
                continue;

            if (line[0] == '[')
            {
                int end = line.LastIndexOf(']');
                if (end <= 1)
                {
                    current = null;
                    continue;
                }

                current = new Dictionary<string, string>(StringComparer.Ordinal);
                sections.Add((line[1..end].Trim(), current));
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals <= 0)
                continue;

            string key = line[..equals].Trim().ToLowerInvariant();
            string value = line[(equals + 1)..].Trim();

            // A comment can follow a value after a space, value ; like this
            int comment = value.IndexOfAny([';', '#']);
            if (comment > 0 && char.IsWhiteSpace(value[comment - 1]))
                value = value[..comment].TrimEnd();
            value = value.ToLowerInvariant();

            if (current is null)
            {
                if (key == "root")
                    root = value == "true";
            }
            else
            {
                current[key] = value;
            }
        }

        return (root, sections);
    }

    /// <summary>Whether a section's glob matches a file, by its path from the .editorconfig's folder with forward slashes.</summary>
    internal static bool Matches(string glob, string relative)
    {
        if (glob.Length == 0)
            return false;

        // With no slash it's the name wherever it is, with one it's from the file's folder
        string pattern = glob.Contains('/') ? glob.TrimStart('/') : "**/" + glob;
        try
        {
            // Windows doesn't mind the case of a name, so neither does a glob there
            var options = RegexOptions.CultureInvariant | (OperatingSystem.IsWindows() ? RegexOptions.IgnoreCase : RegexOptions.None);
            return Regex.IsMatch(relative, "^" + ToRegex(pattern) + "$", options);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // A glob as a regular expression, * anything but a slash, ** anything, ? one character, {a,b} either, [abc] one of them
    private static string ToRegex(string glob)
    {
        var regex = new StringBuilder();
        int braces = 0;
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            switch (c)
            {
                case '*' when i + 1 < glob.Length && glob[i + 1] == '*':
                    i++;
                    // **/ is any number of folders, none at all as well
                    if (i + 1 < glob.Length && glob[i + 1] == '/')
                    {
                        i++;
                        regex.Append("(?:.*/)?");
                    }
                    else
                    {
                        regex.Append(".*");
                    }
                    break;
                case '*':
                    regex.Append("[^/]*");
                    break;
                case '?':
                    regex.Append("[^/]");
                    break;
                case '{':
                    braces++;
                    regex.Append("(?:");
                    break;
                case '}' when braces > 0:
                    braces--;
                    regex.Append(')');
                    break;
                case ',' when braces > 0:
                    regex.Append('|');
                    break;
                case '[':
                    int close = glob.IndexOf(']', i + 1);
                    if (close < 0)
                    {
                        regex.Append(@"\[");
                        break;
                    }

                    string set = glob[(i + 1)..close];
                    regex.Append('[').Append(set.StartsWith('!') ? "^" + Regex.Escape(set[1..]) : Regex.Escape(set)).Append(']');
                    i = close;
                    break;
                case '\\' when i + 1 < glob.Length:
                    i++;
                    regex.Append(Regex.Escape(glob[i].ToString()));
                    break;
                default:
                    regex.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        // A brace left open is a brace and nothing more
        return braces == 0 ? regex.ToString() : Regex.Escape(glob);
    }
}
