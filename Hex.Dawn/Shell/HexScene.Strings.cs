using Horizon.Content;

namespace Hex.Dawn;

// What Hex says in its windows (the keys, the language, about), out of its own file of texts, Assets/hex/localisation.
// Read with the engine's reader of languages but kept apart from the game's language, which Hex loads for the layouts
// it shows (Localisation is one language at a time, the game's). Read again every time one of the windows opens, so
// whoever edits the file sees it the next time without building anything.
internal sealed partial class HexScene
{
    private const string STRINGS_FILE = "Assets/hex/localisation/en-gb.hor";

    private IReadOnlyDictionary<string, string> strings = new Dictionary<string, string>();

    /// <summary>
    /// Reads Hex's texts again, and has the editor's own UI look its @keys up in them before the game's language (which is
    /// loaded for the layouts Hex shows). One that doesn't read says why and leaves the ones there were.
    /// </summary>
    private void ReadStrings()
    {
        try
        {
            strings = Localisation.ReadFile(STRINGS_FILE);
            compositor.Strings = strings;
        }
        catch (Exception e)
        {
            // As the editor starts there is nowhere to say it yet but the log
            if (messageItem is null)
                Horizon.Logging.Log.Error($"[Hex] Hex's texts didn't read, {e.Message}");
            else
                Say($"Hex's texts didn't read, {e.Message}", error: true);
        }
    }

    /// <summary>
    /// What Hex says by a key of its texts, the names in braces filled in. A key there is no text for comes out as
    /// itself, so it is plain to see what is missing.
    /// </summary>
    private string Said(string key, params (string Name, string Value)[] filled)
    {
        if (!strings.TryGetValue(key, out string? text))
            return $"@{key}";

        foreach (var (name, value) in filled)
            text = text.Replace("{" + name + "}", value);

        return text;
    }
}
