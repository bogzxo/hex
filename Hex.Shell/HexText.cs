using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

using Horizon.Content;
using Horizon.Core.Platform;

namespace Hex.Shell;

/// <summary>
/// What Hex says, Dawn and Dusk both, out of its own file of texts (Assets/hex/localisation/en-gb.hor, next to the
/// program). Read with the engine's reader of languages but kept apart from the engine's Localisation, which is the
/// language of the game Hex has open. Read the first time a text is asked for and again by <see cref="Read"/>, which the
/// windows of the Help menu do as they open, so whoever edits the file sees it without building anything.
/// <code>
/// label.Text = HexText.Get("toolbar.save");
/// Say(HexText.Get("files.saved", ("file", name)));
/// </code>
/// </summary>
public static class HexText
{
    /// <summary>The file of texts, from the program's folder.</summary>
    public const string FILE = "Assets/hex/localisation/en-gb.hor";

    private static IReadOnlyDictionary<string, string> strings = ReadOnlyDictionary<string, string>.Empty;
    private static bool read;

    /// <summary>Every text by its key, read the first time it's asked for.</summary>
    public static IReadOnlyDictionary<string, string> Strings
    {
        get
        {
            if (!read)
                Read();
            return strings;
        }
    }

    /// <summary>Reads the file again. What went wrong when it didn't read (the texts there were stay), null when it did.</summary>
    public static string? Read()
    {
        read = true;
        try
        {
            string path = Path.Combine(Hex.Core.HexHost.ProgramFolder, FILE);
            if (File.Exists(path) && TryReadFast(path, out Dictionary<string, string>? fast))
            {
                strings = fast!;
                return null;
            }

            strings = Localisation.ReadFile(path);
            return null;
        }
        catch (Exception e)
        {
            return e.Message;
        }
    }

    // Streams static `let strings = { ... };` files directly into a trimmed dictionary so startup skips instantiating
    // HIDLRuntime, its AST, and its intermediate ObjectValue tree. Falls back to Localisation.ReadFile on any non-literal
    // construct so full HIDL syntax stays supported.
    private static bool TryReadFast(string path, out Dictionary<string, string>? result)
    {
        result = null;
        var into = new Dictionary<string, string>(StringComparer.Ordinal);
        var intern = new HashSet<string>(StringComparer.Ordinal);
        var prefixes = new List<string>(8);
        var scratch = new StringBuilder(128);
        var listBuf = new StringBuilder(256);
        string currentPrefix = string.Empty;
        string? listKey = null;
        int listCount = 0;
        bool started = false;
        bool finished = false;

        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096);
        while (reader.ReadLine() is { } raw)
        {
            ReadOnlySpan<char> line = raw.AsSpan().Trim();
            if (line.IsEmpty || line.StartsWith("//", StringComparison.Ordinal))
                continue;

            if (finished)
                return false;

            if (!started)
            {
                if (!line.SequenceEqual("let strings = {"))
                    return false;
                started = true;
                continue;
            }

            if (listKey is not null)
            {
                if (line.SequenceEqual("]") || line.SequenceEqual("],"))
                {
                    into[listKey] = Intern(intern, listBuf.ToString());
                    listKey = null;
                    listBuf.Clear();
                    listCount = 0;
                    continue;
                }

                if (!TryParseStringLine(line, scratch, out string? item))
                    return false;

                if (listCount > 0)
                    listBuf.Append('\n');
                listBuf.Append(item);
                listCount++;
                continue;
            }

            if (line.SequenceEqual("}") || line.SequenceEqual("},") || line.SequenceEqual("};"))
            {
                if (prefixes.Count == 0)
                {
                    finished = true;
                    continue;
                }

                prefixes.RemoveAt(prefixes.Count - 1);
                currentPrefix = prefixes.Count > 0 ? prefixes[^1] : string.Empty;
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0)
                return false;

            ReadOnlySpan<char> name = line[..colon].TrimEnd();
            if (!IsIdentifier(name))
                return false;

            ReadOnlySpan<char> rest = line[(colon + 1)..].TrimStart();
            string fullKey = currentPrefix.Length > 0
                ? string.Concat(currentPrefix, ".", name)
                : name.ToString();

            if (rest.SequenceEqual("{"))
            {
                prefixes.Add(fullKey);
                currentPrefix = fullKey;
                continue;
            }

            if (rest.SequenceEqual("["))
            {
                listKey = fullKey;
                listBuf.Clear();
                listCount = 0;
                continue;
            }

            if (!TryParseStringLine(rest, scratch, out string? text))
                return false;

            into[fullKey] = Intern(intern, text!);
        }

        if (!started || !finished || prefixes.Count != 0 || listKey is not null)
            return false;

