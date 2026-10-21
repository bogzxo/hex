using Hex.Shell;

namespace Hex.Dawn;

// What Hex says in its windows, out of its own file of texts (HexText, Assets/hex/localisation), which Dusk reads too.
// Kept apart from the game's language, which Hex loads for the layouts it shows (Localisation is one language at a
// time, the game's). Read again every time one of the Help windows opens, so whoever edits the file sees it the next
// time without building anything.
internal sealed partial class DawnScene
{
    /// <summary>
    /// Reads Hex's texts again, and has the editor's own UI look its @keys up in them before the game's language (which is
    /// loaded for the layouts Hex shows). One that doesn't read says why and leaves the ones there were.
    /// </summary>
    private void ReadStrings()
    {
        if (HexText.Read() is { } wrong)
        {
            // As the editor starts there is nowhere to say it yet but the log
            if (messageItem is null)
                Horizon.Logging.Log.Error($"[Hex] Hex's texts didn't read, {wrong}");
            else
                Say(HexText.Get("errors.texts_unread", ("why", wrong)), error: true);
        }

        compositor.Strings = HexText.Strings;
    }

    /// <summary>What Hex says by a key of its texts, the names in braces filled in, see <see cref="HexText.Get"/>.</summary>
    private static string Said(string key, params (string Name, string Value)[] filled) => HexText.Get(key, filled);
}
