namespace Hex.Shell;

/// <summary>
/// Which theme the editor is drawn in while it follows the system's light or dark appearance (a Mac's). A pure
/// decision: what the person picked stays what they picked, this only says what is shown. Only the flat skin has the
/// pairs, any other skin is shown as it is.
/// </summary>
internal static class HexAppearance
{
    /// <summary>The themes of the flat skin that are on paper. Every other theme of it is dark.</summary>
    private static readonly HashSet<string> Light = new(StringComparer.Ordinal) { "light", "solstice_day" };

    /// <summary>
    /// The theme to draw the editor in. The person's own while they don't follow the system, in a skin without the pairs, or
    /// when it is the system's mode already. Otherwise its other half: Solstice and Graphite go to Solstice Day (and Solstice
    /// Day back to Solstice), the rest of the flat skin's dark themes to Light (and Light back to Dark).
    /// </summary>
    public static string ThemeFor(bool followSystem, bool isDark, string skin, string userTheme)
    {
        if (!followSystem || !string.Equals(skin, "flat", StringComparison.Ordinal) || userTheme.Length == 0)
            return userTheme;

        bool userIsLight = Light.Contains(userTheme);
        if (userIsLight != isDark)
            return userTheme;

        if (isDark)
            return userTheme == "solstice_day" ? "solstice" : "dark";

        return userTheme is "solstice" or "graphite" ? "solstice_day" : "light";
    }
}