        into.TrimExcess();
        result = into;
        return true;
    }

    private static string Intern(HashSet<string> pool, string value)
    {
        if (value.Length == 0)
            return string.Empty;
        if (pool.TryGetValue(value, out string? existing))
            return existing;
        pool.Add(value);
        return value;
    }

    private static bool IsIdentifier(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty)
            return false;
        foreach (char c in name)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '_'))
                return false;
        }
        return true;
    }

    private static bool TryParseStringLine(ReadOnlySpan<char> span, StringBuilder scratch, out string? text)
    {
        text = null;
        if (span.Length < 2 || span[0] != '"')
            return false;

        bool hasEscapes = false;
        int i = 1;
        while (i < span.Length && span[i] != '"')
        {
            if (span[i] == '\\')
            {
                if (i + 1 >= span.Length)
                    return false;
                hasEscapes = true;
                i += 2;
                continue;
            }
            i++;
        }

        if (i >= span.Length || span[i] != '"')
            return false;

        ReadOnlySpan<char> trailing = span[(i + 1)..].TrimStart();
        if (!trailing.IsEmpty)
        {
            if (trailing[0] == ',')
                trailing = trailing[1..].TrimStart();
            if (!trailing.IsEmpty && !trailing.StartsWith("//", StringComparison.Ordinal))
                return false;
        }

        ReadOnlySpan<char> inner = span[1..i];
        text = hasEscapes ? Unescape(inner, scratch) : (inner.IsEmpty ? string.Empty : inner.ToString());
        return true;
    }

    // Matches HIDL Lexer.Unescape for \" \\ \n \t \r \0 and \uXXXX.
    private static string Unescape(ReadOnlySpan<char> span, StringBuilder scratch)
    {
        scratch.Clear();
        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            if (c == '\\' && i + 1 < span.Length)
            {
                if (span[i + 1] == 'u' && i + 5 < span.Length
                    && ushort.TryParse(span.Slice(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ushort code))
                {
                    scratch.Append((char)code);
                    i += 5;
                    continue;
                }

                char after = span[++i];
                scratch.Append(after switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    '0' => '\0',
                    _ => after
                });
                continue;
            }

            scratch.Append(c);
        }

        return scratch.ToString();
    }

    /// <summary>
    /// What Hex says by a key, the names in braces filled in. A key there is no text for comes out as itself with an @,
    /// so it's plain to see what is missing.
    /// </summary>
    public static string Get(string key, params (string Name, string Value)[] filled)
    {
        if (!Strings.TryGetValue(key, out string? text))
            return "@" + key;

        foreach (var (name, value) in filled)
            text = text.Replace("{" + name + "}", value);

        return text;
    }

    /// <summary>
    /// So many of something, said right whether it's one or more, "1 file", "3 files", by the counts' keys (file, line,
    /// place, problem). The status bar said a word was "in 1 files".
    /// </summary>
    public static string Count(string what, int count) =>
        Get($"counts.{what}_{(count == 1 ? "one" : "many")}", ("count", count.ToString(CultureInfo.InvariantCulture)));

    // The little words a title leaves small, unless one starts it
    private static readonly HashSet<string> Small = new(StringComparer.Ordinal) { "a", "an", "and", "as", "at", "by", "for", "from", "in", "into", "of", "on", "or", "the", "to", "with" };
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> SmallLookup = Small.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>
    /// A command's name written the way a menu's are, every word with a capital but the little ones ("Select last output"
    /// as "Select Last Output"). The terminal's commands are written like a sentence, the way Dusk's menus are, and in
    /// Dawn's palette they sat in sentence case between commands in title case.
    /// </summary>
    public static string TitleCase(string text)
    {
        var titled = new StringBuilder(text.Length);
        bool first = true;
        ReadOnlySpan<char> remaining = text.AsSpan();
        while (true)
        {
            int space = remaining.IndexOf(' ');
            ReadOnlySpan<char> word = space >= 0 ? remaining[..space] : remaining;

            if (titled.Length > 0)
                titled.Append(' ');

            bool small = !first && SmallLookup.Contains(word);
            if (word.Length > 0 && !small && char.IsLower(word[0]))
            {
                titled.Append(char.ToUpperInvariant(word[0]));
                titled.Append(word[1..]);
            }
            else
            {
                titled.Append(word);
            }

            // A word after a heading (one that ends in a colon, or a dot between) starts the title again
            first = (word.Length > 0 && word[^1] == ':') || word.SequenceEqual("\u00b7");

            if (space < 0)
                break;
            remaining = remaining[(space + 1)..];
        }

        return titled.ToString();
    }

    /// <summary>
    /// A path cut down to so many letters with an ellipsis in the middle, its start and its file kept, the way VS Code
    /// shortens one ("src/.../Program.cs"). The whole of it when it's short enough. A long one wrapped in a tooltip broke
    /// inside a word ("RE/ADME.md").
    /// </summary>
    public static string MiddleOf(string path, int most = 60)
    {
        if (path.Length <= most || most < 8)
            return path;

        // More of the end than of the start, the end is the file
        int end = most * 3 / 5, start = most - end - 1;
        return string.Concat(path.AsSpan(0, start), "…", path.AsSpan(path.Length - end));
    }
}
