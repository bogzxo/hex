using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hex.Core;

/// <summary>
/// The code a chat message points at (docs/hex-link-plan.md, Link Chat for code): a shared selection ("code", with the
/// snippet and an optional note) or a "look here" ping ("look", for one device, <see cref="To"/>). The path is a wire path,
/// workspace-folder-name/relative/path, the lines are 1-based. It rides in the message's optional "ref" field; the message's
/// plain text always says the same in words (<see cref="Fallback"/>), so a peer that doesn't know the field shows that.
/// </summary>
internal sealed record HexLinkTalkRef(string Kind, string Path, int Line, int ToLine, string? Snippet = null, string? Note = null, string? To = null)
{
    public const string CODE = "code";
    public const string LOOK = "look";

    /// <summary>A request to one device to let go of a file it holds or is editing: <see cref="Path"/> the file, <see cref="To"/> the device, <see cref="Note"/> the words.</summary>
    public const string RELEASE = "release";

    /// <summary>The most lines and bytes (UTF-8) of a snippet.</summary>
    public const int MOST_LINES = 60, MOST_SNIPPET_BYTES = 4096;

    public const int MOST_PATH = 512, MOST_NOTE = 400, MOST_LINE = 100_000_000;

    /// <summary>A snippet as it goes on the wire: line ends as \n, no control characters but tabs, at most 60 lines and 4 KB, cut on a character.</summary>
    public static string CapSnippet(string text)
    {
        var kept = new StringBuilder();
        int lines = 1, bytes = 0;
        foreach (Rune rune in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').EnumerateRunes())
        {
            Rune put = rune;
            if (Rune.IsControl(rune) && rune.Value is not ('\n' or '\t'))
                put = new Rune(' ');
            if (put.Value == '\n' && ++lines > MOST_LINES)
                break;
            if (bytes + put.Utf8SequenceLength > MOST_SNIPPET_BYTES)
                break;
            bytes += put.Utf8SequenceLength;
            kept.Append(put.ToString());
        }

        return kept.ToString().TrimEnd('\n', ' ', '\t');
    }

    /// <summary>The lines as people write them, <c>12</c> or <c>12-20</c>.</summary>
    public string Lines => ToLine > Line ? $"{Line}-{ToLine}" : Line.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>What a peer without the field shows: the note, path:lines and the snippet, cut to what a message may hold.</summary>
    public string Fallback()
    {
        if (Kind == RELEASE)
            return string.IsNullOrWhiteSpace(Note) ? Path : Note!.Trim();
        var said = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(Note))
            said.Append(Note!.Trim()).Append('\n');
        said.Append(Path).Append(':').Append(Lines);
        if (Kind == LOOK)
            said.Append(" (look here)");
        else if (!string.IsNullOrWhiteSpace(Snippet))
            said.Append('\n').Append(Snippet);
        string text = said.ToString();
        if (text.Length > HexLinkTalk.MOST_TEXT)
            text = text[..(HexLinkTalk.MOST_TEXT - 1)].TrimEnd() + "…";
        return text;
    }

    public JsonObject ToJson()
    {
        var made = new JsonObject { ["kind"] = Kind, ["path"] = Path, ["line"] = (long)Line, ["toLine"] = (long)ToLine };
        if (Snippet is not null)
            made["snippet"] = Snippet;
        if (Note is not null)
            made["note"] = Note;
        if (To is not null)
            made["to"] = To;
        return made;
    }

    /// <summary>A reference read from the wire, null when it isn't a good one (the message then shows its text alone).</summary>
    public static HexLinkTalkRef? Of(JsonObject? said)
    {
        if (said is null || HexLinkData.String(said, "kind", 8) is not { } kind || kind is not (CODE or LOOK or RELEASE)
            || HexLinkData.String(said, "path", MOST_PATH + 1) is not { Length: > 0 and <= MOST_PATH } path || path.Any(char.IsControl)
            || HexLinkData.Int(said, "line") is not { } line || line is < 1 or > MOST_LINE)
            return null;
        int toLine = HexLinkData.Int(said, "toLine") is { } to && to >= line && to <= MOST_LINE ? to : line;
        string? snippet = HexLinkData.String(said, "snippet", MOST_SNIPPET_BYTES + 1) is { } text ? CapSnippet(text) : null;
        string? note = HexLinkData.String(said, "note", MOST_NOTE + 1) is { Length: <= MOST_NOTE } n && HexLinkTalk.Valid(n) ? n : null;
        string? device = HexLinkData.String(said, "to", 65) is { Length: 64 } id && id.All(char.IsAsciiHexDigit) ? id.ToUpperInvariant() : null;
        return new HexLinkTalkRef(kind, path, line, toLine, kind == CODE ? snippet : null, note, kind is LOOK or RELEASE ? device : null);
    }
}

/// <summary>Finding file references and @mentions in what people write in a chat message.</summary>
internal static class HexLinkTalkText
{
    /// <summary>A reference in a text: where it is, and the path as written (with / or \), the lines 1-based.</summary>
    public readonly record struct Reference(int Start, int Length, string Path, int Line, int ToLine);

