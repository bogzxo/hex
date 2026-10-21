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
}
