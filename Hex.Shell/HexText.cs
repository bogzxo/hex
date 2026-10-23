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

    private static IReadOnlyDictionary<string, string> strings = new Dictionary<string, string>();
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
            strings = Localisation.ReadFile(Path.Combine(PlatformPaths.Program, FILE));
            return null;
        }
        catch (Exception e)
        {
            return e.Message;
        }
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
        Get($"counts.{what}_{(count == 1 ? "one" : "many")}", ("count", count.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    // The little words a title leaves small, unless one starts it
    private static readonly HashSet<string> Small = new(StringComparer.Ordinal) { "a", "an", "and", "as", "at", "by", "for", "from", "in", "into", "of", "on", "or", "the", "to", "with" };

    /// <summary>
    /// A command's name written the way a menu's are, every word with a capital but the little ones ("Select last output"
    /// as "Select Last Output"). The terminal's commands are written like a sentence, the way Dusk's menus are, and in
    /// Dawn's palette they sat in sentence case between commands in title case.
    /// </summary>
    public static string TitleCase(string text)
    {
        var titled = new System.Text.StringBuilder(text.Length);
        bool first = true;
        foreach (string word in text.Split(' '))
        {
            if (titled.Length > 0)
                titled.Append(' ');

            bool small = !first && Small.Contains(word);
            titled.Append(word.Length > 0 && !small && char.IsLower(word[0]) ? char.ToUpperInvariant(word[0]) + word[1..] : word);

            // A word after a heading (one that ends in a colon, or a dot between) starts the title again
            first = word.EndsWith(':') || word == "\u00b7";
        }

        return titled.ToString();
    }
}