    // path/to/file.ext:123 or file.ext:12-20, not the tail of a longer word or a URL's host:port
    private static readonly Regex references = new(
        @"(?<![\w/\\.\-:])(?<path>(?:[\w.\-]+[/\\])*[\w\-]+(?:\.[\w\-]+)*\.[A-Za-z][A-Za-z0-9_]*):(?<line>\d{1,9})(?:-(?<to>\d{1,9}))?(?![\w])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    /// <summary>The file references in a text, in order.</summary>
    public static List<Reference> References(string text)
    {
        var found = new List<Reference>();
        try
        {
            foreach (Match match in references.Matches(text))
            {
                int line = int.Parse(match.Groups["line"].Value, System.Globalization.CultureInfo.InvariantCulture);
                int to = match.Groups["to"].Success ? int.Parse(match.Groups["to"].Value, System.Globalization.CultureInfo.InvariantCulture) : line;
                if (line < 1 || line > HexLinkTalkRef.MOST_LINE || to > HexLinkTalkRef.MOST_LINE)
                    continue;
                found.Add(new Reference(match.Index, match.Length, match.Groups["path"].Value, line, Math.Max(line, to)));
            }
        }
        catch (RegexMatchTimeoutException)
        {
        }

        return found;
    }

    /// <summary>Which of the names a text mentions as @Name (any case, a name with spaces too), the longest name winning where two start the same.</summary>
    public static List<string> Mentions(string text, IEnumerable<string> names)
    {
        var mentioned = new List<string>();
        foreach (string name in names.Where(name => name.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(name => name.Length))
        {
            if (MentionAt(text, name) && !mentioned.Contains(name, StringComparer.OrdinalIgnoreCase))
                mentioned.Add(name);
        }

        return mentioned;
    }

    /// <summary>The places in a text where a name is mentioned, start and length including the @.</summary>
    public static List<(int Start, int Length)> MentionSpans(string text, IEnumerable<string> names)
    {
        var spans = new List<(int Start, int Length)>();
        foreach (string name in names.Where(name => name.Length > 0).OrderByDescending(name => name.Length))
        {
            for (int at = text.IndexOf('@', StringComparison.Ordinal); at >= 0; at = text.IndexOf('@', at + 1))
            {
                int length = name.Length + 1;
                if (IsMention(text, at, name) && !spans.Any(span => at < span.Start + span.Length && span.Start < at + length))
                    spans.Add((at, length));
            }
        }

        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        return spans;
    }

    private static bool MentionAt(string text, string name)
    {
        for (int at = text.IndexOf('@', StringComparison.Ordinal); at >= 0; at = text.IndexOf('@', at + 1))
        {
            if (IsMention(text, at, name))
                return true;
        }

        return false;
    }

    private static bool IsMention(string text, int at, string name) =>
        (at == 0 || !char.IsLetterOrDigit(text[at - 1])) && at + 1 + name.Length <= text.Length
        && string.Compare(text, at + 1, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0
        && (at + 1 + name.Length == text.Length || !char.IsLetterOrDigit(text[at + 1 + name.Length]));

    /// <summary>
    /// The @word being typed when the caret is at <paramref name="caret"/>: where its @ is, and what's typed after it (spaces allowed
    /// up to a device name's own, so "@My Lap" completes "My Laptop"). Null when the caret isn't in one.
    /// </summary>
    public static (int Start, string Typed)? Completing(string text, int caret)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        int at = text.LastIndexOf('@', Math.Max(0, caret - 1), Math.Min(caret, 40));
        if (caret == 0 || at < 0 || (at > 0 && char.IsLetterOrDigit(text[at - 1])))
            return null;
        string typed = text[(at + 1)..caret];
        return typed.Contains('\n') ? null : (at, typed);
    }
}

/// <summary>The last messages of a Link kept in a file, so the chat is there again after a restart.</summary>
internal static class HexLinkTalkHistory
{
    /// <summary>The most messages kept.</summary>
    public const int MOST = 500;

    /// <summary>The last <see cref="MOST"/> messages written to the file, replacing it whole.</summary>
    public static void Save(string path, IEnumerable<HexLinkTalk> messages)
    {
        var all = new JsonArray();
        foreach (HexLinkTalk message in messages.TakeLast(MOST))
            all.Add((JsonNode?)message.ToJson(relayed: true));
        string folder = System.IO.Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, all.ToJsonString(), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The messages in the file, oldest first; none when there's no file or it isn't one this can read.</summary>
    public static List<HexLinkTalk> Load(string path)
    {
        var read = new List<HexLinkTalk>();
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonArray all)
                return read;
            var seen = new HashSet<Guid>();
            foreach (JsonObject said in all.OfType<JsonObject>())
            {
                if (HexLinkTalk.Of(said, new HexLinkDevice(new string('0', 64), "?")) is { } message && seen.Add(message.Id))
                    read.Add(message);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return read.Count > MOST ? read[^MOST..] : read;
    }
}
